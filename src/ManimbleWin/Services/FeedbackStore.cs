using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.Json;
using ManimbleWin.Models;

namespace ManimbleWin.Services
{
    /// <summary>
    /// 一个 deck 的批注库：内存是唯一数据源，任何变更立即**整文件重写**。
    /// <para>读写只发生在<b>工作副本</b>（<c>Save</c> 才写回原目录；关闭不保存 = 副本被丢弃 = 批注消失），
    /// 所以本类不需要任何"保存通道"，<c>WorkspaceService</c> 会把它当普通新文件带走。</para>
    /// <para><b>只读闸门只有一处</b>：本类的 <see cref="IsReadOnly"/>。UI 里的 <c>IsEnabled</c> 只是可用性提示，
    /// 不是安全边界——将来多一个调用点也不会踩穿"绝不覆盖坏文件"这条线。</para>
    /// </summary>
    public sealed class FeedbackStore
    {
        /// <summary>与 <c>slides.py</c> 同级的文件名。</summary>
        public const string FileName = "feedback.json";

        /// <summary>原子写的临时文件名。⚠ 必须以 <c>.</c> 开头：<c>WorkspaceService.ScanDir</c> 跳过点开头的文件，
        /// 否则崩溃残留会被 <c>Save</c> 当成 deck 内容带进原目录。</summary>
        private const string TempFileName = ".feedback.json.tmp";

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        private readonly Action<string> _log;
        private readonly List<FeedbackNote> _notes = new List<FeedbackNote>();

        private bool _readOnly;
        private string _error;
        private int _version = 1;

        public FeedbackStore(string workDeckPath, string originalDeckPath, Action<string> log = null)
        {
            if (string.IsNullOrWhiteSpace(workDeckPath))
                throw new ArgumentException("工作副本路径不能为空", nameof(workDeckPath));

            FilePath = Path.Combine(workDeckPath, FileName);
            OriginalFilePath = string.IsNullOrWhiteSpace(originalDeckPath)
                ? null
                : Path.Combine(originalDeckPath, FileName);

            // keepEmptyFile 判定留在构造时：打开 deck 那一刻原目录有没有这个文件，决定了"删光后写空列表还是删文件"。
            // 中途原目录被别人改了也不该改变本次会话的语义（否则 Save 出去的结果取决于时序）。
            KeepEmptyFile = OriginalFilePath != null && File.Exists(OriginalFilePath);
            _log = log;

            Load();
        }

        /// <summary>工作副本里的 <c>feedback.json</c>（唯一会写的位置）。</summary>
        public string FilePath { get; }

        /// <summary>原目录里的 <c>feedback.json</c>（只用于判定 <see cref="KeepEmptyFile"/> 和提示）。</summary>
        public string OriginalFilePath { get; }

        /// <summary>读到的版本号；缺省 1。写回原样保留，不擅自改写。</summary>
        public int Version => _version;

        /// <summary>坏文件 / <c>version &gt; 1</c> → true。为 true 时本类拒绝一切写盘。</summary>
        public bool IsReadOnly => _readOnly;

        /// <summary>只读原因或写盘失败原因（黄条文案）；正常时为 null。</summary>
        public string Error => _error;

        public FeedbackLoadStatus Status { get; private set; } = FeedbackLoadStatus.Missing;

        /// <summary>打开 deck 时原目录是否已有 <c>feedback.json</c>：有则删光批注时写空列表，没有则删文件。</summary>
        public bool KeepEmptyFile { get; }

        public IReadOnlyList<FeedbackNote> Notes => _notes;

        public int Count => _notes.Count;

        /// <summary>任何内存/磁盘变化（增/改/移/删/Reload/写失败）。UI 只依赖这一个事件刷新。</summary>
        public event EventHandler Changed;

        /// <summary>重读工作副本。Revert 之后必须调（否则内存还留着刚被还原掉的批注，下次写盘会把它写回去）。</summary>
        public void Reload()
        {
            Load();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>某一页的批注，按写下时间升序。</summary>
        public IReadOnlyList<FeedbackNote> NotesForScene(string scene)
        {
            string target = scene ?? "";
            return _notes
                .Where(n => string.Equals(n.Scene, target, StringComparison.Ordinal))
                .OrderBy(n => n.CreatedUtc)
                .ToList();
        }

        /// <summary>有批注的场景名，按首次出现顺序（对齐原版 <c>FeedbackStore.scenes</c>）。</summary>
        public IReadOnlyList<string> SceneNames()
        {
            var seen = new List<string>();
            foreach (FeedbackNote n in _notes)
                if (!seen.Contains(n.Scene)) seen.Add(n.Scene);
            return seen;
        }

        /// <summary>按 id 找（大小写不敏感：外部文件里的 UUID 大小写不受我们控制）。</summary>
        public FeedbackNote Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return _notes.FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>新建一条并立即写盘。只读态返回 null（不写盘、不建条目）。</summary>
        public FeedbackNote Add(string scene, int build, double x, double y, string text = "")
        {
            if (_readOnly) return null;

            var note = new FeedbackNote
            {
                // 与原版一致用大写：文件里看起来统一，比较用序数忽略大小写
                Id = Guid.NewGuid().ToString().ToUpperInvariant(),
                Scene = scene ?? "",
                Build = build < 0 ? 0 : build,
                X = x,
                Y = y,
                W = FeedbackNote.DefaultWidth,
                Text = text ?? "",
                CreatedUtc = DateTime.UtcNow,
            };
            Normalize(note);
            _notes.Add(note);
            WriteFile();
            return note;
        }

        /// <summary>改文字。文本没变就<b>不写盘</b>（避免把 Save 的 * 点亮），返回 false。</summary>
        public bool UpdateText(string id, string text)
        {
            if (_readOnly) return false;
            int i = IndexOf(id);
            if (i < 0) return false;

            string next = text ?? "";
            if (string.Equals(_notes[i].Text, next, StringComparison.Ordinal)) return false;

            _notes[i].Text = next;
            WriteFile();
            return true;
        }

        /// <summary>改位置（只改 x/y，不动 created/build/text）。</summary>
        public bool UpdatePosition(string id, double x, double y)
        {
            if (_readOnly) return false;
            int i = IndexOf(id);
            if (i < 0) return false;

            double nx = Clamp01(x);
            double ny = Clamp01(y);
            // 6 位小数内比较：拖动时鼠标抖动不该产生无意义的写盘
            if (Math.Abs(_notes[i].X - nx) < 1e-6 && Math.Abs(_notes[i].Y - ny) < 1e-6) return false;

            _notes[i].X = nx;
            _notes[i].Y = ny;
            WriteFile();
            return true;
        }

        public bool Remove(string id)
        {
            if (_readOnly) return false;
            int i = IndexOf(id);
            if (i < 0) return false;

            _notes.RemoveAt(i);
            WriteFile();
            return true;
        }

        public bool RemoveAll()
        {
            if (_readOnly) return false;
            if (_notes.Count == 0) return false;

            _notes.Clear();
            WriteFile();
            return true;
        }

        #region 读

        private void Load()
        {
            _notes.Clear();
            _readOnly = false;
            _error = null;
            _version = 1;

            if (!File.Exists(FilePath))
            {
                // 文件不存在 = 没有批注，不是错误（PRD FB-P0-01）。首次加批注时才创建。
                Status = FeedbackLoadStatus.Missing;
                return;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(FilePath);
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                // 存在但读不出来：按坏文件处理（只读），绝不尝试覆盖
                Status = FeedbackLoadStatus.ParseError;
                _readOnly = true;
                _error = "无法读取 feedback.json：" + ex.Message;
                _log?.Invoke("[feedback] " + _error);
                return;
            }

            ReadOnlySpan<byte> span = bytes;
            if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
                span = span.Slice(3);                       // 用户可能用记事本存过带 BOM 的版本

            FeedbackFile doc = null;
            try
            {
                doc = JsonSerializer.Deserialize<FeedbackFile>(span, FeedbackJson.Options);
            }
            catch (Exception ex) when (ex is JsonException || ex is NotSupportedException)
            {
                Status = FeedbackLoadStatus.ParseError;
                _readOnly = true;
                _error = "无法解析 feedback.json：" + ex.Message;
                _log?.Invoke("[feedback] " + _error);
                return;
            }

            if (doc == null)
            {
                Status = FeedbackLoadStatus.ParseError;
                _readOnly = true;
                _error = "无法解析 feedback.json：文件内容为空";
                _log?.Invoke("[feedback] " + _error);
                return;
            }

            _version = doc.Version <= 0 ? 1 : doc.Version;
            if (doc.Notes != null)
            {
                foreach (FeedbackNote n in doc.Notes)
                {
                    if (n == null) continue;
                    Normalize(n);
                    _notes.Add(n);
                }
            }

            if (_version > 1)
            {
                // 尽力解析展示，但保持只读：写回会截断新版本才有的字段
                Status = FeedbackLoadStatus.UnsupportedVersion;
                _readOnly = true;
                _error = $"这份批注来自更新的版本（version {_version}），本次只读以免覆盖";
                _log?.Invoke("[feedback] " + _error);
                return;
            }

            Status = FeedbackLoadStatus.Ok;
        }

        /// <summary>只改内存，<b>不改写文件</b>：外部文件里的越界坐标/空 id 不该在打开 deck 时被悄悄写回去。</summary>
        private static void Normalize(FeedbackNote note)
        {
            if (string.IsNullOrEmpty(note.Id)) note.Id = Guid.NewGuid().ToString().ToUpperInvariant();
            if (note.Scene == null) note.Scene = "";
            if (note.Text == null) note.Text = "";
            if (note.W <= 0 || double.IsNaN(note.W) || double.IsInfinity(note.W)) note.W = FeedbackNote.DefaultWidth;
            note.W = Round6(note.W);
            note.X = Clamp01(note.X);
            note.Y = Clamp01(note.Y);
            if (note.CreatedUtc == default(DateTime)) note.CreatedUtc = DateTime.UtcNow;
        }

        #endregion

        #region 写

        /// <summary>
        /// 整文件重写（原子替换）。空列表时按 <see cref="KeepEmptyFile"/> 决定"写空列表"还是"删文件"。
        /// </summary>
        private bool WriteFile()
        {
            try
            {
                if (_notes.Count == 0 && !KeepEmptyFile)
                {
                    // 原目录本来没有这个文件：删掉副本里的，Save 就不会把它带出去
                    if (File.Exists(FilePath)) File.Delete(FilePath);
                    _error = null;
                    Changed?.Invoke(this, EventArgs.Empty);
                    return true;
                }

                // 深拷贝一份再序列化：序列化期间用户继续在 UI 上改同一条时，不会写出"一半新一半旧"的字段
                var doc = new FeedbackFile
                {
                    Notes = _notes.Select(n => n.Clone()).ToList(),
                    Version = _version,
                };
                byte[] bytes = Utf8NoBom.GetBytes(JsonSerializer.Serialize(doc, FeedbackJson.Options));

                string dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                string temp = Path.Combine(dir ?? "", TempFileName);
                try
                {
                    File.WriteAllBytes(temp, bytes);
                    File.Move(temp, FilePath, overwrite: true);
                }
                finally
                {
                    // 崩溃/权限失败都没关系，残留 temp 必须清掉（它虽然会被 ScanDir 跳过，但没理由留着）
                    if (File.Exists(temp))
                    {
                        try { File.Delete(temp); } catch { /* 删不掉就算了，下次覆盖 */ }
                    }
                }

                _error = null;
                Changed?.Invoke(this, EventArgs.Empty);
                return true;
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                // 写失败不静默、也<b>不</b>置只读：换个权限/磁盘恢复后下次还能试
                _error = "批注保存失败：" + ex.Message;
                _log?.Invoke("[feedback] " + _error);
                Changed?.Invoke(this, EventArgs.Empty);
                return false;
            }
        }

        private static bool IsFileError(Exception ex)
            => ex is IOException
               || ex is UnauthorizedAccessException
               || ex is NotSupportedException
               || ex is SecurityException
               || ex is ArgumentException;

        #endregion

        #region 工具

        private int IndexOf(string id)
        {
            if (string.IsNullOrEmpty(id)) return -1;
            for (int i = 0; i < _notes.Count; i++)
                if (string.Equals(_notes[i].Id, id, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        /// <summary>比例坐标统一夹到 [0, 0.96]：0.96 之外批注块会整个飞出幻灯片，用户看不到也点不到。</summary>
        private static double Clamp01(double v)
        {
            if (double.IsNaN(v)) return 0;
            return Round6(Math.Min(Math.Max(0, v), 0.96));
        }

        /// <summary>消掉 0.42345678901234 这种尾巴（PRD「6 位小数内」）。</summary>
        private static double Round6(double v) => Math.Round(v, 6, MidpointRounding.AwayFromZero);

        #endregion
    }
}
