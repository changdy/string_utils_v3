using System;
using System.Diagnostics;
using System.IO;

namespace StrToolkit.Services;

/// <summary>开机自动启动（对应 Electron 的 app.setLoginItemSettings）。</summary>
public static class AutoLaunchService
{
    private const string AppName = "StrToolkit";

    public static void SetEnabled(bool enabled)
    {
        try
        {
            string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrEmpty(exePath))
            {
                return;
            }
            if (OperatingSystem.IsWindows())
            {
                SetWindows(enabled, exePath);
            }
            else if (OperatingSystem.IsMacOS())
            {
                SetMacOs(enabled, exePath);
            }
            else if (OperatingSystem.IsLinux())
            {
                SetLinux(enabled, exePath);
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"设置开机启动失败: {e.Message}");
        }
    }

    private static void SetWindows(bool enabled, string exePath)
    {
#pragma warning disable CA1416
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (key is null)
        {
            return;
        }
        if (enabled)
        {
            key.SetValue(AppName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
        }
#pragma warning restore CA1416
    }

    private static void SetMacOs(bool enabled, string exePath)
    {
        string plistDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents");
        string plistPath = Path.Combine(plistDir, "com.strtoolkit.autolaunch.plist");
        if (enabled)
        {
            Directory.CreateDirectory(plistDir);
            File.WriteAllText(plistPath, $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                    <key>Label</key>
                    <string>com.strtoolkit.autolaunch</string>
                    <key>ProgramArguments</key>
                    <array><string>{exePath}</string></array>
                    <key>RunAtLoad</key>
                    <true/>
                </dict>
                </plist>
                """);
        }
        else if (File.Exists(plistPath))
        {
            File.Delete(plistPath);
        }
    }

    private static void SetLinux(bool enabled, string exePath)
    {
        string autostartDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "autostart");
        string desktopPath = Path.Combine(autostartDir, "str-toolkit.desktop");
        if (enabled)
        {
            Directory.CreateDirectory(autostartDir);
            File.WriteAllText(desktopPath, $"""
                [Desktop Entry]
                Type=Application
                Name=StrToolkit
                Exec="{exePath}"
                X-GNOME-Autostart-enabled=true
                """);
        }
        else if (File.Exists(desktopPath))
        {
            File.Delete(desktopPath);
        }
    }
}
