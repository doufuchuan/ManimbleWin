using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ManimbleWin.Models;

namespace ManimbleWin.Services
{
    /// <summary>
    /// 读 <c>engine/manimble/themes/*.py</c> 得到 deck 主题清单。
    ///
    /// 为什么要自己扫文件：rpc 没有「列出所有主题」的命令，<c>theme</c> 只认具体 id
    /// （见 engine/manimble/theme.py 的 <c>load()</c>）。macOS 版也是直接扫这个目录
    /// （DeckThemes.swift 的 <c>DeckTheme.all</c>），这里与它保持一致。
    ///
    /// 解析只认行首的顶层 <c>KEY = "值"</c> 赋值，不使用 eval / exec：
    /// 主题文件会被 manim 导入，直接执行既慢又危险；而常量就是纯字面量。
    /// </summary>
    public static class DeckThemeService
    {
        /// <summary>theme.py 认的 6 个常量（KEYS）；THEME 缺了文件就不算主题。</summary>
        private static readonly string[] Keys =
            { "THEME", "BACKGROUND", "FONT", "TEXT_COLOR", "MUTED_COLOR", "ACCENT" };

        /// <summary>一行顶层常量：KEY = "值" / KEY = '值'，行尾可有注释。允许前导缩进外的整行注释。</summary>
        private static readonly Regex AssignLine = new Regex(
            @"^\s*(?<key>[A-Z][A-Z0-9_]*)\s*=\s*(?<q>[""'])(?<value>.*?)\k<q>\s*(#.*)?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static string ThemesDir => Path.Combine(EnvironmentService.EngineDir, "manimble", "themes");

        /// <summary>
        /// 全部主题，Midnight 在最前（它是旧版的黑底默认），其余按显示名排序。
        /// 目录不存在或全部解析失败时返回空列表，由调用方决定兜底。
        /// </summary>
        public static IReadOnlyList<DeckTheme> LoadAll(string dir = null)
        {
            string root = dir ?? ThemesDir;
            var list = new List<DeckTheme>();
            if (!Directory.Exists(root)) return list;

            foreach (string file in Directory.EnumerateFiles(root, "*.py", SearchOption.TopDirectoryOnly))
            {
                DeckTheme theme = Parse(file);
                if (theme != null) list.Add(theme);
            }

            return list
                .OrderBy(t => t.Id == "midnight" ? 0 : 1)
                .ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>按 id 取一套主题；不存在返回 null。</summary>
        public static DeckTheme Load(string id, string dir = null)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            string path = Path.Combine(dir ?? ThemesDir, id + ".py");
            return File.Exists(path) ? Parse(path) : null;
        }

        /// <summary>解析一个主题文件；缺 THEME 或 BACKGROUND 不算主题（与 theme.py 的 load() 一致）。</summary>
        public static DeckTheme Parse(string path)
        {
            string text;
            try { text = File.ReadAllText(path); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }

            var values = ReadConstants(text);
            if (!values.TryGetValue("THEME", out string name) || string.IsNullOrWhiteSpace(name)) return null;
            if (!values.TryGetValue("BACKGROUND", out string background) || string.IsNullOrWhiteSpace(background)) return null;

            return new DeckTheme
            {
                Id = Path.GetFileNameWithoutExtension(path),
                Name = name,
                Background = background,
                Font = Get(values, "FONT"),
                Text = Get(values, "TEXT_COLOR"),
                Muted = Get(values, "MUTED_COLOR"),
                Accent = Get(values, "ACCENT"),
            };
        }

        private static string Get(IDictionary<string, string> map, string key)
            => map.TryGetValue(key, out string v) ? v : "";

        /// <summary>抽出主题文件里的顶层字符串常量。整行注释与行尾注释都会被忽略。</summary>
        public static Dictionary<string, string> ReadConstants(string text)
        {
            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string raw in (text ?? "").Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                string trimmed = line.TrimStart();
                if (trimmed.Length == 0 || trimmed[0] == '#') continue;

                Match m = AssignLine.Match(line);
                if (!m.Success) continue;
                string key = m.Groups["key"].Value;
                if (Array.IndexOf(Keys, key) < 0) continue;
                found[key] = m.Groups["value"].Value;
            }
            return found;
        }

        /// <summary>
        /// 背景色是否为浅色，算法与 theme.py 的 <c>is_light()</c> 相同（相对亮度 &gt; 0.5）。
        /// 只用于界面上的「浅色 / 深色」角标；跨明暗的实际判断由引擎的 <c>crosses</c> 给出。
        /// </summary>
        public static bool IsLight(string color)
        {
            string c = (color ?? "#000000").Trim('"', '\'');
            if (c.Length == 0) c = "#000000";
            if (c[0] != '#') return IsLightNamed(c);

            string s = c.Substring(1);
            if (s.Length == 3) s = string.Concat(s.Select(ch => new string(ch, 2)));
            if (s.Length < 6) return false;
            if (!int.TryParse(s.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int r) ||
                !int.TryParse(s.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int g) ||
                !int.TryParse(s.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int b))
                return false;

            double luminance = 0.2126 * (r / 255.0) + 0.7152 * (g / 255.0) + 0.0722 * (b / 255.0);
            return luminance > 0.5;
        }

        /// <summary>manim 的灰阶名字里属于浅色的那些（与 theme.py 的 is_light() 列表一致）。</summary>
        private static bool IsLightNamed(string name)
        {
            switch (name.Trim().ToUpperInvariant())
            {
                case "WHITE":
                case "GRAY_A":
                case "GREY_A":
                case "LIGHTER_GRAY":
                case "LIGHTER_GREY":
                case "LIGHT_GRAY":
                case "LIGHT_GREY":
                    return true;
                default:
                    return false;
            }
        }
    }
}
