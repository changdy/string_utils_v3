using System;
using System.Runtime.InteropServices;
using Avalonia;

namespace StrToolkit.Services;

/// <summary>
/// 获取全局鼠标光标位置（Avalonia 无跨平台 API，按平台调用原生桌面 API）。
/// Linux 仅支持 X11；Wayland 的安全模型不提供静默读取全局指针坐标的通用 API。
/// </summary>
public static class CursorService
{
    public static PixelPoint? GetCursorPosition()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return GetWindowsCursorPosition();
            }
            if (OperatingSystem.IsMacOS())
            {
                return GetMacOsCursorPosition();
            }
            if (OperatingSystem.IsLinux())
            {
                return GetLinuxCursorPosition();
            }
        }
        catch (Exception e)
        {
            AppLog.Error("获取全局鼠标位置失败", e);
        }

        // 不支持的平台、Wayland 或原生调用失败时返回 null，调用方退化为屏幕居中。
        return null;
    }

    private static PixelPoint? GetWindowsCursorPosition()
    {
        return GetCursorPos(out var point) ? new PixelPoint(point.X, point.Y) : null;
    }

    private static PixelPoint? GetMacOsCursorPosition()
    {
        IntPtr currentEvent = CGEventCreate(IntPtr.Zero);
        if (currentEvent == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var location = CGEventGetLocation(currentEvent);
            return new PixelPoint(
                checked((int)Math.Round(location.X, MidpointRounding.AwayFromZero)),
                checked((int)Math.Round(location.Y, MidpointRounding.AwayFromZero)));
        }
        finally
        {
            CFRelease(currentEvent);
        }
    }

    private static PixelPoint? GetLinuxCursorPosition()
    {
        // XWayland 无法可靠提供整个 Wayland 桌面的全局坐标，避免返回错误位置。
        if (string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland",
                StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            return null;
        }

        IntPtr display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            nuint rootWindow = XDefaultRootWindow(display);
            if (rootWindow == 0)
            {
                return null;
            }

            int success = XQueryPointer(
                display,
                rootWindow,
                out _,
                out _,
                out int rootX,
                out int rootY,
                out _,
                out _,
                out _);
            return success != 0 ? new PixelPoint(rootX, rootY) : null;
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct CGPoint
    {
        public double X;
        public double Y;
    }

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr CGEventCreate(IntPtr source);

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern CGPoint CGEventGetLocation(IntPtr @event);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern void CFRelease(IntPtr cf);

    [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr XOpenDisplay(IntPtr displayName);

    [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint XDefaultRootWindow(IntPtr display);

    [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
    private static extern int XQueryPointer(
        IntPtr display,
        nuint window,
        out nuint rootReturn,
        out nuint childReturn,
        out int rootXReturn,
        out int rootYReturn,
        out int winXReturn,
        out int winYReturn,
        out uint maskReturn);
}
