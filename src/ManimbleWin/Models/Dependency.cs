using System;
using System.Collections.ObjectModel;
using ManimbleWin.Helpers;

namespace ManimbleWin.Models
{
    /// <summary>依赖状态。</summary>
    public enum DependencyState
    {
        Checking,      // 正在检测
        Installed,     // 已安装且可用
        WrongVersion,  // 已安装但版本不满足
        Missing,       // 缺失
        Error          // 检测出错（如无法启动进程）
    }

    /// <summary>单个依赖项（Python / Manim / FFmpeg / LaTeX / 宏包）。</summary>
    public class Dependency : ObservableObject
    {
        private DependencyState _state = DependencyState.Checking;
        private string _detail = "—";
        private string _required = "";
        private bool _busy;

        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }

        public DependencyState State
        {
            get => _state;
            set
            {
                if (SetProperty(ref _state, value))
                {
                    RaisePropertyChanged(nameof(StateText));
                    RaisePropertyChanged(nameof(StateGlyph));
                    RaisePropertyChanged(nameof(StateColor));
                    RaisePropertyChanged(nameof(IsReady));
                }
            }
        }

        /// <summary>检测到的路径 / 版本等细节。</summary>
        public string Detail
        {
            get => _detail;
            set => SetProperty(ref _detail, value);
        }

        /// <summary>要求（版本 / 形态）说明。</summary>
        public string Required
        {
            get => _required;
            set => SetProperty(ref _required, value);
        }

        /// <summary>安装动作是否进行中。</summary>
        public bool Busy
        {
            get => _busy;
            set => SetProperty(ref _busy, value);
        }

        /// <summary>是否可一键安装/修复（手动型依赖为 false）。</summary>
        public bool CanInstall { get; set; }

        /// <summary>安装方式的内部标识（venv-pip / tinytex / open-url 等）。</summary>
        public string InstallHint { get; set; }

        /// <summary>点击"安装/修复"时要打开的下载/文档地址（手动型）。</summary>
        public string InstallUrl { get; set; }

        public bool IsReady => State == DependencyState.Installed;

        public string StateGlyph
        {
            get
            {
                switch (State)
                {
                    case DependencyState.Installed: return "✓";
                    case DependencyState.WrongVersion: return "!";
                    case DependencyState.Missing: return "✕";
                    case DependencyState.Error: return "?";
                    default: return "…";
                }
            }
        }

        public string StateText
        {
            get
            {
                switch (State)
                {
                    case DependencyState.Installed: return "就绪";
                    case DependencyState.WrongVersion: return "版本不符";
                    case DependencyState.Missing: return "未安装";
                    case DependencyState.Error: return "检测失败";
                    default: return "检测中";
                }
            }
        }

        public string StateColor
        {
            get
            {
                switch (State)
                {
                    case DependencyState.Installed: return "#3FB950";
                    case DependencyState.WrongVersion: return "#D29922";
                    case DependencyState.Missing: return "#F85149";
                    case DependencyState.Error: return "#D29922";
                    default: return "#8B949E";
                }
            }
        }
    }

    /// <summary>一条安装/检测日志。</summary>
    public class SetupLogEntry
    {
        public DateTime Time { get; set; } = DateTime.Now;
        public string Message { get; set; }
        public bool IsError { get; set; }
        public string TimeText => Time.ToString("HH:mm:ss");
    }

    /// <summary>依赖检测汇总结果。</summary>
    public class DependencyReport
    {
        public ObservableCollection<Dependency> Items { get; } = new ObservableCollection<Dependency>();
        public bool AllReady
        {
            get
            {
                foreach (var d in Items)
                    if (d.State != DependencyState.Installed) return false;
                return true;
            }
        }

        public bool CoreReady
        {
            get
            {
                // 核心四项：Python / Manim / FFmpeg / LaTeX。宏包是可选增强。
                string[] core = { "python", "manim", "ffmpeg", "latex" };
                foreach (var d in Items)
                    if (Array.IndexOf(core, d.Id) >= 0 && d.State != DependencyState.Installed) return false;
                return true;
            }
        }
    }
}
