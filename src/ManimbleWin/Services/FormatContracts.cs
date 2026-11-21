using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace ManimbleWin.Services
{
    /// <summary>
    /// 格式作用域：决定「哪些对象能用这组格式」。
    /// <para>为什么拆成三个值而不是 PRD 里的两个：Bold / Italic 在引擎里走的是 <c>style</c> 的
    /// <c>weight</c> / <c>slant</c>，这两个是 <c>Text</c> 专有参数，<c>MathTex</c> 不接受，
    /// 一旦写进公式的创建调用，渲染阶段必然 TypeError 并被引擎回滚。所以字重必须单独一档，
    /// 只对 <see cref="Models.ObjectKind.Text"/> 开放；下划线 / 删除线 / 高亮 / 字色 / 清除格式
    /// 走的是 <c>decorate</c> / <c>highlight</c> / <c>color</c> / <c>clear_format</c>，公式也支持。</para>
    /// </summary>
    public enum FormatScope
    {
        /// <summary>字符级且公式也支持：下划线、删除线、高亮、字色、清除格式。</summary>
        Character,

        /// <summary>字重与倾斜：仅 Text（Tex 没有字重概念，见类型注释）。</summary>
        Weight,

        /// <summary>仅 Text：列表、编号、缩进、上/下标、行距、分栏、段落对齐、字体、字号。</summary>
        TextOnly
    }

    /// <summary>
    /// 一次单对象格式请求。<c>oid</c> 与 <c>stop</c> 在构造时就写进 <see cref="Args"/>，
    /// 因为引擎的 <c>edit</c> RPC 把它们当普通字段读；<c>scene</c> 与 <c>op</c> 由
    /// <c>EngineIntegration.EditOneAsync</c> 补齐（与既有的 <c>Edit(scene, op, args)</c> 一致）。
    /// </summary>
    public sealed class FormatRequest
    {
        /// <summary>引擎的 edit op 名：style / color / decorate / highlight / align / spacing / columns / clear_format / font / text。</summary>
        public string Op { get; }

        /// <summary>目标对象 oid（引擎协议里是 objects 的数字 key，这里一律保持字符串原样回传）。</summary>
        public string Oid { get; }

        /// <summary>作用到第几个 build（本轮恒为当前 build 索引）。</summary>
        public int Stop { get; }

        /// <summary>摊平后直接发给引擎的字段表。</summary>
        public Dictionary<string, object> Args { get; }

        public FormatRequest(string op, string oid, int stop)
        {
            if (string.IsNullOrEmpty(op)) throw new ArgumentException("op 不能为空", nameof(op));
            Op = op;
            Oid = oid ?? "";
            Stop = stop;
            Args = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["oid"] = oid ?? "",
                ["stop"] = stop
            };
        }

        /// <summary>追加/覆盖一个字段（链式，便于工厂方法内部复用）。</summary>
        public FormatRequest With(string key, object value)
        {
            if (string.IsNullOrEmpty(key)) return this;
            Args[key] = value;
            return this;
        }

        /// <summary>
        /// 换一个 oid 提交。
        /// <para>为什么需要：引擎在每次渲染后重新给对象编号，一次批量操作里第一个对象若插入了一行
        /// （下划线与删除线是 <c>t.add(underline(t))</c>，会多出对象），后面对象的 oid 就整体漂移，
        /// 继续用旧 oid 会打空。批量执行器据此把请求改投到重新认领到的那个 oid 上。</para>
        /// </summary>
        public FormatRequest Retarget(string oid)
        {
            if (string.IsNullOrEmpty(oid)) return this;
            Args["oid"] = oid;
            return this;
        }

        /// <summary>
        /// 字重 / 倾斜 / 字体族：三者共用 <c>style</c>。
        /// 只把非 null 的项写进去 —— 传 null 会让引擎以为要清空那个参数。
        /// <paramref name="shared"/> 与 macOS 原版一致取 true：同源（共享样式）的一组文本一起换，
        /// 否则同一段文字里会出现两种字重。
        /// </summary>
        public static FormatRequest Style(string oid, int stop, string font = null,
                                          string weight = null, string slant = null, bool shared = true)
        {
            var req = new FormatRequest("style", oid, stop).With("shared", shared);
            if (!string.IsNullOrEmpty(font)) req.With("font", font);
            if (!string.IsNullOrEmpty(weight)) req.With("weight", weight);
            if (!string.IsNullOrEmpty(slant)) req.With("slant", slant);
            return req;
        }

        /// <summary>字体颜色，<paramref name="hex"/> 必须是归一化后的 #RRGGBB。</summary>
        public static FormatRequest Color(string oid, int stop, string hex)
            => new FormatRequest("color", oid, stop).With("value", hex);

        /// <summary>下划线 / 删除线开关。<paramref name="kind"/> 只接受 underline 或 strike。</summary>
        public static FormatRequest Decorate(string oid, int stop, string kind, bool on)
            => new FormatRequest("decorate", oid, stop).With("kind", kind).With("on", on);

        /// <summary>
        /// 高亮。<paramref name="hexOrNull"/> 为 null 表示移除：引擎看到没有 color 就删掉
        /// <c>add_background_rectangle</c> 链调用。opacity 与 macOS 原版一致取 0.6。
        /// </summary>
        public static FormatRequest Highlight(string oid, int stop, string hexOrNull, double opacity = 0.6)
        {
            var req = new FormatRequest("highlight", oid, stop).With("opacity", opacity);
            if (!string.IsNullOrEmpty(hexOrNull)) req.With("color", hexOrNull);
            return req;
        }

        /// <summary>段落对齐：left / center / right（justify 被引擎明确拒绝，不提供）。</summary>
        public static FormatRequest Align(string oid, int stop, string value)
            => new FormatRequest("align", oid, stop).With("value", value);

        /// <summary>行距倍数。<paramref name="value"/> 为 null 时引擎移除 line_spacing（回到默认）。</summary>
        public static FormatRequest Spacing(string oid, int stop, double? value)
        {
            var req = new FormatRequest("spacing", oid, stop);
            if (value.HasValue) req.With("value", value.Value);
            return req;
        }

        /// <summary>分栏数 1–4。</summary>
        public static FormatRequest Columns(string oid, int stop, int n)
            => new FormatRequest("columns", oid, stop).With("value", n);

        /// <summary>清除格式：一次移除字重/倾斜/字色/逐段样式/行距/高亮/下划线/删除线。</summary>
        public static FormatRequest ClearFormat(string oid, int stop)
            => new FormatRequest("clear_format", oid, stop);

        /// <summary>字号绝对值（引擎按 font_size 写）。</summary>
        public static FormatRequest FontSize(string oid, int stop, double value)
            => new FormatRequest("font", oid, stop).With("value", value);

        /// <summary>
        /// 改写文本内容（列表 / 编号 / 缩进 / 上下标都靠它落地）。
        /// <c>shared</c> 取 true：与字重一致，同源文本一起改，避免同一段字出现两套内容。
        /// </summary>
        public static FormatRequest Text(string oid, int stop, string value, bool shared = true)
            => new FormatRequest("text", oid, stop).With("value", value ?? "").With("shared", shared);
    }

    /// <summary>
    /// 单次 edit 的结果。<see cref="Ok"/> 为 false 时 <see cref="Error"/> 是引擎 error 原文，
    /// 直接给用户看（不要翻译、不要截断：里面常带「该文本由辅助函数生成」这类关键线索）。
    /// </summary>
    public sealed class EditOutcome
    {
        public bool Ok { get; }
        public string Error { get; }

        /// <summary>引擎原始回复，留给排错与后续扩展（当前只读不解析）。</summary>
        public JsonElement Raw { get; }

        private EditOutcome(bool ok, string error, JsonElement raw)
        {
            Ok = ok;
            Error = error;
            Raw = raw;
        }

        public static EditOutcome Success(JsonElement raw) => new EditOutcome(true, null, raw);

        public static EditOutcome Fail(string error, JsonElement raw)
            => new EditOutcome(false, string.IsNullOrEmpty(error) ? "引擎未说明原因" : error, raw);
    }

    /// <summary>取色面板的一个预设色块。</summary>
    public sealed class PaletteSwatch
    {
        /// <summary>大写 #RRGGBB。</summary>
        public string Hex { get; }

        /// <summary>中文色名，用作色块的 ToolTip。</summary>
        public string Name { get; }

        public PaletteSwatch(string hex, string name)
        {
            Hex = hex;
            Name = name;
        }
    }

    /// <summary>
    /// 取色面板的预设色板与颜色字符串归一化。
    /// 全应用的颜色都必须是「大写 #RRGGBB」：用户输入、引擎回填、预设色三者都过这里，
    /// 否则同一个黄色会以三种写法出现在比较里，色块的勾选态就飘。
    /// </summary>
    public static class FormatPalette
    {
        /// <summary>8 个预设色，顺序即 UI 顺序（与 macOS 原版一致）。</summary>
        public static IReadOnlyList<PaletteSwatch> Presets { get; } = new[]
        {
            new PaletteSwatch("#FFD400", "黄"),
            new PaletteSwatch("#7CFC00", "绿"),
            new PaletteSwatch("#40E0FF", "青"),
            new PaletteSwatch("#FF6EC7", "粉"),
            new PaletteSwatch("#4D7CFF", "蓝"),
            new PaletteSwatch("#FF4040", "红"),
            new PaletteSwatch("#FF9A2E", "橙"),
            new PaletteSwatch("#8A8A8A", "灰")
        };

        /// <summary>
        /// 把用户输入或引擎回填值归一化为大写 <c>#RRGGBB</c>。
        /// 允许省略前导 #（用户手输「12ab34」是常态），但不接受 3 位缩写与 8 位带 alpha 写法：
        /// 引擎只认 6 位，多一位会静默写进源码然后渲染失败。
        /// </summary>
        /// <param name="input">用户输入或引擎值，可为 null。</param>
        /// <param name="ok">解析是否成功。</param>
        /// <returns>归一化后的颜色；失败返回 null。</returns>
        public static string NormalizeHex(string input, out bool ok)
        {
            ok = false;
            string s = (input ?? "").Trim();
            if (s.StartsWith("#", StringComparison.Ordinal)) s = s.Substring(1);
            if (s.Length != 6) return null;
            foreach (char c in s)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return null;
            }
            ok = true;
            return "#" + s.ToUpperInvariant();
        }

        /// <summary>
        /// 大小写不敏感地比较两个颜色（允许任一侧省略 #）。
        /// 用一个方法收口，避免各处写 <c>string.Equals(..., OrdinalIgnoreCase)</c> 时忘了去掉 #。
        /// </summary>
        public static bool Matches(string current, string preset)
        {
            string a = NormalizeHex(current, out bool okA);
            string b = NormalizeHex(preset, out bool okB);
            return okA && okB && string.Equals(a, b, StringComparison.Ordinal);
        }

        /// <summary>
        /// 行距下拉的可选档位。第一项是占位「默认」（选它 = 移除 line_spacing），
        /// 不显示成 1.0 —— 否则用户会以为默认行距就是 1.0，而源码里其实没有这个参数。
        /// </summary>
        public static IReadOnlyList<double> LineSpacingSteps { get; } = new[] { 1.0, 1.15, 1.5, 2.0, 2.5, 3.0 };

        /// <summary>把行距倍数格式化成下拉里显示的文字（1.5 → "1.5"，2.0 → "2"）。</summary>
        public static string FormatSpacing(double v)
            => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
