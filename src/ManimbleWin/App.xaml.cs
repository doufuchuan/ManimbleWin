using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace ManimbleWin
{
    public partial class App : Application
    {
        protected override void OnExit(ExitEventArgs e)
        {
            // 每个终端标签都有自己的 cmd.exe / powershell.exe，退出时一并回收，避免残留孤儿进程
            try
            {
                foreach (Window w in Windows)
                {
                    if (w.DataContext is ViewModels.MainViewModel vm) vm.Terminal.ShutdownAll();
                }
            }
            catch { }
            base.OnExit(e);
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += (s, args) =>
            {
                try
                {
                    string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                        "ManimbleWin-crash.log");
                    File.AppendAllText(path, DateTime.Now + "\n" + args.Exception + "\n----\n");
                }
                catch { }
                args.Handled = false;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                try
                {
                    string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                        "ManimbleWin-crash.log");
                    File.AppendAllText(path, DateTime.Now + "\n" + args.ExceptionObject + "\n----\n");
                }
                catch { }
            };
        }
    }
}
