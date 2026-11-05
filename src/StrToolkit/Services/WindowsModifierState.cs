using System;
using System.Runtime.InteropServices;
using SharpHook.Native;

namespace StrToolkit.Services;

internal static class WindowsModifierState
{
    /// <summary>
    /// 仅在主键回调中用于校验已经按下的修饰键。低级钩子回调先于当前按键的
    /// 异步状态更新，因此不能用它判断当前这一次修饰键事件的最终状态。
    /// https://learn.microsoft.com/windows/win32/winmsg/lowlevelkeyboardproc
    /// </summary>
    internal static ModifierMask? Read()
    {
        if (!OperatingSystem.IsWindows()) return null;

        ModifierMask mask = ModifierMask.None;
        if (IsDown(0xA0)) mask |= ModifierMask.LeftShift;
        if (IsDown(0xA1)) mask |= ModifierMask.RightShift;
        if (IsDown(0xA2)) mask |= ModifierMask.LeftCtrl;
        if (IsDown(0xA3)) mask |= ModifierMask.RightCtrl;
        if (IsDown(0xA4)) mask |= ModifierMask.LeftAlt;
        if (IsDown(0xA5)) mask |= ModifierMask.RightAlt;
        if (IsDown(0x5B)) mask |= ModifierMask.LeftMeta;
        if (IsDown(0x5C)) mask |= ModifierMask.RightMeta;
        return mask;
    }

    // 只使用最高位；最低位的“自上次查询以来按过”标记不能用作当前按下状态。
    private static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
