using System.Runtime.InteropServices;
using System.Windows.Threading;
using VirtualDesktopTaskbar.Taskbar;
using static VirtualDesktopTaskbar.NativeMethods;

namespace VirtualDesktopTaskbar;

/// <summary>
/// 全屏检测：前台窗口完整覆盖所在显示器（真全屏/无边框全屏）时通知隐藏小组件。
/// 最大化窗口的矩形是工作区（小于显示器），不会被误判。
/// </summary>
internal sealed class FullscreenWatcher
{
    private readonly IntPtr _selfHwnd;
    private readonly DispatcherTimer _timer;
    private bool _suppressed;

    /// <summary>进入/退出全屏时触发，参数 = 是否应显示小组件。</summary>
    public event Action<bool>? FullscreenChanged;

    public FullscreenWatcher(IntPtr selfHwnd)
    {
        _selfHwnd = selfHwnd;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _timer.Tick += (_, _) => Tick();
    }

    public void Start() => _timer.Start();

    private void Tick()
    {
        bool fullscreen = IsForegroundFullscreen();
        if (fullscreen != _suppressed)
        {
            _suppressed = fullscreen;
            FullscreenChanged?.Invoke(!fullscreen);
        }
    }

    private bool IsForegroundFullscreen()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _selfHwnd) return false;
        if (!IsWindowVisible(fg) || IsIconic(fg)) return false;
        if (fg == NotificationAreaFinder.FindShellTrayWnd()) return false;

        var ex = GetWindowLongPtr(fg, GWL_EXSTYLE).ToInt64();
        if ((ex & WS_EX_TOOLWINDOW) != 0) return false;

        if (DwmGetWindowAttribute(fg, DWMWA_CLOAKED, out uint cloaked, sizeof(uint)) == 0 && cloaked != 0)
            return false;

        // 只在“前台窗口盖住小组件所在的任务栏显示器”时隐藏；
        // 副屏全屏不影响主屏任务栏，不需要隐藏
        var taskbar = NotificationAreaFinder.FindShellTrayWnd();
        if (taskbar == IntPtr.Zero) return false;
        var monitor = MonitorFromWindow(taskbar, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(monitor, ref mi)) return false;
        if (!GetWindowRect(fg, out var r)) return false;

        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top
            && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }
}
