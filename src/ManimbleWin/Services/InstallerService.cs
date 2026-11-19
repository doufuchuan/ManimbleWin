using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using ManimbleWin.Models;

namespace ManimbleWin.Services
{
    /// <summary>依赖安装动作：创建 venv + pip 安装 Manim 栈、安装 TinyTeX 宏包、打开下载页。</summary>
    public static class InstallerService
    {
        public delegate void LogFn(string message, bool isError);

        /// <summary>国内网络优先走清华镜像，失败再回落到官方 PyPI。</summary>
        public static bool UseMirror { get; set; } = true;

        public const string MirrorUrl = "https://pypi.tuna.tsinghua.edu.cn/simple";

        /// <summary>根据依赖的 InstallHint 执行安装。</summary>
        public static async Task<bool> Install(Dependency dep, LogFn log)
        {
            try
            {
                switch (dep.InstallHint)
                {
                    case "python":
                        OpenUrl(dep.InstallUrl);
                        log("已打开 Python 官网下载页。请安装 3.12 或 3.11 版本，勾选 “Add python.exe to PATH”，装完后回到依赖中心点“重新检测”。", false);
                        return true;
                    case "ffmpeg":
                        OpenUrl(dep.InstallUrl);
                        log("已打开 FFmpeg 下载页。也可忽略——pip 安装的 imageio-ffmpeg 会自带一个可用副本。", false);
                        return true;
                    case "tinytex":
                        OpenUrl(dep.InstallUrl);
                        log("已打开 TinyTeX 安装说明。若本机已有 TeX Live / MiKTeX，可直接点“安装/修复宏包”补装 Manim 所需宏包。", false);
                        return true;
                    case "manim":
                        return await InstallManimStack(log);
                    case "tex-packages":
                        return await InstallTexPackages(log);
                    default:
                        log("此依赖需要手动安装。", true);
                        return false;
                }
            }
            catch (Exception ex)
            {
                log("安装失败：" + ex.Message, true);
                return false;
            }
        }

        public static void OpenUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            try
            {
                var psi = new ProcessStartInfo { FileName = url, UseShellExecute = true };
                Process.Start(psi);
            }
            catch { }
        }

        /// <summary>创建/复用 venv 并安装 requirements.txt（含 manim）。返回是否成功。</summary>
        public static async Task<bool> InstallManimStack(LogFn log)
        {
            string python = EnvironmentService.FindPython(out string how);
            if (python == null)
            {
                log("未找到 Python。请先安装 Python 3.11+ 并加入 PATH。", true);
                return false;
            }
            log($"使用解释器：{python}（来源：{how}）", false);
            log($"数据目录：{EnvironmentService.UserDataDir}", false);

            string targetPython = await EnsureVenv(python, log);

            // 升级 pip：只在确实很老的时候做。这一步在受限网络里极易卡死（会占满 CPU 十几分钟），
            // 因此加超时并默认跳过，保证安装不会停在这里。
            string pipVer = GetPipVersion(targetPython);
            log($"pip 版本：{pipVer ?? "未知"}", false);
            if (pipVer == null || !Version.TryParse(pipVer, out var pv) || pv.Major < 23)
            {
                log("pip 版本较旧，尝试升级（最长 2 分钟）…", false);
                string pipIndex = UseMirror ? $"-i {MirrorUrl} " : "";
                var (upCode, _) = await RunAsync(targetPython,
                    $"-m pip install {pipIndex}--upgrade pip --disable-pip-version-check --no-input", log, 120000);
                if (upCode != 0) log("pip 升级未成功，继续使用当前版本。", true);
            }
            else
            {
                log("pip 已足够新，跳过升级。", false);
            }

            string req = Path.Combine(EnvironmentService.ProjectRoot, "engine", "requirements.txt");
            if (!File.Exists(req))
            {
                log($"找不到依赖清单：{req}", true);
                return false;
            }

            log("安装 Manim 及其依赖（首次可能需数分钟，请保持网络畅通）…", false);
            log("提示：下载完成后的“安装已收集的包”阶段 pip 会长时间不输出任何内容，这是正常的，请勿关闭窗口。", false);
            bool installed = await PipInstall(targetPython, $"-r \"{req}\"", log);
            if (!installed)
            {
                log("依赖安装失败。常见原因：网络中断、PyPI 不可达、缺少编译工具。可换镜像或稍后重试。", true);
                log("也可在“终端”标签里手动执行：", false);
                log($"  \"{targetPython}\" -m pip install -r \"{req}\" -i {MirrorUrl}", false);
                return false;
            }
            CleanupPipLeftovers();

            // 验证
            var check = EnvironmentService.RunCapture(targetPython,
                "-c \"import manim;print('MANIM',manim.__version__)\"", 40000);
            bool ok = check.code == 0 && check.output.Contains("MANIM");
            if (ok)
            {
                log($"✅ Manim 安装成功：{check.output.Trim()}", false);
                log($"引擎将使用：{targetPython}", false);
            }
            else
            {
                log("Manim 安装后仍无法导入，请查看上方 pip 输出。", true);
            }
            return ok;
        }

        /// <summary>
        /// 确保有一个可用的 venv：优先复用项目内 .venv；不存在时创建；
        /// 创建失败再尝试用户数据目录；都不行时退回当前 Python（不隔离但可用）。
        /// </summary>
        private static async Task<string> EnsureVenv(string basePython, LogFn log)
        {
            string venvDir = EnvironmentService.VenvDir;
            string target = EnvironmentService.VenvPythonTarget;

            if (File.Exists(target))
            {
                log($"复用已有虚拟环境：{venvDir}", false);
                return target;
            }

            log($"创建虚拟环境：{venvDir}", false);
            var (code, _) = await RunAsync(basePython, $"-m venv \"{venvDir}\"", log, 300000);
            if (File.Exists(target)) return target;

            log($"虚拟环境创建失败（退出码 {code}）。若目录不可写或被安全软件拦截，将尝试备用位置。", true);

            string alt = Path.Combine(EnvironmentService.UserDataDir, "venv");
            string altPython = Path.Combine(alt, "Scripts", "python.exe");
            if (!string.Equals(alt, venvDir, StringComparison.OrdinalIgnoreCase))
            {
                log($"尝试备用位置：{alt}", false);
                await RunAsync(basePython, $"-m venv \"{alt}\"", log, 300000);
                if (File.Exists(altPython))
                {
                    log($"已创建备用虚拟环境：{alt}", false);
                    return altPython;
                }
            }

            log("虚拟环境不可用，改为安装到当前 Python 环境（仍可用，但不隔离系统环境）。", true);
            return basePython;
        }

        /// <summary>读取 pip 版本号（如 25.0.1），失败返回 null。</summary>
        private static string GetPipVersion(string python)
        {
            var r = EnvironmentService.RunCapture(python, "-m pip --version", 30000);
            if (r.code != 0) return null;
            var m = System.Text.RegularExpressions.Regex.Match(r.output, @"pip\s+([0-9][0-9.]*)");
            return m.Success ? m.Groups[1].Value.TrimEnd('.') : null;
        }

        /// <summary>pip 安装：先镜像（可选），疑似网络问题时用官方源重试一次。</summary>
        private static async Task<bool> PipInstall(string python, string args, LogFn log)
        {
            var sources = new List<string>();
            if (UseMirror) sources.Add(MirrorUrl);
            sources.Add(null); // 官方 PyPI 兜底

            foreach (var src in sources)
            {
                string index = src == null ? "" : $"-i {src} ";
                string cmd = $"-m pip install {index}{args} --prefer-binary --disable-pip-version-check --no-input --progress-bar off --timeout 20 --retries 2";
                var (code, output) = await RunAsync(python, cmd, log, 1800000);

                if (code == 0) return true;
                if (code == -2) return false; // 已超时终止，重试没有意义

                if (src == null) { log($"pip 退出码 {code}，官方源同样失败。", true); return false; }
                if (!LooksLikeNetworkFailure(output))
                {
                    log($"pip 退出码 {code}，不是网络问题（可能是缺少编译工具或版本冲突），请查看上方输出。", true);
                    return false;
                }
                log($"pip 退出码 {code}，疑似网络问题，改用官方 PyPI 重试…", true);
            }
            return false;
        }

        private static bool LooksLikeNetworkFailure(string output)
        {
            if (string.IsNullOrEmpty(output)) return false;
            string[] keys =
            {
                "Connection", "connection", "timed out", "Timeout", "ReadTimeout", "SSLError",
                "ProxyError", "Max retries", "Temporary failure", "Network is unreachable",
                "Could not find a version", "No matching distribution", "网络"
            };
            foreach (var k in keys) if (output.Contains(k)) return true;
            return false;
        }

        /// <summary>
        /// 清掉 pip 在应用临时目录里留下的构建/解包残留。pip 每次被打断都会留一堆
        /// pip-build-tracker-*、pip-unpack-* 目录，攒多了既占空间也会拖慢下一次安装。
        /// 只清应用自己的 TempDir，不碰系统目录。
        /// </summary>
        private static void CleanupPipLeftovers()
        {
            try
            {
                string dir = EnvironmentService.TempDir;
                if (!Directory.Exists(dir)) return;
                string[] patterns = { "pip-build-tracker-*", "pip-download-*", "pip-unpack-*", "pip-ephem-wheel-cache-*", "pip-install-*", "pip-modern-metadata-*" };
                foreach (var pat in patterns)
                {
                    foreach (var d in Directory.EnumerateDirectories(dir, pat))
                    {
                        try { Directory.Delete(d, true); } catch { }
                    }
                }
            }
            catch { }
        }

        /// <summary>为检测到的 LaTeX 安装 Manim 所需宏包（通过 tlmgr 或 winget/choco）。</summary>
        public static async Task<bool> InstallTexPackages(LogFn log)
        {
            string latex = EnvironmentService.FindLatex();
            if (latex == null)
            {
                log("未检测到 LaTeX。请先安装 TinyTeX 或 TeX Live，再补装宏包。", true);
                EnvironmentService.RunCapture("cmd.exe", "/c start https://yihui.org/tinytex/");
                return false;
            }
            string dir = Path.GetDirectoryName(latex);
            string tlmgr = Path.Combine(dir, "tlmgr.exe");
            if (!File.Exists(tlmgr)) tlmgr = EnvironmentService.FindOnPath("tlmgr.exe");
            if (tlmgr == null)
            {
                log($"在 {dir} 未找到 tlmgr.exe。TeX Live 请用 TeX Manager 安装宏包；MiKTeX 会自动按需安装。", true);
                return false;
            }

            log("更新 tlmgr 自身…", false);
            await RunAsync(tlmgr, "update --self", log, 300000);
            string pkgs = string.Join(" ", EnvironmentService.TexPackages);
            log("安装 Manim 所需的 LaTeX 宏包…", false);
            await RunAsync(tlmgr, $"install {pkgs}", log, 900000);

            string check = EnvironmentService.CheckTex(latex);
            bool ok = check == "ok";
            log(ok ? "✅ LaTeX 宏包齐全，公式渲染可用。" : $"宏包检查结果为 {check}，部分公式可能无法渲染。", !ok);
            return ok;
        }

        /// <summary>异步运行命令，实时把 stdout/stderr 作为日志回流。返回 (退出码, 合并输出)；超时返回 -2。</summary>
        public static Task<(int code, string output)> RunAsync(string file, string args, LogFn log, int timeoutMs = 0)
        {
            return Task.Run(() =>
            {
                var sb = new StringBuilder();
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = file,
                        Arguments = args,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    };
                    EnvironmentService.ApplyCleanTemp(psi);
                    using (var p = Process.Start(psi))
                    {
                        if (p == null) return (-1, "");
                        p.OutputDataReceived += (s, e) => { if (e.Data != null) { sb.AppendLine(e.Data); log(e.Data, false); } };
                        p.ErrorDataReceived += (s, e) => { if (e.Data != null) { sb.AppendLine(e.Data); log(e.Data, true); } };
                        p.BeginOutputReadLine();
                        p.BeginErrorReadLine();

                        bool exited = p.WaitForExit(timeoutMs <= 0 ? int.MaxValue : timeoutMs);
                        if (!exited)
                        {
                            try { p.Kill(entireProcessTree: true); } catch { }
                            p.WaitForExit(3000);
                            log($"命令超时（{timeoutMs / 1000} 秒），已终止。", true);
                            return (-2, sb.ToString());
                        }
                        p.WaitForExit(2000);
                        return (p.ExitCode, sb.ToString());
                    }
                }
                catch (Exception ex)
                {
                    log("命令执行异常：" + ex.Message, true);
                    return (-1, sb.ToString());
                }
            });
        }
    }
}
