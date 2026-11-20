using System.Windows;
using System.Windows.Controls;
using ManimbleWin.ViewModels;

namespace ManimbleWin.Views
{
    public partial class DrawerPanel : UserControl
    {
        public DrawerPanel()
        {
            InitializeComponent();
        }

        /// <summary>备注输入框失焦时立刻提交，不必再等 800ms 防抖。</summary>
        private void Notes_LostFocus(object sender, RoutedEventArgs e)
        {
            (DataContext as MainViewModel)?.FlushNotes();
        }
    }
}
