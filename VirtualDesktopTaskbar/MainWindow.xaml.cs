using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using VirtualDesktopTaskbar.Taskbar;
using VirtualDesktopTaskbar.VirtualDesktop;
using static VirtualDesktopTaskbar.NativeMethods;

namespace VirtualDesktopTaskbar;

/// <summary>
/// 任务栏小组件窗口：无边框、透明、Topmost、不抢焦点（NOACTIVATE + TOOLWINDOW）。
/// 显示状态由“任务栏锚点”与“全屏”两个来源共同决定（见 UpdateVisibility）。
/// </summary>
public partial class MainWindow : Window
{
    private readonly VirtualDesktopManager _manager = new();
    private TaskbarWatcher? _taskbarWatcher;
    private FullscreenWatcher? _fullscreenWatcher;

    private IntPtr _hwnd;
    private uint _taskbarCreatedMsg;
    private uint _trayCallbackMsg;
    private NOTIFYICONDATAW _trayData;
    private IntPtr _trayIconHandle;
    private ContextMenu? _trayMenu;
    private bool _trayAdded;
    private bool _degradeNotified;
    private bool _everNative;
    private bool _pinnedOk;

    // 可见性状态机
    private OverlayRect? _pendingRect;
    private OverlayRect _appliedRect;
    private bool _shown;
    private bool _fullscreenActive;

    public MainWindow()
    {
        InitializeComponent();
        RefreshTheme();
    }

    // ===== 启动 =====

    public void Boot()
    {
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        ApplyNoActivateStyles(_hwnd);

        var source = HwndSource.FromHwnd(_hwnd)!;
        source.AddHook(WndProc);
        _taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");
        _trayCallbackMsg = RegisterWindowMessageW("VirtualDesktopTaskbar.Tray");

        _manager.StateChanged += OnManagerStateChanged;
        bool native = _manager.TryInitialize();
        _everNative = native;
        Log.Write($"启动: 模式={_manager.Mode}, {_manager.ModeDetail}, 桌面数={_manager.DesktopCount}");

        RebuildButtons();
        UpdateHighlight();

        _taskbarWatcher = new TaskbarWatcher(this, _manager);
        _taskbarWatcher.ShellRecreated += OnShellRecreated;
        _taskbarWatcher.Start();

        _fullscreenWatcher = new FullscreenWatcher(_hwnd);
        _fullscreenWatcher.FullscreenChanged += NotifyFullscreen;
        _fullscreenWatcher.Start();

        try
        {
            AddTrayIcon();
        }
        catch (Exception ex)
        {
            // 托盘图标失败不影响小组件本体
            Log.Write("托盘图标创建失败: " + ex.Message);
        }

        EnsurePinned();
        if (!native) NotifyDegrade();
    }

    /// <summary>把小组件固定到所有桌面。每次从降级恢复到 Native 后都要重试
    /// （Explorer 重启后不能假设固定状态仍有效）；失败则由 MoveWindowToDesktop 跟踪兜底。</summary>
    private void EnsurePinned()
    {
        if (_pinnedOk || _manager.Mode != VirtualDesktopManager.VdMode.Native) return;
        _pinnedOk = _manager.TryPinWindow(_hwnd);
        Log.Write(_pinnedOk ? "窗口已固定到所有桌面 (PinView)" : "PinView 失败，改用 MoveWindowToDesktop 跟踪");
    }

    private static void ApplyNoActivateStyles(IntPtr hwnd)
    {
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE,
            new IntPtr(ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST));
    }

    // ===== 可见性状态机 =====

    /// <summary>任务栏锚点有效（带矩形）或无效（Explorer 重启中）。</summary>
    internal void NotifyAnchor(OverlayRect? rect)
    {
        _pendingRect = rect;
        UpdateVisibility();
    }

    /// <summary>进入/退出全屏。</summary>
    internal void NotifyFullscreen(bool show)
    {
        _fullscreenActive = !show;
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        bool shouldShow = _pendingRect.HasValue && !_fullscreenActive;

        if (shouldShow)
        {
            var rect = _pendingRect!.Value;
            if (rect != _appliedRect)
            {
                ApplyBounds(rect);
                _appliedRect = rect;
            }
            if (!_shown)
            {
                ShowOverlay();
                _shown = true;
            }
        }
        else if (_shown)
        {
            HideOverlay();
            _shown = false;
        }
    }

    private void ApplyBounds(OverlayRect rect)
    {
        _ = SetWindowPos(_hwnd, HwndTopmost,
            rect.X, rect.Y, rect.Width, rect.Height,
            SWP_NOACTIVATE);
        _appliedRect = rect;
    }

    /// <summary>任务栏同样位于 topmost 带内，它重排（切桌面、托盘刷新等）时会压到本组件，
    /// 每个巡检周期把组件重新放回 topmost 带最顶端（必然高于任务栏）。</summary>
    internal void EnsureAboveTaskbar()
    {
        if (!_shown) return;
        _ = SetWindowPos(_hwnd, HwndTopmost, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private void ShowOverlay()
    {
        Visibility = Visibility.Visible;
        // WPF Visibility 之外再确保原生窗口位被置位（双层保险）
        _ = SetWindowPos(_hwnd, HwndTopmost, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        Log.Write($"ShowOverlay: native visible={IsWindowVisible(_hwnd)}");
    }

    private void HideOverlay()
    {
        Visibility = Visibility.Hidden;
        _ = SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_HIDEWINDOW);
    }

    // ===== 按钮 =====

    private void RebuildButtons()
    {
        ButtonPanel.Children.Clear();
        int count = Math.Min(_manager.DesktopCount, 9);
        for (int i = 0; i < count; i++)
        {
            int index = i;
            var button = new Button
            {
                Style = (Style)Resources["DesktopButtonStyle"],
                Content = (i + 1).ToString(),
                CommandParameter = index,
                Tag = "",
                ToolTip = $"桌面 {i + 1}",
            };
            button.Click += (_, _) => _manager.SwitchTo(index);
            ButtonPanel.Children.Add(button);
        }
    }

    private void UpdateHighlight()
    {
        int current = _manager.CurrentIndex;
        for (int i = 0; i < ButtonPanel.Children.Count; i++)
            ((Button)ButtonPanel.Children[i]).Tag = i == current ? "cur" : "";
    }

    private void OnManagerStateChanged()
    {
        int expected = Math.Min(_manager.DesktopCount, 9);
        if (ButtonPanel.Children.Count != expected)
        {
            RebuildButtons();
            _taskbarWatcher?.OnDisplayOrSettingsChanged(); // 按钮数变了 → 宽度变化 → 立即重定位
        }
        UpdateHighlight();

        if (_manager.Mode == VirtualDesktopManager.VdMode.Native)
        {
            _everNative = true;
            EnsurePinned(); // 从降级恢复后重新固定
        }
        else
        {
            _pinnedOk = false;
            if (_everNative && !_degradeNotified)
            {
                // 运行中原生控制失效（如系统更新改了 ABI）
                NotifyDegrade();
                RebuildButtons();
                _taskbarWatcher?.OnDisplayOrSettingsChanged();
            }
        }
    }

    private void OnShellRecreated()
    {
        // Explorer 重启：COM 已在 watcher 里重建，这里补窗口固定与托盘图标
        if (_manager.Mode == VirtualDesktopManager.VdMode.Native)
            _ = _manager.TryPinWindow(_hwnd);
        ReAddTrayIcon();
    }

    // ===== 主题 =====

    private void RefreshTheme()
    {
        bool light = ReadDword(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "SystemUsesLightTheme") == 1;

        Resources["Vd.FgBrush"] = light
            ? new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B))
            : new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
        Resources["Vd.HoverBrush"] = light
            ? new SolidColorBrush(Color.FromArgb(0x14, 0x00, 0x00, 0x00))
            : new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));

        var accent = ReadAccentColor();
        Resources["Vd.AccentBrush"] = new SolidColorBrush(accent);
        double luminance = (0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B) / 255.0;
        Resources["Vd.AccentFgBrush"] = luminance > 0.6 ? Brushes.Black : Brushes.White;
    }

    private static Color ReadAccentColor()
    {
        // AccentColorMenu 为 COLORREF（0x00BBGGRR）
        if (Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent",
                "AccentColorMenu", null) is int colorref)
        {
            return Color.FromRgb((byte)colorref, (byte)(colorref >> 8), (byte)(colorref >> 16));
        }
        return Color.FromRgb(0x00, 0x78, 0xD4); // Win11 默认强调色兜底
    }

    private static int ReadDword(string keyPath, string valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(valueName) is int v ? v : 0;
        }
        catch
        {
            return 0;
        }
    }

    // ===== 托盘 =====

    private void AddTrayIcon()
    {
        _trayIconHandle = CreateTrayIconHandle(16, ReadAccentColor());
        _trayData = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = _trayCallbackMsg,
            hIcon = _trayIconHandle,
            szTip = "虚拟桌面切换器",
        };
        _trayAdded = Shell_NotifyIconW(NIM_ADD, ref _trayData);
    }

    private void ReAddTrayIcon()
    {
        if (_trayAdded) return;
        _trayAdded = Shell_NotifyIconW(NIM_ADD, ref _trayData);
    }

    private void RemoveTrayIcon()
    {
        if (!_trayAdded) return;
        _ = Shell_NotifyIconW(NIM_DELETE, ref _trayData);
        _trayAdded = false;
    }

    private void ShowTrayMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint, PlacementTarget = this };

        menu.Items.Add(new MenuItem
        {
            Header = $"模式：{_manager.ModeDetail}",
            IsEnabled = false,
        });

        var autostart = new MenuItem
        {
            Header = "开机启动",
            IsCheckable = true,
            IsChecked = Settings.IsAutostartEnabled(),
        };
        autostart.Click += (_, _) =>
        {
            Settings.SetAutostart(autostart.IsChecked);
            Log.Write($"开机启动 = {autostart.IsChecked}");
        };
        menu.Items.Add(autostart);

        menu.Items.Add(new Separator());

        var exit = new MenuItem { Header = "退出" };
        exit.Click += (_, _) => ExitApplication();
        menu.Items.Add(exit);

        menu.PlacementTarget = this;
        _trayMenu = menu; // 持有引用，防止弹出期间被回收
        menu.IsOpen = true;
    }

    private void NotifyDegrade()
    {
        _degradeNotified = true;
        Log.Write($"降级生效: {_manager.ModeDetail}");
        if (!_trayAdded) return;

        var data = _trayData;
        data.uFlags |= NIF_INFO;
        data.szInfoTitle = "虚拟桌面切换器";
        data.szInfo = "原生桌面控制不可用，已改用键盘模拟模式 (Win+Ctrl+数字)。";
        data.dwInfoFlags = NIIF_INFO;
        _ = Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    private void ExitApplication()
    {
        RemoveTrayIcon();
        if (_trayIconHandle != IntPtr.Zero) DestroyIcon(_trayIconHandle);
        _manager.Dispose();
        Application.Current.Shutdown();
    }

    // ===== WndProc =====

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        uint m = (uint)msg;

        if (m == WM_MOUSEACTIVATE)
        {
            // 鼠标点击绝不激活本窗口
            handled = true;
            return (IntPtr)MA_NOACTIVATE;
        }

        if (_taskbarCreatedMsg != 0 && m == _taskbarCreatedMsg)
        {
            // Explorer 重启广播：重建 COM、重新定位、重加托盘图标、重新固定
            Log.Write("收到 TaskbarCreated（Explorer 重启）");
            _trayAdded = false; // 旧托盘图标已随任务栏销毁
            _taskbarWatcher?.OnShellRecreated(); // 内部触发 ShellRecreated → 重新固定 + 重加托盘
            ReAddTrayIcon();
            handled = true;
            return IntPtr.Zero;
        }

        if (m == WM_SETTINGCHANGE)
        {
            RefreshTheme();
        }

        if (m is WM_DISPLAYCHANGE or WM_DPICHANGED)
        {
            _taskbarWatcher?.OnDisplayOrSettingsChanged();
        }

        if (_trayCallbackMsg != 0 && m == _trayCallbackMsg)
        {
            long mouseMsg = lParam.ToInt64();
            if (mouseMsg is 0x0202 or 0x0205) // WM_LBUTTONUP / WM_RBUTTONUP
                ShowTrayMenu();
            handled = true;
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    // ===== 托盘图标绘制（纯字节数组，不引第三方）=====

    private static IntPtr CreateTrayIconHandle(int size, Color accent)
    {
        var bits = DrawIconPixels(size, accent);
        IntPtr dc = GetDC(IntPtr.Zero);
        try
        {
            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = size,
                    biHeight = -size, // 自上而下
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0, // BI_RGB
                },
            };
            IntPtr colorBmp = CreateDIBSection(dc, ref bmi, 0, out IntPtr pBits, IntPtr.Zero, 0);
            if (colorBmp == IntPtr.Zero) return IntPtr.Zero;
            Marshal.Copy(bits, 0, pBits, bits.Length);

            var info = new ICONINFO
            {
                fIcon = true,
                xHotspot = 0,
                yHotspot = 0,
                hbmMask = CreateBitmap(size, size, 1, 1, IntPtr.Zero),
                hbmColor = colorBmp,
            };
            IntPtr icon = CreateIconIndirect(ref info);
            _ = DeleteObject(info.hbmMask);
            _ = DeleteObject(colorBmp);
            return icon;
        }
        finally
        {
            _ = ReleaseDC(IntPtr.Zero, dc);
        }
    }

    private static byte[] DrawIconPixels(int size, Color accent)
    {
        var bits = new byte[size * size * 4];
        int corner = size * 5 / 16;          // 圆角半径
        int dotRadius = Math.Max(1, size / 9);
        double cell = size / 4.0;            // 2x2 四个点的中心

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                // 圆角正方形内外判定
                int dx = Math.Min(x, size - 1 - x);
                int dy = Math.Min(y, size - 1 - y);
                if (dx < corner && dy < corner)
                {
                    int rx = corner - dx, ry = corner - dy;
                    if (rx * rx + ry * ry > corner * corner) continue; // 圆角外 → 透明
                }

                // 四个白点（代表 4 个虚拟桌面）
                double cx = (Math.Floor(x / (size / 2.0)) + 0.5) * 2 * cell;
                double cy = (Math.Floor(y / (size / 2.0)) + 0.5) * 2 * cell;
                double distSq = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                bool dot = distSq <= dotRadius * dotRadius;

                int offset = (y * size + x) * 4;
                bits[offset + 0] = dot ? (byte)0xFF : accent.B; // B
                bits[offset + 1] = dot ? (byte)0xFF : accent.G; // G
                bits[offset + 2] = dot ? (byte)0xFF : accent.R; // R
                bits[offset + 3] = 0xFF;                        // A
            }
        }
        return bits;
    }
}
