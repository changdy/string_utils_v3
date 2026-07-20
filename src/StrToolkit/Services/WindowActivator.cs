using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace StrToolkit.Services;

/// <summary>强制窗口获得前台焦点（绕过 Windows 前台锁定，保证失焦隐藏可靠触发）。</summary>
public static class WindowActivator
{
    public static void ForceForeground(Window window)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }
        try
        {
            var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }
            var foreground = GetForegroundWindow();
            uint foregroundThread = GetWindowThreadProcessId(foreground, out _);
            uint currentThread = GetCurrentThreadId();
            if (foreground != IntPtr.Zero && foregroundThread != currentThread)
            {
                AttachThreadInput(currentThread, foregroundThread, true);
                SetForegroundWindow(hwnd);
                AttachThreadInput(currentThread, foregroundThread, false);
            }
            else
            {
                SetForegroundWindow(hwnd);
            }
        }
        catch
        {
            // 激活失败不影响窗口显示
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
}
