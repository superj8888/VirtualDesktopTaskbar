using System.Runtime.InteropServices;
using VirtualDesktopTaskbar;
using VirtualDesktopTaskbar.VirtualDesktop;

namespace Probe;

/// <summary>
/// COM ABI 探针：验证 VirtualDesktopManager 在本机（预期 24H2 26100+）能否完成
/// 只读自检。只做读取调用；--switch 才执行真实切换（切过去再切回来）；
/// --pin 额外测试窗口固定。
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine($"OS Build : {VirtualDesktopInterop.OsBuild}");

        if (args.Contains("--where"))
        {
            PrintTaskbarGeometry();
            return 0;
        }

        Console.WriteLine($"MinSupport: {VirtualDesktopInterop.MinSupportedBuild} (24H2)");
        Console.WriteLine();

        using var manager = new VirtualDesktopManager();
        bool ok = manager.TryInitialize();
        Console.WriteLine($"Mode     : {manager.Mode}");
        Console.WriteLine($"Detail   : {manager.ModeDetail}");
        Console.WriteLine();

        if (!ok)
        {
            Console.WriteLine("结果: 失败（将进入键盘模拟模式）");
            Console.WriteLine();
            RunDiagnostic();
            return 1;
        }

        Console.WriteLine($"Desktops : {manager.DesktopCount}");
        Console.WriteLine($"Current  : {manager.CurrentIndex} (0 基)");
        Console.WriteLine("结果: 只读自检通过，Native 模式可用");

        if (args.Contains("--fullscreen"))
        {
            RunFullscreenTest();
            return 0;
        }

        if (args.Contains("--pin"))
        {
            Console.WriteLine();
            Console.WriteLine("== PinWindow 测试 ==");
            IntPtr hwnd = CreateScratchWindow();
            if (hwnd == IntPtr.Zero)
            {
                Console.WriteLine("创建测试窗口失败");
                return 1;
            }
            bool pinned = manager.TryPinWindow(hwnd);
            Console.WriteLine($"TryPinWindow: {(pinned ? "成功" : "失败")}");
            bool onCurrent = manager.IsWindowOnCurrentDesktop(hwnd);
            Console.WriteLine($"IsWindowOnCurrentDesktop: {onCurrent}");
            _ = DestroyWindow(hwnd);
        }

        if (args.Contains("--switch"))
        {
            Console.WriteLine();
            Console.WriteLine("== 切换测试（会真实切换桌面并恢复）==");
            int original = manager.CurrentIndex;
            Console.WriteLine($"原始桌面: {original + 1}");

            manager.SwitchTo(0);
            Console.WriteLine($"→ 切到桌面 1: CurrentIndex={manager.CurrentIndex} {(manager.CurrentIndex == 0 ? "[OK]" : "[FAIL]")}");

            manager.SwitchTo(original);
            Console.WriteLine($"→ 恢复桌面 {original + 1}: CurrentIndex={manager.CurrentIndex} {(manager.CurrentIndex == original ? "[OK]" : "[FAIL]")}");
        }

        return 0;
    }

    private static void PrintTaskbarGeometry()
    {
        var shell = VirtualDesktopTaskbar.NativeMethods.FindWindowW("Shell_TrayWnd", null);
        var tray = VirtualDesktopTaskbar.Taskbar.NotificationAreaFinder.FindTrayNotify(shell);
        Console.WriteLine($"Shell_TrayWnd  : 0x{shell:X}");
        Console.WriteLine($"TrayNotifyWnd  : 0x{tray:X}");
        if (VirtualDesktopTaskbar.NativeMethods.GetWindowRect(shell, out var bar))
            Console.WriteLine($"  taskbar rect : L={bar.Left} T={bar.Top} R={bar.Right} B={bar.Bottom} ({bar.Width}x{bar.Height})");
        if (VirtualDesktopTaskbar.NativeMethods.GetWindowRect(tray, out var trayRect))
            Console.WriteLine($"  tray rect    : L={trayRect.Left} T={trayRect.Top} R={trayRect.Right} B={trayRect.Bottom}");

        for (int n = 2; n <= 4; n += 2)
        {
            if (VirtualDesktopTaskbar.Taskbar.TaskbarManager.TryCompute(n, 1.0, out var r))
                Console.WriteLine($"  widget({n} btns, scale=1.0): X={r.X} Y={r.Y} {r.Width}x{r.Height}");
            else
                Console.WriteLine($"  widget({n} btns): TryCompute 失败");
        }

        IntPtr widget = VirtualDesktopTaskbar.NativeMethods.FindWindowW(null, "虚拟桌面切换器");
        Console.WriteLine($"widget window   : 0x{widget:X}");
        if (widget != IntPtr.Zero)
        {
            bool visible = VirtualDesktopTaskbar.NativeMethods.IsWindowVisible(widget);
            _ = VirtualDesktopTaskbar.NativeMethods.GetWindowRect(widget, out var wr);
            Console.WriteLine($"  visible={visible}, rect L={wr.Left} T={wr.Top} R={wr.Right} B={wr.Bottom} ({wr.Width}x{wr.Height})");

            // 是否在当前桌面（固定是否失效）
            int hrShell = VirtualDesktopInterop.CreateShellServiceProvider(out var shellPtr);
            if (hrShell >= 0)
            {
                var iidDoc = VirtualDesktopInterop.IidVirtualDesktopManager;
                int hr = VirtualDesktopInterop.Vt<VirtualDesktopInterop.FnQueryService>(
                    shellPtr, VirtualDesktopInterop.SlotQueryService)(shellPtr, ref iidDoc, ref iidDoc, out var doc);
                if (hr >= 0 && doc != IntPtr.Zero)
                {
                    hr = VirtualDesktopInterop.Vt<VirtualDesktopInterop.FnInPtrOutI32>(
                        doc, VirtualDesktopInterop.SlotDocIsWindowOnCurrent)(doc, widget, out int onCurrent);
                    Console.WriteLine($"  onCurrentDesktop={onCurrent != 0} (hr=0x{hr:X8})");
                    Marshal.Release(doc);
                }
                Marshal.Release(shellPtr);
            }

            // DWM cloak 状态
            int cloakHr = VirtualDesktopTaskbar.NativeMethods.DwmGetWindowAttribute(
                widget, 14, out uint cloaked, sizeof(uint));
            Console.WriteLine($"  dwmCloaked={cloaked} (hr=0x{cloakHr:X8})");
        }
    }

    /// <summary>弹出覆盖整个主屏的无边框窗口约 3 秒：断言小组件“全屏时隐藏、退出后恢复”。</summary>
    private static void RunFullscreenTest()
    {
        var widget = VirtualDesktopTaskbar.NativeMethods.FindWindowW(null, "虚拟桌面切换器");
        if (widget == IntPtr.Zero) { Console.WriteLine("找不到小组件窗口"); return; }

        var wndClass = new WNDCLASSW
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(DefProcDelegate),
            lpszClassName = "VdProbeFullscreen",
            hInstance = Marshal.GetHINSTANCE(typeof(Program).Module),
            hbrBackground = CreateSolidBrush(0x003C1414), // 深色底
        };
        _ = RegisterClassW(ref wndClass);

        IntPtr hwnd = CreateWindowExW(0, "VdProbeFullscreen",
            "FullscreenTest", 0x90000000 /*WS_POPUP | WS_VISIBLE*/, 0, 0, 2560, 1080,
            IntPtr.Zero, IntPtr.Zero, wndClass.hInstance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) { Console.WriteLine("创建全屏窗口失败"); return; }
        _ = ShowWindow(hwnd, 5 /*SW_SHOW*/);

        // ALT 技巧解锁前台权限，再把全屏窗口设为前台（模拟真实全屏应用）
        _ = keybd_event(0x12, 0, 0, UIntPtr.Zero);
        _ = keybd_event(0x12, 0, 2, UIntPtr.Zero);
        _ = SetForegroundWindow(hwnd);

        Console.WriteLine("全屏窗口已显示 3 秒，请观察任务栏小组件是否隐藏…");
        Thread.Sleep(1500);
        bool hiddenDuring = !VirtualDesktopTaskbar.NativeMethods.IsWindowVisible(widget);
        Thread.Sleep(1500);

        _ = DestroyWindow(hwnd);
        Console.WriteLine("全屏窗口已关闭，观察小组件是否恢复…");
        Thread.Sleep(2000);
        bool shownAfter = VirtualDesktopTaskbar.NativeMethods.IsWindowVisible(widget);

        Console.WriteLine($"全屏中隐藏: {hiddenDuring} {(hiddenDuring ? "[OK]" : "[FAIL]")}");
        Console.WriteLine($"退出后恢复: {shownAfter} {(shownAfter ? "[OK]" : "[FAIL]")}");
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    /// <summary>失败时逐步打印每个 COM 调用的 HRESULT / 返回值（只读）。</summary>
    private static void RunDiagnostic()
    {
        Console.WriteLine("== 逐步诊断（只读） ==");
        try
        {
            int hrShell = VirtualDesktopInterop.CreateShellServiceProvider(out var shell);
            Console.WriteLine($"CoCreateInstance(ImmersiveShell): hr=0x{hrShell:X8}");
            if (hrShell < 0 || shell == IntPtr.Zero) return;

            var service = VirtualDesktopInterop.ClsidManagerService;
            var iid = VirtualDesktopInterop.IidManagerInternal;
            int hr = VirtualDesktopInterop.Vt<VirtualDesktopInterop.FnQueryService>(
                shell, VirtualDesktopInterop.SlotQueryService)(shell, ref service, ref iid, out var managerPtr);
            Console.WriteLine($"QueryService: hr=0x{hr:X8}, ptr=0x{managerPtr:X}");
            if (hr < 0 || managerPtr == IntPtr.Zero) return;

            hr = VirtualDesktopInterop.Vt<VirtualDesktopInterop.FnOutU32>(
                managerPtr, VirtualDesktopInterop.SlotGetCount)(managerPtr, out uint count);
            Console.WriteLine($"GetDesktopCount: hr=0x{hr:X8}, count={count}");

            hr = VirtualDesktopInterop.Vt<VirtualDesktopInterop.FnOutPtr>(
                managerPtr, VirtualDesktopInterop.SlotGetDesktops)(managerPtr, out var array);
            Console.WriteLine($"GetDesktops: hr=0x{hr:X8}, array=0x{array:X}");
            if (hr < 0 || array == IntPtr.Zero) return;

            hr = VirtualDesktopInterop.Vt<VirtualDesktopInterop.FnOutU32>(
                array, VirtualDesktopInterop.SlotArrayCount)(array, out uint arrayCount);
            Console.WriteLine($"IObjectArray.GetCount: hr=0x{hr:X8}, count={arrayCount}");

            var iidDesktop = VirtualDesktopInterop.IidVirtualDesktop;
            for (uint i = 0; i < Math.Min(arrayCount, 8); i++)
            {
                hr = VirtualDesktopInterop.Vt<VirtualDesktopInterop.FnGetAt>(
                    array, VirtualDesktopInterop.SlotArrayGetAt)(array, i, ref iidDesktop, out var item);
                string guidText = "";
                if (item != IntPtr.Zero)
                {
                    _ = VirtualDesktopInterop.Vt<VirtualDesktopInterop.FnOutGuid>(
                        item, VirtualDesktopInterop.SlotDesktopGetId)(item, out var id);
                    guidText = $" id={id}";
                    Marshal.Release(item);
                }
                Console.WriteLine($"GetAt({i}): hr=0x{hr:X8}, item=0x{item:X}{guidText}");
            }

            hr = VirtualDesktopInterop.Vt<VirtualDesktopInterop.FnOutPtr>(
                managerPtr, VirtualDesktopInterop.SlotGetCurrentDesktop)(managerPtr, out var current);
            Console.WriteLine($"GetCurrentDesktop: hr=0x{hr:X8}, ptr=0x{current:X}");
            if (current != IntPtr.Zero) Marshal.Release(current);

            Marshal.Release(array);
            Marshal.Release(managerPtr);
            Marshal.Release(shell);
        }
        catch (Exception ex)
        {
            Console.WriteLine("诊断异常: " + ex);
        }
    }

    private static IntPtr CreateScratchWindow()
    {
        const string className = "VdProbeScratchWindow";
        var wndClass = new WNDCLASSW
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(DefProcDelegate),
            lpszClassName = className,
            hInstance = Marshal.GetHINSTANCE(typeof(Program).Module),
        };
        if (RegisterClassW(ref wndClass) == 0) return IntPtr.Zero;

        // 可见但放到屏幕外（WS_VISIBLE），保证 Shell 为它建立 ApplicationView
        return CreateWindowExW(0, className, "VdProbe",
            0x10CF0000 /*WS_VISIBLE | WS_OVERLAPPEDWINDOW*/,
            -32000, -32000, 160, 120, IntPtr.Zero, IntPtr.Zero, wndClass.hInstance, IntPtr.Zero);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static readonly WndProcDelegate DefProcDelegate = DefWindowProcW;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSW
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASSW lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
