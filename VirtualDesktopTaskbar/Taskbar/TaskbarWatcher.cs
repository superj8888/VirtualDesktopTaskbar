using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using VirtualDesktopTaskbar.VirtualDesktop;

namespace VirtualDesktopTaskbar.Taskbar;

/// <summary>
/// 250ms 巡检：桌面状态轮询（同步高亮、自愈断连）+ 位置跟随。
/// 系统消息（TaskbarCreated / DISPLAYCHANGE / SETTINGCHANGE / DPICHANGED）
/// 由主窗口 WndProc 转发进来即时响应。
/// 显示与否由主窗口的状态机统一决定（锚点 × 全屏两个来源）。
/// </summary>
internal sealed class TaskbarWatcher
{
    private readonly MainWindow _window;
    private readonly VirtualDesktopManager _manager;
    private readonly DispatcherTimer _timer;

    /// <summary>Explorer 重启（TaskbarCreated）后触发：主窗口需重加托盘图标并重新固定窗口。</summary>
    public event Action? ShellRecreated;

    public TaskbarWatcher(MainWindow window, VirtualDesktopManager manager)
    {
        _window = window;
        _manager = manager;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _timer.Tick += (_, _) => Tick();
    }

    public void Start()
    {
        RepositionNow();
        _timer.Start();
    }

    public void OnShellRecreated()
    {
        _manager.TryInitialize();
        RepositionNow();
        ShellRecreated?.Invoke();
    }

    public void OnDisplayOrSettingsChanged() => RepositionNow();

    private void Tick()
    {
        _manager.Poll();          // 高亮同步（含 COM 断连自愈）
        RepositionNow();          // 位置跟随（任务栏/托盘/DPI/显示器变化）
        _window.EnsureAboveTaskbar(); // z 序保持：压回任务栏上方
    }

    private void RepositionNow()
    {
        // 任务栏在主显示器；DPI 跟随窗口当前所在显示器
        double dpi = VisualTreeHelper.GetDpi(_window).PixelsPerDip;
        _window.NotifyAnchor(
            TaskbarManager.TryCompute(_manager.DesktopCount, dpi, out var rect) ? rect : null);
    }
}
