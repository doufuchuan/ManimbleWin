using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ManimbleWin.Helpers;
using ManimbleWin.Models;
using ManimbleWin.ViewModels;

namespace ManimbleWin.Views
{
    public partial class CanvasView : UserControl
    {
        private CanvasObject _dragging;
        private Point _dragStart;
        private double _objStartX, _objStartY;
        private bool _isDragging;

        public CanvasView()
        {
            InitializeComponent();
            DataContextChanged += (s, e) => { }; // 预留：VM 切换时可重置状态
        }

        private MainViewModel VM => DataContext as MainViewModel;

        private void CanvasBG_Click(object sender, MouseButtonEventArgs e)
        {
            if (VM != null) VM.SelectedObject = null;
        }

        private void SlideBG_Click(object sender, MouseButtonEventArgs e)
        {
            // 只有直接点在幻灯片空白处才取消选择；点在对象上由 Object_Click 处理并标记已处理
            if (e.Handled) return;
            if (VM != null) VM.SelectedObject = null;
        }

        private void Object_Click(object sender, MouseButtonEventArgs e)
        {
            var border = sender as Border;
            if (border?.Tag is CanvasObject o && VM != null)
            {
                VM.SelectObjectCommand.Execute(o);
                _dragging = o;
                _objStartX = o.X;
                _objStartY = o.Y;
                _dragStart = e.GetPosition(SlideSurface);
                _isDragging = true;
                border.CaptureMouse();
                e.Handled = true; // 阻止 SlideBG_Click 取消选择
            }
        }

        private void Object_Move(object sender, MouseEventArgs e)
        {
            if (!_isDragging || _dragging == null) return;
            // 设计坐标：SlideSurface 宽 1280，Viewbox 已把鼠标坐标换算到该坐标系
            Point now = e.GetPosition(SlideSurface);
            double dx = now.X - _dragStart.X;
            double dy = now.Y - _dragStart.Y;
            double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
            _dragging.X = _objStartX + dx;
            _dragging.Y = _objStartY + dy;
        }

        private void Object_Up(object sender, MouseButtonEventArgs e)
        {
            if (!_isDragging) return;
            _isDragging = false;
            _dragging = null;
            if (sender is UIElement el && el.IsMouseCaptured) el.ReleaseMouseCapture();
        }
    }
}
