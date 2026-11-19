using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using ManimbleWin.Models;

namespace ManimbleWin.Services
{
    /// <summary>依赖安装动作：创建 venv + pip 安装 Manim 栈、安装 TinyTeX 宏包、打开下载页。</summary>
    public static class InstallerService
    {
        public delegate void LogFn(string message, bool isError);

        private static string VenvPython => Path.Combine(EnvironmentService.UserDataDir, "venv", "Scripts", "python.exe");

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

        /// <summary>创建项目 venv 并安装 requirements.txt（含 manim）。返回是否成功。</summary>
        public static async Task<bool> InstallManimStack(LogFn log)
        {
            string python = EnvironmentService.FindPython(out _);
            if (python == null)
            {
                log("未找到 Python。请先安装 Python 3.11+ 并加入 PATH。", true);
                return false;
            }
            log($"使用解释器：{python}", false);

            string targetPython = VenvPython;
            string venvDir = Path.GetDirectoryName(Path.GetDirectoryName(targetPython)); // .../venv
            if (!File.Exists(targetPython))
            {
                log("创建独立虚拟环境（不污染系统 Python）…", false);
                string r = await RunAsync(python, $"-m venv \"{venvDir}\"", log);
                if (!string.IsNullOrEmpty(r) && File.Exists(targetPython) == false)
                {
                    log("venv 创建可能失败，仍尝试继续。", true);
                }
            }
            else
            {
                log("虚拟环境已存在，跳过创建。", false);
            }

            if (!File.Exists(targetPython)) targetPython = python; // 回退到系统 Python

            log("升级 pip…", false);
            await RunAsync(targetPython, "-m pip install --upgrade pip", log);

            string req = Path.Combine(EnvironmentService.ProjectRoot, "engine", "requirements.txt");
            if (!File.Exists(req))
            {
                log($"找不到依赖清单：{req}", true);
                return false;
            }
            log("安装 Manim 及其依赖（首次可能需数分钟）…", false);
            await RunAsync(targetPython, $"-m pip install -r \"{req}\"", log);

            // 验证
            var check = EnvironmentService.RunCapture(targetPython,
                "-c \"import manim;print('MANIM',manim.__version__)\"", 30000);
            bool ok = check.code == 0 && check.output.Contains("MANIM");
            log(ok ? $"✅ Manim 安装成功：{check.output.Trim()}" : "Manim 安装后仍无法导入，请查看上方 pip 输出。", !ok);
            return ok;
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
            await RunAsync(tlmgr, "update --self", log);
            string pkgs = string.Join(" ", EnvironmentService.TexPackages);
            log("安装 Manim 所需的 LaTeX 宏包…", false);
            await RunAsync(tlmgr, $"install {pkgs}", log);

            string check = EnvironmentService.CheckTex(latex);
            bool ok = check == "ok";
            log(ok ? "✅ LaTeX 宏包齐全，公式渲染可用。" : $"宏包检查结果为 {check}，部分公式可能无法渲染。", !ok);
            return ok;
        }

        /// <summary>异步运行命令，实时把 stdout/stderr 作为日志回流。</summary>
        public static Task<string> RunAsync(string file, string args, LogFn log)
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
                        if (p == null) return "";
                        p.OutputDataReceived += (s, e) => { if (e.Data != null) { sb.AppendLine(e.Data); log(e.Data, false); } };
                        p.ErrorDataReceived += (s, e) => { if (e.Data != null) { sb.AppendLine(e.Data); log(e.Data, true); } };
                        p.BeginOutputReadLine();
                        p.BeginErrorReadLine();
                        p.WaitForExit();
                        p.WaitForExit(1000);
                    }
                }
                catch (Exception ex)
                {
                    log("命令执行异常：" + ex.Message, true);
                }
                return sb.ToString();
            });
        }
    }
}
