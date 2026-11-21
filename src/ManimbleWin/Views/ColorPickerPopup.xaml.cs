using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ManimbleWin.ViewModels;

namespace ManimbleWin.Views
{
    /// <summary>
    /// 取色弹层的内容控件（高亮 / 字色共用一套）。
    /// <para>为什么把弹层做成独立 UserControl 而不是塞进 MainWindow.xaml：
    /// 8 个色块 + 「无高亮」 + 自定义输入行有六十多行 XAML，塞进主窗口会把它推到六百行以上；
    /// 而且两块弹层共用同一个控件，改色板只改一处。</para>
    /// <para>关闭策略见架构 §5.3：Popup 用 <c>StaysOpen=True</c>，Esc / 再点按钮 / 点外部三路自管。</para>
    /// </summary>
    public partial class ColorPickerPopup : UserControl
    {
        public ColorPickerPopup()
        {
            InitializeComponent();
            // 弹层打开时把键盘焦点接过来：否则方向键/Tab 还留在画布上，色块选不到。
            Loaded += (s, e) => Focus();
        }

        /// <summary>"Highlight" 或 "Color"。只影响「无高亮」那一行的文案与显隐。</summary>
        public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
            nameof(Mode), typeof(string), typeof(ColorPickerPopup),
            new PropertyMetadata("Highlight", OnModeChanged));

        public string Mode
        {
            get => (string)GetValue(ModeProperty);
            set => SetValue(ModeProperty, value);
        }

        private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((ColorPickerPopup)d).ApplyMode();

        private void ApplyMode()
        {
            bool highlight = !string.Equals(Mode, "Color", StringComparison.OrdinalIgnoreCase);
            RemoveButton.Content = highlight ? "无高亮" : "无颜色";
            RemoveButton.Visibility = highlight ? Visibility.Visible : Visibility.Collapsed;
        }

        private static MainViewModel VmOf(DependencyObject d) => (d as FrameworkElement)?.DataContext as MainViewModel;

        /// <summary>Esc 关弹层并让焦点回到画布（焦点回来之后 Ctrl+B 之类的快捷键才继续生效）。</summary>
        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            VmOf(this)?.CloseColorPickers();
            MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            e.Handled = true;
        }

        /// <summary>自定义色输入行：Enter 提交，Esc 关弹层。校验失败的红字由 ViewModel 写。</summary>
        private void HexBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var vm = VmOf(this);
            if (vm == null) return;
            if (e.Key == Key.Enter)
            {
                vm.FormatCustomColorCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                vm.CloseColorPickers();
                e.Handled = true;
            }
        }
    }
}
