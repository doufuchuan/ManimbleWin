using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ManimbleWin.Models;
using ManimbleWin.Services;
using ManimbleWin.ViewModels;

namespace ManimbleWin.Views
{
    /// <summary>
    /// 画布交互：点选、Shift 多选、框选、拖动（多选时整组一起动）。
    /// 坐标换算依赖 Viewbox 把鼠标位置映射回 1280x720 的设计坐标系，因此所有位置都取 SlideSurface 的坐标。
    /// </summary>
    public partial class CanvasView : UserControl
    {
        private CanvasObject _dragging;
        private Point _dragStart;
        private bool _isDragging;
        private bool _dragMoved;

        // 拖动开始时每个被拖对象的原始位置：多选拖动时按同一段位移移动，避免逐个累加漂移
        private readonly Dictionary<CanvasObject, (double X, double Y)> _dragOrigins =
            new Dictionary<CanvasObject, (double, double)>();
        private DeckSnapshot _dragSnapshot;

        // 框选
        private bool _rubberActive;
        private Point _rubberStart;

        // 批注拖动（拖动中只改内存，松手才落盘）
        private FeedbackNoteViewModel _noteDragVm;
        private Point _noteDragStart;
        private bool _noteDragMoved;
        private double _noteDragOriginX;
        private double _noteDragOriginY;

        public CanvasView()
        {
            InitializeComponent();
            DataContextChanged += (s, e) => { }; // 预留：VM 切换时可重置状态

            // 画布背景渐变的配色完全交给 XAML 里那两个 {DynamicResource CanvasTopColor/CanvasBottomColor}，
            // 这里刻意不做任何赋值：只要在代码里给 GradientStop.Color 赋过一次值，XAML 的
            // DynamicResource 表达式就会被顶掉（ReadLocalValue 变成 Color 而不是 ResourceReferenceExpression），
            // 之后渐变只能靠手动同步，等于把声明式的绑定换成命令式的追赶。原因见 CanvasView.xaml 里的注释。
        }

        private MainViewModel VM => DataContext as MainViewModel;

        private void CanvasBG_Click(object sender, MouseButtonEventArgs e)
        {
            // 批注模式下点画布（幻灯片外的灰区）也不改变对象选中：
            // 否则"进批注模式顺手点一下"就会把用户辛苦选好的一组对象丢掉（PRD FB-P0-03）
            if (VM?.Feedback?.IsAnnotating == true) return;
            if (VM != null) VM.ClearSelection();
        }

        private void SlideBG_Click(object sender, MouseButtonEventArgs e)
        {
            // 只有直接点在幻灯片空白处才取消选择；点在对象上由 Object_Click 处理并标记已处理
            if (e.Handled) return;

            if (VM == null) return;
            bool additive = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (!additive) VM.ClearSelection();

            // 在空白处按下即开始框选（Shift 时在已有选区上追加）
            _rubberActive = true;
            _rubberStart = e.GetPosition(SlideSurface);
            ShowRubber(_rubberStart, _rubberStart);
            SlideSurface.CaptureMouse();
            e.Handled = true;
        }

        private void Object_Click(object sender, MouseButtonEventArgs e)
        {
            var border = sender as Border;
            if (border?.Tag is CanvasObject o && VM != null)
            {
                bool additive = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                if (additive)
                {
                    VM.ToggleSelection(o);
                }
                else if (!o.IsSelected)
                {
                    // 点在未选中的对象上：换成单选它（含它的整组）
                    VM.SelectSingleObject(o);
                }
                // 已在选区内再按下：保持整个选区，直接开始拖动

                if (!VM.SelectedObjects.Contains(o)) { e.Handled = true; return; }

                _dragging = o;
                _dragStart = e.GetPosition(SlideSurface);
                _isDragging = true;
                _dragMoved = false;
                _dragOrigins.Clear();
                foreach (var sel in VM.SelectedObjects) _dragOrigins[sel] = (sel.X, sel.Y);
                _dragSnapshot = VM.BeginMoveTransaction();
                border.CaptureMouse();
                e.Handled = true; // 阻止 SlideBG_Click 取消选择
            }
        }

        private void Object_Move(object sender, MouseEventArgs e)
        {
            if (_isDragging && _dragging != null)
            {
                // 设计坐标：SlideSurface 宽 1280，Viewbox 已把鼠标坐标换算到该坐标系
                Point now = e.GetPosition(SlideSurface);
                double dx = now.X - _dragStart.X;
                double dy = now.Y - _dragStart.Y;

                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                {
                    // 按住 Shift 对齐到 10px 网格
                    foreach (var kv in _dragOrigins.ToList())
                    {
                        var o = kv.Key;
                        o.X = Math.Round((kv.Value.X + dx) / 10.0) * 10.0;
                        o.Y = Math.Round((kv.Value.Y + dy) / 10.0) * 10.0;
                    }
                }
                else
                {
                    foreach (var kv in _dragOrigins.ToList())
                    {
                        var o = kv.Key;
                        o.X = kv.Value.X + dx;
                        o.Y = kv.Value.Y + dy;
                    }
                }
                if (Math.Abs(dx) > 0.01 || Math.Abs(dy) > 0.01) _dragMoved = true;
                VM?.ShowLivePosition(_dragging);
                return;
            }

            if (_rubberActive)
            {
                Point now = e.GetPosition(SlideSurface);
                ShowRubber(_rubberStart, now);
            }
        }

        private void Object_Up(object sender, MouseButtonEventArgs e)
        {
            if (_rubberActive)
            {
                _rubberActive = false;
                if (SlideSurface.IsMouseCaptured) SlideSurface.ReleaseMouseCapture();
                HideRubber();

                Point end = e.GetPosition(SlideSurface);
                var rect = new Rect(
                    Math.Min(_rubberStart.X, end.X), Math.Min(_rubberStart.Y, end.Y),
                    Math.Abs(end.X - _rubberStart.X), Math.Abs(end.Y - _rubberStart.Y));

                // 太小的框选当作「点空白」，不做选择
                if (rect.Width > 3 || rect.Height > 3)
                {
                    bool additive = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                    VM?.SelectInRect(rect, additive);
                }
                return;
            }

            if (!_isDragging) return;
            _isDragging = false;
            if (sender is UIElement el && el.IsMouseCaptured) el.ReleaseMouseCapture();

            var dragged = _dragOrigins.Keys.ToList();
            _dragging = null;

            // 松手后把新坐标写回 Python 源码（多选时逐个写回）
            if (_dragMoved)
            {
                foreach (var o in dragged) VM?.CommitObjectMove(o);
                VM?.EndMoveTransaction(_dragSnapshot, dragged.Count > 1 ? $"移动 {dragged.Count} 个对象" : "移动对象");
            }
            _dragSnapshot = null;
            _dragOrigins.Clear();
            _dragMoved = false;
        }

        private void ShowRubber(Point a, Point b)
        {
            double x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
            RubberBand.Width = Math.Max(1, Math.Abs(b.X - a.X));
            RubberBand.Height = Math.Max(1, Math.Abs(b.Y - a.Y));
            RubberBand.Margin = new Thickness(x, y, 0, 0);
            RubberBand.Visibility = Visibility.Visible;
        }

        private void HideRubber() => RubberBand.Visibility = Visibility.Collapsed;

        #region 批注层

        /// <summary>
        /// 批注模式下在幻灯片上点一下：在该点新建一条空批注并直接进编辑。
        /// <para>⚠ 坐标只取 <c>e.GetPosition(SlideSurface)</c>：Viewbox 已经把它换算进 1280×720 逻辑坐标系，
        /// 这里<b>不能再乘/除任何缩放比</b>，否则批注会落在点击点的斜上方（风险 R3）。</para>
        /// </summary>
        private void FeedbackCatcher_Click(object sender, MouseButtonEventArgs e)
        {
            FeedbackViewModel feedback = VM?.Feedback;
            if (feedback == null || !feedback.IsAnnotating) return;

            Point p = e.GetPosition(SlideSurface);
            double x = Math.Min(Math.Max(0, p.X / SlideSurface.ActualWidth), 0.96);
            double y = Math.Min(Math.Max(0, p.Y / SlideSurface.ActualHeight), 0.96);

            feedback.EndEdit();                       // 先结束上一条（文本为空会被丢弃）
            FeedbackNoteViewModel note = feedback.AddAt(x, y);
            if (note != null)
            {
                feedback.BeginEdit(note);
                FocusNoteTextBox(note);
            }
            e.Handled = true;                         // 不让 SlideBG_Click 清选区 / 开始框选
        }

        /// <summary>按下批注块：可能是"进编辑"，也可能是"开始拖动"，松手时才见分晓。</summary>
        private void NoteBox_Click(object sender, MouseButtonEventArgs e)
        {
            FeedbackViewModel feedback = VM?.Feedback;
            if (feedback == null) return;
            if (!((sender as Border)?.Tag is FeedbackNoteViewModel note)) return;

            if (note.IsEditing)
            {
                // 编辑中：让 TextBox 自己摆光标
                e.Handled = true;
                return;
            }
            if (feedback.IsReadOnly)
            {
                // 只读态：能看不能改，也不拖动（避免看起来"能改"）
                e.Handled = true;
                return;
            }
            // 已经不在 Store 里的条目（例如刚被丢弃的空批注，容器还没被摘掉）不再进编辑
            if (feedback.Store?.Find(note.Id) == null)
            {
                e.Handled = true;
                return;
            }

            _noteDragVm = note;
            _noteDragStart = e.GetPosition(SlideSurface);
            _noteDragOriginX = note.X;
            _noteDragOriginY = note.Y;
            _noteDragMoved = false;
            (sender as Border)?.CaptureMouse();

            // 必须吃掉：否则会冒泡到 SlideSurface 的 SlideBG_Click（清选区 + 开始框选）
            e.Handled = true;
        }

        private void NoteBox_Move(object sender, MouseEventArgs e)
        {
            if (_noteDragVm == null) return;

            Point now = e.GetPosition(SlideSurface);
            if (!_noteDragMoved &&
                (Math.Abs(now.X - _noteDragStart.X) > 3 || Math.Abs(now.Y - _noteDragStart.Y) > 3))
                _noteDragMoved = true;
            if (!_noteDragMoved) return;

            // 同一坐标系（SlideSurface）里算位移，再折算成比例：与缩放无关
            _noteDragVm.MoveTo(
                _noteDragOriginX + (now.X - _noteDragStart.X) / FeedbackNoteViewModel.SlideWidth,
                _noteDragOriginY + (now.Y - _noteDragStart.Y) / FeedbackNoteViewModel.SlideHeight);
        }

        private void NoteBox_Up(object sender, MouseButtonEventArgs e)
        {
            if (_noteDragVm == null) return;

            FeedbackNoteViewModel note = _noteDragVm;
            _noteDragVm = null;
            if (sender is UIElement el && el.IsMouseCaptured) el.ReleaseMouseCapture();

            FeedbackViewModel feedback = VM?.Feedback;
            if (_noteDragMoved)
            {
                feedback?.CommitMove(note);       // 松手写一次盘
            }
            else
            {
                feedback?.BeginEdit(note);        // 单击即进编辑（PRD FB-P1-01：不要求双击）
                FocusNoteTextBox(note);
            }
            e.Handled = true;
        }

        private void NoteClose_Click(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is FeedbackNoteViewModel note)
                VM?.Feedback?.Remove(note);
            e.Handled = true;
        }

        /// <summary>
        /// 批注输入框的键位。窗口级的 <c>PreviewKeyDown</c> 对 <c>TextBoxBase</c> 早退（不能抢编辑器按键），
        /// 所以 Esc / Ctrl+Enter 只能在这里处理（风险 R4）。
        /// </summary>
        private void NoteText_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                VM?.Feedback?.EndEdit();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                VM?.Feedback?.EndEdit();
                e.Handled = true;
            }
        }

        /// <summary>失焦即提交（点别处 / 点工具按钮 / 切页都会走到这里）。EndEdit 幂等，重复触发不会多写盘。</summary>
        private void NoteText_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
            => VM?.Feedback?.EndEdit();

        /// <summary>把键盘焦点放进指定批注的输入框。容器是模板生成的，得等一次布局才找得到。</summary>
        private void FocusNoteTextBox(FeedbackNoteViewModel note)
        {
            if (note == null) return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                int index = FeedbackNotes.Items.IndexOf(note);
                if (index < 0) return;
                if (!(FeedbackNotes.ItemContainerGenerator.ContainerFromIndex(index) is DependencyObject container)) return;

                TextBox box = FindChild<TextBox>(container);
                if (box == null) return;

                box.Focus();
                Keyboard.Focus(box);
                box.CaretIndex = box.Text?.Length ?? 0;
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private static T FindChild<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) return null;

            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is T typed) return typed;

                T deeper = FindChild<T>(child);
                if (deeper != null) return deeper;
            }
            return null;
        }

        #endregion
    }
}
