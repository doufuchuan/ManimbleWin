using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Input;
using System.Windows.Threading;
using ManimbleWin.Helpers;
using ManimbleWin.Models;
using ManimbleWin.Services;

namespace ManimbleWin.ViewModels
{
    /// <summary>面板里的一组批注（同一页）。页码 0 表示"已删除的页"。</summary>
    public sealed class FeedbackGroup
    {
        public FeedbackGroup(string scene, int pageNumber, bool isMissing, string title,
                             IEnumerable<FeedbackNoteViewModel> notes)
        {
            Scene = scene ?? "";
            PageNumber = pageNumber;
            IsMissing = isMissing;
            Title = title ?? "";
            Notes = new ObservableCollection<FeedbackNoteViewModel>(notes ?? Enumerable.Empty<FeedbackNoteViewModel>());
        }

        public string Scene { get; }

        /// <summary>Slides 里的序号；"已删除的页" = 0。</summary>
        public int PageNumber { get; }

        public bool IsMissing { get; }

        /// <summary>"第 3 页 · Intro" / "已删除的页 · Gone"。</summary>
        public string Title { get; }

        public ObservableCollection<FeedbackNoteViewModel> Notes { get; }
    }

    /// <summary>
    /// 反馈（批注）子视图模型：面板状态 + 画布当前页集合 + 全部命令。
    /// <para>持有 <see cref="FeedbackStore"/> 的生命周期是 <b>deck 打开到 deck 关闭</b>，不是面板开合：
    /// 关掉面板再打开不能丢内存态，而按钮角标在面板关闭时也必须是准的（架构 §3.6）。</para>
    /// <para>唯一刷新入口是 <c>Store.Changed</c> → <see cref="SyncCurrentScene"/> + <see cref="RebuildGroups"/> +
    /// 计数刷新，三者一起走；分头订阅必然出现"删了画布上的批注、列表还在"（风险 R13）。</para>
    /// </summary>
    public sealed class FeedbackViewModel : ObservableObject
    {
        private readonly MainViewModel _owner;

        /// <summary>id → VM，跨页复用。画布的 <see cref="Notes"/> 与面板的 <see cref="Groups"/> 共用这些实例，
        /// 所以编辑态/闪烁在两个视图里天然一致。</summary>
        private readonly Dictionary<string, FeedbackNoteViewModel> _byId =
            new Dictionary<string, FeedbackNoteViewModel>(StringComparer.OrdinalIgnoreCase);

        private FeedbackStore _store;
        private FeedbackNoteViewModel _editing;
        private FeedbackNoteViewModel _selected;
        private bool _isPanelOpen;
        private bool _isAnnotating;

        /// <summary>true 时忽略 Store.Changed：用于"先把条目从内存/文件删掉，再异步刷新可视树"
        /// （在键盘焦点所在的元素上同步摘掉会在 WPF 里把焦点打成 null，风险 §5.4）。</summary>
        private bool _suppressSync;

        public FeedbackViewModel(MainViewModel owner)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));

            TogglePanelCommand = new RelayCommand(_ => TogglePanel());
            DoneCommand = new RelayCommand(_ => ClosePanel());
            ToggleAnnotateCommand = new RelayCommand(_ => SetAnnotating(!IsAnnotating));
            RemoveCommand = new RelayCommand(p => { if (p is FeedbackNoteViewModel note) Remove(note); });
            RemoveAllCommand = new RelayCommand(_ => RemoveAll());
            PreviousCommand = new RelayCommand(_ => Step(-1));
            NextCommand = new RelayCommand(_ => Step(1));
            GoToCommand = new RelayCommand(p => { if (p is FeedbackNoteViewModel note) GoTo(note); });
            OpenFolderCommand = new RelayCommand(_ => OpenFolder());
            ReloadCommand = new RelayCommand(_ => Reload());
        }

        #region 状态

        /// <summary>当前 deck 的批注库；没打开 deck 时为 null。</summary>
        public FeedbackStore Store => _store;

        public bool HasDeck => _store != null;

        public bool IsPanelOpen => _isPanelOpen;

        /// <summary>批注模式：画布光标变十字、点击捕获层可见、点幻灯片即新建。</summary>
        public bool IsAnnotating
        {
            get => _isAnnotating;
            // 给 ToggleButton 的 TwoWay 用：控件的"按下态"永远等于真实状态，不会各说各话
            set => SetAnnotating(value);
        }

        public bool IsReadOnly => _store != null && _store.IsReadOnly;

        /// <summary>能不能写（= 有 deck 且不是只读）。UI 只拿它做 <c>IsEnabled</c>，安全边界仍只在 Store 里。</summary>
        public bool CanEdit => _store != null && !_store.IsReadOnly;

        public bool HasError => _store != null && !string.IsNullOrEmpty(_store.Error);

        public string ErrorText => _store?.Error ?? "";

        public int Count => _store?.Count ?? 0;

        public bool IsEmpty => Count == 0;

        public bool HasBadge => Count > 0;

        /// <summary>角标文字；&gt;99 显示 <c>99+</c>（PRD FB-P0-02 验收 3）。</summary>
        public string BadgeText => Count > 99 ? "99+" : Count.ToString();

        /// <summary>工具行计数："2 of 5"（有选中）/ "共 5 条"（无选中）/ "还没有批注"。</summary>
        public string CounterText
        {
            get
            {
                if (Count == 0) return "还没有批注";
                if (_selected == null) return $"共 {Count} 条";
                List<FeedbackNoteViewModel> flat = Flatten();
                int index = flat.FindIndex(v => ReferenceEquals(v, _selected));
                return index < 0 ? $"共 {Count} 条" : $"{index + 1} of {Count}";
            }
        }

        /// <summary>面板底部常驻小字（PRD FB-P0-02，文案逐字对齐）。</summary>
        public string FooterHint => "批注随「保存」写入原目录；关闭不保存会丢弃";

        /// <summary>画布上的当前页批注（就地增删，不整体 Clear：Clear 会重建容器、丢焦点）。</summary>
        public ObservableCollection<FeedbackNoteViewModel> Notes { get; } = new ObservableCollection<FeedbackNoteViewModel>();

        /// <summary>面板按页分组的全部批注。</summary>
        public ObservableCollection<FeedbackGroup> Groups { get; } = new ObservableCollection<FeedbackGroup>();

        public FeedbackNoteViewModel Selected => _selected;

        #endregion

        #region 命令

        public ICommand TogglePanelCommand { get; }
        public ICommand DoneCommand { get; }
        public ICommand ToggleAnnotateCommand { get; }
        public ICommand RemoveCommand { get; }
        public ICommand RemoveAllCommand { get; }
        public ICommand PreviousCommand { get; }
        public ICommand NextCommand { get; }
        public ICommand GoToCommand { get; }
        public ICommand OpenFolderCommand { get; }

        /// <summary>设计增补：只读态下用户手工修好文件后不必重启应用（架构 §7.2）。</summary>
        public ICommand ReloadCommand { get; }

        #endregion

        #region 生命周期（由 MainViewModel 调）

        /// <summary>deck 打开成功：建 Store（构造里就读一次盘，角标在面板没开时也准）。</summary>
        public void Attach(WorkspaceSession session)
        {
            if (session == null) { Detach(); return; }

            if (_store != null) _store.Changed -= OnStoreChanged;
            ResetCaches();

            _store = new FeedbackStore(session.WorkDeckPath, session.OriginalDeckPath,
                                       message => _owner.StatusMessage = message);
            _store.Changed += OnStoreChanged;

            RefreshFromStore();
            RaisePropertyChanged(nameof(Store));
            RaisePropertyChanged(nameof(HasDeck));
        }

        /// <summary>
        /// deck 关闭 / 换 deck：清空一切。<b>不做任何写盘</b>——批注随工作副本一起被丢弃，这是特性。
        /// </summary>
        public void Detach()
        {
            if (_store != null)
            {
                _store.Changed -= OnStoreChanged;
                _store = null;
            }
            ResetCaches();
            SetAnnotating(false);
            SetPanelOpen(false);
            RefreshFromStore();
            RaisePropertyChanged(nameof(Store));
            RaisePropertyChanged(nameof(HasDeck));
        }

        /// <summary>
        /// Revert 之后重读工作副本。<b>必须调</b>：副本里的 feedback.json 可能刚被按基线删掉/还原，
        /// 内存不跟着回滚的话，用户下次改任意一条批注就会把已还原的内容写回去（风险 R6）。
        /// </summary>
        public void Reload()
        {
            if (_store == null) return;

            // 正在输入的草稿<b>直接放弃</b>，不能 EndEdit：Revert 刚刚把副本恢复成基线，
            // 这时候把草稿写回去等于把刚做的 Revert 又撤销了（风险 R6）。这里宁可丢一句话，
            // 也不能让"还原到原始文件"看起来没生效。
            if (_editing != null)
            {
                _editing.IsEditing = false;
                _editing = null;
            }
            _store.Reload();
        }

        /// <summary>由 <c>CurrentSlide</c> 的 setter 调：换页即换批注集合。先提交正在输入的那条，
        /// 否则它所属的容器会在切页时被移出可视树（焦点会跟着丢）。</summary>
        public void OnSceneChanged(string scene)
        {
            EndEdit();
            SyncCurrentScene();
        }

        /// <summary>拖动松手后落盘（拖动过程中只改内存，避免每一帧都写文件）。</summary>
        public void CommitMove(FeedbackNoteViewModel note)
        {
            if (note == null || _store == null || IsReadOnly) return;
            _store.UpdatePosition(note.Id, note.X, note.Y);
        }

        #endregion

        #region 面板开合 / 批注模式

        public void TogglePanel()
        {
            if (_isPanelOpen) ClosePanel();
            else OpenPanel();
        }

        public void OpenPanel()
        {
            if (_store == null)
            {
                _owner.ShowToast("先打开演示文稿");
                return;
            }
            // 右栏整列被关掉时面板是看不见的，先把它打开，否则用户会以为按钮坏了
            if (!_owner.InspectorVisible) _owner.InspectorVisible = true;
            SetPanelOpen(true);
        }

        public void ClosePanel()
        {
            if (!_isPanelOpen) return;
            EndEdit();
            SetAnnotating(false);
            SetPanelOpen(false);
        }

        public void SetAnnotating(bool on)
        {
            if (on)
            {
                if (_store == null) { _owner.ShowToast("先打开演示文稿"); RaisePropertyChanged(nameof(IsAnnotating)); return; }
                if (IsReadOnly) { _owner.ShowToast(ErrorText); RaisePropertyChanged(nameof(IsAnnotating)); return; }
            }
            else
            {
                EndEdit();
            }

            if (_isAnnotating == on) return;
            _isAnnotating = on;
            RaisePropertyChanged(nameof(IsAnnotating));
        }

        private void SetPanelOpen(bool open)
        {
            if (_isPanelOpen == open) return;
            _isPanelOpen = open;
            RaisePropertyChanged(nameof(IsPanelOpen));
        }

        #endregion

        #region 批注的增 / 改 / 删 / 跳转

        /// <summary>
        /// 在比例坐标处新建一条空批注（当前页 + 当前 build），立即落盘（PRD 允许"写了又删"的中间态）。
        /// 返回新条目对应的 VM，供调用方立刻进编辑并抢焦点。
        /// </summary>
        public FeedbackNoteViewModel AddAt(double x, double y)
        {
            if (_store == null || IsReadOnly) return null;
            Slide slide = _owner.CurrentSlide;
            if (slide == null) return null;                 // 没有页就没有 scene，写出来是一条"孤儿"批注

            FeedbackNote note = _store.Add(slide.Name, slide.CurrentBuildIndex, x, y, "");
            if (note == null) return null;

            return _byId.TryGetValue(note.Id, out FeedbackNoteViewModel vm) ? vm : null;
        }

        /// <summary>进入编辑：同一时刻只允许一条在编辑，先结束上一条。</summary>
        public void BeginEdit(FeedbackNoteViewModel note)
        {
            if (note == null || IsReadOnly) return;
            if (_editing != null && !ReferenceEquals(_editing, note)) EndEdit();

            note.BeginEditCore();
            _editing = note;
            Select(note);
        }

        /// <summary>
        /// 结束编辑：草稿非空 → 落盘（内容没变则不写）；草稿为空 → 该条被删除（空批注不落盘）。
        /// <b>幂等</b>：失焦 + 点别处 + 保存可能同一次操作里都触发，不能写两次盘。
        /// </summary>
        public void EndEdit()
        {
            if (_editing == null) return;

            FeedbackNoteViewModel note = _editing;
            _editing = null;
            note.IsEditing = false;

            string draft = note.DraftText ?? "";
            if (string.IsNullOrWhiteSpace(draft))
            {
                if (_store == null || IsReadOnly) return;
                string id = note.Id;
                if (_store.Find(id) == null) return;

                // 同步删（文件必须立刻正确），但可视树的刷新排到消息队列末尾——
                // 这次调用可能来自 TextBox 的 KeyDown/失焦，当场把控件摘掉会让焦点变成 null。
                RunWithoutSync(() => _store.Remove(id));

                // 面板/缓存/计数立刻对齐：否则「上一条/下一条」还能点到一条已经不存在的批注。
                // 只有画布上的 Notes 集合留到消息队列里再摘（那才是和焦点有关的那一份）。
                _byId.Remove(id);
                RebuildGroups();
                RefreshCounters();
                DispatchRefresh();
                return;
            }

            _store?.UpdateText(note.Id, draft);
        }

        /// <summary>
        /// 点窗口别处 → 提交正在输入的那条。只看一件事：点击源是不是<b>正在编辑的这个批注自己的 TextBox</b>
        /// （是就说明用户在移光标，不能提交）。
        /// </summary>
        public void CommitPendingEditIfOutside(object hitSource)
        {
            if (_editing == null) return;

            System.Windows.DependencyObject node = hitSource as System.Windows.DependencyObject;
            while (node != null)
            {
                if (node is System.Windows.Controls.TextBox box && ReferenceEquals(box.DataContext, _editing)) return;
                node = node is System.Windows.Media.Visual || node is System.Windows.Media.Media3D.Visual3D
                    ? System.Windows.Media.VisualTreeHelper.GetParent(node)
                    : System.Windows.LogicalTreeHelper.GetParent(node);
            }
            EndEdit();
        }

        /// <summary>删单条（不确认，不可撤销）。删的若是正在编辑的那条，先在消息队列里把焦点让出去。</summary>
        public bool Remove(FeedbackNoteViewModel note)
        {
            if (note == null || _store == null || IsReadOnly) return false;

            bool wasEditing = ReferenceEquals(_editing, note);
            if (wasEditing)
            {
                _editing = null;
                note.IsEditing = false;
                string id = note.Id;
                Keyboard.ClearFocus();      // 焦点搬走后再摘控件
                Dispatcher.CurrentDispatcher.BeginInvoke(
                    new Action(() => DeleteById(id)), DispatcherPriority.Background);
                return true;
            }

            return DeleteById(note.Id);
        }

        /// <summary>「全部删除」：必须走确认对话框（不可撤销）。取消时一条都不删、文件不改写。</summary>
        public bool RemoveAll()
        {
            if (_store == null || IsReadOnly) return false;
            EndEdit();

            int count = _store.Count;
            if (count == 0) return false;

            Func<string, string, bool> ask = _owner.ConfirmOkCancelHook;
            if (ask == null || !ask($"删除全部 {count} 条批注？", "此操作不可撤销")) return false;

            bool ok = _store.RemoveAll();
            if (ok) _owner.ShowToast($"已删除全部批注（{count} 条，不可撤销）");
            return ok;
        }

        /// <summary>点列表行：切到那一页并让该条在画布上闪一下。浏览/跳转零写入。</summary>
        public void GoTo(FeedbackNoteViewModel note)
        {
            if (note == null) return;

            Slide target = _owner.Slides.FirstOrDefault(
                s => s != null && string.Equals(s.Name, note.Scene, StringComparison.Ordinal));
            if (target == null)
            {
                _owner.ShowToast("这一页在源码里已不存在");
                return;
            }

            if (!ReferenceEquals(_owner.CurrentSlide, target)) _owner.CurrentSlide = target;
            Select(note);
            note.Flash();
        }

        /// <summary>上一条 / 下一条，环绕。</summary>
        public void Step(int delta)
        {
            List<FeedbackNoteViewModel> flat = Flatten();
            if (flat.Count == 0) return;

            int index = _selected == null ? -1 : flat.FindIndex(v => ReferenceEquals(v, _selected));
            int next;
            if (index < 0) next = delta >= 0 ? 0 : flat.Count - 1;
            else next = ((index + delta) % flat.Count + flat.Count) % flat.Count;

            GoTo(flat[next]);
        }

        #endregion

        #region 两个视图的同步

        /// <summary>
        /// 当前页的批注集合与 Store 对齐。<b>就地增删移</b>，绝不新建编辑中的 VM——
        /// 否则打字打到一半的 TextBox 会被重建、光标飞走（架构 §3.7）。
        /// </summary>
        public void SyncCurrentScene()
        {
            if (_store == null)
            {
                Notes.Clear();
                _byId.Clear();
                return;
            }

            IReadOnlyList<FeedbackNote> all = _store.Notes;

            // 1) 已经不在 Store 里的（被删 / 被 Revert 掉）：连同缓存一起摘掉
            foreach (string id in _byId.Keys.ToList())
            {
                if (all.Any(n => string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase))) continue;
                FeedbackNoteViewModel dead = _byId[id];
                if (ReferenceEquals(_editing, dead)) _editing = null;
                if (ReferenceEquals(_selected, dead))
                {
                    _selected = null;
                    RaisePropertyChanged(nameof(Selected));
                }
                dead.IsSelected = false;
                _byId.Remove(id);
            }

            // 2) 增量补齐 / 更新（编辑中的那条不覆盖草稿）
            foreach (FeedbackNote note in all)
            {
                if (!_byId.TryGetValue(note.Id, out FeedbackNoteViewModel vm))
                {
                    _byId[note.Id] = new FeedbackNoteViewModel(this, note);
                }
                else if (!vm.IsEditing &&
                         (vm.Text != (note.Text ?? "") || Math.Abs(vm.X - note.X) > 1e-9 || Math.Abs(vm.Y - note.Y) > 1e-9))
                {
                    vm.RefreshFromModel(note);
                }
            }

            // 3) 当前页按 created 升序就地重排
            string scene = _owner.CurrentSlide?.Name ?? "";
            var wanted = new List<FeedbackNoteViewModel>();
            foreach (FeedbackNote note in _store.NotesForScene(scene))
                if (_byId.TryGetValue(note.Id, out FeedbackNoteViewModel vm)) wanted.Add(vm);

            SyncOrdered(Notes, wanted);
        }

        /// <summary>
        /// 面板列表按页分组重建。行是只读展示（没有输入控件），整表重建无副作用；
        /// 重建后把 <see cref="Selected"/> 的淡底恢复回去。
        /// </summary>
        public void RebuildGroups()
        {
            Groups.Clear();

            var seenScenes = new HashSet<string>(StringComparer.Ordinal);

            // 组按页码升序（= Slides 顺序）
            foreach (Slide slide in _owner.Slides ?? Enumerable.Empty<Slide>())
            {
                if (slide == null) continue;
                seenScenes.Add(slide.Name ?? "");
                var notes = _byId.Values
                    .Where(v => string.Equals(v.Scene, slide.Name, StringComparison.Ordinal))
                    .OrderBy(v => v.CreatedUtc)
                    .ToList();
                if (notes.Count == 0) continue;

                Groups.Add(new FeedbackGroup(slide.Name, slide.Number, false,
                    $"第 {slide.Number} 页 · {slide.Name}", notes));
            }

            // 源码里已不存在的 scene：归到最后一组，仍可查看/删除（PRD FB-P0-05 验收 4）
            foreach (string scene in (_store?.SceneNames() ?? Array.Empty<string>()))
            {
                if (seenScenes.Contains(scene)) continue;
                var notes = _byId.Values
                    .Where(v => string.Equals(v.Scene, scene, StringComparison.Ordinal))
                    .OrderBy(v => v.CreatedUtc)
                    .ToList();
                if (notes.Count == 0) continue;

                string title = string.IsNullOrEmpty(scene) ? "已删除的页" : "已删除的页 · " + scene;
                Groups.Add(new FeedbackGroup(scene, 0, true, title, notes));
            }

            foreach (FeedbackGroup group in Groups)
                foreach (FeedbackNoteViewModel note in group.Notes)
                    note.IsSelected = ReferenceEquals(note, _selected);

            RaisePropertyChanged(nameof(CounterText));
        }

        #endregion

        #region 内部工具

        private void OnStoreChanged(object sender, EventArgs e)
        {
            if (_suppressSync) return;
            RefreshFromStore();
        }

        private void RefreshFromStore()
        {
            SyncCurrentScene();
            RebuildGroups();
            RefreshCounters();
        }

        private void RefreshCounters()
        {
            RaisePropertyChanged(nameof(Count));
            RaisePropertyChanged(nameof(IsEmpty));
            RaisePropertyChanged(nameof(HasBadge));
            RaisePropertyChanged(nameof(BadgeText));
            RaisePropertyChanged(nameof(CounterText));
            RaisePropertyChanged(nameof(IsReadOnly));
            RaisePropertyChanged(nameof(CanEdit));
            RaisePropertyChanged(nameof(HasError));
            RaisePropertyChanged(nameof(ErrorText));
            RaisePropertyChanged(nameof(HasDeck));

            foreach (FeedbackNoteViewModel note in _byId.Values) note.RefreshReadOnly();
        }

        private bool DeleteById(string id)
        {
            if (_store == null || IsReadOnly) return false;
            bool ok = _store.Remove(id);
            if (ok) _owner.ShowToast("已删除批注（不可撤销）");
            return ok;
        }

        private void Select(FeedbackNoteViewModel note)
        {
            if (ReferenceEquals(_selected, note)) return;

            if (_selected != null) _selected.IsSelected = false;
            _selected = note;
            if (_selected != null) _selected.IsSelected = true;

            RaisePropertyChanged(nameof(Selected));
            RaisePropertyChanged(nameof(CounterText));
        }

        private void ResetCaches()
        {
            _editing = null;
            _selected = null;
            _byId.Clear();
            Notes.Clear();
            Groups.Clear();
        }

        /// <summary>把 <paramref name="target"/> 就地调整成 <paramref name="wanted"/> 的顺序（不 Clear）。</summary>
        private static void SyncOrdered(ObservableCollection<FeedbackNoteViewModel> target,
                                        List<FeedbackNoteViewModel> wanted)
        {
            // 先摘掉已经不该显示的（倒着删，下标不跳）：这一步保证了"集合里不会残留已删除的批注"
            for (int i = target.Count - 1; i >= 0; i--)
                if (!wanted.Contains(target[i])) target.RemoveAt(i);

            for (int i = 0; i < wanted.Count; i++)
            {
                FeedbackNoteViewModel item = wanted[i];
                if (i < target.Count && ReferenceEquals(target[i], item)) continue;

                int existing = -1;
                for (int j = i; j < target.Count; j++)
                {
                    if (!ReferenceEquals(target[j], item)) continue;
                    existing = j;
                    break;
                }

                if (existing >= 0) target.Move(existing, i);
                else target.Insert(i, item);
            }

            while (target.Count > wanted.Count) target.RemoveAt(target.Count - 1);
        }

        /// <summary>遍历顺序 = 面板从上到下的顺序，<c>N of M</c> 与上一条/下一条都基于它。</summary>
        private List<FeedbackNoteViewModel> Flatten()
            => Groups.SelectMany(g => g.Notes).ToList();

        /// <summary>执行一段"只改数据、先别刷可视树"的操作。</summary>
        private void RunWithoutSync(Action action)
        {
            _suppressSync = true;
            try { action(); }
            finally { _suppressSync = false; }
        }

        /// <summary>把一次刷新排到消息队列末尾（当前调用栈可能还在控件的输入事件里）。</summary>
        private void DispatchRefresh()
        {
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(RefreshFromStore), DispatcherPriority.Background);
        }

        private void OpenFolder()
        {
            string dir = _store == null ? null : Path.GetDirectoryName(_store.FilePath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                _owner.ShowToast("找不到批注文件所在文件夹");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _owner.ShowToast("打开文件夹失败：" + ex.Message);
            }
        }

        #endregion
    }
}
