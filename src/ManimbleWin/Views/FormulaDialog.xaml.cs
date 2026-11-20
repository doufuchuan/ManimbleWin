using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ManimbleWin.Services;

namespace ManimbleWin.Views
{
    /// <summary>公式输入对话框：左侧写 LaTeX，下方实时用本机 TeX 排版预览。</summary>
    public partial class FormulaDialog : Window
    {
        private readonly DispatcherTimer _debounce;

        /// <summary>输入框的默认公式（原来写在 XAML 的 Text 上，会和 InitialExpression 两处争抢初值）。</summary>
        private const string DefaultExpression = "e^{i\\pi}+1=0";

        public string Expression { get; private set; }

        /// <summary>打开前赋值：用于编辑已有公式（而不是新建）。</summary>
        public string InitialExpression { get; set; }

        public FormulaDialog()
        {
            // 必须先建好 _debounce 再 InitializeComponent()：
            // TextBox 在加载期间就可能触发 TextChanged（XAML 里的 Text、样式里设的 Text、控件内部文本归一化都算），
            // 那时 _debounce 若还是 null，Input_TextChanged 第一行 Stop() 就是一场「打开即崩」的 NRE。
            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _debounce.Tick += async (s, e) =>
            {
                _debounce.Stop();
                await PreviewAsync();
            };

            InitializeComponent();

            Loaded += (s, e) =>
            {
                // 初值只有一个来源：调用方给的 InitialExpression，否则用默认示例公式
                Input.Text = string.IsNullOrEmpty(InitialExpression) ? DefaultExpression : InitialExpression;
                Input.Focus();
                Input.CaretIndex = Input.Text.Length;
                _debounce.Start();
            };
        }

        private async System.Threading.Tasks.Task PreviewAsync()
        {
            string tex = Input.Text?.Trim();
            if (string.IsNullOrEmpty(tex)) { PreviewImage.Source = null; Placeholder.Visibility = Visibility.Visible; Status.Text = ""; return; }

            if (!LatexService.IsAvailable)
            {
                Status.Text = "未检测到 LaTeX：插入后将以文字近似显示，可在依赖中心安装 TinyTeX。";
                PreviewImage.Source = null;
                Placeholder.Text = "缺少 LaTeX 环境";
                Placeholder.Visibility = Visibility.Visible;
                return;
            }

            try
            {
                Status.Text = "正在排版…";
                string path = await LatexService.RenderAsync(tex, 56, "#F5F5F5");
                if (!File.Exists(path)) { Status.Text = "渲染失败"; return; }
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                PreviewImage.Source = bmp;
                Placeholder.Visibility = Visibility.Collapsed;
                Status.Text = "";
            }
            catch (LatexException ex)
            {
                PreviewImage.Source = null;
                Placeholder.Text = "语法有误";
                Placeholder.Visibility = Visibility.Visible;
                Status.Text = ex.Message;
            }
            catch (Exception ex)
            {
                Status.Text = ex.Message;
            }
        }

        private void Input_TextChanged(object sender, TextChangedEventArgs e)
        {
            // 加载期间 TextBox 自己也可能触发 TextChanged，此时 _debounce 未必就绪（见构造函数注释）
            if (_debounce == null) return;
            _debounce.Stop();
            _debounce.Start();
        }

        private void Input_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                Insert_Click(sender, e);
                e.Handled = true;
            }
        }

        private void Snippet_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string snippet)
            {
                int at = Input.CaretIndex;
                string text = Input.Text ?? "";
                Input.Text = text.Insert(Math.Min(at, text.Length), snippet);
                Input.CaretIndex = Math.Min(at + snippet.Length, Input.Text.Length);
                Input.Focus();
            }
        }

        private async void Insert_Click(object sender, RoutedEventArgs e)
        {
            string tex = Input.Text?.Trim();
            if (string.IsNullOrEmpty(tex)) { Status.Text = "请先输入公式"; return; }

            // 插入前先验证能否排版，避免把错误公式写进代码
            if (LatexService.IsAvailable)
            {
                try
                {
                    Status.Text = "正在校验…";
                    await LatexService.RenderAsync(tex, 56, "#F5F5F5");
                }
                catch (LatexException ex)
                {
                    Status.Text = "无法排版：" + ex.Message;
                    return;
                }
            }

            Expression = tex;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
