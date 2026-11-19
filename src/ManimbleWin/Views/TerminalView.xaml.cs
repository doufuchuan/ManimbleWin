using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ManimbleWin.ViewModels;

namespace ManimbleWin.Views
{
    public partial class TerminalView : UserControl
    {
        private TerminalViewModel _term;

        public TerminalView()
        {
            InitializeComponent();
            DataContextChanged += (s, e) => Hook(e.NewValue as MainViewModel);
            IsVisibleChanged += (s, e) =>
            {
                if (!IsVisible) return;
                Hook(DataContext as MainViewModel);
                ScrollToEnd();
                InputBox.Focus();
            };
            Unloaded += (s, e) => Hook(null);
        }

        private void Hook(MainViewModel vm)
        {
            if (_term != null)
            {
                _term.OutputChanged -= OnOutputChanged;
                _term.ActiveChanged -= OnActiveChanged;
            }
            _term = vm?.Terminal;
            if (_term != null)
            {
                _term.OutputChanged += OnOutputChanged;
                _term.ActiveChanged += OnActiveChanged;
            }
        }

        /// <summary>当前会话有新输出。</summary>
        private void OnOutputChanged() => ScrollToEnd();

        /// <summary>切了标签：滚到新会话自己的末尾，并把焦点交给输入行。</summary>
        private void OnActiveChanged()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ScrollToEnd();
                InputBox.Focus();
                InputBox.CaretIndex = InputBox.Text.Length;
            }));
        }

        private void ScrollToEnd()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { OutputScroll.ScrollToEnd(); } catch { }
            }));
        }

        /// <summary>点终端任意位置即聚焦输入行（像 VSCode 的集成终端）。</summary>
        private void OutputScroll_MouseDown(object sender, MouseButtonEventArgs e)
        {
            InputBox.Focus();
            Keyboard.Focus(InputBox);
            InputBox.CaretIndex = InputBox.Text.Length;
        }

        private void InputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (DataContext is not MainViewModel vm || vm.Terminal.Active == null) return;
            var term = vm.Terminal;

            if (e.Key == Key.Enter)
            {
                term.Submit();
                ScrollToEnd();
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                term.Recall(true);
                InputBox.CaretIndex = InputBox.Text.Length;
                e.Handled = true;
            }
            else if (e.Key == Key.Down)
            {
                term.Recall(false);
                InputBox.CaretIndex = InputBox.Text.Length;
                e.Handled = true;
            }
            else if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control)
            {
                term.ClearCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                // 有选中文本时保持正常的复制行为
                if (string.IsNullOrEmpty(InputBox.SelectedText)) term.Cancel();
            }
        }
    }
}
