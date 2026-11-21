using System.Windows.Controls;

namespace ManimbleWin.Views
{
    /// <summary>
    /// 右栏反馈面板。所有交互都由 <c>FeedbackViewModel</c> 的命令接走
    /// （跳页 / 删单条 / 全部删除 / 开始批注 / 打开文件夹 / 重新读取），
    /// 所以这里除了 <see cref="InitializeComponent"/> 没有别的逻辑——
    /// 面板本身不碰文件、不碰 Store（只读闸门在 FeedbackStore 里，一处判定）。
    /// </summary>
    public partial class FeedbackPanel : UserControl
    {
        public FeedbackPanel()
        {
            InitializeComponent();
        }
    }
}
