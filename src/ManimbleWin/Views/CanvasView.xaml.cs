using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ManimbleWin.Models;
using ManimbleWin.Services;
using ManimbleWin.ViewModels;

namespace ManimbleWin.Views
{
    /// <summary>
    /// 画布交互：点选、Shift 多选、框选、拖动（多选时整组一起动）。
    /// 坐标换算依赖 Viewbox 把鼠标位置映射回 1280x720 的设计坐标系，因此所有位置都取 SlideSurface 的坐标。
    /// </summary>
    public partial class CanvasView : UserControl
    {
        private CanvasObject _dragging;
        private Point _dragStart;
        private bool _isDragging;
        private bool _dragMoved;

        // 拖动开始时每个被拖对象的原始位置：多选拖动时按同一段位移移动，避免逐个累加漂移
        private readonly Dictionary<CanvasObject, (double X, double Y)> _dragOrigins =
            new Dictionary<CanvasObject, (double, double)>();
        private DeckSnapshot _dragSnapshot;

        // 框选
        private bool _rubberActive;
        private Point _rubberStart;

        public CanvasView()
        {
            InitializeComponent();
            DataContextChanged += (s, e) => { }; // 预留：VM 切换时可重置状态

            // 画布背景渐变的配色完全交给 XAML 里那两个 {DynamicResource CanvasTopColor/CanvasBottomColor}，
            // 这里刻意不做任何赋值：只要在代码里给 GradientStop.Color 赋过一次值，XAML 的
            // DynamicResource 表达式就会被顶掉（ReadLocalValue 变成 Color 而不是 ResourceReferenceExpression），
            // 之后渐变只能靠手动同步，等于把声明式的绑定换成命令式的追赶。原因见 CanvasView.xaml 里的注释。
        }

        private MainViewModel VM => DataContext as MainViewModel;

        private void CanvasBG_Click(object sender, MouseButtonEventArgs e)
        {
            if (VM != null) VM.ClearSelection();
        }

        private void SlideBG_Click(object sender, MouseButtonEventArgs e)
        {
            // 只有直接点在幻灯片空白处才取消选择；点在对象上由 Object_Click 处理并标记已处理
            if (e.Handled) return;

            if (VM == null) return;
            bool additive = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (!additive) VM.ClearSelection();

            // 在空白处按下即开始框选（Shift 时在已有选区上追加）
            _rubberActive = true;
            _rubberStart = e.GetPosition(SlideSurface);
            ShowRubber(_rubberStart, _rubberStart);
            SlideSurface.CaptureMouse();
            e.Handled = true;
        }

        private void Object_Click(object sender, MouseButtonEventArgs e)
        {
            var border = sender as Border;
            if (border?.Tag is CanvasObject o && VM != null)
            {
                bool additive = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                if (additive)
                {
                    VM.ToggleSelection(o);
                }
                else if (!o.IsSelected)
                {
                    // 点在未选中的对象上：换成单选它（含它的整组）
                    VM.SelectSingleObject(o);
                }
                // 已在选区内再按下：保持整个选区，直接开始拖动

                if (!VM.SelectedObjects.Contains(o)) { e.Handled = true; return; }

                _dragging = o;
                _dragStart = e.GetPosition(SlideSurface);
                _isDragging = true;
                _dragMoved = false;
                _dragOrigins.Clear();
                foreach (var sel in VM.SelectedObjects) _dragOrigins[sel] = (sel.X, sel.Y);
                _dragSnapshot = VM.BeginMoveTransaction();
                border.CaptureMouse();
                e.Handled = true; // 阻止 SlideBG_Click 取消选择
            }
        }

        private void Object_Move(object sender, MouseEventArgs e)
        {
            if (_isDragging && _dragging != null)
            {
                // 设计坐标：SlideSurface 宽 1280，Viewbox 已把鼠标坐标换算到该坐标系
                Point now = e.GetPosition(SlideSurface);
                double dx = now.X - _dragStart.X;
                double dy = now.Y - _dragStart.Y;

                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                {
                    // 按住 Shift 对齐到 10px 网格
                    foreach (var kv in _dragOrigins.ToList())
                    {
                        var o = kv.Key;
                        o.X = Math.Round((kv.Value.X + dx) / 10.0) * 10.0;
                        o.Y = Math.Round((kv.Value.Y + dy) / 10.0) * 10.0;
                    }
                }
                else
                {
                    foreach (var kv in _dragOrigins.ToList())
                    {
                        var o = kv.Key;
                        o.X = kv.Value.X + dx;
                        o.Y = kv.Value.Y + dy;
                    }
                }
                if (Math.Abs(dx) > 0.01 || Math.Abs(dy) > 0.01) _dragMoved = true;
                VM?.ShowLivePosition(_dragging);
                return;
            }

            if (_rubberActive)
            {
                Point now = e.GetPosition(SlideSurface);
                ShowRubber(_rubberStart, now);
            }
        }

        private void Object_Up(object sender, MouseButtonEventArgs e)
        {
            if (_rubberActive)
            {
                _rubberActive = false;
                if (SlideSurface.IsMouseCaptured) SlideSurface.ReleaseMouseCapture();
                HideRubber();

                Point end = e.GetPosition(SlideSurface);
                var rect = new Rect(
                    Math.Min(_rubberStart.X, end.X), Math.Min(_rubberStart.Y, end.Y),
                    Math.Abs(end.X - _rubberStart.X), Math.Abs(end.Y - _rubberStart.Y));

                // 太小的框选当作「点空白」，不做选择
                if (rect.Width > 3 || rect.Height > 3)
                {
                    bool additive = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                    VM?.SelectInRect(rect, additive);
                }
                return;
            }

            if (!_isDragging) return;
            _isDragging = false;
            if (sender is UIElement el && el.IsMouseCaptured) el.ReleaseMouseCapture();

            var dragged = _dragOrigins.Keys.ToList();
            _dragging = null;

            // 松手后把新坐标写回 Python 源码（多选时逐个写回）
            if (_dragMoved)
            {
                foreach (var o in dragged) VM?.CommitObjectMove(o);
                VM?.EndMoveTransaction(_dragSnapshot, dragged.Count > 1 ? $"移动 {dragged.Count} 个对象" : "移动对象");
            }
            _dragSnapshot = null;
            _dragOrigins.Clear();
            _dragMoved = false;
        }

        private void ShowRubber(Point a, Point b)
        {
            double x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
            RubberBand.Width = Math.Max(1, Math.Abs(b.X - a.X));
            RubberBand.Height = Math.Max(1, Math.Abs(b.Y - a.Y));
            RubberBand.Margin = new Thickness(x, y, 0, 0);
            RubberBand.Visibility = Visibility.Visible;
        }

        private void HideRubber() => RubberBand.Visibility = Visibility.Collapsed;
    }
}
