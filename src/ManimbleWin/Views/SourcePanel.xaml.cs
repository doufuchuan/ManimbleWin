using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ManimbleWin.Helpers;
using ManimbleWin.ViewModels;

namespace ManimbleWin.Views
{
    /// <summary>
    /// 代码面板：RichTextBox + Python 语法高亮。
    /// 高亮采用“延迟重排”（约 150ms），重排后恢复光标与滚动位置；
    /// 因为关掉了 WPF 自带撤销，这里维护文本快照栈支持 Ctrl+Z / Ctrl+Y。
    /// </summary>
    public partial class SourcePanel : UserControl
    {
        private readonly DispatcherTimer _debounce;
        private readonly Stack<string> _undo = new Stack<string>();
        private readonly Stack<string> _redo = new Stack<string>();
        private readonly TranslateTransform _gutterShift = new TranslateTransform();

        private MainViewModel _vm;
        private bool _applying;
        private string _committed = "";
        private ScrollViewer _scrollViewer;
        private double _lineHeight;
        private bool _themeHooked;

        public SourcePanel()
        {
            InitializeComponent();

            _lineHeight = PythonHighlighter.LineHeight(CodeBox.FontSize);
            Gutter.LineHeight = _lineHeight;
            Gutter.RenderTransform = _gutterShift;

            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _debounce.Tick += (s, e) => { _debounce.Stop(); Rebuild(); };

            DataContextChanged += (s, e) => AttachViewModel(e.NewValue as MainViewModel);
            Loaded += (s, e) =>
            {
                HookScrollViewer();
                ApplyFromViewModel(force: true);
                if (!_themeHooked)
                {
                    ThemeManager.ThemeChanged += OnThemeChanged;
                    _themeHooked = true;
                }
            };
            Unloaded += (s, e) =>
            {
                _debounce.Stop();
                ThemeManager.ThemeChanged -= OnThemeChanged;
                // 复位幂等守卫：不复位的话面板被移出再放回时，Loaded 里的 if (!_themeHooked) 会挡住重新订阅，
                // 从此切主题再也不会给这个面板配色（QA 手工重挂载复现过）。
                _themeHooked = false;
            };
        }

        private void OnThemeChanged()
        {
            Dispatcher.InvokeAsync(() => ApplyFromViewModel(force: true));
        }

        #region 与 ViewModel 同步

        private void AttachViewModel(MainViewModel vm)
        {
            if (_vm != null) _vm.PropertyChanged -= Vm_PropertyChanged;
            _vm = vm;
            if (_vm != null) _vm.PropertyChanged += Vm_PropertyChanged;
            if (IsLoaded) ApplyFromViewModel(force: true);
        }

        private void Vm_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(MainViewModel.SourceText)) return;
            if (_applying) return;
            string incoming = _vm?.SourceText ?? "";
            if (incoming == CurrentText()) return;
            ApplyFromViewModel(force: true);
        }

        /// <summary>把 ViewModel 里的源码装载进编辑器（切页、引擎刷新时）。</summary>
        private void ApplyFromViewModel(bool force)
        {
            if (_vm == null) return;
            string text = _vm.SourceText ?? "";
            if (!force && text == CurrentText()) return;

            var (line, col) = GetCaretLineCol();
            _applying = true;
            try
            {
                CodeBox.Document = PythonHighlighter.Build(text, CodeBox.FontSize,
                    CodeBox.FontFamily, ThemeManager.IsDark);
                _committed = text;
            }
            finally { _applying = false; }

            SetCaretLineCol(line, col);
            UpdateGutter(text);
            _undo.Clear();
            _redo.Clear();
        }

        private string CurrentText()
        {
            var range = new TextRange(CodeBox.Document.ContentStart, CodeBox.Document.ContentEnd);
            string t = (range.Text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            // 文档末尾总有一个空段落，只去掉一个换行，保留用户主动敲出来的空行
            if (t.EndsWith("\n", StringComparison.Ordinal)) t = t.Substring(0, t.Length - 1);
            return t;
        }

        #endregion

        #region 高亮重建

        private void CodeBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_applying) return;
            _debounce.Stop();
            _debounce.Start();
        }

        private void Rebuild()
        {
            string text = CurrentText();
            var (line, col) = GetCaretLineCol();
            double vOffset = _scrollViewer?.VerticalOffset ?? 0;

            if (!string.IsNullOrEmpty(_committed) && _committed != text)
            {
                _undo.Push(_committed);
                _redo.Clear();
                if (_undo.Count > 200)
                {
                    var kept = _undo.ToArray();        // 栈顶在前
                    _undo.Clear();
                    for (int i = kept.Length - 2; i >= 0; i--) _undo.Push(kept[i]);  // 丢掉最旧的一条
                }
            }

            _applying = true;
            try
            {
                CodeBox.Document = PythonHighlighter.Build(text, CodeBox.FontSize,
                    CodeBox.FontFamily, ThemeManager.IsDark);
                _committed = text;
            }
            finally { _applying = false; }

            SetCaretLineCol(line, col);
            UpdateGutter(text);
            if (_scrollViewer != null) _scrollViewer.ScrollToVerticalOffset(vOffset);

            // 同步给 ViewModel（Slide.Source 会在“应用”时写回引擎）
            if (_vm != null && _vm.SourceText != text)
            {
                _applying = true;
                _vm.SourceText = text;
                _applying = false;
            }
        }

        private void UpdateGutter(string text)
        {
            int lines = string.IsNullOrEmpty(text) ? 1 : text.Split('\n').Length;
            var sb = new System.Text.StringBuilder(lines * 4);
            for (int i = 1; i <= lines; i++)
            {
                if (i > 1) sb.Append('\n');
                sb.Append(i);
            }
            Gutter.Text = sb.ToString();
            Gutter.LineHeight = _lineHeight;
        }

        #endregion

        #region 光标 / 滚动

        private (int line, int col) GetCaretLineCol()
        {
            try
            {
                var caret = CodeBox.CaretPosition;
                var para = caret.Paragraph;
                if (para == null) return (0, 0);
                int line = 0;
                foreach (var block in CodeBox.Document.Blocks)
                {
                    if (ReferenceEquals(block, para)) break;
                    line++;
                }
                int col = new TextRange(para.ContentStart, caret).Text.Length;
                return (line, col);
            }
            catch { return (0, 0); }
        }

        private void SetCaretLineCol(int line, int col)
        {
            try
            {
                var paragraphs = CodeBox.Document.Blocks.OfType<Paragraph>().ToList();
                if (paragraphs.Count == 0) return;
                int li = Math.Max(0, Math.Min(line, paragraphs.Count - 1));
                var p = paragraphs[li];
                var pos = p.ContentStart.GetPositionAtOffset(Math.Max(0, col), LogicalDirection.Forward) ?? p.ContentEnd;
                CodeBox.CaretPosition = pos;
            }
            catch { }
        }

        private void HookScrollViewer()
        {
            _scrollViewer = FindChild<ScrollViewer>(CodeBox);
            if (_scrollViewer == null) return;
            _scrollViewer.ScrollChanged += (s, e) => { _gutterShift.Y = -e.VerticalOffset; };
        }

        private static T FindChild<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) return null;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T typed) return typed;
                var deeper = FindChild<T>(child);
                if (deeper != null) return deeper;
            }
            return null;
        }

        #endregion

        #region 键盘

        private void CodeBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            if (ctrl && e.Key == Key.Z && !shift) { Undo(); e.Handled = true; }
            else if ((ctrl && e.Key == Key.Y) || (ctrl && shift && e.Key == Key.Z)) { Redo(); e.Handled = true; }
            else if (e.Key == Key.Tab) { InsertIndent(); e.Handled = true; }
            else if (e.Key == Key.Enter) { AutoIndentOnEnter(); e.Handled = true; }
        }

        private void CodeBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            {
                Rebuild();
                _vm?.ApplySourceEditCommand.Execute(null);
                e.Handled = true;
            }
        }

        private void InsertIndent()
        {
            try
            {
                CodeBox.Selection.Text = "    ";
                CodeBox.CaretPosition = CodeBox.Selection.End;
            }
            catch { }
        }

        /// <summary>回车沿用上一行缩进；以 : 结尾自动多缩进一级。</summary>
        private void AutoIndentOnEnter()
        {
            try
            {
                var (line, _) = GetCaretLineCol();
                var paragraphs = CodeBox.Document.Blocks.OfType<Paragraph>().ToList();
                if (paragraphs.Count == 0) return;

                int li = Math.Min(line, paragraphs.Count - 1);
                string current = new TextRange(paragraphs[li].ContentStart, paragraphs[li].ContentEnd).Text;
                int indent = current.Length - current.TrimStart().Length;
                string extra = current.TrimEnd().EndsWith(":", StringComparison.Ordinal) ? "    " : "";
                string tail = new TextRange(CodeBox.CaretPosition, paragraphs[li].ContentEnd).Text;

                // 用段落分隔而不是插入 "\r\n"，保证“一行 = 一个 Paragraph”，行号才能对齐
                var pos = CodeBox.CaretPosition.InsertParagraphBreak();
                if (!string.IsNullOrWhiteSpace(tail)) return;      // 在行中间回车：不补缩进

                CodeBox.CaretPosition = pos;
                string pad = new string(' ', indent + extra.Length);
                if (pad.Length > 0)
                {
                    CodeBox.Selection.Text = pad;
                    CodeBox.CaretPosition = CodeBox.Selection.End;
                }
            }
            catch { }
        }

        private void Undo()
        {
            if (_undo.Count == 0) return;
            string prev = _undo.Pop();
            _redo.Push(CurrentText());
            ApplyText(prev);
        }

        private void Redo()
        {
            if (_redo.Count == 0) return;
            string next = _redo.Pop();
            _undo.Push(CurrentText());
            ApplyText(next);
        }

        private void ApplyText(string text)
        {
            _applying = true;
            try
            {
                CodeBox.Document = PythonHighlighter.Build(text, CodeBox.FontSize,
                    CodeBox.FontFamily, ThemeManager.IsDark);
                _committed = text;
            }
            finally { _applying = false; }

            UpdateGutter(text);
            CodeBox.CaretPosition = CodeBox.Document.ContentEnd;
            if (_vm != null) _vm.SourceText = text;
        }

        #endregion
    }
}
