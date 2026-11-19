using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ManimbleWin.Models;
using ManimbleWin.ViewModels;

namespace ManimbleWin.Views
{
    /// <summary>
    /// 预览舞台。相邻两次构建之间做“对象级”动画：
    /// 新出现的对象淡入 + 轻微上浮 + 缩放回弹，位置/尺寸变化的同名对象做平滑插值，消失的对象淡出。
    /// 这样播放的是动画，而不是一张张静态切片。
    /// </summary>
    public partial class AnimatedStage : UserControl
    {
        private sealed class Visual
        {
            public CanvasObject Model;
            public FrameworkElement Root;
            public TranslateTransform Drift;
            public ScaleTransform Scale;
            public double X, Y, W, H;
            public string Content;   // 文本/公式内容的快照，用于判断是否需要重建
        }

        private readonly Dictionary<string, Visual> _items = new Dictionary<string, Visual>();
        private int _token;

        public AnimatedStage()
        {
            InitializeComponent();
        }

        /// <summary>显示一帧。animate=false 时直接落位（用于拖动进度条或窗口刚打开）。</summary>
        public void Show(PreviewFrame frame, bool animate, bool newSlide)
        {
            if (frame == null) return;
            _token++;
            int token = _token;

            AnimateBackground(frame.Background);

            if (frame.HasRealImage)
            {
                ShowRealImage(frame.ImagePath, animate);
                FadeOutAll(animate);
                EmptyHint.Visibility = Visibility.Collapsed;
                return;
            }

            // 回到“对象绘制”模式
            if (FrameImage.Opacity > 0.01)
            {
                var fade = new DoubleAnimation(FrameImage.Opacity, 0, TimeSpan.FromMilliseconds(animate ? 220 : 0));
                FrameImage.BeginAnimation(OpacityProperty, fade);
            }

            var objects = frame.Objects?.ToList() ?? new List<CanvasObject>();
            EmptyHint.Visibility = objects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (objects.Count == 0)
            {
                FadeOutAll(animate);
                return;
            }

            var keys = BuildKeys(objects);
            var wanted = new HashSet<string>(keys);

            // 1) 消失的对象：淡出后移除
            foreach (var key in _items.Keys.ToList())
            {
                if (wanted.Contains(key)) continue;
                var vis = _items[key];
                _items.Remove(key);
                RemoveWithFade(vis, animate);
            }

            // 2) 保留下来的对象：内容变了就重建，位置/尺寸变了就补间
            int appearIndex = 0;
            for (int i = 0; i < objects.Count; i++)
            {
                var o = objects[i];
                string key = keys[i];
                if (_items.TryGetValue(key, out var vis))
                {
                    if (vis.Content != Snapshot(o))
                    {
                        _items.Remove(key);
                        RemoveWithFade(vis, animate);
                        AddVisual(o, key, animate, token, appearIndex++);
                    }
                    else
                    {
                        MoveVisual(vis, o, animate);
                    }
                }
                else
                {
                    AddVisual(o, key, animate, token, appearIndex++);
                }
            }
        }

        /// <summary>清空舞台（切换演示文稿时用）。</summary>
        public void Clear()
        {
            _token++;
            foreach (var v in _items.Values)
                Stage.Children.Remove(v.Root);
            _items.Clear();
            FrameImage.Source = null;
            FrameImage.Opacity = 0;
        }

        #region 绘制

        private void AddVisual(CanvasObject o, string key, bool animate, int token, int order)
        {
            FrameworkElement visual = BuildContent(o);
            if (visual == null) return;

            var drift = new TranslateTransform(0, 0);
            var scale = new ScaleTransform(1, 1, 0.5, 0.5);
            var group = new TransformGroup();
            group.Children.Add(scale);
            group.Children.Add(drift);
            visual.RenderTransform = group;

            var host = new Grid { Width = Math.Max(1, o.Width), Height = Math.Max(1, o.Height) };
            host.Children.Add(visual);

            Canvas.SetLeft(host, o.X);
            Canvas.SetTop(host, o.Y);
            Panel.SetZIndex(host, o.Z);
            Stage.Children.Add(host);

            var item = new Visual
            {
                Model = o, Root = host, Drift = drift, Scale = scale,
                X = o.X, Y = o.Y, W = o.Width, H = o.Height, Content = Snapshot(o)
            };
            _items[key] = item;

            if (!animate)
            {
                host.Opacity = 1;
                return;
            }

            // 入场：透明度 + 上浮 + 缩放回弹，按出现顺序轻微错峰（manim 的 FadeIn 观感）
            var delay = TimeSpan.FromMilliseconds(Math.Min(order, 6) * 55);
            host.Opacity = 0;
            drift.Y = 26;
            scale.ScaleX = scale.ScaleY = 0.94;

            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(360))
            { BeginTime = delay, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            host.BeginAnimation(OpacityProperty, fade);

            var rise = new DoubleAnimation(26, 0, TimeSpan.FromMilliseconds(620))
            { BeginTime = delay, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            drift.BeginAnimation(TranslateTransform.YProperty, rise);

            var pop = new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(620))
            { BeginTime = delay, EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }

        private void MoveVisual(Visual vis, CanvasObject o, bool animate)
        {
            double x = o.X, y = o.Y, w = Math.Max(1, o.Width), h = Math.Max(1, o.Height);
            bool posChanged = Math.Abs(x - vis.X) > 0.5 || Math.Abs(y - vis.Y) > 0.5;
            bool sizeChanged = Math.Abs(w - vis.W) > 0.5 || Math.Abs(h - vis.H) > 0.5;
            if (!posChanged && !sizeChanged) return;

            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
            var dur = TimeSpan.FromMilliseconds(animate ? 620 : 0);

            if (posChanged)
            {
                var ax = new DoubleAnimation(vis.X, x, dur) { EasingFunction = ease };
                var ay = new DoubleAnimation(vis.Y, y, dur) { EasingFunction = ease };
                vis.Root.BeginAnimation(Canvas.LeftProperty, ax);
                vis.Root.BeginAnimation(Canvas.TopProperty, ay);
            }
            if (sizeChanged)
            {
                var aw = new DoubleAnimation(vis.W, w, dur) { EasingFunction = ease };
                var ah = new DoubleAnimation(vis.H, h, dur) { EasingFunction = ease };
                vis.Root.BeginAnimation(FrameworkElement.WidthProperty, aw);
                vis.Root.BeginAnimation(FrameworkElement.HeightProperty, ah);
            }

            vis.X = x; vis.Y = y; vis.W = w; vis.H = h;
        }

        private void RemoveWithFade(Visual vis, bool animate)
        {
            if (!animate)
            {
                Stage.Children.Remove(vis.Root);
                return;
            }
            var fade = new DoubleAnimation(vis.Root.Opacity, 0, TimeSpan.FromMilliseconds(240))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
            fade.Completed += (s, e) => Stage.Children.Remove(vis.Root);
            vis.Root.BeginAnimation(OpacityProperty, fade);
        }

        private void FadeOutAll(bool animate)
        {
            foreach (var vis in _items.Values.ToList())
            {
                var fade = new DoubleAnimation(vis.Root.Opacity, 0, TimeSpan.FromMilliseconds(animate ? 180 : 0));
                if (!animate) Stage.Children.Remove(vis.Root);
                else
                {
                    var v = vis;
                    fade.Completed += (s, e) => Stage.Children.Remove(v.Root);
                    v.Root.BeginAnimation(OpacityProperty, fade);
                }
            }
            _items.Clear();
        }

        private void ShowRealImage(string path, bool animate)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                FrameImage.Source = bmp;
                var fade = new DoubleAnimation(FrameImage.Opacity, 1, TimeSpan.FromMilliseconds(animate ? 260 : 0));
                FrameImage.BeginAnimation(OpacityProperty, fade);
            }
            catch { }
        }

        private void AnimateBackground(string hex)
        {
            Color target = ParseColor(hex);
            if (Root.Background is SolidColorBrush brush && !brush.IsFrozen)
            {
                var anim = new ColorAnimation(target, TimeSpan.FromMilliseconds(280))
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut } };
                brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
            }
            else
            {
                Root.Background = new SolidColorBrush(target);
            }
        }

        #endregion

        #region 对象 -> 视觉元素

        private static string Snapshot(CanvasObject o)
            => $"{o.Kind}|{o.Value}|{o.DisplayText}|{o.FontSize}|{o.Color}|{o.Font}|{o.Bold}|{o.Italic}|{o.ShapeLabel}|{o.HasSprite}";

        /// <summary>同一次放映里，用“种类 + 源码行 + 内容”作为跨构建的稳定身份。</summary>
        private static List<string> BuildKeys(List<CanvasObject> objects)
        {
            var seen = new Dictionary<string, int>();
            var keys = new List<string>(objects.Count);
            foreach (var o in objects)
            {
                string baseKey = $"{o.Kind}|{o.SourceLine}|{o.Value}|{o.DisplayText}|{o.ShapeLabel}";
                if (seen.TryGetValue(baseKey, out int n))
                {
                    seen[baseKey] = n + 1;
                    keys.Add(baseKey + "#" + (n + 1));
                }
                else
                {
                    seen[baseKey] = 0;
                    keys.Add(baseKey);
                }
            }
            return keys;
        }

        private FrameworkElement BuildContent(CanvasObject o)
        {
            switch (o.Kind)
            {
                case ObjectKind.Tex:
                    return BuildTex(o);
                case ObjectKind.Shape:
                case ObjectKind.Line:
                    return BuildShape(o);
                case ObjectKind.Image:
                    return BuildImageBox(o);
                case ObjectKind.Table:
                    return BuildTable(o);
                default:
                    return BuildText(o);
            }
        }

        private static FrameworkElement BuildText(CanvasObject o)
        {
            return new TextBlock
            {
                Text = o.DisplayText ?? "",
                Foreground = SafeBrush(o.Color),
                FontSize = Math.Max(6, o.FontSize),
                FontFamily = new FontFamily(string.IsNullOrWhiteSpace(o.Font) ? "Segoe UI" : o.Font),
                FontWeight = o.Bold ? FontWeights.Bold : FontWeights.Normal,
                FontStyle = o.Italic ? FontStyles.Italic : FontStyles.Normal,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };
        }

        private FrameworkElement BuildTex(CanvasObject o)
        {
            if (o.HasSprite)
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    bmp.UriSource = new Uri(o.SpritePath, UriKind.Absolute);
                    bmp.EndInit();
                    bmp.Freeze();
                    return new Image { Source = bmp, Stretch = Stretch.Uniform };
                }
                catch { }
            }
            var tb = new TextBlock
            {
                Text = o.DisplayText ?? o.Value ?? "",
                Foreground = SafeBrush(o.Color),
                FontSize = Math.Max(6, o.FontSize),
                FontFamily = new FontFamily("Cambria Math"),
                FontStyle = FontStyles.Italic,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            return tb;
        }

        private FrameworkElement BuildShape(CanvasObject o)
        {
            var brush = SafeBrush(o.Color);
            string label = o.ShapeLabel ?? "Rectangle";

            if (label == "Circle" || label == "Ellipse")
                return new Ellipse { Stroke = brush, StrokeThickness = 4, Fill = Brushes.Transparent, Stretch = Stretch.Fill };

            if (label == "Triangle")
            {
                return new Polygon
                {
                    Points = new PointCollection { new Point(0.5, 0), new Point(1, 1), new Point(0, 1) },
                    Stroke = brush, StrokeThickness = 4, Fill = Brushes.Transparent,
                    Stretch = Stretch.Fill
                };
            }

            if (label == "Line" || label == "Arrow")
            {
                var grid = new Grid();
                grid.Children.Add(new Line
                {
                    X1 = 0, Y1 = 0.5, X2 = 1, Y2 = 0.5,
                    Stroke = brush, StrokeThickness = Math.Max(2, o.Height),
                    Stretch = Stretch.Fill,
                    VerticalAlignment = VerticalAlignment.Center
                });
                if (label == "Arrow")
                {
                    grid.Children.Add(new Polygon
                    {
                        Points = new PointCollection { new Point(0, 0), new Point(1, 0.5), new Point(0, 1) },
                        Fill = brush, Stretch = Stretch.Fill,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Width = 16, VerticalAlignment = VerticalAlignment.Center
                    });
                }
                return grid;
            }

            return new Rectangle { Stroke = brush, StrokeThickness = 4, Fill = Brushes.Transparent, RadiusX = 2, RadiusY = 2 };
        }

        private FrameworkElement BuildImageBox(CanvasObject o)
        {
            var grid = new Grid();
            grid.Children.Add(new Rectangle
            {
                Stroke = SafeBrush("#6A6A6A"), StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 4, 3 }, Fill = SafeBrush("#18FFFFFF")
            });
            grid.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(o.ImageLabel) ? "图片" : o.ImageLabel,
                Foreground = SafeBrush("#AEAEAE"), FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            });
            return grid;
        }

        private FrameworkElement BuildTable(CanvasObject o)
        {
            var grid = new Grid();
            grid.Children.Add(new Rectangle { Stroke = SafeBrush(o.Color), StrokeThickness = 2, Fill = Brushes.Transparent });
            grid.Children.Add(new TextBlock
            {
                Text = (o.DisplayText ?? "").Replace(" / ", Environment.NewLine),
                Foreground = SafeBrush(o.Color), FontFamily = new FontFamily("Consolas"),
                FontSize = Math.Max(10, o.FontSize), TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            });
            return grid;
        }

        private static Brush SafeBrush(string hex)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(hex) && hex.StartsWith("#"))
                    return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            }
            catch { }
            return Brushes.White;
        }

        private static Color ParseColor(string hex)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(hex) && hex.StartsWith("#"))
                    return (Color)ColorConverter.ConvertFromString(hex);
            }
            catch { }
            return Colors.Black;
        }

        #endregion
    }
}
