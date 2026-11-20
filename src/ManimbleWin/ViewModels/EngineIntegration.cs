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

        /// <summary>引擎 deck 里真实存在的场景名，用来判断某页能否写回引擎。</summary>
        public HashSet<string> EngineScenes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public EngineIntegration(MainViewModel vm)
        {
            _vm = vm;
        }

        private void Say(string msg) => Log?.Invoke(msg);

        /// <summary>连接到真实引擎并加载示例 deck。返回是否成功。</summary>
        public async Task<bool> ConnectAndLoad(string deckPath = null)
        {
            _deckPath = deckPath ?? EnvironmentService.SampleDeck;
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
                // cache 目录放在用户数据目录，长期保留
                string cache = Path.Combine(EnvironmentService.UserDataDir, "render-cache");
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
            // 每个 build 的可见对象列表
            var visibleSets = new List<HashSet<int>>();
            foreach (var st in stops)
            {
                var set = new HashSet<int>();
                if (st.TryGetProperty("visible", out var vis) && vis.ValueKind == JsonValueKind.Array)
                    foreach (var v in vis.EnumerateArray())
                        if (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0 && v[0].TryGetInt32(out int oid))
                            set.Add(oid);
                visibleSets.Add(set);
            }
            if (visibleSets.Count == 0) visibleSets.Add(new HashSet<int>());

            Application.Current.Dispatcher.Invoke(() =>
            {
                for (int bi = 0; bi < slide.Builds.Count; bi++)
                {
                    var build = slide.Builds[bi];
                    var visible = bi < visibleSets.Count ? visibleSets[bi] : visibleSets[0];
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
                        if (co.Kind == ObjectKind.Tex)
                        {
                            // 稍后异步请求 sprite 真实渲染
                            _ = RenderSpriteFor(co);
                        }
                        if (co.Kind == ObjectKind.Image)
                        {
                            // 引擎 scene 结果对 image 返回 image(文件名) 与 imagePath(绝对路径)，
                            // 让 CanvasView 的 PathToImage 直接显示真实缩略图。
                            co.ImagePath = GetStr(o, "imagePath") ?? GetStr(o, "image") ?? "";
                            co.ImageLabel = GetStr(o, "image") ?? co.ImageLabel;
                        }
                        build.Objects.Add(co);
                    }
                }
            });
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
                    Say("源码写回被拒绝：" + err);
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
