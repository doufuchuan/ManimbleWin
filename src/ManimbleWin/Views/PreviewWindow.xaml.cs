using System.Windows;
using System.Windows.Input;
using ManimbleWin.ViewModels;

namespace ManimbleWin.Views
{
    public partial class PreviewWindow : Window
    {
        private readonly PreviewViewModel _vm;

        public PreviewWindow(PreviewViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            DataContext = vm;
            Closing += (s, e) => vm.Stop();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.Space) { _vm.PlayPauseCommand.Execute(null); e.Handled = true; }
            else if (e.Key == Key.Right) { _vm.NextCommand.Execute(null); e.Handled = true; }
            else if (e.Key == Key.Left) { _vm.PrevCommand.Execute(null); e.Handled = true; }
        }
    }
}
