using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpHook;
using SharpHook.Native;

namespace StrToolkit.Services;

/// <summary>
/// 基于 SharpHook 的跨平台全局快捷键。
/// Windows、macOS 和 Linux/X11 可用；Wayland 下保留托盘入口并报告明确的降级原因。
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly SimpleGlobalHook _hook;
    private readonly TaskCompletionSource<bool> _startup =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private HotkeyBinding? _binding;
    private int _startRequested;
    private int _hotkeyDown;
    private int _disposed;
    private volatile bool _isAvailable;
    private string? _lastError;

    public bool IsAvailable => _isAvailable;
    public string? LastError => _lastError;

    public event Action? HotkeyPressed;

    /// <summary>底层全局监听可用状态发生变化。</summary>
    public event Action<bool, string?>? AvailabilityChanged;

    /// <summary>全局鼠标按下（屏幕坐标），用于点击窗口外自动隐藏。</summary>
    public event Action<int, int>? GlobalMousePressed;

    public HotkeyService()
    {
        // 事件处理器都很短；使用同步分发可在 Windows/macOS 上阻止匹配按键继续传给前台应用。
        _hook = new SimpleGlobalHook(runAsyncOnBackgroundThread: true);
        _hook.HookEnabled += OnHookEnabled;
        _hook.HookDisabled += OnHookDisabled;
        _hook.KeyPressed += OnKeyPressed;
        _hook.KeyReleased += OnKeyReleased;
        _hook.MousePressed += OnMousePressed;
    }

    /// <summary>启动底层全局监听，并等待监听成功或失败。</summary>
    public async Task<bool> StartAsync()
    {
        if (Interlocked.Exchange(ref _startRequested, 1) != 0)
        {
            return await _startup.Task.ConfigureAwait(false);
        }

        if (IsWaylandSession())
        {
            SetUnavailable("Linux Wayland 不允许普通应用静默监听全局快捷键，请使用托盘入口或切换到 X11 会话。");
            _startup.TrySetResult(false);
            return false;
        }

        _ = RunHookAsync();

        // 正常桌面环境会立即触发 HookEnabled；超时可以避免异常平台永久挂起初始化。
        Task completed = await Task.WhenAny(_startup.Task, Task.Delay(TimeSpan.FromSeconds(5)))
            .ConfigureAwait(false);
        if (completed != _startup.Task)
        {
            SetUnavailable(GetPlatformFailureHint("全局快捷键监听启动超时"));
            _startup.TrySetResult(false);
        }
        return await _startup.Task.ConfigureAwait(false);
    }

    /// <summary>解析并应用加速键。返回 false 表示加速键格式无效。</summary>
    public bool Register(string accelerator)
    {
        if (!TryParseAccelerator(accelerator, out var binding))
        {
            return false;
        }

        Volatile.Write(ref _binding, binding);
        Interlocked.Exchange(ref _hotkeyDown, 0);
        return true;
    }

    public void Unregister()
    {
        Volatile.Write(ref _binding, null);
        Interlocked.Exchange(ref _hotkeyDown, 0);
    }

    private async Task RunHookAsync()
    {
        try
        {
            await _hook.RunAsync().ConfigureAwait(false);
            if (Volatile.Read(ref _disposed) == 0)
            {
                SetUnavailable("全局快捷键监听已意外停止。");
            }
        }
        catch (Exception e)
        {
            string message = GetPlatformFailureHint($"全局快捷键监听启动失败: {e.Message}");
            SetUnavailable(message);
            _startup.TrySetResult(false);
            AppLog.Error(message, e);
        }
    }

    private void OnHookEnabled(object? sender, HookEventArgs e)
    {
        _lastError = null;
        _isAvailable = true;
        _startup.TrySetResult(true);
        AvailabilityChanged?.Invoke(true, null);
    }

    private void OnHookDisabled(object? sender, HookEventArgs e)
    {
        _isAvailable = false;
        Interlocked.Exchange(ref _hotkeyDown, 0);
        if (Volatile.Read(ref _disposed) == 0)
        {
            SetUnavailable("全局快捷键监听已停止。");
        }
    }

    private void OnMousePressed(object? sender, MouseHookEventArgs e)
    {
        GlobalMousePressed?.Invoke(e.Data.X, e.Data.Y);
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        var binding = Volatile.Read(ref _binding);
        if (!_isAvailable || binding is null || e.Data.KeyCode != binding.Key ||
            !HasExactModifiers(e.RawEvent.Mask, binding.Modifiers))
        {
            return;
        }

        // 系统按键自动重复会产生连续 KeyPressed；一次按住只唤醒一次。
        if (Interlocked.Exchange(ref _hotkeyDown, 1) != 0)
        {
            SuppressIfSupported(e);
            return;
        }

        SuppressIfSupported(e);
        HotkeyPressed?.Invoke();
    }

    private void OnKeyReleased(object? sender, KeyboardHookEventArgs e)
    {
        var binding = Volatile.Read(ref _binding);
        if (binding is null || e.Data.KeyCode != binding.Key)
        {
            return;
        }

        Interlocked.Exchange(ref _hotkeyDown, 0);
        SuppressIfSupported(e);
    }

    private static void SuppressIfSupported(HookEventArgs e)
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            e.SuppressEvent = true;
        }
    }

    private static bool HasExactModifiers(ModifierMask mask, HashSet<ModifierMask> required)
    {
        if (!required.All(m => (mask & m) != ModifierMask.None))
        {
            return false;
        }

        foreach (var modifier in new[]
                 {
                     ModifierMask.Ctrl, ModifierMask.Alt, ModifierMask.Shift, ModifierMask.Meta
                 })
        {
            if (!required.Contains(modifier) && (mask & modifier) != ModifierMask.None)
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryParseAccelerator(string accelerator, out HotkeyBinding binding)
    {
        binding = null!;
        if (string.IsNullOrWhiteSpace(accelerator))
        {
            return false;
        }

        var modifiers = new HashSet<ModifierMask>();
        KeyCode key = KeyCode.VcUndefined;
        bool hasMainKey = false;

        foreach (string part in accelerator.Split('+',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string normalized = part.ToLowerInvariant();
            switch (normalized)
            {
                case "commandorcontrol" or "cmdorctrl":
                    modifiers.Add(OperatingSystem.IsMacOS() ? ModifierMask.Meta : ModifierMask.Ctrl);
                    break;
                case "ctrl" or "control":
                    modifiers.Add(ModifierMask.Ctrl);
                    break;
                case "cmd" or "command" or "meta" or "super":
                    modifiers.Add(ModifierMask.Meta);
                    break;
                case "alt" or "option":
                    modifiers.Add(ModifierMask.Alt);
                    break;
                case "shift":
                    modifiers.Add(ModifierMask.Shift);
                    break;
                default:
                    if (hasMainKey)
                    {
                        return false;
                    }
                    key = ParseKey(part);
                    hasMainKey = key != KeyCode.VcUndefined;
                    if (!hasMainKey)
                    {
                        return false;
                    }
                    break;
            }
        }

        if (!hasMainKey)
        {
            return false;
        }

        binding = new HotkeyBinding(modifiers, key);
        return true;
    }

    private static KeyCode ParseKey(string key)
    {
        key = key.Trim();
        if (key.Length == 1)
        {
            char c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z' && Enum.TryParse<KeyCode>("Vc" + c, out var letter))
            {
                return letter;
            }
            if (c is >= '0' and <= '9' && Enum.TryParse<KeyCode>("Vc" + c, out var digit))
            {
                return digit;
            }
            return c switch
            {
                '`' => KeyCode.VcBackQuote,
                '-' => KeyCode.VcMinus,
                '=' => KeyCode.VcEquals,
                '[' => KeyCode.VcOpenBracket,
                ']' => KeyCode.VcCloseBracket,
                '\\' => KeyCode.VcBackslash,
                ';' => KeyCode.VcSemicolon,
                '\'' => KeyCode.VcQuote,
                ',' => KeyCode.VcComma,
                '.' => KeyCode.VcPeriod,
                '/' => KeyCode.VcSlash,
                _ => KeyCode.VcUndefined
            };
        }

        string normalized = key.ToLowerInvariant();
        return normalized switch
        {
            "space" => KeyCode.VcSpace,
            "up" => KeyCode.VcUp,
            "down" => KeyCode.VcDown,
            "left" => KeyCode.VcLeft,
            "right" => KeyCode.VcRight,
            "esc" or "escape" => KeyCode.VcEscape,
            "ins" or "insert" => KeyCode.VcInsert,
            "del" or "delete" => KeyCode.VcDelete,
            "home" => KeyCode.VcHome,
            "end" => KeyCode.VcEnd,
            "pageup" => KeyCode.VcPageUp,
            "pagedown" => KeyCode.VcPageDown,
            "tab" => KeyCode.VcTab,
            "enter" or "return" => KeyCode.VcEnter,
            _ when normalized.StartsWith('f') && int.TryParse(normalized[1..], out int f) && f is >= 1 and <= 24 =>
                Enum.TryParse<KeyCode>("VcF" + f, out var functionKey) ? functionKey : KeyCode.VcUndefined,
            _ when normalized.StartsWith("num") &&
                   Enum.TryParse<KeyCode>("VcNumPad" + normalized[3..], true, out var numberPadKey) => numberPadKey,
            _ => KeyCode.VcUndefined
        };
    }

    private static bool IsWaylandSession()
    {
        return OperatingSystem.IsLinux() &&
               (string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland",
                    StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")));
    }

    private static string GetPlatformFailureHint(string message)
    {
        if (OperatingSystem.IsMacOS())
        {
            return message + " 请在“系统设置 → 隐私与安全性 → 辅助功能”中允许 StrToolkit。";
        }
        if (OperatingSystem.IsLinux())
        {
            return message + " 请确认当前为 X11 会话且 DISPLAY 可用。";
        }
        return message;
    }

    private void SetUnavailable(string message)
    {
        _lastError = message;
        _isAvailable = false;
        AvailabilityChanged?.Invoke(false, message);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Unregister();
        _isAvailable = false;
        try
        {
            _hook.Dispose();
        }
        catch (Exception e)
        {
            AppLog.Error("停止全局快捷键监听失败", e);
        }
    }

    private sealed record HotkeyBinding(HashSet<ModifierMask> Modifiers, KeyCode Key);
}
