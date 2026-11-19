using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
using ManimbleWin.Helpers;
using ManimbleWin.Models;

namespace ManimbleWin.ViewModels
{
    /// <summary>预览中的一帧：可能来自引擎真实 PNG，或来自本地对象的简化渲染。</summary>
    public class PreviewFrame
    {
        public string SlideName { get; set; }
        public string ImagePath { get; set; }          // 引擎真实渲染帧（可空）
        public bool HasRealImage => !string.IsNullOrEmpty(ImagePath) && System.IO.File.Exists(ImagePath);
        public ObservableCollection<CanvasObject> Objects { get; set; } = new ObservableCollection<CanvasObject>();
        public string Background { get; set; } = "#000000";
        public string TextColor { get; set; } = "#FFFFFF";
        public string Caption => string.IsNullOrEmpty(SlideName) ? "帧" : SlideName;
    }

    /// <summary>动画/视频预览的视图模型：把 deck 的所有 build 串成帧序列并自动播放。</summary>
    public class PreviewViewModel : ObservableObject
    {
        private readonly MainViewModel _vm;
        public ObservableCollection<PreviewFrame> Frames { get; } = new ObservableCollection<PreviewFrame>();

        private int _index = 0;
        public int Index
        {
            get => _index;
            set
            {
                if (SetProperty(ref _index, value))
                {
                    RaisePropertyChanged(nameof(Current));
                    RaisePropertyChanged(nameof(PositionText));
                    RaisePropertyChanged(nameof(Progress));
                }
            }
        }

        public PreviewFrame Current => Index >= 0 && Index < Frames.Count ? Frames[Index] : null;
        public string PositionText => Frames.Count == 0 ? "0 / 0" : $"{Index + 1} / {Frames.Count}";
        public double Progress => Frames.Count <= 1 ? 0 : (double)Index / (Frames.Count - 1) * 100;

        private bool _playing = true;
        public bool Playing
        {
            get => _playing;
            set { if (SetProperty(ref _playing, value)) RaisePropertyChanged(nameof(PlayLabel)); }
        }
        public string PlayLabel => Playing ? "⏸ 暂停" : "▶ 播放";

        private bool _onlyCurrent;
        public bool OnlyCurrent
        {
            get => _onlyCurrent;
            set => _onlyCurrent = value;
        }

        public System.Windows.Input.ICommand PlayPauseCommand { get; }
        public System.Windows.Input.ICommand NextCommand { get; }
        public System.Windows.Input.ICommand PrevCommand { get; }

        private readonly DispatcherTimer _timer;

        public PreviewViewModel(MainViewModel vm, int startIndex = -1)
        {
            _vm = vm;
            BuildFrames(startIndex);

            PlayPauseCommand = new RelayCommand(_ => Playing = !Playing);
            NextCommand = new RelayCommand(_ => { if (Index < Frames.Count - 1) Index++; });
            PrevCommand = new RelayCommand(_ => { if (Index > 0) Index--; });

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
            _timer.Tick += (s, e) =>
            {
                if (!Playing) return;
                if (Index < Frames.Count - 1) Index++;
                else Playing = false;
            };
            _timer.Start();
        }

        private void BuildFrames(int startIndex)
        {
            var slides = OnlyCurrent
                ? (_vm.CurrentSlide != null ? new List<Slide> { _vm.CurrentSlide } : new List<Slide>())
                : _vm.Slides.ToList();

            string bg = _vm.CurrentTheme?.Background ?? "#000000";
            string fg = _vm.CurrentTheme?.Text ?? "#FFFFFF";

            foreach (var slide in slides)
            {
                foreach (var build in slide.Builds)
                {
                    var frame = new PreviewFrame
                    {
                        SlideName = slide.Name,
                        ImagePath = build.RenderedImage,
                        Background = bg,
                        TextColor = fg
                    };
                    foreach (var o in build.Objects)
                        frame.Objects.Add(o);
                    Frames.Add(frame);
                }
            }

            if (Frames.Count == 0)
            {
                Frames.Add(new PreviewFrame { SlideName = "无内容", Background = bg, TextColor = fg });
            }

            if (startIndex >= 0 && startIndex < slides.Count)
            {
                int acc = 0;
                for (int i = 0; i < startIndex; i++) acc += slides[i].Builds.Count;
                Index = acc < Frames.Count ? acc : 0;
            }
        }

        public void Stop() => _timer.Stop();
    }
}
