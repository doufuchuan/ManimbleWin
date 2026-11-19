using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using ManimbleWin.Helpers;
using ManimbleWin.Services;

namespace ManimbleWin.ViewModels
{
    public class TerminalLine
    {
        public string Text { get; set; }
        public string Foreground { get; set; }
        public bool Bold { get; set; }
    }

    /// <summary>
    /// 本机终端：直接调用系统的 cmd.exe / powershell.exe 执行命令并实时回显；
    /// 另用 ":" 前缀保留少量应用内命令（:play / :render / :connect …）。
    /// 交互式程序（python 交互模式、vim 等）请用「外部窗口」在真正的控制台里运行。
    /// </summary>
    public class TerminalViewModel : ObservableObject
    {
        private readonly MainViewModel _vm;

        public ObservableCollection<TerminalLine> Lines { get; } = new ObservableCollection<TerminalLine>();

        private const string Green = "#33FF66";
        private const string Grey = "#8A8A8A";
        private const string White = "#DADADA";
        private const string Blue = "#7EC6FF";
        private const string Red = "#F85149";
        private const string Orange = "#FF9F5A";

        private static Encoding _gbk;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        /// <summary>
        /// 控制台输出编码不固定：中文 Windows 默认 GBK，某些环境（chcp 65001 / 本工具链）是 UTF-8。
        /// 逐行尝试严格 UTF-8 解码，失败再按 GBK 解，两种都能正确显示。
        /// </summary>
        private static string DecodeLine(byte[] bytes)
        {
            int len = bytes.Length;
            while (len > 0 && (bytes[len - 1] == (byte)'\r')) len--;
            if (len == 0) return "";
            try
            {
                return StrictUtf8.GetString(bytes, 0, len);
            }
            catch
            {
                try
                {
                    if (_gbk == null)
                    {
                        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                        _gbk = Encoding.GetEncoding(936);
                    }
                    return _gbk.GetString(bytes, 0, len);
                }
                catch
                {
                    return Encoding.UTF8.GetString(bytes, 0, len);
                }
            }
        }

        public TerminalViewModel(MainViewModel vm)
        {
            _vm = vm;
            _currentDirectory = EnvironmentService.ProjectRoot;
            SubmitCommand = new RelayCommand(_ => Submit());
            ClearCommand = new RelayCommand(_ => Clear());
            CancelCommand = new RelayCommand(_ => Cancel());
            OpenExternalCommand = new RelayCommand(_ => OpenExternal());
            SetShellCommand = new RelayCommand(p => Shell = (p as string ?? "cmd").ToLowerInvariant());

            Append("Manimble 终端 · 直接调用本机 " + ShellName, Green, true);
            Append("直接输入 Windows 命令（dir / git status / python -V …）；:help 查看应用内命令；交互式程序用「外部窗口」。", Grey);
            Append("", Grey);
            Append(PromptText, Green, true);
        }

        #region 状态

        private string _input = "";
        public string Input { get => _input; set => SetProperty(ref _input, value); }

        private string _shell = "cmd";
        public string Shell
        {
            get => _shell;
            set
            {
                if (value != "cmd" && value != "powershell") return;
                if (!SetProperty(ref _shell, value)) return;
                RaisePropertyChanged(nameof(ShellName));
                RaisePropertyChanged(nameof(IsCmd));
                RaisePropertyChanged(nameof(IsPwsh));
                RaisePropertyChanged(nameof(PromptText));
                Append("已切换到 " + ShellName, Blue);
                Append(PromptText, Green, true);
            }
        }

        public bool IsCmd => _shell == "cmd";
        public bool IsPwsh => _shell == "powershell";
        public string ShellName => _shell == "powershell" ? "PowerShell" : "CMD";

        private string _currentDirectory;
        public string CurrentDirectory
        {
            get => _currentDirectory;
            set
            {
                if (!SetProperty(ref _currentDirectory, value)) return;
                RaisePropertyChanged(nameof(PromptText));
                RaisePropertyChanged(nameof(DirectoryLabel));
            }
        }

        public string DirectoryLabel => CurrentDirectory;

        public string PromptText => (_shell == "powershell" ? "PS " : "") + CurrentDirectory + ">";

        private bool _isRunning;
        public bool IsRunning
        {
            get => _isRunning;
            set { if (SetProperty(ref _isRunning, value)) RaisePropertyChanged(nameof(StatusText)); }
        }

        public string StatusText => IsRunning ? "执行中…" : "就绪";

        public ICommand SubmitCommand { get; }
        public ICommand ClearCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand OpenExternalCommand { get; }
        public ICommand SetShellCommand { get; }

        #endregion

        #region 输出

        private void Append(string text, string color = null, bool bold = false)
        {
            string c = color ?? White;
            var dispatcher = Application.Current?.Dispatcher;
            void Add()
            {
                foreach (var seg in (text ?? "").Split('\n'))
                    Lines.Add(new TerminalLine { Text = seg.TrimEnd('\r'), Foreground = c, Bold = bold });
                while (Lines.Count > 3000) Lines.RemoveAt(0);
            }

            if (dispatcher == null || dispatcher.CheckAccess()) Add();
            else dispatcher.Invoke(Add);
        }

        private void AppendCommand(string line) => Append(PromptText + " " + line, Green, true);

        private void Clear()
        {
            Lines.Clear();
            Append(PromptText, Green, true);
        }

        /// <summary>回车 / 提交。</summary>
        public void Submit()
        {
            string cmd = (Input ?? "").Trim();
            if (cmd.Length == 0) return;
            Input = "";

            if (_history.Count == 0 || _history[_history.Count - 1] != cmd) _history.Add(cmd);
            _historyIndex = _history.Count;

            AppendCommand(cmd);

            if (cmd.StartsWith(":"))
            {
                RunAppCommand(cmd.Substring(1).Trim());
                Append(PromptText, Green, true);
                return;
            }

            if (TryHandleBuiltIn(cmd)) return;

            _ = RunShellAsync(cmd);
        }

        #endregion

        #region 历史

        private readonly List<string> _history = new List<string>();
        private int _historyIndex = -1;

        /// <summary>供历史上下键使用。</summary>
        public string Recall(bool up)
        {
            if (_history.Count == 0) return Input;
            if (up) _historyIndex = Math.Max(0, _historyIndex - 1);
            else _historyIndex = Math.Min(_history.Count, _historyIndex + 1);
            Input = _historyIndex >= _history.Count || _historyIndex < 0 ? "" : _history[_historyIndex];
            return Input;
        }

        #endregion

        #region 进程执行

        private Process _proc;

        /// <summary>cd / cls 这类会影响会话状态的命令自己处理。</summary>
        private bool TryHandleBuiltIn(string cmd)
        {
            string t = cmd.Trim();
            string lower = t.ToLowerInvariant();

            if (lower == "cls" || lower == "clear")
            {
                Clear();
                return true;
            }

            var m = Regex.Match(t, @"^(cd|chdir)\s+(.+)$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string arg = m.Groups[2].Value.Trim().Trim('"');
                if (arg.Length == 0 || arg == "~")
                {
                    CurrentDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    Append(CurrentDirectory, Grey);
                }
                else
                {
                    string target;
                    try { target = Path.IsPathRooted(arg) ? arg : Path.GetFullPath(Path.Combine(CurrentDirectory, arg)); }
                    catch { target = arg; }

                    if (Directory.Exists(target))
                    {
                        CurrentDirectory = target;
                        Append(target, Grey);
                    }
                    else Append("系统找不到指定的路径：" + arg, Red);
                }
                Append(PromptText, Green, true);
                return true;
            }

            return false;
        }

        private async Task RunShellAsync(string cmd)
        {
            if (IsRunning)
            {
                Append("上一条命令仍在执行，请稍候或点「停止」。", Orange);
                return;
            }

            IsRunning = true;
            try
            {
                var psi = BuildStartInfo(cmd);
                using var p = new Process { StartInfo = psi };
                _proc = p;

                string errText = "";
                p.Start();

                // PowerShell 的 stderr 是 CLIXML 格式，先还原成普通文本再显示
                var clixml = new ClixmlFilter();

                var outTask = PumpAsync(p.StandardOutput.BaseStream, s => Append(CleanAnsi(s), White));
                var errTask = PumpAsync(p.StandardError.BaseStream, s =>
                {
                    foreach (string line in clixml.Feed(s))
                    {
                        if (line.Length == 0) continue;
                        errText += line + "\n";
                        Append(CleanAnsi(line), Red);
                    }
                });

                await Task.WhenAll(outTask, errTask);
                foreach (string line in clixml.Flush())
                {
                    if (line.Length == 0) continue;
                    errText += line + "\n";
                    Append(CleanAnsi(line), Red);
                }
                await Task.Run(() => p.WaitForExit());

                int code = -1;
                try { code = p.ExitCode; } catch { }
                if (code != 0) Append($"命令退出，代码 {code}", Red);
            }
            catch (Exception ex)
            {
                Append("无法执行命令：" + ex.Message, Red);
            }
            finally
            {
                _proc = null;
                IsRunning = false;
                Append(PromptText, Green, true);
            }
        }

        private ProcessStartInfo BuildStartInfo(string cmd)
        {
            string exe = _shell == "powershell" ? "powershell.exe" : "cmd.exe";
            // PowerShell 用 -EncodedCommand 传脚本：彻底避免引号 / 中文在命令行里被二次转义
            string args = _shell == "powershell"
                ? "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " +
                  Convert.ToBase64String(Encoding.Unicode.GetBytes(cmd))
                : "/d /c " + cmd;

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                WorkingDirectory = CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            EnvironmentService.ApplyCleanTemp(psi);
            return psi;
        }

        /// <summary>逐行读取子进程输出（按字节读出，避免编码预设导致的乱码）。</summary>
        private static async Task PumpAsync(Stream stream, Action<string> onLine)
        {
            var buf = new byte[4096];
            var line = new List<byte>(256);
            try
            {
                while (true)
                {
                    int n = await stream.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false);
                    if (n <= 0) break;
                    for (int i = 0; i < n; i++)
                    {
                        byte b = buf[i];
                        if (b == (byte)'\n')
                        {
                            onLine(DecodeLine(line.ToArray()));
                            line.Clear();
                        }
                        else line.Add(b);
                    }
                }
                if (line.Count > 0) onLine(DecodeLine(line.ToArray()));
            }
            catch { }
        }

        private static readonly Regex AnsiRegex =
            new Regex(@"\x1B\[[0-9;?]*[A-Za-z]|\x1B\][^\x07]*\x07", RegexOptions.Compiled);

        private static string CleanAnsi(string s) => string.IsNullOrEmpty(s) ? s : AnsiRegex.Replace(s, "");

        /// <summary>中断当前命令（Ctrl+C / 停止按钮）。</summary>
        public void Cancel()
        {
            var p = _proc;
            if (p == null) { Append("当前没有正在运行的命令。", Grey); return; }
            try
            {
                p.Kill(entireProcessTree: true);
                Append("已终止当前命令。", Orange);
            }
            catch (Exception ex)
            {
                Append("终止失败：" + ex.Message, Red);
            }
        }

        /// <summary>用真正的控制台窗口打开当前目录的 cmd / PowerShell（交互式程序用）。</summary>
        public void OpenExternal()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _shell == "powershell" ? "powershell.exe" : "cmd.exe",
                    WorkingDirectory = CurrentDirectory,
                    UseShellExecute = true
                };
                psi.Arguments = _shell == "powershell" ? "-NoLogo -NoExit" : "/k";
                Process.Start(psi);
                Append("已打开系统 " + ShellName + " 窗口（工作目录同上）。", Blue);
            }
            catch (Exception ex)
            {
                Append("打开失败：" + ex.Message, Red);
            }
        }

        #endregion

        #region 应用内命令（: 前缀）

        private void RunAppCommand(string raw)
        {
            var parts = raw.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            string cmd = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
            string arg = parts.Length > 1 ? parts[1].Trim() : "";

            switch (cmd)
            {
                case "help":
                    Append("应用内命令（Windows 命令直接输入即可）:", Green, true);
                    Append("  :slides            列出所有幻灯片", White);
                    Append("  :open <页码>       切换到指定幻灯片", White);
                    Append("  :insert [类型]     插入对象（text/formula/image/circle…）", White);
                    Append("  :render [公式]     用 LaTeX 渲染公式", White);
                    Append("  :connect           连接 Manim 引擎", White);
                    Append("  :engine            查看引擎状态", White);
                    Append("  :play              播放动画预览", White);
                    Append("  :export            导出 MP4 影片", White);
                    Append("  :source            打印当前页 Python 源码", White);
                    Append("  :shell cmd|ps      切换 CMD / PowerShell", White);
                    break;

                case "slides":
                case "list":
                    int n = 1;
                    foreach (var s in _vm.Slides)
                        Append($"  {(s == _vm.CurrentSlide ? "*" : " ")} {n++}. {s.Name}  [{s.Builds.Count} 构建]", s == _vm.CurrentSlide ? Green : White);
                    break;

                case "open":
                case "goto":
                    if (int.TryParse(arg, out int idx) && idx >= 1 && idx <= _vm.Slides.Count)
                    {
                        _vm.SelectSlideByIndex(idx - 1);
                        Append($"已切换到第 {idx} 页：{_vm.CurrentSlide.Name}", Green);
                    }
                    else Append("用法: :open <页码>", Orange);
                    break;

                case "connect":
                    Append("正在连接 Manim 引擎…", Blue);
                    _vm.ConnectFromTerminal();
                    break;

                case "engine":
                    Append(_vm.IsEngineConnected ? "引擎状态: 已连接（真实渲染）" : "引擎状态: 未连接（原型数据）",
                        _vm.IsEngineConnected ? Green : Orange);
                    break;

                case "render":
                case "tex":
                    string tex = string.IsNullOrEmpty(arg) ? @"e^{i\pi}+1=0" : arg;
                    Append("正在用 LaTeX 渲染公式…", Blue);
                    _vm.RenderFormulaFromTerminal(tex, (ok, msg) => Append(msg, ok ? Green : Red));
                    break;

                case "insert":
                    string kind = arg.Length == 0 ? "text" : arg.Split(' ')[0];
                    _vm.InsertObjectFromTerminal(NormalizeKind(kind));
                    Append($"已在画布插入对象：{kind}（Python 源码已同步更新）", Green);
                    break;

                case "play":
                case "present":
                    Append("正在打开动画预览…", Blue);
                    _vm.OpenPreviewFromTerminal();
                    break;

                case "export":
                    _vm.ExportMovieFromTerminal(msg => Append(msg, Blue));
                    break;

                case "source":
                    _vm.OpenSourceFromTerminal();
                    int c = 0;
                    foreach (var line in (_vm.CurrentSlide?.Source ?? "").Split('\n')) Append($"{(++c),3}: {line}", Grey);
                    break;

                case "shell":
                    Shell = arg.StartsWith("ps", StringComparison.OrdinalIgnoreCase) ||
                            arg.StartsWith("power", StringComparison.OrdinalIgnoreCase)
                        ? "powershell" : "cmd";
                    break;

                default:
                    Append($"未知应用命令: :{cmd}   （:help 查看列表）", Orange);
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

        /// <summary>清屏并回到项目目录（菜单「新建终端」）。</summary>
        public void ResetSession()
        {
            Cancel();
            Lines.Clear();
            CurrentDirectory = EnvironmentService.ProjectRoot;
            Append("Manimble 终端 · " + ShellName, Green, true);
            Append(PromptText, Green, true);
        }
    }
}
