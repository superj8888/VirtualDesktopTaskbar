using System.Runtime.InteropServices;
using static VirtualDesktopTaskbar.NativeMethods;

namespace VirtualDesktopTaskbar.VirtualDesktop;

/// <summary>
/// COM 不可用时的备用切换方案：模拟 Win+Ctrl+1..9（Windows 自带的
/// “切换到第 N 个虚拟桌面”快捷键）。
/// </summary>
internal static class VirtualDesktopFallback
{
    /// <summary>切换到第 index+1 个桌面（0 基索引，最多 9）。</summary>
    public static void SendSwitchTo(int zeroBasedIndex)
    {
        int number = zeroBasedIndex + 1;
        if (number is < 1 or > 9) return;

        ushort vk = (ushort)('0' + number);
        // 事件间留间隔，避免系统把合成序列误判成其他 Win 组合键
        Span<int> delays = [0, 25, 25, 25, 25, 25];
        var inputs = new[]
        {
            Key(NativeMethods.VK_LWIN, down: true),
            Key(NativeMethods.VK_LCONTROL, down: true),
            Key(vk, down: true),
            Key(vk, down: false),
            Key(NativeMethods.VK_LCONTROL, down: false),
            Key(NativeMethods.VK_LWIN, down: false),
        };
        for (int i = 0; i < inputs.Length; i++)
        {
            if (delays[i] > 0) Thread.Sleep(delays[i]);
            _ = NativeMethods.SendInput(1, [inputs[i]], Marshal.SizeOf<INPUT>());
        }
    }

    private static INPUT Key(ushort vk, bool down) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        U = new INPUTUNION
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                dwFlags = down ? 0 : NativeMethods.KEYEVENTF_KEYUP,
            },
        },
    };
}