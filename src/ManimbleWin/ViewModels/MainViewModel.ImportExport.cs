using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using ManimbleWin.Helpers;
using ManimbleWin.Models;
using ManimbleWin.Services;

namespace ManimbleWin.ViewModels
{
    /// <summary>
    /// MainViewModel 的导入导出与 LaTeX 部分（T04）。
    ///
    /// <para><b>事件与响应的区别</b>（设计 §8）：<c>import</c>/<c>export</c>/<c>export_movie</c> 的最终结果走
    /// <b>事件</b>（RPC 只回 ack），<c>latex_install</c> 的最终结果走 <b>RPC response</b>（进度才走事件）。
    /// 这里统一用 EngineIntegration 的封装，不再自己拼事件。</para>
    ///
    /// <para><b>范围</b>：PPTX + Beamer(.tex) 导入；PPTX / MP4 导出为 P0，Beamer(.tex) / Python 为 P1；
    /// Keynote 导出与 .key/.ppt 导入在 Windows 上依赖 macOS Keynote，只给引导不给入口；
    /// PDF 不是引擎的独立 kind，不作为导出目标。</para>
    /// </summary>
    public partial class MainViewModel
    {
        #region 状态与命令

        private bool _importing;
        private bool _exporting;
        private LatexNotice _latexNotice;
        private bool _latexDontAskAgain;

        /// <summary>
        /// 导出选项对话框（T05 的 ExportDialog 注册）。返回 null 表示用户取消。
        /// 未注册时直接问保存路径，选项用 P0 默认值（builds=true、editable=true、720p/2s/30fps）。
        /// </summary>
        public Func<ExportKind, ExportRequest, Task<ExportRequest>> ExportDialogHook { get; set; }

        /// <summary>
        /// 纯信息提示（Keynote 引导、PDF 说明、宏包安装引导）的落地方式。参数：(标题, 正文)。
        /// T05 可以换成应用内提示条；自动化测试注册它来断言「有没有给出引导」且不弹模态框。
        /// </summary>
        public Action<string, string> InfoHook { get; set; }

        private ICommand _cancelBusyCommand;
        /// <summary>busy pill 上的取消按钮：停下正在跑的导入/导出/编辑。</summary>
        public ICommand CancelBusyCommand => _cancelBusyCommand ??= new RelayCommand(_ => _ = CancelBusyAsync());

        private ICommand _importCommand;
        /// <summary>参数 "pptx" / "beamer"（T05 菜单用）。</summary>
        public ICommand ImportCommand => _importCommand ??= new RelayCommand(p =>
        {
            string kind = p?.ToString() ?? "pptx";
            _ = ImportAsync(kind.Equals("beamer", StringComparison.OrdinalIgnoreCase) ? ImportKind.Beamer : ImportKind.Pptx);
        });

        private ICommand _exportCommand;
        /// <summary>参数 "pptx" / "movie" / "beamer" / "python" / "keynote"。</summary>
        public ICommand ExportCommand => _exportCommand ??= new RelayCommand(p => _ = ExportAsync(ParseExportKind(p?.ToString())));

        private ICommand _latexInstallCommand;
        public ICommand LatexInstallCommand => _latexInstallCommand ??= new RelayCommand(_ => _ = InstallLatexAsync());

        private ICommand _latexRetryCommand;
        public ICommand LatexRetryCommand => _latexRetryCommand ??= new RelayCommand(_ => _ = CheckLatexNoticeAsync());

        private ICommand _latexDismissCommand;
        /// <summary>参数 true/"again" 表示「不再询问」（XAML 里 CommandParameter="True" 传进来是字符串，两种都认）。</summary>
        public ICommand LatexDismissCommand => _latexDismissCommand ??= new RelayCommand(p => DismissLatexNotice(IsTrueParam(p)));

        private static ExportKind ParseExportKind(string text)
        {
            switch ((text ?? "").Trim().ToLowerInvariant())
            {
                case "movie":
                case "mp4": return ExportKind.Movie;
                case "beamer":
                case "tex": return ExportKind.Beamer;
                case "python":
                case "py": return ExportKind.Python;
                case "keynote":
                case "key": return ExportKind.KeynoteDisabled;
                default: return ExportKind.Pptx;
            }
        }

        #endregion

        #region 导入（P0-10 / P0-11）

        /// <summary>文件 ▸ 导入 ▸ PowerPoint / Beamer。.key/.ppt 只给转换引导。</summary>
        public async Task ImportAsync(ImportKind kind)
        {
            if (_importing) { ShowToast("正在导入中，请稍候"); return; }

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = kind == ImportKind.Pptx ? "导入 PowerPoint 演示文稿" : "导入 Beamer 源文件",
                CheckFileExists = true,
            };
            if (kind == ImportKind.Pptx)
                dlg.Filter = "PowerPoint 演示文稿|*.pptx|Keynote 演示文稿（需先转 .pptx）|*.key|旧版 PowerPoint（需先转 .pptx）|*.ppt";
            else
                dlg.Filter = "Beamer / LaTeX 源文件|*.tex;*.ltx|所有文件|*.*";

            if (dlg.ShowDialog() != true) return;
            string ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
            if (ext == ".key" || ext == ".ppt")
            {
                ShowKeynoteImportHint(ext);
                return;
            }

            string parent = PickFolder("导入到：选择保存位置");
            if (string.IsNullOrEmpty(parent)) return;

            var req = new ImportRequest
            {
                SourcePath = dlg.FileName,
                DestParent = parent,
                DeckName = Path.GetFileNameWithoutExtension(dlg.FileName),
                Kind = ext == ".tex" || ext == ".ltx" ? ImportKind.Beamer : kind,
            };
            await ImportWithRequestAsync(req);
        }

        /// <summary>
        /// 导入核心（无 UI，可被对话框/测试直接调用）。
        /// done 之后立刻打开新 deck（此时才第一次 open，满足 latex_status 需要已 open 的前提）。
        /// </summary>
        /// <returns>成功返回 null；失败/取消返回原因（提示已经弹过）。</returns>
        public async Task<string> ImportWithRequestAsync(ImportRequest req)
        {
            if (req == null) return "没有导入请求";
            if (string.IsNullOrWhiteSpace(req.SourcePath) || !File.Exists(req.SourcePath))
            {
                ShowToast("找不到要导入的文件：" + req.SourcePath);
                return "找不到文件";
            }

            string ext = Path.GetExtension(req.SourcePath).ToLowerInvariant();
            if (ext == ".key" || ext == ".ppt")
            {
                ShowKeynoteImportHint(ext);
                return "需要先转换成 .pptx";
            }
            if (!_engine.IsConnected)
            {
                ShowToast("导入需要引擎在运行：请先打开或新建一个演示文稿");
                return "引擎未连接";
            }

            if (!await ConfirmCloseAsync("导入演示文稿")) return "已取消";

            if (string.IsNullOrWhiteSpace(req.DeckName)) req.DeckName = "Imported Deck";
            if (string.IsNullOrWhiteSpace(req.DestParent))
            {
                ShowToast("请选择导入到哪个位置");
                return "没有目标位置";
            }

            _importing = true;
            BusyText = "正在导入…";
            try
            {
                JsonElement ev = await _engine.ImportDeckAsync(req, (done, total, scene) =>
                    BusyText = $"正在导入 {done}/{total}" + (string.IsNullOrEmpty(scene) ? "" : "  " + scene));

                if (ev.ValueKind == JsonValueKind.Undefined)
                {
                    ShowToast("导入已取消");
                    return "已取消";
                }
                string error = GetStr(ev, "error");
                if (!string.IsNullOrEmpty(error))
                {
                    ShowToast("导入失败：" + error);
                    return error;
                }

                string deck = GetStr(ev, "deck") ?? req.ResultDeckPath;
                int slides = GetInt(ev, "slides", 0);
                string warning = FirstArrayString(ev, "warnings");
                ShowToast($"导入完成：{slides} 页" + (string.IsNullOrEmpty(warning) ? "" : "（" + warning + "）"));

                if (!await OpenDeckAsync(deck, confirmUnsaved: false))
                    return "导入完成，但打开新 deck 失败";
                return null;
            }
            catch (EngineException ex)
            {
                ShowToast("导入失败：" + ex.Message);
                return ex.Message;
            }
            finally
            {
                _importing = false;
                BusyText = "";
            }
        }

        /// <summary>.key / .ppt 在 Windows 上依赖 macOS 的 Keynote 转换 —— 只给引导，不调引擎。</summary>
        private void ShowKeynoteImportHint(string ext)
        {
            Inform("需要先转换成 .pptx",
                $"Windows 上的 Manimble 不能直接打开 {ext} 文件：这类文件要先由 macOS 的 Keynote 转换成 .pptx。\n\n"
                + "请先在 Keynote（或 PowerPoint）里把它另存为 .pptx，再用「文件 ▸ 导入 ▸ PowerPoint (.pptx)」。");
        }

        #endregion

        #region 导出（P0-12）

        /// <summary>文件 ▸ 导出为 ▸ …。Keynote 只给提示；PDF 不在本版目标里。</summary>
        public async Task ExportAsync(ExportKind kind)
        {
            if (kind == ExportKind.KeynoteDisabled)
            {
                ShowKeynoteExportHint();
                return;
            }
            if (_exporting) { ShowToast("正在导出中，请稍候"); return; }
            if (_session == null)
            {
                ShowToast("没有打开的演示文稿");
                return;
            }
            if (!await EnsureDraftAppliedAsync()) return;

            ExportRequest req = BuildDefaultExportRequest(kind);
            if (ExportDialogHook != null)
            {
                req = await ExportDialogHook(kind, req);
                if (req == null) return;                     // 用户在导出对话框里取消
            }
            else
            {
                string path = PickExportPath(kind, req.OutPath);
                if (string.IsNullOrEmpty(path)) return;
                req.OutPath = path;
            }

            await ExportWithRequestAsync(req);
        }

        /// <summary>导出核心（无 UI，可被对话框/测试直接调用）。</summary>
        /// <returns>成功返回 null；失败/取消返回原因（提示已经弹过）。</returns>
        public async Task<string> ExportWithRequestAsync(ExportRequest req)
        {
            if (req == null) return "没有导出请求";
            if (req.Kind == ExportKind.KeynoteDisabled)
            {
                ShowKeynoteExportHint();
                return "Windows 上不支持导出 Keynote";
            }
            if (string.IsNullOrWhiteSpace(req.OutPath)) return "没有选择输出路径";
            if (!_engine.IsConnected)
            {
                ShowToast("未连接引擎，无法导出");
                return "引擎未连接";
            }
            if (!await EnsureDraftAppliedAsync()) return "源码草稿未应用";

            bool movie = req.Kind == ExportKind.Movie;
            if (_exporting) { ShowToast("正在导出中，请稍候"); return "正在导出"; }

            _exporting = true;
            BusyText = movie ? "正在导出影片…" : "正在导出…";
            try
            {
                Action<int, int, string> progress = (done, total, scene) =>
                    BusyText = (movie ? "正在编码影片 " : "正在导出 ") + $"{done}/{total}"
                               + (string.IsNullOrEmpty(scene) ? "" : "  " + scene);

                if (req.Kind == ExportKind.Python)
                {
                    await _engine.ExportPythonAsync(req.OutPath);
                    ShowToast("已导出 Python：" + req.OutPath);
                    return null;
                }

                JsonElement ev = movie
                    ? await _engine.ExportMovieAsync(req, progress)
                    : await _engine.ExportDeckAsync(req, progress);

                if (ev.ValueKind == JsonValueKind.Undefined)
                {
                    ShowToast("导出已取消");
                    return "已取消";
                }
                string error = GetStr(ev, "error");
                if (!string.IsNullOrEmpty(error))
                {
                    ShowToast("导出失败：" + error);
                    return error;
                }

                string path = GetStr(ev, "path") ?? req.OutPath;
                // pdfError 只是 beamer 导出顺带编译 PDF 的副产物，不是失败标志（设计 §0）
                string pdfError = GetStr(ev, "pdfError");
                ShowToast(string.IsNullOrEmpty(pdfError)
                    ? "已导出：" + path
                    : $"已导出：{path}（PDF 未能顺带生成：{pdfError}）");
                return null;
            }
            catch (EngineException ex)
            {
                ShowToast("导出失败：" + ex.Message);
                return ex.Message;
            }
            finally
            {
                _exporting = false;
                BusyText = "";
            }
        }

        /// <summary>默认导出选项：PPTX/MP4 是 P0，Beamer/Python 是 P1，选项都取引擎的推荐值。</summary>
        private ExportRequest BuildDefaultExportRequest(ExportKind kind)
        {
            string name = _session?.ProjectName ?? "deck";
            var req = new ExportRequest { Kind = kind, Builds = true, Editable = true, Height = 720, Hold = 2, Fps = 30 };
            switch (kind)
            {
                case ExportKind.Beamer: req.OutPath = name + ".tex"; break;
                case ExportKind.Movie: req.OutPath = name + ".mp4"; break;
                case ExportKind.Python: req.OutPath = name + ".py"; break;
                default: req.OutPath = name + ".pptx"; break;
            }
            return req;
        }

        private string PickExportPath(ExportKind kind, string suggestedName)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出为",
                FileName = string.IsNullOrEmpty(suggestedName) ? "deck" : suggestedName,
                AddExtension = true,
            };
            switch (kind)
            {
                case ExportKind.Movie:
                    dlg.Filter = "影片 (.mp4)|*.mp4";
                    dlg.DefaultExt = ".mp4";
                    break;
                case ExportKind.Beamer:
                    dlg.Filter = "Beamer 源文件 (.tex)|*.tex";
                    dlg.DefaultExt = ".tex";
                    break;
                case ExportKind.Python:
                    dlg.Filter = "Python 源码 (.py)|*.py";
                    dlg.DefaultExt = ".py";
                    break;
                default:
                    dlg.Filter = "PowerPoint 演示文稿 (.pptx)|*.pptx";
                    dlg.DefaultExt = ".pptx";
                    break;
            }
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }

        /// <summary>Keynote 导出在 Windows 上必然失败（osascript + Keynote.app），只给引导。</summary>
        private void ShowKeynoteExportHint()
        {
            Inform("Windows 上不支持导出 Keynote",
                "引擎的 Keynote 导出依赖 macOS 的 Keynote.app 与 AppleScript，Windows 上无法使用。\n\n"
                + "请先「导出为 ▸ PowerPoint (.pptx)」，再用 Mac 上的 Keynote 打开它。");
        }

        /// <summary>PDF 不是引擎的独立导出类型，这里只说明现状（P2 待验证，不计入交付）。</summary>
        public void ExplainPdfExport()
        {
            Inform("PDF 导出暂不提供",
                "引擎的导出只有 pptx / beamer / keynote 三种，没有独立的 PDF 分支；\n"
                + "PDF 只是 Beamer 导出时本机 LaTeX 顺带编译出来的副产物，不能作为稳定交付。\n\n"
                + "需要 PDF 时请「导出为 ▸ Beamer (.tex)」，再用本机 TeX 编译（或先导出 PPTX 再另存 PDF）。");
        }

        #endregion

        #region LaTeX 缺包提示（P0-11）

        /// <summary>当前待处理的缺包提示（没有则为 null）。</summary>
        public LatexNotice PendingLatexNotice => _latexNotice;

        /// <summary>提示条是否应当显示（有缺包或占位图，且用户没选「不再询问」）。</summary>
        public bool LatexNoticeVisible => _latexNotice != null && _latexNotice.ShouldShow && !_latexNotice.DontAskAgain;

        /// <summary>提示条正文：缺包列表 + 占位图数量 + 来源文件。</summary>
        public string LatexNoticeText
        {
            get
            {
                if (_latexNotice == null) return "";
                var parts = new List<string>();
                if (_latexNotice.Missing.Count > 0)
                {
                    string list = string.Join("、", _latexNotice.Missing.Take(6));
                    if (_latexNotice.Missing.Count > 6) list += $" 等 {_latexNotice.Missing.Count} 个";
                    parts.Add("缺少宏包：" + list);
                }
                if (_latexNotice.Placeholders > 0) parts.Add($"还有 {_latexNotice.Placeholders} 张占位图");
                if (!string.IsNullOrEmpty(_latexNotice.Source)) parts.Add("来自 " + _latexNotice.Source);
                return parts.Count == 0 ? "" : string.Join(" · ", parts);
            }
        }

        /// <summary>
        /// 打开/导入之后检测缺包与占位图（必须在 deck 已 open 之后调用，rpc 的 latex_status 需要 deck）。
        /// </summary>
        public async Task<LatexNotice> CheckLatexNoticeAsync()
        {
            if (!_engine.IsConnected || _session == null)
            {
                SetLatexNotice(null);
                return null;
            }
            try
            {
                JsonElement status = await _engine.LatexStatusAsync();
                var notice = new LatexNotice
                {
                    Missing = StringArray(status, "missing"),
                    Placeholders = GetInt(status, "placeholders", 0),
                    Source = GetStr(status, "source"),
                    DontAskAgain = _latexDontAskAgain,
                };
                SetLatexNotice(notice);
                return notice;
            }
            catch (Exception ex)
            {
                ShowToast("检查 LaTeX 缺包失败：" + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 一键安装缺包：进度走 latex_install 事件，最终结果取 RPC response；
        /// 装完立刻 latex_retypeset 原位重排占位图（会改写 assets，Ctrl+S 时随 Save 写回原目录）。
        /// </summary>
        public async Task<bool> InstallLatexAsync(string[] files = null)
        {
            string[] wanted = files ?? _latexNotice?.Missing?.ToArray();
            if (wanted == null || wanted.Length == 0)
            {
                ShowToast("没有检测到缺失的宏包");
                return false;
            }
            if (!_engine.IsConnected)
            {
                ShowToast("未连接引擎，无法安装宏包");
                return false;
            }

            BusyText = "正在安装 LaTeX 宏包…";
            try
            {
                JsonElement result = await _engine.LatexInstallAsync(wanted, (done, total, step) =>
                    BusyText = $"正在安装宏包 {done}/{total}" + (string.IsNullOrEmpty(step) ? "" : "  " + step));

                string error = GetStr(result, "error");
                if (IsEngineFalse(result, "ok") || !string.IsNullOrEmpty(error))
                {
                    Inform("无法自动安装 TeX 宏包", LatexInstallHint(error));
                    ShowToast("宏包安装失败：已给出 MiKTeX 安装引导");
                    return false;
                }

                BusyText = "正在重排占位图…";
                await _engine.LatexRetypesetAsync();
                await _engine.ReloadSlides();
                RefreshDeckState();                    // assets 里的清单/占位图变了 → 出现未保存改动
                await CheckLatexNoticeAsync();
                SetLatexNotice(null);
                ShowToast("宏包已安装，占位图已重排（Ctrl+S 可写回原文件）");
                return true;
            }
            catch (Exception ex)
            {
                Inform("无法自动安装 TeX 宏包", LatexInstallHint(ex.Message));
                ShowToast("宏包安装失败：已给出 MiKTeX 安装引导");
                return false;
            }
            finally
            {
                BusyText = "";
            }
        }

        /// <summary>提示条上的 Not Now / Don't Ask Again。</summary>
        public void DismissLatexNotice(bool dontAskAgain)
        {
            if (dontAskAgain) _latexDontAskAgain = true;
            SetLatexNotice(null);
            if (dontAskAgain) ShowToast("不再提示 LaTeX 缺包（下次导入仍会重新检测）");
        }

        private void SetLatexNotice(LatexNotice notice)
        {
            _latexNotice = notice;
            RaisePropertyChanged(nameof(PendingLatexNotice));
            RaisePropertyChanged(nameof(LatexNoticeVisible));
            RaisePropertyChanged(nameof(LatexNoticeText));
        }

        /// <summary>
        /// Windows 上没有 tlmgr（引擎 latex_packages 走的是 macOS 的 tlmgr），
        /// 失败时给可操作的中文引导而不是引擎原文。
        /// </summary>
        private static string LatexInstallHint(string error)
        {
            string tex = EnvironmentService.FindLatex();
            if (string.IsNullOrEmpty(tex))
                return "没有检测到 TeX 发行版。\n\n请先安装 MiKTeX（或 TeX Live），装好后回到这里点「重试」。";
            return "引擎没能自动安装这些宏包。\n\nWindows 上没有 tlmgr 可用，请在 MiKTeX Console ▸ Packages 里搜索并安装上面的宏包，"
                   + "然后回到这里点「重试」。\n\n引擎返回：\n" + (string.IsNullOrEmpty(error) ? "（无详细信息）" : error);
        }

        #endregion

        #region busy 取消

        /// <summary>busy pill 的 X：import / build / export / 编辑都能被 cancel 停下。</summary>
        public async Task CancelBusyAsync()
        {
            await _engine.CancelBusy();
            BusyText = "";
            ShowToast("已请求取消当前作业");
        }

        #endregion

        #region 小工具

        /// <summary>命令参数转 bool：既接受真正的 bool，也接受 "True"/"true"/"again" 这类字符串（XAML 只能传字符串）。</summary>
        private static bool IsTrueParam(object p)
        {
            if (p is bool b) return b;
            string s = p?.ToString();
            return string.Equals(s, "again", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static int GetInt(JsonElement e, string name, int def)
        {
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)) return n;
                if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out int s)) return s;
            }
            return def;
        }

        private static List<string> StringArray(JsonElement e, string name)
        {
            var list = new List<string>();
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement arr)
                && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in arr.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString());
            }
            return list;
        }

        private static string FirstArrayString(JsonElement e, string name)
        {
            List<string> list = StringArray(e, name);
            return list.Count > 0 ? list[0] : null;
        }

        /// <summary>信息提示：有 <see cref="InfoHook"/> 就交给它（T05 提示条 / 自测捕获），否则弹系统对话框。</summary>
        private void Inform(string title, string body)
        {
            if (InfoHook != null)
            {
                InfoHook(title, body);
                return;
            }
            MessageBox.Show(body, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        #endregion
    }
}
