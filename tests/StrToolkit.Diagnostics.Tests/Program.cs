using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using SharpHook;
using SharpHook.Native;
using StrToolkit.Services;
using StrToolkit.Views;

// 无真实窗口、不注册系统钩子、不读取或改写用户配置。
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        AppBuilder.Configure<Application>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .SetupWithoutStarting();
        Application.Current!.Styles.Add(new FluentTheme());
        try
        {
            Run("旧失焦通知不能隐藏新唤醒", window =>
            {
                Deactivate(window);
                window.MarkWake("test-new-wake");
                // 即使排队超过宽限期，旧通知也不能隐藏新一代窗口。
                Field(window, "_lastWakeAt").SetValue(window, Environment.TickCount64 - 1_000);
                Dispatcher.UIThread.RunJobs();
                Require(window.IsVisible, "旧失焦通知隐藏了新唤醒窗口");
            });
            Run("重新激活后忽略旧失焦通知", window =>
            {
                Deactivate(window);
                RaiseNativeEvent(window, "Activated");
                Dispatcher.UIThread.RunJobs();
                Require(window.IsVisible, "旧失焦通知隐藏了已激活窗口");
            });
            Run("真正失焦仍会隐藏", window =>
            {
                Deactivate(window);
                Dispatcher.UIThread.RunJobs();
                Require(!window.IsVisible, "真正失焦没有隐藏");
            });
            Run("唤醒宽限期忽略瞬时失焦", window =>
            {
                window.MarkWake("test-grace");
                Deactivate(window);
                Dispatcher.UIThread.RunJobs();
                Require(window.IsVisible, "宽限期内隐藏了窗口");
            });
            Run("旧鼠标事件不能隐藏新唤醒", window =>
            {
                var app = AppWithWindow(window);
                Invoke(app, "OnGlobalMousePressed", int.MinValue, int.MinValue);
                window.MarkWake("test-new-wake");
                Dispatcher.UIThread.RunJobs();
                Require(window.IsVisible, "旧鼠标事件隐藏了新唤醒窗口");
            });
            Run("实际外部点击仍会隐藏", window =>
            {
                Invoke(AppWithWindow(window), "OnGlobalMousePressed", int.MinValue, int.MinValue);
                Dispatcher.UIThread.RunJobs();
                Require(!window.IsVisible, "外部点击没有隐藏");
            });
            Run("窗口内部点击保持显示", window =>
            {
                Invoke(AppWithWindow(window), "OnGlobalMousePressed", window.Position.X + 10, window.Position.Y + 10);
                Dispatcher.UIThread.RunJobs();
                Require(window.IsVisible, "内部点击隐藏了窗口");
            });
            TestHotkeyState();
            Console.WriteLine("全部诊断回归检查通过。");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            typeof(HotkeyService).Assembly.GetType("StrToolkit.Services.AppLog")!
                .GetMethod("Shutdown", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
        }
    }

    private static void Run(string name, Action<MainWindow> test)
    {
        var window = new MainWindow();
        try
        {
            window.MarkWake("test-setup");
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Field(window, "_lastWakeAt").SetValue(window, Environment.TickCount64 - 1_000);
            test(window);
            Console.WriteLine($"PASS {name}");
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void TestHotkeyState()
    {
        using var service = new HotkeyService();
        Require(service.Register("Ctrl+Alt+D"), "快捷键解析失败");
        Field(service, "_isAvailable").SetValue(service, true);
        int triggers = 0;
        service.HotkeyPressed += _ => triggers++;
        Press();
        Press();
        Require(triggers == 1, "重复按键触发了多次唤醒");
        Invoke(service, "OnKeyReleased", null, Key(KeyCode.VcD));
        Press();
        Require(triggers == 2, "主键释放后无法再次唤醒");
        Invoke(service, "OnKeyReleased", null, Key(KeyCode.VcLeftControl));
        Press();
        Require(triggers == 3, "修饰键释放后无法再次唤醒");
        Field(service, "_lastHotkeyPressedAt").SetValue(service, Environment.TickCount64 - 3_001);
        Press();
        Require(triggers == 4, "遗漏释放事件后没有恢复");
        Invoke(service, "OnKeyReleased", null, Key(KeyCode.VcD));
        Invoke(service, "OnKeyPressed", null, Key(KeyCode.VcD, ModifierMask.Ctrl | ModifierMask.Alt | ModifierMask.Shift));
        Require(triggers == 4, "额外修饰键错误匹配");
        var ordinaryRelease = Key(KeyCode.VcD);
        Invoke(service, "OnKeyReleased", null, ordinaryRelease);
        Require(!ordinaryRelease.SuppressEvent, "普通主键释放被吞掉");
        Console.WriteLine("PASS 快捷键重复抑制、主键/修饰键释放、遗漏释放恢复和精确修饰键匹配");

        void Press() => Invoke(service, "OnKeyPressed", null, Key(KeyCode.VcD, ModifierMask.Ctrl | ModifierMask.Alt));
    }

    private static KeyboardHookEventArgs Key(KeyCode key, ModifierMask mask = ModifierMask.None) =>
        new(new UioHookEvent { Mask = mask, Keyboard = new KeyboardEventData { KeyCode = key } });

    private static IWindowBaseImpl NativeWindow(Window window) =>
        (IWindowBaseImpl)typeof(TopLevel).GetProperty("PlatformImpl", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(window)!;

    private static void RaiseNativeEvent(Window window, string name) =>
        ((Action?)typeof(IWindowBaseImpl).GetProperty(name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(NativeWindow(window)))?.Invoke();

    private static void Deactivate(Window window) => RaiseNativeEvent(window, "Deactivated");

    private static StrToolkit.App AppWithWindow(MainWindow window)
    {
        var app = new StrToolkit.App();
        Field(app, "_mainWindow").SetValue(app, window);
        return app;
    }

    private static FieldInfo Field(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static void Invoke(object target, string name, params object?[] arguments) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
