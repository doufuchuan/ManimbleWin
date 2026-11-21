using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using ManimbleWin.Models;

namespace ManimbleWin.Services
{
    /// <summary>
    /// 新建 deck：在用户选的位置生成一个可直接编辑、也能被引擎直接 <c>open</c> 的项目文件夹。
    ///
    /// <para>产物结构与导入器一致（对齐 NewDeck.swift:47-84）：</para>
    /// <code>
    /// &lt;父目录&gt;\&lt;名称&gt;\
    /// ├── assets\                 （空目录；引擎 latex_retypeset 之类往里写）
    /// └── deck\
    ///     ├── common.py           （所选主题的源码，含 THEME/BACKGROUND/FONT/TEXT_COLOR/MUTED_COLOR/ACCENT）
    ///     ├── slides.py           （一页标题页）
    ///     ├── order.txt           （"Title"）
    ///     └── manim.cfg           （背景色用主题的 BACKGROUND）
    /// </code>
    ///
    /// <para><b>字体</b>：7 套主题的 FONT 都是 macOS 字体（Helvetica Neue / Avenir Next / Menlo / Chalkboard SE …），
    /// Windows 上大多不存在，manim 会回退到 Sans 并输出 "couldn't load font … expect ugly output"。
    /// 所以生成时探测本机已装字体，缺失就换成本机等价字体（Helvetica Neue→Segoe UI、Menlo→Consolas 等），
    /// 保证新建的 deck 一打开就是这套主题该有的样子。</para>
    /// </summary>
    public static class NewDeckService
    {
        /// <summary>deck 目录名（与引擎/导入器一致）。</summary>
        public const string DeckFolderName = "deck";

        /// <summary>assets 目录名（deck 的兄弟目录，引擎按 <c>Path(deck).parent/"assets"</c> 找它）。</summary>
        public const string AssetsFolderName = "assets";

        /// <summary>标题页的类名，order.txt 里同名。</summary>
        public const string TitleSceneName = "Title";

        /// <summary>macOS 主题字体 → Windows 上语义最接近的等价字体（本机没装就不用）。</summary>
        private static readonly Dictionary<string, string> FontSubstitutes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Helvetica Neue"] = "Segoe UI",
                ["Helvetica"] = "Arial",
                ["Avenir Next"] = "Segoe UI",
                ["Menlo"] = "Consolas",
                ["Chalkboard SE"] = "Comic Sans MS",
                ["Palatino"] = "Palatino Linotype",
            };

        /// <summary>主题文件里的顶层 <c>FONT = "…"</c> 行（行尾允许 CRLF）。</summary>
        private static readonly Regex FontLine = new Regex(
            @"^(?<pre>[ \t]*FONT[ \t]*=[ \t]*)(?<q>[""'])(?<value>.*?)\k<q>[ \t]*\r?$",
            RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);

        private static HashSet<string> _installedFonts;

        /// <summary>目标位置下是否已经有一个 deck（同名项目已存在）。</summary>
        public static bool DeckExists(string parentDir, string name)
        {
            try
            {
                string parent = WorkspaceService.NormalizePath(parentDir);
                string cleanName = (name ?? "").Trim();
                if (cleanName.Length == 0) return false;
                return Directory.Exists(Path.Combine(parent, cleanName, DeckFolderName));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 生成新 deck。
        /// </summary>
        /// <param name="parentDir">项目保存位置（父目录，不存在会被创建）。</param>
        /// <param name="name">项目文件夹名 = deck 名。</param>
        /// <param name="theme">所选主题（null 用不带主题样式的朴素模板）。</param>
        /// <param name="deckPath">成功时输出新 deck 目录绝对路径（<c>&lt;父目录&gt;\&lt;名称&gt;\deck</c>）。</param>
        /// <returns>成功返回 null；失败返回可直接显示的中文原因。</returns>
        public static string Create(string parentDir, string name, DeckTheme theme, out string deckPath)
        {
            deckPath = null;

            string cleanName = (name ?? "").Trim();
            if (string.IsNullOrWhiteSpace(parentDir)) return "请选择演示文稿的保存位置";
            if (cleanName.Length == 0) return "请填写演示文稿的名称";
            if (cleanName == "." || cleanName == "..") return "名称不合法";
            if (cleanName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return "名称里不能包含这些字符：\\ / : * ? \" < > |";
            if (cleanName.EndsWith(".", StringComparison.Ordinal) || cleanName.EndsWith(" ", StringComparison.Ordinal))
                return "名称不能以句点或空格结尾";

            string parent;
            try { parent = WorkspaceService.NormalizePath(parentDir); }
            catch (Exception ex) { return "保存位置不合法：" + ex.Message; }

            string projectRoot = Path.Combine(parent, cleanName);
            string deck = Path.Combine(projectRoot, DeckFolderName);
            if (Directory.Exists(deck))
                return $"「{cleanName}」里已经有一个 deck 了。用「打开 deck 或项目」打开它，或者换一个名称。";

            try
            {
                Directory.CreateDirectory(deck);
                Directory.CreateDirectory(Path.Combine(projectRoot, AssetsFolderName));
                var encoding = new UTF8Encoding(false);
                foreach (KeyValuePair<string, string> file in DeckFiles(cleanName, theme))
                    File.WriteAllText(Path.Combine(deck, file.Key), file.Value, encoding);
            }
            catch (Exception ex)
            {
                return "创建失败：" + ex.Message;
            }

            deckPath = deck;
            return null;
        }

        /// <summary>
        /// <see cref="Create"/> 的布尔版本：同名目录已存在或生成失败都返回 false。
        /// </summary>
        public static bool TryCreate(string parentDir, string name, DeckTheme theme, out string deckPath, out string error)
        {
            error = Create(parentDir, name, theme, out deckPath);
            return error == null;
        }

        /// <summary>要写进 deck/ 的全部文件（文件名 → 内容）。</summary>
        public static IReadOnlyList<KeyValuePair<string, string>> DeckFiles(string title, DeckTheme theme)
        {
            string safeTitle = (title ?? "").Trim();
            if (safeTitle.Length == 0) safeTitle = "Untitled Deck";

            bool themed = theme != null;
            string common = themed ? BuildCommonSource(theme) : PlainCommonSource();
            string background = themed && !string.IsNullOrWhiteSpace(theme.Background) ? theme.Background : "#000000";

            // 有主题：标题用 FONT/TEXT_COLOR、副标题用 FONT/MUTED_COLOR（与主题一致的排版）
            // 没主题：朴素模板，标题用 manim 默认，副标题用 GREY_B（与 macOS 原版一致）
            string titleStyle = themed ? ", font=FONT, color=TEXT_COLOR" : "";
            string subtitleStyle = themed ? ", font=FONT, color=MUTED_COLOR" : ", color=GREY_B";

            string slides =
                "\"\"\"Slides of the deck.\"\"\"\n" +
                "\n" +
                "from common import *\n" +
                "\n" +
                "\n" +
                $"class {TitleSceneName}(Deck):\n" +
                "    def construct(self):\n" +
                $"        title = Text({PyString(safeTitle)}, font_size=64{titleStyle}).move_to((0, 0.7, 0))  # manimble\n" +
                "        self.add(title)  # manimble\n" +
                $"        subtitle = Text(\"Subtitle\", font_size=32{subtitleStyle}).move_to((0, -0.8, 0))  # manimble\n" +
                "        self.add(subtitle)  # manimble\n";

            string manimCfg =
                "[CLI]\n" +
                $"background_color = {background}\n" +
                "pixel_width = 1280\n" +
                "pixel_height = 720\n" +
                "frame_rate = 30\n";

            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("common.py", common),
                new KeyValuePair<string, string>("slides.py", slides),
                new KeyValuePair<string, string>("order.txt", TitleSceneName + "\n"),
                new KeyValuePair<string, string>("manim.cfg", manimCfg),
            };
        }

        /// <summary>用主题自己的 common.py 源码（换掉本机缺失的字体），读不到就用解析出的常量合成一份。</summary>
        private static string BuildCommonSource(DeckTheme theme)
        {
            string raw = ReadThemeSource(theme);
            string source = raw ?? SynthesizeCommonSource(theme);
            return SubstituteFont(source);
        }

        /// <summary>读 <c>engine/manimble/themes/&lt;id&gt;.py</c> 原文；读不到返回 null。</summary>
        private static string ReadThemeSource(DeckTheme theme)
        {
            if (theme == null || string.IsNullOrWhiteSpace(theme.Id)) return null;
            try
            {
                string path = Path.Combine(DeckThemeService.ThemesDir, theme.Id + ".py");
                if (!File.Exists(path)) return null;
                string text = File.ReadAllText(path);
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>主题文件读不到时，用 <see cref="DeckTheme"/> 里解析出的 6 个常量合成 common.py。</summary>
        private static string SynthesizeCommonSource(DeckTheme theme)
        {
            var sb = new StringBuilder();
            sb.Append("\"\"\"Shared base class and theme of the deck");
            sb.Append(string.IsNullOrWhiteSpace(theme.Name) ? "" : " (" + theme.Name + ")");
            sb.Append(".\"\"\"\n\n");
            sb.Append("from pathlib import Path\n\n");
            sb.Append("from manim import *\n");
            sb.Append("from manim_slides import Slide\n\n");
            sb.Append("ASSETS = Path(__file__).resolve().parent.parent / \"assets\"\n\n");
            sb.Append("THEME = ").Append(PyString(theme.Name ?? "")).Append('\n');
            sb.Append("BACKGROUND = ").Append(PyString(theme.Background ?? "#000000")).Append('\n');
            if (!string.IsNullOrWhiteSpace(theme.Font)) sb.Append("FONT = ").Append(PyString(theme.Font)).Append('\n');
            if (!string.IsNullOrWhiteSpace(theme.Text)) sb.Append("TEXT_COLOR = ").Append(PyString(theme.Text)).Append('\n');
            if (!string.IsNullOrWhiteSpace(theme.Muted)) sb.Append("MUTED_COLOR = ").Append(PyString(theme.Muted)).Append('\n');
            if (!string.IsNullOrWhiteSpace(theme.Accent)) sb.Append("ACCENT = ").Append(PyString(theme.Accent)).Append('\n');
            sb.Append("\n\nclass Deck(Slide):\n    skip_reversing = True\n");
            return sb.ToString();
        }

        /// <summary>不带主题的朴素 common.py（与导入器写的一样）。</summary>
        private static string PlainCommonSource()
        {
            return
                "\"\"\"Shared base class for the deck.\"\"\"\n" +
                "\n" +
                "from pathlib import Path\n" +
                "\n" +
                "from manim import *\n" +
                "from manim_slides import Slide\n" +
                "\n" +
                "ASSETS = Path(__file__).resolve().parent.parent / \"assets\"\n" +
                "\n" +
                "\n" +
                "class Deck(Slide):\n" +
                "    skip_reversing = True\n";
        }

        /// <summary>把 common.py 里本机没装的 FONT 换成本机等价字体（找不到等价字体就用 Segoe UI / Arial）。</summary>
        private static string SubstituteFont(string source)
        {
            if (string.IsNullOrEmpty(source)) return source;
            HashSet<string> installed = InstalledFonts();
            if (installed.Count == 0) return source;                  // 探测不到字体表就别乱改

            return FontLine.Replace(source, match =>
            {
                string value = match.Groups["value"].Value;
                string resolved = ResolveFont(value, installed);
                if (string.Equals(resolved, value, StringComparison.Ordinal)) return match.Value;
                string quote = match.Groups["q"].Value;
                return match.Groups["pre"].Value + quote + resolved + quote;
            });
        }

        private static string ResolveFont(string font, HashSet<string> installed)
        {
            if (string.IsNullOrWhiteSpace(font)) return font;
            if (installed.Contains(font)) return font;
            if (FontSubstitutes.TryGetValue(font, out string mapped) && installed.Contains(mapped)) return mapped;
            if (installed.Contains("Segoe UI")) return "Segoe UI";
            if (installed.Contains("Arial")) return "Arial";
            return font;
        }

        /// <summary>
        /// 本机已安装的字体族名（读 Windows 字体注册表；值名形如 <c>Segoe UI (TrueType)</c>）。
        /// 只用来判断某个字体在不在，不参与渲染。
        /// </summary>
        private static HashSet<string> InstalledFonts()
        {
            if (_installedFonts != null) return _installedFonts;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ReadFontKey(Microsoft.Win32.Registry.LocalMachine, set);
            ReadFontKey(Microsoft.Win32.Registry.CurrentUser, set);
            _installedFonts = set;
            return set;
        }

        private static void ReadFontKey(Microsoft.Win32.RegistryKey hive, HashSet<string> set)
        {
            try
            {
                using (var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts"))
                {
                    if (key == null) return;
                    foreach (string name in key.GetValueNames())
                    {
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        int cut = name.IndexOf(" (", StringComparison.Ordinal);
                        string family = (cut > 0 ? name.Substring(0, cut) : name).Trim();
                        if (family.Length > 0) set.Add(family);
                    }
                }
            }
            catch
            {
                // 注册表读不到（受限环境）就当探测失败，保留主题原字体
            }
        }

        /// <summary>Python 字符串字面量（双引号，转义反斜杠与双引号）。</summary>
        private static string PyString(string text)
        {
            string value = text ?? "";
            value = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
            value = value.Replace("\r", " ").Replace("\n", " ");
            return "\"" + value + "\"";
        }
    }
}
