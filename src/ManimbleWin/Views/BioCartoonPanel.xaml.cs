using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ManimbleWin.ViewModels;

namespace ManimbleWin.Views
{
    public partial class BioCartoonPanel : UserControl
    {
        public BioCartoonPanel()
        {
            InitializeComponent();
            Loaded += (s, e) =>
            {
                // 打开即加载：CLI 可用时展示目录图标，不可用时展示内置示例，保证面板不会是空壳。
                if (DataContext is BioCartoonViewModel vm && vm.Items.Count == 0)
                    vm.SearchCommand.Execute(null);
            };
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && DataContext is BioCartoonViewModel vm)
            {
                vm.SearchCommand.Execute(null);
                e.Handled = true;
            }
        }
    }
}
