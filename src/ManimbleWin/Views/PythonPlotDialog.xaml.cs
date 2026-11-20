using System.Windows;

namespace ManimbleWin.Views
{
    public partial class PythonPlotDialog : Window
    {
        public PythonPlotDialog()
        {
            InitializeComponent();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
