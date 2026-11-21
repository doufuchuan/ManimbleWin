using System;
using System.Windows.Threading;
using ManimbleWin.Helpers;
using ManimbleWin.Models;

namespace ManimbleWin.ViewModels
{
    /// <summary>
    /// 一条批注的视图模型：同时服务画布上的批注块与面板列表里的行。
    /// <para>位置一律存比例（0..1），画布上的像素由 <see cref="Left"/>/<see cref="Top"/>/<see cref="BoxWidth"/> 换算——
    /// 这样画布 <c>Viewbox</c> 怎么缩放都不会漂移（架构 §4.2）。</para>
    /// <para>文字有两份：<see cref="Text"/> 是已落盘的内容，<see cref="DraftText"/> 是编辑中的草稿；
    /// 只有 <see cref="FeedbackViewModel.EndEdit"/> 才会把草稿提交给 <c>FeedbackStore</c>。</para>
    /// </summary>
    public sealed class FeedbackNoteViewModel : ObservableObject
    {
        /// <summary>⚠ 必须与 <c>CanvasView.xaml</c> 里 <c>SlideSurface</c> 的 Width/Height 一致（两处改一处就得改另一处）。</summary>
        public const double SlideWidth = 1280;

        /// <summary>⚠ 同上，必须与 <c>SlideSurface</c> 的 Height 一致。</summary>
        public const double SlideHeight = 720;

        private readonly FeedbackViewModel _owner;
        private FeedbackNote _model;
        private string _draftText = "";
        private bool _isEditing;
        private bool _isFlashing;
        private bool _isSelected;
        private DispatcherTimer _flashTimer;

        public FeedbackNoteViewModel(FeedbackViewModel owner, FeedbackNote model)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            if (model == null) throw new ArgumentNullException(nameof(model));

            // ⚠ 必须持有一份<b>拷贝</b>：Store 里的 FeedbackNote 是它的唯一数据源，
            // 如果 VM 直接改同一个实例（拖动 MoveTo 就是在改 X/Y），Store 的 UpdatePosition 会发现
            // "新旧相等"从而拒绝写盘 —— 拖动就永远存不下来。
            _model = model.Clone();
            _draftText = _model.Text ?? "";
        }

        public string Id => _model.Id;
        public string Scene => _model.Scene;
        public int Build => _model.Build;
        public DateTime CreatedUtc => _model.CreatedUtc;

        /// <summary>比例坐标（0..0.96），落盘用。</summary>
        public double X => _model.X;
        public double Y => _model.Y;

        /// <summary>画布逻辑坐标（1280×720）。</summary>
        public double Left => Math.Round(_model.X * SlideWidth, 3);
        public double Top => Math.Round(_model.Y * SlideHeight, 3);

        /// <summary>块宽：默认 <c>0.32 × 1280 = 409.6</c>；下限 60 是防止窄到放不下一个汉字。</summary>
        public double BoxWidth => Math.Max(60, Math.Round(_model.W * SlideWidth, 3));

        /// <summary>已落盘文本。</summary>
        public string Text => _model.Text ?? "";

        /// <summary>列表里显示的文字；外部文件里的空文本也要能看见，所以给一个占位。</summary>
        public string DisplayText => string.IsNullOrWhiteSpace(Text) ? "(空批注)" : Text;

        /// <summary>"构建 1"（文件里是 0 基，界面上是 1 基）。</summary>
        public string BuildLabel => "构建 " + (_model.Build + 1);

        /// <summary>只读转发：批注块里的 TextBox 直接绑它，UI 不做额外判断。</summary>
        public bool IsReadOnly => _owner.IsReadOnly;

        /// <summary>编辑中的草稿（TwoWay 绑定，只改内存）。</summary>
        public string DraftText
        {
            get => _draftText;
            set => SetProperty(ref _draftText, value ?? "");
        }

        public bool IsEditing
        {
            get => _isEditing;
            internal set
            {
                if (SetProperty(ref _isEditing, value))
                    RaisePropertyChanged(nameof(IsReadOnly));
            }
        }

        /// <summary>跳转后的闪烁（XAML 里 4px → 2px，0.6 秒）。</summary>
        public bool IsFlashing
        {
            get => _isFlashing;
            private set => SetProperty(ref _isFlashing, value);
        }

        /// <summary>面板列表里的选中行淡底。</summary>
        public bool IsSelected
        {
            get => _isSelected;
            internal set => SetProperty(ref _isSelected, value);
        }

        /// <summary>进入编辑：只有 <see cref="FeedbackViewModel.BeginEdit"/> 会调它（要维护"同时最多一条在编辑"）。</summary>
        internal void BeginEditCore()
        {
            DraftText = Text;
            IsEditing = true;
        }

        /// <summary>结束编辑：交给 owner 统一提交（空文本 → 删除该条）。</summary>
        public void EndEdit() => _owner.EndEdit();

        /// <summary>
        /// 拖动：只改内存里的比例坐标，松手才由 owner 落盘（PRD FB-P1-02：拖动中不写文件）。
        /// clamp 到 0..0.96，与新建时同一公式。
        /// </summary>
        public void MoveTo(double x, double y)
        {
            double nx = Math.Min(Math.Max(0, x), 0.96);
            double ny = Math.Min(Math.Max(0, y), 0.96);
            if (Math.Abs(_model.X - nx) < 1e-9 && Math.Abs(_model.Y - ny) < 1e-9) return;

            _model.X = Math.Round(nx, 6);
            _model.Y = Math.Round(ny, 6);
            RaisePropertyChanged(nameof(X));
            RaisePropertyChanged(nameof(Y));
            RaisePropertyChanged(nameof(Left));
            RaisePropertyChanged(nameof(Top));
        }

        /// <summary>跳转后闪一下。用 600ms 的定时器复位，不需要 code-behind 去动可视树。</summary>
        public void Flash()
        {
            if (_flashTimer == null)
            {
                _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                _flashTimer.Tick += (s, e) =>
                {
                    _flashTimer.Stop();
                    IsFlashing = false;
                };
            }
            _flashTimer.Stop();
            IsFlashing = false;          // 先归零再置位：连续点同一条时触发器能重新进入
            IsFlashing = true;
            _flashTimer.Start();
        }

        /// <summary>用 Store 里的最新模型刷新（<b>编辑中不刷新</b>，否则用户打字打到一半会被文件内容顶掉）。</summary>
        internal void RefreshFromModel(FeedbackNote model)
        {
            if (model == null) return;
            _model = model.Clone();
            RaisePropertyChanged(nameof(Scene));
            RaisePropertyChanged(nameof(Build));
            RaisePropertyChanged(nameof(CreatedUtc));
            RaisePropertyChanged(nameof(X));
            RaisePropertyChanged(nameof(Y));
            RaisePropertyChanged(nameof(Left));
            RaisePropertyChanged(nameof(Top));
            RaisePropertyChanged(nameof(BoxWidth));
            RaisePropertyChanged(nameof(Text));
            RaisePropertyChanged(nameof(DisplayText));
            RaisePropertyChanged(nameof(BuildLabel));
            if (!IsEditing) DraftText = Text;
        }

        /// <summary>只读闸门变化（重新读取坏文件后）时刷新绑定。</summary>
        internal void RefreshReadOnly() => RaisePropertyChanged(nameof(IsReadOnly));
    }
}
