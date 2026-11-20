using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ManimbleWin.Services
{
    /// <summary>
    /// 查找与替换的纯计算部分：正则构造与单点替换。
    /// 为什么单独拎出来：这两段规则必须与 engine/manimble/find.py 完全一致
    /// （整体词加边界、$1 与 \1 两种分组写法、只替换第 n 个匹配），
    /// 放在 ViewModel 里既不好测也容易与引擎版本漂移。
    /// </summary>
    public static class FindMath
    {
        /// <summary>对应 find.py 的 pattern()：query + case / word / regex 三个开关。</summary>
        public static Regex Build(string query, bool matchCase, bool wholeWord, bool useRegex)
        {
            string body = useRegex ? query : Regex.Escape(query ?? "");
            if (wholeWord) body = $"(?<!\\w)(?:{body})(?!\\w)";
            var opts = RegexOptions.None;
            if (!matchCase) opts |= RegexOptions.IgnoreCase;
            return new Regex(body, opts);
        }

        /// <summary>所有非空匹配（find.py 的 _matches 会丢掉零宽匹配，这里保持一致）。</summary>
        public static List<Match> Matches(Regex re, string text)
            => re.Matches(text ?? "").Cast<Match>().Where(m => m.Length > 0).ToList();

        /// <summary>
        /// 把替换串里的分组引用统一成 <c>$n</c> 一种写法。
        /// 为什么要归一：引擎 find.py 的 _substitute 只认 <c>$1</c>（它会把 <c>$(\d+)</c> 换成 \g&lt;n&gt;），
        /// 不认 <c>${1}</c>；而 .NET 的 Match.Result 认 <c>$1</c>/<c>${1}</c> 却不认 <c>\1</c>。
        /// 两边都只保留 <c>$n</c>，就不会出现「同一串在本地有效、连引擎后原样输出」的分裂。
        /// 只在正则模式调用：非正则时替换串是字面量，反斜杠和大括号都必须照原样写进文本。
        /// </summary>
        public static string NormalizeReplacement(string replacement)
        {
            if (string.IsNullOrEmpty(replacement)) return replacement ?? "";
            string s = Regex.Replace(replacement, @"\$\{(\d+)\}", @"$$$1");   // ${1} → $1
            s = Regex.Replace(s, @"\\(\d+)", @"$$$1");                        // \1   → $1
            return s;
        }

        /// <summary>转成 .NET 替换串写法：$1 → ${1}（避免 $12 这类多位数被当成组 12）。</summary>
        private static string ToDotNetReplacement(string repl)
            => Regex.Replace(repl ?? "", @"\$(\d+)", @"$${$1}");

        /// <summary>只替换第 index 个匹配；正则替换里 $1 / ${1} / \1 都当作分组引用。</summary>
        public static string SubstituteOnce(Regex re, string text, string replacement, bool useRegex, int index)
        {
            var list = Matches(re, text);
            if (index < 0 || index >= list.Count) return text;
            var m = list[index];
            string repl = replacement ?? "";
            if (useRegex) repl = ToDotNetReplacement(NormalizeReplacement(repl));
            string expanded;
            try { expanded = useRegex ? m.Result(repl) : repl; }
            catch (ArgumentException) { expanded = repl; }
            return text.Substring(0, m.Index) + expanded + text.Substring(m.Index + m.Length);
        }

        /// <summary>替换所有匹配（原型模式下的「全部替换」）。</summary>
        public static string SubstituteAll(Regex re, string text, string replacement, bool useRegex)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var list = Matches(re, text);
            if (list.Count == 0) return text;
            string repl = replacement ?? "";
            if (useRegex) repl = ToDotNetReplacement(NormalizeReplacement(repl));
            var sb = new System.Text.StringBuilder();
            int last = 0;
            foreach (var m in list)
            {
                sb.Append(text, last, m.Index - last);
                try { sb.Append(useRegex ? m.Result(repl) : repl); }
                catch (ArgumentException) { sb.Append(repl); }
                last = m.Index + m.Length;
            }
            sb.Append(text, last, text.Length - last);
            return sb.ToString();
        }
    }
}
