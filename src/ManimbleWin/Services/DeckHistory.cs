using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ManimbleWin.Models;

namespace ManimbleWin.Services
{
    /// <summary>
    /// 原型模式下的撤销 / 重做（未连接引擎时使用）。
    /// 为什么用整份快照而不是命令对：画布上的每个改动都会同时改「对象集合」和「源码文本」两处，
    /// 命令对很容易两边不同步；快照恢复天然保证二者一致，而一个 deck 只有几页、每页几个对象，
    /// 快照成本可以忽略。连接引擎后一律走引擎的 undo / redo（那边按文件差分，更精确）。
    /// </summary>
    public sealed class DeckHistory
    {
        private readonly List<Entry> _undo = new List<Entry>();
        private readonly List<Entry> _redo = new List<Entry>();

        /// <summary>最多保留的步数：够用又不至于长期持有过多对象副本。</summary>
        private const int MaxSteps = 80;

        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;
        public string UndoLabel => _undo.Count > 0 ? _undo[_undo.Count - 1].Label : null;
        public string RedoLabel => _redo.Count > 0 ? _redo[_redo.Count - 1].Label : null;

        /// <summary>拍一份当前状态的快照（在做任何改动之前调用）。</summary>
        public static DeckSnapshot Capture(IList<Slide> slides, int currentIndex)
        {
            var snap = new DeckSnapshot { CurrentIndex = Math.Max(0, currentIndex) };
            foreach (var s in slides)
            {
                var ss = new SlideSnap
                {
                    Ref = s,
                    Name = s.Name,
                    Source = s.Source ?? "",
                    AltText = s.AltText ?? "",
                    Number = s.Number,
                    CurrentBuildIndex = s.CurrentBuildIndex
                };
                foreach (var b in s.Builds)
                {
                    ss.Builds.Add(new BuildSnap
                    {
                        Name = b.Name,
                        Notes = b.Notes ?? "",
                        BuildNotes = b.BuildNotes ?? "",
                        RenderedImage = b.RenderedImage ?? "",
                        // 克隆一份，避免后续改动把快照里的值一起改掉
                        Objects = b.Objects.Select(o => o.Clone()).ToList()
                    });
                }
                snap.Slides.Add(ss);
            }
            return snap;
        }

        /// <summary>记录一步可撤销的改动。同标签的连续小改动（方向键微调）合并为一步。</summary>
        public void Push(string label, DeckSnapshot before, string mergeKey = null)
        {
            _redo.Clear();
            if (mergeKey != null && _undo.Count > 0 &&
                _undo[_undo.Count - 1].MergeKey == mergeKey &&
                Environment.TickCount64 - _undo[_undo.Count - 1].At < 900)
            {
                // 合并：保留更早的那份快照（撤销要回到这一串改动的起点），只更新时间戳
                _undo[_undo.Count - 1] = new Entry(label, _undo[_undo.Count - 1].Snapshot, mergeKey);
                return;
            }
            _undo.Add(new Entry(label, before, mergeKey));
            while (_undo.Count > MaxSteps) _undo.RemoveAt(0);
        }

        /// <summary>撤销一步：返回要恢复到的快照，并把「撤销前的现状」压进重做栈。</summary>
        public DeckSnapshot Undo(DeckSnapshot current)
        {
            if (_undo.Count == 0) return null;
            var e = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add(new Entry(e.Label, current, null));
            return e.Snapshot;
        }

        /// <summary>重做一步：返回要恢复到的快照，并把它重新压回撤销栈。</summary>
        public DeckSnapshot Redo(DeckSnapshot current)
        {
            if (_redo.Count == 0) return null;
            var e = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            _undo.Add(new Entry(e.Label, current, null));
            return e.Snapshot;
        }

        public void Clear()
        {
            _undo.Clear();
            _redo.Clear();
        }

        /// <summary>把快照写回幻灯片集合：保留原有的 Slide / Build 实例，只重建内容，避免绑定断掉。</summary>
        public static void Restore(DeckSnapshot snap, ObservableCollection<Slide> slides)
        {
            if (snap == null) return;
            var wanted = snap.Slides.Select(x => x.Ref).Where(r => r != null).ToList();
            if (wanted.Count > 0)
            {
                slides.Clear();
                foreach (var s in wanted) slides.Add(s);
            }
            foreach (var ss in snap.Slides)
            {
                var s = ss.Ref;
                if (s == null) continue;
                s.Name = ss.Name;
                s.Source = ss.Source;
                s.AltText = ss.AltText;
                s.Number = ss.Number;
                s.Builds.Clear();
                foreach (var bs in ss.Builds)
                {
                    var b = new Build
                    {
                        Name = bs.Name,
                        Notes = bs.Notes,
                        BuildNotes = bs.BuildNotes,
                        RenderedImage = bs.RenderedImage
                    };
                    foreach (var o in bs.Objects)
                    {
                        var c = o.Clone();
                        c.IsSelected = false;
                        b.Objects.Add(c);
                    }
                    s.Builds.Add(b);
                }
                if (s.Builds.Count > 0)
                    s.CurrentBuildIndex = Math.Max(0, Math.Min(ss.CurrentBuildIndex, s.Builds.Count - 1));
            }
        }

        private sealed class Entry
        {
            public Entry(string label, DeckSnapshot snapshot, string mergeKey)
            {
                Label = label;
                Snapshot = snapshot;
                MergeKey = mergeKey;
                At = Environment.TickCount64;
            }

            public string Label { get; }
            public DeckSnapshot Snapshot { get; }
            public string MergeKey { get; }
            public long At { get; }
        }
    }

    /// <summary>整份 deck 的状态快照。</summary>
    public sealed class DeckSnapshot
    {
        public List<SlideSnap> Slides { get; } = new List<SlideSnap>();
        public int CurrentIndex { get; set; }
    }

    public sealed class SlideSnap
    {
        public Slide Ref { get; set; }
        public string Name { get; set; } = "";
        public string Source { get; set; } = "";
        public string AltText { get; set; } = "";
        public int Number { get; set; }
        public int CurrentBuildIndex { get; set; }
        public List<BuildSnap> Builds { get; } = new List<BuildSnap>();
    }

    public sealed class BuildSnap
    {
        public string Name { get; set; } = "";
        public string Notes { get; set; } = "";
        public string BuildNotes { get; set; } = "";
        public string RenderedImage { get; set; } = "";
        public List<CanvasObject> Objects { get; set; } = new List<CanvasObject>();
    }
}
