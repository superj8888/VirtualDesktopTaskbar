using System.Threading;
using System.Windows;

namespace VirtualDesktopTaskbar;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, @"Local\VirtualDesktopTaskbar.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write("未处理异常: " + args.Exception);
            args.Handled = true; // 常驻小工具：尽量存活
        };

        Log.Write("========== 进程启动 ==========");
        var window = new MainWindow();
        MainWindow = window;
        try
        {
            window.Boot();
        }
        catch (Exception ex)
        {
            Log.Write("启动失败: " + ex);
            MessageBox.Show("虚拟桌面切换器启动失败，详见日志。\r\n" + ex.Message);
            Shutdown();
        }
    }
}
