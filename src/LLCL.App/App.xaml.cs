using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using WinRT.Interop;

namespace LLCL;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }

    /// <summary>崩溃日志目录（exe 同级 LLCL\Logs，无法确定时退回临时目录）。</summary>
    private static string CrashLogPath
    {
        get
        {
            try
            {
                var dir = Path.Combine(AppContext.BaseDirectory, "LLCL", "Logs");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "crash.log");
            }
            catch
            {
                return Path.Combine(Path.GetTempPath(), "LLCL-crash.log");
            }
        }
    }

    public App()
    {
        // 尽早挂上兜底处理：任何未捕获异常都写日志 + 弹窗，
        // 避免出现“双击毫无反应”这种无法诊断的情况。
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportFatal(e.ExceptionObject as Exception, "AppDomain");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ReportFatal(e.Exception, "TaskScheduler");
            e.SetObserved();
        };
        UnhandledException += (_, e) =>
        {
            ReportFatal(e.Exception, "XAML");
            e.Handled = true;
        };

        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            // 单实例
            var activated = AppInstance.GetCurrent().GetActivatedEventArgs();
            var main = AppInstance.FindOrRegisterForKey("LLCL_Main");
            if (!main.IsCurrent)
            {
                // 已存在实例：把激活请求转过去后退出当前进程
                _ = main.RedirectActivationToAsync(activated);
                Exit();
                return;
            }

            MainWindow = new MainWindow();
            MainWindow.Activate();
        }
        catch (Exception ex)
        {
            ReportFatal(ex, "OnLaunched");
            throw;
        }
    }

    /// <summary>把致命异常写入磁盘并尽力弹窗提示，方便用户反馈。</summary>
    private static void ReportFatal(Exception? ex, string source)
    {
        if (ex is null) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 来源: {source}");
            sb.AppendLine($"类型: {ex.GetType().FullName}");
            sb.AppendLine($"消息: {ex.Message}");
            sb.AppendLine($"OS: {Environment.OSVersion}");
            sb.AppendLine($"框架: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
            sb.AppendLine("堆栈:");
            sb.AppendLine(ex.ToString());
            sb.AppendLine(new string('-', 70));

            File.AppendAllText(CrashLogPath, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // 写日志失败不再抛出，避免递归崩溃
        }

        try
        {
            var message = $"""
                LLCL 遇到严重错误，已写入日志：
                {CrashLogPath}

                {ex.GetType().Name}: {ex.Message}
                """;

            // 优先用原生 MessageBox：此时 XAML 可能已不可用
            MessageBox(0, message, "LLCL 错误", 0x00000010 /* MB_ICONERROR */);
        }
        catch
        {
            // 忽略
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint hWnd, string text, string caption, uint type);
}
