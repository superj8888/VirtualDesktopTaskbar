using static VirtualDesktopTaskbar.NativeMethods;

namespace VirtualDesktopTaskbar.Taskbar;

/// <summary>小组件的覆盖矩形（物理像素）。</summary>
internal readonly record struct OverlayRect(int X, int Y, int Width, int Height);

/// <summary>
/// 根据任务栏锚点计算小组件位置：
/// Window.Left = TrayNotifyWnd.Left - Gap - Width，Y/Height 与任务栏对齐。
/// </summary>
internal static class TaskbarManager
{
    /// <summary>按钮节距（DIP）：34 按钮宽度 + 6 按钮间距。</summary>
    private const double ButtonPitchDip = 40;

    /// <summary>右侧内边距（DIP）。</summary>
    private const double PaddingDip = 8;

    /// <summary>与通知区域的间距（DIP）。</summary>
    private const double GapDip = 3;

    public static bool TryCompute(int buttonCount, double dpiScale, out OverlayRect target)
    {
        target = default;
        if (buttonCount < 1 || !NotificationAreaFinder.TryGetRects(out var bar, out var tray))
            return false;

        double s = dpiScale;
        int width = (int)Math.Round(buttonCount * ButtonPitchDip * s + PaddingDip * s);
        int gap = (int)Math.Round(GapDip * s);
        int x = tray.Left - gap - width;
        int y = bar.Top;

        if (x < bar.Left) return false; // 任务栏异常状态（如正在重建）不贴负位置

        target = new OverlayRect(x, y, width, bar.Height);
        return true;
    }

    /// <summary>任务栏是否处于自动隐藏状态。</summary>
    public static bool IsAutoHide()
    {
        var data = new APPBARDATA { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>() };
        return (SHAppBarMessage(ABM_GETSTATE, ref data).ToInt32() & ABS_AUTOHIDE) != 0;
    }
}
