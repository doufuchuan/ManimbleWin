using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ManimbleWin.Helpers;
using ManimbleWin.ViewModels;

namespace ManimbleWin
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            PreviewKeyDown += MainWindow_PreviewKeyDown;
        }

        /// <summary>点「+」弹出 CMD / PowerShell 选择（ContextMenu 默认只在右键弹出）。</summary>
        private void NewTabButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.ContextMenu == null) return;
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            btn.ContextMenu.IsOpen = true;
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
                }
            }
            if (mods == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                switch (e.Key)
                {
                    case Key.G: vm.MenuCommand.Execute("Ungroup"); e.Handled = true; return;
                    case Key.F: vm.MenuCommand.Execute("Front"); e.Handled = true; return;
                    case Key.B: vm.MenuCommand.Execute("Back"); e.Handled = true; return;
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
                    if (vm.Find != null && vm.Find.IsOpen) { vm.Find.Close(); e.Handled = true; break; }
                    if (vm.TerminalVisible) { vm.TerminalVisible = false; e.Handled = true; break; }
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
    }
}
