using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace ManimbleWin.Views
{
    /// <summary>
    /// 2~3 选一的确认框（未保存三选一 / 保存冲突二选一）。
    ///
    /// <para>按钮文案由调用方给，所以「保存 / 丢弃 / 取消」这类会丢数据的选项能自己说明后果，
    /// 比 MessageBox 的「是 / 否 / 取消」安全得多。</para>
    /// </summary>
    public partial class ChoiceDialog : Window
    {
        private readonly int _cancelIndex;

        /// <summary>用户点中的按钮下标；取消/关窗时等于构造时给的 <c>cancelIndex</c>（没给就是 -1）。</summary>
        public int SelectedIndex { get; private set; } = -1;

        /// <param name="title">窗口标题（也显示在正文上方）。</param>
        /// <param name="body">正文（可多行，会自动换行 + 滚动）。</param>
        /// <param name="buttons">按钮文案，从左到右。</param>
        /// <param name="defaultIndex">回车默认按钮（用强调色）。</param>
        /// <param name="cancelIndex">按 Esc / 关窗时返回的下标。</param>
        public ChoiceDialog(string title, string body, string[] buttons, int defaultIndex = 0, int cancelIndex = -1)
        {
            InitializeComponent();
            _cancelIndex = cancelIndex;
            SelectedIndex = cancelIndex;

            Title = string.IsNullOrEmpty(title) ? "确认" : title;
            TitleText.Text = Title;
            BodyText.Text = body ?? "";

            string[] labels = buttons ?? Array.Empty<string>();
            for (int i = 0; i < labels.Length; i++)
            {
                int index = i;
                var button = new Button
                {
                    Content = labels[i],
                    Height = 28,
                    MinWidth = 88,
                    Padding = new Thickness(16, 3, 16, 3),
                    Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0),
                    FontSize = 12,
                };
                string styleKey = i == defaultIndex ? "AccentButton" : "ToolTextButton";
                if (TryFindResource(styleKey) is Style style) button.Style = style;
                if (i == defaultIndex) button.IsDefault = true;
                if (i == cancelIndex) button.IsCancel = true;
                button.Click += (s, e) =>
                {
                    SelectedIndex = index;
                    DialogResult = true;
                };
                Buttons.Children.Add(button);
            }
        }

        /// <summary>按 Esc 或点右上角关闭时也把「取消」语义带回去。</summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            if (DialogResult != true) SelectedIndex = _cancelIndex;
            base.OnClosing(e);
        }

        /// <summary>以 owner 为父窗口弹一次，返回点中的按钮下标（取消返回 <paramref name="cancelIndex"/>）。</summary>
        public static int Ask(Window owner, string title, string body, string[] buttons,
                              int defaultIndex = 0, int cancelIndex = -1)
        {
            var dlg = new ChoiceDialog(title, body, buttons, defaultIndex, cancelIndex);
            if (owner != null && !ReferenceEquals(owner, dlg) && owner.IsVisible) dlg.Owner = owner;
            dlg.ShowDialog();
            return dlg.SelectedIndex;
        }
    }
}
