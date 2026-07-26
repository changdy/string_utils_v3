using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using StrToolkit.Services;
using StrToolkit.ViewModels;

namespace StrToolkit.Views;

public partial class MainWindow : Window
{
    public const int DefaultWindowWidth = 760;
    public const int DefaultWindowHeight = 400;

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    /// <summary>执行处理并写回剪贴板后触发（App 层负责 nextStep 之外的联动）。</summary>
    public event Action? ExecuteRequested;

    /// <summary>修改快捷键模式下点击 Enter 保存时触发。</summary>
    public event Action<string>? HotkeySaveRequested;

    private const int WakeGraceMs = 300;
    private DateTime _lastWake = DateTime.MinValue;

    /// <summary>
    /// 剪贴板加载代数：每次唤醒递增；隐藏时再递增以作废进行中的异步读取，
    /// 避免「读完后窗口已关 / 新一次唤醒」把过期结果写回。
    /// </summary>
    private int _clipboardLoadGeneration;
    private int _enterAnimationGeneration;
    private readonly Dictionary<Control, int> _solverAnimationGenerations = new();

    /// <summary>唤醒显示前调用：短时间内忽略激活过程中的瞬时失焦，避免窗口闪烁。</summary>
    public void MarkWake() => _lastWake = DateTime.UtcNow;

    /// <summary>
    /// 热键/托盘唤醒后主动读剪贴板。不依赖 <see cref="Window.Activated"/> 是否触发
    /// （窗口已可见时再按热键常常不会再次 Activated）。
    /// </summary>
    public void LoadClipboardFromWake()
    {
        int generation = Interlocked.Increment(ref _clipboardLoadGeneration);
        Log($"LoadClipboardFromWake gen={generation}");
        _ = ReadClipboardAndSelectAsync(generation);
    }

    public MainWindow()
    {
        InitializeComponent();

        Deactivated += (_, _) =>
        {
            if ((DateTime.UtcNow - _lastWake).TotalMilliseconds < WakeGraceMs)
            {
                Log("Deactivated (唤醒宽限期内，忽略)");
                return;
            }
            Log("Deactivated");
            Dispatcher.UIThread.Post(HideAndReset);
        };
        // 兜底：从其它应用切回本窗时也刷新；热键主路径在 ShowWindowAtCursor 里主动调用
        Activated += (_, _) =>
        {
            Log("Activated");
            LoadClipboardFromWake();
        };
        AddHandler(KeyDownEvent, OnPreviewKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    public void HideAndReset()
    {
        // 作废尚未完成的剪贴板读取，防止隐藏后异步结果回写 BodyText
        Interlocked.Increment(ref _clipboardLoadGeneration);
        if (ViewModel is { } vm)
        {
            vm.ChangeHotKeyMode = false;
            vm.BodyText = "";
        }
        Hide();
    }

    private static void Log(string msg)
    {
        try { System.IO.File.AppendAllText(@"c:\project\string_utils_v2\clip.log", $"{DateTime.Now:HH:mm:ss.fff} {msg}\n"); } catch { }
    }

    private async Task ReadClipboardAndSelectAsync(int generation)
    {
        Log($"ReadClipboard enter gen={generation}, vm={(ViewModel is not null)}, hotkeyMode={ViewModel?.ChangeHotKeyMode}");
        if (ViewModel is not { } vm || vm.ChangeHotKeyMode)
        {
            return;
        }
        try
        {
            Log($"Clipboard null? {Clipboard is null}");
            var text = Clipboard is null ? null : await Clipboard.GetTextAsync();
            // 唤醒被取消、窗口已隐藏、或又一次更新的读取已经开始 → 丢弃本次结果
            if (generation != Volatile.Read(ref _clipboardLoadGeneration))
            {
                Log($"ReadClipboard stale gen={generation}, current={_clipboardLoadGeneration}");
                return;
            }
            if (!IsVisible || vm.ChangeHotKeyMode)
            {
                Log("ReadClipboard skip: not visible or hotkey mode");
                return;
            }
            Log($"clipboard text: {(text is null ? "<null>" : $"len={text.Length} [{(text.Length > 50 ? text[..50] : text)}]")}");
            if (!string.IsNullOrEmpty(text))
            {
                vm.AutoSelect(text);
            }
        }
        catch (Exception e)
        {
            Log($"读取剪贴板失败: {e}");
            AppLog.Error("读取剪贴板失败", e);
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        if (vm.ChangeHotKeyMode)
        {
            if (e.Key == Key.Enter)
            {
                HotkeySaveRequested?.Invoke(vm.CapturedHotkey);
                e.Handled = true;
                return;
            }
            CaptureHotkey(e, vm);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                HideAndReset();
                e.Handled = true;
                break;
            case Key.Enter when !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                PlayEnterAnimation();
                ExecuteSelected();
                e.Handled = true;
                break;
        }
    }

    private void OnEnterButtonClick(object? sender, RoutedEventArgs e)
    {
        PlayEnterAnimation();
        if (ViewModel is { ChangeHotKeyMode: true } vm)
        {
            HotkeySaveRequested?.Invoke(vm.CapturedHotkey);
            return;
        }
        ExecuteSelected();
    }

    private async void PlayEnterAnimation()
    {
        if (EnterButton.TranslatePoint(
                new Point(EnterButton.Bounds.Width / 2, EnterButton.Bounds.Height / 2),
                EnterBubbleLayer) is { } center)
        {
            EnterBubbleLayer.Start(center, EnterButton.Bounds.Size);
        }

        int generation = Interlocked.Increment(ref _enterAnimationGeneration);
        if (EnterButton.RenderTransform is not ScaleTransform scale)
        {
            return;
        }
        scale.ScaleX = 0.9;
        scale.ScaleY = 0.9;

        await Task.Delay(90);
        if (generation != Volatile.Read(ref _enterAnimationGeneration))
        {
            return;
        }

        scale.ScaleX = 1;
        scale.ScaleY = 1;
    }

    private void ExecuteSelected()
    {
        if (ViewModel is not { } vm)
        {
            return;
        }
        string? result = vm.Execute();
        if (result is not null)
        {
            _ = Dispatcher.UIThread.InvokeAsync(async () =>
            {
                try
                {
                    if (Clipboard is not null)
                    {
                        await Clipboard.SetTextAsync(result);
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Error("写入剪贴板失败", ex);
                }
            });
            ExecuteRequested?.Invoke();
        }
    }

    private void OnSolverIconPressed(object? sender, PointerPressedEventArgs e)
    {
        // 对齐 Electron：点图标 = 选中并立即执行 transfer（preload.mjs parseText）
        if (sender is Control { DataContext: SolverItemViewModel item } && ViewModel is { } vm)
        {
            e.Handled = true;
            if (vm.ChangeHotKeyMode)
            {
                return;
            }
            PlaySolverClickAnimation((Control)sender);
            vm.SelectSolver(item);
            ExecuteSelected();
        }
    }

    private async void PlaySolverClickAnimation(Control control)
    {
        int generation = _solverAnimationGenerations.TryGetValue(control, out int current)
            ? current + 1
            : 1;
        _solverAnimationGenerations[control] = generation;
        _ = PlaySolverSheenAsync(control, generation);

        var (scale, translate) = GetSolverTransforms(control);
        if (scale is null || translate is null)
        {
            return;
        }

        // 按下：短促收缩并下沉，先给出明确的物理反馈。
        scale.ScaleX = 0.93;
        scale.ScaleY = 0.93;
        translate.Y = 1;

        await Task.Delay(70);
        if (!IsCurrentSolverAnimation(control, generation))
        {
            return;
        }

        // 松开：略微超过原尺寸，再稳定下来。
        scale.ScaleX = 1.05;
        scale.ScaleY = 1.05;
        translate.Y = 0;

        await Task.Delay(85);
        if (!IsCurrentSolverAnimation(control, generation))
        {
            return;
        }

        scale.ScaleX = 1;
        scale.ScaleY = 1;
    }

    private async Task PlaySolverSheenAsync(Control control, int generation)
    {
        var sheen = GetSolverClickSheen(control);
        if (sheen?.RenderTransform is not TransformGroup group ||
            group.Children.OfType<TranslateTransform>().FirstOrDefault() is not { } translate)
        {
            return;
        }

        const int frames = 13;
        for (int frame = 0; frame <= frames; frame++)
        {
            if (!IsCurrentSolverAnimation(control, generation))
            {
                sheen.Opacity = 0;
                return;
            }
            double t = frame / (double)frames;
            double eased = 1 - Math.Pow(1 - t, 3);
            translate.X = -24 + 82 * eased;
            sheen.Opacity = t < 0.24
                ? 0.38 * (t / 0.24)
                : 0.38 * ((1 - t) / 0.76);
            await Task.Delay(16);
        }
        sheen.Opacity = 0;
    }

    private bool IsCurrentSolverAnimation(Control control, int generation) =>
        _solverAnimationGenerations.TryGetValue(control, out int current) && current == generation;

    private static (ScaleTransform? Scale, TranslateTransform? Translate) GetSolverTransforms(Control control)
    {
        if (control.RenderTransform is not TransformGroup group)
        {
            return (null, null);
        }
        return (
            group.Children.OfType<ScaleTransform>().FirstOrDefault(),
            group.Children.OfType<TranslateTransform>().FirstOrDefault());
    }

    /// <summary>按结构定位点击高光 Border：Border → Grid → Border[1]。</summary>
    private static Border? GetSolverClickSheen(Control control)
    {
        if (control is not Border { Child: Grid innerGrid }) return null;
        if (innerGrid.Children.Count < 2) return null;
        return innerGrid.Children[1] as Border;
    }

    private void CaptureHotkey(KeyEventArgs e, MainWindowViewModel vm)
    {
        var parts = new List<string>();
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            parts.Add("Alt");
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            parts.Add("Shift");
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            parts.Add("Cmd");
        }

        string? mainKey = e.Key switch
        {
            >= Key.A and <= Key.Z => e.Key.ToString(),
            >= Key.D0 and <= Key.D9 => e.Key.ToString()[1..],
            >= Key.NumPad0 and <= Key.NumPad9 => "Num" + e.Key.ToString()[6..],
            >= Key.F1 and <= Key.F24 => e.Key.ToString(),
            Key.Space => "Space",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Left => "Left",
            Key.Right => "Right",
            Key.OemTilde => "`",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemPipe => "\\",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin => null,
            _ => e.Key.ToString()
        };

        if (mainKey is not null)
        {
            parts.Add(mainKey);
        }
        if (parts.Count > 0)
        {
            vm.CapturedHotkey = string.Join("+", parts);
            vm.BodyText = $"当前快捷键: {vm.CapturedHotkey}\n点击Enter按钮即可保存";
        }
    }
}
