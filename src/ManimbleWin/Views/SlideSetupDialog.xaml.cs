using System;
using System.Windows;
using ManimbleWin.ViewModels;

namespace ManimbleWin.Views
{
    /// <summary>背景与页码设置面板。写入全部经引擎，引擎侧每次写入都是一步撤销。</summary>
    public partial class SlideSetupDialog : Window
    {
        private SlideSetupViewModel VM => DataContext as SlideSetupViewModel;

        public SlideSetupDialog()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (VM != null) await VM.InitializeAsync();
        }
    }
}
