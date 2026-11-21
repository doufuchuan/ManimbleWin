using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using ManimbleWin.Models;
using ManimbleWin.Services;

namespace ManimbleWin.ViewModels
{
    /// <summary>
    /// 引擎桥接层：把 engine/manimble 的 JSON-lines 结果映射为 WPF 模型。
    /// 负责：启动引擎→打开 deck→读取 state/scene→构建 Slide/Build/CanvasObject（含真实渲染帧）→
    /// 为公式对象调用 sprite 生成真实 LaTeX 渲染 PNG。
    /// </summary>
    public class EngineIntegration
    {
        private readonly MainViewModel _vm;
        private EngineService _engine;
        private string _deckPath;

        public bool IsConnected => _engine != null && _engine.IsRunning;
        public string StatusText { get; private set; } = "未连接";
        public event Action<string> Log;

        /// <summary>
        /// 刷新前快照的选区：oid + loc + kind。
        /// 存 loc/kind 是为了在 oid 被引擎重新编号时（分栏、对齐这类结构改写）还能认出同一个对象。
        /// </summary>
        private readonly List<(string Oid, string Loc, ObjectKind Kind)> _restoreSelection =
            new List<(string Oid, string Loc, ObjectKind Kind)>();

        /// <summary>快照时的主选中对象 oid；恢复时优先用它，保证多选后"主选中"不跳。</summary>
        private string _restorePrimaryOid;

        /// <summary>引擎 deck 里真实存在的场景名，用来判断某页能否写回引擎。</summary>
        public HashSet<string> EngineScenes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public EngineIntegration(MainViewModel vm)
        {
            _vm = vm;
        }

        private void Say(string msg) => Log?.Invoke(msg);

        /// <summary>
        /// 连接到真实引擎并打开 <paramref name="deckPath"/>（一般是工作副本的 deck，不是原目录）。
        /// </summary>
        /// <param name="deckPath">要打开的 deck（必须是工作副本里的 deck；不接受 null）。</param>
        /// <param name="cacheDir">
        /// 渲染缓存目录；null 用用户数据目录下的 render-cache。
        /// 生命周期流程应传 <c>WorkspaceService.CacheDirFor(原路径)</c>，让重开同一个 deck 复用缓存。
        /// </param>
        public async Task<bool> ConnectAndLoad(string deckPath = null, string cacheDir = null)
        {
            // 绝不回落到 samples/hello：那会把渲染/写盘直接落到仓库里的示例上（绕过工作副本）。
            // 需要「示例 deck」的调用方必须先建副本，再把副本 deck 传进来。
            if (string.IsNullOrWhiteSpace(deckPath))
            {
                Say("没有指定要打开的 deck：请先建工作副本（OpenDeckAsync）再连引擎。");
                StatusText = "未指定 deck";
                return false;
            }
            _deckPath = deckPath;
            string python = EnvironmentService.FindPython(out _);
            if (python == null)
            {
                Say("未找到 Python，无法启动引擎。请先在依赖中心安装。");
                StatusText = "缺少 Python";
                return false;
            }

            Say($"启动引擎：{python}");
            _engine = new EngineService(python, EnvironmentService.EngineDir, python);
            _engine.LogReceived += s => Say("[engine] " + s);
            _engine.EventReceived += OnEngineEvent;
            if (!_engine.Start())
            {
                StatusText = "引擎启动失败";
                Say("引擎进程启动失败。");
                return false;
            }

            try
            {
                // cache 目录默认放在用户数据目录，长期保留
                string cache = string.IsNullOrEmpty(cacheDir)
                    ? Path.Combine(EnvironmentService.UserDataDir, "render-cache")
                    : cacheDir;
                Directory.CreateDirectory(cache);
                Say($"打开 deck：{_deckPath}");
                await _engine.OpenDeck(_deckPath, cache);
                StatusText = "已连接";
                Say("deck 已打开，读取场景列表…");
                await ReloadSlides();
                await _vm.SyncDeckThemeFromEngineAsync();   // 让界面的主题下拉框对齐 deck 真实主题
                return true;
            }
            catch (EngineException ex)
            {
                StatusText = "打开失败";
                Say("引擎打开 deck 失败：" + ex.Message);
                return false;
            }
        }

        private void OnEngineEvent(JsonElement ev)
        {
            string name = ev.TryGetProperty("event", out var n) ? n.GetString() : "";
            if (name == "rendered")
            {
                string scene = ev.TryGetProperty("name", out var s) ? s.GetString() : "";
                Say($"场景已渲染：{scene}");
                Application.Current?.Dispatcher.InvokeAsync(async () =>
                {
                    // 若渲染的是当前场景，刷新它的帧
                    if (_vm.CurrentSlide != null && _vm.CurrentSlide.Name == scene)
                        await LoadSceneInto(_vm.CurrentSlide);
                });
            }
            else if (name == "edit_failed")
                Say("编辑验证失败，已回滚：" + (ev.TryGetProperty("error", out var e) ? e.GetString() : ""));
        }

        /// <summary>读取 state，构建幻灯片列表。</summary>
        public async Task ReloadSlides()
        {
            if (!IsConnected) return;
            try
            {
                var state = await _engine.State();
                if (!state.TryGetProperty("scenes", out var scenes)) return;

                var newSlides = new ObservableCollection<Slide>();
                int idx = 1;
                foreach (var sc in scenes.EnumerateArray())
                {
                    string name = GetStr(sc, "name") ?? $"Slide {idx}";
                    string file = GetStr(sc, "file") ?? "slides.py";
                    int line = GetInt(sc, "line", 1);
                    bool hidden = GetBool(sc, "hidden");
                    string status = GetStr(sc, "status");
                    string src = ReadSceneSource(file, name, line);

                    var slide = new Slide
                    {
                        Name = name, Number = idx, File = file, Line = line,
                        Hidden = hidden, Error = status == "error", Source = src
                    };
                    slide.Builds.Add(new Build { Name = "Build 1" });
                    newSlides.Add(slide);
                    idx++;
                }

                EngineScenes.Clear();
                foreach (var ns in newSlides) EngineScenes.Add(ns.Name);

                // 合并而不是清空：连接引擎不应该抹掉用户已在应用里做好的内容。
                // 同名页用引擎的源码/行号更新，引擎里没有的页原样保留，引擎新增的页追加到末尾。
                string currentName = _vm.CurrentSlide?.Name;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var existing = _vm.Slides.ToList();
                    foreach (var ns in newSlides)
                    {
                        var match = existing.FirstOrDefault(s => string.Equals(s.Name, ns.Name, StringComparison.Ordinal));
                        if (match == null)
                        {
                            _vm.Slides.Add(ns);
                            continue;
                        }
                        match.File = ns.File;
                        match.Line = ns.Line;
                        match.Error = ns.Error;
                        match.Hidden = ns.Hidden;
                        if (!string.IsNullOrWhiteSpace(ns.Source)) match.Source = ns.Source;
                    }

                    if (_vm.CurrentSlide == null && _vm.Slides.Count > 0) _vm.CurrentSlide = _vm.Slides[0];
                    if (currentName != null && !string.Equals(_vm.CurrentSlide?.Name, currentName, StringComparison.Ordinal))
                        _vm.CurrentSlide = _vm.Slides.FirstOrDefault(s => string.Equals(s.Name, currentName, StringComparison.Ordinal)) ?? _vm.CurrentSlide;
                });

                // 只给「引擎也认识」的当前页刷真实渲染帧，应用内独有的页面保持原样
                if (_vm.CurrentSlide != null && EngineScenes.Contains(_vm.CurrentSlide.Name))
                    await LoadSceneInto(_vm.CurrentSlide);
            }
            catch (EngineException ex)
            {
                Say("读取状态失败：" + ex.Message);
            }
        }

        /// <summary>从源文件里截取某个场景 class 的源码块（近似：从 class 行到下一个 class）。</summary>
        private string ReadSceneSource(string file, string className, int startLine)
        {
            try
            {
                string path = Path.Combine(_deckPath, file);
                if (!File.Exists(path)) return "";
                var lines = File.ReadAllLines(path);
                int start = Math.Max(0, startLine - 1);
                // 若 startLine 不指向 class，向后找 class <name>
                for (int i = start; i < Math.Min(lines.Length, start + 6); i++)
                    if (lines[i].Contains("class " + className)) { start = i; break; }
                var sb = new System.Text.StringBuilder();
                for (int i = start; i < lines.Length; i++)
                {
                    if (i > start && lines[i].StartsWith("class ")) break;
                    sb.AppendLine(lines[i]);
                }
                return sb.ToString().TrimEnd();
            }
            catch { return ""; }
        }

        /// <summary>调用 scene，把真实渲染帧与对象元数据载入指定 Slide。</summary>
        public async Task LoadSceneInto(Slide slide)
        {
            if (!IsConnected || slide == null) return;
            try
            {
                // 快照必须早于 Builds.Clear()：那之后旧对象就成了孤儿，
                // 而格式操作后"选中不丢"完全依赖这份 oid 快照。
                SnapshotSelectionForRestore(slide);
                var data = await _engine.Scene(slide.Name);
                // stops[]: 每个 build 一帧
                if (data.TryGetProperty("stops", out var stops) && stops.ValueKind == JsonValueKind.Array)
                {
                    var arr = stops.EnumerateArray().ToList();
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        slide.Builds.Clear();
                        int n = Math.Max(1, arr.Count);
                        for (int i = 0; i < n; i++)
                        {
                            var b = new Build { Name = $"构建 {i + 1}" };
                            if (i < arr.Count)
                                b.RenderedImage = GetStr(arr[i], "png") ?? "";
                            slide.Builds.Add(b);
                        }
                        slide.CurrentBuildIndex = 0;
                        _vm.RaiseCurrentChanged();
                    });

                    // objects: 全局对象表 -> 分配到第一个 build（近似：显示在该 build 的可见列表）
                    if (data.TryGetProperty("objects", out var objects))
                        ApplyObjects(slide, objects, arr);
                }

                // 备注 / 逐 build 备注 / 替代文本。
                // 用户正在抽屉里输入时不要回写覆盖：等防抖写回提交、下一次渲染再对齐。
                bool notesPending = _vm.HasPendingNotes(slide);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (notesPending) return;
                    if (data.TryGetProperty("notes", out var notes) && notes.ValueKind == JsonValueKind.String
                        && slide.Builds.Count > 0)
                        slide.Builds[0].Notes = notes.GetString() ?? "";
                    if (data.TryGetProperty("buildNotes", out var buildNotes)
                        && buildNotes.ValueKind == JsonValueKind.Array)
                    {
                        var arr = buildNotes.EnumerateArray().ToList();
                        for (int i = 0; i < slide.Builds.Count && i < arr.Count; i++)
                            slide.Builds[i].BuildNotes = arr[i].ValueKind == JsonValueKind.String ? arr[i].GetString() : "";
                    }
                    if (data.TryGetProperty("altText", out var altText) && altText.ValueKind == JsonValueKind.String)
                        slide.AltText = altText.GetString() ?? "";
                });

                // 源码
                if (data.TryGetProperty("source", out var src) && src.TryGetProperty("text", out var txt))
                    slide.Source = txt.GetString() ?? slide.Source;

                Say($"已渲染 {slide.Name}：{slide.Builds.Count} 个 build");
            }
            catch (EngineException ex)
            {
                Say($"渲染 {slide.Name} 失败：{ex.Message}");
                slide.Error = true;
                slide.ErrorMessage = ex.Message;
            }
        }

        private void ApplyObjects(Slide slide, JsonElement objects, List<JsonElement> stops)
        {
            if (objects.ValueKind != JsonValueKind.Object) return;
            // 每个 build 的可见对象列表；visible 项形如 [oid, x1, y1, x2, y2]（1280×720 设计坐标）。
            // 必须把这块几何一起带过来：否则真实渲染后 CanvasObject 都停在 (0,0)、尺寸为 0，
            // 对象模板的点击框根本点不到，用户就会看到「渲染完不能选中修改」。
            var visibleSets = new List<HashSet<int>>();
            var bounds = new Dictionary<int, (double x, double y, double w, double h)>();
            foreach (var st in stops)
            {
                var set = new HashSet<int>();
                if (st.TryGetProperty("visible", out var vis) && vis.ValueKind == JsonValueKind.Array)
                    foreach (var v in vis.EnumerateArray())
                    {
                        if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() == 0 ||
                            !v[0].TryGetInt32(out int oid)) continue;
                        set.Add(oid);
                        if (v.GetArrayLength() < 5 || bounds.ContainsKey(oid)) continue;
                        double x1 = v[1].ValueKind == JsonValueKind.Number ? v[1].GetDouble() : 0;
                        double y1 = v[2].ValueKind == JsonValueKind.Number ? v[2].GetDouble() : 0;
                        double x2 = v[3].ValueKind == JsonValueKind.Number ? v[3].GetDouble() : x1 + 120;
                        double y2 = v[4].ValueKind == JsonValueKind.Number ? v[4].GetDouble() : y1 + 40;
                        bounds[oid] = (Math.Min(x1, x2), Math.Min(y1, y2),
                                       Math.Max(16, Math.Abs(x2 - x1)), Math.Max(12, Math.Abs(y2 - y1)));
                    }
                visibleSets.Add(set);
            }
            if (visibleSets.Count == 0) visibleSets.Add(new HashSet<int>());

            Application.Current.Dispatcher.Invoke(() =>
            {
                int filled = 0;
                for (int bi = 0; bi < slide.Builds.Count; bi++)
                {
                    var build = slide.Builds[bi];
                    // 先在局部 List 里攒完再一次性赋给 build.Objects。
                    // 原来逐条 build.Objects.Add(co) 会让 ItemsControl 每加一个都重排一次，
                    // 一页十几个对象就能看见明显的闪；一次性替换只有一个变更通知。
                    var list = new List<CanvasObject>(16);
                    foreach (var prop in objects.EnumerateObject())
                    {
                        if (!int.TryParse(prop.Name, out int oid)) continue;
                        var o = prop.Value;
                        string kind = GetStr(o, "kind") ?? "text";
                        var co = new CanvasObject
                        {
                            Oid = prop.Name,
                            Kind = MapKind(kind),
                            Value = GetStr(o, "value") ?? "",
                            DisplayText = MakeDisplay(GetStr(o, "value") ?? "", kind),
                            FontSize = GetDouble(o, "fontSize", 28),
                            Color = NormalizeColor(GetStr(o, "color")),
                            Font = GetStr(o, "font") ?? "Segoe UI",
                            Loc = GetStr(o, "loc") ?? "",
                            SourceLine = ParseLine(GetStr(o, "loc")),
                            IsSelected = false
                        };
                        // 格式状态回填：灯亮不亮只认引擎给的 weight/slant/format，不在本地记忆。
                        ApplyFormatFields(co, o);
                        // 用引擎给的包围盒定位点击框；没有几何信息的对象给个居中的最小可点区域。
                        if (bounds.TryGetValue(oid, out var box))
                        {
                            co.X = box.x;
                            co.Y = box.y;
                            co.Width = box.w;
                            co.Height = box.h;
                        }
                        else
                        {
                            co.X = 580;
                            co.Y = 340;
                            co.Width = 120;
                            co.Height = 40;
                        }
                        if (co.Kind == ObjectKind.Image)
                        {
                            // 引擎 scene 结果对 image 返回 image(文件名) 与 imagePath(绝对路径)，
                            // 让 CanvasView 的 PathToImage 直接显示真实缩略图。
                            co.ImagePath = GetStr(o, "imagePath") ?? GetStr(o, "image") ?? "";
                            co.ImageLabel = GetStr(o, "image") ?? co.ImageLabel;
                        }
                        list.Add(co);
                    }
                    build.Objects = new ObservableCollection<CanvasObject>(list);
                    // sprite 请求要放在替换之后：替换前起的任务拿到的是马上要被丢掉的对象，
                    // sprite 回来时那个对象已经不在列表里，图就永远不会显示。
                    foreach (var co in list)
                        if (co.Kind == ObjectKind.Tex) _ = RenderSpriteFor(co);
                    filled = list.Count;
                }
                // 选中恢复必须在这一批对象都进列表之后做，否则匹配不到任何 oid。
                RestoreSelectionByOid(slide);
                // Objects 是"整体换了一个集合"而不是就地增删，画布的 ItemsControl 不会收到
                // CollectionChanged，必须让 CurrentObjects 这个派生属性重新通知一次，
                // 否则画布还绑在上一批（已被丢弃的）集合上 —— 表现为"格式化后画布空白/不刷新"。
                _vm.RaiseCurrentChanged();
                if (filled > 0) Say($"已回填 {filled} 个对象的格式状态");
            });
        }

        /// <summary>
        /// 把 scene 里单个对象的格式字段读进 <see cref="CanvasObject"/>。
        /// <para>全程防御：<c>weight</c> / <c>slant</c> / <c>format</c> 都可能缺失或为 null，
        /// <c>highlight</c> 还可能是字符串 <c>"on"</c>，<c>lineSpacing</c> 可能是 int 也可能是 float。
        /// 这里一律走既有的 GetStr / GetDouble（后者先判 ValueKind，绕开 .NET 8
        /// <c>TryGetDouble</c> 对 null 抛异常的坑）。</para>
        /// </summary>
        private static void ApplyFormatFields(CanvasObject co, JsonElement o)
        {
            if (co == null || o.ValueKind != JsonValueKind.Object) return;

            string weight = GetStr(o, "weight");
            string slant = GetStr(o, "slant");
            co.Weight = weight;
            co.Slant = slant;
            // 以 weight/slant 的取值集合为准，不读引擎的 bold/italic 布尔：
            // 两者语义会漂（SEMIBOLD 算不算粗体只有集合能表达），只留一个事实来源。
            co.Bold = IsBoldWeight(weight);
            co.Italic = IsItalicSlant(slant);

            // 只有 text / tex 才有 format / textOk / frame 三项，其他 kind 直接跳过。
            if (o.TryGetProperty("format", out var fmt) && fmt.ValueKind == JsonValueKind.Object)
            {
                co.Underline = GetBool(fmt, "underline");
                co.Strike = GetBool(fmt, "strike");

                // 三态：null（无高亮）/ "#RRGGBB" / "on"（有底块但没写颜色）。
                // "on" 必须原样保留：归一化成 null 会让灯误灭，而那个对象确实有底块。
                string hl = GetStr(fmt, "highlight");
                co.HighlightColor = string.Equals(hl, "on", StringComparison.Ordinal)
                    ? "on"
                    : NormalizeHexOrNull(hl);

                string align = GetStr(fmt, "align");
                co.ParagraphAlign = string.IsNullOrEmpty(align) ? null : align.ToLowerInvariant();
                co.Columns = Math.Max(1, GetInt(fmt, "columns", 1));

                // lineSpacing 缺失 / null 都表示"源码里没有 line_spacing"，即引擎默认行距。
                bool hasSpacing = fmt.TryGetProperty("lineSpacing", out var ls) && ls.ValueKind == JsonValueKind.Number
                                  && ls.TryGetDouble(out _);
                co.LineSpacing = hasSpacing ? GetDouble(fmt, "lineSpacing", 0) : (double?)null;
            }

            co.IsTextEditable = GetBool(o, "textOk");
            // frame 是 [x1, y1, x2, y2] 数组（有文本框时才有），用来预判段落对齐能不能用。
            co.HasTextBox = o.TryGetProperty("frame", out var fr) && fr.ValueKind == JsonValueKind.Array
                            && fr.GetArrayLength() >= 4;
        }

        /// <summary>粗体判定：只有这几个字重算"按钮该亮"（PRD 明确给的集合，与 macOS 一致）。</summary>
        private static bool IsBoldWeight(string weight)
        {
            if (string.IsNullOrEmpty(weight)) return false;
            switch (weight.ToUpperInvariant())
            {
                case "BOLD":
                case "SEMIBOLD":
                case "ULTRABOLD":
                case "HEAVY":
                case "ULTRAHEAVY":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>斜体判定：ITALIC（真斜体）与 OBLIQUE（伪斜体）都算。</summary>
        private static bool IsItalicSlant(string slant)
        {
            if (string.IsNullOrEmpty(slant)) return false;
            string s = slant.ToUpperInvariant();
            return s == "ITALIC" || s == "OBLIQUE";
        }

        /// <summary>引擎色值归一化：不合法就当没有（null），交给 UI 的"无颜色"分支。</summary>
        private static string NormalizeHexOrNull(string s)
        {
            string hex = FormatPalette.NormalizeHex(s, out bool ok);
            return ok ? hex : null;
        }

        /// <summary>
        /// 快照当前选区，供刷新后的 <see cref="RestoreSelectionByOid"/> 复原。
        /// <para>为什么存 oid + loc + kind 三件套：纯样式改动不改源码结构，oid 不变；
        /// 但 <c>columns</c> 会把 Text 包进 VGroup、<c>align</c> 会把 Text 改写成 Paragraph，
        /// 这两种情况下引擎会重新编号，oid 全变 —— 得靠 loc + kind 兜底。</para>
        /// </summary>
        private void SnapshotSelectionForRestore(Slide slide)
        {
            _restoreSelection.Clear();
            _restorePrimaryOid = null;
            // 只有"正在载入的就是当前页"才谈得上恢复：载入别的页时当前选区跟它无关。
            if (slide == null || !ReferenceEquals(slide, _vm.CurrentSlide)) return;
            foreach (var o in _vm.SelectedObjects)
                if (o != null && !string.IsNullOrEmpty(o.Oid))
                    _restoreSelection.Add((o.Oid, o.Loc, o.Kind));
            _restorePrimaryOid = _vm.SelectedObject?.Oid;
        }

        /// <summary>
        /// 对象集合替换完成后按 oid 恢复选区；oid 找不到时退化为 loc + kind，再找不到就清空选区。
        /// <para>为什么不猜：猜错的代价是用户以为选中的是另一个对象，接着点格式就改错了东西，
        /// 而"选区丢了"至少是可见且可恢复的。</para>
        /// <para>为什么不会打转：SetSelection 只写本地状态 + 发 PropertyChanged，
        /// 不调用任何引擎接口，所以"刷新 → 恢复选中 → 再刷新"这条回路不存在。</para>
        /// </summary>
        private void RestoreSelectionByOid(Slide slide)
        {
            if (slide == null || !ReferenceEquals(slide, _vm.CurrentSlide)) { _restoreSelection.Clear(); return; }
            if (_restoreSelection.Count == 0) return;
            var build = slide.CurrentBuild;
            if (build == null) { _restoreSelection.Clear(); return; }

            var pool = build.Objects.Where(o => o != null).ToList();
            var matched = new List<CanvasObject>(_restoreSelection.Count);
            foreach (var snap in _restoreSelection)
            {
                var hit = pool.FirstOrDefault(o => string.Equals(o.Oid, snap.Oid, StringComparison.Ordinal));
                if (hit == null)
                    hit = pool.FirstOrDefault(o => !matched.Contains(o)
                                                   && o.Kind == snap.Kind
                                                   && string.Equals(o.Loc, snap.Loc, StringComparison.Ordinal));
                if (hit != null && !matched.Contains(hit)) matched.Add(hit);
            }

            _restoreSelection.Clear();
            if (matched.Count == 0)
            {
                // 一个都对不上：宁可清空，也不要留着一个指向已销毁对象的选区。
                _vm.ClearSelection();
                _restorePrimaryOid = null;
                return;
            }

            var primary = matched.FirstOrDefault(o => string.Equals(o.Oid, _restorePrimaryOid, StringComparison.Ordinal))
                          ?? matched[0];
            _restorePrimaryOid = null;
            _vm.SetSelection(matched, primary);
        }

        /// <summary>
        /// 单对象格式编辑：把 <see cref="FormatRequest"/> 摊平发给 edit RPC，返回 ok / error 原文。
        /// 失败时不在本地做任何改动 —— 灯由随后的 scene 回填决定，天然与源码一致。
        /// </summary>
        public async Task<EditOutcome> EditOneAsync(string scene, FormatRequest req)
        {
            if (req == null) return EditOutcome.Fail("没有可提交的格式请求", default);
            if (!IsConnected) return EditOutcome.Fail("未连接引擎", default);
            try
            {
                var raw = await Edit(scene, req.Op, req.Args);
                if (IsEngineFalse(raw, "ok"))
                    return EditOutcome.Fail(GetStr(raw, "error") ?? "引擎未说明原因", raw);
                return EditOutcome.Success(raw);
            }
            catch (EngineException ex)
            {
                return EditOutcome.Fail(ex.Message, default);
            }
        }

        /// <summary>引擎回复是否明确失败（缺省 ok 视为成功，与仓库其他调用处一致）。</summary>
        private static bool IsEngineFalse(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.False;

        /// <summary>
        /// 读某页最新的对象表（oid + loc + kind + 原文）。
        /// <para>用途只有一个：多选批量格式化时，前一个对象的编辑可能让引擎重新编号，
        /// 后面几个对象的 oid 就漂了，需要按「位置 + 类型 + 原文」重新认领。
        /// 刚编辑完的页面走的是已缓存的渲染记录，这次读不会触发重新渲染。</para>
        /// </summary>
        public async Task<List<(string Oid, string Loc, ObjectKind Kind, string Value)>> ReadObjectTableAsync(string scene)
        {
            var rows = new List<(string Oid, string Loc, ObjectKind Kind, string Value)>();
            if (!IsConnected || string.IsNullOrEmpty(scene)) return rows;
            var data = await _engine.Scene(scene);
            if (!data.TryGetProperty("objects", out var objs) || objs.ValueKind != JsonValueKind.Object)
                return rows;
            foreach (var prop in objs.EnumerateObject())
                rows.Add((prop.Name,
                          GetStr(prop.Value, "loc") ?? "",
                          MapKind(GetStr(prop.Value, "kind") ?? "text"),
                          GetStr(prop.Value, "value") ?? ""));
            return rows;
        }

        private async Task RenderSpriteFor(CanvasObject co)
        {
            if (!IsConnected || string.IsNullOrEmpty(co.Value)) return;
            try
            {
                var r = await _engine.Sprite("tex", co.Value, Math.Max(24, (int)co.FontSize), co.Color);
                string path = GetStr(r, "path");
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    await Application.Current.Dispatcher.InvokeAsync(() => co.SpritePath = path);
            }
            catch (EngineException ex)
            {
                Say($"公式渲染失败（{co.Value}）：{ex.Message}");
            }
        }

        /// <summary>公开：为任意 LaTeX 公式渲染 PNG（供 Inspector/终端调用）。返回路径或 null。</summary>
        public async Task<string> RenderFormula(string tex, int fontSize = 64, string color = "#FFFFFF")
        {
            if (!IsConnected) return null;
            try
            {
                var r = await _engine.Sprite("tex", tex, fontSize, color);
                return GetStr(r, "path");
            }
            catch (EngineException ex)
            {
                Say("公式渲染失败：" + ex.Message);
                return null;
            }
        }

        /// <summary>把一段完整源码写回引擎工作副本（edit_source），成功后刷新该页真实渲染。</summary>
        public async Task<bool> ApplyTextEdit(Slide slide, string expected, string text)
        {
            if (!IsConnected || slide == null) return false;

            // 应用内独有的页（引擎 deck 里没有同名场景）不能写回，否则一定被引擎拒绝；
            // 这种页只在本地生效，并明确告诉用户原因。
            if (EngineScenes.Count > 0 && !EngineScenes.Contains(slide.Name))
            {
                Say($"「{slide.Name}」不在引擎当前 deck 中，改动已保存在应用内（未写回文件）。");
                return true;
            }

            try
            {
                var r = await _engine.EditSource(slide.Name, slide.File, expected, text);
                bool ok = !r.TryGetProperty("ok", out var okEl) || okEl.ValueKind != JsonValueKind.False;
                if (!ok)
                {
                    string err = GetStr(r, "error") ?? "未知错误";
                    Say("源码写回被拒绝：" + err + "（本地源码已保留，可在代码窗格重新应用）");
                    return false;
                }
                Say($"源码已写回并重新渲染：{slide.Name}");
                await LoadSceneInto(slide);
                return true;
            }
            catch (EngineException ex)
            {
                Say("源码写回失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 源码面板专用写回：expected 实时从引擎读取，与 edit_source 的校验基准完全一致。
        /// 真正的并发冲突给用户可操作的三选一（覆盖 / 加载新版本并暂存草稿 / 取消），
        /// 不再让用户手动复制草稿后重新加载。
        /// </summary>
        public async Task<bool> ApplyTextEditFromEditor(Slide slide, string draft)
        {
            if (!IsConnected || slide == null) return false;

            if (EngineScenes.Count > 0 && !EngineScenes.Contains(slide.Name))
            {
                Say($"「{slide.Name}」不在引擎当前 deck 中，改动已保存在应用内（未写回文件）。");
                return true;
            }

            string expected = await GetSourceSnapshot(slide);
            var first = await TryEditSource(slide, expected, draft);
            if (first.ok) return true;

            string err = first.error ?? "";
            if (err.IndexOf("Source changed", StringComparison.OrdinalIgnoreCase) < 0)
            {
                Say("源码写回被拒绝：" + err);
                return false;
            }

            var answer = MessageBox.Show(
                "源码在编辑期间被后台写回改变。你的草稿没有丢失。\n\n" +
                "是(Y)：按最新快照重试，把草稿写回（会覆盖后台改动）\n" +
                "否(N)：加载引擎最新版本，草稿暂存到「草稿」按钮\n" +
                "取消：保持现状，稍后再处理",
                "源码写回冲突", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

            if (answer == MessageBoxResult.Yes)
            {
                // 用户明确选择覆盖：重新取快照，尽量缩短取快照到写回之间的窗口
                string fresh = await GetSourceSnapshot(slide);
                var retry = await TryEditSource(slide, fresh, draft);
                if (retry.ok) return true;
                Say("源码写回再次被拒绝：" + (retry.error ?? "未知错误"));
                return false;
            }
            if (answer == MessageBoxResult.No)
            {
                // 冲突说明源码已变，界面要加载的是「此刻」的引擎版本，不是第一次尝试时的快照。
                string latest = await GetSourceSnapshot(slide);
                _vm.LoadSourceFromEngine(slide, latest, draft);
                return false;
            }

            Say("源码写回已取消，草稿保留在编辑器中。");
            return false;
        }

        /// <summary>取引擎当前源码原文；取不到时退回本地副本（本地模式下仍可工作）。</summary>
        private async Task<string> GetSourceSnapshot(Slide slide)
        {
            try
            {
                var r = await _engine.Source(slide.Name);
                if (r.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    return t.GetString() ?? slide.Source ?? "";
            }
            catch (Exception ex)
            {
                Say("读取源码快照失败，改用本地副本：" + ex.Message);
            }
            return slide.Source ?? "";
        }

        private async Task<(bool ok, string error)> TryEditSource(Slide slide, string expected, string draft)
        {
            try
            {
                var r = await _engine.EditSource(slide.Name, slide.File, expected, draft);
                bool ok = !r.TryGetProperty("ok", out var okEl) || okEl.ValueKind != JsonValueKind.False;
                if (!ok) return (false, GetStr(r, "error") ?? "未知错误");
                Say($"源码已写回并重新渲染：{slide.Name}");
                await LoadSceneInto(slide);
                return (true, null);
            }
            catch (EngineException ex)
            {
                return (false, ex.Message);
            }
        }

        /// <summary>通过引擎插入一个对象（insert 命令），返回是否成功。</summary>
        public async Task<bool> InsertViaEngine(Slide slide, int stop, string kind, object extra)
        {
            if (!IsConnected || slide == null) return false;
            try
            {
                var r = await _engine.Insert(slide.Name, stop, kind, extra);
                bool ok = !r.TryGetProperty("ok", out var okEl) || okEl.ValueKind != JsonValueKind.False;
                if (ok) { Say($"已通过引擎插入 {kind}"); await LoadSceneInto(slide); }
                else Say("插入失败：" + (GetStr(r, "error") ?? "未知错误"));
                return ok;
            }
            catch (EngineException ex) { Say("插入失败：" + ex.Message); return false; }
        }

        /// <summary>
        /// 让 manim 真正渲染某个场景的动画片段（animation_preview），
        /// 等引擎通过事件送回 clips 后返回；超时或失败返回 null。
        /// </summary>
        public async Task<List<AnimationClip>> BuildAnimationPreviewAsync(string scene, int timeoutMs = 900000)
        {
            if (!IsConnected || string.IsNullOrEmpty(scene)) return null;

            var tcs = new TaskCompletionSource<List<AnimationClip>>(TaskCreationOptions.RunContinuationsAsynchronously);

            void Handler(JsonElement ev)
            {
                string name = GetStr(ev, "event");
                if (name != "animation_preview") return;
                string phase = GetStr(ev, "phase");
                if (phase == "ready")
                {
                    var list = new List<AnimationClip>();
                    if (ev.TryGetProperty("clips", out var clips) && clips.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var c in clips.EnumerateArray())
                        {
                            string path = GetStr(c, "path");
                            if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                            list.Add(new AnimationClip
                            {
                                Path = path,
                                IsImage = GetStr(c, "kind") == "image",
                                Rate = GetDouble(c, "playbackRate", 1.0),
                                Label = scene
                            });
                        }
                    }
                    Say($"动画片段已就绪：{list.Count} 段");
                    tcs.TrySetResult(list);
                }
                else if (phase == "error")
                {
                    tcs.TrySetException(new EngineException(GetStr(ev, "error") ?? "动画构建失败"));
                }
                else if (phase == "cancelled")
                {
                    tcs.TrySetResult(null);
                }
            }

            _engine.EventReceived += Handler;
            try
            {
                var accepted = await _engine.Request("animation_preview", new { scene, height = 720 }, 60000);
                if (accepted.ValueKind == JsonValueKind.Object && accepted.TryGetProperty("ok", out var okEl)
                    && okEl.ValueKind == JsonValueKind.False)
                    throw new EngineException(GetStr(accepted, "error") ?? "引擎拒绝了动画预览请求");

                var finished = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
                if (finished != tcs.Task)
                {
                    Say("等待动画片段超时");
                    return null;
                }
                return await tcs.Task;
            }
            catch (EngineException ex)
            {
                Say("动画渲染失败：" + ex.Message);
                return null;
            }
            finally
            {
                _engine.EventReceived -= Handler;
            }
        }

        /// <summary>构建并播放演示。片段路径由 present 事件回传。</summary>
        public async Task<bool> Present(string scene = null)        {
            if (!IsConnected) return false;
            try
            {
                var r = await _engine.Present(scene);
                bool ok = !r.TryGetProperty("ok", out var okEl) || okEl.ValueKind != JsonValueKind.False;
                Say(ok ? "演示已生成" : ("演示失败：" + (GetStr(r, "error") ?? "")));
                return ok;
            }
            catch (EngineException ex) { Say("演示失败：" + ex.Message); return false; }
        }

        #region 演讲脚本写回 / 演讲者视图

        /// <summary>读备注。stop 为空读整页备注；否则读第 stop 个 build 的备注。</summary>
        public async Task<string> NotesGet(string scene, int? stop = null)
        {
            if (!IsConnected || string.IsNullOrEmpty(scene)) return "";
            try
            {
                var r = await _engine.Notes(scene, "get", null, stop);
                return GetStr(r, "text") ?? "";
            }
            catch (EngineException ex) { Say("读取备注失败：" + ex.Message); return ""; }
        }

        /// <summary>把备注写回引擎源码。stop 为空写整页备注；否则写第 stop 个 build 的备注。</summary>
        public async Task<bool> NotesSet(string scene, string text, int? stop = null)
        {
            if (!IsConnected || string.IsNullOrEmpty(scene)) return false;
            // 应用内独有的页不在引擎 deck 里，写回一定失败，保持本地内存状态即可。
            if (EngineScenes.Count > 0 && !EngineScenes.Contains(scene)) return false;
            try
            {
                var r = await _engine.Notes(scene, "set", text ?? "", stop);
                bool ok = !r.TryGetProperty("ok", out var okEl) || okEl.ValueKind != JsonValueKind.False;
                if (!ok) Say("备注写回失败：" + (GetStr(r, "error") ?? "未知错误"));
                return ok;
            }
            catch (EngineException ex) { Say("备注写回失败：" + ex.Message); return false; }
        }

        /// <summary>把幻灯片替代文本写回引擎源码（alt_text）。</summary>
        public async Task<bool> AltTextSet(string scene, string text)
        {
            if (!IsConnected || string.IsNullOrEmpty(scene)) return false;
            if (EngineScenes.Count > 0 && !EngineScenes.Contains(scene)) return false;
            try
            {
                var r = await _engine.AltText(scene, "set", text ?? "");
                bool ok = !r.TryGetProperty("ok", out var okEl) || okEl.ValueKind != JsonValueKind.False;
                if (!ok) Say("替代文本写回失败：" + (GetStr(r, "error") ?? "未知错误"));
                return ok;
            }
            catch (EngineException ex) { Say("替代文本写回失败：" + ex.Message); return false; }
        }

        /// <summary>
        /// 演讲者视图数据：每页的静帧路径 + 备注；未就绪的页 pages 为空并带 pending 标记。
        /// 返回 null 表示引擎不可用 / 请求失败（调用方应显示明确提示）。
        /// </summary>
        public async Task<List<DeckPageInfo>> DeckPages(string current, int ahead = 2)
        {
            if (!IsConnected || string.IsNullOrEmpty(current)) return null;
            try
            {
                var r = await _engine.DeckPages(current, ahead);
                var result = new List<DeckPageInfo>();
                if (!r.TryGetProperty("slides", out var slides) || slides.ValueKind != JsonValueKind.Array)
                    return result;
                foreach (var s in slides.EnumerateArray())
                {
                    var info = new DeckPageInfo
                    {
                        Scene = GetStr(s, "scene") ?? "",
                        Notes = GetStr(s, "notes") ?? "",
                        Pending = GetBool(s, "pending"),
                        Error = GetStr(s, "error") ?? ""
                    };
                    if (s.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Array)
                        foreach (var p in pages.EnumerateArray())
                            if (p.ValueKind == JsonValueKind.String) info.Pages.Add(p.GetString());
                    if (s.TryGetProperty("buildNotes", out var bn) && bn.ValueKind == JsonValueKind.Array)
                        foreach (var b in bn.EnumerateArray())
                            info.BuildNotes.Add(b.ValueKind == JsonValueKind.String ? b.GetString() : "");
                    result.Add(info);
                }
                return result;
            }
            catch (Exception ex) { Say("演讲者视图数据读取失败：" + ex.Message); return null; }
        }

        #endregion

        /// <summary>导出影片到指定路径。</summary>
        public async Task<string> ExportMovie(string path)
        {
            if (!IsConnected) return null;
            try
            {
                var r = await _engine.ExportMovie(path);
                bool ok = !r.TryGetProperty("ok", out var okEl) || okEl.ValueKind != JsonValueKind.False;
                return ok ? path : null;
            }
            catch (EngineException ex) { Say("导出失败：" + ex.Message); return null; }
        }

        /// <summary>
        /// 把当前 deck 打成可移植包（文件夹或 .zip）。引擎在后台线程执行，
        /// 结果 manifest 通过 {"event":"package","phase":"done","manifest":...} 事件回传。
        /// </summary>
        public async Task<JsonElement> PackageProject(string path, bool includeMedia, int timeoutMs = 900000)
        {
            if (!IsConnected) throw new EngineException("引擎未连接");

            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

            void Handler(JsonElement ev)
            {
                if (GetStr(ev, "event") != "package") return;
                string phase = GetStr(ev, "phase");
                if (phase == "scanning") { Say("正在扫描依赖关系…"); return; }
                if (phase == "done")
                {
                    tcs.TrySetResult(ev.TryGetProperty("manifest", out var m) ? m.Clone() : default);
                    return;
                }
                if (phase == "error")
                {
                    tcs.TrySetException(new EngineException(GetStr(ev, "error") ?? "打包失败"));
                    return;
                }
                if (phase == "cancelled")
                    tcs.TrySetResult(default);
            }

            _engine.EventReceived += Handler;
            try
            {
                var accepted = await _engine.Package(path, includeMedia, timeoutMs);
                if (accepted.ValueKind == JsonValueKind.Object
                    && accepted.TryGetProperty("ok", out var okEl)
                    && okEl.ValueKind == JsonValueKind.False)
                    throw new EngineException(GetStr(accepted, "error") ?? "引擎拒绝打包请求");

                var finished = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
                return finished == tcs.Task ? await tcs.Task : default;
            }
            finally
            {
                _engine.EventReceived -= Handler;
            }
        }

        public void Shutdown()
        {
            try { _engine?.Dispose(); } catch { }
            _engine = null;
            StatusText = "已断开";
        }

        #region 通用编辑能力（撤销 / 复制粘贴 / 组合对齐 / 查找替换）

        /// <summary>
        /// 透传一条协议请求。参数名必须与 rpc.py / core.py 的 req.get(...) 完全一致，
        /// 因此这里只做转发，不猜字段——调用方按引擎源码填。
        /// </summary>
        public Task<JsonElement> Request(string cmd, object args = null, int timeoutMs = 120000)
        {
            if (!IsConnected) throw new EngineException("引擎未连接");
            return _engine.Request(cmd, args, timeoutMs);
        }

        public Task<JsonElement> Undo() => Request("undo");
        public Task<JsonElement> Redo() => Request("redo");

        /// <summary>撤销历史的当前位置（整数），与 UndoMerge 配对，把一串单对象编辑折成一步。</summary>
        public Task<JsonElement> UndoMark() => Request("undo_mark");

        public Task<JsonElement> UndoMerge(long mark, string label)
            => Request("undo_merge", new { mark, label });

        /// <summary>
        /// 解析 undo_mark 的响应。
        /// 为什么单独拎出来：引擎的 _undo_seq 从 0 开始，<c>{"mark": 0}</c> 是**合法**标记，
        /// 拿 0 当「没有标记」会让连引擎后的第一次多对象操作无法合并；只有拿不到数字才算没有标记。
        /// </summary>
        public static long? ParseUndoMark(JsonElement r)
        {
            if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("mark", out var m) && m.TryGetInt64(out long v))
                return v;
            return null;
        }

        /// <summary>
        /// 层级调整的请求参数。必须带 stop：引擎 _plan_order 按 stop 取该 build 的 sprites，
        /// 缺省当 build 0，在第二、三个 build 上会报 "the selection is not drawn in this build"。
        /// </summary>
        public static Dictionary<string, object> OrderArgs(IReadOnlyList<string> oids, string how, int stop)
            => new Dictionary<string, object>
            {
                ["oid"] = oids != null && oids.Count > 0 ? oids[0] : "",
                ["oids"] = oids != null ? oids.ToArray() : new string[0],
                ["value"] = how,
                ["stop"] = stop
            };

        public Task<JsonElement> EditOrder(IReadOnlyList<string> oids, string how, int stop)
            => Request("edit", new Dictionary<string, object>(OrderArgs(oids, how, stop))
            {
                ["scene"] = _vm.CurrentSlide?.Name ?? ""
            });

        /// <summary>把选中对象交给引擎做一份可粘贴的清单（code / at / box / text / pictures）。</summary>
        public Task<JsonElement> CopyObjects(string scene, int stop, IEnumerable<string> oids)
            => Request("copy", new { scene, stop, oids = oids.ToArray() });

        /// <summary>一次插入多个元素，引擎保证它们合成一步撤销。</summary>
        public Task<JsonElement> InsertMany(string scene, int stop, IEnumerable<object> items)
            => Request("insert_many", new { scene, stop, items = items.ToArray() });

        /// <summary>
        /// 替换某个图片对象引用的图片文件。走 insert_many 顶层的 replace_image 字段
        /// （core.py:3271-3276）：不用插新元素，只把该 oid 的 ImageMobject 路径换掉，
        /// 引擎进同一份撤销历史。file 必须是相对 deck 的路径。返回原始响应由调用方判 ok。
        /// </summary>
        public Task<JsonElement> ReplaceImage(string scene, string oid, string relativeFile)
            => Request("insert_many", new
            {
                scene,
                stop = 0,
                items = Array.Empty<object>(),
                replace_image = new { oid, file = relativeFile }
            });

        /// <summary>搜索 BioCartoon 图标目录。返回原始响应（含 ok/items/error/source）。</summary>
        public Task<JsonElement> BioSearch(string query, int limit = 24, object filters = null)
            => Request("bio_search", new { query, limit, filters = filters ?? new { } }, 150000);

        /// <summary>把一个图标的 SVG 或 PNG 下载到绝对路径 outPath。</summary>
        public Task<JsonElement> BioFetch(string id, string outPath, string kind = "svg")
            => Request("bio_fetch", new { icon = id, @out = outPath, kind }, 150000);

        /// <summary>执行用户 Python 绘图代码，输出 PNG 到绝对路径 outPath。</summary>
        public Task<JsonElement> PythonPlot(string code, string outPath, string cwd = null)
            => Request("python_plot", new { code, @out = outPath, cwd = cwd ?? "" }, 90000);

        /// <summary>列出 deck 中所有被引用的图片文件（只读，不改源码）。</summary>
        public Task<JsonElement> ExtractImagesList(string scene = null)
            => Request("extract_images", string.IsNullOrEmpty(scene)
                ? (object)new { op = "list" }
                : new { op = "list", scene }, 120000);

        /// <summary>把 deck 中的图片复制到指定目录（只读源码，只做文件复制）。</summary>
        public Task<JsonElement> ExtractImagesSave(string dir, string scene = null)
            => Request("extract_images", string.IsNullOrEmpty(scene)
                ? (object)new { op = "save", dir }
                : new { op = "save", dir, scene }, 120000);

        /// <summary>单对象编辑（op + 该 op 需要的字段）。</summary>
        public Task<JsonElement> Edit(string scene, string op, IDictionary<string, object> args)
        {
            var all = new Dictionary<string, object> { ["scene"] = scene, ["op"] = op };
            if (args != null)
                foreach (var kv in args) all[kv.Key] = kv.Value;
            return Request("edit", all);
        }

        /// <summary>全 deck 查找（对象文本与公式，可选备注）。</summary>
        public Task<JsonElement> Find(string query, bool matchCase, bool wholeWord, bool regex, bool notes)
            => Request("find", new { query, @case = matchCase, word = wholeWord, regex, notes });

        /// <summary>替换一处（all=false）或全部（all=true）。</summary>
        public Task<JsonElement> Replace(string query, string replacement, bool matchCase, bool wholeWord,
                                         bool regex, bool notes, bool all, IDictionary<string, object> target)
        {
            var all2 = new Dictionary<string, object>
            {
                ["query"] = query, ["replacement"] = replacement,
                ["case"] = matchCase, ["word"] = wholeWord, ["regex"] = regex,
                ["notes"] = notes, ["all"] = all
            };
            if (target != null)
                foreach (var kv in target) all2[kv.Key] = kv.Value;
            return Request("replace", all2, 300000);
        }

        /// <summary>deck 主题信息（theme.py 的 op=get）：deck 当前主题名、本页自己的主题、切到 themeId 是否跨明暗。</summary>
        public Task<JsonElement> ThemeGet(string themeId, string current)
        {
            var args = new Dictionary<string, object> { ["op"] = "get" };
            if (!string.IsNullOrEmpty(themeId)) args["theme"] = themeId;
            if (!string.IsNullOrEmpty(current)) args["current"] = current;
            return Request("theme", args, 120000);
        }

        /// <summary>把某页按 themeId 渲染成一张 PNG（op=preview），不动 deck。返回 {ok, png}。</summary>
        public Task<JsonElement> ThemePreview(string themeId, string scene, bool slideOnly, bool flip)
            => Request("theme", new { op = "preview", theme = themeId, scene, slide_only = slideOnly, flip }, 600000);

        /// <summary>
        /// 应用主题（不传 op）。引擎侧是**一步撤销**：整份 deck 会重写 common.py/manim.cfg 的常量并重渲染，
        /// 所以超时给到 30 分钟；slide_only 只改当前页，flip 由引擎自己判断要不要顺带写 darkmode。
        /// </summary>
        public Task<JsonElement> ThemeApply(string themeId, string current, bool slideOnly, bool flip)
            => Request("theme", new { theme = themeId, current, slide_only = slideOnly, flip }, 1800000);

        /// <summary>把深色（dark）或反色（invert）映射写进 deck 源码，一步撤销。引擎会重渲染每一页，故超时放宽。</summary>
        public Task<JsonElement> DarkMode(string current, string mode)
            => Request("darkmode", new { current, mode }, 1800000);

        /// <summary>读取某页背景（mode / color / image / transparency / default）。</summary>
        public Task<JsonElement> BackgroundGet(string scene)
            => Request("background", new { op = "get", scene }, 120000);

        /// <summary>设置背景：mode=color 时用 color + transparency；mode=image 时用相对 deck 的 file；all 作用于全部页。</summary>
        public Task<JsonElement> BackgroundSet(string scene, string mode, string color, double transparency, bool all, string file = null)
        {
            var args = new Dictionary<string, object>
            {
                ["op"] = "set", ["scene"] = scene, ["mode"] = mode,
                ["transparency"] = transparency, ["all"] = all
            };
            if (mode == "image") args["file"] = file ?? "";
            else args["color"] = color ?? "#000000";
            return Request("background", args, 1800000);
        }

        /// <summary>清除背景（回到 manim.cfg 的默认色），all 作用于全部页。</summary>
        public Task<JsonElement> BackgroundReset(string scene, bool all)
            => Request("background", new { op = "reset", scene, all }, 1800000);

        /// <summary>页码：get 读选项；set 写全部页；remove 移除；spread 把某页的样式扩散到所有带页码的页。</summary>
        public Task<JsonElement> PageNumbersGet()
            => Request("page_numbers", new { op = "get" }, 120000);

        public Task<JsonElement> PageNumbersSet(string pos, int start, bool skip, bool total, int size, bool sub)
            => Request("page_numbers", new
            {
                op = "set", pos, start, skip, total, size, sub
            }, 1800000);

        public Task<JsonElement> PageNumbers(string op, string scene = null)
        {
            var args = new Dictionary<string, object> { ["op"] = op };
            if (!string.IsNullOrEmpty(scene)) args["scene"] = scene;
            return Request("page_numbers", args, 1800000);
        }

        /// <summary>重新读取某页（引擎改动后刷新画布与源码）。</summary>
        public async Task Refresh(Slide slide)
        {
            if (!IsConnected || slide == null) return;
            await LoadSceneInto(slide);
            _vm.RaiseCurrentChanged();
        }

        /// <summary>引擎里的 deck 路径（判断剪贴板是不是「同一个 deck」）。</summary>
        public string DeckPath => _deckPath;

        #endregion

        #region Deck 生命周期 / 导入导出 / LaTeX 编排

        // 统一沿用 PackageProject 的模式：临时订阅 EventReceived + TCS 等最终事件 + 超时兜底。
        // 区别：import/export/export_movie 的结果走事件（RPC 只回 ack），latex_install 的结果走响应。

        /// <summary>等待渲染队列排空（写回 / 还原 / 导出一致性原则：先 settle 再读文件）。</summary>
        public async Task SettleAsync()
        {
            if (!IsConnected) return;
            try { await _engine.Settle(); }
            catch (EngineException ex) { Say("等待引擎排空失败：" + ex.Message); }
        }

        /// <summary>清空引擎撤销/重做历史（Revert 之后调用）。</summary>
        public async Task ClearHistoryAsync()
        {
            if (!IsConnected) return;
            try { await _engine.ClearHistory(); }
            catch (EngineException ex) { Say("清空撤销历史失败：" + ex.Message); }
        }

        /// <summary>
        /// 引擎撤销栈是否非空（<c>state</c> 的 <c>canUndo</c>）。
        /// 还原之后应当是 false —— 用它确认 <c>clear_history</c> 真的生效（诊断与自测用）。
        /// </summary>
        public async Task<bool> EngineCanUndoAsync()
        {
            if (!IsConnected) return false;
            try
            {
                JsonElement st = await _engine.Request("state");
                if (st.ValueKind != JsonValueKind.Object) return false;
                return st.TryGetProperty("canUndo", out var el) && el.ValueKind == JsonValueKind.True;
            }
            catch (EngineException ex)
            {
                Say("读取撤销状态失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 导入 PPTX / Beamer。
        /// 进度（building 的 done/total/scene）通过 <paramref name="progress"/> 回传；
        /// 返回 <c>done</c> 事件本身（含 <c>deck</c> / <c>slides</c> / <c>warnings</c>）；
        /// 用户取消返回 <c>default</c>；失败抛 <see cref="EngineException"/>。
        /// </summary>
        public async Task<JsonElement> ImportDeckAsync(ImportRequest req, Action<int, int, string> progress = null,
                                                      int timeoutMs = 3600000)
        {
            if (req == null) throw new ArgumentNullException(nameof(req));
            if (!IsConnected) throw new EngineException("引擎未连接");

            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

            void Handler(JsonElement ev)
            {
                if (GetStr(ev, "event") != "import") return;
                string phase = GetStr(ev, "phase");
                if (phase == "building")
                {
                    progress?.Invoke(GetInt(ev, "done", 0), GetInt(ev, "total", 0), GetStr(ev, "scene") ?? "");
                    return;
                }
                if (phase == "done") { tcs.TrySetResult(ev.Clone()); return; }
                if (phase == "error") { tcs.TrySetException(new EngineException(GetStr(ev, "error") ?? "导入失败")); return; }
                if (phase == "cancelled") tcs.TrySetResult(default);
            }

            _engine.EventReceived += Handler;
            try
            {
                // dest 必须是「项目根」（<DestParent>/<DeckName>）：引擎会在它下面建 deck/ 与 assets/
                string dest = req.DestPath ?? req.DestParent;
                var accepted = await _engine.Import(req.SourcePath, dest);
                if (accepted.ValueKind == JsonValueKind.Object
                    && accepted.TryGetProperty("ok", out var okEl)
                    && okEl.ValueKind == JsonValueKind.False)
                    throw new EngineException(GetStr(accepted, "error") ?? "引擎拒绝了导入请求");

                var finished = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
                if (finished != tcs.Task) { Say("等待导入完成超时"); return default; }
                return await tcs.Task;
            }
            finally
            {
                _engine.EventReceived -= Handler;
            }
        }

        /// <summary>
        /// 导出 PPTX / Beamer（<c>export</c> 事件：building 进度 → done/error/cancelled）。
        /// 注意 <c>done</c> 里的 <c>pdfError</c> 不是失败标志。
        /// </summary>
        public async Task<JsonElement> ExportDeckAsync(ExportRequest req, Action<int, int, string> progress = null,
                                                      int timeoutMs = 3600000)
        {
            if (req == null) throw new ArgumentNullException(nameof(req));
            string kind = EngineExportKind(req.Kind);
            if (!IsConnected) throw new EngineException("引擎未连接");

            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

            void Handler(JsonElement ev)
            {
                if (GetStr(ev, "event") != "export") return;
                string phase = GetStr(ev, "phase");
                if (phase == "building")
                {
                    progress?.Invoke(GetInt(ev, "done", 0), GetInt(ev, "total", 0), GetStr(ev, "scene") ?? "");
                    return;
                }
                if (phase == "keynote") { Say("正在转换为 Keynote…"); return; }
                if (phase == "done") { tcs.TrySetResult(ev.Clone()); return; }
                if (phase == "error") { tcs.TrySetException(new EngineException(GetStr(ev, "error") ?? "导出失败")); return; }
                if (phase == "cancelled") tcs.TrySetResult(default);
            }

            _engine.EventReceived += Handler;
            try
            {
                var accepted = await _engine.Export(kind, req.OutPath, req.Builds, req.Editable);
                if (accepted.ValueKind == JsonValueKind.Object
                    && accepted.TryGetProperty("ok", out var okEl)
                    && okEl.ValueKind == JsonValueKind.False)
                    throw new EngineException(GetStr(accepted, "error") ?? "引擎拒绝了导出请求");

                var finished = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
                if (finished != tcs.Task) { Say("等待导出完成超时"); return default; }
                return await tcs.Task;
            }
            finally
            {
                _engine.EventReceived -= Handler;
            }
        }

        /// <summary>
        /// 导出 MP4（<c>export_movie</c> 事件：encoding 进度 → done/error/cancelled）。
        /// 与上面的旧 <see cref="ExportMovie(string)"/> 不同：这个等事件、有进度、可取消，且能选范围/分辨率/hold/fps。
        /// </summary>
        public async Task<JsonElement> ExportMovieAsync(ExportRequest req, Action<int, int, string> progress = null,
                                                       int timeoutMs = 7200000)
        {
            if (req == null) throw new ArgumentNullException(nameof(req));
            if (!IsConnected) throw new EngineException("引擎未连接");

            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

            void Handler(JsonElement ev)
            {
                if (GetStr(ev, "event") != "export_movie") return;
                string phase = GetStr(ev, "phase");
                if (phase == "encoding")
                {
                    progress?.Invoke(GetInt(ev, "done", 0), GetInt(ev, "total", 0), GetStr(ev, "scene") ?? "");
                    return;
                }
                if (phase == "done") { tcs.TrySetResult(ev.Clone()); return; }
                if (phase == "error") { tcs.TrySetException(new EngineException(GetStr(ev, "error") ?? "导出影片失败")); return; }
                if (phase == "cancelled") tcs.TrySetResult(default);
            }

            _engine.EventReceived += Handler;
            try
            {
                var accepted = await _engine.ExportMovie(req.OutPath, req.Scenes, req.Height, req.Hold, req.Fps);
                if (accepted.ValueKind == JsonValueKind.Object
                    && accepted.TryGetProperty("ok", out var okEl)
                    && okEl.ValueKind == JsonValueKind.False)
                    throw new EngineException(GetStr(accepted, "error") ?? "引擎拒绝了导出影片请求");

                var finished = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
                if (finished != tcs.Task) { Say("等待导出影片完成超时"); return default; }
                return await tcs.Task;
            }
            finally
            {
                _engine.EventReceived -= Handler;
            }
        }

        /// <summary>导出单个 .py（结果随响应返回，不走事件）。</summary>
        public async Task<JsonElement> ExportPythonAsync(string path)
        {
            if (!IsConnected) throw new EngineException("引擎未连接");
            var r = await _engine.ExportPython(path);
            if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.False)
                throw new EngineException(GetStr(r, "error") ?? "导出 Python 失败");
            return r;
        }

        /// <summary>
        /// 预扫描一个 .tex 需要但本机缺的宏包。返回 <c>{missing:[...], tlmgr:bool}</c>。
        /// Windows 一般没有 tlmgr，前端据此给 MiKTeX 的手工引导。
        /// </summary>
        public Task<JsonElement> LatexMissingAsync(string src)
        {
            if (!IsConnected) throw new EngineException("引擎未连接");
            return _engine.LatexMissing(src);
        }

        /// <summary>当前 deck 仍缺的宏包与仍是占位图的图片数（必须在 deck 已 open 后调用）。</summary>
        public Task<JsonElement> LatexStatusAsync()
        {
            if (!IsConnected) throw new EngineException("引擎未连接");
            return _engine.LatexStatus();
        }

        /// <summary>
        /// 安装缺包：进度走 <c>latex_install</c> 事件（done/total/step），最终结果随响应返回。
        /// 返回值里可能有 <c>ok=false</c> 与 <c>error</c>，由调用方判断并给 MiKTeX 引导。
        /// </summary>
        public async Task<JsonElement> LatexInstallAsync(string[] files, Action<int, int, string> progress = null)
        {
            if (!IsConnected) throw new EngineException("引擎未连接");

            void Handler(JsonElement ev)
            {
                if (GetStr(ev, "event") != "latex_install") return;
                progress?.Invoke(GetInt(ev, "done", 0), GetInt(ev, "total", 0), GetStr(ev, "step") ?? "");
            }

            _engine.EventReceived += Handler;
            try
            {
                return await _engine.LatexInstall(files ?? Array.Empty<string>());
            }
            finally
            {
                _engine.EventReceived -= Handler;
            }
        }

        /// <summary>把 assets 里的占位图按缺包重排（会改写 assets，故其后必须能让 Save 写回）。</summary>
        public async Task<JsonElement> LatexRetypesetAsync()
        {
            if (!IsConnected) throw new EngineException("引擎未连接");
            var r = await _engine.LatexRetypeset();
            if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.False)
                throw new EngineException(GetStr(r, "error") ?? "重排占位图失败");
            return r;
        }

        /// <summary>
        /// 还原到原始文件：<c>settle</c> → 调 <paramref name="revertFiles"/> 按 baseline 恢复副本
        /// → <c>clear_history</c> 清撤销栈（不留从旧版本出发的撤销步）→ 重载场景与当前页。
        /// </summary>
        /// <param name="revertFiles">恢复文件的工作（一般是 <c>WorkspaceService.Revert</c>）；返回 false 表示中止。</param>
        public async Task<bool> RevertEngineAsync(Func<bool> revertFiles = null)
        {
            if (!IsConnected)
            {
                Say("引擎未连接，无法还原");
                return false;
            }
            try
            {
                await _engine.Settle();
                if (revertFiles != null && !revertFiles())
                {
                    Say("还原文件失败，已中止");
                    return false;
                }
                await _engine.ClearHistory();
                await ReloadSlides();
                if (_vm.CurrentSlide != null && EngineScenes.Contains(_vm.CurrentSlide.Name))
                    await LoadSceneInto(_vm.CurrentSlide);
                int index = _vm.CurrentSlide != null ? _vm.Slides.IndexOf(_vm.CurrentSlide) : -1;
                _vm.SelectSlideByIndex(index);
                Say("已还原到原始文件");
                return true;
            }
            catch (EngineException ex)
            {
                Say("还原失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>busy pill 的取消：停下正在跑的 import / build / export / 编辑。</summary>
        public async Task CancelBusy()
        {
            if (!IsConnected) return;
            try
            {
                await _engine.Cancel();
                Say("已请求取消当前作业");
            }
            catch (EngineException ex)
            {
                Say("取消失败：" + ex.Message);
            }
        }

        /// <summary><see cref="ExportKind"/> → 引擎 <c>export</c> 的 kind。Movie/Python 不是 export 的 kind。</summary>
        private static string EngineExportKind(ExportKind kind)
        {
            switch (kind)
            {
                case ExportKind.Pptx: return "pptx";
                case ExportKind.Beamer: return "beamer";
                case ExportKind.KeynoteDisabled: return "keynote";
                case ExportKind.Movie:
                    throw new EngineException("导出 MP4 请用 ExportMovieAsync");
                case ExportKind.Python:
                    throw new EngineException("导出 Python 请用 ExportPythonAsync");
                default:
                    throw new EngineException("未知的导出类型：" + kind);
            }
        }

        #endregion

        #region JSON helpers
        private static string GetStr(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        private static int GetInt(JsonElement e, string name, int def)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt32(out int i) ? i : def;
        /// <summary>
        /// 读一个可选数字。必须先判 ValueKind == Number：.NET 8 里 <c>JsonElement.TryGetDouble</c> 对
        /// null / 字符串元素**会抛 InvalidOperationException**（TryGetInt32 不会），而引擎的
        /// <c>"fontSize": null</c>（矩形、圆、线这类对象没有字号）正好踩这条，会让整页对象都载入失败。
        /// </summary>
        private static double GetDouble(JsonElement e, string name, double def)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
               && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) ? d : def;
        private static bool GetBool(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        private static ObjectKind MapKind(string k)
        {
            switch (k)
            {
                case "tex": return ObjectKind.Tex;
                case "image": return ObjectKind.Image;
                case "line": return ObjectKind.Line;
                case "shape": return ObjectKind.Shape;
                case "table": return ObjectKind.Table;
                default: return ObjectKind.Text;
            }
        }

        private static string NormalizeColor(string c)
        {
            if (string.IsNullOrEmpty(c)) return "#FFFFFF";
            if (c.StartsWith("#")) return c.Length >= 7 ? c.Substring(0, 7) : "#FFFFFF";
            // 颜色名近似映射
            switch (c.ToLowerInvariant())
            {
                case "white": return "#FFFFFF";
                case "black": return "#000000";
                case "grey_b": case "gray_b": return "#9AA0A6";
                case "yellow": return "#FFFF00";
                case "blue": return "#0C8CFF";
                case "red": return "#FF4B4B";
                default: return "#FFFFFF";
            }
        }

        private static string MakeDisplay(string value, string kind)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (kind == "tex") return value; // 公式显示 LaTeX 源码，真实渲染用 SpritePath
            return value;
        }

        private static int ParseLine(string loc)
        {
            if (string.IsNullOrEmpty(loc)) return 1;
            var parts = loc.Split(':');
            if (parts.Length >= 2 && int.TryParse(parts.Last(), out int n)) return n;
            return 1;
        }
        #endregion
    }
}
