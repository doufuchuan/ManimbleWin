using System;
using System.IO;
using System.Windows;
using ManimbleWin.ViewModels;

namespace ManimbleWin.Views
{
    /// <summary>
    /// 「文件 ▸ 导出为 ▸ …」的选项对话框。
    /// 选项与校验在 <see cref="ExportDialogViewModel"/>；这里只负责弹 <c>SaveFileDialog</c>。
    /// </summary>
    public partial class ExportDialog : Window
    {
        private ExportDialogViewModel VM => DataContext as ExportDialogViewModel;

        public ExportDialog()
        {
            InitializeComponent();
        }

        /// <summary>选输出文件（带该格式的默认扩展名与过滤器）。</summary>
        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            if (VM == null) return;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出为 " + VM.KindLabel,
                Filter = VM.FileFilter,
                DefaultExt = VM.DefaultExtension,
                AddExtension = true,
                OverwritePrompt = true,
            };
            string current = (VM.OutPath ?? "").Trim();
            if (current.Length > 0)
            {
                try
                {
                    dlg.InitialDirectory = Path.GetDirectoryName(Path.GetFullPath(current));
                    dlg.FileName = Path.GetFileName(current);
                }
                catch (Exception)
                {
                    // 路径里带非法字符时忽略即可，用户会用浏览按钮重选
                }
            }
            if (dlg.ShowDialog() == true) VM.OutPath = dlg.FileName;
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            if (VM == null) { DialogResult = false; return; }
            if (!VM.Validate()) return;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
