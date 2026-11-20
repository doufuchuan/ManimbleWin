using System;
using System.Collections.Generic;
using System.Linq;
using ManimbleWin.Models;

namespace ManimbleWin.Services
{
    /// <summary>一个对象的一次位移（画布设计像素，1280x720 坐标系）。</summary>
    public struct ArrangeDelta
    {
        public ArrangeDelta(CanvasObject obj, double dx, double dy)
        {
            Object = obj;
            Dx = dx;
            Dy = dy;
        }

        public CanvasObject Object { get; }
        public double Dx { get; }
        public double Dy { get; }
    }

    /// <summary>
    /// 多对象对齐与分布的纯计算。
    /// 为什么放在客户端算：引擎的 `edit` 只有单对象的 `move`，多选对齐只是「每个对象各移动一段」，
    /// 在本地算出每段位移再逐条下发，既不用扩展引擎，也能保证与画布上看到的一致。
    /// </summary>
    public static class ArrangeService
    {
        /// <summary>包围盒（画布像素）。</summary>
        public static (double Left, double Top, double Right, double Bottom) Bounds(IEnumerable<CanvasObject> objects)
        {
            double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
            foreach (var o in objects)
            {
                l = Math.Min(l, o.X);
                t = Math.Min(t, o.Y);
                r = Math.Max(r, o.X + o.Width);
                b = Math.Max(b, o.Y + o.Height);
            }
            if (l == double.MaxValue) return (0, 0, 0, 0);
            return (l, t, r, b);
        }

        /// <summary>
        /// 对齐：一个对象时对齐到幻灯片（frameW x frameH），多个对象时对齐到它们的包围盒。
        /// how: left / center / right / top / middle / bottom。
        /// </summary>
        public static List<ArrangeDelta> Align(IList<CanvasObject> objects, string how,
                                               double frameW = 1280, double frameH = 720)
        {
            var result = new List<ArrangeDelta>();
            if (objects == null || objects.Count == 0) return result;

            double left, top, right, bottom;
            if (objects.Count == 1)
            {
                left = 0; top = 0; right = frameW; bottom = frameH;
            }
            else
            {
                var b = Bounds(objects);
                left = b.Left; top = b.Top; right = b.Right; bottom = b.Bottom;
            }

            double midX = (left + right) / 2.0;
            double midY = (top + bottom) / 2.0;

            foreach (var o in objects)
            {
                double dx = 0, dy = 0;
                switch (how)
                {
                    case "left": dx = left - o.X; break;
                    case "center": dx = midX - (o.X + o.Width / 2.0); break;
                    case "right": dx = right - (o.X + o.Width); break;
                    case "top": dy = top - o.Y; break;
                    case "middle": dy = midY - (o.Y + o.Height / 2.0); break;
                    case "bottom": dy = bottom - (o.Y + o.Height); break;
                    default: return result;
                }
                if (Math.Abs(dx) > 0.01 || Math.Abs(dy) > 0.01)
                    result.Add(new ArrangeDelta(o, Math.Round(dx, 2), Math.Round(dy, 2)));
            }
            return result;
        }

        /// <summary>
        /// 分布：把首尾固定，中间对象按等间距（对象之间的空隙相等）摆开。
        /// 少于三个对象没有「中间」可调，直接返回空。
        /// </summary>
        public static List<ArrangeDelta> Distribute(IList<CanvasObject> objects, bool horizontal)
        {
            var result = new List<ArrangeDelta>();
            if (objects == null || objects.Count < 3) return result;

            var ordered = horizontal
                ? objects.OrderBy(o => o.X).ThenBy(o => o.Y).ToList()
                : objects.OrderBy(o => o.Y).ThenBy(o => o.X).ToList();

            double firstStart = horizontal ? ordered[0].X : ordered[0].Y;
            // 末尾对象按「右边 / 下边」算，否则会把最后一个也挪走，分布就失去了锚点
            double lastEnd = horizontal
                ? ordered[ordered.Count - 1].X + ordered[ordered.Count - 1].Width
                : ordered[ordered.Count - 1].Y + ordered[ordered.Count - 1].Height;
            double totalSize = ordered.Sum(o => horizontal ? o.Width : o.Height);
            double gap = (lastEnd - firstStart - totalSize) / (ordered.Count - 1);

            double cursor = firstStart;
            for (int i = 0; i < ordered.Count; i++)
            {
                var o = ordered[i];
                double current = horizontal ? o.X : o.Y;
                double dx = 0, dy = 0;
                if (horizontal) dx = Math.Round(cursor - current, 2);
                else dy = Math.Round(cursor - current, 2);
                if (Math.Abs(dx) > 0.01 || Math.Abs(dy) > 0.01) result.Add(new ArrangeDelta(o, dx, dy));
                cursor += (horizontal ? o.Width : o.Height) + gap;
            }
            return result;
        }
    }
}
