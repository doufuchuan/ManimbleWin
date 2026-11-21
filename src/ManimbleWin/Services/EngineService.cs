using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ManimbleWin.Services
{
    /// <summary>
    /// Manimble Python 引擎客户端。启动 <c>python -m manimble.rpc</c> 子进程，
    /// 通过 JSON-lines 走 stdin/stdout 通信（stderr 为日志），对齐 docs/developers/protocol.md。
    /// </summary>
    public sealed class EngineService : IDisposable
    {
        private Process _process;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending =
            new ConcurrentDictionary<int, TaskCompletionSource<JsonElement>>();
        private int _nextId = 1;
        private readonly object _sendLock = new object();
        private CancellationTokenSource _cts;
        private Task _readTask;

        public bool IsRunning => _process != null && !_process.HasExited;
        public event Action<JsonElement> EventReceived;   // 引擎推送事件（rendered / export / present …）
        public event Action<string> LogReceived;          // stderr 日志
        public event Action Exited;                        // 进程意外退出

        private readonly string _python;
        private readonly string _engineDir;
        private readonly string _pythonForScenes;

        public EngineService(string python, string engineDir, string pythonForScenes = null)
        {
            _python = python;
            _engineDir = engineDir;
            _pythonForScenes = pythonForScenes ?? python;
        }

        /// <summary>启动引擎子进程。</summary>
        public bool Start()
        {
            if (IsRunning) return true;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _python,
                    Arguments = "-u -m manimble.rpc",
                    WorkingDirectory = _engineDir,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                // 先统一清理宿主环境（TEMP 可能不可写，PYTHONPATH 可能指向外部的
                // sitecustomize 钩子，引擎渲染时会删中间文件，被钩子接管会直接失败）。
                EnvironmentService.ApplyCleanTemp(psi);
                // 引擎要求：PYTHONPATH 只指向 engine/，MANIMBLE_PYTHON 指向有 manim 的解释器。
                psi.Environment["PYTHONPATH"] = _engineDir;
                psi.Environment["MANIMBLE_PYTHON"] = _pythonForScenes;
                psi.Environment["PYTHONIOENCODING"] = "utf-8";

                _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _process.Exited += (s, e) => { Exited?.Invoke(); };
                if (!_process.Start()) return false;

                _cts = new CancellationTokenSource();
                _readTask = Task.Run(ReadLoop);
                // 抽取 stderr（日志），避免管道写满阻塞
                Task.Run(() =>
                {
                    try
                    {
                        string line;
                        while ((line = _process.StandardError.ReadLine()) != null)
                            LogReceived?.Invoke(line);
                    }
                    catch { }
                });
                return true;
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke("引擎启动失败：" + ex.Message);
                return false;
            }
        }

        private void ReadLoop()
        {
            try
            {
                string line;
                while ((line = _process.StandardOutput.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    JsonElement obj;
                    try { obj = JsonDocument.Parse(line).RootElement.Clone(); }
                    catch { continue; }

                    if (obj.TryGetProperty("event", out _))
                    {
                        EventReceived?.Invoke(obj);
                        continue;
                    }
                    if (obj.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out int id))
                    {
                        if (_pending.TryRemove(id, out var tcs))
                        {
                            if (obj.TryGetProperty("error", out var errEl))
                                tcs.TrySetException(new EngineException(errEl.GetString() ?? "engine error"));
                            else if (obj.TryGetProperty("result", out var resEl))
                                tcs.TrySetResult(resEl);
                            else
                                tcs.TrySetResult(obj);
                        }
                    }
                }
            }
            catch (Exception) { }
            finally
            {
                // 把所有等待中的请求标记为失败，避免死锁
                foreach (var kv in _pending)
                    if (_pending.TryRemove(kv.Key, out var tcs))
                        tcs.TrySetException(new EngineException("引擎已退出"));
            }
        }

        /// <summary>发送一个请求并等待响应。</summary>
        public async Task<JsonElement> Request(string cmd, object args = null, int timeoutMs = 120000)
        {
            if (!IsRunning)
                throw new EngineException("引擎未运行");

            int id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;

            // 用 JsonElement 直接构造，避免 object 往返导致的类型歧义
            var payload = new System.Collections.Generic.Dictionary<string, JsonElement>
            {
                ["id"] = JsonSerializer.SerializeToElement(id),
                ["cmd"] = JsonSerializer.SerializeToElement(cmd)
            };
            if (args != null)
            {
                foreach (var prop in JsonSerializer.SerializeToElement(args).EnumerateObject())
                    payload[prop.Name] = prop.Value.Clone();
            }
            string line = JsonSerializer.Serialize(payload);
            lock (_sendLock)
            {
                _process.StandardInput.WriteLine(line);
                _process.StandardInput.Flush();
            }

            var timeout = Task.Delay(timeoutMs);
            var finished = await Task.WhenAny(tcs.Task, timeout);
            if (finished == timeout)
            {
                _pending.TryRemove(id, out _);
                throw new EngineException($"引擎请求超时：{cmd}");
            }
            return await tcs.Task;
        }

        /// <summary>打开 deck（引擎会先完成 open 再处理后续请求）。</summary>
        public Task<JsonElement> OpenDeck(string deckFolder, string cacheFolder = null)
        {
            var args = new System.Collections.Generic.Dictionary<string, object> { ["deck"] = deckFolder };
            if (!string.IsNullOrEmpty(cacheFolder)) args["cache"] = cacheFolder;
            return Request("open", args, 60000);
        }

        /// <summary>获取状态：场景列表、当前 slide 各 build 的帧 PNG 路径与对象元数据。</summary>
        public Task<JsonElement> State(string current = null)
            => Request("state", current == null ? null : new { current }, 120000);

        /// <summary>渲染并获取某个场景（build）的完整数据，含 png / bg / sprites / objects。</summary>
        public Task<JsonElement> Scene(string name)
            => Request("scene", new { name }, 180000);

        /// <summary>渲染一个文本/公式为透明 PNG（LaTeX 公式渲染入口）。kind: text|tex|code。</summary>
        public Task<JsonElement> Sprite(string kind, string value, int fontSize = 48, string color = null, string font = null)
        {
            var args = new System.Collections.Generic.Dictionary<string, object>
            {
                ["kind"] = kind, ["value"] = value, ["font_size"] = fontSize
            };
            if (color != null) args["color"] = color;
            if (font != null) args["font"] = font;
            return Request("sprite", args, 120000);
        }

        /// <summary>读取某个场景的精确源码快照（edit_source 冲突校验用的同一份文本）。</summary>
        public Task<JsonElement> Source(string scene)
            => Request("source", new { scene }, 20000);

        /// <summary>把一段 Python 源码写回引擎工作副本并触发重渲染。expected 为编辑开始时的原文（用于冲突检测）。</summary>
        public Task<JsonElement> EditSource(string scene, string file, string expected, string text)
            => Request("edit_source", new { scene, file, expected, text }, 180000);

        /// <summary>向当前场景插入一个对象（走引擎，自动写回 Python 并重渲染）。</summary>
        public Task<JsonElement> Insert(string scene, int stop, string kind, object extra = null)
        {
            var args = new System.Collections.Generic.Dictionary<string, object>
            {
                ["scene"] = scene, ["stop"] = stop, ["kind"] = kind
            };
            if (extra != null)
                foreach (var p in JsonSerializer.SerializeToElement(extra).EnumerateObject())
                    args[p.Name] = JsonSerializer.Deserialize<object>(p.Value.GetRawText());
            return Request("insert", args, 180000);
        }

        /// <summary>构建并发送演示/动画预览；播放片段通过 present 事件回传。</summary>
        public Task<JsonElement> Present(string scene = null, int height = 720)
            => Request("present", scene == null ? new { height } : new { scene, height }, 180000);

        /// <summary>
        /// 演讲者备注。op=get 读文本；op=set 写回 next_slide(notes=...) 并重渲染。
        /// stop 非空时读写「某个 build 的备注」（引擎会转给 build_notes）。
        /// </summary>
        public Task<JsonElement> Notes(string scene, string op, string text = null, int? stop = null)
        {
            var args = new System.Collections.Generic.Dictionary<string, object>
            {
                ["scene"] = scene, ["op"] = op, ["text"] = text ?? "", ["stop"] = stop
            };
            return Request("notes", args, 180000);
        }

        /// <summary>幻灯片替代文本：op=get 读，op=set 写（不触发重渲染）。</summary>
        public Task<JsonElement> AltText(string scene, string op, string text = null)
            => Request("alt_text", new { scene, op, text = text ?? "" }, 120000);

        /// <summary>演讲者视图静帧：返回每页的 PNG 路径、备注与逐 build 备注，只等当前及后 ahead-1 页。</summary>
        public Task<JsonElement> DeckPages(string current, int ahead)
            => Request("deck_pages", new { current, ahead }, 180000);

        /// <summary>导出影片（MP4）。进度通过 export_movie 事件回传。</summary>
        public Task<JsonElement> ExportMovie(string path, string[] scenes = null, int height = 720, double hold = 2, int fps = 30)
        {
            var args = new System.Collections.Generic.Dictionary<string, object>
            {
                ["path"] = path, ["height"] = height, ["hold"] = hold, ["fps"] = fps
            };
            if (scenes != null) args["scenes"] = scenes;
            return Request("export_movie", args, 600000);
        }

        /// <summary>打包当前 deck 为可移植包。返回的是受理 ack，真正的 manifest 由 package 事件回传。</summary>
        public Task<JsonElement> Package(string path, bool includeMedia, int timeoutMs = 900000)
            => Request("package", new System.Collections.Generic.Dictionary<string, object>
            {
                ["path"] = path, ["include_media"] = includeMedia
            }, timeoutMs);

        #region Deck 生命周期 / 导入导出 / LaTeX（薄封装）

        // 参数名严格照抄 engine/manimble/rpc.py 与 core.py 的 req.get(...)/a["..."]（大小写、下划线）。

        /// <summary>
        /// 导入 PPTX / Beamer。返回的是受理 ack，真正的进度与结果由 <c>import</c> 事件回传
        /// （rpc.py:110-135：building / done / error / cancelled）。
        /// 导入阶段 deck 还没 open，取消走 <c>cancel</c> 命令里的 <c>cancel_import</c>（rpc.py:53）。
        /// </summary>
        public Task<JsonElement> Import(string src, string dest)
            => Request("import", new { src, dest }, 60000);

        /// <summary>
        /// 导出 PPTX / Beamer。kind 只有 <c>pptx</c>/<c>beamer</c>/<c>keynote</c>（core.py:4083-4094）。
        /// 返回 ack，结果走 <c>export</c> 事件；事件里的 <c>pdfError</c> 不是失败标志。
        /// </summary>
        public Task<JsonElement> Export(string kind, string path, bool builds, bool editable)
            => Request("export", new { kind, path, builds, editable }, 60000);

        /// <summary>把整份 deck 导成单个 .py（结构内联、按 show 顺序）。结果随响应返回。</summary>
        public Task<JsonElement> ExportPython(string path)
            => Request("export_python", new { path }, 60000);

        /// <summary>等待引擎渲染队列排空（写回前必须调用，否则保存的是旧源码）。</summary>
        public Task<JsonElement> Settle()
            => Request("settle", null, 60000);

        /// <summary>清空引擎撤销/重做历史（Revert 之后调用，避免从旧版本出发的撤销步）。</summary>
        public Task<JsonElement> ClearHistory()
            => Request("clear_history", null, 60000);

        /// <summary>busy pill 的取消：停下正在进行的 import / build / export / 编辑。</summary>
        public Task<JsonElement> Cancel()
            => Request("cancel", null, 30000);

        /// <summary>扫描一个 .tex 文件需要但本机没有的宏包：{missing:[...], tlmgr:bool}。</summary>
        public Task<JsonElement> LatexMissing(string src)
            => Request("latex_missing", new { src }, 60000);

        /// <summary>当前 deck 仍缺的宏包与仍为占位图的图片数：{missing, placeholders, source}（需已 open）。</summary>
        public Task<JsonElement> LatexStatus()
            => Request("latex_status", null, 60000);

        /// <summary>
        /// 安装缺包。<b>进度走 <c>latex_install</c> 事件</b>（done/total/step），
        /// 最终结果随本次 RPC 响应同步返回（rpc.py:138-142）——与 import/export 相反。
        /// </summary>
        public Task<JsonElement> LatexInstall(string[] files)
            => Request("latex_install", new { files }, 900000);

        /// <summary>把 assets 里的占位图按缺包重排（会写 assets，故必须能被 Save 写回）。</summary>
        public Task<JsonElement> LatexRetypeset()
            => Request("latex_retypeset", null, 300000);

        #endregion

        /// <summary>搜索 BioCartoon 图标目录（首次会下载 catalog，超时给足）。</summary>
        public Task<JsonElement> BioSearch(string query, int limit, object filters)
            => Request("bio_search", new { query, limit = limit, filters = filters ?? new { } }, 150000);

        /// <summary>下载某个 BioCartoon 图标的 SVG 或 PNG 到指定绝对路径。</summary>
        public Task<JsonElement> BioFetch(string id, string outPath, string kind = "svg")
            => Request("bio_fetch", new { icon = id, @out = outPath, kind }, 150000);

        /// <summary>在受控子进程里执行用户 Python 绘图代码，输出 PNG 到 outPath。</summary>
        public Task<JsonElement> PythonPlot(string code, string outPath, string cwd = null)
            => Request("python_plot", new { code, @out = outPath, cwd = cwd ?? "" }, 90000);

        public void Dispose()
        {
            _cts?.Cancel();
            try
            {
                if (IsRunning)
                {
                    _process.StandardInput.WriteLine("{\"id\":0,\"cmd\":\"cancel\"}");
                    _process.StandardInput.Flush();
                    _process.StandardInput.Close();
                    if (!_process.WaitForExit(2000)) _process.Kill();
                }
            }
            catch { }
            try { _process?.Dispose(); } catch { }
        }
    }

    public class EngineException : Exception
    {
        public EngineException(string message) : base(message) { }
    }
}
