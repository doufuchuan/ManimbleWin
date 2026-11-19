using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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
    public class MainViewModel : ObservableObject
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
                    if (value.CurrentBuildIndex < 0 || value.CurrentBuildIndex >= value.Builds.Count)
                        value.CurrentBuildIndex = 0;
                    SelectedObject = null;
                    RaisePropertyChanged(nameof(CurrentBuild));
                    RaisePropertyChanged(nameof(CurrentObjects));
                    RaisePropertyChanged(nameof(UseRealRender));
                    RaisePropertyChanged(nameof(StatusBarText));
                    RebuildSourceLines();
                    SyncSourceText();
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

        /// <summary>把当前页源码同步到可编辑文本框（切页/引擎刷新时调用）。</summary>
        private void SyncSourceText()
        {
            SourceText = CurrentSlide?.Source ?? "";
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
        public CanvasObject SelectedObject
        {
            get => _selectedObject;
            set
            {
                if (SetProperty(ref _selectedObject, value))
                {
                    foreach (var s in Slides)
                        foreach (var b in s.Builds)
                            foreach (var o in b.Objects)
                                o.IsSelected = ReferenceEquals(o, value);
                    RaisePropertyChanged(nameof(HasSelection));
                    RebuildSourceLines();
                }
            }
        }

        public bool HasSelection => SelectedObject != null;

        private DeckTheme _currentTheme;
        public DeckTheme CurrentTheme
        {
            get => _currentTheme;
            set
            {
                if (SetProperty(ref _currentTheme, value) && value != null)
                {
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

        /// <summary>独立终端面板（覆盖画布区域），保证点“终端”一定能看到。</summary>
        private bool _terminalVisible;
        public bool TerminalVisible { get => _terminalVisible; set => SetProperty(ref _terminalVisible, value); }

        /// <summary>打开终端面板：覆盖在画布上，占满中间列。</summary>
        public void ShowTerminalPanel()
        {
            TerminalVisible = true;
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

        public string StatusBarText => CurrentSlide == null
            ? "未加载演示文稿"
            : $"演示文稿: hello   ·   第 {CurrentSlide.Number}/{Slides.Count} 页   ·   构建 {CurrentSlide.CurrentBuildIndex + 1}/{CurrentSlide.Builds.Count}   ·   {CurrentObjects?.Count ?? 0} 个对象";

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
        public ICommand OpenPreviewCommand { get; }

        /// <summary>交互式终端视图模型。</summary>
        public TerminalViewModel Terminal { get; }

        /// <summary>当前 deck 所在文件夹（终端 pwd 用）。</summary>
        public string DeckFolder => EnvironmentService.SampleDeck;

        #endregion

        #region 抽屉（备注 / 替代文本 / 终端）

        public ObservableCollection<string> DrawerTabs { get; } = new ObservableCollection<string>
        {
            "Slide Notes", "Build Notes", "Alt Text", "Terminal 1"
        };

        private int _selectedDrawerTab;
        public int SelectedDrawerTab { get => _selectedDrawerTab; set => SetProperty(ref _selectedDrawerTab, value); }

        public string SlideNotesText
        {
            get => CurrentSlide?.Builds.FirstOrDefault()?.Notes ?? "";
            set { if (CurrentSlide?.Builds.Count > 0) { CurrentSlide.Builds[0].Notes = value; RaisePropertyChanged(nameof(SlideNotesText)); } }
        }

        public string BuildNotesText
        {
            get => CurrentBuild?.BuildNotes ?? "";
            set { if (CurrentBuild != null) { CurrentBuild.BuildNotes = value; RaisePropertyChanged(nameof(BuildNotesText)); } }
        }

        public string AltTextText
        {
            get => CurrentSlide?.AltText ?? "";
            set { if (CurrentSlide != null) { CurrentSlide.AltText = value; RaisePropertyChanged(nameof(AltTextText)); } }
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
            OpenFindCommand = new RelayCommand(_ => { ShowToast("查找和替换 — 原型模式"); });
            ConnectEngineCommand = new RelayCommand(_ => _ = ConnectEngine());
            OpenDependencyCenterCommand = new RelayCommand(_ => OpenDependencyCenter(false));
            RenderFormulaCommand = new RelayCommand(p => _ = RenderFormulaInteractive(p?.ToString()));
            ApplySourceEditCommand = new RelayCommand(_ => ApplySourceEdit());
            RevertSourceEditCommand = new RelayCommand(_ => RevertSourceEdit());
            OpenPreviewCommand = new RelayCommand(_ => OpenPreview());
            Terminal = new TerminalViewModel(this);

            BuildThemes();
            BuildSampleDeck();
            ThemeManager.Apply(IsDarkMode);

            // 让画布主题跟随 deck 主题
            CurrentTheme = Themes.FirstOrDefault(t => t.Id == "midnight");
            CurrentSlide = Slides.FirstOrDefault();

            // 引擎桥接
            _engine = new EngineIntegration(this);
            _engine.Log += msg => ShowToast(msg);

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

            // 首次启动：窗口呈现后再弹出环境准备向导，避免阻塞主窗口构造
            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!AppFlags.SetupSeen)
                {
                    OpenDependencyCenter(true);
                    AppFlags.SetupSeen = true;
                }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        #region 数据构建

        private void BuildThemes()
        {
            // 取自 engine/manimble/themes/*.py 的常量
            Themes.Add(new DeckTheme { Id = "midnight", Name = "Midnight", Background = "#000000", Text = "#FFFFFF", Muted = "#9AA0A6", Accent = "#7C5CFF", Font = "DejaVu Sans" });
            Themes.Add(new DeckTheme { Id = "paper", Name = "Paper", Background = "#F7F5EF", Text = "#1A1A1A", Muted = "#6B6B6B", Accent = "#C1440E", Font = "Georgia" });
            Themes.Add(new DeckTheme { Id = "chalkboard", Name = "Chalkboard", Background = "#1F2A24", Text = "#F2F0E6", Muted = "#A9B8AE", Accent = "#8FD19E", Font = "Comic Sans MS" });
            Themes.Add(new DeckTheme { Id = "ember", Name = "Ember", Background = "#1A1012", Text = "#FBE9E2", Muted = "#C08A7A", Accent = "#FF6C0C", Font = "Segoe UI" });
            Themes.Add(new DeckTheme { Id = "slate", Name = "Slate", Background = "#2B3038", Text = "#EDF1F5", Muted = "#9AA6B2", Accent = "#5B9BD5", Font = "Segoe UI" });
            Themes.Add(new DeckTheme { Id = "parchment", Name = "Parchment", Background = "#EFE2C4", Text = "#3B2E16", Muted = "#7A6840", Accent = "#8B5E3C", Font = "Palatino Linotype" });
            Themes.Add(new DeckTheme { Id = "terminal", Name = "Terminal", Background = "#0C0C0C", Text = "#33FF66", Muted = "#1FA347", Accent = "#33FF66", Font = "Consolas" });
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

        private void ApplyTheme(DeckTheme t)
        {
            CurrentTheme = t;
            ShowToast($"已应用主题：{t.Name}");
        }

        private void Nudge(string dir)
        {
            if (SelectedObject == null) return;
            double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 2;
            switch (dir)
            {
                case "Left": SelectedObject.X -= step; break;
                case "Right": SelectedObject.X += step; break;
                case "Up": SelectedObject.Y -= step; break;
                case "Down": SelectedObject.Y += step; break;
            }
            // 方向键微调同样写回源码坐标
            CommitObjectMove(SelectedObject);
        }

        public void ShowToast(string text)
        {
            ToastText = text;
            ToastVisible = true;
            StatusMessage = text;
        }

        public void HideToast() => ToastVisible = false;

        public void MoveObject(CanvasObject o, double dx, double dy) { o.X += dx; o.Y += dy; }

        private void ExecuteToolbar(string action)
        {
            switch (action)
            {
                case "NewSlide": NewSlideFromToolbar(); break;
                case "NewBuild": NewBuild(); break;
                case "ExpandAll": ShowToast("已展开导航器中的所有构建"); break;
                case "Theme": ShowToast("主题菜单（在工具栏选择色块）"); break;
                case "DarkMode": IsDarkMode = !IsDarkMode; ShowToast(IsDarkMode ? "深色模式已开启" : "深色模式已关闭"); break;
                case "Terminal": ShowTerminalPanel(); break;
                case "ZoomIn": Zoom += 0.1; break;
                case "ZoomOut": Zoom -= 0.1; break;
                case "ZoomFit": Zoom = 0.72; break;
                case "Share": ShowToast("导出 / 分享 — 原型模式"); break;
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
                string line = SourceCodeGenerator.LineFor(o, SlideDisplayWidth / Zoom, SlideDisplayHeight / Zoom);
                CurrentSlide.Source = SourceCodeGenerator.AppendLine(CurrentSlide.Source, line);
                o.SourceLine = CountLine(CurrentSlide.Source, line);
                o.Loc = $"{CurrentSlide.File}:{o.SourceLine}";
                RebuildSourceLines();
                SyncSourceText();
                SourceEditStatus = "代码已更新（待应用）";
                // 引擎已连接：真正写回工作副本并重渲染
                if (_engine != null && _engine.IsConnected)
                {
                    string expected = CurrentSlide.Source; // 简化：以当前内容为基准
                    _ = _engine.ApplyTextEdit(CurrentSlide, expected, CurrentSlide.Source);
                }
            }
            ShowToast($"已插入 {kind} · Python 代码已同步");
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

        /// <summary>渲染单个公式对象：写 SpritePath 后画布即显示真实排版。</summary>
        private async void RenderFormulaObject(CanvasObject o)
        {
            if (o == null) return;
            string tex = string.IsNullOrWhiteSpace(o.Value) ? o.DisplayText : o.Value;
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
            if (!_latexRequested.Add(key)) return;
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
            var dlg = new Views.FormulaDialog { Owner = Application.Current?.MainWindow };
            if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Expression)) return;
            InsertFormula(dlg.Expression);
        }

        /// <summary>插入一个 LaTeX 公式对象（真排版由 LatexService 完成）。</summary>
        public void InsertFormula(string tex)
        {
            if (CurrentBuild == null || string.IsNullOrWhiteSpace(tex)) return;
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
                case "NewDeck": ShowToast("文件 ▸ 新建演示文稿… — 原型模式"); break;
                case "OpenDeck": ShowToast("文件 ▸ 打开演示文稿或项目… — 原型模式"); break;
                case "Save": ShowToast("已保存到原始文件（模拟）"); break;
                case "Close": Application.Current.MainWindow?.Close(); break;
                case "Quit": Application.Current.Shutdown(); break;

                case "Undo": ShowToast("撤销 — 原型模式"); break;
                case "Redo": ShowToast("重做 — 原型模式"); break;
                case "Cut":
                case "DeleteSelected":
                    if (SelectedObject != null)
                    {
                        var target = SelectedObject;
                        SelectedObject = null;
                        if (CurrentBuild != null) CurrentBuild.Objects.Remove(target);
                        ShowToast("对象已删除");
                    }
                    break;
                case "Copy": ShowToast("复制 — 原型模式"); break;
                case "Paste": InsertObject("Text"); break;
                case "SelectAll": ShowToast("全选 — 原型模式"); break;
                case "Find": OpenFindCommand.Execute(null); break;
                case "Replace": ShowToast("查找和替换 — 原型模式"); break;

                case "InsertText": InsertObject("Text"); break;
                case "InsertFormula": InsertFormulaFromDialog(); break;
                case "InsertImage": InsertObject("Image"); break;
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
                case "Theme": ShowToast("格式 ▸ 主题 — 请在工具栏选择色块"); break;
                case "AlignLeft": ShowToast("左对齐"); break;
                case "AlignCenter": ShowToast("居中对齐"); break;
                case "AlignRight": ShowToast("右对齐"); break;

                case "Front": ShowToast("置于顶层"); break;
                case "Group": ShowToast("组合 — 原型模式"); break;
                case "Ungroup": ShowToast("取消组合 — 原型模式"); break;

                case "GridLines": ShowGrid = !ShowGrid; break;
                case "Ruler": ShowRuler = !ShowRuler; break;
                case "Snap": SnapToObjects = !SnapToObjects; break;
                case "ShowObjects": ShowObjects = !ShowObjects; break;
                case "SpeakerNotes": ShowDrawer(0); break;
                case "DarkModeApp": IsDarkMode = !IsDarkMode; break;
                case "InvertColors": ShowToast("反色 — 原型模式"); break;
                case "ShowTerminal": ShowTerminalPanel(); break;
                case "NewTerminal": ShowTerminalPanel(); ShowToast("已打开终端面板"); break;
                case "CloseTerminal": TerminalVisible = false; break;
                case "ToggleNavigator": NavigatorVisible = !NavigatorVisible; break;
                case "ToggleInspector": InspectorVisible = !InspectorVisible; break;

                case "PlayFromBeginning": OpenPreview(0); break;
                case "PlayFromCurrent": OpenPreview(); break;
                case "Presenter": OpenPreview(); break;
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

        private async Task ConnectEngine()
        {
            if (_engine.IsConnected)
            {
                ShowToast("引擎已连接");
                return;
            }
            EngineStatus = "正在启动引擎…";
            bool ok = await _engine.ConnectAndLoad();
            EngineStatus = ok ? "已连接 · 真实渲染" : "未连接 · 原型数据";
            RaisePropertyChanged(nameof(IsEngineConnected));
            RaisePropertyChanged(nameof(EngineStatusBrush));
            RaisePropertyChanged(nameof(UseRealRender));
            ShowToast(ok ? "Manim 引擎已连接，正在渲染示例 deck" : "引擎未连接，可在依赖中心安装后重试");
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

        /// <summary>应用代码窗格的修改：更新本地源码 → 写回引擎（若连接）→ 刷新源码窗格。</summary>
        private async void ApplySourceEdit()
        {
            if (CurrentSlide == null) return;
            string edited = SourceText ?? "";
            string before = CurrentSlide.Source;
            CurrentSlide.Source = edited;
            RebuildSourceLines();
            SourceEditStatus = "正在应用…";

            if (_engine != null && _engine.IsConnected)
            {
                bool ok = await _engine.ApplyTextEdit(CurrentSlide, before, edited);
                SourceEditStatus = ok ? "已应用并重新渲染" : "已保存到本地（引擎校验未通过）";
                ShowToast(ok ? "代码已应用，画布已重新渲染" : "代码已保存到本地；语法或渲染错误，请检查");
            }
            else
            {
                SourceEditStatus = "已保存（本地）";
                ShowToast("代码已更新；连接引擎后可重新渲染验证");
            }
        }

        private void RevertSourceEdit()
        {
            SyncSourceText();
            ShowToast("已还原为上次保存的源码");
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
