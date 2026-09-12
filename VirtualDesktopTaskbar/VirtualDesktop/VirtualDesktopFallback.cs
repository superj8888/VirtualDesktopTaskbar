using System.Runtime.InteropServices;
using static VirtualDesktopTaskbar.NativeMethods;

namespace VirtualDesktopTaskbar.VirtualDesktop;

/// <summary>
/// COM 不可用时的备用切换方案：相对移动桌面（Win+Ctrl+←/→）。
///
/// 注意：微软官方没有“Win+Ctrl+数字 切换虚拟桌面”快捷键——那是“切换到任务栏第 N 个
/// 固定应用的最近活动窗口”，误用会激活无关程序。降级模式下无法感知真实桌面索引，
/// 只能基于乐观记录的当前索引做相对移动，结果以乐观值为准。
/// </summary>
internal static class VirtualDesktopFallback
{
    /// <summary>从 from 索引向目标索引相对切换（负值向左）。步数封顶 8，防误触长串按键。</summary>
    public static void SendSwitchDelta(int delta)
    {
        if (delta == 0) return;
        ushort key = delta > 0 ? NativeMethods.VK_RIGHT : NativeMethods.VK_LEFT;
        int steps = Math.Min(Math.Abs(delta), 8);
        for (int i = 0; i < steps; i++)
            SendChord(key);
    }

    /// <summary>发送一次 Win+Ctrl+方向键。事件间留间隔，避免被合成序列外的状态干扰。</summary>
    private static void SendChord(ushort arrowKey)
    {
        var inputs = new[]
        {
            Key(NativeMethods.VK_LWIN, down: true),
            Key(NativeMethods.VK_LCONTROL, down: true),
            Key(arrowKey, down: true),
            Key(arrowKey, down: false),
            Key(NativeMethods.VK_LCONTROL, down: false),
            Key(NativeMethods.VK_LWIN, down: false),
        };
        Span<int> delays = [0, 25, 25, 25, 25, 25];
        for (int i = 0; i < inputs.Length; i++)
        {
            if (delays[i] > 0) Thread.Sleep(delays[i]);
            _ = SendInput(1, [inputs[i]], Marshal.SizeOf<INPUT>());
        }
    }

    private static INPUT Key(ushort vk, bool down) => new()
    {
        type = INPUT_KEYBOARD,
        U = new INPUTUNION
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                dwFlags = down ? 0 : KEYEVENTF_KEYUP,
            },
        },
    };
}
