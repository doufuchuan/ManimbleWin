using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using ManimbleWin.Helpers;
using ManimbleWin.Models;

namespace ManimbleWin.ViewModels
{
    /// <summary>预览中的一帧（对应的是一次构建的最终画面）。</summary>
    public class PreviewFrame
    {
        public Slide Slide { get; set; }
        public int BuildIndex { get; set; }
        public string SlideName { get; set; }
        public string SlideKey { get; set; } = "";
        public bool IsSlideStart { get; set; }
        public string ImagePath { get; set; }
        public bool HasRealImage => !string.IsNullOrEmpty(ImagePath) && System.IO.File.Exists(ImagePath);
        public List<CanvasObject> Objects { get; set; } = new List<CanvasObject>();
        public string Background { get; set; } = "#000000";
        public string TextColor { get; set; } = "#FFFFFF";
        public int DurationMs { get; set; } = 1700;
        public int NewObjectCount { get; set; }
        public string Caption => string.IsNullOrEmpty(SlideName) ? "帧" : SlideName;
    }

    /// <summary>一段由 Manim 真实渲染出来的动画片段（video = mp4，image = 静帧）。</summary>
    public class AnimationClip
    {
        public string Path { get; set; }
        public bool IsImage { get; set; }
        public double Rate { get; set; } = 1.0;
        public string Label { get; set; }
    }

    /// <summary>
    /// 播放器：把 deck 的所有构建排成时间线，按持续时间连续播放。
    /// 画面本身由 AnimatedStage 做对象级动画（淡入 / 位移 / 形变），
    /// 若引擎能构建真实片段，则切到真实 mp4 播放。
    /// </summary>
    public class PreviewViewModel : ObservableObject
    {
        private readonly MainViewModel _main;
        public ObservableCollection<PreviewFrame> Frames { get; } = new ObservableCollection<PreviewFrame>();

        private int _index;
        private double _elapsedMs;
        private string _lastSlideKey;

        /// <summary>画面切换通知：frame / 是否做动画 / 是否换页。</summary>
        public event Action<PreviewFrame, bool, bool> FrameChanged;
        /// <summary>真实渲染片段就绪。</summary>
        public event Action<List<AnimationClip>> ClipsReady;
        /// <summary>进度条拖动：参数是 0..1 的片段位置（真实 mp4 模式）。</summary>
        public event Action<double> SeekRequested;
        /// <summary>真实片段模式下的切换：0 = 重新开始，±1 = 上/下一段。</summary>
        public event Action<int> ClipNavigate;

        public PreviewViewModel(MainViewModel main, int startIndex = -1)
        {
            _main = main;
            BuildFrames(startIndex);

            PlayPauseCommand = new RelayCommand(_ => Playing = !Playing);
            NextCommand = new RelayCommand(_ =>
            {
                if (HasRealClips) { ClipNavigate?.Invoke(+1); return; }
                Playing = false; Step(+1);
            });
            PrevCommand = new RelayCommand(_ =>
            {
                if (HasRealClips) { ClipNavigate?.Invoke(-1); return; }
                Playing = false; Step(-1);
            });
            RestartCommand = new RelayCommand(_ =>
            {
                if (HasRealClips) { ClipNavigate?.Invoke(0); return; }
                _elapsedMs = 0;
                Index = 0;
                Playing = true;
            });
            RenderRealCommand = new RelayCommand(_ => _ = RenderRealAsync());

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _timer.Tick += (s, e) => Tick();
            _timer.Start();
        }

        #region 时间线

        public int Index
        {
            get => _index;
            set
            {
                if (value < 0) value = 0;
                if (value >= Frames.Count) value = Math.Max(0, Frames.Count - 1);
                if (_index == value && _lastSlideKey != null) return;
                _index = value;
                _elapsedMs = 0;
                RaisePropertyChanged(nameof(Index));
                RaisePropertyChanged(nameof(Current));
                RaisePropertyChanged(nameof(PositionText));
                RaisePropertyChanged(nameof(SlideText));
                RaiseProgress();
                RaiseCurrentFrame();
            }
        }

        public PreviewFrame Current => Frames.Count == 0 ? null : Frames[Math.Min(_index, Frames.Count - 1)];

        // 真实 mp4 模式：进度按当前片段的时间走，而不是按构建帧的序号。
        private double _mediaProgress;
        private string _timeText = "0:00 / 0:00";
        private string _clipLabel = "";

        public string PositionText => HasRealClips
            ? _timeText
            : (Frames.Count == 0 ? "0 / 0" : $"{_index + 1} / {Frames.Count}");
        public string SlideText => HasRealClips
            ? (string.IsNullOrEmpty(_clipLabel) ? "真实渲染片段" : _clipLabel)
            : (Current == null ? "" : $"{Current.SlideName} · 构建 {Current.BuildIndex + 1}");

        /// <summary>视图定时把 MediaElement 的 Position/NaturalDuration 回报到这里。</summary>
        public void UpdateMediaTime(TimeSpan position, TimeSpan duration, string label = null)
        {
            _mediaProgress = duration.TotalSeconds > 0
                ? Math.Max(0, Math.Min(100, position.TotalSeconds / duration.TotalSeconds * 100))
                : 0;
            _timeText = FormatTime(position) + " / " + FormatTime(duration);
            if (label != null) _clipLabel = label;
            RaisePropertyChanged(nameof(MediaProgress));
            RaisePropertyChanged(nameof(PositionText));
            RaisePropertyChanged(nameof(SlideText));
            RaiseProgress();
        }

        private static string FormatTime(TimeSpan t)
            => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

        private bool _seeking;
        public double MediaProgress => _mediaProgress;

        public double Progress
        {
            get => HasRealClips ? _mediaProgress : (Frames.Count <= 1 ? 0 : (double)_index / (Frames.Count - 1) * 100);
            set
            {
                if (HasRealClips)
                {
                    double fraction = Math.Max(0, Math.Min(100, value)) / 100.0;
                    _mediaProgress = fraction * 100;
                    RaisePropertyChanged(nameof(MediaProgress));
                    RaisePropertyChanged(nameof(Progress));
                    SeekRequested?.Invoke(fraction);
                    return;
                }
                if (Frames.Count <= 1 || _seeking) return;
                _seeking = true;
                Index = (int)Math.Round(Math.Max(0, Math.Min(100, value)) / 100.0 * (Frames.Count - 1));
                _seeking = false;
            }
        }

        private void RaiseProgress()
        {
            RaisePropertyChanged(nameof(Progress));
        }

        private void RaiseCurrentFrame()
        {
            var frame = Current;
            if (frame == null) return;
            bool newSlide = _lastSlideKey == null || frame.SlideKey != _lastSlideKey;
            _lastSlideKey = frame.SlideKey;
            FrameChanged?.Invoke(frame, true, newSlide);
        }

        private void Step(int delta)
        {
            int next = _index + delta;
            if (next < 0) next = 0;
            if (next >= Frames.Count) next = Frames.Count - 1;
            Index = next;
        }

        private void Tick()
        {
            if (!Playing || Frames.Count == 0) return;
            if (HasRealClips) return;               // 真实片段由 MediaElement 自己驱动
            _elapsedMs += _timer.Interval.TotalMilliseconds;
            if (_elapsedMs < (Current?.DurationMs ?? 1700)) return;
            if (_index < Frames.Count - 1) Index = _index + 1;
            else Playing = false;
        }

        private bool _playing = true;
        public bool Playing
        {
            get => _playing;
            set
            {
                if (SetProperty(ref _playing, value))
                {
                    RaisePropertyChanged(nameof(PlayLabel));
                    if (value && _index >= Frames.Count - 1 && Frames.Count > 1) { Index = 0; }
                }
            }
        }
        public string PlayLabel => Playing ? "⏸ 暂停" : "▶ 播放";

        public bool OnlyCurrent { get; set; }

        public ICommand PlayPauseCommand { get; }
        public ICommand NextCommand { get; }
        public ICommand PrevCommand { get; }
        public ICommand RestartCommand { get; }
        public ICommand RenderRealCommand { get; }

        private readonly DispatcherTimer _timer;

        #endregion

        #region 时间线构建

        private void BuildFrames(int startIndex)
        {
            var slides = OnlyCurrent
                ? (_main.CurrentSlide != null ? new List<Slide> { _main.CurrentSlide } : new List<Slide>())
                : _main.Slides.ToList();

            string bg = _main.CurrentTheme?.Background ?? "#000000";
            string fg = _main.CurrentTheme?.Text ?? "#FFFFFF";

            foreach (var slide in slides)
            {
                string prevKeySet = "";
                for (int bi = 0; bi < slide.Builds.Count; bi++)
                {
                    var build = slide.Builds[bi];
                    var frame = new PreviewFrame
                    {
                        Slide = slide,
                        BuildIndex = bi,
                        SlideName = slide.Name,
                        SlideKey = slide.Name + "#" + slide.Number,
                        IsSlideStart = bi == 0,
                        ImagePath = build.RenderedImage,
                        Background = bg,
                        TextColor = fg,
                        Objects = build.Objects.Select(o => o.Clone()).ToList()
                    };

                    // 与上一构建比较，算出“这一帧新出现多少对象”，用来决定停留时长
                    var keys = frame.Objects
                        .Select(o => $"{o.Kind}|{o.SourceLine}|{o.Value}|{o.DisplayText}|{o.ShapeLabel}")
                        .ToList();
                    var keySet = new HashSet<string>(keys);
                    var prevSet = new HashSet<string>(prevKeySet.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                    frame.NewObjectCount = keySet.Count(k => !prevSet.Contains(k));
                    prevKeySet = string.Join("\n", keys);

                    int duration = frame.HasRealImage
                        ? 2300
                        : 1450 + Math.Min(5, frame.NewObjectCount) * 260;
                    if (bi == 0) duration += 250;
                    frame.DurationMs = Math.Min(4200, duration);

                    Frames.Add(frame);
                }
            }

            if (Frames.Count == 0)
                Frames.Add(new PreviewFrame { SlideName = "无内容", Background = bg, TextColor = fg });

            int index = 0;
            if (startIndex >= 0 && startIndex < slides.Count)
            {
                int acc = 0;
                for (int i = 0; i < startIndex; i++) acc += slides[i].Builds.Count;
                index = Math.Min(acc, Frames.Count - 1);
            }
            else if (_main.CurrentSlide != null)
            {
                // 从当前页当前构建开始
                int acc = 0;
                for (int i = 0; i < slides.Count; i++)
                {
                    if (ReferenceEquals(slides[i], _main.CurrentSlide))
                    {
                        acc += Math.Max(0, Math.Min(_main.CurrentSlide.CurrentBuildIndex, slides[i].Builds.Count - 1));
                        break;
                    }
                    acc += slides[i].Builds.Count;
                }
                index = Math.Min(acc, Frames.Count - 1);
            }

            _index = index;
            _elapsedMs = 0;
        }

        #endregion

        #region 真实渲染片段

        private List<AnimationClip> _clips;
        public List<AnimationClip> RealClips => _clips;
        public bool HasRealClips => _clips != null && _clips.Count > 0;

        private string _status = "";
        public string Status { get => _status; set => SetProperty(ref _status, value); }

        private bool _rendering;
        public bool IsRendering
        {
            get => _rendering;
            set { if (SetProperty(ref _rendering, value)) { RaisePropertyChanged(nameof(RenderRealLabel)); RaisePropertyChanged(nameof(ShowBusy)); } }
        }
        public bool ShowBusy => _rendering;
        public string RenderRealLabel => "用 Manim 渲染真实动画";

       private async Task RenderRealAsync()
        {
            if (_rendering) return;
            string scene = Current?.SlideName;
            if (string.IsNullOrEmpty(scene)) return;
            if (_main == null || !_main.IsEngineConnected)
            {
                Status = "未连接 Manim 引擎，正在使用内置动画播放。可在「引擎 ▸ 连接 Manim 引擎」后重试。";
                return;
            }
            IsRendering = true;
            Status = "正在用 Manim 构建动画片段…（首次较慢，请稍候）";
            try
            {
                var clips = await _main.BuildAnimationClipsAsync(scene);
                if (clips != null && clips.Count > 0)
                {
                    _clips = clips;
                    RaisePropertyChanged(nameof(HasRealClips));
                    RaisePropertyChanged(nameof(PositionText));
                    RaisePropertyChanged(nameof(SlideText));
                    RaiseProgress();
                    Status = $"已生成 {clips.Count} 段真实渲染片段，正在播放。";
                    ClipsReady?.Invoke(clips);
                }
                else
                {
                    Status = "引擎没有返回动画片段（可能缺少 manim / ffmpeg）。继续使用内置动画播放。";
                }
            }
            catch (Exception ex)
            {
                Status = "真实渲染失败：" + ex.Message;
            }
            finally
            {
                IsRendering = false;
            }
        }

        #endregion

        public void Stop()
        {
            _timer.Stop();
            Playing = false;
        }

        /// <summary>窗口刚打开时主动推一次当前帧。</summary>
        public void Refresh() => RaiseCurrentFrame();
    }
}
