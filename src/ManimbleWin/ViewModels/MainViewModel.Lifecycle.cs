using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using ManimbleWin.Helpers;
using ManimbleWin.Models;
using ManimbleWin.Services;

namespace ManimbleWin.ViewModels
{
    /// <summary>关闭前的三选一结果（对齐原版 Save / Discard Changes / Cancel）。</summary>
    public enum CloseChoice
    {
        /// <summary>保存到原始文件后继续。</summary>
        Save,

        /// <summary>丢弃副本里的改动继续。</summary>
        Discard,

        /// <summary>取消：留在当前 deck。</summary>
        Cancel,
    }

    /// <summary>新建演示文稿的三态结果（成功 / 用户取消 / 失败）。</summary>
    public sealed class NewDeckResult
    {
        private NewDeckResult() { }

        /// <summary>生成的 deck 目录（仅 <see cref="Succeeded"/> 时有值）。</summary>
        public string DeckPath { get; private init; }

        /// <summary>用户取消（什么都没落盘）。</summary>
        public bool Cancelled { get; private init; }

        /// <summary>失败原因（可直接显示）。</summary>
        public string Error { get; private init; }

        /// <summary>是否已经生成并打开。</summary>
        public bool Succeeded => !Cancelled && Error == null;

        /// <summary>是否失败（有原因可显示）。</summary>
        public bool Failed => Error != null;

        public static NewDeckResult Ok(string deckPath) => new NewDeckResult { DeckPath = deckPath };

        public static NewDeckResult Cancel() => new NewDeckResult { Cancelled = true };

        public static NewDeckResult Fail(string error) => new NewDeckResult { Error = error ?? "新建失败" };
    }

    /// <summary>
    /// MainViewModel 的 Deck 生命周期部分（T03）：私有工作副本的打开 / 保存 / 还原 / 另存 /
    /// 最近文档 / 关闭确认 + 统一的源码草稿守卫。
    ///
    /// <para><b>状态机</b>（对齐设计 §1.3）：Closed → Opening(建副本+引擎 open 副本) → Clean ⇄ Dirty
    /// → Saving/Reverting → Closing/Discarding。所有写盘动作都只作用于副本，只有 Save 才写回原目录。</para>
    ///
    /// <para>为什么单独一个 partial 文件：MainViewModel 已经有 2800+ 行，生命周期与导入导出各占一区，
    /// 与设计里「区域 ownership」的要求一致，T04/T05 只碰自己的文件，避免互相覆盖。</para>
    /// </summary>
    public partial class MainViewModel
    {
        #region 工作副本状态

        private readonly WorkspaceService _workspace;
        private readonly RecentDecksService _recent;
        private WorkspaceSession _session;
        private System.Windows.Threading.DispatcherTimer _modifiedTimer;
        private bool _deckBusy;          // 打开/保存/还原进行中，避免菜单重复触发
        private bool _closeApproved;     // 关闭确认已通过：OnClosing 不再重复询问

        /// <summary>是否禁用启动时自动打开示例 deck（自动化测试用；生产保持 true）。</summary>
        public static bool SuppressStartupAutoOpen { get; set; }

        /// <summary>当前工作副本会话；未打开时为 null。</summary>
        public WorkspaceSession Session => _session;

        /// <summary>是否有打开的演示文稿。</summary>
        public bool HasDeck => _session != null;

        /// <summary>标题里显示的「父目录 / deck」。</summary>
        public string DeckDisplayName => _session?.DeckRelativePath ?? "未打开演示文稿";

        /// <summary>窗口标题：未保存时带星号（对齐原版 isDocumentEdited 的可见反馈）。</summary>
        public string WindowTitle => IsModified ? $"Manimble — {DeckDisplayName} *" : $"Manimble — {DeckDisplayName}";

        /// <summary>状态栏里的未保存标记。</summary>
        public string ModifiedMark => IsModified ? "*" : "";

        /// <summary>是否有未保存改动（相对 baseline 有差异的文件）。</summary>
        public bool IsModified => _session != null && _session.IsModified;

        /// <summary>当前编辑的是副本 —— 状态栏/设置里用来排障（P2-1）。</summary>
        public string DeckStatusHint
        {
            get
            {
                if (_session == null) return "未打开演示文稿";
                string state = IsModified ? "未保存的改动" : "已保存到原文件";
                return $"正在编辑工作副本（{state}）· Ctrl+S 保存到原文件 · 原件：{_session.OriginalDeckPath}";
            }
        }

        /// <summary>File ▸ Open Recent 的数据源（最近 10 个，已剔除失效路径）。</summary>
        public ObservableCollection<RecentEntry> RecentDecks { get; } = new ObservableCollection<RecentEntry>();

        #endregion

        #region 可替换的确认钩子（T05 用真实对话框，自动化测试直接返回选择）

        /// <summary>未保存三选一。参数：(动作描述)。默认真实弹框。</summary>
        public Func<string, CloseChoice> ConfirmCloseHook { get; set; }

        /// <summary>保存冲突二选一。参数：冲突信息。默认弹框。</summary>
        public Func<ConflictInfo, ConflictChoice> ConfirmConflictHook { get; set; }

        /// <summary>是/否二次确认（Revert、退出等）。参数：(标题, 正文)。默认弹框。</summary>
        public Func<string, string, bool> ConfirmOkCancelHook { get; set; }

        /// <summary>
        /// 新建演示文稿对话框：T05 的 NewDeckDialog 注册进来，返回 true 表示用户确认。
        /// 未注册时退化为「选文件夹 + 默认名称」的简易流程。
        /// </summary>
        public Func<NewDeckDialogViewModel, Task<bool>> NewDeckDialogHook { get; set; }

        private CloseChoice AskClose(string actionText)
        {
            if (ConfirmCloseHook != null) return ConfirmCloseHook(actionText);
            string files = DescribeChangedFiles();
            var r = MessageBox.Show(
                "你正在编辑一份私有工作副本，里面还有未保存的改动。\n\n"
                + files
                + $"\n\n要在{actionText}前把改动保存到原始文件吗？\n"
                + "「是」保存到原文件 · 「否」丢弃这些改动 · 「取消」返回继续编辑",
                actionText, MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (r == MessageBoxResult.Yes) return CloseChoice.Save;
            return r == MessageBoxResult.No ? CloseChoice.Discard : CloseChoice.Cancel;
        }

        private ConflictChoice AskConflict(ConflictInfo info)
        {
            if (ConfirmConflictHook != null) return ConfirmConflictHook(info);
            string list = string.Join("\n", info.Files.Take(10));
            if (info.Files.Count > 10) list += $"\n…等 {info.Files.Count} 个文件";
            var r = MessageBox.Show(
                "原始目录里的这些文件在打开之后被改动过（同步盘或别人编辑）：\n\n" + list
                + "\n\n覆盖它们吗？选「否」则这次一个文件都不写。",
                "原始文件已被外部改动", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            return r == MessageBoxResult.Yes ? ConflictChoice.Overwrite : ConflictChoice.Cancel;
        }

        private bool AskOkCancel(string title, string body)
        {
            if (ConfirmOkCancelHook != null) return ConfirmOkCancelHook(title, body);
            return MessageBox.Show(body, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
        }

        private string DescribeChangedFiles()
        {
            if (_session == null) return "";
            var changed = _workspace.ChangedFiles(_session);
            if (changed.Count == 0) return "（没有检测到文件级改动）";
            string list = string.Join("、", changed.Take(6));
            if (changed.Count > 6) list += $" 等 {changed.Count} 个文件";
            return "有改动的文件：" + list;
        }

        #endregion

        #region 打开 / 新建 / 关闭

        /// <summary>
        /// 统一打开入口：确认未保存 → 建工作副本 → 引擎 open <b>副本</b> → 记入最近文档。
        /// </summary>
        /// <param name="originalDeckPath">原 deck 目录（也接受项目文件夹、deck 内的文件、.manimble 包）。</param>
        /// <param name="confirmUnsaved">是否需要先走未保存三选一（导入流程里已经问过就传 false）。</param>
        public async Task<bool> OpenDeckAsync(string originalDeckPath, bool confirmUnsaved = true)
        {
            if (_deckBusy)
            {
                ShowToast("正在处理上一个操作，请稍候");
                return false;
            }
            if (string.IsNullOrWhiteSpace(originalDeckPath))
            {
                ShowToast("没有选择演示文稿");
                return false;
            }

            // 已经开着同一个 deck：只前置窗口，绝不重建副本（那会丢掉未保存的改动）
            string probe = TryPeekDeckFolder(originalDeckPath);
            if (probe != null && _session != null
                && string.Equals(probe, _session.OriginalDeckPath, StringComparison.OrdinalIgnoreCase))
            {
                BringMainWindowToFront();
                ShowToast("这个演示文稿已经打开了：" + _session.DeckRelativePath);
                return true;
            }

            if (confirmUnsaved && !await ConfirmCloseAsync("打开另一份演示文稿")) return false;

            string deckPath;
            try
            {
                deckPath = ResolveDeckPath(originalDeckPath);
            }
            catch (Exception ex)
            {
                ShowToast("无法打开：" + ex.Message);
                return false;
            }
            if (deckPath == null) return false;
            return await LoadDeckIntoWorkspaceAsync(deckPath);
        }

        /// <summary>真正建副本并让引擎打开副本 deck。</summary>
        private async Task<bool> LoadDeckIntoWorkspaceAsync(string originalDeckPath)
        {
            _deckBusy = true;
            BusyText = "正在打开演示文稿…";
            WorkspaceSession session = null;
            try
            {
                await CloseEngineAndDiscardAsync();      // 释放旧副本与旧引擎进程

                session = _workspace.Create(originalDeckPath);   // 唯一工作根 + 整棵真实拷贝 + baseline
                ResetDeckState();

                EngineStatus = "正在打开工作副本…";
                bool ok = await _engine.ConnectAndLoad(
                    session.WorkDeckPath,
                    WorkspaceService.CacheDirFor(session.OriginalDeckPath));
                if (!ok)
                {
                    _workspace.Discard(session);
                    EngineStatus = "未连接 · 原型数据";
                    ShowToast("打开失败：引擎无法加载这个 deck（详见日志）。原始目录没有被修改。");
                    return false;
                }

                _session = session;
                _workspace.Refresh(_session);
                _recent.Note(session.OriginalDeckPath);
                ReloadRecentDecks();

                if (Slides.Count > 0 && CurrentSlide == null) CurrentSlide = Slides[0];
                EngineStatus = "已连接 · 真实渲染";
                RaisePropertyChanged(nameof(IsEngineConnected));
                RaisePropertyChanged(nameof(EngineStatusBrush));
                RaisePropertyChanged(nameof(UseRealRender));
                StartModifiedTimer();
                RefreshDeckState();
                ShowToast($"已打开 {session.DeckRelativePath}：正在编辑工作副本，Ctrl+S 保存到原文件");

                _ = CheckLatexNoticeAsync();             // T04：缺包提示（不阻塞打开流程）
                return true;
            }
            catch (Exception ex)
            {
                if (session != null) _workspace.Discard(session);
                ShowToast("打开失败：" + ex.Message);
                return false;
            }
            finally
            {
                _deckBusy = false;
                BusyText = "";
            }
        }

        /// <summary>File ▸ 新建演示文稿…：选项对话框（T05 提供）→ 未保存确认 → 生成 → 立即打开。</summary>
        public async Task NewDeckAsync()
        {
            // 第二个参数是「deck 名称」的初值（不是窗口标题）：窗口标题由 NewDeckDialog.Title 自己给
            var dialog = new NewDeckDialogViewModel(DefaultNewDeckParent(), NewDeckDialogViewModel.DefaultName, Themes, CurrentTheme);
            if (NewDeckDialogHook != null)
            {
                if (!await NewDeckDialogHook(dialog)) return;
            }
            else if (!SimpleNewDeckPrompt(dialog))
            {
                return;
            }

            NewDeckResult result = await CreateAndOpenNewDeckAsync(dialog.ParentDir, dialog.Name, dialog.SelectedTheme);
            if (result.Failed) ShowToast(result.Error);
        }

        /// <summary>
        /// 生成并打开新 deck（无 UI，供命令与自动化测试直接调用）。
        /// <para>顺序很关键：<b>先问未保存三选一，再落盘</b>。否则用户选「取消」时会留下一个没人要的空 deck。</para>
        /// </summary>
        /// <param name="parentDir">父目录：会生成 <c>&lt;parentDir&gt;/&lt;name&gt;/deck</c> 与 <c>assets/</c>。</param>
        /// <param name="name">deck 名（也是文件夹名）。</param>
        /// <param name="theme">主题（决定 common.py 的 FONT/底色等）。</param>
        /// <param name="confirmUnsaved">是否先走未保存三选一（调用方已经问过就传 false）。</param>
        public async Task<NewDeckResult> CreateAndOpenNewDeckAsync(string parentDir, string name, DeckTheme theme,
                                                                  bool confirmUnsaved = true)
        {
            if (confirmUnsaved && !await ConfirmCloseAsync("新建演示文稿")) return NewDeckResult.Cancel();

            string error = NewDeckService.Create(parentDir, name, theme, out string deckPath);
            if (error != null) return NewDeckResult.Fail(error);

            // 到这里副本还没建，磁盘上只有刚生成的「原始」deck；紧接着建副本并打开它
            if (!await OpenDeckAsync(deckPath, confirmUnsaved: false))
                return NewDeckResult.Fail("演示文稿已生成，但打开失败（详见日志）");
            return NewDeckResult.Ok(deckPath);
        }

        /// <summary>没有对话框壳时的退化流程：只问保存位置，名称用「未命名演示文稿」+ 主题用当前主题。</summary>
        private bool SimpleNewDeckPrompt(NewDeckDialogViewModel dialog)
        {
            string parent = PickFolder("新建演示文稿：选择保存位置");
            if (string.IsNullOrEmpty(parent)) return false;
            dialog.ParentDir = parent;
            if (NewDeckService.DeckExists(parent, dialog.Name))
            {
                int n = 2;
                while (NewDeckService.DeckExists(parent, dialog.Name + " " + n)) n++;
                dialog.Name = dialog.Name + " " + n;
            }
            return true;
        }

        private static string DefaultNewDeckParent()
        {
            try
            {
                string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (!string.IsNullOrWhiteSpace(docs) && Directory.Exists(docs)) return docs;
            }
            catch { }
            return Environment.CurrentDirectory;
        }

        /// <summary>File ▸ 打开演示文稿或项目…：选文件夹后按目录结构定位 deck。</summary>
        public async Task OpenDeckDialogAsync()
        {
            string folder = PickFolder("打开演示文稿或项目：选择 deck 文件夹 / 项目文件夹");
            if (string.IsNullOrEmpty(folder)) return;
            await OpenDeckAsync(folder);
        }

        /// <summary>File ▸ 打开…（选 .manimble 包或 deck 里的文件）。</summary>
        public async Task OpenPackageDialogAsync()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "打开 .manimble 包或 deck 里的文件",
                Filter = "Manimble 包|*.manimble|Python 源码|*.py|所有文件|*.*",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog() != true) return;
            await OpenDeckAsync(dlg.FileName);
        }

        /// <summary>Open Recent：失效条目自动忘掉并提示。</summary>
        public async Task OpenRecentAsync(string deckPath)
        {
            if (string.IsNullOrWhiteSpace(deckPath)) return;
            if (!Directory.Exists(deckPath))
            {
                _recent.Forget(deckPath);
                ReloadRecentDecks();
                ShowToast("这个演示文稿已经不在了：" + deckPath);
                return;
            }
            await OpenDeckAsync(deckPath);
        }

        /// <summary>Open Recent ▸ Clear Menu。</summary>
        public void ClearRecent()
        {
            _recent.Clear();
            ReloadRecentDecks();
            ShowToast("已清空最近打开的演示文稿");
        }

        private void ReloadRecentDecks()
        {
            RecentDecks.Clear();
            foreach (RecentEntry e in _recent.Load()) RecentDecks.Add(e);
            RaisePropertyChanged(nameof(RecentDecks));
        }

        /// <summary>
        /// 关闭前确认（三选一）。返回 true 表示可以继续（已保存或选择丢弃）。
        /// 顺序对齐原版 confirmClose：草稿 → 未保存改动 → （保存分支里再问冲突）。
        /// </summary>
        public async Task<bool> ConfirmCloseAsync(string actionText = "关闭")
        {
            if (_session == null && !_engine.IsConnected) return true;

            // 1) 源码草稿：能应用就先应用；有语法错误则阻止（原版给「继续编辑 / 丢弃草稿」两选）
            if (SourceHasDraft)
            {
                if (await EnsureDraftAppliedAsync()) { }
                else
                {
                    bool discard = ConfirmOkCancelHook != null
                        ? ConfirmOkCancelHook("Python 代码还没有应用", "代码里还有未应用的改动且无法写回引擎。丢弃这份草稿继续吗？")
                        : MessageBox.Show("代码里还有未应用的改动且无法写回引擎。\n\n丢弃这份草稿继续吗？",
                                          "Python 代码还没有应用", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
                    if (!discard)
                    {
                        FocusSourcePane();
                        return false;
                    }
                    SourceText = _appliedSource ?? "";
                    SourceEditStatus = "草稿已丢弃";
                }
            }

            // 2) 未保存改动：保存 / 丢弃 / 取消
            RefreshDeckState();
            if (!IsModified) return true;

            switch (AskClose(actionText))
            {
                case CloseChoice.Save:
                    return await SaveCoreAsync();
                case CloseChoice.Discard:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>窗口关闭/退出时真正清理：取消作业 → 停引擎 → 删副本。</summary>
        public async Task CloseDeckAsync()
        {
            _closeApproved = true;
            _modifiedTimer?.Stop();
            BusyText = "正在关闭演示文稿…";
            try
            {
                await _engine.CancelBusy();
                await CloseEngineAndDiscardAsync();
                ResetDeckState();
                RefreshDeckState();
            }
            finally
            {
                BusyText = "";
            }
        }

        /// <summary>File ▸ 关闭窗口（Ctrl+W）：确认 → 清理副本 → 关窗口。</summary>
        public async Task CloseWindowAsync()
        {
            if (!await ConfirmCloseAsync("关闭窗口")) return;
            await CloseDeckAsync();
            Application.Current?.MainWindow?.Close();
        }

        /// <summary>File ▸ 退出：确认 → 清理副本 → 退出应用。</summary>
        public async Task QuitAsync()
        {
            if (!await ConfirmCloseAsync("退出 Manimble")) return;
            await CloseDeckAsync();
            Terminal.ShutdownAll();
            Application.Current?.Shutdown();
        }

        /// <summary>关闭确认是否已通过（T05 的 OnClosing 用它避免二次询问）。</summary>
        public bool CloseApproved => _closeApproved;

        /// <summary>
        /// 停掉引擎并删除旧副本（幂等）。
        /// <b>顺序必须先 Dispose 引擎</b>：manim 子进程会短暂占用副本目录，先删会失败（R5）。
        /// </summary>
        private async Task CloseEngineAndDiscardAsync()
        {
            _modifiedTimer?.Stop();
            WorkspaceSession old = _session;
            _session = null;
            try { _engine.Shutdown(); } catch { }
            if (old != null)
            {
                await Task.Delay(250);           // 给子进程一点时间释放句柄；Discard 内部还有递增重试
                _workspace.Discard(old);
            }
            RefreshDeckState();
        }

        /// <summary>把 Slides/选区/草稿/撤销栈清空（打开另一份 deck 前必须做，否则旧页会残留）。</summary>
        private void ResetDeckState()
        {
            FlushPendingNotesNow();
            _history.Clear();
            SelectedObject = null;                 // 先解绑旧对象的属性通知
            SelectedObjects.Clear();
            Slides.Clear();
            CurrentSlide = null;
            SourceText = "";
            _appliedSource = "";
            SourceDraftBackup = "";
            SourceLines.Clear();
            SourceEditStatus = "";
            RaisePropertyChanged(nameof(CurrentBuild));
            RaisePropertyChanged(nameof(CurrentObjects));
            RaisePropertyChanged(nameof(StatusBarText));
        }

        private void StartModifiedTimer()
        {
            if (_modifiedTimer != null) { _modifiedTimer.Start(); return; }
            // 对齐原版：每 2 秒比对一次（size+mtime 命中后不读盘，大图片不会卡）
            _modifiedTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _modifiedTimer.Tick += (s, e) => RefreshDeckState();
            _modifiedTimer.Start();
        }

        /// <summary>重算未保存状态并刷新标题星号 / 状态栏（定时器与各操作后调用）。</summary>
        public void RefreshDeckState()
        {
            if (_session != null)
            {
                try { _workspace.Refresh(_session); }
                catch (Exception ex) { StatusMessage = "检查改动失败：" + ex.Message; }
            }
            RaisePropertyChanged(nameof(IsModified));
            RaisePropertyChanged(nameof(ModifiedMark));
            RaisePropertyChanged(nameof(WindowTitle));
            RaisePropertyChanged(nameof(DeckDisplayName));
            RaisePropertyChanged(nameof(DeckStatusHint));
            RaisePropertyChanged(nameof(HasDeck));
            RaisePropertyChanged(nameof(StatusBarText));
        }

        private void BringMainWindowToFront()
        {
            Window win = Application.Current?.MainWindow;
            if (win == null) return;
            if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
            win.Activate();
        }

        #endregion

        #region 保存 / 还原 / 另存项目副本

        /// <summary>Ctrl+S：应用草稿 → settle → 只写回差异文件（冲突时二选一）。</summary>
        public Task<bool> SaveAsync() => SaveCoreAsync();

        private async Task<bool> SaveCoreAsync()
        {
            if (_session == null)
            {
                ShowToast("没有打开的演示文稿");
                return false;
            }
            if (_deckBusy)
            {
                ShowToast("正在处理上一个操作，请稍候");
                return false;
            }
            if (!await EnsureDraftAppliedAsync()) return false;

            _deckBusy = true;
            BusyText = "正在保存到原始文件…";
            try
            {
                await _engine.SettleAsync();                      // 队列排空后再读盘，否则写回旧源码
                SaveOutcome outcome = _workspace.Save(_session, false);

                if (!string.IsNullOrEmpty(outcome.OriginalMissingPath))
                {
                    ShowToast(outcome.Error);
                    return false;
                }

                if (outcome.Conflicts.Count > 0)
                {
                    var info = new ConflictInfo { Files = outcome.Conflicts };
                    if (AskConflict(info) != ConflictChoice.Overwrite)
                    {
                        ShowToast("已取消保存：原始目录里的文件没有被覆盖");
                        return false;
                    }
                    outcome = _workspace.Save(_session, true);
                }

                if (!outcome.Ok)
                {
                    ShowToast(outcome.Error ?? "保存失败");
                    return false;
                }

                RefreshDeckState();
                int total = outcome.SavedCount + outcome.DeletedCount;
                ShowToast(total == 0 ? "没有需要保存的改动" : $"已保存到原始文件：{outcome.SavedCount} 个写入、{outcome.DeletedCount} 个删除");
                return true;
            }
            catch (Exception ex)
            {
                ShowToast("保存失败：" + ex.Message);
                return false;
            }
            finally
            {
                _deckBusy = false;
                BusyText = "";
            }
        }

        /// <summary>Revert to Original：二次确认 → settle → 按 baseline 恢复副本 → 清撤销栈 → 重载。</summary>
        public async Task<bool> RevertAsync()
        {
            if (_session == null)
            {
                ShowToast("没有打开的演示文稿");
                return false;
            }
            if (!await EnsureDraftAppliedAsync()) return false;

            RefreshDeckState();
            if (!IsModified)
            {
                ShowToast("没有可还原的改动");
                return false;
            }
            if (!AskOkCancel("还原到原始文件",
                    "将丢弃自上次保存以来的全部改动，把副本恢复成原始文件的当前内容，并清空撤销历史。\n\n继续吗？"))
                return false;

            _deckBusy = true;
            BusyText = "正在还原到原始文件…";
            try
            {
                bool ok = await _engine.RevertEngineAsync(() =>
                {
                    _workspace.Revert(_session);        // 文件级：按 baseline 恢复 / 删新增
                    return true;
                });
                RefreshDeckState();
                if (!ok)
                {
                    ShowToast("还原失败（详见日志）");
                    return false;
                }
                ShowToast("已还原到原始文件");
                return true;
            }
            finally
            {
                _deckBusy = false;
                BusyText = "";
            }
        }

        /// <summary>
        /// Save Project Copy：把副本最新状态另存为完整项目文件夹（重名加 "(2)"）。
        /// <b>不切换</b>当前编辑对象，未保存改动原样保留。
        /// </summary>
        public async Task<string> SaveProjectCopyAsync(string parentDir = null, string name = null)
        {
            if (_session == null)
            {
                ShowToast("没有打开的演示文稿");
                return null;
            }
            if (!await EnsureDraftAppliedAsync()) return null;

            if (string.IsNullOrEmpty(parentDir))
            {
                parentDir = PickFolder("保存项目副本：选择位置");
                if (string.IsNullOrEmpty(parentDir)) return null;
            }

            _deckBusy = true;
            BusyText = "正在保存项目副本…";
            try
            {
                await _engine.SettleAsync();
                string deck = _workspace.SaveProjectCopy(_session, parentDir, name);
                if (deck == null)
                {
                    ShowToast("保存项目副本失败（详见日志）");
                    return null;
                }
                ShowToast("项目副本已保存：" + deck + "（当前仍在编辑原来那份）");
                return deck;
            }
            finally
            {
                _deckBusy = false;
                BusyText = "";
            }
        }

        #endregion

        #region 源码草稿守卫（P1-5）

        /// <summary>
        /// 所有生命周期入口的统一守卫：有草稿就先写回引擎；写不回去（语法错误/冲突）返回 false
        /// 并把提示与焦点落到源码窗格。没有草稿或已一致时直接返回 true。
        /// </summary>
        public async Task<bool> EnsureDraftAppliedAsync()
        {
            if (CurrentSlide == null) return true;
            if (!SourceHasDraft) return true;

            string edited = SourceText ?? "";
            if (_engine != null && _engine.IsConnected)
            {
                bool ok = await _engine.ApplyTextEditFromEditor(CurrentSlide, edited);
                if (!ok)
                {
                    SourceEditStatus = "草稿未应用（代码有错误）";
                    FocusSourcePane();
                    ShowToast("源码里还有错误，先修好再继续");
                    return false;
                }
                _appliedSource = edited;
                SourceDraftBackup = "";
                SourceEditStatus = "已应用并重新渲染";
                return true;
            }

            // 未连引擎：本地落地，保证后续动作看到的是最新代码
            _appliedSource = edited;
            CurrentSlide.Source = edited;
            RebuildSourceLines();
            SourceEditStatus = "已保存（本地）";
            return true;
        }

        private void FocusSourcePane()
        {
            InspectorVisible = true;
            SourceEditStatus = "请先修复这里的语法错误";
        }

        #endregion

        #region 路径解析（deck / 项目 / .manimble 包）

        /// <summary>
        /// 用户选的东西 → deck 目录：
        /// 项目根（含 deck/）→ deck；deck 目录本身（含 order.txt）→ 它自己；
        /// .py → 所在目录；.manimble → 解包到包旁同名文件夹再定位；其他 → null。
        /// </summary>
        public string ResolveDeckPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                ShowToast("没有选择演示文稿");
                return null;
            }
            string full = WorkspaceService.NormalizePath(path);

            if (Directory.Exists(full)) return ResolveDeckFolder(full);

            if (!File.Exists(full))
            {
                ShowToast("找不到这个位置：" + full);
                return null;
            }

            string ext = Path.GetExtension(full).ToLowerInvariant();
            if (ext == ".manimble")
            {
                string unpacked = UnpackPackage(full);
                return unpacked == null ? null : ResolveDeckFolder(unpacked);
            }
            if (ext == ".py")
            {
                string dir = Path.GetDirectoryName(full);
                return string.IsNullOrEmpty(dir) ? null : ResolveDeckFolder(dir);
            }

            ShowToast("这个文件不是 deck：请选择 deck 文件夹、项目文件夹或 .manimble 包");
            return null;
        }

        /// <summary>目录 → deck 目录（不抛异常，多猜一层，对齐原版 PortableProject.deckURL）。</summary>
        public static string ResolveDeckFolder(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return null;
            string leaf = Path.GetFileName(dir);
            if (string.Equals(leaf, "deck", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(Path.Combine(dir, "order.txt")))
                return dir;

            string nested = Path.Combine(dir, "deck");
            if (Directory.Exists(nested) && File.Exists(Path.Combine(nested, "order.txt"))) return nested;
            if (File.Exists(Path.Combine(dir, "order.txt"))) return dir;

            // 便携包/项目副本的根：里面只有一个子文件夹是真的 deck
            try
            {
                foreach (string sub in Directory.GetDirectories(dir))
                {
                    if (File.Exists(Path.Combine(sub, "order.txt"))) return sub;
                    string deep = Path.Combine(sub, "deck");
                    if (Directory.Exists(deep) && File.Exists(Path.Combine(deep, "order.txt"))) return deep;
                }
            }
            catch { }

            return dir;      // 兜底：当成 deck 目录（引擎自己会报错）
        }

        /// <summary>期望的 deck 目录（不解包、不写盘）；无法判断时返回 null。</summary>
        private static string TryPeekDeckFolder(string path)
        {
            try
            {
                string full = WorkspaceService.NormalizePath(path);
                return Directory.Exists(full) ? ResolveDeckFolder(full) : null;
            }
            catch { return null; }
        }

        /// <summary>把 .manimble（zip）解包到包旁的同名文件夹（重名加 (2)）。</summary>
        private string UnpackPackage(string archive)
        {
            string parent = Path.GetDirectoryName(archive);
            string baseName = Path.GetFileNameWithoutExtension(archive);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(baseName))
            {
                ShowToast("这个包的位置无法解包：" + archive);
                return null;
            }
            string target = Path.Combine(parent, baseName);
            int n = 2;
            while (Directory.Exists(target)) target = Path.Combine(parent, $"{baseName} ({n++})");

            // 解包会在磁盘上留下一个新文件夹，先问一次（原版同样是一次显式确认）
            if (!AskOkCancel("打开 .manimble 包",
                    "这是一个打包好的 Manimble 演示文稿。\n\n要把它解包成普通项目文件夹再打开吗？\n\n解包到：" + target))
                return null;
            try
            {
                System.IO.Compression.ZipFile.ExtractToDirectory(archive, target);
                ShowToast("已解包到：" + target);
                return target;
            }
            catch (Exception ex)
            {
                ShowToast("解包失败：" + ex.Message);
                return null;
            }
        }

        private static string PickFolder(string title)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = title, Multiselect = false };
            return dlg.ShowDialog() == true ? dlg.FolderName : null;
        }

        #endregion

        #region 生命周期命令（T05 的菜单可直接绑定）

        private ICommand _newDeckCommand;
        public ICommand NewDeckCommand => _newDeckCommand ??= new RelayCommand(_ => _ = NewDeckAsync());

        private ICommand _openDeckCommand;
        public ICommand OpenDeckCommand => _openDeckCommand ??= new RelayCommand(_ => _ = OpenDeckDialogAsync());

        private ICommand _openPackageCommand;
        public ICommand OpenPackageCommand => _openPackageCommand ??= new RelayCommand(_ => _ = OpenPackageDialogAsync());

        private ICommand _saveCommand;
        public ICommand SaveCommand => _saveCommand ??= new RelayCommand(_ => _ = SaveAsync());

        private ICommand _revertCommand;
        public ICommand RevertCommand => _revertCommand ??= new RelayCommand(_ => _ = RevertAsync());

        private ICommand _saveProjectCopyCommand;
        public ICommand SaveProjectCopyCommand => _saveProjectCopyCommand ??= new RelayCommand(_ => _ = SaveProjectCopyAsync());

        private ICommand _openRecentCommand;
        /// <summary>参数可以是 RecentEntry 或路径字符串。</summary>
        public ICommand OpenRecentCommand => _openRecentCommand ??= new RelayCommand(p =>
        {
            string path = p as string ?? (p as RecentEntry)?.Path;
            _ = OpenRecentAsync(path);
        });

        private ICommand _clearRecentCommand;
        public ICommand ClearRecentCommand => _clearRecentCommand ??= new RelayCommand(_ => ClearRecent());

        #endregion
    }
}
