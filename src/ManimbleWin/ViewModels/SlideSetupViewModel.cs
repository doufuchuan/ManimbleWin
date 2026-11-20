using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using ManimbleWin.Helpers;
using ManimbleWin.Services;

namespace ManimbleWin.ViewModels
{
    /// <summary>
    /// 「幻灯片 ▸ 背景与页码…」的设置面板。所有写入都走引擎——这两项都是往 deck 源码里插语句，
    /// 本地改画布属性只会在下次渲染时被覆盖。引擎的每次写入都是一步撤销。
    /// </summary>
    public class SlideSetupViewModel : ObservableObject
    {
        private readonly MainViewModel _owner;
        private readonly EngineIntegration _engine;

        public SlideSetupViewModel(MainViewModel owner, EngineIntegration engine)
        {
            _owner = owner;
            _engine = engine;
            ApplyBackgroundCommand = new RelayCommand(_ => _ = ApplyBackgroundAsync(), _ => CanWrite);
            ResetBackgroundCommand = new RelayCommand(_ => _ = ResetBackgroundAsync(), _ => CanWrite);
            ApplyPagesCommand = new RelayCommand(_ => _ = ApplyPagesAsync(), _ => CanWrite);
            RemovePagesCommand = new RelayCommand(_ => _ = RemovePagesAsync(), _ => CanWrite);
            SpreadPagesCommand = new RelayCommand(_ => _ = SpreadPagesAsync(), _ => CanWrite);
            PickImageCommand = new RelayCommand(_ => PickImage());
        }

        public bool CanWrite => _engine != null && _engine.IsConnected;
        public string SceneName => _owner?.CurrentSlide?.Name ?? "";

        public RelayCommand ApplyBackgroundCommand { get; }
        public RelayCommand ResetBackgroundCommand { get; }
        public RelayCommand ApplyPagesCommand { get; }
        public RelayCommand RemovePagesCommand { get; }
        public RelayCommand SpreadPagesCommand { get; }
        public RelayCommand PickImageCommand { get; }

        private string _status = "";
        public string Status { get => _status; private set => SetProperty(ref _status, value); }

        private bool _busy;
        public bool IsBusy { get => _busy; private set { if (SetProperty(ref _busy, value)) { OnPropertyChanged(nameof(CanWrite)); Raise(); } } }
        private void Raise()
        {
            ApplyBackgroundCommand?.RaiseCanExecuteChanged();
            ResetBackgroundCommand?.RaiseCanExecuteChanged();
            ApplyPagesCommand?.RaiseCanExecuteChanged();
            RemovePagesCommand?.RaiseCanExecuteChanged();
            SpreadPagesCommand?.RaiseCanExecuteChanged();
        }

        #region 背景

        private string _bgMode = "color";
        /// <summary>color / image / none（none 表示跟着 deck 默认色）。</summary>
        public string BgMode
        {
            get => _bgMode;
            set { if (SetProperty(ref _bgMode, value)) OnPropertyChanged(nameof(ShowColor)); }
        }
        public bool ShowColor => BgMode != "image";

        private string _bgColor = "#000000";
        public string BgColor { get => _bgColor; set => SetProperty(ref _bgColor, value); }

        private double _transparency;
        public double Transparency { get => _transparency; set => SetProperty(ref _transparency, value); }

        private string _bgImage = "";
        /// <summary>相对 deck 目录的图片路径（引擎要求这个格式，绝对路径会被拒绝）。</summary>
        public string BgImage { get => _bgImage; private set { if (SetProperty(ref _bgImage, value)) OnPropertyChanged(nameof(BgImageLabel)); } }
        public string BgImageLabel => string.IsNullOrEmpty(BgImage) ? "未选择" : BgImage;

        private bool _bgAll;
        public bool BgAll { get => _bgAll; set => SetProperty(ref _bgAll, value); }

        private string _bgCurrent = "默认";
        public string BgCurrent { get => _bgCurrent; private set => SetProperty(ref _bgCurrent, value); }

        #endregion

        #region 页码

        private string _pagePos = "DR";
        public string PagePos { get => _pagePos; set => SetProperty(ref _pagePos, value); }
        public IReadOnlyList<string> PagePositions => new[] { "DR", "DOWN", "DL" };

        private int _pageStart = 1;
        public int PageStart { get => _pageStart; set => SetProperty(ref _pageStart, value); }

        private bool _pageSkip = true;
        public bool PageSkip { get => _pageSkip; set => SetProperty(ref _pageSkip, value); }

        private bool _pageTotal = true;
        public bool PageTotal { get => _pageTotal; set => SetProperty(ref _pageTotal, value); }

        private int _pageSize = 24;
        public int PageSize { get => _pageSize; set => SetProperty(ref _pageSize, value); }

        private bool _pageSub;
        public bool PageSub { get => _pageSub; set => SetProperty(ref _pageSub, value); }

        private string _pageState = "未读取";
        public string PageState { get => _pageState; private set => SetProperty(ref _pageState, value); }

        #endregion

        /// <summary>打开面板时把当前页背景和全局页码选项读回来。</summary>
        public async Task InitializeAsync()
        {
            if (!CanWrite)
            {
                Status = "背景和页码要改 deck 源码：请先「引擎 ▸ 连接 Manim 引擎」。";
                return;
            }
            try
            {
                var bg = await _engine.BackgroundGet(SceneName);
                string mode = Str(bg, "mode") ?? "none";
                string color = Str(bg, "color");
                string image = Str(bg, "image");
                double t = Num(bg, "transparency");
                string def = Str(bg, "default");
                BgCurrent = mode == "none" ? "跟随 deck 默认" + (string.IsNullOrEmpty(def) ? "" : "（" + def + "）")
                          : mode == "image" ? "图片" : "纯色 " + (color ?? "");
                BgMode = mode == "image" ? "image" : "color";
                if (!string.IsNullOrEmpty(color)) BgColor = color;
                if (!string.IsNullOrEmpty(image)) BgImage = ToRelative(image);
                Transparency = t;

                var pg = await _engine.PageNumbersGet();
                bool on = Bool(pg, "on");
                PageState = on ? "已启用" : "未启用";
                if (on)
                {
                    string pos = Str(pg, "pos");
                    if (!string.IsNullOrEmpty(pos)) PagePos = pos.ToUpperInvariant();
                    int start = (int)Num(pg, "start"); if (start > 0) PageStart = start;
                    int size = (int)Num(pg, "size"); if (size > 0) PageSize = size;
                    PageSkip = Bool(pg, "skip"); PageTotal = Bool(pg, "total"); PageSub = Bool(pg, "sub");
                }
                Status = "";
            }
            catch (Exception ex) { Status = "读取失败：" + ex.Message; }
        }

        private async Task ApplyBackgroundAsync()
        {
            if (!CanWrite) return;
            IsBusy = true;
            Status = "正在写入背景…";
            try
            {
                var r = await _engine.BackgroundSet(SceneName, BgMode, BgColor, Transparency, BgAll, BgImage);
                if (IsFalse(r)) { Status = Err(r, "背景设置失败"); return; }
                await RefreshDeckAsync(BgAll);
                Status = BgAll ? "已为所有页设置背景" : "已设置本页背景";
            }
            catch (Exception ex) { Status = "设置失败：" + ex.Message; }
            finally { IsBusy = false; }
        }

        private async Task ResetBackgroundAsync()
        {
            if (!CanWrite) return;
            IsBusy = true;
            Status = "正在清除背景…";
            try
            {
                var r = await _engine.BackgroundReset(SceneName, BgAll);
                if (IsFalse(r)) { Status = Err(r, "清除失败"); return; }
                await RefreshDeckAsync(BgAll);
                Status = BgAll ? "已清除所有页的背景" : "已清除本页背景";
                BgCurrent = "跟随 deck 默认";
            }
            catch (Exception ex) { Status = "清除失败：" + ex.Message; }
            finally { IsBusy = false; }
        }

        private async Task ApplyPagesAsync()
        {
            if (!CanWrite) return;
            IsBusy = true;
            Status = "正在写入页码…";
            try
            {
                var r = await _engine.PageNumbersSet(PagePos, PageStart, PageSkip, PageTotal, PageSize, PageSub);
                if (IsFalse(r)) { Status = Err(r, "页码设置失败"); return; }
                await RefreshDeckAsync(true);
                PageState = "已启用";
                Status = "已写入所有页的页码";
            }
            catch (Exception ex) { Status = "设置失败：" + ex.Message; }
            finally { IsBusy = false; }
        }

        private async Task RemovePagesAsync()
        {
            if (!CanWrite) return;
            IsBusy = true;
            Status = "正在移除页码…";
            try
            {
                var r = await _engine.PageNumbers("remove");
                if (IsFalse(r)) { Status = Err(r, "移除失败"); return; }
                await RefreshDeckAsync(true);
                PageState = "未启用";
                Status = "已移除页码";
            }
            catch (Exception ex) { Status = "移除失败：" + ex.Message; }
            finally { IsBusy = false; }
        }

        private async Task SpreadPagesAsync()
        {
            if (!CanWrite) return;
            IsBusy = true;
            Status = "正在把本页页码样式扩散到其它页…";
            try
            {
                var r = await _engine.PageNumbers("spread", SceneName);
                if (IsFalse(r)) { Status = Err(r, "扩散失败"); return; }
                await RefreshDeckAsync(true);
                Status = "已把本页的页码样式应用到所有带页码的页";
            }
            catch (Exception ex) { Status = "扩散失败：" + ex.Message; }
            finally { IsBusy = false; }
        }

        private void PickImage()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择背景图片",
                Filter = "图片|*.png;*.jpg;*.jpeg;*.svg;*.webp|所有文件|*.*"
            };
            if (dlg.ShowDialog() != true) return;
            string deck = _engine.DeckPath;
            BgImage = ToRelative(dlg.FileName, deck);
            Status = "已选择 " + dlg.FileName;
        }

        /// <summary>引擎要求图片路径相对 deck 目录；选到 deck 外的文件时提示用户复制过去。</summary>
        private string ToRelative(string path, string deck = null)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try
            {
                string root = deck ?? _engine.DeckPath;
                if (string.IsNullOrEmpty(root)) return path;
                string rel = Path.GetRelativePath(root, path);
                return rel.StartsWith("..") ? path : rel.Replace('\\', '/');
            }
            catch { return path; }
        }

        /// <summary>只改本页时刷一页；作用于全部页或全局页码时每页都要重取渲染结果。</summary>
        private async Task RefreshDeckAsync(bool all)
        {
            try
            {
                if (all) await _owner.RefreshAllEngineSlides();
                else await _owner.RefreshAfterEngineEdit(null);
            }
            catch (Exception ex)
            {
                Status = "已写入，但画布刷新失败：" + ex.Message;
            }
        }

        private static bool IsFalse(JsonElement r)
            => r.ValueKind == JsonValueKind.Object && r.TryGetProperty("ok", out var v) && v.ValueKind == JsonValueKind.False;

        private static string Err(JsonElement r, string fallback)
        {
            string e = Str(r, "error");
            return string.IsNullOrEmpty(e) ? fallback : e;
        }

        private static string Str(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;

        private static double Num(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) &&
               (v.ValueKind == JsonValueKind.Number || v.ValueKind == JsonValueKind.String) &&
               double.TryParse(v.ToString(), System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : 0;

        private static bool Bool(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) &&
               (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String &&
                string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase)));
    }
}
