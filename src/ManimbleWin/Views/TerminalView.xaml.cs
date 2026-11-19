using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ManimbleWin.ViewModels;

namespace ManimbleWin.Views
{
    public partial class TerminalView : UserControl
    {
        public TerminalView()
        {
            InitializeComponent();
            IsVisibleChanged += (s, e) =>
            {
                if (IsVisible) InputBox.Focus();
            };
        }

        private void InputBox_KeyDown(object sender, KeyEventArgs e)
        {
            var vm = DataContext as MainViewModel;
            if (vm == null) return;

            if (e.Key == Key.Enter)
            {
                vm.Terminal.SubmitCommand.Execute(null);
                OutputScroll.ScrollToEnd();
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                vm.Terminal.Recall(true);
                InputBox.CaretIndex = InputBox.Text.Length;
                e.Handled = true;
            }
            else if (e.Key == Key.Down)
            {
                vm.Terminal.Recall(false);
                InputBox.CaretIndex = InputBox.Text.Length;
                e.Handled = true;
            }
        }
    }
}
