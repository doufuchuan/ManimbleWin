using System.Windows;
using ManimbleWin.ViewModels;

namespace ManimbleWin.Views
{
    /// <summary>
    /// 「文件 ▸ 新建演示文稿…」的选项对话框。
    /// 逻辑全在 <see cref="NewDeckDialogViewModel"/>；这里只负责选文件夹与把「校验通过」翻译成 DialogResult。
    /// </summary>
    public partial class NewDeckDialog : Window
    {
        private NewDeckDialogViewModel VM => DataContext as NewDeckDialogViewModel;

        public NewDeckDialog()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // 名称是用户最可能要改的东西，直接把焦点放进去并全选，改起来一步到位
            NameBox.Focus();
            NameBox.SelectAll();
        }

        /// <summary>选父目录（只改输入，不写盘）。</summary>
        private void PickParent_Click(object sender, RoutedEventArgs e)
        {
            if (VM == null) return;
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "新建演示文稿：选择保存位置",
                Multiselect = false,
            };
            if (!string.IsNullOrEmpty(VM.ParentDir) && System.IO.Directory.Exists(VM.ParentDir))
                dlg.InitialDirectory = VM.ParentDir;
            if (dlg.ShowDialog() == true) VM.ParentDir = dlg.FolderName;
        }

        private void Create_Click(object sender, RoutedEventArgs e)
        {
            if (VM == null) { DialogResult = false; return; }
            // 生成文件在 MainViewModel 里做：这里只保证输入合法
            if (!VM.Validate()) return;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
