using System.Windows.Controls;

namespace ManimbleWin.Views
{
    /// <summary>
    /// LaTeX 缺包 / 占位图提示条。纯绑定控件，逻辑都在 <c>MainViewModel</c>（T04）里，
    /// 这里不需要任何点击处理 —— 四个按钮都由命令驱动。
    /// </summary>
    public partial class LatexNoticeBar : UserControl
    {
        public LatexNoticeBar()
        {
            InitializeComponent();
        }
    }
}
