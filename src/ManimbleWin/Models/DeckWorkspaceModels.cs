using System;
using System.Collections.Generic;

namespace ManimbleWin.Models
{
    /// <summary>
    /// 工作副本里一个文件的基线记录：建立副本时原文件的字节、大小与修改时间。
    ///
    /// 为什么同时存字节和 size/mtime：<see cref="Content"/> 用于冲突判断与 Revert 写回；
    /// size+mtime 是「没有变」的快速判据——窗口每 2 秒轮询一次未保存状态，一个 deck 里可能有
    /// 大图片，不能每次都整文件读一遍（对齐 Workspace.swift:127-137 的 compared 缓存）。
    /// </summary>
    public sealed class BaselineEntry
    {
        /// <summary>相对「项目根」的路径，已用 '/' 归一化，例如 "deck/slides.py"、"assets/latex_pictures.json"。</summary>
        public string RelativePath { get; set; }

        /// <summary>
        /// 建立副本时的原始字节。超大文件（超过 <c>WorkspaceService.MaxBaselineBytes</c>）为 null，
        /// 此时只靠 <see cref="Sha256"/> 比较、Revert 退回从原目录重新读一次，避免一次打开就吃掉几百 MB 内存。
        /// </summary>
        public byte[] Content { get; set; }

        /// <summary>内容的 SHA-256（小写十六进制），冲突比较与 Content 为 null 时的兜底判据。</summary>
        public string Sha256 { get; set; }

        /// <summary>字节数。</summary>
        public long Size { get; set; }

        /// <summary>建立副本时的最后写入时间（UTC ticks）。</summary>
        public long LastWriteTicksUtc { get; set; }

        /// <summary>上一次比较的结论：null=没比过，true=与基线一致，false=有差异。</summary>
        public bool? LastCompareSame { get; set; }

        /// <summary>上一次比较时看到的文件大小，与 <see cref="ComparedTicksUtc"/> 一起构成缓存键。</summary>
        public long ComparedSize { get; set; }

        /// <summary>上一次比较时看到的最后写入时间（UTC ticks）。</summary>
        public long ComparedTicksUtc { get; set; }
    }

    /// <summary>
    /// 一份私有工作副本的会话。
    ///
    /// 结构不变量（引擎三处 resolve 依赖它，不能改）：<see cref="WorkProjectRoot"/> 下同时有
    /// <c>assets/</c> 与 <see cref="WorkDeckPath"/>（deck 目录），即「项目根 = deck 的父目录」。
    /// 所有 baseline key / Save / Revert / 冲突检测的路径都相对 <see cref="WorkProjectRoot"/>，
    /// 而不是相对 deck —— 因为 <c>latex_retypeset</c> 会改写 assets 下的清单与占位图。
    /// </summary>
    public sealed class WorkspaceSession
    {
        private IReadOnlyList<string> _changedFiles = Array.Empty<string>();
        private IReadOnlyList<string> _conflictFiles = Array.Empty<string>();
        private bool _originalMissing;

        /// <summary>建立副本所需的信息。<paramref name="projectName"/> 为空时取「父目录名-deck名」。</summary>
        public WorkspaceSession(string originalDeckPath, string originalProjectRoot, string workDeckPath,
                                string workProjectRoot, string rootPath, string projectName = null)
        {
            OriginalDeckPath = originalDeckPath;
            OriginalProjectRoot = originalProjectRoot;
            WorkDeckPath = workDeckPath;
            WorkProjectRoot = workProjectRoot;
            RootPath = rootPath;
            string parent = ParentName;
            ProjectName = !string.IsNullOrWhiteSpace(projectName)
                ? projectName
                : (parent.Length > 0 ? parent + "-" + DeckName : DeckName);
            DeckRelativePath = parent.Length > 0 ? parent + "/" + DeckName : DeckName;
        }

        /// <summary>原 deck 目录（规范化绝对路径）。</summary>
        public string OriginalDeckPath { get; }

        /// <summary>原 deck 的父目录 —— 也就是 assets/ 与 deck/ 共同的「项目根」。</summary>
        public string OriginalProjectRoot { get; }

        /// <summary>副本 deck 目录 = 引擎 <c>open</c> 的 deck（真实目录，不是 junction）。</summary>
        public string WorkDeckPath { get; }

        /// <summary>副本项目根：含 assets/ 与 deck/（以及原项目根里的其他同级目录）。</summary>
        public string WorkProjectRoot { get; }

        /// <summary>唯一工作根（<c>%LOCALAPPDATA%\Manimble\work\...</c>），Discard 时整棵删除。</summary>
        public string RootPath { get; }

        /// <summary>"父目录/deck"，窗口标题与最近文档用同一种写法。</summary>
        public string DeckRelativePath { get; }

        /// <summary>项目名（默认「父目录名-deck名」），存项目副本时用的默认文件夹名。</summary>
        public string ProjectName { get; }

        /// <summary>deck 目录名。</summary>
        public string DeckName => System.IO.Path.GetFileName(WorkDeckPath);

        /// <summary>原 deck 的父目录名。</summary>
        public string ParentName => System.IO.Path.GetFileName(OriginalProjectRoot);

        /// <summary>逐文件基线，key = 相对项目根路径（'/' 归一化）。</summary>
        public Dictionary<string, BaselineEntry> Baseline { get; } =
            new Dictionary<string, BaselineEntry>(StringComparer.Ordinal);

        /// <summary>
        /// 只写在副本、永不写回原目录的文件（agent 指令文件之类）。本增量不生成这类文件，
        /// 但扫描/保存都尊重这个集合，后续要写 AGENTS.md 时不必改 WorkspaceService。
        /// </summary>
        public HashSet<string> OwnFiles { get; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>相对 baseline 有差异的文件（含新增、删除），最近一次扫描的结果。</summary>
        public IReadOnlyList<string> ChangedFiles => _changedFiles;

        /// <summary>其中原目录也被别人改过的文件，最近一次扫描的结果。</summary>
        public IReadOnlyList<string> ConflictFiles => _conflictFiles;

        /// <summary>原 deck 目录是否已经不在了（被移动、改名或删除）。</summary>
        public bool OriginalMissing => _originalMissing;

        /// <summary>是否有未保存改动（标题星号 / 状态栏由它驱动）。</summary>
        public bool IsModified => _changedFiles.Count > 0;

        /// <summary>由 <see cref="Services.WorkspaceService"/> 刷新快照，外部只读。</summary>
        internal void UpdateSnapshot(IReadOnlyList<string> changedFiles, IReadOnlyList<string> conflictFiles, bool originalMissing)
        {
            _changedFiles = changedFiles ?? Array.Empty<string>();
            _conflictFiles = conflictFiles ?? Array.Empty<string>();
            _originalMissing = originalMissing;
        }
    }

    /// <summary>Save to Original 的结果。冲突与「原目录丢了」是两种不同的拒绝原因，前端要分开提示。</summary>
    public sealed class SaveOutcome
    {
        /// <summary>是否写回成功（没有改动也算成功，此时 SavedCount/DeletedCount 都是 0）。</summary>
        public bool Ok { get; set; }

        /// <summary>失败原因（中文，可直接显示）。成功时为 null。</summary>
        public string Error { get; set; }

        /// <summary>原目录丢失时这里是原 deck 路径，供提示里显示并引导「另存项目副本」。</summary>
        public string OriginalMissingPath { get; set; }

        /// <summary>冲突文件（相对项目根路径）；非空表示原目录也被外部改动过。</summary>
        public List<string> Conflicts { get; set; } = new List<string>();

        /// <summary>写回的差异文件数。</summary>
        public int SavedCount { get; set; }

        /// <summary>同步删除的文件数（副本里删掉、原目录也要跟着删）。</summary>
        public int DeletedCount { get; set; }
    }

    /// <summary>最近文档条目。</summary>
    public sealed class RecentEntry
    {
        /// <summary>deck 目录的绝对路径。</summary>
        public string Path { get; set; }

        /// <summary>菜单显示文本「父目录 / deck」（deck 常常都叫 deck，只显示末级会分不清）。</summary>
        public string DisplayName { get; set; }

        /// <summary>菜单项提示：完整路径。</summary>
        public string ToolTip { get; set; }
    }

    /// <summary>Save 冲突时用户的选择（严格对齐原版：二选一，不提供自动合并）。</summary>
    public enum ConflictChoice
    {
        /// <summary>覆盖原目录。</summary>
        Overwrite,

        /// <summary>取消：一个文件都不写。</summary>
        Cancel,
    }

    /// <summary>冲突信息（文件列表 + 用户选择）。</summary>
    public sealed class ConflictInfo
    {
        /// <summary>冲突文件（相对项目根路径）。</summary>
        public List<string> Files { get; set; } = new List<string>();

        /// <summary>用户选择。</summary>
        public ConflictChoice Choice { get; set; } = ConflictChoice.Cancel;
    }

    /// <summary>可导入的源文件类型。Keynote(.key) 与旧 .ppt 不支持（Windows 上依赖 Keynote 转换）。</summary>
    public enum ImportKind
    {
        /// <summary>PowerPoint 演示文稿（引擎 importer）。</summary>
        Pptx,

        /// <summary>Beamer 源文件 .tex/.ltx（引擎 beamer_import）。</summary>
        Beamer,
    }

    /// <summary>
    /// 导出目标。PDF 不在其中：引擎 <c>export</c> 没有独立 pdf 分支，
    /// <c>pdfError</c> 只是 beamer 导出副产物，不能当稳定交付。
    /// </summary>
    public enum ExportKind
    {
        /// <summary>.pptx（引擎 export kind=pptx）。</summary>
        Pptx,

        /// <summary>.tex（引擎 export kind=beamer，编译 PDF 依赖本机 TeX）。</summary>
        Beamer,

        /// <summary>.mp4（引擎 export_movie）。</summary>
        Movie,

        /// <summary>单个 .py（引擎 export_python）。</summary>
        Python,

        /// <summary>Keynote：Windows 上必然失败（osascript），菜单项保留但禁用并给引导。</summary>
        KeynoteDisabled,
    }

    /// <summary>一次导入请求。参数名与引擎 <c>import</c> 的 <c>src</c>/<c>dest</c> 对应。</summary>
    public sealed class ImportRequest
    {
        /// <summary>源文件绝对路径（.pptx / .tex / .ltx）。</summary>
        public string SourcePath { get; set; }

        /// <summary>目标父目录：引擎会在它下面建 <c>&lt;DeckName&gt;/deck</c> 与 <c>assets/</c>。</summary>
        public string DestParent { get; set; }

        /// <summary>目标 deck 名（文件夹名）。</summary>
        public string DeckName { get; set; }

        /// <summary>源文件类型。</summary>
        public ImportKind Kind { get; set; } = ImportKind.Pptx;

        /// <summary>
        /// 引擎 <c>import</c> 的 <c>dest</c>：项目根，引擎会在它下面建 <c>deck/</c> 与 <c>assets/</c>。
        /// </summary>
        public string DestPath =>
            string.IsNullOrWhiteSpace(DestParent) || string.IsNullOrWhiteSpace(DeckName)
                ? null
                : System.IO.Path.Combine(DestParent, DeckName);

        /// <summary>导入产物的 deck 路径（<c>DestParent/DeckName/deck</c>），导入完成后打开它。</summary>
        public string ResultDeckPath =>
            DestPath == null ? null : System.IO.Path.Combine(DestPath, "deck");
    }

    /// <summary>一次导出请求。默认值对齐引擎：builds=true、editable=true、720p、hold=2s、30fps。</summary>
    public sealed class ExportRequest
    {
        /// <summary>导出目标。</summary>
        public ExportKind Kind { get; set; } = ExportKind.Pptx;

        /// <summary>输出路径（.pptx / .tex / .mp4 / .py）。</summary>
        public string OutPath { get; set; }

        /// <summary>是否逐 build 导出（引擎 builds）。</summary>
        public bool Builds { get; set; } = true;

        /// <summary>可编辑文本（true）还是纯图片（false），引擎 editable。</summary>
        public bool Editable { get; set; } = true;

        /// <summary>MP4 分辨率高度：720 / 1080 / 1440 / 2160。</summary>
        public int Height { get; set; } = 720;

        /// <summary>MP4 每页停帧秒数。</summary>
        public double Hold { get; set; } = 2;

        /// <summary>MP4 帧率（1..120）。</summary>
        public int Fps { get; set; } = 30;

        /// <summary>只导出这些页（null/空 = 全部可见页，按 show 顺序）。</summary>
        public string[] Scenes { get; set; }
    }

    /// <summary>LaTeX 缺包提示条的数据。</summary>
    public sealed class LatexNotice
    {
        /// <summary>仍然缺失的宏包文件（.sty/.cls）。</summary>
        public List<string> Missing { get; set; } = new List<string>();

        /// <summary>仍是占位图的公式/图片数量（重排后应变成 0）。</summary>
        public int Placeholders { get; set; }

        /// <summary>这些缺包来自哪个 .tex 文件（提示里带上更有用）。</summary>
        public string Source { get; set; }

        /// <summary>用户勾了「不再询问」。</summary>
        public bool DontAskAgain { get; set; }

        /// <summary>缺包或占位图任一存在时才需要显示提示条。</summary>
        public bool ShouldShow => Missing.Count > 0 || Placeholders > 0;
    }
}
