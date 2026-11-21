using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ManimbleWin.Helpers;
using ManimbleWin.Models;
using ManimbleWin.Services;

namespace ManimbleWin.ViewModels
{
    /// <summary>
    /// 主视图模型：驱动整个窗口。数据与交互均为原型模拟（不接真实 Manim 引擎），
    /// 但结构上预留了未来对接 engine/manimble JSON 协议的位置（见 ExecuteMenu 中标注）。
    /// </summary>
    public partial class MainViewModel : ObservableObject
    {
        #region 集合与当前状态

        public ObservableCollection<Slide> Slides { get; } = new ObservableCollection<Slide>();
        public ObservableCollection<DeckTheme> Themes { get; } = new ObservableCollection<DeckTheme>();

        private Slide _currentSlide;
        public Slide CurrentSlide
        {
            get => _currentSlide;
            set
            {
                if (SetProperty(ref _currentSlide, value) && value != null)
                {
                    // 切页前先把上一页未提交的备注写回，避免停手时间不够就切走导致丢失
                    FlushPendingNotesNow();
                    if (value.CurrentBuildIndex < 0 || value.CurrentBuildIndex >= value.Builds.Count)
                        value.CurrentBuildIndex = 0;
                    SelectedObject = null;
                    RaisePropertyChanged(nameof(CurrentBuild));
                    RaisePropertyChanged(nameof(CurrentObjects));
                    RaisePropertyChanged(nameof(UseRealRender));
                    RaisePropertyChanged(nameof(StatusBarText));
                    RebuildSourceLines();
                    SyncSourceText(force: true);   // 切页：旧页草稿不属于新页
                    // 公式对象：用本机 LaTeX 渲染成真实 PNG（不依赖引擎）
                    EnsureLatexRendered(value);
                    // 引擎已连接时，切换幻灯片后加载该场景的真实渲染帧
                    bool alreadyRendered = value.CurrentBuild != null && value.CurrentBuild.HasRealRender;
                    if (_engine != null && _engine.IsConnected && !alreadyRendered)
                        _ = _engine.LoadSceneInto(value);
                }
            }
        }

        private ObservableCollection<SourceLine> _sourceLines = new ObservableCollection<SourceLine>();
        public ObservableCollection<SourceLine> SourceLines { get => _sourceLines; set => SetProperty(ref _sourceLines, value); }

        private void RebuildSourceLines()
        {
            SourceLines.Clear();
            if (CurrentSlide == null || string.IsNullOrEmpty(CurrentSlide.Source)) return;
            int i = 1;
            foreach (var raw in CurrentSlide.Source.Split('\n'))
            {
                SourceLines.Add(new SourceLine
                {
                    Number = i,
                    Text = raw,
                    ColorBrush = ClassifyLine(raw),
                    Highlighted = SelectedObject != null && SelectedObject.SourceLine == i
                });
                i++;
            }
        }

        /// <summary>引擎已接受的源码版本；用于判断代码窗格里是否还有未应用的草稿。</summary>
        private string _appliedSource = "";

        /// <summary>代码窗格里有改动但还没成功写回引擎。</summary>
        public bool SourceHasDraft => CurrentSlide != null &&
            !string.Equals(SourceText, _appliedSource, StringComparison.Ordinal);

        // 冲突时用户选了「加载引擎版本」，先把草稿暂存这里，绝不在没确认前丢掉。
        private string _sourceDraftBackup = "";
        public string SourceDraftBackup
        {
            get => _sourceDraftBackup;
            private set
            {
                if (SetProperty(ref _sourceDraftBackup, value))
                    RaisePropertyChanged(nameof(HasSourceDraftBackup));
            }
        }
        public bool HasSourceDraftBackup => !string.IsNullOrEmpty(_sourceDraftBackup);

        /// <summary>
        /// 把当前页源码同步到可编辑文本框。后台写回触发的刷新不能覆盖用户草稿；
        /// 切页 / 显式还原传 force: true 才会丢弃草稿。
        /// </summary>
        private void SyncSourceText(bool force = false)
        {
            string incoming = CurrentSlide?.Source ?? "";
            if (!force && SourceHasDraft) return;
            SourceText = incoming;
            _appliedSource = incoming;
            SourceEditStatus = "已保存";
        }

        private static Brush ClassifyLine(string line)
        {
            string t = line.TrimStart();
            if (t.StartsWith("#")) return new SolidColorBrush(Color.FromRgb(0x6A, 0x99, 0x6A));
            if (t.StartsWith("class ") || t.StartsWith("def ")) return new SolidColorBrush(Color.FromRgb(0xC6, 0x8C, 0xFF));
            if (t.StartsWith("self.") || t.Contains("self.play") || t.Contains("self.add") || t.Contains("self.next_slide"))
                return new SolidColorBrush(Color.FromRgb(0x7E, 0xC6, 0x99));
            if (t.StartsWith("from ") || t.StartsWith("import ")) return new SolidColorBrush(Color.FromRgb(0x5B, 0xA9, 0xD8));
            if (t.Contains("\"") || t.Contains("r\"")) return new SolidColorBrush(Color.FromRgb(0xE8, 0xC0, 0x7A));
            return new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));
        }

        public Build CurrentBuild => CurrentSlide?.CurrentBuild;
        public ObservableCollection<CanvasObject> CurrentObjects => CurrentBuild?.Objects;

        private CanvasObject _selectedObject;
        /// <summary>主选中对象：检视器、方向键微调、源码行高亮都以它为准。</summary>
        public CanvasObject SelectedObject
        {
            get => _selectedObject;
            set
            {
                if (value == null) ClearSelection();
                else SelectSingleObject(value);
            }
        }

        /// <summary>多选集合（含主选中对象）。组合、对齐、分布、批量删除都读它。</summary>
        public ObservableCollection<CanvasObject> SelectedObjects { get; } = new ObservableCollection<CanvasObject>();

        public bool HasSelection => SelectedObjects.Count > 0;

        public string SelectionSummary
        {
            get
            {
                int n = SelectedObjects.Count;
                if (n == 0) return "";
                if (n == 1) return "1 个对象";
                return $"{n} 个对象";
            }
        }

        /// <summary>
        /// 统一设置选区。
        /// 为什么不用 SelectedObject 的属性赋值来做：一次操作要同时改「每个对象的 IsSelected」、
        /// 「选区集合」和「主选中对象」，分散在属性 setter 里很容易只改到一半，导致画布与命令看到的状态不一致。
        /// </summary>
        public void SetSelection(IEnumerable<CanvasObject> objects, CanvasObject primary = null)
        {
            var list = objects == null
                ? new List<CanvasObject>()
                : objects.Where(o => o != null).Distinct().ToList();

            if (primary == null || !list.Contains(primary)) primary = list.FirstOrDefault();

            // 组合在原型模式下就是「选中即同组」：把同组兄弟一起拉进选区
            var expanded = new List<CanvasObject>(list);
            foreach (var o in list.ToList())
            {
                if (!o.IsGrouped) continue;
                foreach (var s in Slides)
                    foreach (var b in s.Builds)
                        foreach (var ob in b.Objects)
                            if (ob.IsGrouped && ob.GroupId == o.GroupId && !expanded.Contains(ob)) expanded.Add(ob);
            }
            list = expanded;

            foreach (var s in Slides)
                foreach (var b in s.Builds)
                    foreach (var o in b.Objects)
                        o.IsSelected = list.Contains(o);

            SelectedObjects.Clear();
            foreach (var o in list) SelectedObjects.Add(o);

            var old = _selectedObject;
            if (!ReferenceEquals(old, primary))
            {
                if (old != null) old.PropertyChanged -= SelectedObject_PropertyChanged;
                _selectedObject = primary;
                if (primary != null) primary.PropertyChanged += SelectedObject_PropertyChanged;
                RaisePropertyChanged(nameof(SelectedObject));
            }

            RaiseSelectionChanged();
        }

        public void SelectSingleObject(CanvasObject o)
        {
            if (o == null) { ClearSelection(); return; }
            SetSelection(new[] { o }, o);
        }

        /// <summary>Shift 点选：已在选区里则移出，否则加入。</summary>
        public void ToggleSelection(CanvasObject o)
        {
            if (o == null) return;
            var list = SelectedObjects.ToList();
            if (list.Contains(o))
            {
                list.Remove(o);
                SetSelection(list, list.LastOrDefault());
            }
            else
            {
                list.Add(o);
                SetSelection(list, o);
            }
        }

        public void ClearSelection() => SetSelection(null, null);

        public void SelectAll()
        {
            var objs = CurrentObjects?.ToList() ?? new List<CanvasObject>();
            if (objs.Count == 0) { ShowToast("这一页没有可选的对象"); return; }
            SetSelection(objs, objs.Last());
            ShowToast($"已全选 {objs.Count} 个对象");
        }

        private void RaiseSelectionChanged()
        {
            RaisePropertyChanged(nameof(HasSelection));
            RaisePropertyChanged(nameof(SelectionSummary));
            RaisePropertyChanged(nameof(StatusBarText));
            RebuildSourceLines();
        }

        /// <summary>框选：矩形与对象包围盒相交即选中（只要沾到就算，符合大多数演示软件的手感）。</summary>
        public void SelectInRect(Rect rect, bool additive)
        {
            var hits = new List<CanvasObject>();
            foreach (var o in CurrentObjects ?? Enumerable.Empty<CanvasObject>())
            {
                var box = new Rect(o.X, o.Y, Math.Max(1, o.Width), Math.Max(1, o.Height));
                if (rect.IntersectsWith(box)) hits.Add(o);
            }
            if (additive) hits = SelectedObjects.Concat(hits).Distinct().ToList();
            SetSelection(hits, hits.LastOrDefault());
        }


        private DeckTheme _currentTheme;
        public DeckTheme CurrentTheme
        {
            get => _currentTheme;
            set
            {
                if (SetProperty(ref _currentTheme, value) && value != null && !EngineConnected)
                {
                    // 原型模式（没有引擎）时，换主题就地给所有文字换色，所见即所得。
                    // 连了引擎就不要再这么做：deck 的真实配色由引擎改写源码 + 重渲染决定，
                    // 这里抢先改本地颜色只会让画布和源码短暂不一致。
                    foreach (var s in Slides)
                        foreach (var b in s.Builds)
                            foreach (var o in b.Objects)
                                if (o.Kind == ObjectKind.Text || o.Kind == ObjectKind.Tex)
                                    o.Color = value.Text;
                }
            }
        }

        #endregion

        #region 视图开关与显示状态

        private bool _navigatorVisible = true;
        public bool NavigatorVisible { get => _navigatorVisible; set => SetProperty(ref _navigatorVisible, value); }

        private bool _inspectorVisible = true;
        public bool InspectorVisible { get => _inspectorVisible; set => SetProperty(ref _inspectorVisible, value); }

        private bool _drawerVisible = true;
        public bool DrawerVisible { get => _drawerVisible; set => SetProperty(ref _drawerVisible, value); }

        /// <summary>终端在抽屉里的标签序号（应用内唯一的终端入口）。</summary>
        public const int TerminalDrawerTab = 3;

        /// <summary>终端可见性等同于抽屉停在「终端 1」标签；保留属性供菜单/快捷键使用。</summary>
        public bool TerminalVisible
        {
            get => DrawerVisible && SelectedDrawerTab == TerminalDrawerTab;
            set
            {
                if (value) ShowDrawer(TerminalDrawerTab);
                else DrawerVisible = false;
            }
        }

        /// <summary>打开终端：切到抽屉的终端标签，不再额外占用中栏底部一整行。</summary>
        public void ShowTerminalPanel()
        {
            ShowDrawer(TerminalDrawerTab);
            ShowToast("终端已打开 · 输入 help 查看命令");
        }

        /// <summary>打开底部抽屉的某个标签，并保证它有可见高度（拖到 0 也能恢复）。</summary>
        public void ShowDrawer(int tab)
        {
            if (DrawerHeight.Value < 120) DrawerHeight = new System.Windows.GridLength(200);
            DrawerVisible = true;
            SelectedDrawerTab = tab;
        }

        private bool _isDarkMode = true;
        public bool IsDarkMode
        {
            get => _isDarkMode;
            set
            {
                if (SetProperty(ref _isDarkMode, value))
                    ThemeManager.Apply(value);
            }
        }

        private double _zoom = 0.72;
        public double Zoom
        {
            get => _zoom;
            set
            {
                if (SetProperty(ref _zoom, Math.Max(0.2, Math.Min(2.0, value))))
                {
                    RaisePropertyChanged(nameof(ZoomText));
                    RaisePropertyChanged(nameof(SlideDisplayWidth));
                    RaisePropertyChanged(nameof(SlideDisplayHeight));
                }
            }
        }
        public string ZoomText => ((int)Math.Round(Zoom * 100)) + "%";

        private bool _showGrid;
        public bool ShowGrid { get => _showGrid; set => SetProperty(ref _showGrid, value); }

        private bool _showRuler;
        public bool ShowRuler { get => _showRuler; set => SetProperty(ref _showRuler, value); }

        private bool _snapToObjects = true;
        public bool SnapToObjects { get => _snapToObjects; set => SetProperty(ref _snapToObjects, value); }

        private bool _showObjects = true;
        public bool ShowObjects { get => _showObjects; set => SetProperty(ref _showObjects, value); }

        private bool _showAnimations = true;
        public bool ShowAnimations { get => _showAnimations; set => SetProperty(ref _showAnimations, value); }

        private string _findText = "";
        public string FindText { get => _findText; set => SetProperty(ref _findText, value); }

        private string _statusMessage = "";
        public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

        private string _toastText = "";
        public string ToastText { get => _toastText; set => SetProperty(ref _toastText, value); }

        private bool _toastVisible;
        public bool ToastVisible { get => _toastVisible; set => SetProperty(ref _toastVisible, value); }

        private string _busyText = "";
        public string BusyText { get => _busyText; set { if (SetProperty(ref _busyText, value)) RaisePropertyChanged(nameof(BusyVisible)); } }
        public bool BusyVisible => !string.IsNullOrEmpty(BusyText);

        /// <summary>
        /// 状态栏：deck 名（未保存时带 *）+ 页码/构建/对象数。
        /// deck 名用「父目录 / deck」——很多项目的 deck 都叫 deck，只显示末级分不清。
        /// </summary>
        public string StatusBarText
        {
            get
            {
                if (CurrentSlide == null)
                    return _session == null ? "未加载演示文稿" : $"演示文稿: {DeckDisplayName}   ·   没有可显示的幻灯片";
                return $"演示文稿: {DeckDisplayName}{(IsModified ? " *" : "")}   ·   第 {CurrentSlide.Number}/{Slides.Count} 页   ·   构建 {CurrentSlide.CurrentBuildIndex + 1}/{CurrentSlide.Builds.Count}   ·   {CurrentObjects?.Count ?? 0} 个对象"
                      + (SelectedObjects.Count > 1 ? $"（已选 {SelectedObjects.Count}）" : "");
            }
        }

        // 画布标尺刻度（设计坐标 1280 宽，每 100 一大格）
        public ObservableCollection<RulerMark> RulerMarks { get; } = BuildRulerMarks();

        private static ObservableCollection<RulerMark> BuildRulerMarks()
        {
            var c = new ObservableCollection<RulerMark>();
            for (int x = 0; x <= 1280; x += 100)
                c.Add(new RulerMark { Pos = x, Tick = x % 500 == 0 ? 12 : 7, Label = (x % 500 == 0) ? x.ToString() : "" });
            return c;
        }

        // 空布局占位符（新建幻灯片时显示 "Click to add…"）
        public ObservableCollection<CanvasObject> PlaceholderObjects { get; }
            = new ObservableCollection<CanvasObject>();

        public double SlideDisplayWidth => 1280 * Zoom;
        public double SlideDisplayHeight => 720 * Zoom;

        // 面板尺寸（GridSplitter 双向绑定，WPF 记住拖动后的大小）
        private System.Windows.GridLength _navigatorWidth = new System.Windows.GridLength(230);
        public System.Windows.GridLength NavigatorWidth { get => _navigatorWidth; set => SetProperty(ref _navigatorWidth, value); }

        private System.Windows.GridLength _inspectorWidth = new System.Windows.GridLength(290);
        public System.Windows.GridLength InspectorWidth { get => _inspectorWidth; set => SetProperty(ref _inspectorWidth, value); }

        private System.Windows.GridLength _drawerHeight = new System.Windows.GridLength(180);
        public System.Windows.GridLength DrawerHeight { get => _drawerHeight; set => SetProperty(ref _drawerHeight, value); }

        private System.Windows.GridLength _sourceHeight = new System.Windows.GridLength(240);
        public System.Windows.GridLength SourceHeight { get => _sourceHeight; set => SetProperty(ref _sourceHeight, value); }

        #endregion

        #region 引擎集成（真实 Manim 渲染）

        private readonly EngineIntegration _engine;
        public EngineIntegration Engine => _engine;
        private bool _packaging;   // 打包进行中，避免菜单/工具栏重复点击

        // 拖动/微调后的坐标写回引擎做节流：连续拖动只在停下来后提交一次
        private readonly System.Windows.Threading.DispatcherTimer _engineSync;
        private Slide _syncSlide;
        private string _syncExpected;

        /// <summary>把对象当前坐标写回源码（拖动结束、方向键微调后调用）。</summary>
        public void CommitObjectMove(CanvasObject o)
        {
            if (o == null || CurrentSlide == null) return;
            string before = CurrentSlide.Source ?? "";
            string note;
            string updated;
            if (SourceCodeGenerator.TryUpdatePosition(before, o, 1280, 720, out updated, out note))
            {
                if (updated != before)
                {
                    CurrentSlide.Source = updated;
                    SyncSourceText();
                    RebuildSourceLines();
                    SourceEditStatus = "坐标已写回代码 · " + note;
                    StatusMessage = $"({Math.Round(o.X)}, {Math.Round(o.Y)}) → {note}";
                    ScheduleEngineSync(CurrentSlide, before);
                }
            }
            else if (!string.IsNullOrEmpty(note))
            {
                SourceEditStatus = "坐标未写回：" + note;
                StatusMessage = note;
            }
        }

        /// <summary>拖动过程中在状态栏实时显示坐标。</summary>
        public void ShowLivePosition(CanvasObject o)
        {
            if (o == null) return;
            StatusMessage = $"位置  x = {Math.Round(o.X)}, y = {Math.Round(o.Y)}   （松手后写回代码）";
        }

        private void ScheduleEngineSync(Slide slide, string expected)
        {
            if (_engine == null || !_engine.IsConnected || slide == null) return;
            _syncSlide = slide;
            _syncExpected = expected;
            _engineSync.Stop();
            _engineSync.Start();
        }

        private string _engineStatus = "未连接（原型数据）";
        public string EngineStatus { get => _engineStatus; set { if (SetProperty(ref _engineStatus, value)) RaisePropertyChanged(nameof(EngineStatusBrush)); } }

        public string EngineStatusBrush => _engine != null && _engine.IsConnected ? "#3FB950" : "#8B949E";
        public bool IsEngineConnected => _engine != null && _engine.IsConnected;

        /// <summary>当前 build 是否有引擎真实渲染帧（画布据此显示 PNG 或模拟对象）。</summary>
        public bool UseRealRender => CurrentBuild != null && CurrentBuild.HasRealRender;

        public ICommand ConnectEngineCommand { get; }
        public ICommand OpenDependencyCenterCommand { get; }
        public ICommand RenderFormulaCommand { get; }

        /// <summary>检视器里「重新渲染」：强制用 LaTeX 重排当前选中的公式。</summary>
        public ICommand RerenderFormulaCommand { get; }

        /// <summary>检视器里「编辑…」：打开带实时预览的公式编辑器修改选中公式。</summary>
        public ICommand EditFormulaCommand { get; }

        /// <summary>检视器里「更换图片…」：为选中的图片对象换一张图片（走引擎，一步撤销）。</summary>
        public ICommand ReplaceImageCommand { get; }

        /// <summary>「提取图片…」：列出 deck 中所有图片并复制到用户选择的文件夹。</summary>
        public ICommand ExtractImagesCommand { get; }

        #endregion

        #region 编辑核心：撤销 / 剪贴板 / 组合对齐 / 查找替换

        // 原型模式下的撤销栈；连接引擎后撤销交给引擎（它按文件差分，更精确）
        private readonly DeckHistory _history = new DeckHistory();
        private readonly ObjectClipboard _clipboard = ObjectClipboard.Shared;

        /// <summary>查找替换浮层的视图模型。</summary>
        public FindViewModel Find { get; }

        private bool EngineConnected => _engine != null && _engine.IsConnected;

        private List<CanvasObject> SelectionList => SelectedObjects.ToList();

        private DeckSnapshot Capture() => DeckHistory.Capture(Slides, Slides.IndexOf(CurrentSlide));

        /// <summary>
        /// 拖动开始时拍一份快照，松手时提交。
        /// 为什么拖动要单独开事务：拖动过程中每帧都在改坐标，若每帧都记一步历史，
        /// 一次拖动就会刷出几十步撤销；这里只在松手时记一步。
        /// </summary>
        public DeckSnapshot BeginMoveTransaction()
            => EngineConnected ? null : Capture();

        /// <summary>拖动结束：把这一整段位移记成一步撤销。</summary>
        public void EndMoveTransaction(DeckSnapshot snap, string label = "移动对象")
        {
            if (snap != null) _history.Push(label, snap);
        }

        /// <summary>供查找替换等外部调用方开一步可撤销的本地改动（引擎模式下返回 null，由引擎记录）。</summary>
        public DeckSnapshot BeginLocalUndoStep() => EngineConnected ? null : Capture();

        public void CommitLocalUndoStep(DeckSnapshot snap, string label)
        {
            if (snap != null) _history.Push(label, snap);
        }

        /// <summary>打开查找浮层（Ctrl+F / Ctrl+Alt+F）。</summary>
        public void OpenFind(bool withReplace)
        {
            Find.Open(withReplace);
            ShowToast(withReplace ? "查找和替换（Esc 关闭）" : "查找（Enter 下一个，Shift+Enter 上一个）");
        }

        /// <summary>源码被本地改动后同步界面；连接引擎时交给既有的节流写回。</summary>
        public void NoteSourceChanged(string before) => NoteSourceChangedFor(CurrentSlide, before);

        /// <summary>指定某一页的源码被改动（查找替换可能跨页，改的不一定是当前页）。</summary>
        public void NoteSourceChangedFor(Slide slide, string before)
        {
            if (slide == null) return;
            if (ReferenceEquals(slide, CurrentSlide))
            {
                SyncSourceText();
                RebuildSourceLines();
            }
            SourceEditStatus = "代码已更新";
            ScheduleEngineSync(slide, before);
        }

        /// <summary>备注被改动后刷新备注面板的显示（全部替换可能改到备注）。</summary>
        public void RaiseNotesChanged()
        {
            RaisePropertyChanged(nameof(SlideNotesText));
            RaisePropertyChanged(nameof(BuildNotesText));
            RaisePropertyChanged(nameof(AltTextText));
        }

        /// <summary>把某一页切到指定 build（查找跳转用）。</summary>
        public void SetBuildIndex(Slide slide, int index)
        {
            if (slide == null || slide.Builds.Count == 0) return;
            slide.CurrentBuildIndex = Math.Max(0, Math.Min(index, slide.Builds.Count - 1));
            if (ReferenceEquals(slide, CurrentSlide))
            {
                RaisePropertyChanged(nameof(CurrentBuild));
                RaisePropertyChanged(nameof(CurrentObjects));
                RaisePropertyChanged(nameof(UseRealRender));
                RebuildSourceLines();
            }
        }

        /// <summary>Esc 关闭浮层后把焦点还给画布，方向键才能继续微调。</summary>
        public void FocusCanvas() => Keyboard.Focus(Application.Current?.MainWindow);

        #region 撤销 / 重做

        public async Task UndoAsync()
        {
            if (EngineConnected)
            {
                try
                {
                    var r = await _engine.Undo();
                    if (IsEngineFalse(r, "ok"))
                    {
                        ShowToast(GetStr(r, "error") == "nothing to undo" ? "没有可撤销的操作" : "撤销被拒绝：" + GetStr(r, "error"));
                        return;
                    }
                    string label = GetStr(r, "label");
                    await RefreshAfterEngineEdit(null);
                    ShowToast(string.IsNullOrEmpty(label) ? "已撤销" : "已撤销：" + label);
                }
                catch (Exception ex) { ShowToast("撤销失败：" + ex.Message); }
                return;
            }

            if (!_history.CanUndo) { ShowToast("没有可撤销的操作"); return; }
            var snap = _history.Undo(Capture());
            RestoreSnapshot(snap);
            ShowToast("已撤销");
        }

        public async Task RedoAsync()
        {
            if (EngineConnected)
            {
                try
                {
                    var r = await _engine.Redo();
                    if (IsEngineFalse(r, "ok"))
                    {
                        ShowToast(GetStr(r, "error") == "nothing to redo" ? "没有可重做的操作" : "重做被拒绝：" + GetStr(r, "error"));
                        return;
                    }
                    string label = GetStr(r, "label");
                    await RefreshAfterEngineEdit(null);
                    ShowToast(string.IsNullOrEmpty(label) ? "已重做" : "已重做：" + label);
                }
                catch (Exception ex) { ShowToast("重做失败：" + ex.Message); }
                return;
            }

            if (!_history.CanRedo) { ShowToast("没有可重做的操作"); return; }
            var snap = _history.Redo(Capture());
            RestoreSnapshot(snap);
            ShowToast("已重做");
        }

        /// <summary>恢复快照。快照里的对象是新克隆的，所以必须清空选区并重建当前页的派生状态。</summary>
        private void RestoreSnapshot(DeckSnapshot snap)
        {
            if (snap == null) return;
            DeckHistory.Restore(snap, Slides);
            SelectedObjects.Clear();
            _selectedObject = null;
            RaisePropertyChanged(nameof(SelectedObject));
            if (Slides.Count == 0) { RaiseSelectionChanged(); return; }

            var target = Slides[Math.Max(0, Math.Min(snap.CurrentIndex, Slides.Count - 1))];
            CurrentSlide = null;
            CurrentSlide = target;
            RaiseSelectionChanged();
        }

        #endregion

        #region 复制 / 剪切 / 粘贴 / 删除

        public async Task CopySelectionAsync(bool alsoDelete = false)
        {
            var targets = SelectionList;
            if (targets.Count == 0) { ShowToast("请先选中对象"); return; }

            _clipboard.Clear();
            string source = CurrentSlide?.Source ?? "";
            foreach (var o in targets)
            {
                var c = o.Clone();
                c.IsSelected = false;
                _clipboard.Objects.Add(new CopiedObject
                {
                    Clone = c,
                    LineText = SourceCodeGenerator.LineTextFor(source, o.CodeId)
                });
            }
            _clipboard.DeckPath = _engine?.DeckPath ?? "";
            _clipboard.Scene = CurrentSlide?.Name ?? "";

            // 引擎能给出「可移植的代码」，同 deck 跨页粘贴时比本地近似代码更忠实
            if (EngineConnected && CurrentSlide != null)
            {
                try
                {
                    var oids = targets.Select(o => o.Oid).Where(s => !string.IsNullOrEmpty(s)).ToList();
                    var r = await _engine.CopyObjects(CurrentSlide.Name, CurrentSlide.CurrentBuildIndex, oids);
                    if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("items", out var items)
                        && items.ValueKind == JsonValueKind.Array)
                        _clipboard.SetEngineItems(items.EnumerateArray(), _engine.DeckPath, CurrentSlide.Name);
                }
                catch (Exception ex)
                {
                    // 引擎不给清单不影响使用：本页/跨页仍可用本地副本粘贴
                    SourceEditStatus = "引擎复制不可用，已改存本地副本：" + ex.Message;
                }
            }

            ShowToast(_clipboard.Summary + "（Ctrl+V 粘贴）");
            if (alsoDelete) await DeleteSelectionAsync();
        }

        public Task CutSelectionAsync() => CopySelectionAsync(alsoDelete: true);

        public async Task PasteAsync()
        {
            if (CurrentSlide == null || CurrentBuild == null) { ShowToast("没有可粘贴的页面"); return; }
            if (!_clipboard.HasContent) { ShowToast("剪贴板为空"); return; }

            bool sameDeck = string.Equals(_clipboard.DeckPath ?? "", _engine?.DeckPath ?? "", StringComparison.OrdinalIgnoreCase);
            if (EngineConnected && sameDeck && _clipboard.EngineItems.Count > 0)
            {
                bool ok = await PasteViaEngineAsync();
                if (ok) return;
                // 引擎拒绝（源码冲突 / 对象已不在）时退回本地粘贴，至少让用户看到副本
            }
            PasteLocal();
            await Task.CompletedTask;
        }

        /// <summary>
        /// 引擎粘贴：把 copy 给出的代码原样插回目标页，一次 insert_many 就是一步撤销。
        /// 与源页同一页时把副本错开一点，避免正好盖住原件。
        /// </summary>
        private async Task<bool> PasteViaEngineAsync()
        {
            try
            {
                int k = _clipboard.PasteCount;
                bool samePage = string.Equals(_clipboard.Scene, CurrentSlide.Name, StringComparison.Ordinal);
                // 第一次粘贴也要错开（PasteCount 初值 0，用 k+1 才得到 51.2px），否则副本正压在原件上看不出变化
                double d = samePage ? (k + 1) * 0.04 * 1280 : 0;
                double ux = 1280 / 14.22, uy = 720 / 8.0;

                var items = new List<object>();
                foreach (var it in _clipboard.EngineItems)
                {
                    string code = GetStr(it, "code");
                    if (string.IsNullOrEmpty(code)) continue;       // 由循环或合造出来的对象无法以代码粘贴
                    double x = GetDouble(it, "at", 0, 0) + d / ux;
                    double y = GetDouble(it, "at", 1, 0) - d / uy;
                    var box = GetDoubleArray(it, "box");
                    if (box.Length == 4)
                        for (int i = 0; i < 4; i++) box[i] += d;
                    items.Add(new Dictionary<string, object>
                    {
                        ["kind"] = "code",
                        ["code"] = code + $".move_to(({x.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}, {y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}, 0))",
                        ["box"] = box
                    });
                }
                if (items.Count == 0) return false;

                var r = await _engine.InsertMany(CurrentSlide.Name, CurrentSlide.CurrentBuildIndex, items);
                if (IsEngineFalse(r, "ok"))
                {
                    ShowToast("引擎未能粘贴：" + (GetStr(r, "error") ?? "未知原因"));
                    return false;
                }
                _clipboard.PasteCount = (k + 1) % 6;
                await _engine.Refresh(CurrentSlide);
                ShowToast($"已粘贴 {items.Count} 个对象");
                return true;
            }
            catch (Exception ex)
            {
                ShowToast("粘贴失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 本地粘贴：克隆对象 + 在目标页源码里补一行，两者必须同时做，否则下次渲染副本会消失。
        /// 优先复用原对象那一行源码（保留完整调用），只把行尾标记换成新 id，再按偏移改写 move_to。
        /// </summary>
        private void PasteLocal()
        {
            if (CurrentBuild == null || CurrentSlide == null) return;
            var snap = Capture();
            int k = _clipboard.PasteCount;
            _clipboard.PasteCount = (k + 1) % 6;
            // 与引擎路径同一套偏移：51.2 / 102.4 / 153.6 …（第一次粘贴就错开，不盖住原件）
            double d = (k + 1) * 0.04 * 1280;
            string before = CurrentSlide.Source ?? "";

            var added = new List<CanvasObject>();
            foreach (var item in _clipboard.Objects)
            {
                var c = item.Clone.Clone();
                c.Oid = Guid.NewGuid().ToString("N").Substring(0, 8);
                c.CodeId = SourceCodeGenerator.NewCodeId();
                c.X += d;
                c.Y += d;
                c.Z = CurrentBuild.Objects.Count;
                c.IsSelected = false;
                c.GroupId = "";
                CurrentBuild.Objects.Add(c);
                added.Add(c);

                string line = RetagLine(item.LineText, c.CodeId) ?? SourceCodeGenerator.LineFor(c, 1280, 720);
                CurrentSlide.Source = SourceCodeGenerator.AppendLine(CurrentSlide.Source, line);
                // 复用原行时坐标还是原件的：按新位置改写它的 move_to
                if (SourceCodeGenerator.TryUpdatePosition(CurrentSlide.Source, c, 1280, 720, out string fixedSource, out _))
                    CurrentSlide.Source = fixedSource;
            }

            NoteSourceChanged(before);
            SetSelection(added, added.LastOrDefault());
            _history.Push(added.Count > 1 ? $"粘贴 {added.Count} 个对象" : "粘贴对象", snap);
            ShowToast($"已粘贴 {added.Count} 个对象");
        }

        /// <summary>把源码行尾的 # manimble:old 标记换成新 id（行内其余部分一字不动）。</summary>
        private static string RetagLine(string lineText, string newCodeId)
        {
            if (string.IsNullOrEmpty(lineText) || string.IsNullOrEmpty(newCodeId)) return null;
            int at = lineText.LastIndexOf("# manimble", StringComparison.Ordinal);
            if (at < 0) return null;
            return lineText.Substring(0, at).TrimEnd() + "  # manimble:" + newCodeId;
        }

        public async Task DeleteSelectionAsync()
        {
            var targets = SelectionList;
            if (targets.Count == 0) { ShowToast("请先选中对象"); return; }

            if (EngineConnected)
            {
                try
                {
                    long? mark = await EngineUndoMarkAsync();
                    int done = 0;
                    string scene = CurrentSlide?.Name ?? "";
                    foreach (var o in targets)
                    {
                        var build = FindOwningBuild(o);
                        if (build == null || string.IsNullOrEmpty(scene)) continue;
                        var args = new Dictionary<string, object>
                        {
                            ["oid"] = o.Oid,
                            ["stop"] = CurrentSlide?.Builds.IndexOf(build) ?? 0
                        };
                        var r = await _engine.Edit(scene, "delete", args);
                        if (!IsEngineFalse(r, "ok")) done++;
                        else ShowToast("删除被拒绝：" + (GetStr(r, "error") ?? "未知原因"));
                    }
                    await EngineUndoMergeAsync(mark, done > 1 ? $"delete {done} objects" : "delete object");
                    await RefreshAfterEngineEdit(null);
                    ShowToast(done > 0 ? $"已删除 {done} 个对象" : "没有删掉任何对象");
                }
                catch (Exception ex) { ShowToast("删除失败：" + ex.Message); }
                return;
            }

            var snap = Capture();
            string before = CurrentSlide?.Source ?? "";
            int removed = 0;
            foreach (var o in targets)
            {
                var build = FindOwningBuild(o);
                if (build == null) continue;
                if (build.Objects.Remove(o)) removed++;
                if (!string.IsNullOrEmpty(o.CodeId) &&
                    SourceCodeGenerator.TryRemoveObjectLine(CurrentSlide.Source, o.CodeId, out string updated))
                    CurrentSlide.Source = updated;
            }
            ClearSelection();
            if (removed > 0)
            {
                NoteSourceChanged(before);
                _history.Push(removed > 1 ? $"删除 {removed} 个对象" : "删除对象", snap);
                ShowToast($"已删除 {removed} 个对象");
            }
            await Task.CompletedTask;
        }

        /// <summary>找出对象属于哪个 build（跨页粘贴后对象可能与当前页无关）。</summary>
        private Build FindOwningBuild(CanvasObject o)
        {
            if (o == null) return null;
            foreach (var s in Slides)
                foreach (var b in s.Builds)
                    if (b.Objects.Contains(o)) return b;
            return null;
        }

        #endregion

        #region 组合 / 层级 / 对齐 / 分布

        public async Task GroupSelectionAsync(bool ungroup)
        {
            var targets = SelectionList;
            if (targets.Count == 0) { ShowToast("请先选中对象"); return; }
            if (!ungroup && targets.Count < 2) { ShowToast("请选中两个以上对象再组合"); return; }

            if (EngineConnected)
            {
                try
                {
                    var args = new Dictionary<string, object>
                    {
                        ["oid"] = targets[0].Oid,
                        ["oids"] = targets.Select(o => o.Oid).ToArray()
                    };
                    var r = await _engine.Edit(CurrentSlide.Name, ungroup ? "ungroup" : "group", args);
                    if (IsEngineFalse(r, "ok")) { ShowToast("操作被拒绝：" + (GetStr(r, "error") ?? "未知原因")); return; }
                    await RefreshAfterEngineEdit(null);
                    ShowToast(ungroup ? "已取消组合" : $"已组合 {targets.Count} 个对象");
                }
                catch (Exception ex) { ShowToast("操作失败：" + ex.Message); }
                return;
            }

            var snap = Capture();
            if (ungroup)
            {
                foreach (var o in targets) o.GroupId = "";
                ShowToast("已取消组合");
            }
            else
            {
                string gid = Guid.NewGuid().ToString("N").Substring(0, 6);
                foreach (var o in targets) o.GroupId = gid;
                ShowToast($"已组合 {targets.Count} 个对象（点击其中任意一个即选中整组）");
            }
            _history.Push(ungroup ? "取消组合" : "组合", snap);
            RaiseSelectionChanged();
            await Task.CompletedTask;
        }

        /// <summary>层级调整：front / back / forward / backward。引擎写 set_z_index，原型模式只改画布顺序。</summary>
        public async Task ChangeOrderAsync(string how)
        {
            var targets = SelectionList;
            if (targets.Count == 0) { ShowToast("请先选中对象"); return; }
            string label = how switch
            {
                "front" => "置于顶层",
                "back" => "置于底层",
                "forward" => "上移一层",
                "backward" => "下移一层",
                _ => how
            };

            if (EngineConnected)
            {
                try
                {
                    var r = await _engine.EditOrder(targets.Select(o => o.Oid).ToList(), how,
                                                    CurrentSlide?.CurrentBuildIndex ?? 0);
                    if (IsEngineFalse(r, "ok")) { ShowToast("层级调整被拒绝：" + (GetStr(r, "error") ?? "未知原因")); return; }
                    await RefreshAfterEngineEdit(null);
                    ShowToast(label);
                }
                catch (Exception ex) { ShowToast("层级调整失败：" + ex.Message); }
                return;
            }

            if (CurrentBuild == null) return;
            var snap = Capture();
            var sorted = CurrentBuild.Objects.OrderBy(o => o.Z).ToList();
            for (int i = 0; i < sorted.Count; i++) sorted[i].Z = i;
            var set = new HashSet<CanvasObject>(targets);

            switch (how)
            {
                case "front":
                case "back":
                {
                    var movers = sorted.Where(o => set.Contains(o)).ToList();
                    var keep = sorted.Where(o => !set.Contains(o)).ToList();
                    var order = how == "front" ? keep.Concat(movers).ToList() : movers.Concat(keep).ToList();
                    for (int i = 0; i < order.Count; i++) order[i].Z = i;
                    break;
                }
                case "forward":
                    // 从最上面的开始处理，逐层越过它上面那个「不在选区里」的对象
                    for (int i = sorted.Count - 2; i >= 0; i--)
                        if (set.Contains(sorted[i]) && !set.Contains(sorted[i + 1]))
                        {
                            int z = sorted[i].Z;
                            sorted[i].Z = sorted[i + 1].Z;
                            sorted[i + 1].Z = z;
                        }
                    break;
                case "backward":
                    for (int i = 1; i < sorted.Count; i++)
                        if (set.Contains(sorted[i]) && !set.Contains(sorted[i - 1]))
                        {
                            int z = sorted[i].Z;
                            sorted[i].Z = sorted[i - 1].Z;
                            sorted[i - 1].Z = z;
                        }
                    break;
            }

            // 归一化，避免 Z 越拉越大导致后续排序不稳
            var final = CurrentBuild.Objects.OrderBy(o => o.Z).ToList();
            for (int i = 0; i < final.Count; i++) final[i].Z = i;

            _history.Push(label, snap);
            ShowToast(label + "（连接引擎后会写入 set_z_index）");
            await Task.CompletedTask;
        }

        public Task AlignSelectionAsync(string how)
        {
            var targets = SelectionList;
            if (targets.Count == 0) { ShowToast("请先选中对象"); return Task.CompletedTask; }
            string label = how switch
            {
                "left" => "左对齐",
                "center" => "水平居中",
                "right" => "右对齐",
                "top" => "顶端对齐",
                "middle" => "垂直居中",
                "bottom" => "底端对齐",
                _ => "对齐"
            };
            var deltas = ArrangeService.Align(targets, how, 1280, 720);
            return ApplyDeltasAsync(deltas, label);
        }

        public Task DistributeSelectionAsync(bool horizontal)
        {
            var targets = SelectionList;
            if (targets.Count < 3) { ShowToast("分布需要至少三个对象"); return Task.CompletedTask; }
            var deltas = ArrangeService.Distribute(targets, horizontal);
            return ApplyDeltasAsync(deltas, horizontal ? "水平分布" : "垂直分布");
        }

        /// <summary>
        /// 把一批位移落到画布与源码上。
        /// 引擎模式：每个对象一条 move，用 undo_mark/undo_merge 折成一步撤销；
        /// 原型模式：直接改坐标 + 逐条写回它自己的源码行。
        /// </summary>
        private async Task ApplyDeltasAsync(List<ArrangeDelta> deltas, string label)
        {
            if (deltas.Count == 0) { ShowToast(label + "：已经对齐/均匀了"); return; }

            if (EngineConnected)
            {
                try
                {
                    long? mark = await EngineUndoMarkAsync();
                    int failed = 0;
                    foreach (var d in deltas)
                    {
                        // 先动本地：画布立刻有反馈；随后按引擎状态刷新，失败的改动会被覆盖，不留脏数据
                        d.Object.X += d.Dx;
                        d.Object.Y += d.Dy;
                        var args = new Dictionary<string, object>
                        {
                            ["oid"] = d.Object.Oid,
                            ["stop"] = CurrentSlide?.CurrentBuildIndex ?? 0,
                            ["dx"] = d.Dx,
                            ["dy"] = d.Dy
                        };
                        if (IsEngineFalse(await _engine.Edit(CurrentSlide.Name, "move", args), "ok")) failed++;
                    }
                    await EngineUndoMergeAsync(mark, label);
                    await RefreshAfterEngineEdit(null);
                    ShowToast(failed == 0
                        ? $"{label} · {deltas.Count} 个对象"
                        : $"{label} · {deltas.Count - failed} 个已移动，{failed} 个未能移动（引擎拒绝了这一处）");
                }
                catch (Exception ex) { ShowToast(label + "失败：" + ex.Message); }
                return;
            }

            var snap = Capture();
            foreach (var d in deltas)
            {
                d.Object.X += d.Dx;
                d.Object.Y += d.Dy;
                CommitObjectMove(d.Object);
            }
            _history.Push(label, snap);
            ShowToast($"{label} · {deltas.Count} 个对象");
        }

        #endregion

        #region 引擎编辑辅助

        private async Task<long?> EngineUndoMarkAsync()
            => EngineIntegration.ParseUndoMark(await _engine.UndoMark());

        /// <summary>
        /// 把一串单对象编辑折成一步撤销。
        /// mark 用 long? 而不是数值哨兵：引擎的 _undo_seq 从 0 开始，undo_mark 正常返回 {mark: 0}，
        /// 若拿 0 当「没有标记」，连接引擎后的第一次多对象操作就永远不会合并（变成 N 步撤销）。
        /// </summary>
        private Task EngineUndoMergeAsync(long? mark, string label)
            => mark.HasValue ? _engine.UndoMerge(mark.Value, label) : Task.CompletedTask;

        /// <summary>引擎改动后刷新：scene 为空时刷新当前页。</summary>
        public async Task RefreshAfterEngineEdit(string scene)
        {
            if (!EngineConnected) return;
            var slide = string.IsNullOrEmpty(scene)
                ? CurrentSlide
                : Slides.FirstOrDefault(s => string.Equals(s.Name, scene, StringComparison.Ordinal));
            if (slide == null) return;
            await _engine.Refresh(slide);
        }

        /// <summary>全部替换这类跨页改动后，把每一页都刷新一遍。</summary>
        public async Task RefreshAllEngineSlides()
        {
            if (!EngineConnected) return;
            foreach (var s in Slides.ToList())
                await _engine.Refresh(s);
        }

        private static bool IsEngineFalse(System.Text.Json.JsonElement e, string name)
            => e.ValueKind == System.Text.Json.JsonValueKind.Object
               && e.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.False;

        private static string GetStr(System.Text.Json.JsonElement e, string name)
            => e.ValueKind == System.Text.Json.JsonValueKind.Object && e.TryGetProperty(name, out var v)
               && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;

        // 注意两处都先判 ValueKind == Number：.NET 8 的 JsonElement.TryGetDouble 对 null/字符串元素会抛
        // InvalidOperationException（只有 TryGetInt32 是安全的），引擎会给出 "fontSize": null 这类值。
        private static double GetDouble(System.Text.Json.JsonElement e, string name, int index, double def)
        {
            if (e.ValueKind != System.Text.Json.JsonValueKind.Object) return def;
            if (!e.TryGetProperty(name, out var arr) || arr.ValueKind != System.Text.Json.JsonValueKind.Array) return def;
            var list = arr.EnumerateArray().ToList();
            return index < list.Count && IsNumber(list[index]) && list[index].TryGetDouble(out double d) ? d : def;
        }

        private static double[] GetDoubleArray(System.Text.Json.JsonElement e, string name)
        {
            if (e.ValueKind != System.Text.Json.JsonValueKind.Object) return Array.Empty<double>();
            if (!e.TryGetProperty(name, out var arr) || arr.ValueKind != System.Text.Json.JsonValueKind.Array)
                return Array.Empty<double>();
            return arr.EnumerateArray().Select(v => IsNumber(v) && v.TryGetDouble(out double d) ? d : 0).ToArray();
        }

        private static bool IsNumber(System.Text.Json.JsonElement v)
            => v.ValueKind == System.Text.Json.JsonValueKind.Number;

        #endregion

        #endregion

        #region 源码编辑 / 预览 / 终端

        /// <summary>可编辑的当前页 Python 源码（与 SourcePanel 双向绑定）。</summary>
        private string _sourceText = "";
        public string SourceText
        {
            get => _sourceText;
            set
            {
                if (SetProperty(ref _sourceText, value))
                {
                    int n = string.IsNullOrEmpty(value) ? 0 : value.Split('\n').Length;
                    SourceLineNumbers = string.Join("\n", Enumerable.Range(1, n));
                }
            }
        }
        private string _sourceLineNumbers = "1";
        public string SourceLineNumbers { get => _sourceLineNumbers; set => SetProperty(ref _sourceLineNumbers, value); }

        private string _sourceEditStatus = "";
        public string SourceEditStatus { get => _sourceEditStatus; set => SetProperty(ref _sourceEditStatus, value); }

        public ICommand ApplySourceEditCommand { get; }
        public ICommand RevertSourceEditCommand { get; }
        public ICommand RestoreSourceDraftCommand { get; }
        public ICommand OpenPreviewCommand { get; }

        /// <summary>交互式终端视图模型。</summary>
        public TerminalViewModel Terminal { get; }

        /// <summary>
        /// 当前 deck 所在文件夹（终端 pwd、图片拷贝都用它）。
        /// 编辑期一律指向<b>工作副本</b>：写进 assets 的图片要落在副本里，才会被 Save 写回原目录。
        /// 没有会话时才回退到示例 deck（原型模式）。
        /// </summary>
        public string DeckFolder => _session?.WorkDeckPath ?? EnvironmentService.SampleDeck;

        #endregion

        #region 抽屉（备注 / 替代文本 / 终端）

        public ObservableCollection<string> DrawerTabs { get; } = new ObservableCollection<string>
        {
            "Slide Notes", "Build Notes", "Alt Text", "Terminal 1"
        };

        private int _selectedDrawerTab;
        public int SelectedDrawerTab { get => _selectedDrawerTab; set => SetProperty(ref _selectedDrawerTab, value); }

        // 备注写回：输入时只改内存，停手 800ms（或切页/失焦）后再发 RPC，避免每敲一个字都重渲染。
        // 纯内存模式（未连引擎）不启动定时器，行为与以前一致。
        private readonly System.Windows.Threading.DispatcherTimer _notesTimer;
        private Slide _notesSlide;
        private readonly HashSet<string> _pendingNoteKinds = new HashSet<string>();
        private string _slideNotesPending = "";
        private string _buildNotesPending = "";
        private string _altTextPending = "";
        private int? _buildNotesStopPending;

        /// <summary>引擎重渲染某页时用它判断要不要跳过备注回写，避免覆盖用户正在输入的内容。</summary>
        public bool HasPendingNotes(Slide slide) => slide != null && ReferenceEquals(_notesSlide, slide) && _pendingNoteKinds.Count > 0;

        public string SlideNotesText
        {
            get => CurrentSlide?.Builds.FirstOrDefault()?.Notes ?? "";
            set
            {
                if (CurrentSlide?.Builds.Count > 0)
                {
                    CurrentSlide.Builds[0].Notes = value;
                    RaisePropertyChanged(nameof(SlideNotesText));
                    ScheduleNoteWriteBack("slide", value);
                }
            }
        }

        public string BuildNotesText
        {
            get => CurrentBuild?.BuildNotes ?? "";
            set
            {
                if (CurrentBuild != null)
                {
                    CurrentBuild.BuildNotes = value;
                    RaisePropertyChanged(nameof(BuildNotesText));
                    ScheduleNoteWriteBack("build", value, CurrentSlide.CurrentBuildIndex);
                }
            }
        }

        public string AltTextText
        {
            get => CurrentSlide?.AltText ?? "";
            set
            {
                if (CurrentSlide != null)
                {
                    CurrentSlide.AltText = value;
                    RaisePropertyChanged(nameof(AltTextText));
                    ScheduleNoteWriteBack("alt", value);
                }
            }
        }

        private void ScheduleNoteWriteBack(string kind, string value, int? stop = null)
        {
            if (!EngineConnected || CurrentSlide == null) return;   // 未连引擎：保持纯内存
            if (kind == "slide") _slideNotesPending = value ?? "";
            else if (kind == "build") { _buildNotesPending = value ?? ""; _buildNotesStopPending = stop; }
            else _altTextPending = value ?? "";
            _notesSlide = CurrentSlide;
            _pendingNoteKinds.Add(kind);
            _notesTimer.Stop();
            _notesTimer.Start();
        }

        private void FlushPendingNotesNow()
        {
            if (_notesTimer == null || _pendingNoteKinds.Count == 0) return;
            _notesTimer.Stop();
            var slide = _notesSlide;
            var kinds = new HashSet<string>(_pendingNoteKinds);
            string slideNotes = _slideNotesPending, buildNotes = _buildNotesPending, alt = _altTextPending;
            int? stop = _buildNotesStopPending;
            _pendingNoteKinds.Clear();
            _notesSlide = null;
            if (slide == null || !EngineConnected) return;
            _ = WriteBackNotes(slide, kinds, slideNotes, buildNotes, stop, alt);
        }

        /// <summary>立即提交未写回的备注（文本域失焦、切页、打开演讲者视图时调用）。</summary>
        public void FlushNotes() => FlushPendingNotesNow();

        /// <summary>打开演讲者视图前调用：先把未提交的备注写回，避免静帧备注与抽屉里的内容不一致。</summary>
        public void FlushNotesForPresenter() => FlushPendingNotesNow();

        private async Task WriteBackNotes(Slide slide, HashSet<string> kinds, string slideNotes,
                                          string buildNotes, int? stop, string alt)
        {
            string scene = slide.Name;
            bool failed = false;
            try
            {
                if (kinds.Contains("slide") && !await _engine.NotesSet(scene, slideNotes, null)) failed = true;
                if (kinds.Contains("build") && !await _engine.NotesSet(scene, buildNotes, stop)) failed = true;
                if (kinds.Contains("alt") && !await _engine.AltTextSet(scene, alt)) failed = true;
            }
            catch (Exception ex)
            {
                ShowToast("备注写回失败：" + ex.Message);
                return;
            }
            // 只报告结果，不把引擎的值写回模型：用户此刻的输入必须原样保留在编辑器里。
            if (failed) ShowToast("备注写回失败，输入内容已保留（稍后会自动重试或可再次编辑）");
        }

        #endregion

        #region 命令

        public ICommand MenuCommand { get; }
        public ICommand ToolbarCommand { get; }
        public ICommand SelectSlideCommand { get; }
        public ICommand SelectBuildCommand { get; }
        public ICommand SelectObjectCommand { get; }
        public ICommand SetThemeCommand { get; }
        public ICommand NudgeCommand { get; }
        public ICommand OpenFindCommand { get; }

        #endregion

        public MainViewModel()
        {
            MenuCommand = new RelayCommand(p => ExecuteMenu(p?.ToString() ?? ""));
            ToolbarCommand = new RelayCommand(p => ExecuteToolbar(p?.ToString() ?? ""));
            SelectSlideCommand = new RelayCommand(p => { if (p is Slide s) { CurrentSlide = s; ShowToast($"已选择幻灯片：{s.Name}"); } });
            SelectBuildCommand = new RelayCommand(p =>
            {
                if (p is BuildTag tag && tag.Slide != null)
                {
                    CurrentSlide = tag.Slide;
                    tag.Slide.CurrentBuildIndex = tag.Index;
                    RaisePropertyChanged(nameof(CurrentBuild));
                    RaisePropertyChanged(nameof(CurrentObjects));
                    ShowToast($"正在显示 {tag.Slide.Name} 的构建 {tag.Index + 1}");
                }
            });
            SelectObjectCommand = new RelayCommand(p => { if (p is CanvasObject o) SelectedObject = o; });
            SetThemeCommand = new RelayCommand(p => { if (p is DeckTheme t) ApplyTheme(t); });
            NudgeCommand = new RelayCommand(p => Nudge(p?.ToString() ?? ""));
            OpenFindCommand = new RelayCommand(_ => OpenFind(false));
            ConnectEngineCommand = new RelayCommand(_ => _ = ConnectEngine());
            OpenDependencyCenterCommand = new RelayCommand(_ => OpenDependencyCenter(false));
            RenderFormulaCommand = new RelayCommand(p => _ = RenderFormulaInteractive(p?.ToString()));
            RerenderFormulaCommand = new RelayCommand(_ => RerenderSelectedFormula());
            EditFormulaCommand = new RelayCommand(_ => EditSelectedFormula());
            ReplaceImageCommand = new RelayCommand(_ => _ = ReplaceSelectedImage());
            ExtractImagesCommand = new RelayCommand(_ => _ = ExtractImages());
            ApplySourceEditCommand = new RelayCommand(_ => ApplySourceEdit());
            RevertSourceEditCommand = new RelayCommand(_ => RevertSourceEdit());
            RestoreSourceDraftCommand = new RelayCommand(_ => RestoreSourceDraft());
            OpenPreviewCommand = new RelayCommand(_ => OpenPreview());
            Terminal = new TerminalViewModel(this);
            Find = new FindViewModel(this);

            BuildThemes();
            BuildSampleDeck();
            ThemeManager.Apply(IsDarkMode);

            // 让画布主题跟随 deck 主题
            CurrentTheme = Themes.FirstOrDefault(t => t.Id == "midnight") ?? Themes.FirstOrDefault();
            _deckThemeApplied = CurrentTheme;
            CurrentSlide = Slides.FirstOrDefault();

            // 引擎桥接
            _engine = new EngineIntegration(this);
            _engine.Log += msg => ShowToast(msg);

            // 工作副本与最近文档（日志走状态栏，不弹 Toast，避免每次保存都刷屏）
            _workspace = new WorkspaceService(msg => StatusMessage = msg);
            _recent = new RecentDecksService(msg => StatusMessage = msg);
            ReloadRecentDecks();

            // 坐标写回引擎的节流器（拖动停手 0.7 秒后提交一次）
            _engineSync = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _engineSync.Tick += async (s, e) =>
            {
                _engineSync.Stop();
                Slide slide = _syncSlide;
                string expected = _syncExpected;
                _syncSlide = null;
                _syncExpected = null;
                if (slide == null || _engine == null || !_engine.IsConnected) return;
                string text = slide.Source;
                if (string.IsNullOrEmpty(text) || text == expected) return;
                SourceEditStatus = "正在把新坐标写回引擎…";
                bool ok = await _engine.ApplyTextEdit(slide, expected, text);
                SourceEditStatus = ok ? "坐标已写回引擎并重新渲染" : "已写回本地代码（引擎未接受该修改）";
            };

            // 检视器改公式时的防抖：停手 400ms 才排版一次，避免每敲一个字符都跑 LaTeX
            _texEditTimer.Tick += (s, e) =>
            {
                _texEditTimer.Stop();
                var target = _texEditTarget;
                _texEditTarget = null;
                if (target != null) ApplyFormulaEdit(target);
            };

            // 备注写回的防抖：抽屉里停手 800ms 才把备注/替代文本写回引擎并重渲染
            _notesTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _notesTimer.Tick += (s, e) => FlushPendingNotesNow();

            // 首次启动：窗口呈现后再弹出环境准备向导，避免阻塞主窗口构造。
            // 环境就绪时接着把示例 deck 打开成工作副本 —— 用户第一笔编辑就落在副本里，
            // 而不是直接改 samples/hello（那是仓库里的原件）。
            if (!SuppressStartupAutoOpen)
            {
                Application.Current?.Dispatcher.BeginInvoke(new Action(async () =>
                {
                    if (!AppFlags.SetupSeen)
                    {
                        OpenDependencyCenter(true);
                        AppFlags.SetupSeen = true;
                    }
                    if (_session == null && !_engine.IsConnected && EnvironmentService.FindPython(out _) != null)
                        await OpenDeckAsync(EnvironmentService.SampleDeck, confirmUnsaved: false);
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        #region 数据构建

        /// <summary>
        /// 主题清单来自 engine/manimble/themes/*.py（8 套的真实色板），不再在这里硬编码：
        /// 硬编码的色值早就和引擎里的主题文件对不上（例如 Paper 的背景在文件里是 #FFFFFF）。
        /// 读不到文件时退回到一套 Midnight，保证界面不会空着。
        /// </summary>
        private void BuildThemes()
        {
            IReadOnlyList<DeckTheme> loaded = DeckThemeService.LoadAll();
            foreach (DeckTheme t in loaded) Themes.Add(t);
            if (Themes.Count == 0)
            {
                Themes.Add(new DeckTheme
                {
                    Id = "midnight", Name = "Midnight", Background = "#000000", Text = "#FFFFFF",
                    Muted = "#BBBBBB", Accent = "#7C5CFF", Font = "Helvetica Neue"
                });
            }
        }

        private CanvasObject Txt(string text, double x, double y, double w, double h, double size, string color, int line, bool bold = false)
            => new CanvasObject
            {
                Kind = ObjectKind.Text, Value = text, DisplayText = text,
                X = x, Y = y, Width = w, Height = h, FontSize = size,
                Color = color, Bold = bold, SourceLine = line, Loc = $"slides.py:{line}"
            };

        private void BuildSampleDeck()
        {
            // ---- Slide 1: Welcome ----
            var welcome = NewSlide("Welcome", 1,
@"class Welcome(Deck):
    def construct(self):
        title = label(TITLE, 56)
        subtitle = label(SUBTITLE, 28, GREY_B)
        rule = Line(LEFT * 2.2, RIGHT * 2.2, color=ACCENT, stroke_width=5)
        VGroup(title, rule, subtitle).arrange(DOWN, buff=0.35)
        self.play(FadeIn(title), Create(rule), FadeIn(subtitle), run_time=0.8)");
            var wb = welcome.Builds[0];
            wb.Objects.Add(Txt("你好，Manimble", 340, 250, 600, 90, 56, "#FFFFFF", 3, true));
            wb.Objects.Add(new CanvasObject { Kind = ObjectKind.Shape, ShapeLabel = "Line", X = 470, Y = 352, Width = 340, Height = 3, Color = "#7C5CFF", SourceLine = 5 });
            wb.Objects.Add(Txt("单击幻灯片即可编辑", 400, 380, 480, 50, 28, "#9AA0A6", 4));
            welcome.AltText = "标题页“你好，Manimble”，副标题为“单击幻灯片即可编辑”。";
            wb.Notes = "欢迎体验演示文稿，这里的每一页都是 Python 代码。";

            // ---- Slide 2: Shapes ----
            var shapes = NewSlide("Shapes", 2,
@"class Shapes(Deck):
    def construct(self):
        heading = label(""Shapes, lines and text"", 40).to_corner(UL, buff=0.5)
        box = Rectangle(width=3.2, height=1.8, color=ACCENT, stroke_width=5).move_to((-3.5, 0, 0))
        circle = Circle(radius=1.0, color=ORANGE, stroke_width=5).move_to((3.5, 0, 0))
        arrow = Arrow((-1.7, 0, 0), (2.2, 0, 0), buff=0, color=WHITE)
        caption = label(""drag, resize, retype"", 26, GREY_B).move_to((0, -2.4, 0))
        self.add(heading, box, circle, arrow, caption)");
            var sb = shapes.Builds[0];
            sb.Objects.Add(Txt("形状、线条与文本", 60, 40, 520, 56, 40, "#FFFFFF", 2, true));
            sb.Objects.Add(new CanvasObject { Kind = ObjectKind.Shape, ShapeLabel = "Rectangle", X = 90, Y = 250, Width = 230, Height = 150, Color = "#7C5CFF", SourceLine = 3 });
            sb.Objects.Add(new CanvasObject { Kind = ObjectKind.Shape, ShapeLabel = "Circle", X = 900, Y = 250, Width = 150, Height = 150, Color = "#FF9F0A", SourceLine = 4 });
            sb.Objects.Add(new CanvasObject { Kind = ObjectKind.Shape, ShapeLabel = "Arrow", X = 360, Y = 320, Width = 480, Height = 3, Color = "#FFFFFF", SourceLine = 5 });
            sb.Objects.Add(Txt("拖动、缩放、改字", 480, 470, 340, 40, 26, "#9AA0A6", 6));
            shapes.AltText = "形状页面：紫色矩形、橙色圆形和白色箭头。";

            // ---- Slide 3: Formula (三个 build) ----
            var formula = NewSlide("Formula", 3,
@"class Formula(Deck):
    def construct(self):
        heading = label(""A formula, in builds"", 40).to_corner(UL, buff=0.5)
        formula = MathTex(FORMULA, font_size=96).move_to((0, 0.6, 0))
        self.play(FadeIn(heading), FadeIn(formula), run_time=0.6)
        self.next_slide()

        note = label(""Double-click the formula to edit its LaTeX"", 28, GREY_B).move_to((0, -1.6, 0))
        self.play(FadeIn(note), run_time=0.5)
        self.next_slide()

        table = MathTable([[1, 2, 3], [4, 5, 6]], include_outer_lines=True).scale(0.6).move_to((0, -3.0, 0))
        self.play(FadeIn(table), run_time=0.5)");
            var f0 = formula.Builds[0];
            f0.Objects.Add(Txt("逐步呈现的公式", 60, 40, 520, 56, 40, "#FFFFFF", 2, true));
            f0.Objects.Add(new CanvasObject { Kind = ObjectKind.Tex, Value = @"e^{i\pi}+1=0", DisplayText = "e^iπ + 1 = 0", X = 380, Y = 240, Width = 520, Height = 110, FontSize = 72, Color = "#FFFFFF", SourceLine = 3, Italic = true });
            f0.Notes = "欧拉恒等式，逐步呈现。";

            var f1 = new Build { Name = "构建 2" };
            f1.Objects.Add(Txt("逐步呈现的公式", 60, 40, 520, 56, 40, "#FFFFFF", 2, true));
            f1.Objects.Add(new CanvasObject { Kind = ObjectKind.Tex, Value = @"e^{i\pi}+1=0", DisplayText = "e^iπ + 1 = 0", X = 380, Y = 200, Width = 520, Height = 110, FontSize = 72, Color = "#FFFFFF", SourceLine = 3, Italic = true });
            f1.Objects.Add(Txt("双击公式可编辑其 LaTeX", 300, 360, 620, 40, 26, "#9AA0A6", 7));
            f1.BuildNotes = "此备注出现在构建 2 中。";
            formula.Builds.Add(f1);

            var f2 = new Build { Name = "构建 3" };
            f2.Objects.Add(Txt("逐步呈现的公式", 60, 40, 520, 56, 40, "#FFFFFF", 2, true));
            f2.Objects.Add(new CanvasObject { Kind = ObjectKind.Table, DisplayText = "1 2 3 / 4 5 6", X = 470, Y = 200, Width = 340, Height = 180, Color = "#FFFFFF", SourceLine = 11 });
            f2.BuildNotes = "最后一个构建添加了数学表格。";
            formula.Builds.Add(f2);

            Slides.Add(welcome);
            Slides.Add(shapes);
            Slides.Add(formula);

            // ---- Slide 4: Animation（动画演示：预览会逐对象动起来） ----
            var anim = NewSlide("Animation", 4,
@"class Animation(Deck):
    def construct(self):
        heading = label(""真正会动的演示"", 40).to_corner(UL, buff=0.5)
        subtitle = label(""Create → Transform → Indicate"", 26, GREY_B).move_to((0, -1.2, 0))
        self.play(Write(heading), FadeIn(subtitle), run_time=0.9)
        self.next_slide()

        square = Square(side_length=1.8, color=ACCENT, stroke_width=6).move_to((-3.6, 0.2, 0))
        caption = label(""Create"", 26, GREY_B).move_to((0, -2.6, 0))
        self.play(Create(square), FadeIn(caption), run_time=1.2)
        self.next_slide()

        circle = Circle(radius=1.0, color=ORANGE, stroke_width=6).move_to((3.6, 0.2, 0))
        self.play(Transform(square, circle), run_time=1.4)");
            var a0 = anim.Builds[0];
            a0.Objects.Add(Txt("真正会动的演示", 120, 90, 640, 60, 40, "#FFFFFF", 3, true));
            a0.Objects.Add(Txt("Create → Transform → Indicate", 300, 330, 680, 44, 26, "#9AA0A6", 4));
            a0.Notes = "这是动画演示页：进入预览后会看到对象逐个淡入、位移与形变。";

            var a1 = new Build { Name = "构建 2" };
            a1.Objects.Add(Txt("真正会动的演示", 120, 90, 640, 60, 40, "#FFFFFF", 3, true));
            a1.Objects.Add(Txt("Create → Transform → Indicate", 300, 330, 680, 44, 26, "#9AA0A6", 4));
            a1.Objects.Add(new CanvasObject { Kind = ObjectKind.Shape, ShapeLabel = "Rectangle", X = 220, Y = 250, Width = 240, Height = 240, Color = "#7C5CFF", SourceLine = 8 });
            a1.Objects.Add(Txt("Create", 540, 540, 200, 40, 24, "#9AA0A6", 9));
            a1.BuildNotes = "调用 Create(square)：矩形描边生长出来。";
            anim.Builds.Add(a1);

            var a2 = new Build { Name = "构建 3" };
            a2.Objects.Add(Txt("真正会动的演示", 120, 90, 640, 60, 40, "#FFFFFF", 3, true));
            a2.Objects.Add(Txt("Create → Transform → Indicate", 300, 330, 680, 44, 26, "#9AA0A6", 4));
            a2.Objects.Add(new CanvasObject { Kind = ObjectKind.Shape, ShapeLabel = "Rectangle", X = 400, Y = 250, Width = 240, Height = 240, Color = "#7C5CFF", SourceLine = 8 });
            a2.Objects.Add(new CanvasObject { Kind = ObjectKind.Shape, ShapeLabel = "Circle", X = 860, Y = 250, Width = 240, Height = 240, Color = "#FF9F0A", SourceLine = 13 });
            a2.Objects.Add(Txt("Transform", 540, 540, 200, 40, 24, "#9AA0A6", 9));
            a2.BuildNotes = "Transform(square, circle)：矩形移动并变成圆。";
            anim.Builds.Add(a2);

            anim.AltText = "动画页：标题、副标题、一个紫色矩形和一个橙色圆形。";
            Slides.Add(anim);
        }

        private Slide NewSlide(string name, int number, string source)
        {
            var s = new Slide { Name = name, Number = number, Source = source };
            s.Builds.Add(new Build { Name = "构建 1" });
            return s;
        }

        #endregion

        #region 交互逻辑

        #region deck 主题与演示文稿配色（走引擎）

        /// <summary>最近一次真正写进 deck 的主题：取消选择器时用它把工具栏下拉框拨回去。</summary>
        private DeckTheme _deckThemeApplied;
        private bool _themePickerBusy;

        /// <summary>
        /// 工具栏色块下拉选了主题：不立刻应用，先打开确认面板做一次预览。
        /// 应用会改写 common.py / manim.cfg 并重渲染每一页，误点代价太大（macOS 版也是「预览 → Apply」两步）。
        /// </summary>
        public void SelectThemeFromUi(DeckTheme theme)
        {
            if (theme == null || _themePickerBusy) return;
            if (_deckThemeApplied != null && theme.Id == _deckThemeApplied.Id) return;  // 选回当前主题，什么也不用做
            OpenThemePicker(theme);
        }

        /// <summary>「格式 ▸ 主题…」、工具栏「主题」按钮、色块下拉共用这一个入口。</summary>
        public void OpenThemePicker(DeckTheme preset = null)
        {
            if (_themePickerBusy) return;

            DeckTheme previous = _deckThemeApplied ?? CurrentTheme;
            if (!EngineConnected)
            {
                ShowToast("主题要改 deck 源码：请先「引擎 ▸ 连接 Manim 引擎」");
                CurrentTheme = previous;
                return;
            }

            _themePickerBusy = true;
            try
            {
                var vm = new ThemePickerViewModel(this, _engine, preset ?? CurrentTheme);
                var dialog = new Views.ThemePickerDialog { DataContext = vm };
                if (Application.Current?.MainWindow is Window owner && owner.IsVisible)
                    dialog.Owner = owner;

                bool accepted = dialog.ShowDialog() == true && vm.Applied;
                if (accepted)
                {
                    _deckThemeApplied = vm.AppliedTheme;
                    CurrentTheme = vm.AppliedTheme;
                    ShowToast("已保存 · " + (string.IsNullOrEmpty(vm.ResultMessage) ? vm.AppliedTheme?.Name : vm.ResultMessage));
                    _ = RefreshDeckAfterThemeAsync(vm.AppliedToSlideOnly);
                }
                else
                {
                    CurrentTheme = previous;                                  // 取消：下拉框回到 deck 的真实主题
                    if (!string.IsNullOrEmpty(vm.Error)) ShowToast(vm.Error); // 引擎的 error 原样给用户
                }
            }
            catch (Exception ex)
            {
                CurrentTheme = previous;
                ShowToast("主题面板打开失败：" + ex.Message);
            }
            finally
            {
                _themePickerBusy = false;
            }
        }

        /// <summary>主题改完刷新画布：仅本页时只刷这一页，改整份 deck 时全刷（每页背景都变了）。</summary>
        private async Task RefreshDeckAfterThemeAsync(bool slideOnly)
        {
            try
            {
                if (slideOnly) await RefreshAfterEngineEdit(null);
                else await RefreshAllEngineSlides();
            }
            catch (Exception ex)
            {
                ShowToast("主题已应用，但画布刷新失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 演示文稿深色 / 反色：走引擎 <c>darkmode</c>，把颜色映射写进源码（一步撤销），与应用界面无关。
        /// 不要把这一步和主题的 flip 叠加：引擎在 theme 里自己会以 _nested 方式调一次 dark_mode，
        /// 这里只在用户显式点菜单时才发请求。
        /// </summary>
        private async Task ApplyDeckColorsAsync(string mode)
        {
            if (!EngineConnected)
            {
                ShowToast("演示文稿配色要改源码：请先「引擎 ▸ 连接 Manim 引擎」");
                return;
            }

            string label = mode == "invert" ? "反色" : "深色模式";
            BusyText = "正在把" + label + "写入演示文稿…";
            try
            {
                var r = await _engine.DarkMode(CurrentSlide?.Name ?? "", mode);
                if (IsEngineFalse(r, "ok"))
                {
                    ShowToast(GetStr(r, "error") ?? (label + "应用失败"));
                    return;
                }
                string msg = GetStr(r, "message");
                ShowToast(msg == "no change" ? "没有需要变换的颜色" : "已保存 · " + (msg ?? label));
                await RefreshAllEngineSlides();      // 每一页的颜色都可能变了
            }
            catch (Exception ex)
            {
                ShowToast(label + "应用失败：" + ex.Message);
            }
            finally
            {
                BusyText = "";
            }
        }

        private void ApplyTheme(DeckTheme t) => SelectThemeFromUi(t);

        /// <summary>
        /// 连上引擎后把「deck 真实在用的主题」同步到界面：下拉框显示 deck 实际那套，
        /// 而不是本地默认的 Midnight（否则取消选择器时会拨回一个错误的值）。
        /// </summary>
        public async Task SyncDeckThemeFromEngineAsync()
        {
            if (!EngineConnected) return;
            try
            {
                var r = await _engine.ThemeGet(null, CurrentSlide?.Name ?? "");
                string name = GetStr(r, "theme");
                if (string.IsNullOrEmpty(name)) return;
                DeckTheme t = Themes.FirstOrDefault(x => x.Name == name);
                if (t == null) return;
                _deckThemeApplied = t;
                CurrentTheme = t;
            }
            catch (Exception)
            {
                // 主题信息读不到不影响连接本身，保持当前显示即可
            }
        }

        #endregion

        private void Nudge(string dir)
        {
            if (SelectedObjects.Count == 0) return;
            double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 2;

            // 一次微调 = 一步撤销，且连续微调折叠成一步（与拖动的手感一致，不会刷出几十步历史）
            DeckSnapshot snap = EngineConnected ? null : Capture();
            foreach (var o in SelectedObjects.ToList())
            {
                switch (dir)
                {
                    case "Left": o.X -= step; break;
                    case "Right": o.X += step; break;
                    case "Up": o.Y -= step; break;
                    case "Down": o.Y += step; break;
                }
                // 方向键微调同样写回源码坐标
                CommitObjectMove(o);
            }
            if (snap != null) _history.Push("微调位置", snap, mergeKey: "nudge");
        }

        public void ShowToast(string text)
        {
            ToastText = text;
            ToastVisible = true;
            StatusMessage = text;
        }

        public void HideToast() => ToastVisible = false;

        public void MoveObject(CanvasObject o, double dx, double dy) { o.X += dx; o.Y += dy; }

        /// <summary>「幻灯片 ▸ 背景与页码…」：背景和页码都是写进 deck 源码的属性，必须经引擎。</summary>
        public void OpenSlideSetup()
        {
            if (!EngineConnected)
            {
                ShowToast("背景与页码要改 deck 源码：请先「引擎 ▸ 连接 Manim 引擎」");
                return;
            }
            try
            {
                var vm = new SlideSetupViewModel(this, _engine);
                var dlg = new Views.SlideSetupDialog
                {
                    DataContext = vm,
                    Owner = Application.Current?.MainWindow != null && Application.Current.MainWindow.IsVisible
                        ? Application.Current.MainWindow : null
                };
                dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                ShowToast("打开背景与页码失败：" + ex.Message);
            }
        }

        private void ExecuteToolbar(string action)
        {
            switch (action)
            {
                case "NewSlide": NewSlideFromToolbar(); break;
                case "NewBuild": NewBuild(); break;
                case "ExpandAll": ShowToast("已展开导航器中的所有构建"); break;
                case "Theme": OpenThemePicker(); break;
                // 工具栏的月亮只切应用界面，不动 deck（演示文稿配色在「格式」菜单里）
                case "DarkMode": IsDarkMode = !IsDarkMode; ShowToast(IsDarkMode ? "界面已切换为深色" : "界面已切换为浅色"); break;
                case "Terminal": ShowTerminalPanel(); break;
                case "BioCartoon": OpenBioCartoon(); break;
                case "PythonPlot": OpenPythonPlot(); break;
                case "ZoomIn": Zoom += 0.1; break;
                case "ZoomOut": Zoom -= 0.1; break;
                case "ZoomFit": Zoom = 0.72; break;
                case "Share": _ = PackageProjectAsync(); break;
                case "ToggleDrawer": DrawerVisible = !DrawerVisible; break;
                case "ToggleNavigator": NavigatorVisible = !NavigatorVisible; break;
                case "ToggleInspector": InspectorVisible = !InspectorVisible; break;
                default: InsertObject(action); break;
            }
        }

        private void NewSlideFromToolbar()
        {
            int n = Slides.Count + 1;
            var s = NewSlide("Slide " + n, n, $"class Slide{n}(Deck):\n    def construct(self):\n        pass  # manimble 新建幻灯片");
            s.Builds[0].Objects.Add(Txt("单击此处添加标题", 340, 260, 600, 70, 44, "#7A7A7A", 2));
            Slides.Add(s);
            CurrentSlide = s;
            ShowToast("已新建幻灯片");
        }

        private void NewBuild()
        {
            if (CurrentSlide == null) return;
            var b = new Build { Name = "构建 " + (CurrentSlide.Builds.Count + 1) };
            foreach (var o in CurrentSlide.Builds.Last().Objects)
            {
                var c = o.Clone(); c.IsSelected = false; b.Objects.Add(c);
            }
            CurrentSlide.Builds.Add(b);
            CurrentSlide.CurrentBuildIndex = CurrentSlide.Builds.Count - 1;
            RaisePropertyChanged(nameof(CurrentBuild));
            RaisePropertyChanged(nameof(CurrentObjects));
            ShowToast("已新建构建");
        }

        private void InsertObject(string kind)
        {
            if (kind == "Image") { _ = InsertImage(); return; }
            if (CurrentBuild == null) return;
            CanvasObject o;
            switch (kind)
            {
                case "Text": o = Txt("Text", 460, 300, 360, 50, 32, CurrentTheme?.Text ?? "#FFFFFF", 1); break;
                case "Formula": o = new CanvasObject { Kind = ObjectKind.Tex, DisplayText = "x^2", Value = "x^2", X = 520, Y = 300, Width = 240, Height = 80, FontSize = 56, Color = CurrentTheme?.Text ?? "#FFFFFF", Italic = true }; break;
                case "Table": o = new CanvasObject { Kind = ObjectKind.Table, DisplayText = "1 2 / 3 4", X = 500, Y = 280, Width = 280, Height = 140, Color = "#FFFFFF" }; break;
                case "Image": o = new CanvasObject { Kind = ObjectKind.Image, ImageLabel = "Picture", X = 460, Y = 260, Width = 380, Height = 220, Color = "#FFFFFF" }; break;
                default: o = new CanvasObject { Kind = ObjectKind.Shape, ShapeLabel = kind, X = 500, Y = 300, Width = 240, Height = 140, Color = CurrentTheme?.Accent ?? "#7C5CFF" }; break;
            }
            o.Z = CurrentBuild.Objects.Count;
            CurrentBuild.Objects.Add(o);
            SelectedObject = o;
            if (o.Kind == ObjectKind.Tex) RenderFormulaObject(o);

            // 关键：把对象操作写回 Python 源码，并同步到可编辑代码窗格
            if (CurrentSlide != null)
            {
                // 引擎的 edit_source 需要「旧文本」来定位替换，必须在改动之前抓快照，
                // 否则 expected 与 new 相同，引擎会判定源码不匹配而拒绝。
                string before = CurrentSlide.Source;

                if (string.IsNullOrEmpty(o.CodeId)) o.CodeId = SourceCodeGenerator.NewCodeId();
                string line = SourceCodeGenerator.LineFor(o, SlideDisplayWidth / Zoom, SlideDisplayHeight / Zoom);
                CurrentSlide.Source = SourceCodeGenerator.AppendLine(before, line);
                o.SourceLine = CountLine(CurrentSlide.Source, line);
                o.Loc = $"{CurrentSlide.File}:{o.SourceLine}";
                RebuildSourceLines();
                SyncSourceText();
                SourceEditStatus = "代码已更新（待应用）";
                // 引擎已连接：真正写回工作副本并重渲染
                if (_engine != null && _engine.IsConnected)
                    _ = _engine.ApplyTextEdit(CurrentSlide, before, CurrentSlide.Source);
            }
            ShowToast($"已插入 {kind} · Python 代码已同步");
        }

        /// <summary>
        /// 选择一张图片复制到当前 deck 的 assets 目录，返回相对 deck 的正斜杠路径与落地绝对路径。
        /// 同名文件加 _1/_2 后缀避免覆盖引擎正在引用的旧资源。
        /// </summary>
        private (string relative, string dest) CopyImageIntoDeck(string sourceFile)
        {
            // 一定落在工作副本里（DeckFolder = 副本 deck）；未打开 deck 时才回退示例目录。
            // 若这里还写 EnvironmentService.SampleDeck，图片会绕过副本、既不进 baseline 也写不回原目录。
            string deck = DeckFolder;
            if (string.IsNullOrEmpty(deck) || !Directory.Exists(deck)) deck = EnvironmentService.SampleDeck;
            Directory.CreateDirectory(deck);
            string assets = Path.Combine(deck, "assets");
            Directory.CreateDirectory(assets);

            string name = Path.GetFileName(sourceFile);
            string stem = Path.GetFileNameWithoutExtension(name);
            string ext = Path.GetExtension(name);
            string dest = Path.Combine(assets, name);
            int n = 1;
            while (File.Exists(dest)) dest = Path.Combine(assets, stem + "_" + n++ + ext);
            File.Copy(sourceFile, dest, false);

            string relative = "assets/" + Path.GetFileName(dest).Replace('\\', '/');
            return (relative, dest);
        }

        private static Microsoft.Win32.OpenFileDialog NewImageDialog(string title)
            => new Microsoft.Win32.OpenFileDialog
            {
                Title = title,
                Filter = "图片|*.png;*.jpg;*.jpeg;*.svg;*.webp;*.gif|所有文件|*.*"
            };

        /// <summary>插入图片：连引擎时走引擎写回；未连时保留本地占位并写回源码。</summary>
        private async Task InsertImage()
        {
            var dlg = NewImageDialog("选择图片");
            if (dlg.ShowDialog() != true) return;

            var (relative, dest) = CopyImageIntoDeck(dlg.FileName);

            if (EngineConnected && CurrentSlide != null)
            {
                bool ok = await _engine.InsertViaEngine(CurrentSlide,
                    Math.Max(0, CurrentSlide.CurrentBuildIndex), "image",
                    new { file = relative, width = 4.0 });
                ShowToast(ok ? "已通过引擎插入图片" : "引擎插入图片失败，请查看日志");
                return;
            }

            // 未连引擎：本地占位 + 写回源码，保证原型模式闭环
            if (CurrentBuild == null) return;
            var o = new CanvasObject
            {
                Kind = ObjectKind.Image,
                ImagePath = dest,
                ImageLabel = Path.GetFileName(dest),
                X = 460, Y = 260, Width = 380, Height = 220,
                Color = "#FFFFFF",
                Z = CurrentBuild.Objects.Count
            };
            CurrentBuild.Objects.Add(o);
            SelectedObject = o;

            string before = CurrentSlide.Source;
            if (string.IsNullOrEmpty(o.CodeId)) o.CodeId = SourceCodeGenerator.NewCodeId();
            string line = SourceCodeGenerator.LineFor(o, SlideDisplayWidth / Zoom, SlideDisplayHeight / Zoom);
            CurrentSlide.Source = SourceCodeGenerator.AppendLine(before, line);
            o.SourceLine = CountLine(CurrentSlide.Source, line);
            o.Loc = $"{CurrentSlide.File}:{o.SourceLine}";
            RebuildSourceLines();
            SyncSourceText();
            SourceEditStatus = "代码已更新（待应用）";
            ShowToast("未连接引擎，仅本地预览");
        }

        /// <summary>更换选中图片对象的图片文件：仅连引擎时可用，走 replace_image 一步撤销。</summary>
        private async Task ReplaceSelectedImage()
        {
            if (!EngineConnected) { ShowToast("未连接引擎，无法更换图片"); return; }
            var o = SelectedObject;
            if (o == null || o.Kind != ObjectKind.Image || CurrentSlide == null)
            {
                ShowToast("请先选中一个图片对象");
                return;
            }
            if (string.IsNullOrEmpty(o.Oid) || !int.TryParse(o.Oid, out _))
            {
                ShowToast("该图片对象来自本地预览，请连接引擎后重新插入图片");
                return;
            }

            var dlg = NewImageDialog("更换图片");
            if (dlg.ShowDialog() != true) return;

            var (relative, dest) = CopyImageIntoDeck(dlg.FileName);
            try
            {
                var r = await _engine.ReplaceImage(CurrentSlide.Name, o.Oid, relative);
                bool ok = r.ValueKind != JsonValueKind.Object
                          || !r.TryGetProperty("ok", out var okEl)
                          || okEl.ValueKind != JsonValueKind.False;
                if (!ok)
                {
                    string err = r.ValueKind == JsonValueKind.Object && r.TryGetProperty("error", out var e)
                        && e.ValueKind == JsonValueKind.String ? e.GetString() : "未知错误";
                    ShowToast("更换图片失败：" + err);
                    return;
                }
                o.ImageLabel = Path.GetFileName(dest);
                await _engine.Refresh(CurrentSlide);
                ShowToast("图片已更换");
            }
            catch (Exception ex)
            {
                ShowToast("更换图片失败：" + ex.Message);
            }
        }

        // ---------------------------------------------------------- BioCartoon 插画库

        private Window _bioCartoonWindow;

        /// <summary>打开 BioCartoon 插画库浮层（独立窗口，简单可靠；重复调用只激活已有窗口）。</summary>
        public void OpenBioCartoon()
        {
            if (_bioCartoonWindow != null && _bioCartoonWindow.IsVisible)
            {
                _bioCartoonWindow.Activate();
                return;
            }
            var vm = new BioCartoonViewModel(_engine, InsertBioCartoonAsync);
            var panel = new Views.BioCartoonPanel { DataContext = vm };
            var win = new Window
            {
                Title = "BioCartoon 插画库",
                Content = panel,
                Width = 820, Height = 620,
                MinWidth = 560, MinHeight = 420,
                Owner = Application.Current?.MainWindow,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            win.SetResourceReference(Window.BackgroundProperty, "ChromeBackgroundBrush");
            win.Closed += (s, e) => { if (_bioCartoonWindow == win) _bioCartoonWindow = null; };
            _bioCartoonWindow = win;
            win.Show();
        }

        /// <summary>
        /// 插入一个 BioCartoon 图标：fetch SVG+PNG 到临时目录 → svg_icon prepare（manim 能否按 PNG 画出矢量）→
        /// 落一份到 deck/assets → 走引擎插 image（宽 3）。矢量不可靠时回退 PNG 位图，并在返回消息里说明。
        /// 署名信息追加到本页 alt text（对象级备注不可行，选可行的 alt text）。返回给面板显示的中文状态。
        /// </summary>
        private async Task<string> InsertBioCartoonAsync(BioCartoonItem item)
        {
            if (!EngineConnected) return "请先「引擎 ▸ 连接 Manim 引擎」再插入图标。";
            if (CurrentSlide == null) return "请先选择一张幻灯片。";

            string safeId = string.Concat((item.Id ?? "icon").Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_'));
            if (string.IsNullOrEmpty(safeId)) safeId = "icon";
            string tmpRoot = Path.Combine(EnvironmentService.TempDir, "biocartoon-insert", Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tmpRoot);
                string tmpSvg = Path.Combine(tmpRoot, safeId + ".svg");
                string tmpPng = Path.Combine(tmpRoot, safeId + ".png");
                string readySvg = Path.Combine(tmpRoot, safeId + "-ready.svg");

                var frSvg = await _engine.BioFetch(item.Id, tmpSvg, "svg");
                if (IsEngineFalse(frSvg, "ok") || !File.Exists(tmpSvg))
                    return "下载 SVG 失败：" + (GetStr(frSvg, "error") ?? "未知错误");
                var frPng = await _engine.BioFetch(item.Id, tmpPng, "png");
                if (IsEngineFalse(frPng, "ok") || !File.Exists(tmpPng))
                    return "下载 PNG 失败：" + (GetStr(frPng, "error") ?? "未知错误");

                bool svgOk = false;
                try
                {
                    var prep = await _engine.Request("svg_icon",
                        new { svg = tmpSvg, png = tmpPng, @out = readySvg }, 120000);
                    svgOk = !IsEngineFalse(prep, "ok") && File.Exists(readySvg);
                }
                catch { svgOk = false; }

                string sourceFile = svgOk ? readySvg : tmpPng;
                var (relative, dest) = CopyImageIntoDeck(sourceFile);
                bool ok = await _engine.InsertViaEngine(CurrentSlide,
                    Math.Max(0, CurrentSlide.CurrentBuildIndex), "image",
                    new { file = relative, width = 3.0 });
                if (!ok) return "插入失败，请查看引擎日志。";

                // 署名写进本页 alt text（可行且持久）；对象级备注引擎未提供入口。
                string credit = string.IsNullOrEmpty(item.Credit)
                    ? $"{item.Title} · {item.License} · {item.SourceLabel}"
                    : item.Credit;
                try
                {
                    string existing = CurrentSlide.AltText ?? "";
                    if (!existing.Contains(credit))
                    {
                        string line = "图标署名：" + credit;
                        string combined = string.IsNullOrEmpty(existing) ? line : existing + "\n" + line;
                        await _engine.AltTextSet(CurrentSlide.Name, combined);
                        CurrentSlide.AltText = combined;
                        RaisePropertyChanged(nameof(AltTextText));
                    }
                }
                catch { /* 署名写回失败不影响插入本身 */ }

                return svgOk
                    ? $"已插入矢量图标：{item.Title}（{item.License}）"
                    : $"该图标含渐变/文字，已用 PNG 位图插入：{item.Title}（{item.License}）";
            }
            catch (Exception ex)
            {
                return "插入图标出错：" + ex.Message;
            }
            finally
            {
                try { if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, true); } catch { }
            }
        }

        // ---------------------------------------------------------- Python 图表

        // 同一对话框会话内记住刚插入的图片 oid，重复运行时覆盖它而不是叠加
        private string _plotTargetOid;

        /// <summary>打开「Python 图表」对话框。</summary>
        public void OpenPythonPlot()
        {
            _plotTargetOid = null;
            var vm = new PythonPlotViewModel(RunPythonPlotAsync);
            var dlg = new Views.PythonPlotDialog { DataContext = vm, Owner = Application.Current.MainWindow };
            dlg.ShowDialog();
        }

        /// <summary>
        /// 运行用户 Python 绘图代码：引擎在受控子进程里执行并输出 PNG → 落进 deck/assets →
        /// 选中了图片对象就 ReplaceImage 覆盖原图（重复执行不叠图），否则 InsertViaEngine 插新图。
        /// 返回 (是否成功, 给对话框显示的状态/错误)。
        /// </summary>
        private async Task<(bool ok, string message)> RunPythonPlotAsync(string code)
        {
            if (!EngineConnected) return (false, "请先「引擎 ▸ 连接 Manim 引擎」。");
            if (CurrentSlide == null) return (false, "请先选择一张幻灯片。");

            string tmpDir = Path.Combine(EnvironmentService.TempDir, "python-plot", Guid.NewGuid().ToString("N"));
            string tmpPng = Path.Combine(tmpDir, "plot.png");
            try
            {
                Directory.CreateDirectory(tmpDir);
                var r = await _engine.PythonPlot(code, tmpPng, _engine.DeckPath);
                bool ok = r.ValueKind != JsonValueKind.Object
                          || !r.TryGetProperty("ok", out var okEl) || okEl.ValueKind != JsonValueKind.False;
                if (!ok || !File.Exists(tmpPng))
                    return (false, "绘图失败：" + (GetStr(r, "error") ?? "未生成图片"));

                var (relative, dest) = CopyImageIntoDeck(tmpPng);

                // 目标 oid：当前选中的图片对象优先；否则用本会话上一次插入的图片
                string oid = null;
                var sel = SelectedObject;
                if (sel != null && sel.Kind == ObjectKind.Image && !string.IsNullOrEmpty(sel.Oid)
                    && int.TryParse(sel.Oid, out _))
                    oid = sel.Oid;
                else if (!string.IsNullOrEmpty(_plotTargetOid))
                    oid = _plotTargetOid;

                if (!string.IsNullOrEmpty(oid))
                {
                    var rr = await _engine.ReplaceImage(CurrentSlide.Name, oid, relative);
                    bool rok = rr.ValueKind != JsonValueKind.Object
                               || !rr.TryGetProperty("ok", out var rokEl) || rokEl.ValueKind != JsonValueKind.False;
                    if (!rok) return (false, "覆盖原图失败：" + (GetStr(rr, "error") ?? "未知错误"));
                    await _engine.Refresh(CurrentSlide);
                    return (true, $"已运行并覆盖原图（{Path.GetFileName(dest)}）。");
                }

                bool ins = await _engine.InsertViaEngine(CurrentSlide,
                    Math.Max(0, CurrentSlide.CurrentBuildIndex), "image", new { file = relative, width = 4.0 });
                if (!ins) return (false, "插入图表失败，请查看引擎日志。");
                _plotTargetOid = CurrentBuild?.Objects.LastOrDefault(o => o.Kind == ObjectKind.Image
                                 && !string.IsNullOrEmpty(o.Oid))?.Oid;
                return (true, $"已运行并插入图表（{Path.GetFileName(dest)}）。再次运行会覆盖此图。");
            }
            catch (Exception ex)
            {
                return (false, "运行出错：" + ex.Message);
            }
            finally
            {
                try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { }
            }
        }

        /// <summary>
        /// 提取图片：先从引擎列出 deck 中所有被引用的图片，让用户选一个文件夹，
        /// 再由引擎把这些文件复制过去。全程不改 deck 源码，不进撤销历史。
        /// </summary>
        private async Task ExtractImages()
        {
            if (!EngineConnected) { ShowToast("未连接引擎，无法提取图片"); return; }
            try
            {
                var listed = await _engine.ExtractImagesList();
                bool ok = listed.ValueKind != JsonValueKind.Object
                          || !listed.TryGetProperty("ok", out var okEl)
                          || okEl.ValueKind != JsonValueKind.False;
                if (!ok)
                {
                    string err = listed.ValueKind == JsonValueKind.Object && listed.TryGetProperty("error", out var e)
                        && e.ValueKind == JsonValueKind.String ? e.GetString() : "未知错误";
                    ShowToast("读取图片列表失败：" + err);
                    return;
                }
                var images = listed.ValueKind == JsonValueKind.Object && listed.TryGetProperty("images", out var arr)
                    && arr.ValueKind == JsonValueKind.Array ? arr.EnumerateArray().ToList() : new List<JsonElement>();
                if (images.Count == 0) { ShowToast("这个 deck 里没有可提取的图片"); return; }

                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = $"提取 {images.Count} 张图片：选择保存位置",
                    FileName = "选择文件夹（文件名随意）",
                    Filter = "所有文件|*.*",
                    AddExtension = false,
                    OverwritePrompt = false
                };
                if (dlg.ShowDialog() != true) return;
                string dir = Path.GetDirectoryName(dlg.FileName);
                if (string.IsNullOrEmpty(dir)) return;

                var saved = await _engine.ExtractImagesSave(dir);
                bool ok2 = saved.ValueKind != JsonValueKind.Object
                           || !saved.TryGetProperty("ok", out var okEl2)
                           || okEl2.ValueKind != JsonValueKind.False;
                if (!ok2)
                {
                    string err = saved.ValueKind == JsonValueKind.Object && saved.TryGetProperty("error", out var e2)
                        && e2.ValueKind == JsonValueKind.String ? e2.GetString() : "未知错误";
                    ShowToast("提取图片失败：" + err);
                    return;
                }
                int n = saved.ValueKind == JsonValueKind.Object && saved.TryGetProperty("saved", out var sarr)
                    && sarr.ValueKind == JsonValueKind.Array ? sarr.GetArrayLength() : 0;
                ShowToast($"已提取 {n} 张图片到 {dir}");
            }
            catch (Exception ex)
            {
                ShowToast("提取图片失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 打包当前 deck 为可移植工程：选目标位置、确认是否带媒体缓存，交给引擎后台执行。
        /// </summary>
        private async Task PackageProjectAsync()
        {
            if (_packaging) { ShowToast("正在打包中，请稍候"); return; }
            if (!EngineConnected) { ShowToast("未连接引擎，无法打包"); return; }

            var answer = MessageBox.Show(
                "是否把媒体缓存（media/）一起打进包里？\n\n"
                + "选「是」：包含缓存，包更大但接收方首次播放更快。\n"
                + "选「否」：只打源码与资源（推荐）。\n"
                + "选「取消」：放弃打包。",
                "打包项目以便分享", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return;
            bool includeMedia = answer == MessageBoxResult.Yes;

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "打包项目以便分享",
                FileName = "ManimbleShow.zip",
                Filter = "ZIP 包|*.zip|文件夹|*.*",
                AddExtension = true
            };
            if (dlg.ShowDialog() != true) return;
            string target = dlg.FileName;

            _packaging = true;
            BusyText = "正在打包演示文稿…";
            try
            {
                var manifest = await _engine.PackageProject(target, includeMedia);
                if (manifest.ValueKind == JsonValueKind.Undefined)
                {
                    ShowToast("打包超时，请查看日志");
                    return;
                }
                bool ok = manifest.ValueKind != JsonValueKind.Object
                          || !manifest.TryGetProperty("ok", out var okEl)
                          || okEl.ValueKind != JsonValueKind.False;
                if (!ok)
                {
                    string err = manifest.ValueKind == JsonValueKind.Object
                        && manifest.TryGetProperty("error", out var e)
                        && e.ValueKind == JsonValueKind.String ? e.GetString() : "未知错误";
                    ShowToast("打包失败：" + err);
                    return;
                }
                int count = manifest.ValueKind == JsonValueKind.Object
                    && manifest.TryGetProperty("count", out var c) && c.TryGetInt32(out int n) ? n : 0;
                ShowToast($"已打包 {count} 个文件到 {Path.GetFileName(target)}");
                if (manifest.ValueKind == JsonValueKind.Object
                    && manifest.TryGetProperty("warnings", out var ws)
                    && ws.ValueKind == JsonValueKind.Array && ws.GetArrayLength() > 0)
                {
                    string first = ws[0].ValueKind == JsonValueKind.String ? ws[0].GetString() : "";
                    if (!string.IsNullOrEmpty(first)) ShowToast("提示：" + first);
                }
            }
            catch (Exception ex)
            {
                ShowToast("打包失败：" + ex.Message);
            }
            finally
            {
                _packaging = false;
                BusyText = "";
            }
        }

        private static int CountLine(string source, string line)
        {
            if (string.IsNullOrEmpty(source)) return 1;
            var lines = source.Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
                if (lines[i].Contains(line)) return i + 1;
            return lines.Length;
        }

        #region LaTeX 公式

        private readonly HashSet<string> _latexRequested = new HashSet<string>();
        private bool _latexWarned;

        private string _formulaStatus = "公式：就绪";
        /// <summary>公式渲染状态，显示在状态栏。</summary>
        public string FormulaStatus { get => _formulaStatus; set => SetProperty(ref _formulaStatus, value); }

        // 检视器里改公式源码 / 字号 / 颜色：停手 400ms 后自动重新排版
        private readonly System.Windows.Threading.DispatcherTimer _texEditTimer =
            new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        private CanvasObject _texEditTarget;
        private bool _applyingFormula;

        /// <summary>选中对象变化时只监听当前对象，避免整表订阅。</summary>
        private void SelectedObject_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            var o = sender as CanvasObject;
            if (o == null || _applyingFormula) return;
            if (o.Kind != ObjectKind.Tex || !ReferenceEquals(o, SelectedObject)) return;
            if (e.PropertyName != nameof(CanvasObject.Value) &&
                e.PropertyName != nameof(CanvasObject.FontSize) &&
                e.PropertyName != nameof(CanvasObject.Color)) return;

            _texEditTarget = o;
            _texEditTimer.Stop();
            _texEditTimer.Start();
        }

        /// <summary>把检视器里的公式改动落到画布（重新排版）和源码。</summary>
        private void ApplyFormulaEdit(CanvasObject o)
        {
            if (o == null || o.Kind != ObjectKind.Tex) return;

            string tex = LatexService.NormalizeTex(o.Value);
            if (string.IsNullOrWhiteSpace(tex)) { FormulaStatus = "公式：内容为空"; return; }

            _applyingFormula = true;
            try
            {
                if (tex != o.Value) o.Value = tex;
                o.DisplayText = LatexPretty.Pretty(tex);
            }
            finally { _applyingFormula = false; }

            RenderFormulaObject(o, force: true);
            CommitObjectProperties(o, "公式");
        }

        /// <summary>把对象当前参数重写回它那行源码（仅限画布生成、带标识的行）。</summary>
        private void CommitObjectProperties(CanvasObject o, string what)
        {
            if (o == null || CurrentSlide == null) return;
            string before = CurrentSlide.Source ?? "";
            string updated, note;
            if (!SourceCodeGenerator.TryRewriteObjectLine(before, o, SlideDisplayWidth / Zoom, SlideDisplayHeight / Zoom,
                                                          out updated, out note))
                return;
            if (updated == before) return;

            CurrentSlide.Source = updated;
            SyncSourceText();
            RebuildSourceLines();
            SourceEditStatus = $"{what}已写回代码";
            ScheduleEngineSync(CurrentSlide, before);
        }

        /// <summary>检视器「重新渲染」：忽略缓存强制重排当前公式。</summary>
        public void RerenderSelectedFormula()
        {
            var o = SelectedObject;
            if (o == null || o.Kind != ObjectKind.Tex)
            {
                ShowToast("请先选中一个公式对象");
                return;
            }
            RenderFormulaObject(o, force: true);
        }

        /// <summary>检视器「编辑…」：用公式编辑器改现有公式。</summary>
        public void EditSelectedFormula()
        {
            var o = SelectedObject;
            if (o == null || o.Kind != ObjectKind.Tex)
            {
                ShowToast("请先选中一个公式对象");
                return;
            }
            var dlg = new Views.FormulaDialog { InitialExpression = o.Value };
            // Owner 只能在「已经显示过」的窗口上设置，否则 WPF 抛 InvalidOperationException
            var owner = Application.Current?.MainWindow;
            if (owner != null && owner.IsVisible) dlg.Owner = owner;
            if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Expression)) return;

            o.Value = LatexService.NormalizeTex(dlg.Expression);
            ApplyFormulaEdit(o);
            ShowToast("公式已更新");
        }

        /// <summary>为当前页所有公式对象请求真实 LaTeX 渲染（结果按内容缓存，重复调用没有开销）。</summary>
        public void EnsureLatexRendered(Slide slide)
        {
            if (slide == null) return;
            if (!LatexService.IsAvailable)
            {
                FormulaStatus = "公式：未检测到 LaTeX（依赖中心 → LaTeX）";
                return;
            }
            foreach (var build in slide.Builds)
                foreach (var o in build.Objects)
                    if (o.Kind == ObjectKind.Tex && !o.HasSprite)
                        RenderFormulaObject(o);
        }

        /// <summary>渲染单个公式对象：写 SpritePath 后画布即显示真实排版。force=true 时忽略去重缓存。</summary>
        private async void RenderFormulaObject(CanvasObject o, bool force = false)
        {
            if (o == null) return;
            string tex = LatexService.NormalizeTex(string.IsNullOrWhiteSpace(o.Value) ? o.DisplayText : o.Value);
            if (string.IsNullOrWhiteSpace(tex)) return;
            if (!LatexService.IsAvailable)
            {
                FormulaStatus = "公式：缺少 LaTeX 环境";
                if (!_latexWarned)
                {
                    _latexWarned = true;
                    ShowToast("未检测到 LaTeX：公式将以文字近似显示，可在依赖中心安装 TinyTeX");
                }
                return;
            }
            string key = tex + "|" + (int)o.FontSize + "|" + o.Color;
            if (!_latexRequested.Add(key) && !force) return;
            try
            {
                FormulaStatus = "公式：正在用 LaTeX 渲染…";
                string path = await LatexService.RenderAsync(tex, o.FontSize, o.Color);
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    o.SpritePath = path;
                    FormulaStatus = "公式：LaTeX 渲染完成";
                }
            }
            catch (LatexException ex)
            {
                _latexRequested.Remove(key);
                FormulaStatus = "公式渲染失败：" + ex.Message;
            }
            catch (Exception ex)
            {
                _latexRequested.Remove(key);
                FormulaStatus = "公式渲染异常：" + ex.Message;
            }
        }

        /// <summary>打开公式编辑对话框并插入公式对象。</summary>
        public void InsertFormulaFromDialog()
        {
            if (CurrentBuild == null) return;
            var dlg = new Views.FormulaDialog();
            var owner = Application.Current?.MainWindow;
            if (owner != null && owner.IsVisible) dlg.Owner = owner;
            if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Expression)) return;
            InsertFormula(dlg.Expression);
        }

        /// <summary>插入一个 LaTeX 公式对象（真排版由 LatexService 完成）。</summary>
        public void InsertFormula(string tex)
        {
            if (CurrentBuild == null || string.IsNullOrWhiteSpace(tex)) return;
            tex = LatexService.NormalizeTex(tex);
            if (string.IsNullOrWhiteSpace(tex)) return;
            var o = new CanvasObject
            {
                Kind = ObjectKind.Tex,
                Value = tex.Trim(),
                DisplayText = LatexPretty.Pretty(tex),
                X = 440, Y = 300, Width = 400, Height = 110,
                FontSize = 56,
                Color = CurrentTheme?.Text ?? "#FFFFFF",
                Italic = true
            };
            o.Z = CurrentBuild.Objects.Count;
            CurrentBuild.Objects.Add(o);
            SelectedObject = o;

            if (CurrentSlide != null)
            {
                string line = SourceCodeGenerator.LineFor(o, 1280, 720);
                CurrentSlide.Source = SourceCodeGenerator.AppendLine(CurrentSlide.Source, line);
                o.SourceLine = CountLine(CurrentSlide.Source, line);
                o.Loc = $"{CurrentSlide.File}:{o.SourceLine}";
                RebuildSourceLines();
                SyncSourceText();
                SourceEditStatus = "代码已更新（待应用）";
            }

            RenderFormulaObject(o);
            ShowToast("已插入公式 · LaTeX 正在排版");
        }

        #endregion

        private void DuplicateSlide()
        {
            if (CurrentSlide == null) return;
            var copy = new Slide
            {
                Name = CurrentSlide.Name + " copy",
                Number = Slides.Count + 1,
                Source = CurrentSlide.Source,
                AltText = CurrentSlide.AltText
            };
            foreach (var b in CurrentSlide.Builds)
            {
                var nb = new Build { Name = b.Name, Notes = b.Notes, BuildNotes = b.BuildNotes };
                foreach (var o in b.Objects) { var c = o.Clone(); c.IsSelected = false; nb.Objects.Add(c); }
                copy.Builds.Add(nb);
            }
            Slides.Add(copy);
            CurrentSlide = copy;
            ShowToast("幻灯片已复制");
        }

        private void DeleteSlide()
        {
            if (CurrentSlide == null || Slides.Count <= 1) { ShowToast("不能删除最后一页幻灯片"); return; }
            int idx = Slides.IndexOf(CurrentSlide);
            Slides.Remove(CurrentSlide);
            CurrentSlide = Slides[Math.Min(idx, Slides.Count - 1)];
            for (int i = 0; i < Slides.Count; i++) Slides[i].Number = i + 1;
            ShowToast("幻灯片已删除");
        }

        private void ExecuteMenu(string action)
        {
            // 未来对接引擎：这些 action 与 engine protocol 的 cmd 一一对应：
            // open/state/scene/edit/insert/slide/notes/undo/redo/present/export...
            switch (action)
            {
                // ---- File ▸ 生命周期（P0-3~P0-9、P0-13）----
                case "NewDeck": _ = NewDeckAsync(); break;
                case "OpenDeck": _ = OpenDeckDialogAsync(); break;
                case "OpenDeckPackage": _ = OpenPackageDialogAsync(); break;
                case "Save": _ = SaveAsync(); break;
                case "Revert": _ = RevertAsync(); break;
                case "SaveProjectCopy": _ = SaveProjectCopyAsync(); break;
                case "ClearRecent": ClearRecent(); break;
                case "Package": _ = PackageProjectAsync(); break;
                case "Close": _ = CloseWindowAsync(); break;
                case "Quit": _ = QuitAsync(); break;

                // ---- File ▸ 导入（T04，P0-10/11）----
                case "Import": _ = ImportAsync(ImportKind.Pptx); break;
                case "ImportPptx": _ = ImportAsync(ImportKind.Pptx); break;
                case "ImportBeamer": _ = ImportAsync(ImportKind.Beamer); break;
                case "ImportKeynote": ShowKeynoteImportHint(".key"); break;
                case "ImportOldPpt": ShowKeynoteImportHint(".ppt"); break;

                // ---- File ▸ 导出为（T04，P0-12）----
                case "ExportPPT": _ = ExportAsync(ExportKind.Pptx); break;
                case "ExportMovie": _ = ExportAsync(ExportKind.Movie); break;
                case "ExportBeamer": _ = ExportAsync(ExportKind.Beamer); break;
                case "ExportPython": _ = ExportAsync(ExportKind.Python); break;
                case "ExportKeynote": _ = ExportAsync(ExportKind.KeynoteDisabled); break;
                case "ExportPDF": ExplainPdfExport(); break;

                // ---- LaTeX 缺包提示条 ----
                case "LatexRetry": _ = CheckLatexNoticeAsync(); break;
                case "CancelBusy": _ = CancelBusyAsync(); break;

                case "Undo": _ = UndoAsync(); break;
                case "Redo": _ = RedoAsync(); break;
                case "Cut": _ = CutSelectionAsync(); break;
                case "DeleteSelected": _ = DeleteSelectionAsync(); break;
                case "Copy": _ = CopySelectionAsync(); break;
                case "Paste": _ = PasteAsync(); break;
                case "SelectAll": SelectAll(); break;
                case "Find": OpenFind(false); break;
                case "Replace": OpenFind(true); break;
                case "ToggleReplaceRow":
                    if (Find.IsOpen) Find.ReplaceOpen = !Find.ReplaceOpen;
                    else OpenFind(true);
                    break;
                case "FindNext": Find.NextCommand.Execute(null); break;
                case "FindPrev": Find.PrevCommand.Execute(null); break;

                case "InsertText": InsertObject("Text"); break;
                case "InsertFormula": InsertFormulaFromDialog(); break;
                case "InsertImage": InsertObject("Image"); break;
                case "BioCartoon": OpenBioCartoon(); break;
                case "PythonPlot": OpenPythonPlot(); break;
                case "ExtractImages": _ = ExtractImages(); break;
                case "InsertTable": InsertObject("Table"); break;
                case "InsertShape": InsertObject("Rectangle"); break;
                case "InsertPen": ShowToast("绘制 ▸ 画笔 — 原型模式"); break;

                case "NewSlide": NewSlideFromToolbar(); break;
                case "DuplicateSlide": DuplicateSlide(); break;
                case "DeleteSlide": DeleteSlide(); break;
                case "HideSlide": if (CurrentSlide != null) { CurrentSlide.Hidden = !CurrentSlide.Hidden; ShowToast(CurrentSlide.Hidden ? "幻灯片已隐藏" : "幻灯片已显示"); } break;
                case "MoveSlideUp": MoveSlide(-1); break;
                case "MoveSlideDown": MoveSlide(1); break;

                case "Bold": ToggleFormat("Bold"); break;
                case "Italic": ToggleFormat("Italic"); break;
                case "Underline": ToggleFormat("Underline"); break;
                case "Theme": OpenThemePicker(); break;
                case "SlideSetup": OpenSlideSetup(); break;
                case "AlignLeft": _ = AlignSelectionAsync("left"); break;
                case "AlignCenter": _ = AlignSelectionAsync("center"); break;
                case "AlignRight": _ = AlignSelectionAsync("right"); break;
                case "AlignTop": _ = AlignSelectionAsync("top"); break;
                case "AlignMiddle": _ = AlignSelectionAsync("middle"); break;
                case "AlignBottom": _ = AlignSelectionAsync("bottom"); break;
                case "DistributeH": _ = DistributeSelectionAsync(true); break;
                case "DistributeV": _ = DistributeSelectionAsync(false); break;

                case "Front": _ = ChangeOrderAsync("front"); break;
                case "Forward": _ = ChangeOrderAsync("forward"); break;
                case "Backward": _ = ChangeOrderAsync("backward"); break;
                case "Back": _ = ChangeOrderAsync("back"); break;
                case "Group": _ = GroupSelectionAsync(false); break;
                case "Ungroup": _ = GroupSelectionAsync(true); break;

                case "GridLines": ShowGrid = !ShowGrid; break;
                case "Ruler": ShowRuler = !ShowRuler; break;
                case "Snap": SnapToObjects = !SnapToObjects; break;
                case "ShowObjects": ShowObjects = !ShowObjects; break;
                case "SpeakerNotes": ShowDrawer(0); break;
                case "DarkModeApp": IsDarkMode = !IsDarkMode; ShowToast(IsDarkMode ? "界面已切换为深色" : "界面已切换为浅色"); break;
                // 演示文稿深色 / 反色：改的是 deck 源码里的颜色，与应用界面无关
                case "DeckDark": _ = ApplyDeckColorsAsync("dark"); break;
                case "DeckInvert": _ = ApplyDeckColorsAsync("invert"); break;
                case "InvertColors": _ = ApplyDeckColorsAsync("invert"); break;   // 旧参数名，保留兼容
                case "ShowTerminal": ShowTerminalPanel(); break;
                case "NewTerminal":
                    ShowTerminalPanel();
                    var fresh = Terminal.NewSession(null);
                    ShowToast("已新建终端标签：" + fresh.Title + "（与原标签互不影响）");
                    break;
                case "CloseTerminal": TerminalVisible = false; break;
                case "ToggleNavigator": NavigatorVisible = !NavigatorVisible; break;
                case "ToggleInspector": InspectorVisible = !InspectorVisible; break;

                case "PlayFromBeginning": OpenPreview(0); break;
                case "PlayFromCurrent": OpenPreview(); break;
                case "Presenter": OpenPresenter(); break;
                case "PreviewAnimation": OpenPreviewCurrentOnly(); break;

                case "Docs": ShowToast("文档：pachterlab.github.io/Manimble"); break;
                case "About": ShowToast("ManimbleWin — 界面原型 0.5.0"); break;
                case "Insert": InsertObject("Text"); break;
                default: if (!string.IsNullOrEmpty(action)) ShowToast(action + " — 原型模式"); break;
            }
        }

        private void ShowAnimationsCommand(string where)
        {
            BusyText = "Building animations 0/3";
            ShowToast("从" + (where == "beginning" ? "开头" : "当前页") + "播放 — 原型模式");
            // 原型：短暂显示忙碌指示后清除
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
            timer.Tick += (s, e) => { BusyText = ""; timer.Stop(); };
            timer.Start();
        }

        private void MoveSlide(int delta)
        {
            if (CurrentSlide == null) return;
            int i = Slides.IndexOf(CurrentSlide);
            int j = i + delta;
            if (j < 0 || j >= Slides.Count) return;
            Slides.Move(i, j);
            for (int k = 0; k < Slides.Count; k++) Slides[k].Number = k + 1;
            ShowToast("幻灯片已移动");
        }

        private void ToggleFormat(string prop)
        {
            if (SelectedObject == null) { ShowToast("请先选择一个文本对象"); return; }
            switch (prop)
            {
                case "Bold": SelectedObject.Bold = !SelectedObject.Bold; break;
                case "Italic": SelectedObject.Italic = !SelectedObject.Italic; break;
                case "Underline": SelectedObject.Underline = !SelectedObject.Underline; break;
            }
            ShowToast(prop + " 已切换");
        }

        #endregion

        #region 引擎 / 依赖方法

        /// <summary>供引擎桥接刷新 CurrentBuild 相关派生属性。</summary>
        public void RaiseCurrentChanged()
        {
            RaisePropertyChanged(nameof(CurrentBuild));
            RaisePropertyChanged(nameof(CurrentObjects));
            RaisePropertyChanged(nameof(UseRealRender));
            RaisePropertyChanged(nameof(StatusBarText));
            RebuildSourceLines();
        }

        /// <summary>
        /// 「连接引擎」统一走生命周期入口：建工作副本 → 引擎 open 副本。
        /// 已打开 deck 时重开原件（会先走未保存确认）；否则打开示例 deck 作为起点。
        /// </summary>
        private async Task ConnectEngine()
        {
            if (_engine.IsConnected && _session != null)
            {
                ShowToast("引擎已连接");
                return;
            }
            EngineStatus = "正在启动引擎…";
            string deck = _session?.OriginalDeckPath ?? EnvironmentService.SampleDeck;
            bool ok = await OpenDeckAsync(deck, confirmUnsaved: _session == null);
            if (!ok)
            {
                EngineStatus = "未连接 · 原型数据";
                RaisePropertyChanged(nameof(IsEngineConnected));
                RaisePropertyChanged(nameof(EngineStatusBrush));
                ShowToast("引擎未连接，可在依赖中心安装后重试");
            }
        }

        /// <summary>打开依赖中心/首次启动向导。</summary>
        public void OpenDependencyCenter(bool firstRun)
        {
            var vm = new SetupViewModel(this, firstRun);
            var win = new Views.SetupWindow(vm)
            {
                Owner = Application.Current?.MainWindow
            };
            var result = win.ShowDialog();
            if (result == true && !firstRun)
            {
                // 用户从依赖中心关闭：若核心依赖就绪且引擎未连，自动尝试连接
                var report = EnvironmentService.Detect();
                if (report.CoreReady && !_engine.IsConnected)
                    _ = ConnectEngine();
            }
        }

        #region 源码编辑 / 预览 / 终端桥接

        /// <summary>应用代码窗格的修改：写回引擎（若连接）→ 刷新源码窗格；失败时草稿原样保留。</summary>
        private async void ApplySourceEdit()
        {
            if (CurrentSlide == null) return;
            string edited = SourceText ?? "";
            SourceEditStatus = "正在应用…";

            if (_engine != null && _engine.IsConnected)
            {
                // expected 由引擎侧实时读取，避免本地按行拼出的 CRLF / 尾注释被误判为冲突。
                bool ok = await _engine.ApplyTextEditFromEditor(CurrentSlide, edited);
                if (ok)
                {
                    _appliedSource = edited;
                    SourceEditStatus = "已应用并重新渲染";
                    SourceDraftBackup = "";
                    ShowToast("代码已应用，画布已重新渲染");
                    return;
                }

                // 没写回成功也绝不丢草稿：本地留一份，编辑器仍显示用户原文。
                // 若冲突对话框里已选择「加载引擎版本」，编辑器里的 SourceText 已变，
                // 这时不能再把刚加载的源码覆盖回草稿。
                if (string.Equals(SourceText, edited, StringComparison.Ordinal))
                {
                    CurrentSlide.Source = edited;
                    RebuildSourceLines();
                    SourceEditStatus = "草稿已保留（未写回引擎）";
                }
                ShowToast("源码未写回；草稿已保留，请按代码窗格提示处理");
            }
            else
            {
                _appliedSource = edited;
                CurrentSlide.Source = edited;
                RebuildSourceLines();
                SourceEditStatus = "已保存（本地）";
                ShowToast("代码已更新；连接引擎后可重新渲染验证");
            }
        }

        private void RevertSourceEdit()
        {
            SyncSourceText(force: true);
            ShowToast("已还原为上次保存的源码");
        }

        /// <summary>冲突时用户选择「加载引擎最新版本」：暂存草稿后再刷新，避免丢改动。</summary>
        internal void LoadSourceFromEngine(Slide slide, string engineText, string draft)
        {
            if (slide == null) return;
            slide.Source = engineText ?? "";
            SourceDraftBackup = draft ?? "";
            SyncSourceText(force: true);
            SourceEditStatus = HasSourceDraftBackup
                ? "已加载引擎版本；草稿已暂存，可点「草稿」恢复"
                : "已加载引擎版本";
            ShowToast(HasSourceDraftBackup ? "源码冲突：已加载引擎版本，草稿可恢复" : "已加载引擎版本");
        }

        private void RestoreSourceDraft()
        {
            if (!HasSourceDraftBackup)
            {
                ShowToast("没有暂存的草稿");
                return;
            }
            string draft = SourceDraftBackup;
            SourceDraftBackup = "";
            SourceText = draft;
            SourceEditStatus = "已恢复暂存草稿（待应用）";
            ShowToast("已恢复暂存的源码草稿");
        }

        /// <summary>打开动画预览窗口。startIndex 为起始幻灯片索引（-1 表示当前页）。</summary>
        private void OpenPreview(int startIndex = -1)
        {
            var vm = new PreviewViewModel(this, startIndex);
            var win = new Views.PreviewWindow(vm) { Owner = Application.Current?.MainWindow };
            win.Show();
        }

        private void OpenPreviewCurrentOnly()
        {
            var vm = new PreviewViewModel(this, -1) { OnlyCurrent = true };
            var win = new Views.PreviewWindow(vm) { Owner = Application.Current?.MainWindow };
            win.Show();
        }

        /// <summary>打开演讲者视图：当前页/下一页静帧 + 备注 + 计时器（静帧来自 deck_pages）。</summary>
        private void OpenPresenter()
        {
            var vm = new PresenterViewModel(this);
            var win = new Views.PresenterWindow(vm) { Owner = Application.Current?.MainWindow };
            win.Show();
        }

        /// <summary>供预览窗调用：让引擎用 manim 渲染某场景的真实动画片段。</summary>
        public Task<List<AnimationClip>> BuildAnimationClipsAsync(string scene)
            => _engine != null ? _engine.BuildAnimationPreviewAsync(scene)
                               : Task.FromResult<List<AnimationClip>>(null);

        // ---- 终端桥接方法 ----
        public void ConnectFromTerminal() => _ = ConnectEngine();

        public void RenderFormulaFromTerminal(string tex, Action<bool, string> report)
        {
            _ = Task.Run(async () =>
            {
                string path = await _engine.RenderFormula(tex, 72, "#FFFFFF");
                bool ok = path != null;
                string msg = ok ? ("公式已渲染：" + Path.GetFileName(path)) : "渲染失败：请确认引擎已连接且 LaTeX 就绪";
                await Application.Current.Dispatcher.InvokeAsync(() => report(ok, msg));
                if (ok)
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true }); } catch { }
            });
        }

        public void InsertObjectFromTerminal(string kind)
        {
            Application.Current?.Dispatcher.Invoke(() => InsertObject(kind));
        }

        public void OpenPreviewFromTerminal()
        {
            Application.Current?.Dispatcher.Invoke(() => OpenPreview());
        }

        public void OpenSourceFromTerminal()
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                InspectorVisible = true;
                ShowToast("已在右侧显示代码窗格");
            });
        }

        public void SelectSlideByIndex(int index)
        {
            if (index >= 0 && index < Slides.Count)
                Application.Current?.Dispatcher.Invoke(() => CurrentSlide = Slides[index]);
        }

        public void ExportMovieFromTerminal(Action<string> report)
        {
            _ = Task.Run(async () =>
            {
                if (!_engine.IsConnected)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() => report("导出需要连接 Manim 引擎；原型模式下跳过。"));
                    return;
                }
                string path = Path.Combine(EnvironmentService.UserDataDir, "ManimbleShow.mp4");
                await Application.Current.Dispatcher.InvokeAsync(() => report("正在导出 MP4：" + path));
                string result = await _engine.ExportMovie(path);
                await Application.Current.Dispatcher.InvokeAsync(() => report(result != null ? "导出完成：" + result : "导出失败"));
            });
        }

        #endregion

        private async Task RenderFormulaInteractive(string tex)
        {
            // 参数来自终端输入或菜单；为空时渲染一个默认示例公式
            string value = string.IsNullOrWhiteSpace(tex) ? @"e^{i\pi}+1=0" : tex;
            ShowToast($"正在渲染公式：{value}");
            string path = await _engine.RenderFormula(value, 72, "#FFFFFF");
            if (path != null)
            {
                ShowToast("公式已渲染：" + Path.GetFileName(path));
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true }); } catch { }
            }
            else ShowToast("公式渲染失败：请确认已连接引擎且 LaTeX 就绪");
        }

        #endregion
    }

    /// <summary>用于把一个 Build 连同它所属 Slide 传给命令（Navigator 里的 build 缩略图）。</summary>
    public class BuildTag
    {
        public Slide Slide { get; set; }
        public int Index { get; set; }
    }

    /// <summary>画布标尺上的一个刻度。</summary>
    public class RulerMark
    {
        public double Pos { get; set; }
        public double Tick { get; set; }
        public string Label { get; set; }
    }

    /// <summary>源码窗格中的一行，带简单语法着色。</summary>
    public class SourceLine
    {
        public int Number { get; set; }
        public string Text { get; set; }
        public Brush ColorBrush { get; set; }
        public bool Highlighted { get; set; }
    }
}
