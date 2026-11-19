using System;
using System.IO;
using ManimbleWin.Services;

namespace ManimbleWin
{
    /// <summary>应用级一次性标记（落盘到 LocalAppData，无管理员权限）。</summary>
    public static class AppFlags
    {
        private static string FlagPath => Path.Combine(EnvironmentService.UserDataDir, "setup-seen.flag");

        public static bool SetupSeen
        {
            get
            {
                try { return File.Exists(FlagPath); }
                catch { return false; }
            }
            set
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FlagPath));
                    if (value) File.WriteAllText(FlagPath, DateTime.Now.ToString("O"));
                    else if (File.Exists(FlagPath)) File.Delete(FlagPath);
                }
                catch { }
            }
        }

        /// <summary>供依赖中心"重置向导"用。</summary>
        public static void ResetSetup() => SetupSeen = false;
    }
}
