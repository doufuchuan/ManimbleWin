using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace ManimbleWin.Helpers
{
    /// <summary>
    /// PowerShell 的 stderr 一旦被重定向到管道，就会把错误 / 进度记录序列化成 CLIXML：
    /// <code>
    /// #&lt; CLIXML
    /// &lt;Objs Version="1.1.0.1" ...&gt;&lt;Obj S="progress" ...&gt;…&lt;/Obj&gt;
    /// &lt;S S="Error"&gt;文本_x000D__x000A_&lt;/S&gt;&lt;/Objs&gt;
    /// </code>
    /// 直接显示是一团标签。这里把 Error 记录还原成普通文本，progress 记录（如
    /// “正在准备首次使用模块”）直接丢弃。
    /// </summary>
    public sealed class ClixmlFilter
    {
        private const string Header = "#< CLIXML";
        private const string Footer = "</Objs>";

        private static readonly Regex ErrorRegex =
            new Regex("<S S=\"Error\">(.*?)</S>", RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex EscapeSeq =
            new Regex(@"_x([0-9A-Fa-f]{4})_", RegexOptions.Compiled);

        private readonly StringBuilder _pending = new StringBuilder();
        private bool _inBlock;

        public bool InBlock => _inBlock;

        /// <summary>喂入一行（不含换行）。返回应当显示的行；空列表表示整行被吞掉。</summary>
        public List<string> Feed(string line)
        {
            line = line ?? "";

            if (!_inBlock)
            {
                string probe = line.TrimStart('\uFEFF', ' ', '\t');
                if (probe.StartsWith(Header, StringComparison.Ordinal))
                {
                    _inBlock = true;
                    _pending.Clear();
                    _pending.Append(line.Substring(line.IndexOf(Header, StringComparison.Ordinal) + Header.Length));
                    return TryClose();
                }
                return new List<string> { line };
            }

            _pending.Append('\n').Append(line);
            return TryClose();
        }

        /// <summary>进程结束时把没等到 &lt;/Objs&gt; 的残留也吐出来。</summary>
        public List<string> Flush()
        {
            if (!_inBlock) return new List<string>();
            _inBlock = false;
            var res = Parse(_pending.ToString());
            _pending.Clear();
            return res;
        }

        private List<string> TryClose()
        {
            string all = _pending.ToString();
            int end = all.IndexOf(Footer, StringComparison.Ordinal);
            if (end < 0) return new List<string>();

            string block = all.Substring(0, end);
            string rest = all.Substring(end + Footer.Length).Trim('\uFEFF', '\r', '\n', ' ', '\t');

            _pending.Clear();
            _inBlock = false;

            var res = Parse(block);
            if (rest.Length > 0) res.Add(rest);
            return res;
        }

        private static List<string> Parse(string block)
        {
            var lines = new List<string>();
            foreach (Match m in ErrorRegex.Matches(block))
            {
                string text = Unescape(m.Groups[1].Value);
                foreach (string raw in text.Split('\n'))
                {
                    string t = raw.Replace("\r", "").TrimEnd();
                    if (t.Trim().Length == 0) continue;
                    lines.Add(t);
                }
            }
            return lines;
        }

        private static string Unescape(string s)
        {
            s = s.Replace("&lt;", "<")
                 .Replace("&gt;", ">")
                 .Replace("&quot;", "\"")
                 .Replace("&apos;", "'")
                 .Replace("&amp;", "&");
            // _x000D__x000A_ 这类转义就是 CR/LF，还原后按行拆开
            s = EscapeSeq.Replace(s, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
            return s;
        }
    }
}
