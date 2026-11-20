using System;
using System.Threading.Tasks;
using System.Windows.Input;
using ManimbleWin.Helpers;

namespace ManimbleWin.ViewModels
{
    /// <summary>
    /// 「Python 图表…」对话框的视图模型：多行代码编辑框 + 运行按钮 + 错误显示区。
    /// 真正的执行与落地由宿主从 runPlot 回调注入，返回 (是否成功, 状态/错误消息)。
    /// </summary>
    public class PythonPlotViewModel : ObservableObject
    {
        private readonly Func<string, Task<(bool ok, string message)>> _runPlot;

        // 预填一个 matplotlib 折线图示例。用户可写 plt.savefig(__out__) 或干脆不写——
        // 引擎会在包装脚本里注入 __out__ 变量，并在用户没生成文件时兜底 savefig。
        public const string SampleCode =
            "import numpy as np\n" +
            "import matplotlib.pyplot as plt\n\n" +
            "x = np.linspace(0, 2 * np.pi, 200)\n" +
            "plt.figure(figsize=(6, 4))\n" +
            "plt.plot(x, np.sin(x), label='sin(x)')\n" +
            "plt.plot(x, np.cos(x), label='cos(x)')\n" +
            "plt.xlabel('x')\n" +
            "plt.ylabel('y')\n" +
            "plt.title('Python 图表示例')\n" +
            "plt.legend()\n" +
            "plt.grid(True)\n" +
            "# 直接画即可，引擎会把当前图保存到 __out__。\n" +
            "# 也可以显式写：plt.savefig(__out__, bbox_inches='tight')\n";

        public PythonPlotViewModel(Func<string, Task<(bool ok, string message)>> runPlot)
        {
            _runPlot = runPlot;
            RunCommand = new RelayCommand(_ => _ = RunAsync(), _ => !IsBusy);
        }

        public ICommand RunCommand { get; }

        private string _code = SampleCode;
        public string Code { get => _code; set => SetProperty(ref _code, value); }

        private string _status = "点击「运行」在受控子进程里执行代码（60 秒超时）。";
        public string Status { get => _status; private set => SetProperty(ref _status, value); }

        private string _error = "";
        public string Error { get => _error; private set => SetProperty(ref _error, value); }

        private bool _busy;
        public bool IsBusy { get => _busy; private set { if (SetProperty(ref _busy, value)) (RunCommand as RelayCommand)?.RaiseCanExecuteChanged(); } }

        private async Task RunAsync()
        {
            if (IsBusy) return;
            IsBusy = true;
            Error = "";
            Status = "正在运行 Python 绘图代码…";
            try
            {
                var (ok, message) = await _runPlot(Code ?? "");
                if (ok) Status = message ?? "已运行并生成图表";
                else { Error = message ?? "运行失败"; Status = "运行失败，请查看下方错误。"; }
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                Status = "运行失败，请查看下方错误。";
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}
