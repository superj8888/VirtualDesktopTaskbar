using static VirtualDesktopTaskbar.NativeMethods;

namespace VirtualDesktopTaskbar.Taskbar;

/// <summary>
/// 任务栏锚点查找：Shell_TrayWnd → TrayNotifyWnd。
///
/// TrayNotifyWnd 是通知区域（隐藏图标 ^、Wi-Fi、音量、电池、时钟）的整体容器，
/// 其左边缘即“^”的左边缘（没有隐藏图标时即快设组左边缘），
/// 因此它本身就是统一锚点，无需 UI Automation。
/// </summary>
internal static class NotificationAreaFinder
{
    public static IntPtr FindShellTrayWnd() =>
        FindWindowW("Shell_TrayWnd", null);

    public static IntPtr FindTrayNotify(IntPtr shellTrayWnd) =>
        shellTrayWnd == IntPtr.Zero
            ? IntPtr.Zero
            : FindWindowExW(shellTrayWnd, IntPtr.Zero, "TrayNotifyWnd", null);

    /// <summary>取任务栏与通知区域矩形（物理像素）。任一窗口不存在返回 false。</summary>
    public static bool TryGetRects(out RECT taskbar, out RECT trayNotify)
    {
        taskbar = default;
        trayNotify = default;
        var shell = FindShellTrayWnd();
        if (shell == IntPtr.Zero) return false;
        var tray = FindTrayNotify(shell);
        if (tray == IntPtr.Zero) return false;
        if (!GetWindowRect(shell, out taskbar) || !GetWindowRect(tray, out trayNotify)) return false;
        return taskbar.Height > 0 && trayNotify.Right > trayNotify.Left;
    }
}
