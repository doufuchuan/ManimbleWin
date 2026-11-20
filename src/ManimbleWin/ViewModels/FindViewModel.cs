using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using ManimbleWin.Helpers;
using ManimbleWin.Models;
using ManimbleWin.Services;

namespace ManimbleWin.ViewModels
{
    /// <summary>命中的一处：引擎模式用 scene/oid/index 定位，原型模式直接持有对象引用。</summary>
    public class FindHit
    {
        public string Scene { get; set; } = "";
        public int Oid { get; set; }
        public int Stop { get; set; }
        public int Index { get; set; }
        public string Kind { get; set; } = "text";
        public string Value { get; set; } = "";
        public string Match { get; set; } = "";
        public bool Editable { get; set; } = true;

        /// <summary>原型模式下的定位依据（引擎模式为 null）。</summary>
        public CanvasObject LocalObject { get; set; }
        public Slide LocalSlide { get; set; }
        public int LocalBuildIndex { get; set; }

        public bool InNotes => Kind == "notes" || Kind == "buildNotes";

        public string Key => $"{Scene}:{Kind}:{Oid}:{Stop}:{Index}";

        /// <summary>浮层里显示的那一行摘要（对象类型 · 命中前后的片段）。</summary>
        public string Preview
        {
            get
            {
                if (string.IsNullOrEmpty(Value)) return Scene;
                int at = Value.IndexOf(Match ?? "", StringComparison.Ordinal);
                if (at < 0) return $"{Scene} · {Truncate(Value, 40)}";
                int from = Math.Max(0, at - 12);
                string head = from > 0 ? "…" : "";
                string body = Value.Substring(from, Math.Min(Value.Length - from, (Match ?? "").Length + 24));
                return $"{Scene} · {head}{body.Replace("\n", " ")}";
            }
        }

        private static string Truncate(string s, int n)
            => s.Length <= n ? s : s.Substring(0, n) + "…";
    }

    /// <summary>
    /// 查找替换浮层的视图模型（Ctrl+F）。
    /// 搜索要么交给引擎的 find（能搜到每页每个 build 的真实文本、公式与备注，命中带 oid 可直接定位），
    /// 要么在原型模式下遍历内存里的对象文本——两条路的命中都统一成 <see cref="FindHit"/>，上层 UI 无需区分。
    /// </summary>
    public class FindViewModel : ObservableObject
    {
        private readonly MainViewModel _main;
        private readonly DispatcherTimer _debounce;
        private int _generation;

        public FindViewModel(MainViewModel main)
        {
            _main = main;
            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            _debounce.Tick += (s, e) => { _debounce.Stop(); _ = SearchAsync(); };

            NextCommand = new RelayCommand(_ => Step(1));
            PrevCommand = new RelayCommand(_ => Step(-1));
            ReplaceOneCommand = new RelayCommand(_ => _ = ReplaceOneAsync());
            ReplaceAllCommand = new RelayCommand(_ => _ = ReplaceAllAsync());
            CloseCommand = new RelayCommand(_ => Close());
        }

        #region 状态

        private bool _isOpen;
        public bool IsOpen
        {
            get => _isOpen;
            set { if (SetProperty(ref _isOpen, value) && !value) Reset(); }
        }

        private bool _replaceOpen;
        /// <summary>替换行是否展开（Ctrl+Alt+F 直接展开）。</summary>
        public bool ReplaceOpen { get => _replaceOpen; set => SetProperty(ref _replaceOpen, value); }

        private string _query = "";
        public string Query
        {
            get => _query;
            set
            {
                if (!SetProperty(ref _query, value)) return;
                ReplaceStatus = "";
                _debounce.Stop();
                _debounce.Start();
            }
        }

        private string _replacement = "";
        public string Replacement { get => _replacement; set => SetProperty(ref _replacement, value); }

        /// <summary>
        /// 发给引擎的替换串：正则模式先归一成 <c>$n</c>（引擎不认 ${1} 也不认 \1），
        /// 本地路径由 FindMath 内部做同一套归一，两边行为一致。
        /// </summary>
        private string EngineReplacement => UseRegex ? FindMath.NormalizeReplacement(Replacement ?? "") : (Replacement ?? "");

        private bool _matchCase;
        public bool MatchCase
        {
            get => _matchCase;
            set { if (SetProperty(ref _matchCase, value)) Reschedule(); }
        }

        private bool _wholeWord;
        public bool WholeWord
        {
            get => _wholeWord;
            set { if (SetProperty(ref _wholeWord, value)) Reschedule(); }
        }

        private bool _useRegex;
        public bool UseRegex
        {
            get => _useRegex;
            set { if (SetProperty(ref _useRegex, value)) Reschedule(); }
        }

        private bool _searchNotes;
        public bool SearchNotes
        {
            get => _searchNotes;
            set { if (SetProperty(ref _searchNotes, value)) Reschedule(); }
        }

        public ObservableCollection<FindHit> Matches { get; } = new ObservableCollection<FindHit>();

        private int _currentIndex = -1;
        public int CurrentIndex
        {
            get => _currentIndex;
            set
            {
                if (!SetProperty(ref _currentIndex, value)) return;
                RaisePropertyChanged(nameof(CountText));
            }
        }

        private string _searchStatus = "";
        /// <summary>搜索状态（错误 / 无匹配）——直接显示在计数位置，避免用户以为没反应。</summary>
        public string SearchStatus { get => _searchStatus; set { if (SetProperty(ref _searchStatus, value)) RaisePropertyChanged(nameof(CountText)); } }

        private string _replaceStatus = "";
        public string ReplaceStatus { get => _replaceStatus; set => SetProperty(ref _replaceStatus, value); }

        public string CountText
        {
            get
            {
                if (!string.IsNullOrEmpty(SearchStatus)) return SearchStatus;
                if (string.IsNullOrEmpty(Query)) return "";
                if (Matches.Count == 0) return "无匹配";
                return $"{CurrentIndex + 1}/{Matches.Count}";
            }
        }

        public ICommand NextCommand { get; }
        public ICommand PrevCommand { get; }
        public ICommand ReplaceOneCommand { get; }
        public ICommand ReplaceAllCommand { get; }
        public ICommand CloseCommand { get; }

        /// <summary>浮层打开时把焦点交给搜索框（由视图订阅）。</summary>
        public event Action FocusRequested;

        #endregion

        #region 打开 / 关闭

        /// <summary>打开查找浮层；withReplace=true 时同时展开替换行。</summary>
        public void Open(bool withReplace)
        {
            IsOpen = true;
            ReplaceOpen = withReplace;
            // 工具栏那个搜索框里的内容带过来，用户的意图往往就是「搜这个」
            if (string.IsNullOrEmpty(Query) && !string.IsNullOrEmpty(_main.FindText))
                Query = _main.FindText;
            FocusRequested?.Invoke();
            _ = SearchAsync();
        }

        public void Close()
        {
            IsOpen = false;
            _main.FocusCanvas();
        }

        private void Reset()
        {
            Matches.Clear();
            CurrentIndex = -1;
            SearchStatus = "";
            ReplaceStatus = "";
        }

        private void Reschedule()
        {
            _debounce.Stop();
            _debounce.Start();
        }

        #endregion

        #region 搜索

        private async Task SearchAsync()
        {
            int gen = ++_generation;
            string query = Query ?? "";
            if (string.IsNullOrEmpty(query))
            {
                Matches.Clear();
                CurrentIndex = -1;
                SearchStatus = "";
                RaisePropertyChanged(nameof(CountText));
                return;
            }

            List<FindHit> hits;
            try
            {
                hits = _main.Engine != null && _main.Engine.IsConnected
                    ? await SearchEngineAsync(query)
                    : SearchLocal(query);
            }
            catch (Exception ex)
            {
                if (gen != _generation) return;
                SearchStatus = "搜索失败：" + ex.Message;
                Matches.Clear();
                CurrentIndex = -1;
                return;
            }
            if (gen != _generation) return;   // 已有更新的搜索，丢弃这次结果

            Matches.Clear();
            foreach (var h in hits) Matches.Add(h);
            CurrentIndex = hits.Count > 0 ? 0 : -1;
            SearchStatus = "";
            RaisePropertyChanged(nameof(CountText));
            if (hits.Count > 0) GoTo(0);
        }

        private async Task<List<FindHit>> SearchEngineAsync(string query)
        {
            var r = await _main.Engine.Find(query, MatchCase, WholeWord, UseRegex, SearchNotes);
            var list = new List<FindHit>();
            if (r.ValueKind != JsonValueKind.Object) return list;
            if (r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            {
                SearchStatus = GetStr(r, "error") ?? "搜索失败";
                return list;
            }
            if (!r.TryGetProperty("matches", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
            foreach (var m in arr.EnumerateArray())
            {
                list.Add(new FindHit
                {
                    Scene = GetStr(m, "scene") ?? "",
                    Oid = GetInt(m, "oid"),
                    Stop = GetInt(m, "stop"),
                    Index = GetInt(m, "index"),
                    Kind = GetStr(m, "kind") ?? "text",
                    Value = GetStr(m, "value") ?? "",
                    Match = GetStr(m, "match") ?? "",
                    Editable = !(m.TryGetProperty("editable", out var ed) && ed.ValueKind == JsonValueKind.False)
                });
            }
            return list;
        }

        /// <summary>原型模式下的搜索：遍历当前 deck 每页每个 build 的文本 / 公式对象。</summary>
        private List<FindHit> SearchLocal(string query)
        {
            var list = new List<FindHit>();
            Regex re;
            try { re = FindMath.Build(query, MatchCase, WholeWord, UseRegex); }
            catch (ArgumentException ex)
            {
                SearchStatus = "正则表达式：" + ex.Message;
                return list;
            }

            foreach (var slide in _main.Slides)
            {
                for (int bi = 0; bi < slide.Builds.Count; bi++)
                {
                    var build = slide.Builds[bi];
                    foreach (var o in build.Objects)
                    {
                        if (o.Kind != ObjectKind.Text && o.Kind != ObjectKind.Tex) continue;
                        string text = ObjectText(o);
                        if (string.IsNullOrEmpty(text)) continue;
                        int i = 0;
                        foreach (var m in FindMath.Matches(re, text))
                        {
                            list.Add(new FindHit
                            {
                                Scene = slide.Name, Oid = -1, Stop = bi, Index = i++, Kind = o.Kind == ObjectKind.Tex ? "tex" : "text",
                                Value = text, Match = m.Value, Editable = true,
                                LocalObject = o, LocalSlide = slide, LocalBuildIndex = bi
                            });
                        }
                    }
                    if (SearchNotes)
                    {
                        foreach (var (kind, text) in new[] { ("notes", build.Notes), ("buildNotes", build.BuildNotes) })
                        {
                            if (string.IsNullOrEmpty(text)) continue;
                            int i = 0;
                            foreach (var m in FindMath.Matches(re, text))
                            {
                                list.Add(new FindHit
                                {
                                    Scene = slide.Name, Oid = -1, Stop = bi, Index = i++, Kind = kind,
                                    Value = text, Match = m.Value, Editable = true,
                                    LocalSlide = slide, LocalBuildIndex = bi
                                });
                            }
                        }
                    }
                }
            }
            return list;
        }

        /// <summary>对象显示出来的文本（公式取 LaTeX 源码，与其在页面上代表的内容一致）。</summary>
        private static string ObjectText(CanvasObject o)
            => string.IsNullOrEmpty(o.Value) ? (o.DisplayText ?? "") : o.Value;

        #endregion

        #region 导航

        private void Step(int delta)
        {
            if (Matches.Count == 0) return;
            int next = (CurrentIndex + delta + Matches.Count) % Matches.Count;
            GoTo(next);
        }

        /// <summary>跳到第 i 个命中：切到它所在页与 build，选中对象（源码行随之高亮）。</summary>
        public void GoTo(int i)
        {
            if (i < 0 || i >= Matches.Count) return;
            CurrentIndex = i;
            var hit = Matches[i];

            if (hit.LocalObject != null)
            {
                if (hit.LocalSlide != null && !ReferenceEquals(_main.CurrentSlide, hit.LocalSlide))
                    _main.CurrentSlide = hit.LocalSlide;
                if (hit.LocalSlide != null && hit.LocalBuildIndex >= 0 && hit.LocalBuildIndex < hit.LocalSlide.Builds.Count)
                    _main.SetBuildIndex(hit.LocalSlide, hit.LocalBuildIndex);
                if (hit.LocalObject != null) _main.SelectSingleObject(hit.LocalObject);
                return;
            }

            var slide = _main.Slides.FirstOrDefault(s => string.Equals(s.Name, hit.Scene, StringComparison.Ordinal));
            if (slide == null) return;
            if (!ReferenceEquals(_main.CurrentSlide, slide)) _main.CurrentSlide = slide;
            _main.SetBuildIndex(slide, hit.Stop);

            if (hit.InNotes)
            {
                _main.ShowDrawer(hit.Kind == "notes" ? 0 : 1);
                _main.ShowToast($"命中在备注：{hit.Match}");
                return;
            }

            var obj = slide.Builds.ElementAtOrDefault(hit.Stop)?.Objects
                          .FirstOrDefault(o => string.Equals(o.Oid, hit.Oid.ToString(), StringComparison.Ordinal));
            if (obj != null) _main.SelectSingleObject(obj);
            else _main.ShowToast("该对象暂时不在画布上（可能只存在于源码里）");
        }

        #endregion

        #region 替换

        private async Task ReplaceOneAsync()
        {
            if (CurrentIndex < 0 || CurrentIndex >= Matches.Count) { ReplaceStatus = "没有选中的命中"; return; }
            var hit = Matches[CurrentIndex];
            int keepAt = CurrentIndex;

            if (_main.Engine != null && _main.Engine.IsConnected && hit.LocalObject == null)
            {
                var target = new Dictionary<string, object> { ["scene"] = hit.Scene, ["oid"] = hit.Oid, ["stop"] = hit.Stop, ["index"] = hit.Index };
                if (hit.InNotes) { target["kind"] = hit.Kind; target["stop"] = hit.Stop; }
                var r = await _main.Engine.Replace(Query, EngineReplacement, MatchCase, WholeWord, UseRegex, SearchNotes,
                                                   all: false, target: target);
                string err = GetStr(r, "error");
                if (r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
                {
                    ReplaceStatus = err ?? "替换失败";
                    return;
                }
                ReplaceStatus = "已替换一处";
                await _main.RefreshAfterEngineEdit(hit.Scene);
            }
            else
            {
                if (!ReplaceLocal(hit)) return;
                ReplaceStatus = "已替换一处";
            }

            await SearchAsync();
            // 重搜后停留在原来那一处的位置，用户才能继续往后替换
            if (Matches.Count > 0) GoTo(Math.Min(keepAt, Matches.Count - 1));
        }

        private async Task ReplaceAllAsync()
        {
            if (string.IsNullOrEmpty(Query)) { ReplaceStatus = "先输入要查找的内容"; return; }

            if (_main.Engine != null && _main.Engine.IsConnected)
            {
                var r = await _main.Engine.Replace(Query, EngineReplacement, MatchCase, WholeWord, UseRegex,
                                                   SearchNotes, all: true, target: null);
                if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
                {
                    ReplaceStatus = GetStr(r, "error") ?? "替换失败";
                    return;
                }
                ReplaceStatus = GetStr(r, "message") ?? $"已替换 {GetInt(r, "count")} 处";
                foreach (var name in ChangedScenes(r))
                    await _main.RefreshAfterEngineEdit(name);
                await _main.RefreshAllEngineSlides();
                await SearchAsync();
                return;
            }

            int count = ReplaceAllLocal();
            ReplaceStatus = count > 0 ? $"已替换 {count} 处" : "没有可替换的匹配";
            await SearchAsync();
        }

        /// <summary>
        /// 原型模式的「全部替换」：按对象聚合，每个对象只做一次整体替换。
        /// 不能逐个命中替换——同一对象上有两处命中时，改完第一处第二处的字符位置就变了。
        /// </summary>
        private int ReplaceAllLocal()
        {
            Regex re;
            try { re = FindMath.Build(Query, MatchCase, WholeWord, UseRegex); }
            catch (ArgumentException ex) { ReplaceStatus = "正则表达式：" + ex.Message; return 0; }

            int count = 0;
            var undoStep = _main.BeginLocalUndoStep();
            foreach (var slide in _main.Slides)
            {
                string srcBefore = slide.Source ?? "";
                bool touched = false;
                foreach (var build in slide.Builds)
                {
                    foreach (var o in build.Objects)
                    {
                        if (o.Kind != ObjectKind.Text && o.Kind != ObjectKind.Tex) continue;
                        string text = ObjectText(o);
                        int n = FindMath.Matches(re, text).Count;
                        if (n == 0) continue;
                        string replaced = FindMath.SubstituteAll(re, text, Replacement, UseRegex);
                        if (replaced == text) continue;

                        o.Value = replaced;
                        o.DisplayText = o.Kind == ObjectKind.Text ? replaced : LatexPretty.Pretty(replaced);
                        count += n;
                        touched = true;

                        if (!string.IsNullOrEmpty(o.CodeId) &&
                            SourceCodeGenerator.TryReplaceLiteral(slide.Source, o.CodeId, replaced, out string updated, out _))
                            slide.Source = updated;
                    }
                    if (SearchNotes)
                    {
                        if (!string.IsNullOrEmpty(build.Notes))
                        {
                            int n = FindMath.Matches(re, build.Notes).Count;
                            string next = FindMath.SubstituteAll(re, build.Notes, Replacement, UseRegex);
                            if (n > 0 && next != build.Notes) { build.Notes = next; count += n; touched = true; }
                        }
                        if (!string.IsNullOrEmpty(build.BuildNotes))
                        {
                            int n = FindMath.Matches(re, build.BuildNotes).Count;
                            string next = FindMath.SubstituteAll(re, build.BuildNotes, Replacement, UseRegex);
                            if (n > 0 && next != build.BuildNotes) { build.BuildNotes = next; count += n; touched = true; }
                        }
                    }
                }
                if (touched) _main.NoteSourceChangedFor(slide, srcBefore);
            }

            // 备注面板显示的是当前页的备注，改完要通知一次
            _main.RaiseNotesChanged();
            if (count > 0) _main.CommitLocalUndoStep(undoStep, $"全部替换 {count} 处");
            return count;
        }

        /// <summary>原型模式下替换一处：改对象文本 + 改它那一行源码，二者必须同步，否则下次渲染会被旧代码覆盖。</summary>
        private bool ReplaceLocal(FindHit hit, bool silent = false)
        {
            if (hit.LocalObject == null) return false;
            var o = hit.LocalObject;
            string before = o.Value ?? "";
            string replaced;
            try
            {
                var re = FindMath.Build(Query, MatchCase, WholeWord, UseRegex);
                replaced = FindMath.SubstituteOnce(re, before, Replacement, UseRegex, hit.Index);
            }
            catch (ArgumentException ex)
            {
                if (!silent) ReplaceStatus = "替换失败：" + ex.Message;
                return false;
            }
            if (replaced == before) return false;

            // 替换也是编辑：原型模式下记一步撤销（引擎模式下由引擎记录）
            var undoStep = _main.BeginLocalUndoStep();

            o.Value = replaced;
            if (o.Kind == ObjectKind.Text) o.DisplayText = replaced;
            else o.DisplayText = LatexPretty.Pretty(replaced);

            var slide = hit.LocalSlide ?? _main.CurrentSlide;
            if (slide != null && !string.IsNullOrEmpty(o.CodeId))
            {
                string srcBefore = slide.Source ?? "";
                if (SourceCodeGenerator.TryReplaceLiteral(srcBefore, o.CodeId, replaced, out string updated, out _))
                {
                    slide.Source = updated;
                    _main.NoteSourceChangedFor(slide, srcBefore);
                }
            }
            _main.CommitLocalUndoStep(undoStep, "替换");
            return true;
        }

        private static IEnumerable<string> ChangedScenes(JsonElement r)
        {
            if (r.ValueKind != JsonValueKind.Object) yield break;
            if (!r.TryGetProperty("scenes", out var arr) || arr.ValueKind != JsonValueKind.Array) yield break;
            foreach (var s in arr.EnumerateArray())
                if (s.ValueKind == JsonValueKind.String) yield return s.GetString();
        }

        #endregion

        #region JSON helpers

        private static string GetStr(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;

        private static int GetInt(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt32(out int i) ? i : 0;

        #endregion
    }
}
