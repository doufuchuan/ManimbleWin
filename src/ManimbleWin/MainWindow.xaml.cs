using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ManimbleWin.Helpers;
using ManimbleWin.Models;
using ManimbleWin.ViewModels;
using ManimbleWin.Views;

namespace ManimbleWin
{
    /// <summary>
    /// 主窗口：只做「View 层接线」——把 MainViewModel 的钩子换成真实对话框，
    /// 以及处理窗口关闭的三选一（保存 / 丢弃 / 取消）。业务逻辑一律留在 ViewModel。
    /// </summary>
    public partial class MainWindow : Window
    {
        private bool _hooksRegistered;
        private bool _closeConfirmed;
        private bool _closing;

        public MainWindow()
        {
            InitializeComponent();
            PreviewKeyDown += MainWindow_PreviewKeyDown;
            // 取色弹层用 StaysOpen=True，点弹层外面要自己关（PRD 说的"兜底"）。
            PreviewMouseDown += MainWindow_PreviewMouseDown;

            // DataContext 由 XAML 里的 <Window.DataContext> 建立，可能早于构造函数返回；
            // 两条路都试一次，用 _hooksRegistered 保证只注册一遍。
            DataContextChanged += (s, e) => RegisterHooks();
            Loaded += (s, e) => RegisterHooks();
            Closing += MainWindow_Closing;

            RegisterHooks();
        }

        /// <summary>把确认类钩子接到真实 UI 上（只注册一次）。</summary>
        private void RegisterHooks()
        {
            if (_hooksRegistered) return;
            if (!(DataContext is MainViewModel vm)) return;
            _hooksRegistered = true;

            // 未保存三选一：自绘 3 按钮，让「保存 / 丢弃 / 取消」的后果一目了然
            vm.ConfirmCloseHook = AskCloseChoice;

            // 保存冲突二选一：列出冲突文件，默认选「取消」（不写盘更安全）
            vm.ConfirmConflictHook = AskConflict;

            // 是/否确认（还原、丢弃草稿等）
            vm.ConfirmOkCancelHook = AskOkCancel;

            // 纯信息提示：走应用内横条，不用会阻塞的模态框
            vm.InfoHook = ShowInfoBar;

            // 新建演示文稿：VM 已经准备好选项，窗口只负责收集
            vm.NewDeckDialogHook = dlg =>
            {
                var window = new NewDeckDialog { DataContext = dlg, Owner = this };
                return Task.FromResult(window.ShowDialog() == true);
            };

            // 导出选项：确认后返回填好的 ExportRequest，取消返回 null
            vm.ExportDialogHook = (kind, seed) =>
            {
                var options = new ExportDialogViewModel(kind, seed);
                var window = new ExportDialog { DataContext = options, Owner = this };
                return Task.FromResult(window.ShowDialog() == true ? options.ToRequest() : null);
            };
        }

        #region 确认对话框

        /// <summary>未保存三选一（顺序对齐原版 Save / Discard / Cancel）。</summary>
        private CloseChoice AskCloseChoice(string actionText)
        {
            string body =
                "你正在编辑一份私有工作副本，里面还有没写回原始文件的改动。\n\n"
                + $"在{actionText}之前要把它们保存到原始文件吗？\n\n"
                + "「保存到原始文件」把改动写回原目录（冲突时会再问一次）\n"
                + "「丢弃改动」副本里的改动全部作废\n"
                + "「取消」留在当前演示文稿继续编辑";

            int index = ChoiceDialog.Ask(this, "有未保存的改动", body,
                new[] { "保存到原始文件", "丢弃改动", "取消" }, defaultIndex: 0, cancelIndex: 2);
            switch (index)
            {
                case 0: return CloseChoice.Save;
                case 1: return CloseChoice.Discard;
                default: return CloseChoice.Cancel;
            }
        }

        /// <summary>保存冲突二选一：覆盖原始文件 / 取消（一个文件都不写）。</summary>
        private ConflictChoice AskConflict(ConflictInfo info)
        {
            var files = info?.Files ?? new System.Collections.Generic.List<string>();
            string list = string.Join("\n", files.Take(12));
            if (files.Count > 12) list += $"\n…等 {files.Count} 个文件";

            string body =
                "原始目录里的这些文件在你打开之后又被改动过（同步盘或别人编辑）：\n\n"
                + list
                + "\n\n覆盖它们吗？选「取消」这次一个文件都不会写。";

            int index = ChoiceDialog.Ask(this, "原始文件已被外部改动", body,
                new[] { "覆盖原始文件", "取消" }, defaultIndex: 1, cancelIndex: 1);
            return index == 0 ? ConflictChoice.Overwrite : ConflictChoice.Cancel;
        }

        private bool AskOkCancel(string title, string body)
        {
            return ChoiceDialog.Ask(this, title, body, new[] { "确定", "取消" }, defaultIndex: 0, cancelIndex: 1) == 0;
        }

        /// <summary>InfoHook 的落点：应用内横条（标题 + 正文 + 知道了）。</summary>
        private void ShowInfoBar(string title, string body)
        {
            InfoTitle.Text = string.IsNullOrEmpty(title) ? "提示" : title;
            InfoBody.Text = body ?? "";
            InfoBody.Visibility = string.IsNullOrWhiteSpace(body) ? Visibility.Collapsed : Visibility.Visible;
            InfoBar.Visibility = Visibility.Visible;
        }

        private void InfoBarClose_Click(object sender, RoutedEventArgs e)
        {
            InfoBar.Visibility = Visibility.Collapsed;
        }

        #endregion

        #region 关窗

        /// <summary>
        /// 关窗：先走未保存三选一，通过后再关引擎并删副本。
        /// <para>用 <c>e.Cancel = true</c> + 异步确认再 Close() 的标准写法：
        /// 关窗必须先取消，等用户选完再真正关。异常必须自己吞掉 —— 这是 <c>async void</c>，
        /// 漏出去会直接崩进程。</para>
        /// </summary>
        private async void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_closeConfirmed) return;

            e.Cancel = true;

            // 确认还挂在 await 上时又来一次 Close（连点 X / 任务栏关闭）：直接忽略，
            // 否则会同时走两遍清理，还可能互相踩踏
            if (_closing) return;

            var vm = DataContext as MainViewModel;
            if (vm == null)
            {
                FinishClose();
                return;
            }

            _closing = true;
            try
            {
                // 菜单里的「关闭窗口」已经确认过并清理完（CloseApproved=true），这里不再重复问
                if (!vm.CloseApproved && !await vm.ConfirmCloseAsync("关闭窗口"))
                {
                    _closing = false;
                    return;                     // 用户选了取消：留在当前演示文稿
                }

                await vm.CloseDeckAsync();      // 停引擎 → 删副本（幂等）
                FinishClose();
            }
            catch (Exception ex)
            {
                _closing = false;
                MessageBox.Show("关闭时出错：" + ex.Message, "关闭窗口",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                // 出错不强行关窗：让用户能看到提示，再自己决定
            }
        }

        /// <summary>
        /// 真正关窗。<b>不能在 Closing 的调用栈里直接 Close()</b>（WPF 会在关窗过程中重入关窗），
        /// 所以排到 Dispatcher 队列里、等这次 Closing 事件走完再关。
        /// </summary>
        private void FinishClose()
        {
            _closeConfirmed = true;
            Dispatcher.BeginInvoke(new Action(Close), System.Windows.Threading.DispatcherPriority.Normal);
        }

        #endregion

        /// <summary>
        /// 点窗口别处关闭取色弹层。
        /// <para>判定只看两件事：点在弹层内容里，还是点在触发它的那个按钮上 —— 后者交给
        /// <c>FormatPickerToggleCommand</c> 自己取反，这里再关一次会让"再点按钮关闭"变成"关了又开"。</para>
        /// <para>弹层内容在独立的可视树里（PopupRoot），所以"是否点在弹层里"是**从点击源往上找**
        /// 弹层的 Child，而不是从弹层往下找。</para>
        /// </summary>
        private void MainWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!(DataContext is MainViewModel vm)) return;
            if (!vm.IsHighlightPickerOpen && !vm.IsColorPickerOpen) return;

            var src = e.OriginalSource as DependencyObject;
            if (IsWithin(src, HighlightPickerPopup.Child as DependencyObject)) return;
            if (IsWithin(src, ColorPickerPopup.Child as DependencyObject)) return;
            if (IsWithin(src, HighlightButton)) return;
            if (IsWithin(src, ColorButton)) return;

            vm.CloseColorPickers();
        }

        /// <summary>从 <paramref name="node"/> 沿可视树（必要时退化到逻辑树）往上找 <paramref name="root"/>。</summary>
        private static bool IsWithin(DependencyObject node, DependencyObject root)
        {
            if (root == null) return false;
            while (node != null)
            {
                if (ReferenceEquals(node, root)) return true;
                node = node is System.Windows.Media.Visual || node is System.Windows.Media.Media3D.Visual3D
                    ? System.Windows.Media.VisualTreeHelper.GetParent(node)
                    : LogicalTreeHelper.GetParent(node);
            }
            return false;
        }

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!(DataContext is MainViewModel vm)) return;

            // Ctrl+Enter：在检视器里改公式时立即重新排版（焦点在输入框里也要生效）
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                vm.RerenderSelectedFormula();
                e.Handled = true;
                return;
            }

            // 查找 / 替换要在任何焦点位置都能叫出来（包括正在代码框里编辑时）
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
            {
                vm.OpenFind(false);
                e.Handled = true;
                return;
            }
            if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt) && e.Key == Key.F)
            {
                vm.OpenFind(true);
                e.Handled = true;
                return;
            }

            // 焦点在文本输入控件内时不劫持按键（含代码编辑器的 RichTextBox）
            if (e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase) return;

            var mods = Keyboard.Modifiers;
            if (mods == ModifierKeys.Control)
            {
                switch (e.Key)
                {
                    case Key.C: vm.MenuCommand.Execute("Copy"); e.Handled = true; return;
                    case Key.X: vm.MenuCommand.Execute("Cut"); e.Handled = true; return;
                    case Key.V: vm.MenuCommand.Execute("Paste"); e.Handled = true; return;
                    case Key.A: vm.MenuCommand.Execute("SelectAll"); e.Handled = true; return;
                    case Key.G: vm.MenuCommand.Execute("Group"); e.Handled = true; return;
                    case Key.L: vm.MenuCommand.Execute("AlignLeft"); e.Handled = true; return;
                    case Key.E: vm.MenuCommand.Execute("AlignCenter"); e.Handled = true; return;
                    case Key.R: vm.MenuCommand.Execute("AlignRight"); e.Handled = true; return;
                    // Ctrl+B/I/U 走格式通道（真正的引擎 edit，不再是本地翻字段）。
                    // 这几条必须留在上面那条 TextBoxBase 守卫之后：代码窗格是 RichTextBox，
                    // 在它里面按 Ctrl+B 应该执行编辑器自己的加粗，不能被窗口抢走。
                    case Key.B: vm.FormatToggleCommand.Execute("Bold"); e.Handled = true; return;
                    case Key.I: vm.FormatToggleCommand.Execute("Italic"); e.Handled = true; return;
                    case Key.U: vm.FormatToggleCommand.Execute("Underline"); e.Handled = true; return;
                }
            }
            if (mods == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                switch (e.Key)
                {
                    case Key.G: vm.MenuCommand.Execute("Ungroup"); e.Handled = true; return;
                    case Key.F: vm.MenuCommand.Execute("Front"); e.Handled = true; return;
                    case Key.B: vm.MenuCommand.Execute("Back"); e.Handled = true; return;
                    case Key.OemPeriod: vm.FormatFontStepCommand.Execute("up"); e.Handled = true; return;
                    case Key.OemComma: vm.FormatFontStepCommand.Execute("down"); e.Handled = true; return;
                }
            }

            switch (e.Key)
            {
                case Key.Delete:
                    if (vm.HasSelection)
                    {
                        vm.MenuCommand.Execute("DeleteSelected");
                        e.Handled = true;
                    }
                    break;
                case Key.Escape:
                    // 取色弹层先关（它的优先级高于清空选区，否则用户按 Esc 会连带丢掉选中）
                    if (vm.IsHighlightPickerOpen || vm.IsColorPickerOpen) { vm.CloseColorPickers(); e.Handled = true; break; }
                    if (vm.Find != null && vm.Find.IsOpen) { vm.Find.Close(); e.Handled = true; break; }
                    if (vm.TerminalVisible) { vm.TerminalVisible = false; e.Handled = true; break; }
                    if (InfoBar.Visibility == Visibility.Visible) { InfoBar.Visibility = Visibility.Collapsed; e.Handled = true; break; }
                    vm.ClearSelection();
                    e.Handled = true;
                    break;
                case Key.Oem3:
                    // Ctrl + ` 打开/关闭终端
                    if (Keyboard.Modifiers == ModifierKeys.Control)
                    {
                        if (vm.TerminalVisible) vm.TerminalVisible = false;
                        else vm.ShowTerminalPanel();
                        e.Handled = true;
                    }
                    break;
                case Key.Enter:
                    if (vm.SelectedObject != null)
                    {
                        if (vm.SelectedObject.Kind == ManimbleWin.Models.ObjectKind.Tex)
                            vm.EditSelectedFormula();
                        else
                            vm.ShowToast("双击对象可重新输入内容");
                        e.Handled = true;
                    }
                    break;
                case Key.OemPlus:
                    if (Keyboard.Modifiers == ModifierKeys.Control) { vm.ToolbarCommand.Execute("ZoomIn"); e.Handled = true; }
                    break;
                case Key.OemMinus:
                    if (Keyboard.Modifiers == ModifierKeys.Control) { vm.ToolbarCommand.Execute("ZoomOut"); e.Handled = true; }
                    break;
                case Key.D0:
                    if (Keyboard.Modifiers == ModifierKeys.Control) { vm.ToolbarCommand.Execute("ZoomFit"); e.Handled = true; }
                    break;
            }
        }

        /// <summary>
        /// 工具栏的主题下拉选了别的主题：不直接改 deck，而是打开确认面板先预览（取消时面板会把选择拨回去）。
        /// 放在这里而不是 MainViewModel 的属性 setter 里，是因为打开模态框必须在交互回调里，
        /// 不能在数据绑定赋值过程中做（会在绑定管线里重入对话框）。
        /// </summary>
        private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0) return;
            if (DataContext is MainViewModel vm && e.AddedItems[0] is ManimbleWin.Models.DeckTheme theme)
                vm.SelectThemeFromUi(theme);
        }

        #region 格式栏的提交时机

        // 字体 / 字号是「可编辑 ComboBox + 回车/失焦提交」：
        // 不能在 Text 绑定上加 UpdateSourceTrigger=PropertyChanged —— 那样每敲一个字符就会写一次源码，
        // 中间态（"1"、"12"）会被引擎真的落进 font_size。

        private void FontFamilyBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            CommitFontFamily();
            e.Handled = true;
        }

        private void FontFamilyBox_LostFocus(object sender, RoutedEventArgs e) => CommitFontFamily();

        private void CommitFontFamily()
        {
            if (DataContext is MainViewModel vm && FontFamilyBox != null)
                vm.FormatFontFamilyCommand.Execute(FontFamilyBox.Text);
        }

        private void FontSizeBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            CommitFontSize();
            e.Handled = true;
        }

        private void FontSizeBox_LostFocus(object sender, RoutedEventArgs e) => CommitFontSize();

        private void CommitFontSize()
        {
            if (DataContext is MainViewModel vm && FontSizeBox != null)
                vm.FormatFontSizeCommand.Execute(FontSizeBox.Text);
        }

        /// <summary>
        /// 行距 / 分栏下拉：选中项是 OneWay 绑定的回填值，所以程序化更新也会触发 SelectionChanged。
        /// 用「选中的就是当前值」做去重，避免回填时又发一次格式请求（否则会一直互相触发）。
        /// </summary>
        private void LineSpacingBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(DataContext is MainViewModel vm)) return;
            if (e.AddedItems.Count == 0) return;
            string picked = e.AddedItems[0] as string;
            if (string.IsNullOrEmpty(picked) || picked == vm.FormatLineSpacingText) return;
            vm.FormatLineSpacingCommand.Execute(picked);
        }

        private void ColumnsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(DataContext is MainViewModel vm)) return;
            if (e.AddedItems.Count == 0) return;
            string picked = e.AddedItems[0] as string;
            if (string.IsNullOrEmpty(picked) || picked == vm.FormatColumnsText) return;
            vm.FormatColumnsCommand.Execute(picked);
        }

        #endregion
    }
}
