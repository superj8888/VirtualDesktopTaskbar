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
/// - 已知索引（曾连上 COM）：从该索引相对移动 |delta| 步（|delta| &gt; 16 拒绝）；
/// - 未知索引（从未连上 COM）：先按 9 次左键回到桌面 1（桌面数 ≤10 时确定性到达），
///   再右移到目标。首次切换后索引转为已知。
///
/// 失败语义：任一 SendInput 失败立即中止、补发所有已按下键的 KeyUp 并返回 false——
/// 此时实际桌面位置已不可知，调用方必须把索引置为未知，不得保留旧基准。
/// 切换结果基于乐观索引：用户用键盘切过桌面后程序无法感知，可能偏移。
/// </summary>
internal static class VirtualDesktopFallback
{
    /// <summary>切到目标索引。knownIndex 为乐观记录的当前索引，-1 表示未知。</summary>
    public static bool SendSwitchTo(int targetIndex, int knownIndex)
    {
        if (knownIndex < 0)
        {
            // 索引未知：先回桌面 1（9 次左键覆盖 ≤10 桌面的情况），再右移到目标
            if (!SendChord(NativeMethods.VK_LEFT, 9)) return false;
            if (targetIndex > 0 && !SendChord(NativeMethods.VK_RIGHT, targetIndex)) return false;
            return true;
        }

        int delta = targetIndex - knownIndex;
        if (delta == 0) return true;
        return SendChord(delta > 0 ? NativeMethods.VK_RIGHT : NativeMethods.VK_LEFT,
            Math.Abs(delta));
    }

    /// <summary>
    /// 连发 count 次 Win+Ctrl+方向键，事件间留 25ms 间隔。
    /// 调用方需保证 count 在合理范围（相对切换上限 16；回桌面 1 固定 9）。
    /// 任一次 SendInput 失败：中止剩余发送，并补发所有处于按下状态的键的 KeyUp，
    /// 避免 Win/Ctrl/方向键滞留导致用户键盘行为异常。
    /// </summary>
    private static bool SendChord(ushort arrowKey, int count)
    {
        bool winDown = false, ctrlDown = false, arrowDown = false;
        try
        {
            for (int n = 0; n < count; n++)
            {
                if (!SendKey(NativeMethods.VK_LWIN, true)) return false;
                winDown = true;
                Thread.Sleep(25);
                if (!SendKey(NativeMethods.VK_LCONTROL, true)) return false;
                ctrlDown = true;
                Thread.Sleep(25);
                if (!SendKey(arrowKey, true)) return false;
                arrowDown = true;
                Thread.Sleep(25);
                if (!SendKey(arrowKey, false)) return false;
                arrowDown = false;
                Thread.Sleep(25);
                if (!SendKey(NativeMethods.VK_LCONTROL, false)) return false;
                ctrlDown = false;
                Thread.Sleep(25);
                if (!SendKey(NativeMethods.VK_LWIN, false)) return false;
                winDown = false;
                if (n < count - 1) Thread.Sleep(25); // 相邻和弦之间留间隔
            }
            return true;
        }
        finally
        {
            // 补发 KeyUp：即使某个 KeyUp 本身发送失败也再试一次（重复 KeyUp 无害）
            if (arrowDown) _ = SendKey(arrowKey, false);
            if (ctrlDown) _ = SendKey(NativeMethods.VK_LCONTROL, false);
            if (winDown) _ = SendKey(NativeMethods.VK_LWIN, false);
        }
    }

    private static bool SendKey(ushort vk, bool down) =>
        SendInput(1, [Key(vk, down)], Marshal.SizeOf<INPUT>()) == 1;

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
