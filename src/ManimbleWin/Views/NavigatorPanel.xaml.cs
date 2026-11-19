using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ManimbleWin.Models;
using ManimbleWin.ViewModels;

namespace ManimbleWin.Views
{
    public partial class NavigatorPanel : UserControl
    {
        public NavigatorPanel()
        {
            InitializeComponent();
        }

        private void SlideThumb_Click(object sender, MouseButtonEventArgs e)
        {
            var border = sender as Border;
            if (border?.Tag is Slide slide && DataContext is MainViewModel vm)
                vm.SelectSlideCommand.Execute(slide);
        }

        private void BuildThumb_Click(object sender, MouseButtonEventArgs e)
        {
            var border = sender as Border;
            if (border?.Tag is Build build && DataContext is MainViewModel vm)
            {
                // 找到这个 build 所属的 Slide：沿可视树向上找到 ListBoxItem，其 DataContext 即 Slide
                var slide = FindAncestorDataContext<Slide>(border);
                if (slide != null)
                    vm.SelectBuildCommand.Execute(new BuildTag { Slide = slide, Index = slide.Builds.IndexOf(build) });
            }
        }

        private static T FindAncestorDataContext<T>(DependencyObject obj) where T : class
        {
            DependencyObject cur = obj;
            while (cur != null)
            {
                if (cur is FrameworkElement fe && fe.DataContext is T t) return t;
                cur = VisualTreeHelper.GetParent(cur);
            }
            return null;
        }
    }
}
