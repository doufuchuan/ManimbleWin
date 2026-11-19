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

        /// <summary>每个对象带一个稳定 id，源码行被插入/删除后仍能找回自己那一行。</summary>
        private static string MarkerOf(CanvasObject o) =>
            string.IsNullOrEmpty(o?.CodeId) ? Marker : Marker + ":" + o.CodeId;

        /// <summary>为新对象分配源码标识。</summary>
        public static string NewCodeId() => Guid.NewGuid().ToString("N").Substring(0, 6);

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
                    string tex = NormalizeTex(string.IsNullOrEmpty(o.Value) ? o.DisplayText : o.Value);
                    return $"self.add(MathTex({RawString(tex)}, font_size={Int(o.FontSize)}, color=\"{color}\").move_to({pos}))  {MarkerOf(o)}";

                case ObjectKind.Image:
                    return $"self.add(ImageMobject(\"assets/picture.png\").scale(0.6).move_to({pos}))  {MarkerOf(o)}";

                case ObjectKind.Table:
                    return $"self.add(Table([[\"1\", \"2\"], [\"3\", \"4\"]]).scale(0.8).move_to({pos}))  {MarkerOf(o)}";

                case ObjectKind.Line:
                    return $"self.add(Line(LEFT, RIGHT, color=\"{color}\").move_to({pos}))  {MarkerOf(o)}";

                case ObjectKind.Shape:
                    string label = o.ShapeLabel ?? "Rectangle";
                    if (label == "Circle" || label == "Ellipse")
                        return $"self.add(Circle(color=\"{color}\").scale(0.5).move_to({pos}))  {MarkerOf(o)}";
                    if (label == "Triangle")
                        return $"self.add(Triangle(color=\"{color}\").scale(0.5).move_to({pos}))  {MarkerOf(o)}";
                    return $"self.add(Rectangle(color=\"{color}\").scale(0.5).move_to({pos}))  {MarkerOf(o)}";

                default: // Text
                    string txt = string.IsNullOrEmpty(o.Value) ? o.DisplayText : o.Value;
                    var sb = new StringBuilder();
                    sb.Append($"self.add(Text(\"{Escape(txt)}\", font_size={Int(o.FontSize)}, color=\"{color}\"");
                    if (o.Bold) sb.Append(", weight=BOLD");
                    if (o.Italic) sb.Append(", slant=ITALIC");
                    sb.Append($").move_to({pos}))  {MarkerOf(o)}");
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
            sb.AppendLine($"        self.add(Text(\"{Escape(t)}\", font_size=48))  {Marker}");
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
        public static string RemoveLineContaining(string source, string needle)        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(needle)) return source;
            var lines = source.Replace("\r\n", "\n").Split('\n');
            var kept = lines.Where(l => !l.Contains(needle)).ToArray();
            return string.Join("\n", kept);
        }

        /// <summary>
        /// 对象被拖动/移动后，把新坐标写回它在源码里对应的那一行。
        /// 优先改写该行已有的 <c>.move_to((x, y, 0))</c>；没有则追加一个；
        /// 若该行是 self.play / self.add / VGroup().arrange 之类的“组织语句”，则不硬改，返回说明。
        /// </summary>
        public static bool TryUpdatePosition(string source, CanvasObject o, double frameW, double frameH,
                                             out string updated, out string note)
        {
            updated = source;
            note = null;
            if (string.IsNullOrEmpty(source) || o == null) { note = "没有可写回的源码"; return false; }

            var lines = source.Replace("\r\n", "\n").Split('\n').ToList();

            // 先用对象自己的源码标识定位（行号会随编辑漂移，标识不会）；
            // 没有标识的历史对象才退回 SourceLine。
            int idx = -1;
            if (!string.IsNullOrEmpty(o.CodeId))
            {
                string tag = Marker + ":" + o.CodeId;
                idx = lines.FindIndex(l => l.Contains(tag));
            }
            if (idx < 0) idx = o.SourceLine - 1;
            if (idx < 0 || idx >= lines.Count) { note = "找不到对象对应的源码行"; return false; }
            o.SourceLine = idx + 1;

            string line = lines[idx];
            string trimmed = line.Trim();
            if (trimmed.Length == 0) { note = "对象对应的源码行为空"; return false; }

            double fx = ToManimX(o.X + o.Width / 2.0, frameW);
            double fy = ToManimY(o.Y + o.Height / 2.0, frameH);
            string pos = $"({Num(fx)}, {Num(fy)}, 0)";

            int moveAt = line.IndexOf("move_to(", StringComparison.Ordinal);
            if (moveAt >= 0)
            {
                int open = moveAt + "move_to".Length;
                int close = FindMatchingParen(line, open);
                if (close < 0) { note = "move_to 括号不完整"; return false; }
                // 保留原调用的括号，只替换里面的坐标元组
                lines[idx] = line.Substring(0, open) + "(" + pos + ")" + line.Substring(close + 1);
                updated = string.Join("\n", lines);
                note = $"move_to {pos}";
                return true;
            }

            if (trimmed.StartsWith("self.play") || trimmed.StartsWith("self.add") ||
                trimmed.StartsWith("self.wait") || trimmed.StartsWith("self.next_slide") ||
                trimmed.StartsWith("VGroup(") || trimmed.StartsWith("Group(") ||
                trimmed.StartsWith("#") || trimmed.StartsWith("def ") || trimmed.StartsWith("class ") ||
                trimmed.StartsWith("return") || trimmed.EndsWith(":") || trimmed.EndsWith(",") ||
                trimmed.EndsWith("(") || !trimmed.Contains("="))
            {
                note = "该对象由剧本语句（play/add/arrange 等）摆放，代码里没有独立的坐标";
                return false;
            }

            // 普通创建语句：obj = Xxx(...) → 在行尾补一个 move_to
            (int lineEnd, string comment) = SplitTrailingComment(line);
            string head = line.Substring(0, lineEnd).TrimEnd();
            if (!head.EndsWith(")"))
            {
                note = "这一行不是对象创建语句";
                return false;
            }
            lines[idx] = head + ".move_to(" + pos + ")" + comment;
            updated = string.Join("\n", lines);
            note = $"追加 move_to {pos}";
            return true;
        }

        private static int FindMatchingParen(string s, int openIdx)
        {
            if (openIdx < 0 || openIdx >= s.Length || s[openIdx] != '(') return -1;
            int depth = 0;
            for (int i = openIdx; i < s.Length; i++)
            {
                if (s[i] == '(') depth++;
                else if (s[i] == ')')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        /// <summary>把行尾的 # 注释分离出来（忽略字符串里的 #）。</summary>
        private static (int end, string comment) SplitTrailingComment(string line)
        {
            bool inString = false;
            char quote = '"';
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inString)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == quote) inString = false;
                    continue;
                }
                if (c == '"' || c == '\'') { inString = true; quote = c; continue; }
                if (c == '#') return (i, line.Substring(i));
            }
            return (line.Length, "");
        }

        /// <summary>定位 construct() 方法体的插入点：method 末尾（最后一个缩进行之后、下一个 def/class 之前）。</summary>
        private static int FindConstructBodyEnd(List<string> lines)        {
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

        /// <summary>
        /// 公式值可能粘着 Python 字符串外壳（例如从源码回读到的 r"x^2"），
        /// 直接再套一层 raw string 会生成 rr"…" 这种语法错误，这里先剥掉。
        /// </summary>
        private static string NormalizeTex(string tex)
        {
            string t = (tex ?? "").Trim();
            if (t.Length == 0) return t;
            var m = System.Text.RegularExpressions.Regex.Match(
                t, "^(?:[rR])?(?:\"([\\s\\S]*)\"|'([\\s\\S]*)')$");
            if (m.Success)
            {
                string inner = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (inner.Length > 0) return inner;
            }
            return t;
        }

        /// <summary>把公式写成 Python raw string：保留反斜杠，按内容选择引号，绝不出现 rr"…"。</summary>
        private static string RawString(string s)
        {
            string t = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (!t.Contains("\"")) return "r\"" + t + "\"";
            if (!t.Contains("'")) return "r'" + t + "'";
            // 单双引号都有：退回普通字符串（反斜杠转义，Python 侧仍还原为 LaTeX）
            return "\"" + t.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static string Num(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);
        private static string Int(double d) => Math.Max(8, (int)Math.Round(d)).ToString(CultureInfo.InvariantCulture);
    }
}
