using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace ManimbleWin.Services
{
    /// <summary>
    /// 本地 LaTeX 渲染：调用 TeX Live / MiKTeX 的 latex + dvipng，
    /// 把一段公式编译成透明背景 PNG（不依赖 manim，也不依赖 Python）。
    /// 结果按 (公式, 颜色, DPI) 哈希缓存到 %LOCALAPPDATA%\ManimbleWin\latex-cache。
    /// </summary>
    public static class LatexService
    {
        private static readonly ConcurrentDictionary<string, Task<string>> Inflight =
            new ConcurrentDictionary<string, Task<string>>();

        private static readonly ConcurrentDictionary<string, string> Done =
            new ConcurrentDictionary<string, string>();

        public static string CacheDir
        {
            get
            {
                string dir = Path.Combine(EnvironmentService.UserDataDir, "latex-cache");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static string _latexExe;
        private static string _dvipngExe;
        private static bool _probed;

        public static string LatexExe
        {
            get
            {
                Probe();
                return _latexExe;
            }
        }

        public static string DvipngExe
        {
            get
            {
                Probe();
                return _dvipngExe;
            }
        }

        /// <summary>探测一次并缓存（FindLatex 在极端情况下会扫盘，不能每次切页都做）。</summary>
        private static void Probe()
        {
            if (_probed) return;
            _probed = true;
            string latex = EnvironmentService.FindLatex();
            _latexExe = latex;
            if (!string.IsNullOrEmpty(latex))
            {
                string dir = Path.GetDirectoryName(latex);
                string candidate = Path.Combine(dir, "dvipng.exe");
                _dvipngExe = File.Exists(candidate) ? candidate
                    : (EnvironmentService.FindOnPath("dvipng.exe") ?? EnvironmentService.FindOnPath("dvipng"));
            }
            else
            {
                _dvipngExe = EnvironmentService.FindOnPath("dvipng.exe") ?? EnvironmentService.FindOnPath("dvipng");
            }
        }

        public static bool IsAvailable => !string.IsNullOrEmpty(LatexExe) && !string.IsNullOrEmpty(DvipngExe);

        /// <summary>剥掉可能粘连的 Python 字符串外壳（r"x^2" → x^2），避免把 r" 当成公式内容。</summary>
        public static string NormalizeTex(string tex)
        {
            string t = (tex ?? "").Trim();
            if (t.Length == 0) return t;
            var m = System.Text.RegularExpressions.Regex.Match(t, "^(?:[rR])?(?:\"([\\s\\S]*)\"|'([\\s\\S]*)')$");
            if (m.Success)
            {
                string inner = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (inner.Length > 0) return inner;
            }
            return t;
        }

        /// <summary>把公式编译成 PNG，返回文件路径；失败抛 LatexException（含 TeX 日志尾部）。</summary>
        public static string Render(string tex, double fontSizePt = 48, string colorHex = "#FFFFFF", int dpi = 300)
        {
            if (string.IsNullOrWhiteSpace(tex)) throw new LatexException("公式为空");
            if (!IsAvailable) throw new LatexException("未检测到 LaTeX（latex / dvipng）。请在依赖中心安装 TinyTeX 或 TeX Live。");

            string key = Hash($"{tex.Trim()}|{colorHex}|{dpi}|{fontSizePt:0.#}");
            if (Done.TryGetValue(key, out string cached) && File.Exists(cached)) return cached;
            if (Inflight.TryGetValue(key, out var running)) return running.GetAwaiter().GetResult();

            var task = Task.Run(() => RenderCore(key, tex, colorHex, dpi, fontSizePt));
            Inflight[key] = task;
            try
            {
                string path = task.GetAwaiter().GetResult();
                Done[key] = path;
                return path;
            }
            finally { Inflight.TryRemove(key, out _); }
        }

        public static Task<string> RenderAsync(string tex, double fontSizePt = 48, string colorHex = "#FFFFFF", int dpi = 300)
            => Task.Run(() => Render(tex, fontSizePt, colorHex, dpi));

        private static string RenderCore(string key, string tex, string colorHex, int dpi, double fontSizePt)
        {
            string target = Path.Combine(CacheDir, key + ".png");
            if (File.Exists(target)) return target;

            string work = Path.Combine(EnvironmentService.TempDir, "manimble-tex", key);
            Directory.CreateDirectory(work);
            string texFile = Path.Combine(work, "formula.tex");
            string dviFile = Path.Combine(work, "formula.dvi");
            string pngFile = Path.Combine(work, "formula.png");

            // 12pt 是 TeX 的基础字号；按目标字号同比放大 DPI，保证贴图分辨率足够
            int effectiveDpi = Math.Max(150, Math.Min(1200, (int)Math.Round(dpi * Math.Max(0.6, fontSizePt / 48.0))));
            string document =
                "\\documentclass[12pt]{article}\n" +
                "\\usepackage{amsmath}\n" +
                "\\usepackage{amssymb}\n" +
                "\\usepackage{amsfonts}\n" +
                "\\pagestyle{empty}\n" +
                "\\begin{document}\n" +
                "\\noindent$\\displaystyle " + tex.Trim() + "$\n" +
                "\\end{document}\n";
            File.WriteAllText(texFile, document, new UTF8Encoding(false));

            var run = Run(LatexExe, "-interaction=nonstopmode -halt-on-error -no-shell-escape formula.tex", work, 60000);
            if (run.code != 0 || !File.Exists(dviFile))
            {
                string log = ReadTail(Path.Combine(work, "formula.log")) ?? run.output;
                throw new LatexException(ExtractError(log) ?? "LaTeX 编译失败");
            }

            (byte r, byte g, byte b) = ParseColor(colorHex);
            string fg = string.Format(CultureInfo.InvariantCulture, "rgb {0:0.0##} {1:0.0##} {2:0.0##}",
                r / 255.0, g / 255.0, b / 255.0);
            var png = Run(DvipngExe,
                $"-T tight -D {effectiveDpi} -bg Transparent -fg \"{fg}\" formula.dvi -o formula.png",
                work, 60000);
            if (png.code != 0 || !File.Exists(pngFile))
                throw new LatexException("dvipng 生成 PNG 失败：" + (png.output ?? "").Trim());

            try
            {
                File.Copy(pngFile, target, true);
            }
            catch (IOException)
            {
                // 并发写同一个缓存文件：忽略，直接用已存在的
            }
            return File.Exists(target) ? target : pngFile;
        }

        private static string ExtractError(string log)
        {
            if (string.IsNullOrEmpty(log)) return null;
            var lines = log.Replace("\r\n", "\n").Split('\n');
            var idx = Array.FindIndex(lines, l => l.StartsWith("!"));
            if (idx < 0) return null;
            return string.Join(" ", lines.Skip(idx).Take(4)).Replace("\t", " ").Trim();
        }

        private static string ReadTail(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }
            catch { return null; }
        }

        private static (byte, byte, byte) ParseColor(string hex)
        {
            try
            {
                if (!string.IsNullOrEmpty(hex) && hex.StartsWith("#") && hex.Length >= 7)
                    return (Convert.ToByte(hex.Substring(1, 2), 16),
                            Convert.ToByte(hex.Substring(3, 2), 16),
                            Convert.ToByte(hex.Substring(5, 2), 16));
            }
            catch { }
            return (255, 255, 255);
        }

        private static string Hash(string s)
        {
            using var sha = SHA1.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
            return string.Concat(bytes.Take(12).Select(x => x.ToString("x2")));
        }

        private static (int code, string output) Run(string exe, string args, string cwd, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    WorkingDirectory = cwd,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                EnvironmentService.ApplyCleanTemp(psi);
                using var p = Process.Start(psi);
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    return (-1, "命令超时：" + Path.GetFileName(exe));
                }
                string outText = outTask.GetAwaiter().GetResult();
                string errText = errTask.GetAwaiter().GetResult();
                return (p.ExitCode, (outText + "\n" + errText).Trim());
            }
            catch (Exception ex)
            {
                return (-1, ex.Message);
            }
        }
    }

    public class LatexException : Exception
    {
        public LatexException(string message) : base(message) { }
    }
}
