using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ManimbleWin.Models;

namespace ManimbleWin.Services
{
    /// <summary>
    /// 「最近打开」列表：<c>%LOCALAPPDATA%\Manimble\recent.json</c>，最近 10 条，新开置顶、去重、失效剔除。
    ///
    /// <para><b>不要和 <c>DeckHistory.cs</c> 混用</b>：那是撤销/重做快照栈，与最近文档无关。
    /// 本类只存 deck 目录路径，不碰任何 deck 内容。</para>
    ///
    /// <para>对齐 RecentDecks.swift：菜单显示「父目录 / deck」（deck 常常都叫 deck，只显示末级分不清），
    /// tooltip 给完整路径；<c>.manimble</c> 包记录的是解包后的项目路径，包本身不变。</para>
    /// </summary>
    public sealed class RecentDecksService
    {
        /// <summary>最多记住多少条。</summary>
        public const int Limit = 10;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true };

        private readonly Action<string> _log;
        private readonly string _filePath;

        /// <summary>
        /// <paramref name="log"/> 可为 null。
        /// <paramref name="filePath"/> 只给测试用：默认用 <see cref="FilePath"/>（真实用户数据目录）。
        /// </summary>
        public RecentDecksService(Action<string> log = null, string filePath = null)
        {
            _log = log;
            _filePath = filePath;
        }

        /// <summary>存储文件：<c>%LOCALAPPDATA%\Manimble\recent.json</c>（与应用数据根同根，见设计 R6）。</summary>
        public static string FilePath => Path.Combine(WorkspaceService.AppRoot, "recent.json");

        /// <summary>本实例实际读写的文件（测试可覆盖）。</summary>
        public string StoragePath => string.IsNullOrEmpty(_filePath) ? FilePath : _filePath;

        /// <summary>
        /// 读取最近文档（已剔除失效路径，最近的在最前）。
        /// 文件缺失、损坏、权限不足都返回空列表——最近文档坏掉不该影响打开应用。
        /// </summary>
        public IReadOnlyList<RecentEntry> Load()
        {
            var entries = new List<RecentEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in ReadPaths())
            {
                string normalized;
                try { normalized = WorkspaceService.NormalizePath(path); }
                catch { continue; }
                if (!seen.Add(normalized)) continue;

                bool exists;
                try { exists = Directory.Exists(normalized); }
                catch { exists = false; }
                if (!exists) continue;                                  // 失效条目：菜单里不显示

                entries.Add(ToEntry(normalized));
                if (entries.Count >= Limit) break;
            }
            return entries;
        }

        /// <summary>
        /// 记住一个刚打开的 deck：规范化 → 去重 → 置顶 → 截断到 <see cref="Limit"/>。
        /// </summary>
        public void Note(string deckPath)
        {
            string normalized;
            try { normalized = WorkspaceService.NormalizePath(deckPath); }
            catch (Exception ex) { _log?.Invoke("记录最近文档失败：" + ex.Message); return; }
            if (File.Exists(normalized))
            {
                string parent = Path.GetDirectoryName(normalized);
                if (string.IsNullOrEmpty(parent)) return;
                normalized = parent;
            }

            var paths = new List<string> { normalized };
            foreach (string existing in ReadPaths())
            {
                if (string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)) continue;
                paths.Add(existing);
                if (paths.Count >= Limit) break;
            }
            WritePaths(paths);
        }

        /// <summary>忘掉一个 deck（点击失效条目时调用）。</summary>
        public void Forget(string deckPath)
        {
            string normalized;
            try { normalized = WorkspaceService.NormalizePath(deckPath); }
            catch { return; }
            var paths = new List<string>();
            foreach (string existing in ReadPaths())
                if (!string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)) paths.Add(existing);
            WritePaths(paths);
        }

        /// <summary>清空列表（Clear Menu）。</summary>
        public void Clear() => WritePaths(new List<string>());

        /// <summary>路径 → 菜单条目（显示名「父目录 / deck」，tooltip 完整路径）。</summary>
        public static RecentEntry ToEntry(string deckPath)
        {
            string path = deckPath ?? "";
            string deck = SafeLeaf(path);
            string parent = SafeLeaf(SafeParent(path));
            string display = string.IsNullOrEmpty(parent) ? deck : (parent + " / " + deck);
            return new RecentEntry { Path = path, DisplayName = display, ToolTip = path };
        }

        private List<string> ReadPaths()
        {
            var list = new List<string>();
            try
            {
                if (!File.Exists(StoragePath)) return list;
                string text = File.ReadAllText(StoragePath);
                if (string.IsNullOrWhiteSpace(text)) return list;
                using (JsonDocument doc = JsonDocument.Parse(text))
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
                    foreach (JsonElement item in doc.RootElement.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            string value = item.GetString();
                            if (!string.IsNullOrWhiteSpace(value)) list.Add(value);
                        }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke("读取最近文档失败：" + ex.Message);
            }
            return list;
        }

        private void WritePaths(List<string> paths)
        {
            try
            {
                string dir = Path.GetDirectoryName(StoragePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string json = JsonSerializer.Serialize(paths ?? new List<string>(), JsonOptions);
                // 先写临时文件再替换：避免写一半留下坏 JSON，下次启动就把列表全丢了
                string temp = StoragePath + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, StoragePath, true);
            }
            catch (Exception ex)
            {
                _log?.Invoke("保存最近文档失败：" + ex.Message);
            }
        }

        private static string SafeLeaf(string path)
        {
            try { return Path.GetFileName(path ?? "") ?? ""; }
            catch { return ""; }
        }

        private static string SafeParent(string path)
        {
            try { return Path.GetDirectoryName(path ?? "") ?? ""; }
            catch { return ""; }
        }
    }
}
