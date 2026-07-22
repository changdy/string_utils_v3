using System;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Svg.Skia;
using StrToolkit.Services;

namespace StrToolkit;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--jsonhero-server", StringComparer.OrdinalIgnoreCase))
        {
            RunJsonHeroServer();
            return;
        }

        if (!SingleInstance.TryAcquire())
        {
            // 已有实例在运行，通知其显示窗口后退出
            SingleInstance.NotifyFirstInstance();
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            SingleInstance.Release();
        }
    }

    private static void RunJsonHeroServer()
    {
        var service = new JsonHeroService();
        service.StartAsync().GetAwaiter().GetResult();
        if (!service.IsRunning)
        {
            Environment.ExitCode = 1;
            return;
        }

        Console.WriteLine("[jsonhero] API-only development mode. Press Ctrl+C to stop.");
        using var stopped = new ManualResetEventSlim();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stopped.Set();
        };
        Console.CancelKeyPress += handler;
        try
        {
            stopped.Wait();
        }
        finally
        {
            Console.CancelKeyPress -= handler;
            service.StopAsync().GetAwaiter().GetResult();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        GC.KeepAlive(typeof(SvgImageExtension).Assembly);
        GC.KeepAlive(typeof(Avalonia.Svg.Skia.Svg).Assembly);
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

        if (OperatingSystem.IsWindows())
        {
            builder.With(new Win32PlatformOptions
            {
                RenderingMode = new[] { Win32RenderingMode.Software }
            });
        }

        return builder;
    }
}
