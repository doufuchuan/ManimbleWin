using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
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
    /// 一个独立的终端会话，对应 VSCode 里的一个终端标签页。
    /// 自己的滚动缓冲、工作目录、命令历史、运行状态和子进程——切换标签互不影响，
    /// 后台标签里跑的命令会继续跑，输出也只写进它自己的缓冲。
    /// </summary>
    public class TerminalSession : ObservableObject
    {
        public const string Green = "#33FF66";
        public const string Grey = "#8A8A8A";
        public const string White = "#DADADA";
        public const string Blue = "#7EC6FF";
        public const string Red = "#F85149";
        public const string Orange = "#FF9F5A";

        private static Encoding _gbk;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private readonly TerminalViewModel _owner;
        private readonly List<string> _history = new List<string>();
        private int _historyIndex = -1;
        private Process _proc;

        public TerminalSession(TerminalViewModel owner, string shell, string title, string startDirectory)
        {
            _owner = owner;
            Shell = shell == "powershell" ? "powershell" : "cmd";
            _title = title;
            _currentDirectory = string.IsNullOrEmpty(startDirectory) ? EnvironmentService.ProjectRoot : startDirectory;
        }

        #region 标识

        public string Shell { get; }

        public string ShellName => Shell == "powershell" ? "PowerShell" : "CMD";

        private string _title;
        /// <summary>标签标题：cmd / cmd 2 / powershell …</summary>
        public string Title
        {
            get => _title;
            set { if (SetProperty(ref _title, value)) RaisePropertyChanged(nameof(TabTitle)); }
        }

        private bool _isActive;
        public bool IsActive
        {
            get => _isActive;
            set { if (SetProperty(ref _isActive, value)) RaisePropertyChanged(nameof(TabTitle)); }
        }

        /// <summary>标签上显示的文字：标题 + 运行中的小圆点。</summary>
        public string TabTitle => _title + (IsRunning ? "  ●" : "");

        #endregion

        #region 状态

        public ObservableCollection<TerminalLine> Lines { get; } = new ObservableCollection<TerminalLine>();

        private string _input = "";
        public string Input { get => _input; set => SetProperty(ref _input, value); }

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

        public string PromptText => (Shell == "powershell" ? "PS " : "") + CurrentDirectory + ">";

        private bool _isRunning;
        public bool IsRunning
        {
            get => _isRunning;
            set
            {
                if (!SetProperty(ref _isRunning, value)) return;
                RaisePropertyChanged(nameof(StatusText));
                RaisePropertyChanged(nameof(TabTitle));
            }
        }

        public string StatusText => IsRunning ? "执行中…" : "就绪";

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
                while (Lines.Count > 5000) Lines.RemoveAt(0);
            }

            if (dispatcher == null || dispatcher.CheckAccess()) Add();
            else dispatcher.Invoke(Add);

            _owner.NotifyOutput(this);
        }

        public void AppendInfo(string text, string color = null, bool bold = false) => Append(text, color, bold);

        /// <summary>回显用户敲的那一行（提示符 + 命令），这是唯一会往缓冲里写提示符的地方。</summary>
        private void AppendCommand(string line) => Append(PromptText + " " + line, Green, true);

        /// <summary>
        /// 会话开头的欢迎行。注意不要再补一行提示符：视图底部那个输入行本身就带提示符，
        /// 缓冲里再写一行就会出现两个一模一样的提示符叠在一起。
        /// </summary>
        public void WriteBanner()
        {
            Append("Manimble 终端 · " + ShellName + "（独立会话，工作目录 " + CurrentDirectory + "）", Green, true);
            Append("直接输入 Windows 命令；:help 查看应用内命令；交互式程序用「外部窗口」。", Grey);
            Append("", Grey);
        }

        public void Clear()
        {
            Lines.Clear();
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
                _owner.RunAppCommand(this, cmd.Substring(1).Trim());
                return;
            }

            if (TryHandleBuiltIn(cmd)) return;

            _ = RunShellAsync(cmd);
        }

        /// <summary>供历史上下键使用（每个会话一套自己的历史）。</summary>
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
                }
                else
                {
                    string target;
                    try { target = Path.IsPathRooted(arg) ? arg : Path.GetFullPath(Path.Combine(CurrentDirectory, arg)); }
                    catch { target = arg; }

                    if (Directory.Exists(target)) CurrentDirectory = target;
                    else Append("系统找不到指定的路径：" + arg, Red);
                }
                // 成功切换时不打印路径：输入行的提示符本身就会变成新目录（跟 cmd 一样安静）
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

                p.Start();

                // PowerShell 的 stderr 是 CLIXML 格式，先还原成普通文本再显示
                var clixml = new ClixmlFilter();

                var outTask = PumpAsync(p.StandardOutput.BaseStream, s => Append(CleanAnsi(s), White));
                var errTask = PumpAsync(p.StandardError.BaseStream, s =>
                {
                    foreach (string line in clixml.Feed(s))
                    {
                        if (line.Length == 0) continue;
                        Append(CleanAnsi(line), Red);
                    }
                });

                await Task.WhenAll(outTask, errTask);
                foreach (string line in clixml.Flush())
                {
                    if (line.Length == 0) continue;
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
            }
        }

        private ProcessStartInfo BuildStartInfo(string cmd)
        {
            string exe = Shell == "powershell" ? "powershell.exe" : "cmd.exe";
            // PowerShell 用 -EncodedCommand 传脚本：彻底避免引号 / 中文在命令行里被二次转义
            string args = Shell == "powershell"
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

        private static readonly Regex AnsiRegex =
            new Regex(@"\x1B\[[0-9;?]*[A-Za-z]|\x1B\][^\x07]*\x07", RegexOptions.Compiled);

        private static string CleanAnsi(string s) => string.IsNullOrEmpty(s) ? s : AnsiRegex.Replace(s, "");

        /// <summary>中断本会话当前命令（Ctrl+C / 停止按钮）。</summary>
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

        /// <summary>停止命令但不刷提示信息（关闭标签、退出应用时用）。</summary>
        public void KillQuietly()
        {
            try { _proc?.Kill(entireProcessTree: true); } catch { }
        }

        /// <summary>用真正的控制台窗口打开当前目录的 cmd / PowerShell（交互式程序用）。</summary>
        public void OpenExternal()
        {
            string dir = Directory.Exists(CurrentDirectory) ? CurrentDirectory : EnvironmentService.ProjectRoot;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Shell == "powershell" ? "powershell.exe" : "cmd.exe",
                    WorkingDirectory = dir,
                    UseShellExecute = true
                };
                psi.Arguments = Shell == "powershell" ? "-NoLogo -NoExit" : "/k";
                Process.Start(psi);
                Append("已打开系统 " + ShellName + " 窗口（工作目录同上）。", Blue);
            }
            catch (Exception ex)
            {
                Append("打开失败：" + ex.Message, Red);
            }
        }

        #endregion
    }
}
