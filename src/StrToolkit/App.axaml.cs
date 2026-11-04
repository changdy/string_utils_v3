using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using StrToolkit.Services;
using StrToolkit.Solvers;
using StrToolkit.ViewModels;
using StrToolkit.Views;

namespace StrToolkit;

public partial class App : Application
{
    private static readonly TimeSpan WebServicesShutdownTimeout = TimeSpan.FromSeconds(5);

    private SettingsService _settings = null!;
    private HotkeyService? _hotkey;
    private JsonCrackServer _jsonCrack = null!;
    private JsonHeroService _jsonHero = null!;
    private readonly CancellationTokenSource _webServicesLifetime = new();
    private Task _webServicesStartupTask = Task.CompletedTask;
    private int _webServicesShutdownStarted;
    private JsonPreviewService _preview = null!;
    private MainWindowViewModel _viewModel = null!;
    private MainWindow _mainWindow = null!;
    private TrayIcon? _trayIcon;
    private DateTime _lastAutoHide = DateTime.MinValue;
    private Timer? _diagnosticsTimer;
    private long _wakeRequestSequence;
    private int _diagnosticUiPending;
    private long _diagnosticUiQueuedAt;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        VsCodeDiffService.CleanupTempFiles();
        _settings = new SettingsService();
        _jsonCrack = new JsonCrackServer();
        _jsonHero = new JsonHeroService();
        _preview = new JsonPreviewService(_jsonCrack, _jsonHero);
        _webServicesStartupTask = StartWebServicesAsync(_webServicesLifetime.Token);
        desktop.Exit += OnApplicationExit;

        _viewModel = new MainWindowViewModel(_settings);
        foreach (var solver in BuildSolvers())
        {
            _viewModel.AddSolver(solver);
        }

        _mainWindow = new MainWindow { DataContext = _viewModel };
        _mainWindow.HotkeySaveRequested += OnHotkeySaveRequested;

        SetupHotkey();
        SetupTray();
        _diagnosticsTimer = new Timer(WriteDiagnosticHeartbeat, null,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        SingleInstance.SecondInstanceLaunched += () =>
            QueueWindowWake("second-instance");

        base.OnFrameworkInitializationCompleted();

        // 首次启动也应给出可见反馈；后续仍保持失焦隐藏、快捷键和托盘唤醒语义。
        QueueWindowWake("startup");
    }

    private async Task StartWebServicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAll(
                _jsonCrack.StartAsync(cancellationToken),
                _jsonHero.StartAsync(cancellationToken))
                .ConfigureAwait(false);

            if (!_jsonCrack.IsRunning)
            {
                AppLog.Warn("JSONCrack Web 服务未能启动，相关预览功能不可用。");
            }
            if (!_jsonHero.IsRunning)
            {
                AppLog.Warn("JSON Hero Web 服务未能启动，相关预览功能不可用。");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 应用退出时取消仍在进行的服务启动。
        }
        catch (Exception e)
        {
            AppLog.Error("Web 服务启动失败", e);
        }
    }

    private void OnApplicationExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        AppLog.Info($"应用开始退出: exitCode={e.ApplicationExitCode}");
        _diagnosticsTimer?.Dispose();
        _hotkey?.Dispose();

        if (Interlocked.Exchange(ref _webServicesShutdownStarted, 1) != 0)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(WebServicesShutdownTimeout);
        try
        {
            _webServicesLifetime.Cancel();
            StopWebServicesAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            AppLog.Warn($"Web 服务未能在 {WebServicesShutdownTimeout.TotalSeconds:0} 秒内停止。");
        }
        catch (Exception exception)
        {
            AppLog.Error("Web 服务停止失败", exception);
        }
        finally
        {
            _webServicesLifetime.Dispose();
        }
    }

    private async Task StopWebServicesAsync(CancellationToken cancellationToken)
    {
        await _webServicesStartupTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(
                _jsonHero.StopAsync(cancellationToken),
                _jsonCrack.StopAsync(cancellationToken))
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private List<ISolver> BuildSolvers()
    {
        var solvers = new List<ISolver>
        {
            new IdJoinSolver(),
            new JsonExtractSolver(),
            new JsonViewSolver(json => _preview.OpenPreview(json)),
            new MybatisExtractSolver(),
            new NamingConversionSolver(),
            new SortDistinctSolver(),
            new SqlExtractSolver(),
            new JsonDiffSolver(VsCodeDiffService.OpenDiff)
        };

        // 加载用户脚本（Jint）：兼容根目录旧单文件，并支持一级目录脚本包。
        if (Directory.Exists(SettingsService.UserScriptDir))
        {
            foreach (var file in Directory.EnumerateFiles(
                         SettingsService.UserScriptDir,
                         "*.js",
                         SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    solvers.Add(JsUserScriptSolver.Load(file));
                }
                catch (Exception e)
                {
                    AppLog.Error($"加载用户脚本失败: {file}", e);
                }
            }

            foreach (var directory in Directory.EnumerateDirectories(SettingsService.UserScriptDir)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(Path.Combine(directory, "index.js")))
                {
                    continue;
                }

                try
                {
                    solvers.Add(JsUserScriptSolver.Load(
                        UserScriptPackage.FromDirectory(directory)));
                }
                catch (Exception e)
                {
                    AppLog.Error($"加载用户脚本包失败: {directory}", e);
                }
            }
        }
        return solvers;
    }

    private void SetupHotkey()
    {
        try
        {
            _hotkey = new HotkeyService();
            _hotkey.HotkeyPressed += trigger => QueueWindowWake("hotkey", trigger);
            _hotkey.GlobalMousePressed += OnGlobalMousePressed;
            _hotkey.AvailabilityChanged += (available, message) =>
                Dispatcher.UIThread.Post(() => UpdateHotkeyAvailability(available, message));
            if (!_hotkey.Register(_settings.Settings.Accelerator))
            {
                AppLog.Warn($"快捷键格式无效: {_settings.Settings.Accelerator}");
            }
            _ = StartHotkeyAsync(_hotkey);
        }
        catch (Exception e)
        {
            AppLog.Error("全局快捷键初始化失败（Wayland 下受限）", e);
        }
    }

    private static async Task StartHotkeyAsync(HotkeyService hotkey)
    {
        if (!await hotkey.StartAsync())
        {
            AppLog.Warn(hotkey.LastError ?? "全局快捷键不可用");
        }
    }

    private void UpdateHotkeyAvailability(bool available, string? message)
    {
        if (_trayIcon is not null)
        {
            _trayIcon.ToolTipText = available
                ? "字符串小工具"
                : "字符串小工具（全局快捷键不可用，请使用托盘）";
        }
        if (!available && !string.IsNullOrWhiteSpace(message))
        {
            AppLog.Warn(message);
        }
    }

    /// <summary>点击窗口区域之外时自动隐藏（不依赖窗口激活状态，等效 Electron 的 blur 隐藏）。</summary>
    private void OnGlobalMousePressed(int x, int y)
    {
        long generation = _mainWindow.WakeGeneration;
        long pressedAt = Environment.TickCount64;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_mainWindow.IsVisible)
            {
                return;
            }
            if (generation != _mainWindow.WakeGeneration)
            {
                AppLog.Info($"Window.StaleMouseIgnored: queuedGeneration={generation}, " +
                            $"queueDelayMs={Environment.TickCount64 - pressedAt}, {_mainWindow.DiagnosticState}");
                return;
            }
            var pos = _mainWindow.Position;
            var size = PixelSize.FromSize(_mainWindow.ClientSize, _mainWindow.DesktopScaling);
            bool inside = x >= pos.X && x < pos.X + size.Width
                          && y >= pos.Y && y < pos.Y + size.Height;
            if (!inside)
            {
                _lastAutoHide = DateTime.UtcNow;
                _mainWindow.HideAndReset($"outside-click x={x} y={y} queueDelayMs={Environment.TickCount64 - pressedAt}");
            }
        });
    }

    private void QueueWindowWake(string source, long trigger = 0)
    {
        long requestId = Interlocked.Increment(ref _wakeRequestSequence);
        long queuedAt = Environment.TickCount64;
        AppLog.Info($"Wake.Queued: request={requestId}, source={source}, trigger={trigger}");
        Dispatcher.UIThread.Post(() =>
        {
            AppLog.Info($"Wake.Dispatched: request={requestId}, source={source}, " +
                        $"queueDelayMs={Environment.TickCount64 - queuedAt}");
            try
            {
                ShowWindowAtCursor(source, requestId);
            }
            catch (Exception exception)
            {
                AppLog.Error($"Wake.Failed: request={requestId}, source={source}", exception);
            }
        });
    }

    private void ShowWindowAtCursor(string source, long requestId)
    {
        _mainWindow.MarkWake(source, requestId);
        try
        {
            // 对应 Electron: 窗口出现在鼠标位置附近 (x-180, y-100)
            if (CursorService.GetCursorPosition() is { } cursor)
            {
                var target = new PixelPoint(cursor.X - 180, cursor.Y - 100);
                var screen = _mainWindow.Screens.ScreenFromPoint(cursor) ?? _mainWindow.Screens.Primary;
                if (screen is not null)
                {
                    target = ClampToWorkingArea(target, screen);
                }
                AppLog.Info($"Window.Position: request={requestId}, cursor={cursor}, target={target}, " +
                            $"workingArea={screen?.WorkingArea}, screenScaling={screen?.Scaling}");
                _mainWindow.Position = target;
            }
            else
            {
                // 无法获取光标位置时退化为屏幕居中偏上
                var screen = _mainWindow.Screens.ScreenFromWindow(_mainWindow) ?? _mainWindow.Screens.Primary;
                if (screen is not null)
                {
                    _mainWindow.Position = GetWorkingAreaPosition(screen, verticalDivisor: 3);
                }
            }
        }
        catch (Exception exception)
        {
            AppLog.Error($"Window.PositionFailed: request={requestId}", exception);
        }
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }
        _mainWindow.Show();
        AppLog.Info($"Window.ShowReturned: {_mainWindow.DiagnosticState}");
        _mainWindow.Activate();
        WindowActivator.ForceForeground(_mainWindow);
        // 每次热键/托盘唤醒都强制读剪贴板，不依赖 Activated 是否再次触发
        // （窗口已显示时再按热键，Activated 经常不会发，导致正文不刷新）
        _mainWindow.LoadClipboardFromWake();
        AppLog.Info($"Wake.Completed: source={source}, {_mainWindow.DiagnosticState}");
        _ = VerifyWakeAsync(_mainWindow.WakeGeneration);
    }

    private async Task VerifyWakeAsync(long generation)
    {
        try
        {
            await Task.Delay(500);
            if (generation != _mainWindow.WakeGeneration) return;
            string state = $"Wake.Verified: {_mainWindow.DiagnosticState}, {WindowActivator.GetDiagnosticState(_mainWindow)}";
            if (_mainWindow.IsVisible && _mainWindow.IsActive) AppLog.Info(state);
            else AppLog.Warn(state);
        }
        catch (Exception exception)
        {
            AppLog.Error("Wake.VerifyFailed", exception);
        }
    }

    private void WriteDiagnosticHeartbeat(object? state)
    {
        try
        {
            AppLog.Info($"Diagnostics.Heartbeat: {_hotkey?.GetDiagnosticState() ?? "hotkey=uninitialized"}");
            if (Interlocked.CompareExchange(ref _diagnosticUiPending, 1, 0) != 0)
            {
                AppLog.Warn($"Diagnostics.UIStillPending: queueDelayMs={Environment.TickCount64 - Interlocked.Read(ref _diagnosticUiQueuedAt)}");
                return;
            }
            long queuedAt = Environment.TickCount64;
            Interlocked.Exchange(ref _diagnosticUiQueuedAt, queuedAt);
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref _diagnosticUiPending, 0);
                AppLog.Info($"Diagnostics.UI: queueDelayMs={Environment.TickCount64 - queuedAt}, " +
                            $"{_mainWindow.DiagnosticState}, {WindowActivator.GetDiagnosticState(_mainWindow)}");
            });
        }
        catch (Exception exception)
        {
            AppLog.Error("Diagnostics.HeartbeatFailed", exception);
        }
    }

    /// <summary>
    /// Width/Height 是逻辑像素，Screen.WorkingArea 和 Window.Position 是物理像素；
    /// 边界计算必须使用目标屏幕的缩放比例进行换算。
    /// </summary>
    private static PixelSize GetWindowPixelSize(Screen screen) =>
        PixelSize.FromSize(
            new Size(MainWindow.DefaultWindowWidth, MainWindow.DefaultWindowHeight),
            screen.Scaling);

    private static PixelPoint ClampToWorkingArea(PixelPoint target, Screen screen)
    {
        var bounds = screen.WorkingArea;
        var windowSize = GetWindowPixelSize(screen);
        int maxX = bounds.X + Math.Max(0, bounds.Width - windowSize.Width);
        int maxY = bounds.Y + Math.Max(0, bounds.Height - windowSize.Height);
        return new PixelPoint(
            Math.Clamp(target.X, bounds.X, maxX),
            Math.Clamp(target.Y, bounds.Y, maxY));
    }

    private static PixelPoint GetWorkingAreaPosition(Screen screen, int verticalDivisor)
    {
        var bounds = screen.WorkingArea;
        var windowSize = GetWindowPixelSize(screen);
        int availableWidth = Math.Max(0, bounds.Width - windowSize.Width);
        int availableHeight = Math.Max(0, bounds.Height - windowSize.Height);
        return new PixelPoint(
            bounds.X + availableWidth / 2,
            bounds.Y + availableHeight / verticalDivisor);
    }

    private void SetupTray()
    {
        var menu = new NativeMenu();

        // 功能显隐开关
        foreach (var item in _viewModel.Solvers.Reverse().ToList())
        {
            var menuItem = new NativeMenuItem(item.Describe)
            {
                ToggleType = NativeMenuItemToggleType.CheckBox,
                IsChecked = !_settings.Settings.SkipList.Contains(item.Name)
            };
            string name = item.Name;
            menuItem.Click += (_, _) =>
            {
                bool nowChecked = menuItem.IsChecked;
                if (nowChecked)
                {
                    _settings.Settings.SkipList.Remove(name);
                }
                else if (!_settings.Settings.SkipList.Contains(name))
                {
                    _settings.Settings.SkipList.Add(name);
                }
                _settings.Save();
                _viewModel.SetSolverVisible(name, nowChecked);
            };
            menu.Items.Add(menuItem);
        }

        menu.Items.Add(new NativeMenuItemSeparator());

        var autoLaunchItem = new NativeMenuItem("开机自动启动")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = _settings.Settings.AutoLaunch
        };
        autoLaunchItem.Click += (_, _) =>
        {
            _settings.Settings.AutoLaunch = autoLaunchItem.IsChecked;
            _settings.Save();
            AutoLaunchService.SetEnabled(autoLaunchItem.IsChecked);
        };
        menu.Items.Add(autoLaunchItem);

        var openScriptsItem = new NativeMenuItem("打开用户脚本目录");
        openScriptsItem.Click += (_, _) =>
        {
            try
            {
                JsonPreviewService.OpenInBrowser(new Uri(SettingsService.UserScriptDir).AbsoluteUri);
            }
            catch (Exception e)
            {
                AppLog.Error($"打开目录失败: {SettingsService.UserScriptDir}", e);
            }
        };
        menu.Items.Add(openScriptsItem);

        var openLogsItem = new NativeMenuItem("打开日志目录");
        openLogsItem.Click += (_, _) =>
        {
            try
            {
                AppLog.Info($"Diagnostics.OpenLogs: {_mainWindow.DiagnosticState}");
                WriteDiagnosticHeartbeat(null);
                Directory.CreateDirectory(AppLog.LogDirectory);
                JsonPreviewService.OpenInBrowser(new Uri(AppLog.LogDirectory).AbsoluteUri);
            }
            catch (Exception exception)
            {
                AppLog.Error($"打开日志目录失败: {AppLog.LogDirectory}", exception);
            }
        };
        menu.Items.Add(openLogsItem);

        var hotkeyItem = new NativeMenuItem("设置快捷键");
        hotkeyItem.Click += (_, _) =>
        {
            _viewModel.ChangeHotKeyMode = true;
            string statusHint = _hotkey is { IsAvailable: false }
                ? $"\n全局快捷键当前不可用：{_hotkey.LastError}"
                : "";
            _viewModel.BodyText =
                $"当前快捷键: {_settings.Settings.Accelerator}\n请直接按下新快捷键，然后点击Enter按钮{statusHint}";
            var screen = _mainWindow.Screens.Primary;
            if (screen is not null)
            {
                _mainWindow.Position = GetWorkingAreaPosition(screen, verticalDivisor: 2);
            }
            _mainWindow.MarkWake("hotkey-settings", Interlocked.Increment(ref _wakeRequestSequence));
            _mainWindow.Show();
            _mainWindow.Activate();
            WindowActivator.ForceForeground(_mainWindow);
        };
        menu.Items.Add(hotkeyItem);

        var exitItem = new NativeMenuItem("退出");
        exitItem.Click += (_, _) =>
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        };
        menu.Items.Add(exitItem);

        _trayIcon = new TrayIcon
        {
            ToolTipText = _hotkey is { IsAvailable: false, LastError: not null }
                ? "字符串小工具（全局快捷键不可用，请使用托盘）"
                : "字符串小工具",
            Menu = menu,
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://StrToolkit/Assets/app-icon/tray-icon.png")))
        };
        _trayIcon.Clicked += (_, _) =>
        {
            AppLog.Info($"Tray.Clicked: {_mainWindow.DiagnosticState}, {_hotkey?.GetDiagnosticState()}");
            if (_mainWindow.IsVisible)
            {
                _mainWindow.HideAndReset("tray-click");
            }
            else if ((DateTime.UtcNow - _lastAutoHide).TotalMilliseconds > 400)
            {
                // 点击托盘时全局鼠标监听刚把窗口隐藏，此时不再重新显示（保持托盘切换语义）
                QueueWindowWake("tray");
            }
        };

        TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
    }

    private void OnHotkeySaveRequested(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }
        _hotkey?.Unregister();
        bool valid = _hotkey?.Register(key) ?? false;
        bool success = valid && _hotkey!.IsAvailable;
        if (success)
        {
            _settings.Settings.Accelerator = key;
            _settings.Save();
            _viewModel.BodyText = $"快捷键: {key}注册成功";
        }
        else
        {
            string reason = valid
                ? _hotkey?.LastError ?? "底层全局监听不可用"
                : "快捷键格式无效";
            _viewModel.BodyText = $"快捷键: {key}注册失败\n{reason}";
            _hotkey?.Register(_settings.Settings.Accelerator);
        }
        _viewModel.ChangeHotKeyMode = false;
    }
}
