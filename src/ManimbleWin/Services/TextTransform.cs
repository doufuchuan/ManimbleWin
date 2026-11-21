using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace ManimbleWin.Services
{
    /// <summary>
    /// 纯文本变换：列表 marker、缩进、Unicode 上/下标。
    /// <para>逐条对齐 macOS 原版
    /// <c>Manimble-reference/apps/macos/Sources/Manimble/FormatBar.swift</c> 的 <c>enum TextTransform</c>
    /// （188–283 行）。两端必须给出逐字节一致的结果，否则同一个 deck 在 Mac 和 Windows 上
    /// 打开会是两份不同的源码。</para>
    /// <para>无 UI、无引擎依赖，可以直接单测；ViewModel 里禁止内联同类正则。</para>
    /// </summary>
    public static class TextTransform
    {
        /// <summary>
        /// 行首列表 marker（与 Swift 的 NSRegularExpression 逐字一致）：
        /// 任意缩进 + 一个圆点/短横/项目符号字符 或 「数字 + . / )」+ 至少一个空白。
        /// </summary>
        public const string MarkerPattern = @"^(\s*)(?:[•\-–▪◦]|\d+[.)])\s+";

        private static readonly Regex Marker = new Regex(MarkerPattern, RegexOptions.Compiled);

        /// <summary>上标正表：可转换字符。</summary>
        private const string SupSource = "0123456789+-=()abcdefghijklmnoprstuvwxyzABDEGHIJKLMNOPRTUVW";

        /// <summary>上标正表：对应的上标字符（与 <see cref="SupSource"/> 等长，逐位对应）。</summary>
        private const string SupTarget = "⁰¹²³⁴⁵⁶⁷⁸⁹⁺⁻⁼⁽⁾ᵃᵇᶜᵈᵉᶠᵍʰⁱʲᵏˡᵐⁿᵒᵖʳˢᵗᵘᵛʷˣʸᶻᴬᴮᴰᴱᴳᴴᴵᴶᴷᴸᴹᴺᴼᴾᴿᵀᵁⱽᵂ";

        /// <summary>下标正表：可转换字符。</summary>
        private const string SubSource = "0123456789+-=()aehijklmnoprstuvx";

        /// <summary>下标正表：对应的下标字符。</summary>
        private const string SubTarget = "₀₁₂₃₄₅₆₇₈₉₊₋₌₍₎ₐₑₕᵢⱼₖₗₘₙₒₚᵣₛₜᵤᵥₓ";

        private static readonly Dictionary<char, char> Sup = BuildMap(SupSource, SupTarget, "上标");
        private static readonly Dictionary<char, char> SupBack = Invert(Sup);
        private static readonly Dictionary<char, char> Sub = BuildMap(SubSource, SubTarget, "下标");
        private static readonly Dictionary<char, char> SubBack = Invert(Sub);

        /// <summary>
        /// 建表。两串长度不等会静默错位（'q' 之后所有字符都映射错），是这类映射表最容易埋的坑，
        /// 所以在这里直接断言——宁可启动时炸，也不要产出一堆看似正常的错字符。
        /// </summary>
        private static Dictionary<char, char> BuildMap(string source, string target, string label)
        {
            Debug.Assert(source.Length == target.Length, label + "映射表长度不一致");
            var map = new Dictionary<char, char>(source.Length);
            int n = Math.Min(source.Length, target.Length);
            for (int i = 0; i < n; i++) map[source[i]] = target[i];
            return map;
        }

        /// <summary>反向表：上标字符 → 原字符。</summary>
        private static Dictionary<char, char> Invert(Dictionary<char, char> map)
        {
            var back = new Dictionary<char, char>(map.Count);
            foreach (var kv in map) back[kv.Value] = kv.Key;
            return back;
        }

        /// <summary>按 \n 切行。保留空行（与 Swift 的 components(separatedBy:) 一致）。</summary>
        public static string[] Lines(string s) => (s ?? "").Split('\n');

        /// <summary>该行是否以列表 marker 开头（含编号 marker）。</summary>
        public static bool HasMarker(string line) => Marker.IsMatch(line ?? "");

        /// <summary>
        /// 剥掉列表 marker，返回 (缩进, 正文)。
        /// 没有 marker 时，把行首连续的半角空格/制表符当缩进，其余算正文。
        /// </summary>
        public static (string Indent, string Body) Strip(string line)
        {
            string l = line ?? "";
            Match m = Marker.Match(l);
            if (m.Success)
                return (m.Groups[1].Value, l.Substring(m.Index + m.Length));

            int k = 0;
            while (k < l.Length && (l[k] == ' ' || l[k] == '\t')) k++;
            return (l.Substring(0, k), l.Substring(k));
        }

        /// <summary>非空行（去掉首尾空白后还有内容）。空行在列表变换里要原样跳过。</summary>
        private static List<string> NonEmptyLines(string s)
            => Lines(s).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

        /// <summary>
        /// 是否「已是项目符号列表」：所有非空行都带 marker，且去掉空白后首字符不是数字。
        /// 空文本 / 全空行返回 false（没有东西可当列表）。
        /// </summary>
        public static bool IsBulleted(string s)
        {
            var ls = NonEmptyLines(s);
            if (ls.Count == 0) return false;
            foreach (var l in ls)
            {
                if (!HasMarker(l)) return false;
                string t = l.Trim();
                if (t.Length == 0 || char.IsDigit(t[0])) return false;
            }
            return true;
        }

        /// <summary>是否「已是编号列表」：所有非空行都带 marker，且首字符是数字。</summary>
        public static bool IsNumbered(string s)
        {
            var ls = NonEmptyLines(s);
            if (ls.Count == 0) return false;
            foreach (var l in ls)
            {
                if (!HasMarker(l)) return false;
                string t = l.Trim();
                if (t.Length == 0 || !char.IsDigit(t[0])) return false;
            }
            return true;
        }

        /// <summary>
        /// 切换项目符号。已开 → 逐行剥掉 marker（保留缩进）；未开 → 先剥掉可能已有的编号 marker
        /// 再补 <c>• </c>。空行原样保留，不产生空的「• 」项。
        /// </summary>
        public static string ToggleBullets(string s)
        {
            bool on = IsBulleted(s);
            var lines = Lines(s);
            for (int i = 0; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                var (indent, rest) = Strip(lines[i]);
                lines[i] = on ? indent + rest : indent + "• " + rest;
            }
            return string.Join("\n", lines);
        }

        /// <summary>
        /// 切换编号。从 1 重新计数，空行跳过且**不占号**；
        /// 已开则剥掉 marker 恢复原文。
        /// </summary>
        public static string ToggleNumbers(string s)
        {
            bool on = IsNumbered(s);
            var lines = Lines(s);
            int n = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                var (indent, rest) = Strip(lines[i]);
                n++;
                lines[i] = on ? indent + rest : indent + n + ". " + rest;
            }
            return string.Join("\n", lines);
        }

        /// <summary>
        /// 整体缩进 / 反缩进。<paramref name="delta"/> &gt; 0 时每行前加 4 个空格；
        /// &lt; 0 时每行最多剥掉 4 个**半角空格**（制表符不剥，与 Swift 一致：
        /// 用户手打的 Tab 属于他自己的排版意图）。
        /// <para>本轮没有选区模型，所以覆盖全部行；将来做选区级时再加一个行区间参数即可。</para>
        /// </summary>
        public static string IndentAll(string s, int delta)
        {
            if (delta == 0) return s ?? "";
            var lines = Lines(s);
            for (int i = 0; i < lines.Length; i++)
            {
                if (delta > 0)
                {
                    lines[i] = "    " + lines[i];
                    continue;
                }
                int k = 0;
                while (k < 4 && k < lines[i].Length && lines[i][k] == ' ') k++;
                lines[i] = lines[i].Substring(k);
            }
            return string.Join("\n", lines);
        }

        /// <summary>
        /// 上/下标转换。<paramref name="result"/> 为转换后的文本。
        /// <para>规则（照抄 Swift <c>script</c>）：可转换字符 = 有正向或反向映射的字符；
        /// 一个都没有 → 返回 false（调用方据此就地提示「这些字符没有上标形式」，不发请求）；
        /// 可转换字符**全部**已是目标形态 → 反向转回；否则能转的转、转不了的原样保留。</para>
        /// <para>上下标互斥是这条规则的自然结果：`x²` 里 '²' 属于上标反向表，点下标时它不在下标表里，
        /// 会被原样留下，不会叠成 `x²₂`。</para>
        /// </summary>
        public static bool Script(string s, bool superscript, out string result)
        {
            string text = s ?? "";
            var map = superscript ? Sup : Sub;
            var back = superscript ? SupBack : SubBack;

            var chars = text.ToCharArray();
            var convertible = chars.Where(c => map.ContainsKey(c) || back.ContainsKey(c)).ToList();
            if (convertible.Count == 0)
            {
                result = text;
                return false;
            }

            bool allScripted = convertible.All(c => back.ContainsKey(c));
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (allScripted)
                {
                    if (back.TryGetValue(c, out char plain)) chars[i] = plain;
                }
                else if (map.TryGetValue(c, out char scripted))
                {
                    chars[i] = scripted;
                }
            }
            result = new string(chars);
            return true;
        }

        /// <summary>
        /// 全文可转换字符是否都已是上/下标形态（按钮按下态判定）。
        /// 没有可转换字符时返回 false：这样的文本既转不了、也不该亮灯。
        /// </summary>
        public static bool IsScripted(string s, bool superscript)
        {
            var map = superscript ? Sup : Sub;
            var back = superscript ? SupBack : SubBack;
            var convertible = (s ?? "").Where(c => map.ContainsKey(c) || back.ContainsKey(c)).ToList();
            return convertible.Count > 0 && convertible.All(c => back.ContainsKey(c));
        }
    }
}
