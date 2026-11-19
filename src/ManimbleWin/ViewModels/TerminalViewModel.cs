using System;
using System.Collections.ObjectModel;
using System.Linq;
using ManimbleWin.Helpers;

namespace ManimbleWin.ViewModels
{
    public class TerminalLine
    {
        public string Text { get; set; }
        public string Foreground { get; set; }
        public bool Bold { get; set; }
    }

    /// <summary>
    /// 可交互终端：接受命令、维护历史、把结果写回行集合。
    /// 既能驱动真实引擎（render/connect/play/export），也能在原型模式下给出反馈。
    /// </summary>
    public class TerminalViewModel : ObservableObject
    {
        private readonly MainViewModel _vm;
        public ObservableCollection<TerminalLine> Lines { get; } = new ObservableCollection<TerminalLine>();

        private string _input = "";
        public string Input { get => _input; set => SetProperty(ref _input, value); }

        public System.Windows.Input.ICommand SubmitCommand { get; }

        private readonly System.Collections.Generic.List<string> _history = new System.Collections.Generic.List<string>();
        private int _historyIndex = -1;

        private const string Green = "#33FF66";
        private const string Grey = "#8A8A8A";
        private const string White = "#DADADA";
        private const string Blue = "#7EC6FF";
        private const string Orange = "#FF9F5A";

        public TerminalViewModel(MainViewModel vm)
        {
            _vm = vm;
            SubmitCommand = new RelayCommand(_ => Submit());
            Append("Manimble 终端  ·  输入 help 查看可用命令", Green, true);
            Append("", Grey);
        }

        private void Append(string text, string color = White, bool bold = false)
        {
            foreach (var seg in (text ?? "").Split('\n'))
                Lines.Add(new TerminalLine { Text = seg, Foreground = color, Bold = bold });
        }

        private void Submit()
        {
            string cmd = Input?.Trim() ?? "";
            if (cmd.Length == 0) return;
            Append("➜  " + cmd, Green);
            if (_history.LastOrDefault() != cmd) _history.Add(cmd);
            _historyIndex = _history.Count;
            Input = "";

            try { Execute(cmd); }
            catch (Exception ex) { Append("错误: " + ex.Message, "#F85149"); }
            Append("", Grey);
        }

        /// <summary>供历史上下键使用。</summary>
        public string Recall(bool up)
        {
            if (_history.Count == 0) return Input;
            if (up) _historyIndex = Math.Max(0, _historyIndex - 1);
            else _historyIndex = Math.Min(_history.Count, _historyIndex + 1);
            Input = _historyIndex >= _history.Count ? "" : _history[_historyIndex];
            return Input;
        }

        private void Execute(string raw)
        {
            var parts = raw.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            string cmd = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
            string arg = parts.Length > 1 ? parts[1].Trim() : "";

            switch (cmd)
            {
                case "help": PrintHelp(); break;
                case "clear": Lines.Clear(); break;
                case "echo": Append(arg, White); break;
                case "ls":
                case "dir":
                    Append("slides.py    common.py    config.py    manim.cfg    order.txt", White);
                    break;
                case "pwd": Append(_vm.DeckFolder, Grey); break;
                case "git": Append("位于分支 main\n工作区干净", Grey); break;

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
                    else Append("用法: open <页码>", Orange);
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
                    _vm.RenderFormulaFromTerminal(tex, (ok, msg) => Append(msg, ok ? Green : "#F85149"));
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

                case "claude":
                case "codex":
                case "gemini":
                    SimulateAgent(arg);
                    break;

                default:
                    Append($"未知命令: {cmd}   （输入 help 查看可用命令）", Orange);
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

        private void SimulateAgent(string request)
        {
            Append("● 正在读取 slides.py …", Grey);
            Append("● 分析当前场景结构 …", Grey);
            Append("● 生成源码补丁 …", Grey);
            if (!string.IsNullOrEmpty(request))
            {
                _vm.InsertObjectFromTerminal("Text");
                Append($"● 已应用更改：{request}", Blue);
            }
            Append("● 验证渲染 … done", Green);
            Append("更改已出现在画布上，Python 源码已同步。", Grey);
        }

        private void PrintHelp()
        {
            Append("可用命令:", Green, true);
            Append("  help              显示帮助", White);
            Append("  clear             清屏", White);
            Append("  slides            列出所有幻灯片", White);
            Append("  open <页码>       切换到指定幻灯片", White);
            Append("  insert [类型]     插入对象 (text/formula/image/table/circle…)", White);
            Append("  render [公式]     用 LaTeX 渲染公式", White);
            Append("  connect           连接 Manim 引擎", White);
            Append("  engine            查看引擎状态", White);
            Append("  play / present    播放动画预览", White);
            Append("  export            导出 MP4 影片", White);
            Append("  source            显示当前页 Python 源码", White);
            Append("  claude [需求]     模拟 AI 编程助手修改幻灯片", White);
        }
    }
}
