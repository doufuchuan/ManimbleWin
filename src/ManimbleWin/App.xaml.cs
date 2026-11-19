using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace ManimbleWin
{
    public partial class App : Application
    {
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
