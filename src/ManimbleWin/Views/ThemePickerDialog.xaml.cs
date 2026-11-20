using System;
using System.Windows;
using ManimbleWin.ViewModels;

namespace ManimbleWin.Views
{
    /// <summary>
    /// 「格式 ▸ 主题…」的确认面板。逻辑全在 <see cref="ThemePickerViewModel"/>，
    /// 这里只负责开场时拉一次当前主题 + 预览，以及把「应用成功」翻译成 DialogResult。
    /// </summary>
    public partial class ThemePickerDialog : Window
    {
        private ThemePickerViewModel VM => DataContext as ThemePickerViewModel;

        public ThemePickerDialog()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is ThemePickerViewModel old) old.CloseRequested -= OnCloseRequested;
            if (e.NewValue is ThemePickerViewModel vm) vm.CloseRequested += OnCloseRequested;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (VM == null) return;
            await VM.InitializeAsync();
        }

        /// <summary>VM 决定关窗：applied=true 时 DialogResult 为 true，调用方据此刷新画布。</summary>
        private void OnCloseRequested(bool applied)
        {
            try
            {
                DialogResult = applied;
            }
            catch (InvalidOperationException)
            {
                // 窗口还没以 ShowDialog 显示时不能设 DialogResult（例如测试里直接 new）
                Close();
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            try { DialogResult = false; }
            catch (InvalidOperationException) { Close(); }
        }
    }
}
