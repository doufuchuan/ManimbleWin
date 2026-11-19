using System.Windows;
using ManimbleWin.ViewModels;

namespace ManimbleWin.Views
{
    public partial class SetupWindow : Window
    {
        public SetupWindow(SetupViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
        }
    }
}
