using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
                    return $"self.add(ImageMobject({PyPath(ImagePathOf(o))}).scale(0.6).move_to({pos}))  {MarkerOf(o)}";

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

        /// <summary>
        /// 对象的内容 / 字号 / 颜色在检视器里被改过之后，只替换那一行里对应的三处：
        /// <c>MathTex("…")</c> 的公式文本、<c>font_size=</c>、<c>color=</c>。
        /// <c>.move_to(...)</c> 与行尾 <c># manimble:&lt;id&gt;</c> 原样保留 ——
        /// 整行重写会按画布像素反算坐标，可能让物体轻微移位，这里不冒这个风险。
        /// 只有能按标识找到“画布自己生成的行”才动手，手写语句一律不碰。
        /// </summary>
        public static bool TryRewriteObjectLine(string source, CanvasObject o, double frameW, double frameH,
                                                out string updated, out string note)
        {
            updated = source;
            note = null;
            if (string.IsNullOrEmpty(source) || o == null) { note = "没有可写回的源码"; return false; }
            if (string.IsNullOrEmpty(o.CodeId)) { note = "该对象没有源码标识"; return false; }

            var lines = source.Replace("\r\n", "\n").Split('\n').ToList();
            string tag = Marker + ":" + o.CodeId;
            int idx = lines.FindIndex(l => l.Contains(tag));
            if (idx < 0) { note = "代码里找不到该对象的标记行"; return false; }
            o.SourceLine = idx + 1;

            string line = lines[idx];
            int callAt = line.IndexOf("MathTex(", StringComparison.Ordinal);
            int open = callAt >= 0 ? callAt + "MathTex".Length : -1;
            int close = open >= 0 ? FindMatchingParen(line, open) : -1;

            string rewritten = line;

            // 1) 公式文本：调用参数里的第一个字符串字面量
            if (close > open)
            {
                int q = -1;
                for (int i = open + 1; i < close; i++)
                    if (line[i] == '"' || line[i] == '\'') { q = i; break; }

                if (q >= 0)
                {
                    char quote = line[q];
                    int qEnd = -1;
                    for (int i = q + 1; i < close; i++)
                    {
                        if (line[i] == '\\') { i++; continue; }   // 跳过被转义的字符
                        if (line[i] == quote) { qEnd = i; break; }
                    }
                    if (qEnd > q)
                    {
                        // 行里原本可能带 r"…" / f"…" 前缀，前缀要一起替换掉，
                        // 否则会拼出 rr"…" 这种非法字面量。
                        int qStart = q;
                        if (q - 1 > open && "rRuUbBfF".IndexOf(line[q - 1]) >= 0 &&
                            (q - 2 <= open || !(char.IsLetterOrDigit(line[q - 2]) || line[q - 2] == '_')))
                            qStart = q - 1;

                        string tex = NormalizeTex(string.IsNullOrEmpty(o.Value) ? o.DisplayText : o.Value);
                        rewritten = rewritten.Substring(0, qStart) + RawString(tex) + rewritten.Substring(qEnd + 1);
                    }
                }
            }

            // 2) 字号 3) 颜色
            rewritten = Regex.Replace(rewritten, @"font_size\s*=\s*\d+", "font_size=" + Int(o.FontSize));
            if (!string.IsNullOrEmpty(o.Color))
                rewritten = Regex.Replace(rewritten, "color\\s*=\\s*\"#[0-9A-Fa-f]{3,8}\"", "color=\"" + o.Color + "\"");

            if (rewritten == line) { note = "代码已是最新"; return false; }

            lines[idx] = rewritten;
            updated = string.Join("\n", lines);
            note = "已更新该公式的代码行";
            return true;
        }

        private static int FindMatchingParen(string s, int openIdx)        {
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
        /// 取图片对象写进源码的相对路径：引擎/落地的 ImagePath 通常是绝对路径或含 assets/ 的路径，
        /// 统一成正斜杠；若路径里含 assets/ 段则从最后一个 assets/ 起截取（相对 deck），否则原样保留。
        /// 没有路径时回退到占位图，保证原型模式也生成能解析的代码。
        /// </summary>
        private static string ImagePathOf(CanvasObject o)
        {
            string p = o == null ? "" : (o.ImagePath ?? "");
            if (string.IsNullOrWhiteSpace(p)) return "assets/picture.png";
            p = p.Replace('\\', '/');
            int at = p.LastIndexOf("assets/", StringComparison.OrdinalIgnoreCase);
            if (at >= 0) return p.Substring(at);
            return p;
        }

        /// <summary>
        /// 把路径写成 Python 双引号字符串字面量（引擎 py_str 的等价）。
        /// 故意不加 r/f 前缀：路径已是正斜杠、且 Escape 已正确转义 \ 与引号，
        /// 再叠加前缀只会引入前缀与转义冲突（rr"…" 或把 \n 误当插值）。
        /// </summary>
        private static string PyPath(string path) => "\"" + Escape(path) + "\"";

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

        /// <summary>取某个对象在源码里的整行文本（按 # manimble:id 标记定位）；没有标记行返回 null。</summary>
        public static string LineTextFor(string source, string codeId)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(codeId)) return null;
            string tag = Marker + ":" + codeId;
            foreach (var line in source.Replace("\r\n", "\n").Split('\n'))
                if (line.Contains(tag)) return line;
            return null;
        }

        /// <summary>删除某个对象对应的整行代码（画布上删掉对象时调用）。返回是否真的删掉了。</summary>
        public static bool TryRemoveObjectLine(string source, string codeId, out string updated)
        {
            updated = source;
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(codeId)) return false;
            string tag = Marker + ":" + codeId;
            var lines = source.Replace("\r\n", "\n").Split('\n').ToList();
            int idx = lines.FindIndex(l => l.Contains(tag));
            if (idx < 0) return false;
            lines.RemoveAt(idx);
            updated = string.Join("\n", lines);
            return true;
        }

        /// <summary>
        /// 查找替换用：把某个对象那一行里的第一个字符串字面量换成新文本（字号/颜色/坐标等参数原样保留）。
        /// 只在本应用生成的行上工作（必须有 # manimble:id 标记），手写代码一律不碰——
        /// 手写行的字符串可能是表达式的一部分，盲目替换会改坏语法。
        /// </summary>
        public static bool TryReplaceLiteral(string source, string codeId, string newText,
                                             out string updated, out string note)
        {
            updated = source;
            note = null;
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(codeId)) { note = "没有可写回的源码"; return false; }
            string tag = Marker + ":" + codeId;
            var lines = source.Replace("\r\n", "\n").Split('\n').ToList();
            int idx = lines.FindIndex(l => l.Contains(tag));
            if (idx < 0) { note = "代码里找不到该对象的标记行"; return false; }

            string line = lines[idx];
            int q = -1;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '"' || line[i] == '\'') { q = i; break; }
                // 行尾注释之前还没遇到引号，说明这一行没有字面量
                if (line[i] == '#') break;
            }
            if (q < 0) { note = "这一行没有可以替换的文本"; return false; }

            // 前缀（r / f / u / b）要原样保留：切掉 r 会让反斜杠被再转义一遍，
            // 切掉 f 会让原来的 {} 从插值退化成字面量（或反过来）。
            int quoteStart = q;
            char prefix = '\0';
            if (q - 1 >= 0 && "rRuUbBfF".IndexOf(line[q - 1]) >= 0 &&
                (q - 2 < 0 || !(char.IsLetterOrDigit(line[q - 2]) || line[q - 2] == '_')))
            {
                prefix = line[q - 1];
                quoteStart = q - 1;
            }

            char quote = line[q];
            int qEnd = -1;
            for (int i = q + 1; i < line.Length; i++)
            {
                if (line[i] == '\\') { i++; continue; }
                if (line[i] == quote) { qEnd = i; break; }
            }
            if (qEnd < 0) { note = "字符串字面量没有闭合"; return false; }

            string literal = EscapeLiteral(newText ?? "", quote, prefix);
            lines[idx] = line.Substring(0, quoteStart) + literal + line.Substring(qEnd + 1);
            updated = string.Join("\n", lines);
            note = "已替换该对象代码里的文本";
            return true;
        }

        /// <summary>
        /// 按目标字面量类型转义一段普通文本，返回值包含前缀与引号。
        /// raw（r"…"）：反斜杠就是字面量，不能再转义；f-string（f"…"）：花括号是插值，字面花括号要写成 {{ }}。
        /// </summary>
        private static string EscapeLiteral(string s, char quote, char prefix = '\0')
        {
            string t = (s ?? "").Replace("\r", "");
            bool isRaw = prefix == 'r' || prefix == 'R';

            if (isRaw && t.IndexOf(quote) >= 0)
            {
                // raw 字符串里写不出「引号本身」而不带反斜杠，只能退回普通字符串（此时反斜杠要转义）
                isRaw = false;
                prefix = '\0';
            }

            // 前缀要显式转成字符串：char + char 是整数加法（'r' + '"' == 148），会写出非法代码
            string pre = prefix == '\0' ? "" : prefix.ToString();

            if (isRaw)
            {
                t = t.Replace("\n", "\\n");   // 单行 raw 里不能有真换行；\n 在 LaTeX 里正好是换行
                return pre + quote + t + quote;
            }

            // 必须先转义反斜杠再转义换行：反过来的话，刚写进去的 \n 会被再转义一次变成 \\n
            t = t.Replace("\\", "\\\\");
            if (quote == '\'') t = t.Replace("'", "\\'");
            else t = t.Replace("\"", "\\\"");
            if (prefix == 'f' || prefix == 'F')
                t = t.Replace("{", "{{").Replace("}", "}}");
            t = t.Replace("\n", "\\n");
            return pre + quote + t + quote;
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
