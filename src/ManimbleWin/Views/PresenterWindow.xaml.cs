using System.Windows;
using System.Windows.Input;
using ManimbleWin.ViewModels;

namespace ManimbleWin.Views
{
    public partial class PresenterWindow : Window
    {
        private readonly PresenterViewModel _vm;

        public PresenterWindow(PresenterViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            DataContext = vm;

            Loaded += async (s, e) =>
            {
                await _vm.LoadAsync();
                Keyboard.Focus(this);
            };
            Closing += (s, e) => _vm.OnClosed();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.Right) { _vm.NextCommand.Execute(null); e.Handled = true; }
            else if (e.Key == Key.Left) { _vm.PrevCommand.Execute(null); e.Handled = true; }
        }
    }
}
