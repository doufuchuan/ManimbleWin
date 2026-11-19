using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using ManimbleWin.Helpers;
using ManimbleWin.Models;
using ManimbleWin.Services;

namespace ManimbleWin.ViewModels
{
    /// <summary>依赖中心 / 首次启动向导的 ViewModel。</summary>
    public class SetupViewModel : ObservableObject
    {
        private readonly MainViewModel _main;
        private bool _isFirstRun;
        private string _progressText = "";
        private bool _isBusy;
        private bool _texPackagesAdded;

        public ObservableCollection<Dependency> Dependencies { get; } = new ObservableCollection<Dependency>();
        public ObservableCollection<SetupLogEntry> Logs { get; } = new ObservableCollection<SetupLogEntry>();

        public ICommand RefreshCommand { get; }
        public ICommand InstallCommand { get; }
        public ICommand InstallTexPackagesCommand { get; }
        public ICommand ContinueCommand { get; }
        public ICommand OpenUrlCommand { get; }

        public bool IsFirstRun
        {
            get => _isFirstRun;
            set { if (SetProperty(ref _isFirstRun, value)) RaisePropertyChanged(nameof(Title)); }
        }

        public string Title => IsFirstRun ? "首次启动 · 环境准备" : "依赖中心";

        public string ProgressText { get => _progressText; set { if (SetProperty(ref _progressText, value)) RaisePropertyChanged(nameof(ProgressVisible)); } }
        public bool ProgressVisible => !string.IsNullOrEmpty(_progressText);

        public bool IsBusy { get => _isBusy; set { if (SetProperty(ref _isBusy, value)) { RaisePropertyChanged(nameof(InstallEnabled)); } } }

        /// <summary>pip 走清华镜像（国内网络更稳），失败会自动回落到官方 PyPI。</summary>
        private bool _useMirror = InstallerService.UseMirror;
        public bool UseMirror
        {
            get => _useMirror;
            set
            {
                if (SetProperty(ref _useMirror, value)) InstallerService.UseMirror = value;
            }
        }

        public bool AllReady => EvaluateReady(false);
        public bool CoreReady => EvaluateReady(true);
        public bool InstallEnabled => !IsBusy;

        public string SummaryText
        {
            get
            {
                int ready = 0;
                foreach (var d in Dependencies) if (d.State == DependencyState.Installed) ready++;
                return $"{ready}/{Dependencies.Count} 项就绪";
            }
        }

        public string ContinueText => IsFirstRun
            ? (CoreReady ? "启动 Manimble" : "仍要继续（部分功能不可用）")
            : "关闭";

        public SetupViewModel(MainViewModel main, bool isFirstRun)
        {
            _main = main;
            _isFirstRun = isFirstRun;
            RefreshCommand = new RelayCommand(_ => Refresh());
            InstallCommand = new RelayCommand(p => Install(p as Dependency));
            InstallTexPackagesCommand = new RelayCommand(_ => AddTexPackages());
            ContinueCommand = new RelayCommand(_ => Continue());
            OpenUrlCommand = new RelayCommand(p => { if (p is string url) InstallerService.OpenUrl(url); });
            Refresh();
        }

        private bool EvaluateReady(bool coreOnly)
        {
            string[] core = { "python", "manim", "ffmpeg", "latex" };
            if (Dependencies.Count == 0) return false;
            foreach (var d in Dependencies)
            {
                if (coreOnly && Array.IndexOf(core, d.Id) < 0) continue; // 非核心项忽略
                if (d.State != DependencyState.Installed) return false;
            }
            return true;
        }

        private void RaiseSummary()
        {
            RaisePropertyChanged(nameof(AllReady));
            RaisePropertyChanged(nameof(CoreReady));
            RaisePropertyChanged(nameof(SummaryText));
            RaisePropertyChanged(nameof(ContinueText));
        }

        private void Log(string msg, bool error = false)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                Logs.Add(new SetupLogEntry { Message = msg, IsError = error });
                if (Logs.Count > 300) Logs.RemoveAt(0);
            });
        }

        public void Refresh()
        {
            if (IsBusy) return;
            ProgressText = "正在检测环境…";
            Dependencies.Clear();
            Task.Run(() =>
            {
                var report = EnvironmentService.Detect();
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    foreach (var d in report.Items) Dependencies.Add(d);
                    ProgressText = "";
                    RaiseSummary();
                });
                Log("环境检测完成：" + report.SummaryTextForLog());
            });
        }

        private void Install(Dependency dep)
        {
            if (dep == null || IsBusy) return;
            IsBusy = true;
            dep.Busy = true;
            ProgressText = $"正在处理：{dep.Name} …";
            Log($"开始：{dep.Name}");

            Task.Run(async () =>
            {
                bool ok = await InstallerService.Install(dep, (m, e) => Log(m, e));
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    dep.Busy = false;
                    IsBusy = false;
                });
                Log(ok ? $"{dep.Name}：操作已完成，建议重新检测。" : $"{dep.Name}：操作未完成。", !ok);

                // Manim 装好后直接让主窗口连引擎，省得用户再点一次
                if (ok && dep.Id == "manim") _main.ConnectFromTerminal();

                // 安装完自动重新检测
                Refresh();
            });
        }

        private void AddTexPackages()
        {
            if (IsBusy) return;
            IsBusy = true;
            var dep = new Dependency { Name = "LaTeX 宏包", Id = "tex-packages" };
            ProgressText = "正在补装 LaTeX 宏包…";
            Log("补装 Manim 所需的 LaTeX 宏包（tlmgr）…");
            Task.Run(async () =>
            {
                bool ok = await InstallerService.InstallTexPackages((m, e) => Log(m, e));
                Application.Current?.Dispatcher.Invoke(() => { IsBusy = false; _texPackagesAdded = ok; });
                Refresh();
            });
        }

        private void Continue()
        {
            var win = Application.Current?.Windows;
            if (win != null)
                foreach (System.Windows.Window w in win)
                    if (w is Views.SetupWindow) { w.DialogResult = true; w.Close(); break; }
        }
    }

    internal static class ReportLogExt
    {
        public static string SummaryTextForLog(this DependencyReport r)
        {
            int ready = 0;
            foreach (var d in r.Items) if (d.State == DependencyState.Installed) ready++;
            return $"{ready}/{r.Items.Count} 项就绪";
        }
    }
}
