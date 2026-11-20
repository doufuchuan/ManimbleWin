using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ManimbleWin.Helpers;
using ManimbleWin.Models;
using ManimbleWin.Services;

namespace ManimbleWin.ViewModels
{
    /// <summary>选择器里的一行主题：真实色板 + 是否为当前主题 + 是否跨明暗。</summary>
    public class ThemeOption : ObservableObject
    {
        public ThemeOption(DeckTheme theme) { Theme = theme; }

        public DeckTheme Theme { get; }
        public string Id => Theme.Id;
        public string Name => Theme.Name;
        public string Background => Theme.Background;
        public string Text => Theme.Text;
        public string Muted => Theme.Muted;
        public string Accent => Theme.Accent;
        public string Font => Theme.Font;

        /// <summary>背景色本身是浅色还是深色（只作角标提示；跨明暗以引擎的 crosses 为准）。</summary>
        public bool IsLight => DeckThemeService.IsLight(Theme.Background);
        public string ToneText => IsLight ? "浅色" : "深色";

        private bool _isCurrent;
        /// <summary>这套主题就是 deck 现在用的那套（引擎 op=get 返回的 THEME 显示名）。</summary>
        public bool IsCurrent { get => _isCurrent; set => SetProperty(ref _isCurrent, value); }

        private bool _isSlideTheme;
        /// <summary>当前页自己带主题且就是这一套。</summary>
        public bool IsSlideTheme { get => _isSlideTheme; set => SetProperty(ref _isSlideTheme, value); }

        private bool? _crosses;
        /// <summary>切到这套是否跨明暗（引擎 op=get 的 crosses，null 表示还没查）。</summary>
        public bool? Crosses
        {
            get => _crosses;
            set { if (SetProperty(ref _crosses, value)) OnPropertyChanged(nameof(CrossesText)); }
        }

        public string CrossesText => Crosses == true ? "跨明暗" : "";
    }

    /// <summary>
    /// 「格式 ▸ 主题…」的确认面板：列出 8 套主题的真实色板，先用引擎 <c>theme op=preview</c> 把当前页
    /// 按该主题渲染成缩略图，确认后再应用（整份 deck 或仅当前页），应用是引擎侧一步撤销。
    ///
    /// 为什么不直接应用：改主题会重写 common.py / manim.cfg 并重渲染所有页，误点代价很大；
    /// macOS 版（DeckThemes.swift 的 ThemeSheet）也是「预览 → Apply」两步。
    /// </summary>
    public class ThemePickerViewModel : ObservableObject
    {
        private readonly MainViewModel _owner;
        private readonly EngineIntegration _engine;
        private readonly DispatcherTimer _debounce;
        private int _generation;

        public ObservableCollection<ThemeOption> Options { get; } = new ObservableCollection<ThemeOption>();

        public ThemePickerViewModel(MainViewModel owner, EngineIntegration engine, DeckTheme preset)
        {
            _owner = owner;
            _engine = engine;

            foreach (DeckTheme t in owner.Themes) Options.Add(new ThemeOption(t));

            _selected = Options.FirstOrDefault(o => preset != null && o.Id == preset.Id) ?? Options.FirstOrDefault();

            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            _debounce.Tick += (s, e) => { _debounce.Stop(); _ = LoadPreviewAsync(); };

            ApplyCommand = new RelayCommand(_ => _ = ApplyAsync(), _ => CanApply);
            CloseCommand = new RelayCommand(p => Close(p is bool b && b));
        }

        #region 结果

        /// <summary>是否真的应用成功（对话框用它决定 DialogResult，并让调用方刷新画布）。</summary>
        public bool Applied { get; private set; }
        public DeckTheme AppliedTheme { get; private set; }
        public bool AppliedToSlideOnly { get; private set; }
        public string ResultMessage { get; private set; } = "";
        public string Error { get; private set; } = "";

        public event Action<bool> CloseRequested;

        private void Close(bool applied) => CloseRequested?.Invoke(applied);

        #endregion

        #region 可绑定状态

        private ThemeOption _selected;
        public ThemeOption SelectedOption
        {
            get => _selected;
            set
            {
                if (!SetProperty(ref _selected, value)) return;
                OnPropertyChanged(nameof(ShowFlip));
                OnPropertyChanged(nameof(DetailText));
                OnPropertyChanged(nameof(CanApply));
                _ = RefreshCrossesAndPreviewAsync();
            }
        }

        private bool _slideOnly;
        /// <summary>只改当前页（引擎写一行常量覆盖 + 该页背景），不动 deck 的其它页。</summary>
        public bool SlideOnly
        {
            get => _slideOnly;
            set
            {
                if (!SetProperty(ref _slideOnly, value)) return;
                OnPropertyChanged(nameof(ShowFlip));
                OnPropertyChanged(nameof(DetailText));
                _debounce.Stop();
                _debounce.Start();
            }
        }

        private bool _flip = true;
        /// <summary>跨明暗时顺便把源码里的黑白互换（否则白字会落在浅底上不可读）。</summary>
        public bool Flip
        {
            get => _flip;
            set
            {
                if (!SetProperty(ref _flip, value)) return;
                _debounce.Stop();
                _debounce.Start();
            }
        }

        /// <summary>只有「整份 deck + 目标主题确实跨明暗」时才让用户选黑白互换（引擎其余情况会忽略 flip）。</summary>
        public bool ShowFlip => !SlideOnly && (SelectedOption?.Crosses ?? false);

        private bool _busy;
        public bool IsBusy
        {
            get => _busy;
            private set
            {
                if (!SetProperty(ref _busy, value)) return;
                OnPropertyChanged(nameof(CanApply));
                ApplyCommand?.RaiseCanExecuteChanged();
            }
        }

        private string _status = "";
        public string Status { get => _status; private set => SetProperty(ref _status, value); }

        private ImageSource _previewImage;
        public ImageSource PreviewImage { get => _previewImage; private set => SetProperty(ref _previewImage, value); }

        /// <summary>引擎写出的预览 PNG 路径（测试与排错用）。</summary>
        public string PreviewPath { get; private set; } = "";

        private string _deckThemeName = "";
        public string DeckThemeName
        {
            get => _deckThemeName;
            private set
            {
                if (SetProperty(ref _deckThemeName, value)) OnPropertyChanged(nameof(DeckThemeDisplay));
            }
        }

        /// <summary>deck 还没写过 THEME 常量时（例如自建 deck）显示「未设置」，不要显示空白。</summary>
        public string DeckThemeDisplay => string.IsNullOrEmpty(DeckThemeName) ? "未设置" : DeckThemeName;

        private string _slideThemeName = "";
        public string SlideThemeName
        {
            get => _slideThemeName;
            private set
            {
                if (SetProperty(ref _slideThemeName, value)) OnPropertyChanged(nameof(SlideThemeDisplay));
            }
        }

        public string SlideThemeDisplay => string.IsNullOrEmpty(SlideThemeName) ? "跟随 deck" : SlideThemeName;

        public string SceneName => _owner?.CurrentSlide?.Name ?? "";

        public bool Connected => _engine != null && _engine.IsConnected;

        public bool CanApply => !IsBusy && SelectedOption != null && Connected;

        public string DetailText
        {
            get
            {
                if (SlideOnly)
                    return "这一页会单独套上主题的背景，页内代码使用主题的 FONT / TEXT_COLOR / MUTED_COLOR / ACCENT；"
                         + "与其他页共用的 helper 仍用 deck 的。在这里选回 deck 当前的主题即可撤销。撤销（Ctrl+Z）可回退。";
                return "deck 的 FONT / TEXT_COLOR / MUTED_COLOR / ACCENT 与 manim.cfg 的背景都会改成主题的值，"
                     + "自带主题的页会回到 deck 的主题；每一页都会重新渲染。撤销（Ctrl+Z）一步回退。";
            }
        }

        public RelayCommand ApplyCommand { get; }
        public RelayCommand CloseCommand { get; }

        #endregion

        #region 逻辑

        /// <summary>打开面板时先问引擎：deck 现在是什么主题、这一页有没有自己的主题。</summary>
        public async Task InitializeAsync()
        {
            if (!Connected)
            {
                Status = "引擎未连接：主题需要引擎改写源码，请先「引擎 ▸ 连接 Manim 引擎」。";
                return;
            }
            try
            {
                JsonElement r = await _engine.ThemeGet(null, SceneName);
                DeckThemeName = Str(r, "theme") ?? "";
                SlideThemeName = Str(r, "slide_theme") ?? "";
                foreach (ThemeOption o in Options)
                {
                    o.IsCurrent = !string.IsNullOrEmpty(DeckThemeName) && o.Name == DeckThemeName;
                    o.IsSlideTheme = !string.IsNullOrEmpty(SlideThemeName) && o.Name == SlideThemeName;
                }
            }
            catch (Exception ex)
            {
                Status = "读取当前主题失败：" + ex.Message;
            }
            await RefreshCrossesAndPreviewAsync();
        }

        /// <summary>先补 crosses（决定要不要显示黑白互换），再渲染预览。</summary>
        private async Task RefreshCrossesAndPreviewAsync()
        {
            ThemeOption o = SelectedOption;
            if (o != null && o.Crosses == null && Connected)
            {
                try
                {
                    JsonElement r = await _engine.ThemeGet(o.Id, SceneName);
                    o.Crosses = Bool(r, "crosses");
                    OnPropertyChanged(nameof(ShowFlip));
                }
                catch (Exception)
                {
                    o.Crosses = false;   // 查不到就按「不跨明暗」处理，不影响主流程
                }
            }
            await LoadPreviewAsync();
        }

        /// <summary>把当前页按选中的主题渲染成缩略图。引擎不动 deck，可以随便点。</summary>
        private async Task LoadPreviewAsync()
        {
            ThemeOption o = SelectedOption;
            if (o == null) return;
            if (!Connected)
            {
                Status = "引擎未连接，无法预览。";
                return;
            }

            int gen = ++_generation;
            IsBusy = true;
            Status = "正在渲染预览…";
            try
            {
                JsonElement r = await _engine.ThemePreview(o.Id, SceneName, SlideOnly, ShowFlip && Flip);
                if (gen != _generation) return;      // 用户已经换了别的主题，这次结果作废

                if (!IsOk(r))
                {
                    PreviewImage = null;
                    PreviewPath = "";
                    Status = "预览失败：" + (Str(r, "error") ?? "引擎没有返回 PNG");
                    return;
                }
                string png = Str(r, "png") ?? "";
                PreviewPath = png;
                PreviewImage = LoadImage(png);
                Status = PreviewImage == null ? "预览图读取失败：" + png : "";
            }
            catch (Exception ex)
            {
                if (gen == _generation)
                {
                    Status = "预览失败：" + ex.Message;
                    PreviewImage = null;
                }
            }
            finally
            {
                if (gen == _generation) IsBusy = false;
            }
        }

        private async Task ApplyAsync()
        {
            ThemeOption o = SelectedOption;
            if (o == null || !CanApply) return;

            IsBusy = true;
            Status = SlideOnly ? "正在把主题应用到这一页…" : "正在应用主题，每一页都会重新渲染…";
            Error = "";
            try
            {
                JsonElement r = await _engine.ThemeApply(o.Id, SceneName, SlideOnly, ShowFlip && Flip);
                if (!IsOk(r))
                {
                    // 引擎的 error 原样显示，不吞成"操作失败"
                    Error = Str(r, "error") ?? "主题应用失败";
                    Status = Error;
                    return;
                }
                string message = Str(r, "message") ?? "";
                Applied = true;
                AppliedTheme = o.Theme;
                AppliedToSlideOnly = SlideOnly;
                ResultMessage = message == "no change"
                    ? (SlideOnly ? "这一页已经是该主题" : "deck 已经是该主题")
                    : message;
                Close(true);
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                Status = "应用失败：" + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>读 PNG 成 BitmapImage。用 FileStream + OnLoad，避免锁文件（引擎会用同一路径重渲染）。</summary>
        private static ImageSource LoadImage(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                var bmp = new BitmapImage();
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    // 不要设 BitmapCreateOptions.IgnoreImageCache：它拿 UriSource 当缓存键，
                    // 配 StreamSource 会在 EndInit 抛 ArgumentNullException（Parameter 'key'），
                    // 预览图就永远是空的。这里每次都是新流 + OnLoad，本来也绕过了缓存。
                    bmp.StreamSource = fs;
                    bmp.EndInit();
                }
                bmp.Freeze();
                return bmp;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool IsOk(JsonElement r)
            => r.ValueKind == JsonValueKind.Object
               && (!r.TryGetProperty("ok", out JsonElement ok) || ok.ValueKind != JsonValueKind.False);

        private static string Str(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;

        private static bool Bool(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;

        #endregion
    }
}
