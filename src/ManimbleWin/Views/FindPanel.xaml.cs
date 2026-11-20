using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ManimbleWin.ViewModels;

namespace ManimbleWin.Views
{
    /// <summary>
    /// 查找替换浮层。
    /// 视图只负责「焦点」与「键盘语义」（回车下一个、Shift+回车上一个、Esc 关闭），
    /// 搜索 / 替换逻辑全部在 FindViewModel 里，避免两处各写一份规则。
    /// </summary>
    public partial class FindPanel : UserControl
    {
        private FindViewModel _find;

        public FindPanel()
        {
            InitializeComponent();
            DataContextChanged += (s, e) => Attach(e.NewValue as MainViewModel);
            Unloaded += (s, e) => Detach();
        }

        private void Attach(MainViewModel vm)
        {
            Detach();
            _find = vm?.Find;
            if (_find == null) return;
            _find.FocusRequested += FocusSearch;
        }

        private void Detach()
        {
            if (_find == null) return;
            _find.FocusRequested -= FocusSearch;
            _find = null;
        }

        /// <summary>浮层刚打开时把焦点放进搜索框，并全选已有内容，方便直接覆盖输入。</summary>
        private void FocusSearch()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                QueryBox.Focus();
                QueryBox.SelectAll();
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        /// <summary>回车 / Shift+回车切换命中；Esc 关闭浮层（与文档里的快捷键一致）。</summary>
        private void QueryBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (_find == null) return;
            switch (e.Key)
            {
                case Key.Enter:
                    if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) _find.PrevCommand.Execute(null);
                    else _find.NextCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Escape:
                    _find.Close();
                    e.Handled = true;
                    break;
            }
        }
    }
}
