using System;
using System.Collections.Generic;
using System.IO;
using ManimbleWin.Helpers;
using ManimbleWin.Models;

namespace ManimbleWin.ViewModels
{
    /// <summary>
    /// 「导出为」对话框的视图模型：显示格式说明 + 输出路径 + 该格式专属的选项。
    ///
    /// <para>为什么单独成类：选项的可见性、路径的补全与校验都是纯逻辑，
    /// 放在 XAML/code-behind 里就没法测。窗口只负责弹 <c>SaveFileDialog</c> 和返回 <see cref="ToRequest"/>。</para>
    ///
    /// <para>范围（对齐 T04）：PPTX / MP4 是 P0，Beamer(.tex) / Python 是 P1；
    /// Keynote 在 Windows 上必然失败，这里不给确认入口（菜单项已禁用）。</para>
    /// </summary>
    public class ExportDialogViewModel : ObservableObject
    {
        private string _outPath = "";
        private bool _builds = true;
        private bool _editable = true;
        private int _height = 720;
        private double _hold = 2;
        private int _fps = 30;
        private string _error = "";

        /// <param name="kind">导出目标。</param>
        /// <param name="seed">MainViewModel 给出的默认请求（建议文件名、builds/editable、分辨率等）。</param>
        public ExportDialogViewModel(ExportKind kind, ExportRequest seed = null)
        {
            Kind = kind;
            _outPath = seed?.OutPath ?? "";
            _builds = seed?.Builds ?? true;
            _editable = seed?.Editable ?? true;
            _height = seed?.Height ?? 720;
            _hold = seed?.Hold ?? 2;
            _fps = seed?.Fps ?? 30;
            Validate();
        }

        /// <summary>导出目标（构造后不变）。</summary>
        public ExportKind Kind { get; }

        /// <summary>对话框标题用的格式名。</summary>
        public string KindLabel
        {
            get
            {
                switch (Kind)
                {
                    case ExportKind.Beamer: return "Beamer 源文件（.tex）";
                    case ExportKind.Movie: return "影片（.mp4）";
                    case ExportKind.Python: return "Python 源码（.py）";
                    case ExportKind.KeynoteDisabled: return "Keynote（.key）· Windows 不支持";
                    default: return "PowerPoint 演示文稿（.pptx）";
                }
            }
        }

        /// <summary>一句话说明这个格式会产出什么（含已知限制）。</summary>
        public string KindHint
        {
            get
            {
                switch (Kind)
                {
                    case ExportKind.Beamer:
                        return "导出可编译的 Beamer 源文件；本机 TeX 能编译时会顺带产出 PDF（PDF 编译失败不算导出失败）。";
                    case ExportKind.Movie:
                        return "把每一页逐个 build 渲染成视频。页数多、分辨率高时耗时会明显变长，可随时在忙碌条上取消。";
                    case ExportKind.Python:
                        return "导出与 deck 等价的 Python 源码（单文件），便于放进别的工程或做版本管理。";
                    case ExportKind.KeynoteDisabled:
                        return "Windows 上无法导出 Keynote：引擎依赖 macOS 的 Keynote.app 与 AppleScript。";
                    default:
                        return "导出可编辑文本的 PowerPoint。取消「逐 build 导出」时每页只出最终画面。";
                }
            }
        }

        /// <summary>PPTX / Beamer 才有「逐 build」选项。</summary>
        public bool ShowBuildsOption => Kind == ExportKind.Pptx || Kind == ExportKind.Beamer;

        /// <summary>只有 PPTX 能选「可编辑文本 / 纯图片」。</summary>
        public bool ShowEditableOption => Kind == ExportKind.Pptx;

        /// <summary>MP4 专属选项（分辨率 / 停帧 / 帧率）。</summary>
        public bool ShowMovieOptions => Kind == ExportKind.Movie;

        /// <summary>输出文件绝对路径。</summary>
        public string OutPath
        {
            get => _outPath;
            set
            {
                if (!SetProperty(ref _outPath, value)) return;
                Validate();
            }
        }

        /// <summary>逐 build 导出（引擎 builds）。</summary>
        public bool Builds
        {
            get => _builds;
            set => SetProperty(ref _builds, value);
        }

        /// <summary>可编辑文本（true）还是纯图片（false），引擎 editable。</summary>
        public bool Editable
        {
            get => _editable;
            set => SetProperty(ref _editable, value);
        }

        /// <summary>MP4 分辨率高度：720 / 1080 / 1440 / 2160。</summary>
        public int Height
        {
            get => _height;
            set => SetProperty(ref _height, value);
        }

        /// <summary>MP4 每页停帧秒数。</summary>
        public double Hold
        {
            get => _hold;
            set => SetProperty(ref _hold, value);
        }

        /// <summary>MP4 帧率（1..120）。</summary>
        public int Fps
        {
            get => _fps;
            set => SetProperty(ref _fps, value);
        }

        /// <summary>分辨率候选（引擎推荐 720，最高 2160）。</summary>
        public IReadOnlyList<int> Heights { get; } = new[] { 720, 1080, 1440, 2160 };

        /// <summary>停帧候选（秒）。</summary>
        public IReadOnlyList<double> HoldOptions { get; } = new[] { 0.5, 1, 2, 3, 5 };

        /// <summary>帧率候选。</summary>
        public IReadOnlyList<int> FpsOptions { get; } = new[] { 24, 30, 60 };

        /// <summary>当前输入的问题（空字符串表示可以导出）。</summary>
        public string Error
        {
            get => _error;
            private set
            {
                if (!SetProperty(ref _error, value)) return;
                RaisePropertyChanged(nameof(HasError));
                RaisePropertyChanged(nameof(CanExport));
            }
        }

        public bool HasError => !string.IsNullOrEmpty(Error);

        /// <summary>「导出」按钮是否可用（路径非空且在已有目录里）。</summary>
        public bool CanExport => !string.IsNullOrEmpty(OutPath) && IsUsablePath(OutPath);

        /// <summary><c>SaveFileDialog</c> 用的过滤器。</summary>
        public string FileFilter
        {
            get
            {
                switch (Kind)
                {
                    case ExportKind.Movie: return "影片 (.mp4)|*.mp4";
                    case ExportKind.Beamer: return "Beamer 源文件 (.tex)|*.tex";
                    case ExportKind.Python: return "Python 源码 (.py)|*.py";
                    default: return "PowerPoint 演示文稿 (.pptx)|*.pptx";
                }
            }
        }

        /// <summary><c>SaveFileDialog</c> 用的默认扩展名。</summary>
        public string DefaultExtension => ExtensionFor(Kind);

        /// <summary>补全扩展名、校验路径；返回是否可以导出。</summary>
        public bool Validate()
        {
            string path = (_outPath ?? "").Trim();
            if (path.Length == 0) { Error = "请选择导出位置"; return false; }

            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception) { Error = "这个路径不可用"; return false; }

            string dir = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                Error = "导出位置不存在：" + (dir ?? "");
                return false;
            }
            if (Directory.Exists(full))
            {
                Error = "这个路径是一个文件夹，请给出文件名";
                return false;
            }
            Error = "";
            return true;
        }

        /// <summary>把界面上的输入落成一次导出请求（自动补扩展名）。</summary>
        public ExportRequest ToRequest()
        {
            var req = new ExportRequest
            {
                Kind = Kind,
                OutPath = WithExtension((_outPath ?? "").Trim(), ExtensionFor(Kind)),
                Builds = _builds,
                Editable = _editable,
                Height = _height,
                Hold = _hold,
                Fps = _fps,
            };
            return req;
        }

        /// <summary>各格式的扩展名。</summary>
        public static string ExtensionFor(ExportKind kind)
        {
            switch (kind)
            {
                case ExportKind.Movie: return ".mp4";
                case ExportKind.Beamer: return ".tex";
                case ExportKind.Python: return ".py";
                default: return ".pptx";
            }
        }

        /// <summary>路径可用性：父目录存在、且不是已存在的文件夹。</summary>
        private static bool IsUsablePath(string path)
        {
            try
            {
                string full = Path.GetFullPath(path.Trim());
                string dir = Path.GetDirectoryName(full);
                return !string.IsNullOrEmpty(dir) && Directory.Exists(dir) && !Directory.Exists(full);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>没有扩展名时补上当前格式的扩展名（用户自己写了别的扩展名就按用户写的来）。</summary>
        private static string WithExtension(string path, string ext)
        {
            if (string.IsNullOrEmpty(path)) return path;
            return Path.HasExtension(path) ? path : path + ext;
        }
    }
}
