using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using ManimbleWin.Helpers;

namespace ManimbleWin.ViewModels
{
    /// <summary>deck_pages 返回的「一页」：场景名 + 各 build 静帧路径 + 备注。</summary>
    public class DeckPageInfo
    {
        public string Scene { get; set; } = "";
        public List<string> Pages { get; } = new List<string>();
        public List<string> BuildNotes { get; } = new List<string>();
        public string Notes { get; set; } = "";
        public string Error { get; set; } = "";
        public bool Pending { get; set; }
    }

    /// <summary>演讲者视图里的一帧（一个 build）。</summary>
    internal sealed class PresenterFrame
    {
        public string Scene { get; set; }
        public string Image { get; set; }
        public string Notes { get; set; }
        public string BuildNote { get; set; }
    }

    /// <summary>
    /// 演讲者视图：静帧来自引擎 deck_pages，不监听 present 事件、不依赖后台动画构建。
    /// 未连引擎 / 静帧未就绪时给出明确提示，不抛异常。
    /// </summary>
    public class PresenterViewModel : ObservableObject
    {
        private readonly MainViewModel _main;
        private readonly List<PresenterFrame> _frames = new List<PresenterFrame>();
        private int _index = -1;

        private readonly DispatcherTimer _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private TimeSpan _elapsed;
        private bool _running;

        private readonly RelayCommand _prevCommand;
        private readonly RelayCommand _nextCommand;

        public ICommand PrevCommand => _prevCommand;
        public ICommand NextCommand => _nextCommand;
        public ICommand ToggleTimerCommand { get; }
        public ICommand ResetTimerCommand { get; }

        public PresenterViewModel(MainViewModel main)
        {
            _main = main;
            _prevCommand = new RelayCommand(_ => Move(-1), _ => _index > 0);
            _nextCommand = new RelayCommand(_ => Move(1), _ => _index >= 0 && _index < _frames.Count - 1);
            ToggleTimerCommand = new RelayCommand(_ => ToggleTimer());
            ResetTimerCommand = new RelayCommand(_ => ResetTimer());
            _clock.Tick += (s, e) =>
            {
                _elapsed = _elapsed.Add(TimeSpan.FromSeconds(1));
                RaisePropertyChanged(nameof(ElapsedText));
            };
        }

        /// <summary>从引擎加载当前页与后续页的静帧。窗口 Loaded 时调用。</summary>
        public async Task LoadAsync()
        {
            _main.FlushNotesForPresenter();
            if (_main.Engine == null || !_main.Engine.IsConnected)
            {
                StatusText = "未连接 Manim 引擎：演讲者视图的静帧需要引擎渲染。请先「引擎 ▸ 连接 Manim 引擎」。";
                return;
            }
            string current = _main.CurrentSlide?.Name;
            if (string.IsNullOrEmpty(current))
            {
                StatusText = "当前没有幻灯片。";
                return;
            }

            StatusText = "正在加载静帧…";
            List<DeckPageInfo> slides;
            try { slides = await _main.Engine.DeckPages(current, 3); }
            catch (Exception ex) { StatusText = "演讲者视图加载失败：" + ex.Message; return; }
            if (slides == null)
            {
                StatusText = "无法从引擎读取静帧（deck_pages 不可用）。请检查引擎连接后重试。";
                return;
            }

            _frames.Clear();
            foreach (var s in slides)
            {
                // 未就绪的页返回空 pages，这里仍放一个占位帧，翻过去能看到「静帧未就绪」提示。
                int count = Math.Max(1, s.Pages.Count);
                for (int i = 0; i < count; i++)
                {
                    _frames.Add(new PresenterFrame
                    {
                        Scene = s.Scene,
                        Image = i < s.Pages.Count ? s.Pages[i] : "",
                        Notes = s.Notes,
                        BuildNote = i < s.BuildNotes.Count ? s.BuildNotes[i] : ""
                    });
                }
            }
            if (_frames.Count == 0)
            {
                StatusText = "引擎没有返回可用页面，可能尚未渲染完成。稍后重新打开演讲者视图再试。";
                return;
            }

            int start = 0;
            for (int i = 0; i < _frames.Count; i++)
            {
                if (!string.Equals(_frames[i].Scene, current, StringComparison.OrdinalIgnoreCase)) continue;
                start = i + Math.Max(0, _main.CurrentSlide.CurrentBuildIndex);
                break;
            }
            _index = Math.Min(start, _frames.Count - 1);
            StatusText = "静帧来自引擎渲染缓存 · ← → 翻页 · ESC 关闭";
            RaiseAll();
        }

        private PresenterFrame Current => _index >= 0 && _index < _frames.Count ? _frames[_index] : null;
        private PresenterFrame Next => _index + 1 >= 0 && _index + 1 < _frames.Count ? _frames[_index + 1] : null;

        public string CurrentImage => Current?.Image ?? "";
        public string NextImage => Next?.Image ?? "";
        public bool HasCurrentImage => !string.IsNullOrEmpty(CurrentImage);
        public bool HasNextImage => !string.IsNullOrEmpty(NextImage);

        public string CurrentLabel => Current == null ? "当前页" : "当前页 · " + Current.Scene;
        public string NextLabel => Next == null ? "下一页" : "下一页 · " + Next.Scene;

        public string CurrentNotes => CombineNotes(Current);
        public string NextNotes => CombineNotes(Next);

        private static string CombineNotes(PresenterFrame f)
        {
            if (f == null) return "";
            string slide = f.Notes ?? "";
            string build = f.BuildNote ?? "";
            if (string.IsNullOrWhiteSpace(slide)) return build;
            if (string.IsNullOrWhiteSpace(build)) return slide;
            return slide + "\n\n" + build;
        }

        public string PositionText => _index < 0 ? "— / —" : $"{_index + 1} / {_frames.Count}";
        public string ElapsedText => $"{(int)_elapsed.TotalHours:00}:{_elapsed.Minutes:00}:{_elapsed.Seconds:00}";
        public string TimerButtonText => _running ? "暂停" : "开始";
        public bool TimerRunning => _running;

        private string _statusText = "正在准备…";
        public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

        private void Move(int delta)
        {
            int target = _index + delta;
            if (target < 0 || target >= _frames.Count) return;
            _index = target;
            RaiseAll();
        }

        private void ToggleTimer()
        {
            _running = !_running;
            if (_running) _clock.Start(); else _clock.Stop();
            RaisePropertyChanged(nameof(TimerButtonText));
            RaisePropertyChanged(nameof(TimerRunning));
        }

        private void ResetTimer()
        {
            _running = false;
            _clock.Stop();
            _elapsed = TimeSpan.Zero;
            RaisePropertyChanged(nameof(ElapsedText));
            RaisePropertyChanged(nameof(TimerButtonText));
            RaisePropertyChanged(nameof(TimerRunning));
        }

        private void RaiseAll()
        {
            foreach (var name in new[]
            {
                nameof(CurrentImage), nameof(NextImage), nameof(HasCurrentImage), nameof(HasNextImage),
                nameof(CurrentLabel), nameof(NextLabel), nameof(CurrentNotes), nameof(NextNotes),
                nameof(PositionText)
            })
                RaisePropertyChanged(name);
            _prevCommand.RaiseCanExecuteChanged();
            _nextCommand.RaiseCanExecuteChanged();
        }

        public void OnClosed()
        {
            _clock.Stop();
            _running = false;
        }
    }
}
