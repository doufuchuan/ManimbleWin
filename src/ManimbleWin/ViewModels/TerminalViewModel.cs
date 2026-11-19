using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Input;
using ManimbleWin.Helpers;
using ManimbleWin.Services;

namespace ManimbleWin.ViewModels
{
    /// <summary>
    /// 终端面板的会话管理器，对应 VSCode 集成终端的标签栏：
    /// 每个标签是一个 <see cref="TerminalSession"/>，各自持有自己的输出、工作目录、历史与进程。
    /// 切换标签只是切换显示对象，不会重启、清空或影响另一个会话。
    /// </summary>
    public class TerminalViewModel : ObservableObject
    {
        private readonly MainViewModel _vm;

        public ObservableCollection<TerminalSession> Sessions { get; } = new ObservableCollection<TerminalSession>();

        private TerminalSession _active;

        /// <summary>当前显示的会话（绑定都走它，切标签时整块换掉）。</summary>
        public TerminalSession Active
        {
            get => _active;
            private set
            {
                if (ReferenceEquals(_active, value)) return;
                if (_active != null) _active.IsActive = false;
                if (SetProperty(ref _active, value))
                {
                    if (_active != null) _active.IsActive = true;
                    RaisePropertyChanged(nameof(HasActive));
                    ActiveChanged?.Invoke();
                }
            }
        }

        public bool HasActive => _active != null;

        /// <summary>活动会话有新输出（视图据此滚到底）。</summary>
        public event Action OutputChanged;

        /// <summary>切换了标签（视图据此滚动并聚焦输入）。</summary>
        public event Action ActiveChanged;

        public ICommand SubmitCommand { get; }
        public ICommand ClearCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand OpenExternalCommand { get; }
        public ICommand NewSessionCommand { get; }
        public ICommand SelectSessionCommand { get; }
        public ICommand CloseSessionCommand { get; }

        public TerminalViewModel(MainViewModel vm)
        {
            _vm = vm;

            SubmitCommand = new RelayCommand(_ => Active?.Submit());
            ClearCommand = new RelayCommand(_ => Active?.Clear());
            CancelCommand = new RelayCommand(_ => Active?.Cancel());
            OpenExternalCommand = new RelayCommand(_ => Active?.OpenExternal());
            NewSessionCommand = new RelayCommand(p => NewSession(p as string));
            SelectSessionCommand = new RelayCommand(p => { if (p is TerminalSession s) Active = s; });
            CloseSessionCommand = new RelayCommand(p => CloseSession(p as TerminalSession));

            NewSession("cmd");
        }

        /// <summary>活动会话的输出发生变化。</summary>
        internal void NotifyOutput(TerminalSession session)
        {
            if (ReferenceEquals(session, _active)) OutputChanged?.Invoke();
        }

        #region 会话生命周期

        /// <summary>
        /// 新建一个会话并切过去。shell 传空则沿用当前会话的类型；
        /// 工作目录沿用当前会话的（VSCode 也是这个行为）。
        /// </summary>
        public TerminalSession NewSession(string shell)
        {
            if (string.IsNullOrEmpty(shell)) shell = _active?.Shell ?? "cmd";
            string s = shell.ToLowerInvariant();
            bool pwsh = s == "powershell" || s == "ps" || s.StartsWith("power", StringComparison.Ordinal);
            string normalized = pwsh ? "powershell" : "cmd";

            string dir = _active?.CurrentDirectory;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) dir = EnvironmentService.ProjectRoot;

            string baseName = normalized == "powershell" ? "powershell" : "cmd";
            string title = baseName;
            int n = 2;
            while (Sessions.Any(x => string.Equals(x.Title, title, StringComparison.OrdinalIgnoreCase)))
                title = baseName + " " + (n++);

            var session = new TerminalSession(this, normalized, title, dir);
            Sessions.Add(session);
            Active = session;
            session.WriteBanner();
            return session;
        }

        /// <summary>关闭一个会话；关掉最后一个时补一个 CMD，面板不会空着。</summary>
        public void CloseSession(TerminalSession session)
        {
            if (session == null) return;
            int index = Sessions.IndexOf(session);
            if (index < 0) return;

            session.KillQuietly();
            Sessions.Remove(session);

            if (Sessions.Count == 0)
            {
                _active = null;
                NewSession("cmd");
                return;
            }

            if (ReferenceEquals(_active, session))
                Active = Sessions[Math.Min(index, Sessions.Count - 1)];
        }

        /// <summary>退出应用时收尾：把这些会话的子进程都杀掉，避免残留 cmd.exe。</summary>
        public void ShutdownAll()
        {
            foreach (var s in Sessions) s.KillQuietly();
        }

        #endregion

        #region 视图转发

        public void Submit() => Active?.Submit();

        public string Recall(bool up) => Active?.Recall(up) ?? "";

        public void Cancel() => Active?.Cancel();

        public void ClearActive() => Active?.Clear();

        /// <summary>菜单「新建终端」：新开一个同类型的会话并显示面板。</summary>
        public void ResetSession() => NewSession(_active?.Shell ?? "cmd");

        #endregion

        #region 应用内命令（: 前缀）

        internal void RunAppCommand(TerminalSession session, string raw)
        {
            var parts = raw.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            string cmd = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
            string arg = parts.Length > 1 ? parts[1].Trim() : "";

            switch (cmd)
            {
                case "help":
                    session.AppendInfo("应用内命令（Windows 命令直接输入即可）:", TerminalSession.Green, true);
                    session.AppendInfo("  :slides            列出所有幻灯片", TerminalSession.White);
                    session.AppendInfo("  :open <页码>       切换到指定幻灯片", TerminalSession.White);
                    session.AppendInfo("  :insert [类型]     插入对象（text/formula/image/circle…）", TerminalSession.White);
                    session.AppendInfo("  :render [公式]     用 LaTeX 渲染公式", TerminalSession.White);
                    session.AppendInfo("  :connect           连接 Manim 引擎", TerminalSession.White);
                    session.AppendInfo("  :engine            查看引擎状态", TerminalSession.White);
                    session.AppendInfo("  :play              播放动画预览", TerminalSession.White);
                    session.AppendInfo("  :export            导出 MP4 影片", TerminalSession.White);
                    session.AppendInfo("  :source            打印当前页 Python 源码", TerminalSession.White);
                    session.AppendInfo("  :new [cmd|ps]      新建一个独立终端标签", TerminalSession.White);
                    session.AppendInfo("  :close             关闭当前终端标签", TerminalSession.White);
                    break;

                case "slides":
                case "list":
                    int n = 1;
                    foreach (var s in _vm.Slides)
                        session.AppendInfo($"  {(s == _vm.CurrentSlide ? "*" : " ")} {n++}. {s.Name}  [{s.Builds.Count} 构建]",
                            s == _vm.CurrentSlide ? TerminalSession.Green : TerminalSession.White);
                    break;

                case "open":
                case "goto":
                    if (int.TryParse(arg, out int idx) && idx >= 1 && idx <= _vm.Slides.Count)
                    {
                        _vm.SelectSlideByIndex(idx - 1);
                        session.AppendInfo($"已切换到第 {idx} 页：{_vm.CurrentSlide.Name}", TerminalSession.Green);
                    }
                    else session.AppendInfo("用法: :open <页码>", TerminalSession.Orange);
                    break;

                case "connect":
                    session.AppendInfo("正在连接 Manim 引擎…", TerminalSession.Blue);
                    _vm.ConnectFromTerminal();
                    break;

                case "engine":
                    session.AppendInfo(_vm.IsEngineConnected ? "引擎状态: 已连接（真实渲染）" : "引擎状态: 未连接（原型数据）",
                        _vm.IsEngineConnected ? TerminalSession.Green : TerminalSession.Orange);
                    break;

                case "render":
                case "tex":
                    string tex = string.IsNullOrEmpty(arg) ? @"e^{i\pi}+1=0" : arg;
                    session.AppendInfo("正在用 LaTeX 渲染公式…", TerminalSession.Blue);
                    _vm.RenderFormulaFromTerminal(tex, (ok, msg) =>
                        session.AppendInfo(msg, ok ? TerminalSession.Green : TerminalSession.Red));
                    break;

                case "insert":
                    string kind = arg.Length == 0 ? "text" : arg.Split(' ')[0];
                    _vm.InsertObjectFromTerminal(NormalizeKind(kind));
                    session.AppendInfo($"已在画布插入对象：{kind}（Python 源码已同步更新）", TerminalSession.Green);
                    break;

                case "play":
                case "present":
                    session.AppendInfo("正在打开动画预览…", TerminalSession.Blue);
                    _vm.OpenPreviewFromTerminal();
                    break;

                case "export":
                    _vm.ExportMovieFromTerminal(msg => session.AppendInfo(msg, TerminalSession.Blue));
                    break;

                case "source":
                    _vm.OpenSourceFromTerminal();
                    int c = 0;
                    foreach (var line in (_vm.CurrentSlide?.Source ?? "").Split('\n'))
                        session.AppendInfo($"{(++c),3}: {line}", TerminalSession.Grey);
                    break;

                case "new":
                    NewSession(arg);
                    break;

                case "close":
                    CloseSession(session);
                    break;

                case "shell":
                    // 兼容旧用法：:shell ps 等价于切到（或新开）一个 PowerShell 标签
                    string want = arg.StartsWith("ps", StringComparison.OrdinalIgnoreCase) ||
                                  arg.StartsWith("power", StringComparison.OrdinalIgnoreCase)
                        ? "powershell" : "cmd";
                    var hit = Sessions.FirstOrDefault(x => x.Shell == want);
                    if (hit != null) Active = hit;
                    else NewSession(want);
                    break;

                default:
                    session.AppendInfo($"未知应用命令: :{cmd}   （:help 查看列表）", TerminalSession.Orange);
                    break;
            }
        }

        private static string NormalizeKind(string k)
        {
            switch (k.ToLowerInvariant())
            {
                case "text": return "Text";
                case "formula": case "tex": case "math": return "Formula";
                case "image": case "pic": return "Image";
                case "table": return "Table";
                case "circle": return "Circle";
                case "rectangle": case "rect": return "Rectangle";
                case "line": return "Line";
                default: return "Rectangle";
            }
        }

        #endregion
    }
}
