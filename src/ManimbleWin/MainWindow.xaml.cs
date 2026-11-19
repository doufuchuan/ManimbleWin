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

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!(DataContext is MainViewModel vm)) return;

            // 焦点在文本输入控件内时不劫持按键（含代码编辑器的 RichTextBox）
            if (e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase) return;

            switch (e.Key)
            {
                case Key.Delete:
                    if (vm.SelectedObject != null)
                    {
                        vm.MenuCommand.Execute("DeleteSelected");
                        e.Handled = true;
                    }
                    break;
                case Key.Escape:
                    if (vm.TerminalVisible) { vm.TerminalVisible = false; e.Handled = true; break; }
                    vm.SelectedObject = null;
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
                        vm.ShowToast("双击或按 Enter 编辑文本 — 原型模式");
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
    }
}
