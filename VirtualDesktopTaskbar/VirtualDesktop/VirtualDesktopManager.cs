using System.Runtime.InteropServices;

namespace VirtualDesktopTaskbar.VirtualDesktop;

/// <summary>
/// 虚拟桌面管理器。
///
/// 启动时以只读调用自检（数量 → 枚举 → 当前桌面 GUID 匹配），全部通过才进入
/// Native 模式；任何一步失败或运行中断连，都自动降级为键盘模拟模式。
/// 所有 COM 对象持裸指针（无 RCW），引用计数手动管理，固定在创建它的
/// UI（STA）线程上使用。
/// </summary>
internal sealed class VirtualDesktopManager : IDisposable
{
    public enum VdMode { HotkeyFallback, Native }

    public VdMode Mode { get; private set; } = VdMode.HotkeyFallback;

    /// <summary>人类可读的模式说明（托盘 tooltip / 日志用）。</summary>
    public string ModeDetail { get; private set; } = "未初始化";

    public int DesktopCount { get; private set; } = 4;

    /// <summary>当前桌面索引（0 基）。降级模式下是乐观值（最近一次点击）。</summary>
    public int CurrentIndex { get; private set; } = -1;

    /// <summary>桌面数量 / 当前索引 / 模式变化时触发（UI 线程）。</summary>
    public event Action? StateChanged;

    private IntPtr _shell;      // IServiceProvider（裸）
    private IntPtr _manager;    // IVirtualDesktopManagerInternal（裸）
    private IntPtr _documented; // IVirtualDesktopManager（裸，文档化接口，兜底用）

    private int _fallbackTickCount;
    private const int FallbackRetryTicks = 8; // 250ms × 8 = 2s

    // ===== 初始化 =====

    /// <summary>初始化（或 Explorer 重启后重建）COM 连接。成功进入 Native 模式。</summary>
    public bool TryInitialize()
    {
        ReleaseCom();

        if (VirtualDesktopInterop.OsBuild < VirtualDesktopInterop.MinSupportedBuild)
        {
            Degrade($"系统 build {VirtualDesktopInterop.OsBuild} 低于最低支持版本 26100 (24H2)");
            return false;
        }

        try
        {
            if (TryInitCom()) return true;
        }
        catch (Exception ex)
        {
            Log.Write("COM 初始化异常: " + ex);
        }

        ReleaseCom(); // 自检失败也要释放已持有的引用
        Degrade(LastInitFailure ?? "COM 自检失败");
        return false;
    }

    private string? LastInitFailure { get; set; }

    private bool TryInitCom()
    {
        LastInitFailure = null;

        var hrShell = VirtualDesktopInterop.CreateShellServiceProvider(out _shell);
        if (hrShell < 0 || _shell == IntPtr.Zero)
            return Fail($"CoCreateInstance(ImmersiveShell) 失败 (hr=0x{hrShell:X8})");

        var service = VirtualDesktopInterop.ClsidManagerService;
        var iidManager = VirtualDesktopInterop.IidManagerInternal;
        int hr = VirtualDesktopInterop.Vt<VirtualDesktopInterop.FnQueryService>(
            _shell, VirtualDesktopInterop.SlotQueryService)(_shell, ref service, ref iidManager, out _manager);
        if (hr < 0 || _manager == IntPtr.Zero)
            return Fail($"QueryService(IVirtualDesktopManagerInternal) 失败 (hr=0x{hr:X8})");

        // 文档化管理器仅用于窗口归属判断，失败不影响主路径
        var iidDoc = VirtualDesktopInterop.IidVirtualDesktopManager;
        _ = VirtualDesktopInterop.Vt<VirtualDesktopInterop.FnQueryService>(
            _shell, VirtualDesktopInterop.SlotQueryService)(_shell, ref iidDoc, ref iidDoc, out _documented);

        // ===== 只读自检 =====
        int count = GetCount();
        if (count is < 1 or > 64)
            return Fail($"自检失败：桌面数量异常 ({count})，ABI 可能已随系统更新变化");

        var items = GetItemsRaw();
        if (items is null || items.Count != count)
        {
            ReleaseItems(items);
            return Fail("自检失败：桌面枚举与数量不一致");
        }

        int idx = FindCurrentIndex(items);
        ReleaseItems(items);
        if (idx < 0)
            return Fail("自检失败：无法定位当前桌面");

        DesktopCount = count;
        CurrentIndex = idx;
        Mode = VdMode.Native;
        ModeDetail = NativeDetail;
        return true;
    }

    private bool Fail(string reason)
    {
        LastInitFailure = reason;
        return false;
    }

    private string NativeDetail => $"Native (build {VirtualDesktopInterop.OsBuild})";

    // ===== 状态轮询（250ms，UI 线程）=====

    /// <summary>刷新桌面数量与当前索引。COM 断连时尝试原地重建，重建失败降级。</summary>
    public void Poll()
    {
        if (Mode != VdMode.Native)
        {
            // 降级状态下每 ~2 秒重试一次原生连接（Explorer 重启、系统更新后自动恢复）
            if (++_fallbackTickCount >= FallbackRetryTicks)
            {
                _fallbackTickCount = 0;
                if (TryInitialize()) StateChanged?.Invoke();
            }
            return;
        }

        _fallbackTickCount = 0;
        try
        {
            int count = GetCount();
            if (count is < 1 or > 64) throw new InvalidOperationException($"GetCount → {count}");

            var items = GetItemsRaw();
            if (items is null || items.Count != count)
            {
                ReleaseItems(items);
                throw new InvalidOperationException("GetDesktops 枚举失败");
            }

            int idx = FindCurrentIndex(items);
            ReleaseItems(items);
            if (idx < 0) throw new InvalidOperationException("GetCurrentDesktop 定位失败");

            bool changed = count != DesktopCount || idx != CurrentIndex || ModeDetail != NativeDetail;
            DesktopCount = count;
            CurrentIndex = idx;
            if (changed) StateChanged?.Invoke();
        }
        catch
        {
            // 多为 Explorer 重启导致 RPC 断连：原地重建一次，并把新状态通知 UI
            TryInitialize();
            StateChanged?.Invoke();
        }
    }

    // ===== 切换 =====

    /// <summary>切换到指定桌面。Native 模式走 COM 并校验结果；降级模式模拟 Win+Ctrl+数字。</summary>
    public bool SwitchTo(int index)
    {
        if (index < 0) return false;

        if (Mode != VdMode.Native)
        {
            VirtualDesktopFallback.SendSwitchTo(index);
            CurrentIndex = index; // 乐观更新，无确认手段
            StateChanged?.Invoke();
            return true;
        }

        try
        {
            var items = GetItemsRaw();
            if (items is null || index >= items.Count)
            {
                ReleaseItems(items);
                return false;
            }

            int hr = VirtualDesktopInterop.Vt<VirtualDesktopInterop.FnInPtr>(
                _manager, VirtualDesktopInterop.SlotSwitchDesktop)(_manager, items[index]);
            ReleaseItems(items);
            if (hr < 0)
            {
                Log.Write($"SwitchDesktop hr=0x{hr:X8}");
                return false;
            }

            Poll(); // 校验切换确实生效
            return Mode == VdMode.Native && CurrentIndex == index;
        }
        catch
        {
            TryInitialize();
            StateChanged?.Invoke();
            return false;
        }
    }

    // ===== 窗口跨桌面常驻 =====

    /// <summary>把窗口固定到所有桌面（PinView）。成功后窗口不随桌面切换消失。</summary>
    public bool TryPinWindow(IntPtr hwnd)
    {
        if (Mode != VdMode.Native || _shell == IntPtr.Zero) return false;

        IntPtr appViewCollection = IntPtr.Zero, view = IntPtr.Zero, pinnedApps = IntPtr.Zero;
        try
        {
            var iidAv = VirtualDesktopInterop.IidAppViewCollection;
            int hr = VirtualDesktopInterop.Vt<VirtualDesktopInterop.FnQueryService>(
                _shell, VirtualDesktopInterop.SlotQueryService)(_shell, ref iidAv, ref iidAv, out appViewCollection);
            if (hr < 0 || appViewCollection == IntPtr.Zero)
            {
                Log.Write($"Pin: QueryService(IApplicationViewCollection) hr=0x{hr:X8}");
                return false;
            }

            hr = VirtualDesktopInterop.Vt<VirtualDesktopInterop.FnInPtrOutPtr>(
                appViewCollection, VirtualDesktopInterop.SlotGetViewForHwnd)(appViewCollection, hwnd, out view);
            if (hr < 0 || view == IntPtr.Zero)
            {
                Log.Write($"Pin: GetViewForHwnd hr=0x{hr:X8}");
                return false;
            }

            var clsidPa = VirtualDesktopInterop.ClsidPinnedApps;
            var iidPa = VirtualDesktopInterop.IidPinnedApps;
            hr = VirtualDesktopInterop.Vt<VirtualDesktopInterop.FnQueryService>(
                _shell, VirtualDesktopInterop.SlotQueryService)(_shell, ref clsidPa, ref iidPa, out pinnedApps);
            if (hr < 0 || pinnedApps == IntPtr.Zero)
            {
                Log.Write($"Pin: QueryService(IVirtualDesktopPinnedApps) hr=0x{hr:X8}");
                return false;
            }

            hr = VirtualDesktopInterop.Vt<VirtualDesktopInterop.FnInPtrOutI32>(
                pinnedApps, VirtualDesktopInterop.SlotIsViewPinned)(pinnedApps, view, out int pinned);
            if (hr < 0)
            {
                Log.Write($"Pin: IsViewPinned hr=0x{hr:X8}");
                return false;
            }
            if (pinned != 0) return true;

            hr = VirtualDesktopInterop.Vt<VirtualDesktopInterop.FnInPtr>(
                pinnedApps, VirtualDesktopInterop.SlotPinView)(pinnedApps, view);
            if (hr < 0)
            {
                Log.Write($"Pin: PinView hr=0x{hr:X8}");
                return false;
            }

            _ = VirtualDesktopInterop.Vt<VirtualDesktopInterop.FnInPtrOutI32>(
                pinnedApps, VirtualDesktopInterop.SlotIsViewPinned)(pinnedApps, view, out pinned);
            return pinned != 0;
        }
        catch (Exception ex)
        {
            Log.Write("Pin: 异常 " + ex.Message);
            return false;
        }
        finally
        {
            ReleaseIf(ref appViewCollection);
            ReleaseIf(ref view);
            ReleaseIf(ref pinnedApps);
        }
    }

    /// <summary>窗口是否在当前桌面上（文档化 API）。</summary>
    public bool IsWindowOnCurrentDesktop(IntPtr hwnd)
    {
        if (Mode != VdMode.Native || _documented == IntPtr.Zero) return false;
        int hr = VirtualDesktopInterop.Vt<VirtualDesktopInterop.FnInPtrOutI32>(
            _documented, VirtualDesktopInterop.SlotDocIsWindowOnCurrent)(_documented, hwnd, out int onCurrent);
        return hr >= 0 && onCurrent != 0;
    }

    /// <summary>把窗口拉到当前桌面（文档化 API；固定失败时的兜底跟踪）。</summary>
    public bool MoveWindowToCurrentDesktop(IntPtr hwnd, IntPtr foregroundHwnd)
    {
        if (Mode != VdMode.Native || _documented == IntPtr.Zero) return false;

        int hr = VirtualDesktopInterop.Vt<VirtualDesktopInterop.FnInPtrOutGuid>(
            _documented, VirtualDesktopInterop.SlotDocGetWindowDesktopId)(_documented, foregroundHwnd, out var desktopId);
        if (hr < 0) return false;

        hr = VirtualDesktopInterop.Vt<VirtualDesktopInterop.FnInPtrInGuid>(
            _documented, VirtualDesktopInterop.SlotDocMoveWindowToDesktop)(_documented, hwnd, ref desktopId);
        return hr >= 0;
    }

    // ===== 内部：COM 原始调用 =====

    /// <summary>GetDesktopCount。返回负值表示失败（HRESULT）。</summary>
    private int GetCount()
    {
        int hr = VirtualDesktopInterop.Vt<VirtualDesktopInterop.FnOutU32>(
            _manager, VirtualDesktopInterop.SlotGetCount)(_manager, out uint count);
        return hr < 0 ? hr : (int)count;
    }

    /// <summary>GetDesktops + 逐项 GetAt(IID_IVirtualDesktop)。返回的指针列表由调用方 Release。</summary>
    private List<IntPtr>? GetItemsRaw()
    {
        int hr = VirtualDesktopInterop.Vt<VirtualDesktopInterop.FnOutPtr>(
            _manager, VirtualDesktopInterop.SlotGetDesktops)(_manager, out var array);
        if (hr < 0 || array == IntPtr.Zero) return null;

        try
        {
            hr = VirtualDesktopInterop.Vt<VirtualDesktopInterop.FnOutU32>(
                array, VirtualDesktopInterop.SlotArrayCount)(array, out uint count);
            if (hr < 0 || count is < 1 or > 64) return null;

            var items = new List<IntPtr>((int)count);
            var iidDesktop = VirtualDesktopInterop.IidVirtualDesktop;
            for (uint i = 0; i < count; i++)
            {
                hr = VirtualDesktopInterop.Vt<VirtualDesktopInterop.FnGetAt>(
                    array, VirtualDesktopInterop.SlotArrayGetAt)(array, i, ref iidDesktop, out var item);
                if (hr < 0 || item == IntPtr.Zero)
                {
                    ReleaseItems(items);
                    return null;
                }
                items.Add(item);
            }
            return items;
        }
        finally
        {
            Marshal.Release(array);
        }
    }

    /// <summary>
    /// GetCurrentDesktop 并与枚举项按 GUID（IVirtualDesktop::GetID）匹配。
    /// 不用 IUnknown 身份比较：twinapi 内部对象的 QI 表不含 IUnknown。
    /// </summary>
    private int FindCurrentIndex(List<IntPtr> items)
    {
        int hr = VirtualDesktopInterop.Vt<VirtualDesktopInterop.FnOutPtr>(
            _manager, VirtualDesktopInterop.SlotGetCurrentDesktop)(_manager, out var current);
        if (hr < 0 || current == IntPtr.Zero) return -1;

        try
        {
            hr = VirtualDesktopInterop.Vt<VirtualDesktopInterop.FnOutGuid>(
                current, VirtualDesktopInterop.SlotDesktopGetId)(current, out var currentId);
            if (hr < 0) return -1;

            for (int i = 0; i < items.Count; i++)
            {
                hr = VirtualDesktopInterop.Vt<VirtualDesktopInterop.FnOutGuid>(
                    items[i], VirtualDesktopInterop.SlotDesktopGetId)(items[i], out var itemId);
                if (hr < 0) continue;
                if (itemId == currentId) return i;
            }
            return -1;
        }
        finally { Marshal.Release(current); }
    }

    // ===== 生命周期 =====

    private void Degrade(string reason)
    {
        Mode = VdMode.HotkeyFallback;
        ModeDetail = reason;
        DesktopCount = 4;
        CurrentIndex = -1;
        Log.Write("降级为键盘模式: " + reason);
    }

    private static void ReleaseItems(List<IntPtr>? items)
    {
        if (items is null) return;
        foreach (var p in items)
            if (p != IntPtr.Zero) Marshal.Release(p);
    }

    private static void ReleaseIf(ref IntPtr pointer)
    {
        if (pointer != IntPtr.Zero)
        {
            Marshal.Release(pointer);
            pointer = IntPtr.Zero;
        }
    }

    private void ReleaseCom()
    {
        ReleaseIf(ref _manager);
        ReleaseIf(ref _documented);
        ReleaseIf(ref _shell);
    }

    public void Dispose() => ReleaseCom();
}
