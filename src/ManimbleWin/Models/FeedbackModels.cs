using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ManimbleWin.Models
{
    /// <summary>
    /// 一条批注：幻灯片上某个比例位置的一小块红字。
    /// 字段名/语义逐条对齐 macOS 的 <c>Feedback.swift</c>——两端必须能互相读写同一份 <c>feedback.json</c>。
    /// </summary>
    public sealed class FeedbackNote
    {
        /// <summary>换行宽度占幻灯片宽度的比例（原版 <c>FeedbackNote.defaultWidth</c>）。</summary>
        public const double DefaultWidth = 0.32;

        // JsonPropertyOrder 全部按字母序，因为 macOS 用 .sortedKeys 输出；
        // 写成显式的 order 是为了让"声明顺序即契约"这件事不被无关的文件重排打乱。
        [JsonPropertyOrder(0)] public int Build { get; set; }                 // 写下时所在的 build（0 基）
        // 文件里的键必须叫 created（C# 侧叫 CreatedUtc 只是为了强调它是 UTC），
        // JsonPropertyName 会覆盖 CamelCase 策略，否则会写出 "createdUtc" 让 Mac 端读不到。
        [JsonPropertyOrder(1), JsonPropertyName("created")] public DateTime CreatedUtc { get; set; }
        [JsonPropertyOrder(2)] public string Id { get; set; } = "";            // UUID 大写字符串
        [JsonPropertyOrder(3)] public string Scene { get; set; } = "";         // 必须等于 Slide.Name
        [JsonPropertyOrder(4)] public string Text { get; set; } = "";          // 纯文本，可含换行
        [JsonPropertyOrder(5)] public double W { get; set; } = DefaultWidth;   // 换行宽度（占幻灯片宽）
        [JsonPropertyOrder(6)] public double X { get; set; }                   // 左上角比例 0..0.96
        [JsonPropertyOrder(7)] public double Y { get; set; }                   // 左上角比例 0..0.96（y 向下）

        /// <summary>复制一份。写盘前复制，避免序列化过程中用户继续在 UI 上改同一条。</summary>
        public FeedbackNote Clone() => (FeedbackNote)MemberwiseClone();
    }

    /// <summary><c>feedback.json</c> 的根对象。仅内部使用：对外只暴露 <see cref="FeedbackNote"/> 列表。</summary>
    internal sealed class FeedbackFile
    {
        [JsonPropertyOrder(0)] public List<FeedbackNote> Notes { get; set; } = new List<FeedbackNote>();
        [JsonPropertyOrder(1)] public int Version { get; set; } = 1;
    }

    /// <summary>读取结果。只读闸门在 <c>FeedbackStore</c>，这里只表达"读到了什么"。</summary>
    public enum FeedbackLoadStatus { Ok, Missing, ParseError, UnsupportedVersion }

    /// <summary>
    /// 日期在文件里是 ISO8601 UTC。<b>读要宽容、写要严格</b>：
    /// 写一律 <c>yyyy-MM-ddTHH:mm:ssZ</c>（无毫秒）——Swift 的 <c>JSONDecoder.dateDecodingStrategy = .iso8601</c>
    /// 用 <c>ISO8601DateFormatter</c> 的默认选项，不认小数秒；写了毫秒会让 Mac 端**整文件**解析失败，
    /// 参考实现的 <c>load()</c> 里 <c>?? []</c> 随即把列表当成空的，用户一保存就把我们的批注清空。
    /// 读的时候带毫秒、带偏移、缺时区的写法都收，因为文件可能来自别的手写/工具。
    /// </summary>
    public sealed class Iso8601UtcConverter : JsonConverter<DateTime>
    {
        public const string WriteFormat = "yyyy-MM-ddTHH:mm:ssZ";

        private static readonly string[] ReadFormats =
        {
            "yyyy-MM-ddTHH:mm:ssZ",              // 我们自己写的 / Swift .iso8601
            "yyyy-MM-ddTHH:mm:ss.FFFFFFFZ",      // 带毫秒（Mac 某些版本、第三方工具）
            "yyyy-MM-ddTHH:mm:sszzz",            // 带偏移 +08:00
            "yyyy-MM-ddTHH:mm:ss.FFFFFFFzzz",
            "yyyy-MM-ddTHH:mm:ss",               // 没时区 → 按 UTC 处理
        };

        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string s = reader.GetString();
            if (string.IsNullOrWhiteSpace(s)) return DateTime.UtcNow;

            if (DateTimeOffset.TryParseExact(s.Trim(), ReadFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out DateTimeOffset exact))
                return exact.UtcDateTime;

            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset loose))
                return loose.UtcDateTime;

            // 抛 JsonException → 上层把它当作 ParseError（只读、一个字节都不写），而不是静默当成"没有批注"
            throw new JsonException("无法解析日期：" + s);
        }

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToUniversalTime().ToString(WriteFormat, CultureInfo.InvariantCulture));
    }

    /// <summary>读写共用的唯一一份 JSON 选项。</summary>
    internal static class FeedbackJson
    {
        public static readonly JsonSerializerOptions Options = Create();

        private static JsonSerializerOptions Create()
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,         // id/scene/w/x/y/created —— 必须
                PropertyNameCaseInsensitive = true,                        // 读：容忍 Id/ID
                WriteIndented = true,                                      // .NET 8：2 空格缩进 + '\n'
                // 中文/emoji 不转 \uXXXX：用户会直接打开这个文件核对内容
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,                                // 手改过的文件尽量能读
                DefaultIgnoreCondition = JsonIgnoreCondition.Never,        // 任何字段都不省略
            };
            options.Converters.Add(new Iso8601UtcConverter());
            return options;
        }
    }
}
