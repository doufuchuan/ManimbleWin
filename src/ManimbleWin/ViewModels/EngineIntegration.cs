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

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    _vm.Slides.Clear();
                    foreach (var s in newSlides) _vm.Slides.Add(s);
                    if (_vm.Slides.Count > 0) _vm.CurrentSlide = _vm.Slides[0];
                });

                // 加载当前场景的渲染数据
                if (_vm.CurrentSlide != null) await LoadSceneInto(_vm.CurrentSlide);
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

                // 备注
                if (data.TryGetProperty("notes", out var notes) && slide.Builds.Count > 0)
                    slide.Builds[0].Notes = notes.GetString() ?? "";

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

        public void Shutdown()
        {
            try { _engine?.Dispose(); } catch { }
            _engine = null;
            StatusText = "已断开";
        }

        #region JSON helpers
        private static string GetStr(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        private static int GetInt(JsonElement e, string name, int def)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt32(out int i) ? i : def;
        private static double GetDouble(JsonElement e, string name, double def)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetDouble(out double d) ? d : def;
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
