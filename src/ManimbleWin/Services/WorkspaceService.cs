using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using ManimbleWin.Models;

namespace ManimbleWin.Services
{
    /// <summary>
    /// 私有工作副本的核心：建立副本、baseline 差分、冲突/丢失检测、Save / Revert / Discard / SaveProjectCopy。
    ///
    /// <para><b>副本单元 = 「项目根 = deck 的父目录」整棵真实拷贝</b>（<c>{项目根}/{assets/, deck/}</c>）。
    /// 因为引擎有三处依赖这个结构：<c>srcedit.py:210</c> 的 <c>realpath(deck)</c>、
    /// <c>core.py:365/430</c> 的 <c>Path(deck).parent/"assets"</c>、
    /// <c>common.py:11</c> 的 <c>Path(__file__).resolve().parent.parent/"assets"</c>。</para>
    ///
    /// <para><b>路径约定</b>：baseline key、Save/Revert 的写回目标、冲突检测一律相对「项目根」，
    /// 例如 <c>deck/slides.py</c>、<c>assets/latex_pictures.json</c>——因为 <c>latex_retypeset</c>
    /// 会改写 assets 下的清单与占位图，那些改动同样要能被保存。</para>
    ///
    /// <para><b>生成目录</b>（<c>slides</c>/<c>media</c>/<c>__pycache__</c>/<c>.manimble</c>）不进副本、
    /// 不进 baseline、永不写回：它们是引擎的产物，写回原目录就是污染。</para>
    /// </summary>
    public sealed class WorkspaceService
    {
        /// <summary>
        /// 超过这个大小的文件 baseline 只记 SHA-256 不记字节，避免打开一个带大视频的 deck 就吃掉几百 MB 内存。
        /// 这类文件的 Revert 退回从原目录重读一次。
        /// </summary>
        public const long MaxBaselineBytes = 8L * 1024 * 1024;

        /// <summary>原子写用的临时文件后缀；扫描 baseline 与写回时都跳过它。</summary>
        private const string TempSuffix = ".manimble.tmp";

        private static readonly HashSet<string> GeneratedDirNames =
            new HashSet<string>(DirectoryLinkUtil.DefaultSkipDirNames, StringComparer.OrdinalIgnoreCase);

        private readonly Action<string> _log;
        private static string _appRoot;

        /// <summary><paramref name="log"/> 可为 null（不需要日志时）。</summary>
        public WorkspaceService(Action<string> log = null)
        {
            _log = log;
        }

        /// <summary>
        /// 生命周期数据的根：<c>%LOCALAPPDATA%\Manimble</c>（与最近文档同根，见设计 R6）。
        ///
        /// 环境变量 <c>MANIMBLE_DATA_ROOT</c> 可覆盖（便携运行 / 受限环境 / 自动化测试）；
        /// 每个候选目录都会做「建目录 + 写探针文件」的真实写测试，不可写就换下一个，
        /// 最后退到临时目录——保证功能可用而不是第一次打开就崩。
        /// </summary>
        public static string AppRoot
        {
            get
            {
                if (_appRoot != null) return _appRoot;
                var candidates = new List<string>();
                string overrideRoot = Environment.GetEnvironmentVariable("MANIMBLE_DATA_ROOT");
                if (!string.IsNullOrWhiteSpace(overrideRoot)) candidates.Add(overrideRoot.Trim());
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrWhiteSpace(localAppData))
                    candidates.Add(Path.Combine(localAppData, "Manimble"));
                string tempRoot = Path.GetTempPath();
                if (!string.IsNullOrWhiteSpace(tempRoot))
                    candidates.Add(Path.Combine(tempRoot, "Manimble"));

                foreach (string c in candidates)
                {
                    if (!TryPrepare(c)) continue;
                    _appRoot = c;
                    return c;
                }
                _appRoot = candidates.Count > 0 ? candidates[candidates.Count - 1] : Path.Combine(".", "Manimble");
                return _appRoot;
            }
        }

        /// <summary>目录能否创建并写入（写一个探针文件后删除）。</summary>
        private static bool TryPrepare(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return false;
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".write-probe-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                File.WriteAllText(probe, "ok");
                try { File.Delete(probe); } catch { }
                return true;
            }
            catch { return false; }
        }

        /// <summary>工作副本根：<c>%LOCALAPPDATA%\Manimble\work</c>。</summary>
        public static string BaseRoot => Path.Combine(AppRoot, "work");

        /// <summary>渲染缓存根：<c>%LOCALAPPDATA%\Manimble\cache</c>（按原路径哈希，重开复用）。</summary>
        public static string CacheRoot => Path.Combine(AppRoot, "cache");

        /// <summary>
        /// 某个原 deck 的渲染缓存目录：<c>cache\&lt;deck&gt;-&lt;SHA256(路径)前6位&gt;</c>。
        /// 按「原路径」而不是副本路径算，重开同一个 deck 能继续用上次的渲染缓存。
        /// </summary>
        public static string CacheDirFor(string deckPath)
        {
            string deck = NormalizePath(deckPath);
            if (File.Exists(deck))
            {
                string parent = Path.GetDirectoryName(deck);
                if (!string.IsNullOrEmpty(parent)) deck = parent;
            }
            string name = Path.GetFileName(deck);
            if (string.IsNullOrEmpty(name)) name = "deck";
            string dir = Path.Combine(CacheRoot, name + "-" + Hash6(deck));
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        /// <summary>规范化绝对路径：去掉引号与末尾分隔符（盘符根保留），不做 realpath。</summary>
        public static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("路径为空", nameof(path));
            string full = Path.GetFullPath(path.Trim().Trim('"').Trim());
            // "C:\" 这种盘符根的长度是 3，不能被裁掉
            while (full.Length > 3 && (full.EndsWith("\\", StringComparison.Ordinal) || full.EndsWith("/", StringComparison.Ordinal)))
                full = full.Substring(0, full.Length - 1);
            return full;
        }

        /// <summary>路径的 SHA-256 前 6 位；统一按小写比较，保证同路径不同大小写拿到同一个 hash。</summary>
        public static string Hash6(string text)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes((text ?? "").ToLowerInvariant()));
                var sb = new StringBuilder(6);
                for (int i = 0; i < 3 && i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        #region 建立副本

        /// <summary>
        /// 为 <paramref name="deckPath"/> 建立一份私有工作副本（每次打开唯一根，避免重开时互相误删）。
        ///
        /// 步骤：规范化路径 → 唯一工作根 → 以「deck 的父目录」为单元整棵真实拷贝 → 逐文件 baseline。
        /// </summary>
        /// <param name="deckPath">原 deck 目录；传了目录里的文件时自动取它所在的目录。</param>
        /// <exception cref="DirectoryNotFoundException">原 deck 目录不存在。</exception>
        /// <exception cref="IOException">路径不可用（盘符根、应用自己的数据目录等）。</exception>
        public WorkspaceSession Create(string deckPath)
        {
            string deck = NormalizePath(deckPath);
            if (File.Exists(deck))
            {
                _log?.Invoke("选中的是文件，改用它所在的文件夹：" + deck);
                string parent = Path.GetDirectoryName(deck);
                if (string.IsNullOrEmpty(parent)) throw new IOException("无法从文件路径定位 deck 目录：" + deck);
                deck = parent;
            }
            if (!Directory.Exists(deck)) throw new DirectoryNotFoundException("找不到 deck 目录：" + deck);

            string deckName = Path.GetFileName(deck);
            string originalRoot = Path.GetDirectoryName(deck);
            if (string.IsNullOrEmpty(deckName) || string.IsNullOrEmpty(originalRoot))
                throw new IOException("这个位置不能作为 deck 打开：" + deck);

            // 副本根就落在应用数据目录里，若把数据目录里的东西再拷一份会自我递归。
            if (IsInside(deck, AppRoot))
                throw new IOException("不能把应用自己的数据目录当作 deck 打开：" + deck);

            // 盘符根不能当项目根：那等于整盘拷贝。这种位置只带 deck/ 与 assets/。
            bool rootIsDriveRoot = IsFileSystemRoot(originalRoot);
            if (rootIsDriveRoot)
                _log?.Invoke("deck 直接放在盘符根下，副本只包含 deck 与 assets：" + deck);

            string rootName = Truncate(deckName, 40) + "-" + Hash6(deck) + "-" + Guid.NewGuid().ToString("N");
            string root = Path.Combine(BaseRoot, rootName);
            TryDeleteTree(root);                                  // 幂等：同名根不该存在，存在就清掉
            Directory.CreateDirectory(root);

            string projectLeaf = rootIsDriveRoot ? deckName + "-project" : Path.GetFileName(originalRoot);
            string workProjectRoot = Path.Combine(root, projectLeaf);
            string workDeckPath = Path.Combine(workProjectRoot, deckName);
            Directory.CreateDirectory(workProjectRoot);

            if (rootIsDriveRoot)
            {
                DirectoryLinkUtil.CopyTree(deck, workDeckPath, DirectoryLinkUtil.DefaultSkipDirNames, LogSkip);
                string assetsSrc = Path.Combine(originalRoot, "assets");
                if (Directory.Exists(assetsSrc))
                    DirectoryLinkUtil.CopyTree(assetsSrc, Path.Combine(workProjectRoot, "assets"),
                                               DirectoryLinkUtil.DefaultSkipDirNames, LogSkip);
            }
            else
            {
                DirectoryLinkUtil.CopyTree(originalRoot, workProjectRoot, DirectoryLinkUtil.DefaultSkipDirNames, LogSkip);
            }

            Directory.CreateDirectory(workDeckPath);
            // assets 是 deck 的兄弟目录：引擎 latex_retypeset 会往里写，副本里必须先存在，
            // 这样所有写入都落在副本树内，而不是被引擎顺手在原目录建一个。
            Directory.CreateDirectory(Path.Combine(workProjectRoot, "assets"));

            var session = new WorkspaceSession(deck, originalRoot, workDeckPath, workProjectRoot, root);
            BuildBaseline(session);
            Refresh(session);
            _log?.Invoke($"工作副本已建立：{workDeckPath}（{session.Baseline.Count} 个文件进基线）");
            return session;
        }

        /// <summary>逐文件记录 baseline（覆盖整个项目根，含 assets）。</summary>
        private void BuildBaseline(WorkspaceSession session)
        {
            session.Baseline.Clear();
            foreach (string rel in ScanWorkFiles(session))
            {
                string path = ToWorkPath(session, rel);
                byte[] data;
                try { data = File.ReadAllBytes(path); }
                catch (Exception ex) { _log?.Invoke($"基线读取失败，跳过 {rel}：{ex.Message}"); continue; }

                long ticks = 0;
                try { ticks = File.GetLastWriteTimeUtc(path).Ticks; } catch { }

                session.Baseline[rel] = new BaselineEntry
                {
                    RelativePath = rel,
                    Content = data.LongLength <= MaxBaselineBytes ? data : null,
                    Sha256 = Sha256Hex(data),
                    Size = data.LongLength,
                    LastWriteTicksUtc = ticks,
                };
            }
        }

        private void LogSkip(string message) => _log?.Invoke(message);

        #endregion

        #region 差分 / 冲突 / 丢失检测

        /// <summary>是否有未保存改动。</summary>
        public bool IsModified(WorkspaceSession session)
            => session != null && ComputeChangedFiles(session).Count > 0;

        /// <summary>相对 baseline 有差异的文件（改动 / 新增 / 删除），路径相对项目根。</summary>
        public IReadOnlyList<string> ChangedFiles(WorkspaceSession session)
            => session == null ? Array.Empty<string>() : ComputeChangedFiles(session);

        /// <summary>改动文件中原目录也被别人改过的那些（同步盘 / 手工编辑），路径相对项目根。</summary>
        public IReadOnlyList<string> ConflictFiles(WorkspaceSession session)
            => session == null
                ? Array.Empty<string>()
                : ComputeConflicts(session, ComputeChangedFiles(session));

        /// <summary>原 deck 目录是否已经不在了。</summary>
        public bool OriginalMissing(WorkspaceSession session)
        {
            if (session == null) return true;
            try { return !Directory.Exists(session.OriginalDeckPath); }
            catch { return true; }
        }

        /// <summary>
        /// 重新算一遍快照并写回 session（窗口每 2 秒轮询用它刷新标题星号与状态栏）。
        /// 未变化的文件靠 size+mtime 缓存直接复用上次结论，不重复读盘。
        /// </summary>
        public WorkspaceSession Refresh(WorkspaceSession session)
        {
            if (session == null) return null;
            var changed = ComputeChangedFiles(session);
            var conflicts = ComputeConflicts(session, changed);
            session.UpdateSnapshot(changed, conflicts, OriginalMissing(session));
            return session;
        }

        private List<string> ComputeChangedFiles(WorkspaceSession session)
        {
            var present = ScanWorkFiles(session);
            var presentSet = new HashSet<string>(present, StringComparer.Ordinal);
            var changed = new List<string>();
            foreach (string rel in present)
                if (Differs(session, rel)) changed.Add(rel);
            // 副本里被删掉的文件同样算差异：Save 时要在原目录删掉，Revert 时要按基线恢复
            foreach (string rel in session.Baseline.Keys)
                if (!presentSet.Contains(rel)) changed.Add(rel);
            changed.Sort(StringComparer.Ordinal);
            return changed;
        }

        private List<string> ComputeConflicts(WorkspaceSession session, IReadOnlyList<string> changed)
        {
            var conflicts = new List<string>();
            if (changed == null || changed.Count == 0) return conflicts;
            foreach (string rel in changed)
            {
                string originalPath = ToOriginalPath(session, rel);
                session.Baseline.TryGetValue(rel, out BaselineEntry entry);

                FileInfo info = null;
                try { info = new FileInfo(originalPath); } catch { }
                bool exists = info != null && info.Exists;

                if (entry == null)
                {
                    // 副本里新增的文件：原目录若已存在同名文件，说明是外部新建的，算冲突
                    if (exists) conflicts.Add(rel);
                    continue;
                }
                if (!exists) { conflicts.Add(rel); continue; }        // 原文件被外部删了
                if (!SameAsBaseline(entry, originalPath)) conflicts.Add(rel);
            }
            return conflicts;
        }

        /// <summary>副本文件是否与基线不同。size+mtime 没变就复用上次结论，大文件不重复读。</summary>
        private bool Differs(WorkspaceSession session, string rel)
        {
            string path = ToWorkPath(session, rel);
            long size, ticks;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return true;
                size = info.Length;
                ticks = info.LastWriteTimeUtc.Ticks;
            }
            catch { return true; }

            session.Baseline.TryGetValue(rel, out BaselineEntry entry);
            if (entry != null && entry.LastCompareSame.HasValue
                && entry.ComparedSize == size && entry.ComparedTicksUtc == ticks)
                return !entry.LastCompareSame.Value;

            bool same = entry != null && SameAsBaseline(entry, path);
            if (entry != null)
            {
                entry.LastCompareSame = same;
                entry.ComparedSize = size;
                entry.ComparedTicksUtc = ticks;
            }
            return !same;
        }

        /// <summary>文件内容是否与基线一致（先比大小，再比字节或哈希）。</summary>
        private static bool SameAsBaseline(BaselineEntry entry, string path)
        {
            if (entry == null) return false;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length != entry.Size) return false;
                byte[] data = File.ReadAllBytes(path);
                if (entry.Content != null) return BytesEqual(data, entry.Content);
                return string.Equals(Sha256Hex(data), entry.Sha256, StringComparison.Ordinal);
            }
            catch { return false; }
        }

        #endregion

        #region Save / Revert / Discard / 存项目副本

        /// <summary>
        /// 把副本里相对 baseline 有差异的文件写回原目录（原子写 + 删除同步 + 更新 baseline）。
        ///
        /// <para>调用前请先 <c>EnsureDraftAppliedAsync()</c> + 引擎 <c>settle</c>，否则写回的是旧源码。</para>
        ///
        /// <para>两种拒绝：原目录丢失（<see cref="SaveOutcome.OriginalMissingPath"/>）与外部改动冲突
        /// （<see cref="SaveOutcome.Conflicts"/>，需用户选覆盖才继续）。</para>
        /// </summary>
        /// <param name="session">工作副本会话。</param>
        /// <param name="overwriteConflicts">用户已确认覆盖外部改动。</param>
        public SaveOutcome Save(WorkspaceSession session, bool overwriteConflicts)
        {
            var outcome = new SaveOutcome();
            if (session == null)
            {
                outcome.Error = "没有打开的工作副本";
                return outcome;
            }
            if (OriginalMissing(session))
            {
                outcome.OriginalMissingPath = session.OriginalDeckPath;
                outcome.Error = $"原 deck 目录已不在（{session.OriginalDeckPath}）：它被移动、改名或删除了。"
                                + "你的改动还在副本里，请用「保存项目副本」把它存到新位置。";
                return outcome;
            }

            List<string> changed;
            List<string> conflicts;
            try
            {
                changed = ComputeChangedFiles(session);
                conflicts = ComputeConflicts(session, changed);
            }
            catch (Exception ex)
            {
                outcome.Error = "检查改动失败：" + ex.Message;
                return outcome;
            }

            if (conflicts.Count > 0 && !overwriteConflicts)
            {
                outcome.Conflicts = conflicts;
                outcome.Error = "原目录里的这些文件已被外部改动，保存会覆盖它们。";
                return outcome;
            }

            try
            {
                foreach (string rel in changed)
                {
                    string workPath = ToWorkPath(session, rel);
                    string originalPath = ToOriginalPath(session, rel);

                    if (File.Exists(workPath))
                    {
                        byte[] data = File.ReadAllBytes(workPath);
                        AtomicWrite(originalPath, data);

                        long ticks = 0;
                        try { ticks = File.GetLastWriteTimeUtc(originalPath).Ticks; } catch { }
                        session.Baseline[rel] = new BaselineEntry
                        {
                            RelativePath = rel,
                            Content = data.LongLength <= MaxBaselineBytes ? data : null,
                            Sha256 = Sha256Hex(data),
                            Size = data.LongLength,
                            LastWriteTicksUtc = ticks,
                        };
                        outcome.SavedCount++;
                    }
                    else
                    {
                        // 副本里删掉了：原目录同步删，基线里也去掉（这个文件从此不属于这个 deck）
                        if (File.Exists(originalPath))
                        {
                            File.Delete(originalPath);
                            outcome.DeletedCount++;
                        }
                        session.Baseline.Remove(rel);
                    }
                }
                outcome.Ok = true;
                _log?.Invoke($"已写回 {outcome.SavedCount} 个文件，删除 {outcome.DeletedCount} 个");
            }
            catch (Exception ex)
            {
                outcome.Ok = false;
                outcome.Error = "写回失败：" + ex.Message;
                _log?.Invoke(outcome.Error);
            }

            Refresh(session);
            return outcome;
        }

        /// <summary>
        /// 按基线恢复副本：改动的文件还原成基线内容、副本里新增的文件删掉（含 assets 下的）。
        /// 只动文件；引擎侧的 <c>clear_history</c> 与重载由 <c>EngineIntegration.RevertEngineAsync</c> 负责。
        /// </summary>
        public void Revert(WorkspaceSession session)
        {
            if (session == null) return;
            IReadOnlyList<string> changed = ComputeChangedFiles(session);
            foreach (string rel in changed)
            {
                string workPath = ToWorkPath(session, rel);
                if (session.Baseline.TryGetValue(rel, out BaselineEntry entry))
                {
                    byte[] data = entry.Content;
                    if (data == null)
                    {
                        // 超大文件没存字节：退回从原目录读一次（原目录若也被改过，日志里说清楚）
                        try { data = File.ReadAllBytes(ToOriginalPath(session, rel)); }
                        catch (Exception ex) { _log?.Invoke($"无法还原 {rel}（原文件读不到）：{ex.Message}"); }
                    }
                    if (data != null)
                    {
                        try { AtomicWrite(workPath, data); }
                        catch (Exception ex) { _log?.Invoke($"还原 {rel} 失败：{ex.Message}"); }
                    }
                    entry.LastCompareSame = null;
                }
                else
                {
                    try { if (File.Exists(workPath)) File.Delete(workPath); }
                    catch (Exception ex) { _log?.Invoke($"删除副本里的新增文件 {rel} 失败：{ex.Message}"); }
                }
            }
            Refresh(session);
            _log?.Invoke($"已按原始文件还原 {changed.Count} 个文件");
        }

        /// <summary>
        /// 删除整个工作根。引擎/子进程刚退出时可能还占着目录，按递增间隔重试几次（R5），
        /// 仍失败就只记日志——留一个副本目录比让关窗流程崩掉好。
        /// </summary>
        public void Discard(WorkspaceSession session)
        {
            if (session == null) return;
            string root = session.RootPath;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
            const int attempts = 6;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                try
                {
                    Directory.Delete(root, true);
                    _log?.Invoke("工作副本已清理：" + root);
                    return;
                }
                catch (Exception ex)
                {
                    if (attempt == attempts - 1) { _log?.Invoke($"清理工作副本失败（可稍后手工删除 {root}）：{ex.Message}"); return; }
                    Thread.Sleep(200 * (attempt + 1));      // 200/400/600/800/1000ms：等子进程真正退出
                }
            }
        }

        /// <summary>
        /// 把副本的当前状态另存为一份完整可编辑的项目文件夹（默认名 = <see cref="WorkspaceSession.ProjectName"/>，
        /// 重名自动加 " (2)"）。<b>不切换</b>当前编辑对象，副本与未保存改动都原样保留。
        /// </summary>
        /// <param name="session">工作副本会话。</param>
        /// <param name="parentDir">目标父目录。</param>
        /// <param name="name">文件夹名；空则用会话的 ProjectName。</param>
        /// <returns>新建项目里的 deck 目录绝对路径；失败返回 null（原因在日志里）。</returns>
        public string SaveProjectCopy(WorkspaceSession session, string parentDir, string name)
        {
            if (session == null) return null;
            string parent;
            try { parent = NormalizePath(parentDir); }
            catch (Exception ex) { _log?.Invoke("保存项目副本失败：" + ex.Message); return null; }

            try
            {
                Directory.CreateDirectory(parent);
                string baseName = string.IsNullOrWhiteSpace(name) ? session.ProjectName : name.Trim();
                string dest = Path.Combine(parent, baseName);
                int suffix = 2;
                while (Directory.Exists(dest) || File.Exists(dest))
                {
                    dest = Path.Combine(parent, $"{baseName} ({suffix})");
                    suffix++;
                }

                // 生成目录（slides/media/__pycache__/.manimble）不带走：复制出来的是可继续编辑的项目，
                // 渲染产物由引擎重新生成，也避免把几百 MB 的构建缓存搬过去。
                DirectoryLinkUtil.CopyTree(session.WorkProjectRoot, dest, DirectoryLinkUtil.DefaultSkipDirNames, LogSkip);

                string deck = Path.Combine(dest, session.DeckName);
                if (!Directory.Exists(deck))
                {
                    _log?.Invoke("项目副本里没有找到 deck 目录：" + deck);
                    return null;
                }
                _log?.Invoke("项目副本已保存：" + dest);
                return deck;
            }
            catch (Exception ex)
            {
                _log?.Invoke("保存项目副本失败：" + ex.Message);
                return null;
            }
        }

        #endregion

        #region 文件扫描与路径工具

        /// <summary>扫描副本项目根下的所有「该被跟踪」的文件（相对项目根、'/' 归一化、已排序）。</summary>
        private List<string> ScanWorkFiles(WorkspaceSession session)
        {
            var list = new List<string>();
            if (session == null || !Directory.Exists(session.WorkProjectRoot)) return list;
            ScanDir(session.WorkProjectRoot, session.WorkProjectRoot, session.OwnFiles, list);
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        private static void ScanDir(string root, string dir, HashSet<string> ownFiles, List<string> outList)
        {
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(dir); }
            catch { return; }

            foreach (string entry in entries)
            {
                string name = Path.GetFileName(entry);
                if (string.IsNullOrEmpty(name)) continue;
                if (name[0] == '.') continue;                          // 隐藏项不进基线（对齐原版）
                if (DirectoryLinkUtil.IsReparsePoint(entry)) continue; // 防御：副本树里不该有链接

                bool isDir;
                try { isDir = Directory.Exists(entry); }
                catch { continue; }

                if (isDir)
                {
                    if (GeneratedDirNames.Contains(name)) continue;     // 生成目录：不跟踪
                    ScanDir(root, entry, ownFiles, outList);
                    continue;
                }

                string rel = Path.GetRelativePath(root, entry).Replace('\\', '/');
                if (ownFiles != null && ownFiles.Contains(rel)) continue;
                if (rel.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                outList.Add(rel);
            }
        }

        /// <summary>相对项目根的路径 → 副本里的绝对路径。</summary>
        public static string ToWorkPath(WorkspaceSession session, string relativePath)
            => Path.Combine(session.WorkProjectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

        /// <summary>相对项目根的路径 → 原目录里的绝对路径。</summary>
        public static string ToOriginalPath(WorkspaceSession session, string relativePath)
            => Path.Combine(session.OriginalProjectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

        /// <summary>原子写：先写同目录临时文件再替换，避免写一半断电/异常留下半个文件。</summary>
        private static void AtomicWrite(string destPath, byte[] data)
        {
            string dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string temp = destPath + TempSuffix;
            try
            {
                File.WriteAllBytes(temp, data);
                File.Move(temp, destPath, true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        private static string Sha256Hex(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(data ?? Array.Empty<byte>());
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static bool IsFileSystemRoot(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                string root = Path.GetPathRoot(full);
                if (string.IsNullOrEmpty(root)) return false;
                return string.Equals(full.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary><paramref name="path"/> 是否在 <paramref name="dir"/> 里面（含自己）。</summary>
        private static bool IsInside(string path, string dir)
        {
            try
            {
                string p = Path.GetFullPath(path).TrimEnd('\\', '/');
                string d = Path.GetFullPath(dir).TrimEnd('\\', '/');
                if (string.Equals(p, d, StringComparison.OrdinalIgnoreCase)) return true;
                return p.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text ?? "";
            return text.Substring(0, max);
        }

        private static void TryDeleteTree(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch { }
        }

        #endregion
    }
}
