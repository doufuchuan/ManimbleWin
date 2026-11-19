using System;
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
            double nx = _objStartX + dx;
            double ny = _objStartY + dy;
            // 按住 Shift 对齐到 10px 网格
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                nx = Math.Round(nx / 10.0) * 10.0;
                ny = Math.Round(ny / 10.0) * 10.0;
            }
            _dragging.X = nx;
            _dragging.Y = ny;
            VM?.ShowLivePosition(_dragging);
        }

        private void Object_Up(object sender, MouseButtonEventArgs e)
        {
            if (!_isDragging) return;
            _isDragging = false;
            if (sender is UIElement el && el.IsMouseCaptured) el.ReleaseMouseCapture();
            var moved = _dragging;
            _dragging = null;
            // 松手后把新坐标写回 Python 源码
            if (moved != null && (Math.Abs(moved.X - _objStartX) > 0.01 || Math.Abs(moved.Y - _objStartY) > 0.01))
                VM?.CommitObjectMove(moved);
        }
    }
}
