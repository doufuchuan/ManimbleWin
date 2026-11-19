using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace ManimbleWin.Helpers
{
    /// <summary>
    /// 轻量 Python 语法高亮：把源码切成一行的 <see cref="Paragraph"/>，
    /// 每一段再按 token 上色。支持跨行字符串（docstring）、注释、数字、
    /// 关键字、内置名、Manim 常用类名、装饰器、def/class 名称。
    /// </summary>
    public static class PythonHighlighter
    {
        public enum TokenKind { Plain, Keyword, Control, String, Comment, Number, Builtin, ClassName, Function, Decorator, Self }

        public struct Span
        {
            public int Start;
            public int Length;
            public TokenKind Kind;
        }

        private static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "and", "as", "assert", "async", "await", "break", "continue", "del", "elif", "else",
            "except", "finally", "for", "from", "global", "if", "import", "in", "is", "lambda",
            "nonlocal", "not", "or", "pass", "raise", "return", "try", "while", "with", "yield",
            "match", "case"
        };

        private static readonly HashSet<string> Declarations = new HashSet<string>(StringComparer.Ordinal)
        { "def", "class", "import", "from", "as", "return", "global", "nonlocal" };

        private static readonly HashSet<string> Constants = new HashSet<string>(StringComparer.Ordinal)
        { "True", "False", "None", "self", "cls" };

        private static readonly HashSet<string> Builtins = new HashSet<string>(StringComparer.Ordinal)
        {
            "print", "len", "range", "enumerate", "zip", "str", "int", "float", "bool", "list",
            "dict", "set", "tuple", "sorted", "sum", "min", "max", "abs", "round", "open",
            "isinstance", "getattr", "setattr", "super", "type", "map", "filter", "any", "all",
            "math", "np", "numpy", "plt", "np", "sys", "os", "platform", "json", "random"
        };

        /// <summary>Manim 里出现频率最高的名字，单独给一个类名色，读代码更快。</summary>
        private static readonly HashSet<string> ManimNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Scene", "MovingCameraScene", "ThreeDScene", "Slide", "Deck", "Mobject", "VMobject",
            "VGroup", "Group", "Text", "MathTex", "Tex", "MarkupText", "MathTable", "Table",
            "ImageMobject", "SVGMobject", "Square", "Rectangle", "Circle", "Ellipse", "Triangle",
            "Polygon", "Line", "Arrow", "DoubleArrow", "DashedLine", "Dot", "Axes", "NumberPlane",
            "NumberLine", "Brace", "SurroundingRectangle", "Create", "Uncreate", "Write",
            "Unwrite", "FadeIn", "FadeOut", "Transform", "ReplacementTransform", "TransformMatchingTex",
            "Indicate", "Flash", "Circumscribe", "Rotate", "MoveAlongPath", "ApplyWave", "AnimationGroup",
            "Succession", "LaggedStart", "always_redraw", "ValueTracker", "DecimalNumber", "label",
            "WHITE", "BLACK", "GREY", "GREY_B", "RED", "GREEN", "BLUE", "YELLOW", "ORANGE",
            "PURPLE", "PINK", "TEAL", "GOLD", "ACCENT", "UP", "DOWN", "LEFT", "RIGHT",
            "ORIGIN", "UL", "UR", "DL", "DR", "PI", "TAU", "DEGREES", "BOLD", "ITALIC"
        };

        public static double LineHeight(double fontSize) => Math.Round(fontSize * 1.46);

        /// <summary>按行生成带高亮的 FlowDocument。</summary>
        public static FlowDocument Build(string text, double fontSize, FontFamily family, bool dark)
        {
            var palette = dark ? DarkPalette : LightPalette;
            var doc = new FlowDocument
            {
                PageWidth = 4000,
                PagePadding = new Thickness(0),
                FontFamily = family,
                FontSize = fontSize,
                Foreground = palette[TokenKind.Plain],
                LineHeight = LineHeight(fontSize),
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextAlignment = TextAlignment.Left
            };

            string normalized = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            var lines = normalized.Split('\n');
            bool inTriple = false;
            string tripleQuote = null;

            foreach (string raw in lines)
            {
                var p = new Paragraph
                {
                    Margin = new Thickness(0),
                    LineHeight = LineHeight(fontSize),
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight
                };

                var spans = TokenizeLine(raw, ref inTriple, ref tripleQuote);
                if (spans.Count == 0)
                {
                    p.Inlines.Add(new Run(" ") { Foreground = palette[TokenKind.Plain] });
                }
                else
                {
                    foreach (var span in spans)
                    {
                        p.Inlines.Add(new Run(raw.Substring(span.Start, span.Length))
                        {
                            Foreground = palette[span.Kind]
                        });
                    }
                }
                doc.Blocks.Add(p);
            }
            return doc;
        }

        private static readonly Dictionary<TokenKind, Brush> DarkPalette = new Dictionary<TokenKind, Brush>
        {
            [TokenKind.Plain] = Freeze("#D4D4D4"),
            [TokenKind.Keyword] = Freeze("#C586C0"),
            [TokenKind.Control] = Freeze("#C586C0"),
            [TokenKind.String] = Freeze("#CE9178"),
            [TokenKind.Comment] = Freeze("#6A9955"),
            [TokenKind.Number] = Freeze("#B5CEA8"),
            [TokenKind.Builtin] = Freeze("#4EC9B0"),
            [TokenKind.ClassName] = Freeze("#4EC9B0"),
            [TokenKind.Function] = Freeze("#DCDCAA"),
            [TokenKind.Decorator] = Freeze("#DCDCAA"),
            [TokenKind.Self] = Freeze("#9CDCFE")
        };

        private static readonly Dictionary<TokenKind, Brush> LightPalette = new Dictionary<TokenKind, Brush>
        {
            [TokenKind.Plain] = Freeze("#1F1F1F"),
            [TokenKind.Keyword] = Freeze("#AF00DB"),
            [TokenKind.Control] = Freeze("#AF00DB"),
            [TokenKind.String] = Freeze("#A31515"),
            [TokenKind.Comment] = Freeze("#008000"),
            [TokenKind.Number] = Freeze("#098658"),
            [TokenKind.Builtin] = Freeze("#267F99"),
            [TokenKind.ClassName] = Freeze("#267F99"),
            [TokenKind.Function] = Freeze("#795E26"),
            [TokenKind.Decorator] = Freeze("#795E26"),
            [TokenKind.Self] = Freeze("#0070C1")
        };

        private static Brush Freeze(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        /// <summary>把一行切成 span 列表（连续同色的字符合并成一段）。</summary>
        public static List<Span> TokenizeLine(string line, ref bool inTriple, ref string tripleQuote)
        {
            var spans = new List<Span>();
            if (string.IsNullOrEmpty(line)) return spans;

            int i = 0;
            while (i < line.Length)
            {
                char c = line[i];

                // 跨行字符串（docstring）
                if (inTriple)
                {
                    int end = line.IndexOf(tripleQuote, i, StringComparison.Ordinal);
                    int stop = end < 0 ? line.Length : end + 3;
                    Add(spans, i, stop - i, TokenKind.String);
                    if (end >= 0) { inTriple = false; tripleQuote = null; }
                    i = stop;
                    continue;
                }

                // 注释
                if (c == '#')
                {
                    Add(spans, i, line.Length - i, TokenKind.Comment);
                    break;
                }

                // 字符串（含 r"", f"", b"" 之类前缀）
                if (c == '"' || c == '\'')
                {
                    int start = i;
                    int prefix = StringPrefixStart(line, i);
                    if (prefix >= 0) start = prefix;

                    if (i + 2 < line.Length && line[i + 1] == c && line[i + 2] == c)
                    {
                        string quote = new string(c, 3);
                        int end = line.IndexOf(quote, i + 3, StringComparison.Ordinal);
                        if (end < 0)
                        {
                            Add(spans, start, line.Length - start, TokenKind.String);
                            inTriple = true;
                            tripleQuote = quote;
                            i = line.Length;
                            continue;
                        }
                        Add(spans, start, end + 3 - start, TokenKind.String);
                        i = end + 3;
                        continue;
                    }

                    int j = i + 1;
                    while (j < line.Length)
                    {
                        if (line[j] == '\\') { j += 2; continue; }
                        if (line[j] == c) { j++; break; }
                        j++;
                    }
                    Add(spans, start, Math.Min(j, line.Length) - start, TokenKind.String);
                    i = Math.Min(j, line.Length);
                    continue;
                }

                // 装饰器
                if (c == '@' && IsLineStart(line, i))
                {
                    int j = i + 1;
                    while (j < line.Length && (char.IsLetterOrDigit(line[j]) || line[j] == '_' || line[j] == '.')) j++;
                    Add(spans, i, j - i, TokenKind.Decorator);
                    i = j;
                    continue;
                }

                // 数字
                if (char.IsDigit(c))
                {
                    int j = i;
                    while (j < line.Length && (char.IsLetterOrDigit(line[j]) || line[j] == '.' || line[j] == '_')) j++;
                    Add(spans, i, j - i, TokenKind.Number);
                    i = j;
                    continue;
                }

                // 标识符
                if (char.IsLetter(c) || c == '_')
                {
                    int j = i;
                    while (j < line.Length && (char.IsLetterOrDigit(line[j]) || line[j] == '_')) j++;
                    string word = line.Substring(i, j - i);
                    Add(spans, i, j - i, Classify(line, word, i, j));
                    i = j;
                    continue;
                }

                Add(spans, i, 1, TokenKind.Plain);
                i++;
            }
            return spans;
        }

        private static TokenKind Classify(string line, string word, int start, int end)
        {
            if (word == "self" || word == "cls") return TokenKind.Self;
            if (Constants.Contains(word)) return TokenKind.Keyword;
            if (Keywords.Contains(word)) return Declarations.Contains(word) ? TokenKind.Control : TokenKind.Control;
            if (ManimNames.Contains(word)) return TokenKind.ClassName;
            if (Builtins.Contains(word)) return TokenKind.Builtin;

            // def xxx( / class Xxx 后面的名字
            string before = line.Substring(0, start).TrimEnd();
            if (before.EndsWith("def", StringComparison.Ordinal) || before.EndsWith("class", StringComparison.Ordinal))
                return TokenKind.Function;

            // 后面紧跟 ( 的，按函数调用着色
            int k = end;
            while (k < line.Length && line[k] == ' ') k++;
            if (k < line.Length && line[k] == '(') return TokenKind.Function;

            return TokenKind.Plain;
        }

        private static int StringPrefixStart(string line, int quoteIndex)
        {
            int j = quoteIndex - 1;
            while (j >= 0 && (line[j] == 'r' || line[j] == 'R' || line[j] == 'b' || line[j] == 'B'
                              || line[j] == 'f' || line[j] == 'F' || line[j] == 'u' || line[j] == 'U')) j--;
            if (j == quoteIndex - 1) return -1;                    // 没有前缀
            if (j >= 0 && (char.IsLetterOrDigit(line[j]) || line[j] == '_')) return -1; // 是别的标识符的一部分
            return j + 1;
        }

        private static bool IsLineStart(string line, int i)
        {
            for (int k = 0; k < i; k++)
                if (!char.IsWhiteSpace(line[k])) return false;
            return true;
        }

        private static void Add(List<Span> spans, int start, int length, TokenKind kind)
        {
            if (length <= 0) return;
            if (spans.Count > 0)
            {
                var last = spans[spans.Count - 1];
                if (last.Kind == kind && last.Start + last.Length == start)
                {
                    last.Length += length;
                    spans[spans.Count - 1] = last;
                    return;
                }
            }
            spans.Add(new Span { Start = start, Length = length, Kind = kind });
        }
    }
}
