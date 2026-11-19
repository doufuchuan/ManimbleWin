using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ManimbleWin.Models;

namespace ManimbleWin.Services
{
    /// <summary>
    /// 环境探测：定位 Python 解释器、检测 Manim / FFmpeg / LaTeX 及其宏包。
    /// 检测逻辑对齐原版 install_dependencies.sh 的 check-tex。
    /// </summary>
    public static class EnvironmentService
    {
        /// <summary>项目根（exe 所在目录向上回溯到含 engine/ 的目录）。</summary>
        public static string ProjectRoot
        {
            get
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                var d = new DirectoryInfo(dir);
                while (d != null)
                {
                    if (Directory.Exists(Path.Combine(d.FullName, "engine"))) return d.FullName;
                    d = d.Parent;
                }
                return dir;
            }
        }

        public static string EngineDir => Path.Combine(ProjectRoot, "engine");
        public static string SampleDeck => Path.Combine(ProjectRoot, "samples", "hello", "deck");

        /// <summary>项目内的 Python venv（若存在）。</summary>
        public static string VenvPython
        {
            get
            {
                string p = Path.Combine(ProjectRoot, ".venv", "Scripts", "python.exe");
                return File.Exists(p) ? p : null;
            }
        }

        /// <summary>用户级安装目录，用于存放我们创建的 venv。</summary>
        public static string UserDataDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ManimbleWin");

        private static ProcessStartInfo QuietPsi(string file, string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            ApplyCleanTemp(psi);
            return psi;
        }

        /// <summary>给子进程设置应用专属临时目录，规避受限环境下的 %TEMP% 权限问题。</summary>
        public static void ApplyCleanTemp(ProcessStartInfo psi)
        {
            try
            {
                string appTmp = Path.Combine(UserDataDir, "tmp");
                Directory.CreateDirectory(appTmp);
                psi.Environment["TMP"] = appTmp;
                psi.Environment["TEMP"] = appTmp;
                psi.Environment["TMPDIR"] = appTmp;
            }
            catch { }
        }

        /// <summary>运行命令，返回 (退出码, 合并输出)。超时返回 -1。</summary>
        public static (int code, string output) RunCapture(string file, string args, int timeoutMs = 20000, string workDir = null)
        {
            try
            {
                var psi = QuietPsi(file, args);
                if (!string.IsNullOrEmpty(workDir)) psi.WorkingDirectory = workDir;
                using (var p = Process.Start(psi))
                {
                    if (p == null) return (-1, "无法启动进程");
                    string outText = p.StandardOutput.ReadToEnd();
                    string errText = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return (-1, outText + errText);
                    }
                    return (p.ExitCode, outText + "\n" + errText);
                }
            }
            catch (Exception ex)
            {
                return (-1, ex.Message);
            }
        }

        #region Python

        /// <summary>
        /// 查找可用于运行引擎的 Python 解释器。优先项目 venv，其次 py -3.12/3.11，最后 PATH python。
        /// </summary>
        public static string FindPython(out string how)
        {
            // 1. 项目 venv
            string venv = VenvPython;
            if (venv != null) { how = "项目虚拟环境"; return venv; }

            // 2. py launcher（Windows 官方安装器自带）
            string py = FindOnPath("py.exe");
            if (py != null)
            {
                foreach (var ver in new[] { "3.12", "3.11", "3.13" })
                {
                    var r = RunCapture(py, $"-{ver} -c \"import sys;print(sys.executable)\"", 10000);
                    if (r.code == 0 && File.Exists(r.output.Trim()))
                    { how = $"py -{ver}"; return r.output.Trim(); }
                }
            }

            // 3. PATH 上的 python
            string python = FindOnPath("python.exe") ?? FindOnPath("python");
            if (python != null) { how = "PATH"; return python; }

            how = null;
            return null;
        }

        public static string FindOnPath(string name)
        {
            try
            {
                string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in pathEnv.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    string full = Path.Combine(dir.Trim('"'), name);
                    if (File.Exists(full)) return full;
                }
            }
            catch { }
            return null;
        }

        /// <summary>检测 Python 版本（返回如 3.12.10）。</summary>
        public static string PythonVersion(string python)
        {
            if (string.IsNullOrEmpty(python)) return null;
            var r = RunCapture(python, "-c \"import sys;print('.'.join(map(str,sys.version_info[:3])))\"", 10000);
            return r.code == 0 ? r.output.Trim().Split('\n')[0].Trim() : null;
        }

        /// <summary>检测某个 Python 是否装有 manim，返回版本；否则 null。</summary>
        public static string ManimVersion(string python)
        {
            if (string.IsNullOrEmpty(python)) return null;
            var r = RunCapture(python, "-c \"import manim;print(getattr(manim,'__version__','?'))\"", 25000);
            if (r.code == 0 && r.output.IndexOf("Traceback", StringComparison.OrdinalIgnoreCase) < 0)
                return r.output.Trim().Split('\n')[0].Trim();
            return null;
        }

        #endregion

        #region FFmpeg

        public static string FindFFmpeg()
        {
            string f = FindOnPath("ffmpeg.exe");
            if (f != null) return f;
            // imageio-ffmpeg 会自带一个二进制
            string python = VenvPython;
            if (python != null)
            {
                var r = RunCapture(python,
                    "-c \"import imageio_ffmpeg;print(imageio_ffmpeg.get_ffmpeg_exe())\"", 15000);
                if (r.code == 0 && File.Exists(r.output.Trim())) return r.output.Trim();
            }
            return null;
        }

        public static string FFmpegVersion(string ffmpeg)
        {
            if (string.IsNullOrEmpty(ffmpeg)) return null;
            var r = RunCapture(ffmpeg, "-version", 10000);
            if (r.code != 0) return null;
            var m = Regex.Match(r.output, @"ffmpeg version\s+(\S+)");
            return m.Success ? m.Groups[1].Value : "found";
        }

        #endregion

        #region LaTeX

        public static string FindLatex()
        {
            string latex = FindOnPath("latex.exe") ?? FindOnPath("latex");
            if (latex != null) return latex;
            // 常见 TeX Live / MiKTeX 路径
            foreach (var baseDir in new[]
            {
                @"E:\texlive", @"C:\texlive", @"C:\Program Files\MiKTeX", @"C:\Users\Administrator\AppData\Local\Programs\MiKTeX"
            })
            {
                if (!Directory.Exists(baseDir)) continue;
                try
                {
                    var found = Directory.GetFiles(baseDir, "latex.exe", SearchOption.AllDirectories).FirstOrDefault();
                    if (found != null) return found;
                }
                catch { }
            }
            return null;
        }

        /// <summary>根据原版 check-tex：latex + dvisvgm + 关键宏包齐全才算 ok。</summary>
        public static string CheckTex(string latex)
        {
            if (string.IsNullOrEmpty(latex)) return "missing";
            string dir = Path.GetDirectoryName(latex);
            string dvisvgm = Path.Combine(dir, "dvisvgm.exe");
            if (!File.Exists(dvisvgm) && FindOnPath("dvisvgm.exe") == null) return "packages";

            string kpsewhich = Path.Combine(dir, "kpsewhich.exe");
            if (!File.Exists(kpsewhich)) kpsewhich = FindOnPath("kpsewhich.exe");
            if (kpsewhich == null) return "packages";

            string[] need = { "standalone.cls", "preview.sty", "dsfont.sty", "relsize.sty", "xcolor.sty", "amsmath.sty" };
            var missing = new List<string>();
            foreach (var f in need)
            {
                var r = RunCapture(kpsewhich, f, 8000, dir);
                if (r.code != 0 || string.IsNullOrWhiteSpace(r.output)) missing.Add(f);
            }
            return missing.Count == 0 ? "ok" : "packages";
        }

        public static string LatexVersion(string latex)
        {
            if (string.IsNullOrEmpty(latex)) return null;
            var r = RunCapture(latex, "--version", 10000);
            if (r.code != 0) return null;
            var m = Regex.Match(r.output, @"pdfTeX\s+([\d.]+)");
            return m.Success ? "pdfTeX " + m.Groups[1].Value : "found";
        }

        /// <summary>Manim 的 TeX 模板所用宏包（对齐原版 TEX_PACKAGES）。</summary>
        public static readonly string[] TexPackages =
        {
            "latex-bin", "dvisvgm", "standalone", "preview", "babel-english", "doublestroke",
            "everysel", "fundus-calligra", "mathastext", "physics", "ragged2e", "relsize",
            "rsfs", "setspace", "tipa", "wasy", "wasysym", "xcolor", "xkeyval", "cbfonts-fd",
            "jknapltx", "microtype", "multitoc", "prelim2e", "count1to", "frcursive", "amsfonts", "amsmath"
        };

        #endregion

        /// <summary>执行完整检测。</summary>
        public static DependencyReport Detect()
        {
            var report = new DependencyReport();
            string how;
            string python = FindPython(out how);
            string pyVer = PythonVersion(python);
            bool pyOk = pyVer != null && Version.TryParse(pyVer, out var v) && v.Major == 3 && v.Minor >= 11;

            report.Items.Add(new Dependency
            {
                Id = "python", Name = "Python 3.11+",
                Description = "运行 Manimble 引擎与 Manim 的 Python 解释器",
                Required = "3.11 或更高（检测到 3.13/3.12 均可）",
                State = python == null ? DependencyState.Missing
                      : pyOk ? DependencyState.Installed : DependencyState.WrongVersion,
                Detail = python == null ? "未找到解释器" : $"{pyVer}  ·  {python}  ·  {how}",
                CanInstall = true, InstallHint = "python",
                InstallUrl = "https://www.python.org/downloads/windows/"
            });

            string manimVer = ManimVersion(python);
            bool manimOk = manimVer != null;
            report.Items.Add(new Dependency
            {
                Id = "manim", Name = "Manim Community",
                Description = "幻灯片场景的真实渲染引擎（requirements.txt 固定 0.21.0）",
                Required = "manim==0.21.0（含 manim-slides / Pillow / matplotlib / python-pptx）",
                State = !pyOk && python == null ? DependencyState.Missing
                      : manimOk ? DependencyState.Installed : DependencyState.Missing,
                Detail = manimOk ? $"manim {manimVer}  ·  来自 {how ?? "Python"}" : "未安装（可一键安装到项目虚拟环境）",
                CanInstall = true, InstallHint = "manim"
            });

            string ffmpeg = FindFFmpeg();
            string ffVer = FFmpegVersion(ffmpeg);
            report.Items.Add(new Dependency
            {
                Id = "ffmpeg", Name = "FFmpeg",
                Description = "导出 MP4 动画视频所需",
                Required = "任意近期版本（imageio-ffmpeg 自带的也可用）",
                State = ffmpeg != null ? DependencyState.Installed : DependencyState.Missing,
                Detail = ffmpeg != null ? $"{ffVer}  ·  {ffmpeg}" : "未找到 ffmpeg.exe",
                CanInstall = true, InstallHint = "ffmpeg",
                InstallUrl = "https://www.gyan.dev/ffmpeg/builds/"
            });

            string latex = FindLatex();
            string texCheck = CheckTex(latex);
            string texVer = LatexVersion(latex);
            DependencyState texState;
            if (texCheck == "missing") texState = DependencyState.Missing;
            else if (texCheck == "ok") texState = DependencyState.Installed;
            else texState = DependencyState.WrongVersion;
            report.Items.Add(new Dependency
            {
                Id = "latex", Name = "LaTeX（TinyTeX / TeX Live）",
                Description = "渲染数学公式与 Beamer 导出（Manim 的 Tex 需要）",
                Required = "TinyTeX + Manim 宏包，或已装 TeX Live / MiKTeX 且宏包齐全",
                State = texState,
                Detail = latex == null ? "未找到 LaTeX 发行版"
                       : $"{texVer}  ·  {latex}  ·  宏包检查: {texCheck}",
                CanInstall = true, InstallHint = "tinytex",
                InstallUrl = "https://yihui.org/tinytex/"
            });

            return report;
        }
    }
}
