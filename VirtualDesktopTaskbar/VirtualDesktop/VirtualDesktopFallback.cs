using System.Runtime.InteropServices;
using static VirtualDesktopTaskbar.NativeMethods;

namespace VirtualDesktopTaskbar.VirtualDesktop;

/// <summary>
/// COM 不可用时的备用切换方案：相对移动桌面（Win+Ctrl+←/→）。
///
/// 注意：微软官方没有“Win+Ctrl+数字 切换虚拟桌面”快捷键——那是“切换到任务栏第 N 个
/// 固定应用的最近活动窗口”，误用会激活无关程序。
///
/// 两种策略：
/// - 已知索引（曾连上 COM）：从该索引相对移动 |delta| 步（封顶 16）；
/// - 未知索引（从未连上 COM）：先按 9 次左键回到桌面 1（桌面数 ≤10 时确定性到达），
///   再右移到目标。首次切换后索引转为已知。
/// 切换结果基于乐观索引：用户用键盘切过桌面后程序无法感知，可能偏移。
/// </summary>
internal static class VirtualDesktopFallback
{
    /// <summary>切到目标索引。knownIndex 为乐观记录的当前索引，-1 表示未知。</summary>
    public static void SendSwitchTo(int targetIndex, int knownIndex)
    {
        if (knownIndex < 0)
        {
            // 索引未知：先回桌面 1（9 次左键覆盖 ≤10 桌面的情况），再右移到目标
            SendChord(NativeMethods.VK_LEFT, 9);
            if (targetIndex > 0)
                SendChord(NativeMethods.VK_RIGHT, targetIndex);
            return;
        }

        int delta = targetIndex - knownIndex;
        if (delta == 0) return;
        SendChord(delta > 0 ? NativeMethods.VK_RIGHT : NativeMethods.VK_LEFT,
            Math.Min(Math.Abs(delta), 16));
    }

    /// <summary>连发 count 次 Win+Ctrl+方向键，事件间留 25ms 间隔。</summary>
    private static void SendChord(ushort arrowKey, int count)
    {
        for (int n = 0; n < count; n++)
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
