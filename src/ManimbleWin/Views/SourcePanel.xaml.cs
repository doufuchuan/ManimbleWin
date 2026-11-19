using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ManimbleWin.ViewModels;

namespace ManimbleWin.Views
{
    public partial class SourcePanel : UserControl
    {
        public SourcePanel()
        {
            InitializeComponent();
        }

        private void CodeBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            {
                var vm = DataContext as MainViewModel;
                vm?.ApplySourceEditCommand.Execute(null);
                e.Handled = true;
            }
        }
    }
}
