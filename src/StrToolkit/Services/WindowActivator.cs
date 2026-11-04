using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace StrToolkit.Services;

/// <summary>尝试让窗口获得前台焦点，并记录原生调用结果。</summary>
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
                AppLog.Warn("Window.Foreground: 窗口尚无原生句柄。");
                return;
            }
            var foreground = GetForegroundWindow();
            uint foregroundThread = GetWindowThreadProcessId(foreground, out _);
            uint currentThread = GetCurrentThreadId();
            AppLog.Info($"Window.ForegroundBegin: target=0x{hwnd.ToInt64():X}, " +
                        $"foreground=0x{foreground.ToInt64():X}, foregroundThread={foregroundThread}, currentThread={currentThread}");
            bool attached = false;
            bool result;
            try
            {
                if (foreground != IntPtr.Zero && foregroundThread != currentThread)
                {
                    attached = AttachThreadInput(currentThread, foregroundThread, true);
                    if (!attached)
                    {
                        AppLog.Warn($"Window.AttachThreadInputFailed: error={Marshal.GetLastWin32Error()}");
                    }
                }
                result = SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached && !AttachThreadInput(currentThread, foregroundThread, false))
                {
                    AppLog.Warn($"Window.DetachThreadInputFailed: error={Marshal.GetLastWin32Error()}");
                }
            }
            string state = $"Window.ForegroundResult: result={result}, {GetDiagnosticState(window)}";
            if (result && GetForegroundWindow() == hwnd) AppLog.Info(state);
            else AppLog.Warn(state);
        }
        catch (Exception exception)
        {
            AppLog.Error("Window.ForegroundFailed", exception);
        }
    }

    internal static string GetDiagnosticState(Window window)
    {
        if (!OperatingSystem.IsWindows()) return "foreground=unsupported";
        try
        {
            IntPtr foreground = GetForegroundWindow();
            uint thread = GetWindowThreadProcessId(foreground, out uint process);
            IntPtr target = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            return $"foreground=0x{foreground.ToInt64():X}, foregroundPid={process}, foregroundThread={thread}, " +
                   $"foregroundIsTarget={target != IntPtr.Zero && foreground == target}";
        }
        catch (Exception exception)
        {
            return $"foregroundError={exception.GetType().Name}";
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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
}
