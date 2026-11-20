using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using ManimbleWin.Helpers;
using ManimbleWin.Services;

namespace ManimbleWin.ViewModels
{
    /// <summary>搜索结果里的一项。缩略图是 fetch 到临时目录的 PNG，用 PathToImage 显示。</summary>
    public class BioCartoonItem : ObservableObject
    {
        private bool _isSelected;
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Source { get; set; } = "";       // 来源集合（scidraw / 内置示例 …）
        public string License { get; set; } = "";
        public string Author { get; set; } = "";
        public string Credit { get; set; } = "";       // 完整署名，插入时写进对象备注
        public string ThumbPath { get; set; } = "";    // 临时 PNG 绝对路径
        public bool Embedded { get; set; }             // true=内置离线图标
        public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

        public string LicenseBadge => string.IsNullOrEmpty(License) ? "未知许可" : License;
        public string SourceLabel => string.IsNullOrEmpty(Source) ? "未知来源" : Source;
    }

    /// <summary>
    /// 「BioCartoon 图标…」面板的视图模型。搜索/缩略图获取全部异步走引擎 RPC，
    /// 不在 UI 线程做 subprocess 或网络等待。真正的插入流水线由宿主 MainViewModel
    /// 通过 onInsert 回调执行（它掌握 deck/assets 与对象插入）。
    /// </summary>
    public class BioCartoonViewModel : ObservableObject
    {
        private readonly EngineIntegration _engine;
        private readonly Func<BioCartoonItem, Task<string>> _onInsert;
        private readonly string _thumbDir;

        public BioCartoonViewModel(EngineIntegration engine, Func<BioCartoonItem, Task<string>> onInsert)
        {
            _engine = engine;
            _onInsert = onInsert;
            _thumbDir = Path.Combine(EnvironmentService.TempDir, "biocartoon-thumbs");
            try { Directory.CreateDirectory(_thumbDir); } catch { }
            SearchCommand = new RelayCommand(_ => _ = SearchAsync());
            InsertCommand = new RelayCommand(_ => _ = InsertAsync(), _ => Selected != null && !IsBusy);
        }

        public ICommand SearchCommand { get; }
        public ICommand InsertCommand { get; }

        public ObservableCollection<BioCartoonItem> Items { get; } = new ObservableCollection<BioCartoonItem>();

        private string _query = "";
        public string Query { get => _query; set => SetProperty(ref _query, value); }

        private string _status = "输入关键词后点击搜索，例如 cell / DNA / 细胞。";
        public string Status { get => _status; private set => SetProperty(ref _status, value); }

        private string _warning = "";
        public string Warning { get => _warning; private set => SetProperty(ref _warning, value); }

        private bool _busy;
        public bool IsBusy { get => _busy; private set { if (SetProperty(ref _busy, value)) RaiseCommands(); } }

        private BioCartoonItem _selected;
        public BioCartoonItem Selected
        {
            get => _selected;
            set
            {
                if (_selected != null) _selected.IsSelected = false;
                if (SetProperty(ref _selected, value) && value != null) value.IsSelected = true;
                RaiseCommands();
            }
        }

        private void RaiseCommands()
        {
            (InsertCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        public bool CanInsert => _engine != null && _engine.IsConnected;

        private static string Str(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : "";

        private async Task SearchAsync()
        {
            if (IsBusy) return;
            if (!CanInsert) { Status = "请先「引擎 ▸ 连接 Manim 引擎」再搜索图标。"; return; }

            IsBusy = true;
            Selected = null;
            Items.Clear();
            Warning = "";
            Status = "正在搜索 BioCartoon 图标目录…（首次使用需下载 catalog，可能较慢）";
            try
            {
                var r = await _engine.BioSearch(Query?.Trim() ?? "", 24);
                bool ok = r.ValueKind != JsonValueKind.Object
                          || !r.TryGetProperty("ok", out var okEl) || okEl.ValueKind != JsonValueKind.False;
                if (!ok)
                {
                    Status = "搜索失败：" + (Str(r, "error") is string s && s.Length > 0 ? s : "未知错误");
                    return;
                }

                string source = Str(r, "source");
                if (source == "offline")
                    Warning = "BioCartoon CLI 当前不可用（" + (Str(r, "error") is string w && w.Length > 0 ? w : "网络或目录受限")
                              + "）。已切换到内置示例图标，仍可正常搜索与插入。";

                if (!r.TryGetProperty("items", out var arr) || arr.ValueKind != JsonValueKind.Array)
                {
                    Status = "没有解析到图标结果。";
                    return;
                }

                int n = 0;
                foreach (var it in arr.EnumerateArray())
                {
                    if (it.ValueKind != JsonValueKind.Object) continue;
                    var item = new BioCartoonItem
                    {
                        Id = Str(it, "id"),
                        Title = Str(it, "title"),
                        Source = Str(it, "source_name") is string sn && sn.Length > 0 ? sn : Str(it, "source"),
                        License = Str(it, "license"),
                        Author = Str(it, "author"),
                        Credit = Str(it, "credit"),
                        Embedded = it.TryGetProperty("embedded", out var eb) && eb.ValueKind == JsonValueKind.True,
                    };
                    if (string.IsNullOrEmpty(item.Id)) continue;
                    Items.Add(item);
                    n++;
                    // 缩略图逐个异步拉取，UI 保持响应
                    var thumb = await ThumbnailAsync(item);
                    if (thumb != null) item.ThumbPath = thumb;
                }
                Status = n > 0 ? $"找到 {n} 个图标。点击选中后再点「插入选中图标」。" : "没有匹配的图标，换个关键词试试。";
            }
            catch (Exception ex)
            {
                Status = "搜索出错：" + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task<string> ThumbnailAsync(BioCartoonItem item)
        {
            try
            {
                string safe = string.Concat(item.Id.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_'));
                string png = Path.Combine(_thumbDir, safe + ".png");
                var r = await _engine.BioFetch(item.Id, png, "png");
                bool ok = r.ValueKind != JsonValueKind.Object
                          || !r.TryGetProperty("ok", out var okEl) || okEl.ValueKind != JsonValueKind.False;
                return ok && File.Exists(png) ? png : null;
            }
            catch { return null; }
        }

        private async Task InsertAsync()
        {
            if (Selected == null) return;
            IsBusy = true;
            try
            {
                string msg = await _onInsert(Selected);
                Status = msg ?? "已插入图标";
            }
            catch (Exception ex)
            {
                Status = "插入失败：" + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}
