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
        private int _clipIndex;

        public PreviewWindow(PreviewViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            DataContext = vm;

            _imageHold = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
            _imageHold.Tick += (s, e) => { _imageHold.Stop(); NextClip(); };

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
            PlayCurrentClip();
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
            _clipIndex++;
            if (_vm.RealClips != null && _clipIndex < _vm.RealClips.Count)
                PlayCurrentClip();
            else
                _vm.Status = "片段播放完毕。按 ESC 退出，或按 ← → 回到内置动画。";
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
