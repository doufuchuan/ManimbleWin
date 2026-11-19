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
                string p = VenvPythonTarget;
                return File.Exists(p) ? p : null;
            }
        }

        private static string _venvDir;

        /// <summary>
        /// 我们创建 / 复用的 venv 目录。优先项目内 .venv（便于跟随项目迁移、不必写 AppData），
        /// 项目目录不可写时才退到用户数据目录。
        /// </summary>
        public static string VenvDir
        {
            get
            {
                if (_venvDir != null) return _venvDir;
                string proj = Path.Combine(ProjectRoot, ".venv");
                _venvDir = CanWriteTo(ProjectRoot) || Directory.Exists(proj)
                    ? proj
                    : Path.Combine(UserDataDir, "venv");
                return _venvDir;
            }
        }

        public static string VenvPythonTarget => Path.Combine(VenvDir, "Scripts", "python.exe");

        private static string _dataDir;

        /// <summary>
        /// 应用数据目录（venv 兜底位置、LaTeX 缓存、临时文件）。
        /// 并非所有机器都允许写入 %LOCALAPPDATA%（企业策略 / 安全软件 / 便携运行），
        /// 因此这里按顺序探测一个真正可写的目录，而不是假定 AppData 一定可用。
        /// </summary>
        public static string UserDataDir
        {
            get
            {
                if (_dataDir != null) return _dataDir;
                var candidates = new List<string>
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ManimbleWin"),
                    Path.Combine(ProjectRoot, ".data")
                };
                string tempRoot = Path.GetTempPath();
                if (!string.IsNullOrEmpty(tempRoot)) candidates.Add(Path.Combine(tempRoot, "ManimbleWin"));

                foreach (var c in candidates)
                {
                    if (TryPrepare(c)) { _dataDir = c; return c; }
                }
                _dataDir = Path.Combine(ProjectRoot, ".data");
                try { Directory.CreateDirectory(_dataDir); } catch { }
                return _dataDir;
            }
        }

        /// <summary>应用专属临时目录，规避受限环境下的 %TEMP% 不可写问题。</summary>
        public static string TempDir => Path.Combine(UserDataDir, "tmp");

        /// <summary>目录能否创建并写入（写一个探针文件后删除）。</summary>
        public static bool CanWriteTo(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return false;
            return TryPrepare(dir);
        }

        private static bool TryPrepare(string dir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dir)) return false;
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".write-probe-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                File.WriteAllText(probe, "ok");
                try { File.Delete(probe); } catch { }
                return true;
            }
            catch { return false; }
        }

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

        /// <summary>
        /// 给子进程设置可写的临时目录与 UTF-8 输出。
        /// Python 默认按系统代码页（中文 Windows 是 GBK）写错误信息，父进程按 UTF-8 读会变成乱码，
        /// 所以这里强制 PYTHONUTF8/PYTHONIOENCODING，保证日志里的 "拒绝访问" 之类能正常显示。
        /// </summary>
        public static void ApplyCleanTemp(ProcessStartInfo psi)
        {
            try
            {
                string appTmp = TempDir;
                Directory.CreateDirectory(appTmp);
                psi.Environment["TMP"] = appTmp;
                psi.Environment["TEMP"] = appTmp;
                psi.Environment["TMPDIR"] = appTmp;
            }
            catch { }

            // 宿主的 PYTHONPATH / PYTHONHOME 会被子解释器全盘继承，把外部 sitecustomize
            // 之类的钩子塞进 venv（有些钩子会接管 open/unlink，表现为 pip "装到一半不动"）。
            // 应用只认自己建的 venv，所以这些变量一律清掉。
            TryRemoveEnv(psi, "PYTHONPATH");
            TryRemoveEnv(psi, "PYTHONHOME");
            TryRemoveEnv(psi, "PYTHONSTARTUP");
            TryRemoveEnv(psi, "PYTHONEXECUTABLE");
            TryRemoveEnv(psi, "PYTHONUSERBASE");
            TryRemoveEnv(psi, "PIP_INDEX_URL");
            TryRemoveEnv(psi, "PIP_EXTRA_INDEX_URL");

            try
            {
                psi.Environment["PYTHONUTF8"] = "1";
                psi.Environment["PYTHONIOENCODING"] = "utf-8";
                // 用户级 site-packages 不参与，避免半装的第三方包干扰引擎。
                psi.Environment["PYTHONNOUSERSITE"] = "1";
                psi.Environment["PIP_DISABLE_PIP_VERSION_CHECK"] = "1";
                psi.Environment["PIP_NO_INPUT"] = "1";
                // %LOCALAPPDATA%\pip\cache 在受限环境里不可写，pip 会卡在写缓存上。
                string cache = Path.Combine(UserDataDir, "pipcache");
                Directory.CreateDirectory(cache);
                psi.Environment["PIP_CACHE_DIR"] = cache;
            }
            catch { }
        }

        private static void TryRemoveEnv(ProcessStartInfo psi, string key)
        {
            try { psi.Environment.Remove(key); } catch { }
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

            // 常见发行版的固定布局，避免递归扫盘（TeX Live 有几十万文件，全盘搜索会卡好几秒）
            var roots = new List<string>
            {
                @"C:\texlive", @"D:\texlive", @"E:\texlive", @"F:\texlive",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MiKTeX"),
                @"C:\Program Files\MiKTeX",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TinyTeX"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Roaming", "TinyTeX"),
            };

            foreach (var root in roots)
            {
                string hit = ProbeLatexUnder(root);
                if (hit != null) return hit;
            }
            return null;
        }

        /// <summary>在发行版根目录下按已知目录结构找 latex.exe（最多两层，不做递归）。</summary>
        private static string ProbeLatexUnder(string root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;

            // <root>/bin/windows/latex.exe（TinyTeX）
            foreach (var bin in new[] { "windows", "win32" })
            {
                string direct = Path.Combine(root, "bin", bin, "latex.exe");
                if (File.Exists(direct)) return direct;
            }

            // <root>/<year>/bin/<platform>/latex.exe（TeX Live）
            try
            {
                foreach (var year in Directory.GetDirectories(root))
                {
                    foreach (var bin in new[] { "windows", "win32" })
                    {
                        string p = Path.Combine(year, "bin", bin, "latex.exe");
                        if (File.Exists(p)) return p;
                    }
                    // <root>/<year>/bin/x64/latex.exe 之类
                    string binDir = Path.Combine(year, "bin");
                    if (Directory.Exists(binDir))
                    {
                        try
                        {
                            var found = Directory.GetFiles(binDir, "latex.exe", SearchOption.AllDirectories).FirstOrDefault();
                            if (found != null) return found;
                        }
                        catch { }
                    }
                }

                // MiKTeX：<root>/miktex/bin/x64/latex.exe
                foreach (var sub in new[] { Path.Combine(root, "miktex", "bin"), Path.Combine(root, "bin") })
                {
                    if (!Directory.Exists(sub)) continue;
                    try
                    {
                        var found = Directory.GetFiles(sub, "latex.exe", SearchOption.AllDirectories).FirstOrDefault();
                        if (found != null) return found;
                    }
                    catch { }
                }
            }
            catch { }
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
