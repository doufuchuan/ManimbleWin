using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ManimbleWin.ViewModels;

namespace ManimbleWin.Views
{
    public partial class PreviewWindow : Window
    {
        private readonly PreviewViewModel _vm;
        private readonly DispatcherTimer _imageHold;
        private readonly DispatcherTimer _mediaTimer;
        private int _clipIndex;

        public PreviewWindow(PreviewViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            DataContext = vm;

            _imageHold = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
            _imageHold.Tick += (s, e) => { _imageHold.Stop(); NextClip(); };

            // 真实 mp4 的进度由 MediaElement 驱动：定时回读位置，进度条拖动时回写 Position。
            _mediaTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _mediaTimer.Tick += (s, e) => UpdateMediaProgress();
            vm.SeekRequested += fraction =>
            {
                if (Movie.Source == null || !Movie.NaturalDuration.HasTimeSpan) return;
                TimeSpan total = Movie.NaturalDuration.TimeSpan;
                Movie.Position = TimeSpan.FromTicks((long)(total.Ticks * fraction));
            };
            vm.ClipNavigate += delta =>
            {
                _vm.Playing = true;
                if (delta == 0) { _clipIndex = 0; PlayCurrentClip(); }
                else if (delta > 0) NextClip();
                else if (_clipIndex > 0) { _clipIndex--; PlayCurrentClip(); }
            };
            vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName != nameof(PreviewViewModel.Playing) || !vm.HasRealClips || Movie.Source == null) return;
                if (vm.Playing) Movie.Play();
                else Movie.Pause();
            };

            vm.FrameChanged += (frame, animate, newSlide) =>
            {
                if (vm.HasRealClips) return;      // 真实片段播放时舞台隐藏
                Stage.Visibility = Visibility.Visible;
                Stage.Show(frame, animate, newSlide);
            };
            vm.ClipsReady += clips => Dispatcher.Invoke(() => PlayClips(clips));

            Loaded += (s, e) =>
            {
                _vm.Refresh();
                Keyboard.Focus(this);
            };
            Closing += (s, e) =>
            {
                _imageHold.Stop();
                _mediaTimer.Stop();
                vm.Stop();
                try { Movie.Stop(); Movie.Close(); } catch { }
            };
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.Space) { _vm.PlayPauseCommand.Execute(null); e.Handled = true; }
            else if (e.Key == Key.Right) { _vm.NextCommand.Execute(null); e.Handled = true; }
            else if (e.Key == Key.Left) { _vm.PrevCommand.Execute(null); e.Handled = true; }
            else if (e.Key == Key.Home) { _vm.RestartCommand.Execute(null); e.Handled = true; }
        }

        #region 真实渲染片段播放

        private void PlayClips(List<AnimationClip> clips)
        {
            if (clips == null || clips.Count == 0) return;
            _clipIndex = 0;
            Stage.Visibility = Visibility.Collapsed;
            ClipImage.Visibility = Visibility.Collapsed;
            Movie.Visibility = Visibility.Visible;
            _mediaTimer.Start();
            PlayCurrentClip();
        }

        private void UpdateMediaProgress()
        {
            if (!_vm.HasRealClips || Movie.Source == null || Movie.Visibility != Visibility.Visible) return;
            var clips = _vm.RealClips;
            string label = clips != null && _clipIndex < clips.Count ? clips[_clipIndex].Label : null;
            _vm.UpdateMediaTime(Movie.Position,
                Movie.NaturalDuration.HasTimeSpan ? Movie.NaturalDuration.TimeSpan : TimeSpan.Zero, label);
        }

        private void PlayCurrentClip()
        {
            var clips = _vm.RealClips;
            if (clips == null || _clipIndex < 0 || _clipIndex >= clips.Count)
            {
                _vm.Status = "片段播放完毕。";
                return;
            }

            var clip = clips[_clipIndex];
            _vm.UpdateMediaTime(TimeSpan.Zero, TimeSpan.Zero, clip.Label);
            if (string.IsNullOrEmpty(clip.Path) || !File.Exists(clip.Path))
            {
                NextClip();
                return;
            }

            if (clip.IsImage)
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(clip.Path, UriKind.Absolute);
                    bmp.EndInit();
                    bmp.Freeze();
                    Movie.Visibility = Visibility.Collapsed;
                    ClipImage.Source = bmp;
                    ClipImage.Visibility = Visibility.Visible;
                    _imageHold.Start();
                }
                catch { NextClip(); }
                return;
            }

            try
            {
                ClipImage.Visibility = Visibility.Collapsed;
                Movie.Visibility = Visibility.Visible;
                Movie.Source = new Uri(clip.Path, UriKind.Absolute);
                Movie.SpeedRatio = clip.Rate <= 0 ? 1.0 : clip.Rate;
                Movie.Position = TimeSpan.Zero;
                Movie.Play();
                _vm.Status = $"正在播放真实渲染片段 {_clipIndex + 1} / {clips.Count}";
            }
            catch (Exception ex)
            {
                _vm.Status = "播放失败：" + ex.Message;
            }
        }

        private void NextClip()
        {
            _imageHold.Stop();
            _clipIndex++;
            if (_vm.RealClips != null && _clipIndex < _vm.RealClips.Count)
            {
                PlayCurrentClip();
            }
            else
            {
                _vm.Status = "片段播放完毕。按 ESC 退出，或按 ← → 回到内置动画。";
                _vm.Playing = false;
            }
        }

        private void Movie_MediaEnded(object sender, RoutedEventArgs e) => NextClip();

        private void Movie_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            _vm.Status = "片段无法播放：" + e.ErrorException?.Message;
            NextClip();
        }

        #endregion
    }
}
