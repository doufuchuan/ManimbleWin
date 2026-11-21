using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;

namespace ManimbleWin.Models
{
    /// <summary>画布上可选中/编辑的单个对象，对应 Manimble 引擎 protocol 里 scene 结果的一个对象。</summary>
    public class CanvasObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private void Notify(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        private string _oid = Guid.NewGuid().ToString("N").Substring(0, 8);
        private ObjectKind _kind;
        private string _value = "";
        private string _displayText = "";
        private double _fontSize = 28;
        private string _color = "#FFFFFF";
        private string _font = "Segoe UI";
        private bool _bold, _italic, _underline, _strike;
        private string _weight, _slant, _highlightColor, _paragraphAlign;
        private int _columns = 1;
        private double? _lineSpacing;
        private bool _isTextEditable, _hasTextBox;
        private int _z, _sourceLine = 1;
        private double _x, _y, _width, _height;
        private double _rotation;
        private bool _isSelected;
        private int _build = 0;
        private bool _shared;
        private string _shapeLabel = "";
        private string _imageLabel = "";
        private string _imagePath = "";
        private string _loc = "slides.py:1";
        private string _spritePath = "";
        private string _codeId = "";
        private string _groupId = "";

        /// <summary>
        /// 组合标识。原型模式下引擎不参与，组合关系只存在于应用内：
        /// 同一 GroupId 的对象一起选中、一起移动；连接引擎后组合由源码里的 Group(...) 表达。
        /// </summary>
        public string GroupId { get => _groupId; set { _groupId = value; Notify(nameof(GroupId)); Notify(nameof(IsGrouped)); } }

        public bool IsGrouped => !string.IsNullOrEmpty(_groupId);

        /// <summary>写进源码注释里的稳定标识（# manimble:ab12cd），用于精确找回对象对应的代码行。</summary>
        public string CodeId { get => _codeId; set { _codeId = value; Notify(nameof(CodeId)); } }

        public string Oid { get => _oid; set { _oid = value; Notify(nameof(Oid)); } }
        public ObjectKind Kind { get => _kind; set { _kind = value; Notify(nameof(Kind)); Notify(nameof(KindLabel)); Notify(nameof(FontFamilyName)); } }
        public string Value { get => _value; set { _value = value; Notify(nameof(Value)); } }
        public string DisplayText { get => _displayText; set { _displayText = value; Notify(nameof(DisplayText)); } }
        public double FontSize { get => _fontSize; set { _fontSize = value; Notify(nameof(FontSize)); } }
        public string Color { get => _color; set { _color = value; Notify(nameof(Color)); Notify(nameof(Brush)); } }
        public string Font { get => _font; set { _font = value; Notify(nameof(Font)); Notify(nameof(FontFamilyName)); } }
        public bool Bold { get => _bold; set { _bold = value; Notify(nameof(Bold)); } }
        public bool Italic { get => _italic; set { _italic = value; Notify(nameof(Italic)); } }
        public bool Underline { get => _underline; set { _underline = value; Notify(nameof(Underline)); } }
        public bool Strike { get => _strike; set { _strike = value; Notify(nameof(Strike)); } }

        // ---- 格式状态（全部由引擎 scene 结果回填，不在本地猜） ----
        // 这些字段每次刷新都会被重新赋一遍同样的值；不短路的话一次刷新会打出几十条无意义的
        // PropertyChanged，把整个格式栏的绑定重算一遍。所以 setter 一律先比再赋。

        /// <summary>原始字重字符串（BOLD / SEMIBOLD / NORMAL / null）。保留它只为了排错，按钮看 <see cref="Bold"/>。</summary>
        public string Weight
        {
            get => _weight;
            set { if (_weight != value) { _weight = value; Notify(nameof(Weight)); } }
        }

        /// <summary>原始倾斜字符串（ITALIC / OBLIQUE / NORMAL / null）。</summary>
        public string Slant
        {
            get => _slant;
            set { if (_slant != value) { _slant = value; Notify(nameof(Slant)); } }
        }

        /// <summary>
        /// 高亮色。三态：<c>#RRGGBB</c>（有色）、<c>null</c>（无高亮）、<c>"on"</c>（源码有
        /// <c>add_background_rectangle</c> 但没写颜色字面量）。"on" 不能归一化成 null，
        /// 否则灯会误灭 —— 那个对象确实有底块。
        /// </summary>
        public string HighlightColor
        {
            get => _highlightColor;
            set
            {
                if (_highlightColor != value)
                {
                    _highlightColor = value;
                    Notify(nameof(HighlightColor));
                    Notify(nameof(HasHighlight));
                }
            }
        }

        /// <summary>有底块（不管取不取得到颜色）。</summary>
        public bool HasHighlight => !string.IsNullOrEmpty(_highlightColor);

        /// <summary>段落对齐（left / center / right / null=源码没写）。只用于对齐三键的灯。</summary>
        public string ParagraphAlign
        {
            get => _paragraphAlign;
            set { if (_paragraphAlign != value) { _paragraphAlign = value; Notify(nameof(ParagraphAlign)); } }
        }

        /// <summary>分栏数，引擎默认 1。</summary>
        public int Columns
        {
            get => _columns;
            set { if (_columns != value) { _columns = value; Notify(nameof(Columns)); } }
        }

        /// <summary>行距倍数；null = 源码里没有 line_spacing（即引擎默认）。</summary>
        public double? LineSpacing
        {
            get => _lineSpacing;
            set { if (_lineSpacing != value) { _lineSpacing = value; Notify(nameof(LineSpacing)); } }
        }

        /// <summary>
        /// 引擎的 <c>textOk</c>：这个对象的文字能不能用 <c>text</c> op 改写。
        /// 由辅助函数生成的文本为 false —— 提前拦下比等引擎抛 TypeError 好读。
        /// </summary>
        public bool IsTextEditable
        {
            get => _isTextEditable;
            set { if (_isTextEditable != value) { _isTextEditable = value; Notify(nameof(IsTextEditable)); } }
        }

        /// <summary>有文本框（引擎返回了 frame）。段落对齐要求「多行文本 或 有文本框」，靠它预判。</summary>
        public bool HasTextBox
        {
            get => _hasTextBox;
            set { if (_hasTextBox != value) { _hasTextBox = value; Notify(nameof(HasTextBox)); } }
        }
        public int Z { get => _z; set { _z = value; Notify(nameof(Z)); } }
        public int SourceLine { get => _sourceLine; set { _sourceLine = value; Notify(nameof(SourceLine)); } }
        public double X { get => _x; set { _x = value; Notify(nameof(X)); } }
        public double Y { get => _y; set { _y = value; Notify(nameof(Y)); } }
        public double Width { get => _width; set { _width = value; Notify(nameof(Width)); } }
        public double Height { get => _height; set { _height = value; Notify(nameof(Height)); } }
        public double Rotation { get => _rotation; set { _rotation = value; Notify(nameof(Rotation)); } }
        public bool IsSelected { get => _isSelected; set { _isSelected = value; Notify(nameof(IsSelected)); } }
        public int Build { get => _build; set { _build = value; Notify(nameof(Build)); } }
        public bool Shared { get => _shared; set { _shared = value; Notify(nameof(Shared)); } }
        public string ShapeLabel { get => _shapeLabel; set { _shapeLabel = value; Notify(nameof(ShapeLabel)); } }
        public string ImageLabel { get => _imageLabel; set { _imageLabel = value; Notify(nameof(ImageLabel)); } }

        /// <summary>图片对象的文件路径：引擎返回的真实图片路径，或本地占位时选中的文件绝对路径。</summary>
        public string ImagePath
        {
            get => _imagePath;
            set { if (_imagePath != value) { _imagePath = value; Notify(nameof(ImagePath)); Notify(nameof(HasImage)); } }
        }
        /// <summary>图片路径有效且文件存在时显示真实缩略图，否则在画布上回退到图标+文件名。</summary>
        public bool HasImage => !string.IsNullOrEmpty(_imagePath) && System.IO.File.Exists(_imagePath);

        public string Loc { get => _loc; set { _loc = value; Notify(nameof(Loc)); } }

        /// <summary>引擎 sprite 命令渲染出的该对象透明 PNG（公式=真实 LaTeX 渲染）。</summary>
        public string SpritePath
        {
            get => _spritePath;
            set { if (_spritePath != value) { _spritePath = value; Notify(nameof(SpritePath)); Notify(nameof(HasSprite)); } }
        }
        public bool HasSprite => !string.IsNullOrEmpty(_spritePath) && System.IO.File.Exists(_spritePath);

        public string KindLabel
        {
            get
            {
                switch (Kind)
                {
                    case ObjectKind.Text: return "文本";
                    case ObjectKind.Tex: return "公式";
                    case ObjectKind.Image: return "图片";
                    case ObjectKind.Line: return "线条";
                    case ObjectKind.Shape: return "形状";
                    case ObjectKind.Table: return "表格";
                    default: return Kind.ToString();
                }
            }
        }

        public string FontFamilyName => Kind == ObjectKind.Tex ? "Cambria Math" : Font;

        public Brush Brush
        {
            get
            {
                try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(Color)); }
                catch { return Brushes.White; }
            }
        }

        public CanvasObject Clone()
        {
            return (CanvasObject)MemberwiseClone();
        }
    }

    public enum ObjectKind { Text, Tex, Image, Line, Shape, Table }

    /// <summary>一个 build（next_slide 之间的一段），含该阶段可见的对象与备注。</summary>
    public class Build : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private void Notify(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        private string _name = "Build";
        private string _pngKey = "";
        private string _notes = "";
        private string _buildNotes = "";
        private string _renderedImage = "";
        private ObservableCollection<CanvasObject> _objects = new ObservableCollection<CanvasObject>();

        public string Name { get => _name; set { _name = value; Notify(nameof(Name)); } }
        public string PngKey { get => _pngKey; set { _pngKey = value; Notify(nameof(PngKey)); } }
        public string Notes { get => _notes; set { _notes = value; Notify(nameof(Notes)); } }
        public string BuildNotes { get => _buildNotes; set { _buildNotes = value; Notify(nameof(BuildNotes)); } }

        /// <summary>引擎真实渲染出的整帧 PNG 绝对路径（有值时画布优先显示它）。</summary>
        public string RenderedImage
        {
            get => _renderedImage;
            set { if (_renderedImage != value) { _renderedImage = value; Notify(nameof(RenderedImage)); Notify(nameof(HasRealRender)); } }
        }
        public bool HasRealRender => !string.IsNullOrEmpty(_renderedImage) && System.IO.File.Exists(_renderedImage);

        public ObservableCollection<CanvasObject> Objects { get => _objects; set { _objects = value; Notify(nameof(Objects)); } }
    }

    /// <summary>一张幻灯片，含多个 build 与底层 Python 源码。</summary>
    public class Slide : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private void Notify(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        private string _name;
        private string _file = "slides.py";
        private int _line = 1;
        private bool _hidden;
        private bool _error;
        private string _errorMessage = "";
        private string _source = "";
        private ObservableCollection<Build> _builds = new ObservableCollection<Build>();
        private int _currentBuildIndex;
        private string _altText = "";

        public string Name { get => _name; set { _name = value; Notify(nameof(Name)); } }
        public string File { get => _file; set { _file = value; Notify(nameof(File)); } }
        public int Line { get => _line; set { _line = value; Notify(nameof(Line)); } }
        public bool Hidden { get => _hidden; set { _hidden = value; Notify(nameof(Hidden)); } }
        public bool Error { get => _error; set { _error = value; Notify(nameof(Error)); } }
        public string ErrorMessage { get => _errorMessage; set { _errorMessage = value; Notify(nameof(ErrorMessage)); } }
        public string Source { get => _source; set { _source = value; Notify(nameof(Source)); } }
        public string AltText { get => _altText; set { _altText = value; Notify(nameof(AltText)); } }
        public ObservableCollection<Build> Builds { get => _builds; set { _builds = value; Notify(nameof(Builds)); } }
        public int CurrentBuildIndex
        {
            get => _currentBuildIndex;
            set { if (_currentBuildIndex != value && value >= 0 && value < _builds.Count) { _currentBuildIndex = value; Notify(nameof(CurrentBuildIndex)); Notify(nameof(CurrentBuild)); } }
        }
        public Build CurrentBuild => (_builds.Count > 0 && _currentBuildIndex < _builds.Count) ? _builds[_currentBuildIndex] : null;
        public int Number { get; set; }
    }

    /// <summary>一个 deck 主题，对应 engine/manimble/themes 里的一个 py 文件。</summary>
    public class DeckTheme
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Background { get; set; }
        public string Text { get; set; }
        public string Muted { get; set; }
        public string Accent { get; set; }
        public string Font { get; set; }
    }
}
