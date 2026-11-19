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

            // 焦点在 TextBox / ComboBox 内时不劫持按键
            if (e.OriginalSource is TextBox || e.OriginalSource is ComboBox) return;

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
                    vm.SelectedObject = null;
                    e.Handled = true;
                    break;
                case Key.Enter:
                    if (vm.SelectedObject != null)
                    {
                        vm.ShowToast("Double-click / Enter to edit text — prototype");
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
