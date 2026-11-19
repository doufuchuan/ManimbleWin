using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ManimbleWin.ViewModels;

namespace ManimbleWin.Views
{
    public partial class SourcePanel : UserControl
    {
        public SourcePanel()
        {
            InitializeComponent();
        }

        private void Line_Click(object sender, MouseButtonEventArgs e)
        {
            // 预留：点击代码行时跳转到对应对象（真实引擎会通过 loc 关联）
        }
    }
}
