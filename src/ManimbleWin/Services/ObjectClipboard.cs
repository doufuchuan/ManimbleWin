using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ManimbleWin.Models;

namespace ManimbleWin.Services
{
    /// <summary>剪贴板里的一个对象：一份可随时克隆的副本，外加它原本那一行源码（用于在目标页复现同样的代码）。</summary>
    public sealed class CopiedObject
    {
        public CanvasObject Clone { get; set; }

        /// <summary>该对象在源页源码里的整行文本（含行尾 # manimble:id 标记）；找不到标记行为 null。</summary>
        public string LineText { get; set; }
    }

    /// <summary>
    /// 对象剪贴板。
    /// 为什么同时保留「引擎条目」和「本地副本」两套数据：
    /// 引擎的 copy 给出的是一段可移植的代码（能精确复现手写代码造出来的对象，本地生成器做不到），
    /// 但只有在同一个 deck 里、且该对象确实是独立代码造出来的时候才存在。其余情况（对象由循环/合造、
    /// 或引擎未连接）只能退回到本地副本，用 SourceCodeGenerator 生成一行近似代码。
    /// </summary>
    public sealed class ObjectClipboard
    {
        private static ObjectClipboard _shared;

        /// <summary>进程内单例：复制与粘贴分处不同的命令调用，状态要跨调用保留。</summary>
        public static ObjectClipboard Shared => _shared ??= new ObjectClipboard();

        public List<CopiedObject> Objects { get; } = new List<CopiedObject>();

        /// <summary>引擎 copy 返回的 items（每个元素：kind/code/why/at/box/pictures/text）。</summary>
        public List<JsonElement> EngineItems { get; } = new List<JsonElement>();

        public string DeckPath { get; set; } = "";
        public string Scene { get; set; } = "";

        /// <summary>本页已粘贴次数，用来把连续粘贴的副本错开一点，避免叠在一起看不出来。</summary>
        public int PasteCount { get; set; }

        public bool HasContent => Objects.Count > 0 || EngineItems.Count > 0;

        public string Summary
        {
            get
            {
                if (!HasContent) return "剪贴板为空";
                int n = Math.Max(Objects.Count, EngineItems.Count);
                return n == 1 ? "已复制 1 个对象" : $"已复制 {n} 个对象";
            }
        }

        public void Clear()
        {
            Objects.Clear();
            EngineItems.Clear();
            DeckPath = "";
            Scene = "";
            PasteCount = 0;
        }

        /// <summary>用引擎的 items 填充剪贴板（同 deck 粘贴优先走这些）。</summary>
        public void SetEngineItems(IEnumerable<JsonElement> items, string deckPath, string scene)
        {
            EngineItems.Clear();
            if (items != null) EngineItems.AddRange(items.Select(i => i.Clone()));
            DeckPath = deckPath ?? "";
            Scene = scene ?? "";
            PasteCount = 0;
        }
    }
}
