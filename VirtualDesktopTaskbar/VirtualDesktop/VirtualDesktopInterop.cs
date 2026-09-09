using System.Runtime.InteropServices;

namespace VirtualDesktopTaskbar.VirtualDesktop;

/// <summary>
/// Windows 未公开的 Virtual Desktop COM ABI。
///
/// 兼容策略：仅支持 24H2（build 26100）及以上；未来 Windows 更新若改动 ABI，
/// 启动自检会失败并自动降级为键盘模拟模式，不会崩溃或产生错误调用。
///
/// 实现约束：所有 COM 对象（含文档化的 IServiceProvider/IVirtualDesktopManager）
/// 一律持裸指针 + vtable 委托调用，不创建任何 RCW，避免 RCW 缓存与
/// FinalReleaseComObject 的释放顺序问题；引用计数完全手动管理。
///
/// GUID 与槽位来源（交叉验证）：
///  - Ciantic/VirtualDesktopAccessor（MIT，实测 24H2 26100.2605 / 25H2 26200）
///  - CLSID 与 mntone/VirtualDesktop 一致
/// </summary>
internal static class VirtualDesktopInterop
{
    /// <summary>最低支持的 Windows 版本：24H2。</summary>
    public const int MinSupportedBuild = 26100;

    // ===== CLSID / IID =====

    public static readonly Guid ClsidImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");

    /// <summary>QueryService 的服务 GUID（CLSID_VirtualDesktopManagerInternal）。</summary>
    public static readonly Guid ClsidManagerService = new("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");

    /// <summary>IVirtualDesktopManagerInternal（26100 布局）。</summary>
    public static readonly Guid IidManagerInternal = new("53F5CA0B-158F-4124-900C-057158060B27");

    /// <summary>IVirtualDesktop。注意：twinapi 数组的 GetAt 只接受此 IID，
    /// 传 IID_IUnknown 会得到 E_NOINTERFACE（内部对象手写 QI 表不含 IUnknown）。</summary>
    public static readonly Guid IidVirtualDesktop = new("3F07F4BE-B107-441A-AF0F-39D82529072C");

    /// <summary>文档化 shell 接口，GUID 长期稳定。</summary>
    public static readonly Guid IidObjectArray = new("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9");

    public static readonly Guid IidIUnknown = Guid.Empty;

    /// <summary>IVirtualDesktopPinnedApps（窗口跨桌面固定）。</summary>
    public static readonly Guid IidPinnedApps = new("4CE81583-1E4C-4632-A621-07A53543148F");
    public static readonly Guid ClsidPinnedApps = new("B5A399E7-1C87-46B8-88E9-FC5747B171BD");

    /// <summary>IApplicationViewCollection（HWND → ApplicationView，固定窗口的前置步骤）。</summary>
    public static readonly Guid IidAppViewCollection = new("1841C6D7-4F9D-42C0-AF41-8747538F10E5");

    /// <summary>文档化 IVirtualDesktopManager（查询/移动窗口所属桌面；不能切换当前桌面）。</summary>
    public static readonly Guid IidVirtualDesktopManager = new("A5CD92FF-29BE-454C-8D04-D82879FB3F1B");

    /// <summary>文档化 IServiceProvider（ImmersiveShell 的激活接口）。</summary>
    public static readonly Guid IidServiceProvider = new("6D5140C1-7436-11CE-8034-00AA006009FA");

    public static int OsBuild { get; } = Environment.OSVersion.Version.Build;

    // ===== vtable 槽位（IUnknown 占 0..2：QI=0, AddRef=1, Release=2，自定义方法从 3 开始）=====

    // IServiceProvider（文档化布局）
    public const int SlotQueryService = 0;      // HRESULT QueryService(SID, IID, void** out)

    // IVirtualDesktopManagerInternal {53F5CA0B-158F-4124-900C-057158060B27}
    public const int SlotGetCount = 0;          // HRESULT GetDesktopCount(UINT* out)
    public const int SlotGetCurrentDesktop = 3; // HRESULT GetCurrentDesktop(IVirtualDesktop** out)
    public const int SlotGetDesktops = 4;       // HRESULT GetDesktops(IObjectArray** out)
    public const int SlotSwitchDesktop = 6;     // HRESULT SwitchDesktop(IVirtualDesktop* in)

    // IObjectArray {92CA9DCD-5622-4BBA-A805-5E9F541BD8C9}（文档化布局）
    public const int SlotArrayCount = 0;        // HRESULT GetCount(UINT* out)
    public const int SlotArrayGetAt = 1;        // HRESULT GetAt(UINT index, REFIID, void** out)

    // IVirtualDesktop {3F07F4BE-B107-441A-AF0F-39D82529072C}
    public const int SlotDesktopGetId = 1;      // HRESULT GetID(GUID* out)

    // IApplicationViewCollection {1841C6D7-4F9D-42C0-AF41-8747538F10E5}
    public const int SlotGetViewForHwnd = 3;    // HRESULT GetViewForHwnd(HWND, IApplicationView** out)

    // IVirtualDesktopPinnedApps {4CE81583-1E4C-4632-A621-07A53543148F}
    public const int SlotIsViewPinned = 3;      // HRESULT IsViewPinned(IApplicationView*, BOOL* out)
    public const int SlotPinView = 4;           // HRESULT PinView(IApplicationView* in)

    // IVirtualDesktopManager {A5CD92FF-29BE-454C-8D04-D82879FB3F1B}（文档化布局）
    public const int SlotDocIsWindowOnCurrent = 0;  // HRESULT IsWindowOnCurrentVirtualDesktop(HWND, BOOL* out)
    public const int SlotDocGetWindowDesktopId = 1; // HRESULT GetWindowDesktopId(HWND, GUID* out)
    public const int SlotDocMoveWindowToDesktop = 2;// HRESULT MoveWindowToDesktop(HWND, GUID* in)

    // ===== vtable 委托 =====

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int FnQueryInterface(IntPtr self, ref Guid riid, out IntPtr ppv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int FnQueryService(IntPtr self, ref Guid sid, ref Guid iid, out IntPtr ppv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int FnOutU32(IntPtr self, out uint value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int FnOutPtr(IntPtr self, out IntPtr pointer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int FnOutGuid(IntPtr self, out Guid guid);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int FnInPtr(IntPtr self, IntPtr pointer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int FnGetAt(IntPtr self, uint index, ref Guid riid, out IntPtr ppv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int FnInPtrOutPtr(IntPtr self, IntPtr hwnd, out IntPtr view);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int FnInPtrOutI32(IntPtr self, IntPtr view, out int pinned);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int FnInPtrInGuid(IntPtr self, IntPtr hwnd, ref Guid guid);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int FnInPtrOutGuid(IntPtr self, IntPtr hwnd, out Guid guid);

    /// <summary>取 vtable 槽位函数。IUnknown 占 0..2，自定义方法从槽位 3 开始。</summary>
    public static T Vt<T>(IntPtr comObject, int slot) where T : class
    {
        var vtbl = Marshal.ReadIntPtr(comObject);
        var fn = Marshal.ReadIntPtr(vtbl, (3 + slot) * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(fn);
    }

    // ===== 激活 =====

    private const uint CLSCTX_LOCAL_SERVER = 0x4;

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

    /// <summary>创建 ImmersiveShell 服务提供程序（裸 IServiceProvider 指针，调用方负责 Release）。
    /// 注意必须以 IID_IServiceProvider 激活：该 coclass 的 DCOM 激活拒绝裸 IID_IUnknown。</summary>
    public static int CreateShellServiceProvider(out IntPtr shell)
    {
        var clsid = ClsidImmersiveShell;
        var iid = IidServiceProvider;
        return CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_LOCAL_SERVER, ref iid, out shell);
    }
}
