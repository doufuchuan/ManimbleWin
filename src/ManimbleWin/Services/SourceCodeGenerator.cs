using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ManimbleWin.Models;

namespace ManimbleWin.Services
{
    /// <summary>
    /// 把画布对象的增删改翻译为 Manim Python 源码，并维护一个 Slide 的 construct() 方法体。
    /// 生成的源码与引擎 edit_source 期望的格式一致，可写回工作副本。
    /// 本类即使用户未连接引擎也能工作（原型模式下保证"创建对象→代码更新→可编辑→生效"闭环）。
    /// </summary>
    public static class SourceCodeGenerator
    {
        private const string Marker = "# manimble";

        /// <summary>为一个新对象生成一行 Manim 代码（含 manimble 标记，便于回读定位）。</summary>
        public static string LineFor(CanvasObject o, double frameW = 1280, double frameH = 720)
        {
            // 画布设计坐标(1280x720) -> Manim 逻辑坐标(宽约14.22, 高8)，中心为原点
            double fx = ToManimX(o.X + o.Width / 2.0, frameW);
            double fy = ToManimY(o.Y + o.Height / 2.0, frameH);
            string color = string.IsNullOrEmpty(o.Color) ? "#FFFFFF" : o.Color;
            string pos = $"[{Num(fx)}, {Num(fy)}, 0]";

            switch (o.Kind)
            {
                case ObjectKind.Tex:
                    string tex = string.IsNullOrEmpty(o.Value) ? o.DisplayText : o.Value;
                    return $"self.add(MathTex(r\"{Escape(tex)}\", font_size={Int(o.FontSize)}, color=\"{color}\").move_to({pos}))  {Marker}";

                case ObjectKind.Image:
                    return $"self.add(ImageMobject(\"assets/picture.png\").scale(0.6).move_to({pos}))  {Marker}";

                case ObjectKind.Table:
                    return $"self.add(Table([[\"1\", \"2\"], [\"3\", \"4\"]]).scale(0.8).move_to({pos}))  {Marker}";

                case ObjectKind.Line:
                    return $"self.add(Line(LEFT, RIGHT, color=\"{color}\").move_to({pos}))  {Marker}";

                case ObjectKind.Shape:
                    string label = o.ShapeLabel ?? "Rectangle";
                    if (label == "Circle" || label == "Ellipse")
                        return $"self.add(Circle(color=\"{color}\").scale(0.5).move_to({pos}))  {Marker}";
                    if (label == "Triangle")
                        return $"self.add(Triangle(color=\"{color}\").scale(0.5).move_to({pos}))  {Marker}";
                    return $"self.add(Rectangle(color=\"{color}\").scale(0.5).move_to({pos}))  {Marker}";

                default: // Text
                    string txt = string.IsNullOrEmpty(o.Value) ? o.DisplayText : o.Value;
                    var sb = new StringBuilder();
                    sb.Append($"self.add(Text(\"{Escape(txt)}\", font_size={Int(o.FontSize)}, color=\"{color}\"");
                    if (o.Bold) sb.Append(", weight=BOLD");
                    if (o.Italic) sb.Append(", slant=ITALIC");
                    sb.Append($").move_to({pos}))  {Marker}");
                    return sb.ToString();
            }
        }

        /// <summary>生成一个新场景类（新幻灯片）的完整源码。</summary>
        public static string NewScene(string className, string title = null)
        {
            string t = string.IsNullOrEmpty(title) ? className : title;
            var sb = new StringBuilder();
            sb.AppendLine($"class {className}(MovingCameraScene):");
            sb.AppendLine("    def construct(self):");
            sb.AppendLine($"        self.add(Text(\"{Escape(t)}\", font_size=48)  {Marker}");
            return sb.ToString().TrimEnd();
        }

        /// <summary>把一行代码追加到 Slide 的 construct() 方法体末尾（在下一个 class 之前）。</summary>
        public static string AppendLine(string source, string codeLine)
        {
            if (string.IsNullOrWhiteSpace(source))
                return "class Slide(MovingCameraScene):\n    def construct(self):\n        " + codeLine;

            var lines = source.Replace("\r\n", "\n").Split('\n').ToList();
            int insertAt = FindConstructBodyEnd(lines);
            string indent = "        ";
            lines.Insert(insertAt, indent + codeLine.TrimStart());
            return string.Join("\n", lines);
        }

        /// <summary>删除源码中包含指定标记文本的行（rough but adequate for a prototype）。</summary>
        public static string RemoveLineContaining(string source, string needle)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(needle)) return source;
            var lines = source.Replace("\r\n", "\n").Split('\n');
            var kept = lines.Where(l => !l.Contains(needle)).ToArray();
            return string.Join("\n", kept);
        }

        /// <summary>定位 construct() 方法体的插入点：method 末尾（最后一个缩进行之后、下一个 def/class 之前）。</summary>
        private static int FindConstructBodyEnd(List<string> lines)
        {
            int defLine = -1;
            for (int i = 0; i < lines.Count; i++)
                if (lines[i].TrimStart().StartsWith("def construct")) { defLine = i; break; }
            if (defLine < 0) defLine = 0;

            int end = lines.Count;
            for (int i = defLine + 1; i < lines.Count; i++)
            {
                string t = lines[i].TrimStart();
                if (t.StartsWith("class ") || t.StartsWith("def ")) { end = i; break; }
            }
            // 回退跳过尾部空行
            while (end > defLine + 1 && string.IsNullOrWhiteSpace(lines[end - 1])) end--;
            return end;
        }

        private static double ToManimX(double canvasX, double frameW) => (canvasX / frameW - 0.5) * 14.22;
        private static double ToManimY(double canvasY, double frameH) => (0.5 - canvasY / frameH) * 8.0;

        private static string Escape(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

        private static string Num(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);
        private static string Int(double d) => Math.Max(8, (int)Math.Round(d)).ToString(CultureInfo.InvariantCulture);
    }
}
