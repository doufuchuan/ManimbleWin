using System;
using System.Collections.Generic;
using System.IO;

namespace ManimbleWin.Services
{
    /// <summary>
    /// 目录整棵真实拷贝（工作副本用）。
    ///
    /// <para>
    /// <b>为什么不用 junction / 符号链接</b>：引擎会把传进来的 deck 路径再解析一次——
    /// <c>srcedit.py:210</c> 的 <c>dir = os.path.realpath(deck)</c>、<c>core.py:365/430</c> 的
    /// <c>Path(deck).parent / "assets"</c>、以及 7 套主题 <c>common.py</c> 里的
    /// <c>ASSETS = Path(__file__).resolve().parent.parent / "assets"</c>。
    /// 只要 deck 或 assets 是链接，<c>resolve()</c> 就会穿透回原目录，
    /// <c>latex_retypeset</c> 的占位图/字体写入与图片扫描都会落到用户原件上，
    /// 「编辑只落副本」的隔离就破了。所以这里只做真实拷贝，本类**不提供**创建链接的方法。
    /// </para>
    /// </summary>
    public static class DirectoryLinkUtil
    {
        /// <summary>
        /// 生成目录：Manim / Manim Slides / 本应用产出的东西，副本里既不拷贝也不写回
        /// （对齐 Workspace.swift:89 的 generated 集合）。原样留给引擎在副本内重建。
        /// </summary>
        public static readonly string[] DefaultSkipDirNames = { "slides", "media", "__pycache__", ".manimble" };

        /// <summary>
        /// 把 <paramref name="src"/> 整棵真实拷贝到 <paramref name="dst"/>。
        ///
        /// 规则：
        /// <list type="bullet">
        /// <item>目录递归创建；文件二进制复制并保留最后写入时间。</item>
        /// <item>名字出现在 <paramref name="skipDirNames"/> 里的目录整棵跳过（不建空目录）——
        /// 这些是生成目录，副本里让引擎重建，也避免带着几百 MB 的 media/ 一起拷。</item>
        /// <item>以 '.' 开头的文件与目录一律跳过：<c>.git</c>/<c>.manimble</c>/<c>.DS_Store</c> 之类
        /// 既不该进副本，也不该进 baseline（对齐原版 <c>.skipsHiddenFiles</c>）。</item>
        /// <item>遇到 reparse point（链接）不跟随，直接跳过：拷贝树里只允许真实目录/文件。</item>
        /// </list>
        ///
        /// 单个文件拷贝失败（被占用、权限）不抛异常，交由 <paramref name="onSkipped"/> 报告——
        /// 一个 deck 里某个临时文件读不了不该让整次打开失败。
        /// </summary>
        /// <param name="src">源目录（必须存在）。</param>
        /// <param name="dst">目标目录（会被创建）。</param>
        /// <param name="skipDirNames">跳过的目录名；null 用 <see cref="DefaultSkipDirNames"/>。</param>
        /// <param name="onSkipped">可选：报告被跳过的路径与原因。</param>
        public static void CopyTree(string src, string dst, string[] skipDirNames = null, Action<string> onSkipped = null)
        {
            if (string.IsNullOrWhiteSpace(src)) throw new ArgumentException("源目录为空", nameof(src));
            if (string.IsNullOrWhiteSpace(dst)) throw new ArgumentException("目标目录为空", nameof(dst));
            string source = Path.GetFullPath(src);
            if (!Directory.Exists(source)) throw new DirectoryNotFoundException("找不到要拷贝的目录：" + source);

            var skip = new HashSet<string>(skipDirNames ?? DefaultSkipDirNames, StringComparer.OrdinalIgnoreCase);
            Directory.CreateDirectory(Path.GetFullPath(dst));
            CopyInto(source, Path.GetFullPath(dst), skip, onSkipped, 0);
        }

        /// <summary>递归拷贝一层目录内容（深度上限防御性保护，正常 deck 不会接近）。</summary>
        private static void CopyInto(string srcDir, string dstDir, HashSet<string> skipDirs,
                                     Action<string> onSkipped, int depth)
        {
            if (depth > 64) { onSkipped?.Invoke("跳过过深目录：" + srcDir); return; }

            string[] entries;
            try { entries = Directory.GetFileSystemEntries(srcDir); }
            catch (Exception ex) { onSkipped?.Invoke($"跳过目录 {srcDir}：{ex.Message}"); return; }

            foreach (string entry in entries)
            {
                string name = Path.GetFileName(entry);
                if (string.IsNullOrEmpty(name)) continue;
                if (name[0] == '.') continue;                       // 隐藏项：.git / .manimble / .DS_Store…
                if (IsReparsePoint(entry)) { onSkipped?.Invoke("跳过链接：" + entry); continue; }

                bool isDir;
                try { isDir = Directory.Exists(entry); }
                catch (Exception ex) { onSkipped?.Invoke($"跳过 {entry}：{ex.Message}"); continue; }

                if (isDir)
                {
                    if (skipDirs.Contains(name)) continue;           // 生成目录：不拷贝
                    string childDst = Path.Combine(dstDir, name);
                    try { Directory.CreateDirectory(childDst); }
                    catch (Exception ex) { onSkipped?.Invoke($"跳过目录 {entry}：{ex.Message}"); continue; }
                    CopyInto(entry, childDst, skipDirs, onSkipped, depth + 1);
                    continue;
                }

                string dest = Path.Combine(dstDir, name);
                try
                {
                    File.Copy(entry, dest, true);
                    try { File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(entry)); } catch { }
                }
                catch (Exception ex)
                {
                    onSkipped?.Invoke($"跳过文件 {entry}：{ex.Message}");
                }
            }
        }

        /// <summary>
        /// 是否 reparse point（junction / 符号链接 / 挂载点）。
        /// 只用于诊断与防御：副本树里不该出现链接，扫描 baseline 与 Save 时都会跳过。
        /// </summary>
        public static bool IsReparsePoint(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                var attrs = File.GetAttributes(path);
                return (attrs & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
            }
            catch
            {
                return false;
            }
        }
    }
}
