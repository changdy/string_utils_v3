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
    // KeyReleased 偶尔会被操作系统或底层钩子漏掉。超过该时间后把仍为按下的状态
    // 视为过期，避免一次漏报导致快捷键在本次进程生命周期内永久失效。
    private const long HotkeyLatchRecoveryMilliseconds = 3_000;
    private const int HookRestartDelayMilliseconds = 1_000;
    private const int HookRestartMaxDelayMilliseconds = 30_000;
    private const ModifierMask KeyboardModifiers =
        ModifierMask.Ctrl | ModifierMask.Alt | ModifierMask.Shift | ModifierMask.Meta;

    private readonly SimpleGlobalHook _hook;
    private readonly Func<ModifierMask?> _readSystemModifiers;
    private readonly TaskCompletionSource<bool> _startup =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private HotkeyBinding? _binding;
    private int _startRequested;
    private int _hotkeyDown;
    private int _suppressMainKeyUp;
    private long _lastHotkeyPressedAt;
    private int _hookEnableGeneration;
    private int _disposed;
    private volatile bool _isAvailable;
    private string? _lastError;
    private long _keyboardEventCount;
    private long _mousePressCount;
    private long _lastKeyboardEventAt;
    private long _lastMousePressAt;
    private long _pressSequence;
    private long _lastMismatchLogAt;
    private int _repeatWasLogged;
    private long _lastModifierCheckAt;
    private int _lastHookModifiers;
    private int _lastSystemModifiers = -1;
    private int _lastEffectiveModifiers;
    private long _modifierRecoveryCount;
    private long _lastModifierAdjustmentLogAt;
    private long _lastSystemReadFailureAt;
    private long _lastMetaEventAt;
    private int _lastMetaKey;
    private int _lastMetaDown;

    public bool IsAvailable => _isAvailable;
    public string? LastError => _lastError;

    public event Action<long>? HotkeyPressed;

    /// <summary>底层全局监听可用状态发生变化。</summary>
    public event Action<bool, string?>? AvailabilityChanged;

    /// <summary>全局鼠标按下（屏幕坐标），用于点击窗口外自动隐藏。</summary>
    public event Action<int, int>? GlobalMousePressed;

    public HotkeyService() : this(WindowsModifierState.Read)
    {
    }

    internal HotkeyService(Func<ModifierMask?> readSystemModifiers)
    {
        _readSystemModifiers = readSystemModifiers;
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

        AppLog.Info($"Hotkey.Start: 请求启动全局监听，systemModifierValidation={OperatingSystem.IsWindows()}");
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
            AppLog.Warn($"Hotkey.Register: 无效快捷键 accelerator={accelerator}");
            return false;
        }

        Volatile.Write(ref _binding, binding);
        ResetHotkeyState();
        AppLog.Info($"Hotkey.Register: accelerator={accelerator}, key={binding.Key}, " +
                    $"modifiers={string.Join('+', binding.Modifiers)}");
        return true;
    }

    public void Unregister()
    {
        AppLog.Info("Hotkey.Unregister: 清除快捷键绑定。");
        Volatile.Write(ref _binding, null);
        ResetHotkeyState();
    }

    private async Task RunHookAsync()
    {
        int restartDelay = HookRestartDelayMilliseconds;
        while (Volatile.Read(ref _disposed) == 0)
        {
            int generationBeforeRun = Volatile.Read(ref _hookEnableGeneration);
            try
            {
                AppLog.Info($"Hotkey.Run: 启动监听 generation={generationBeforeRun}");
                await _hook.RunAsync().ConfigureAwait(false);
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                SetUnavailable("全局快捷键监听已意外停止，正在尝试恢复。");
            }
            catch (Exception e)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                string message = GetPlatformFailureHint($"全局快捷键监听失败: {e.Message}");
                SetUnavailable(message);
                _startup.TrySetResult(false);
                AppLog.Error(message, e);
            }

            ResetHotkeyState();
            // 曾成功启动后再意外退出时快速恢复；连续启动失败则指数退避，避免刷日志。
            if (Volatile.Read(ref _hookEnableGeneration) != generationBeforeRun)
            {
                restartDelay = HookRestartDelayMilliseconds;
            }
            await Task.Delay(restartDelay).ConfigureAwait(false);
            restartDelay = Math.Min(restartDelay * 2, HookRestartMaxDelayMilliseconds);
            if (Volatile.Read(ref _disposed) == 0)
            {
                AppLog.Info("正在重新启动全局快捷键监听。");
            }
        }
    }

    private void OnHookEnabled(object? sender, HookEventArgs e)
    {
        _lastError = null;
        _isAvailable = true;
        Interlocked.Increment(ref _hookEnableGeneration);
        ResetHotkeyState();
        AppLog.Info($"Hotkey.Enabled: generation={_hookEnableGeneration}");
        _startup.TrySetResult(true);
        AvailabilityChanged?.Invoke(true, null);
    }

    private void OnHookDisabled(object? sender, HookEventArgs e)
    {
        AppLog.Info($"Hotkey.Disabled: disposed={Volatile.Read(ref _disposed)}");
        _isAvailable = false;
        ResetHotkeyState();
        if (Volatile.Read(ref _disposed) == 0)
        {
            SetUnavailable("全局快捷键监听已停止。");
        }
    }

    private void OnMousePressed(object? sender, MouseHookEventArgs e)
    {
        Interlocked.Increment(ref _mousePressCount);
        Interlocked.Exchange(ref _lastMousePressAt, Environment.TickCount64);
        GlobalMousePressed?.Invoke(e.Data.X, e.Data.Y);
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        Interlocked.Increment(ref _keyboardEventCount);
        Interlocked.Exchange(ref _lastKeyboardEventAt, Environment.TickCount64);
        RecordMetaEvent(e, pressed: true);
        var binding = Volatile.Read(ref _binding);
        if (!_isAvailable || binding is null || e.Data.KeyCode != binding.Key)
        {
            return;
        }

        ModifierMask? systemMask = ReadSystemModifiers();
        ModifierMask effectiveMask = ResolveModifiers(e.RawEvent.Mask, systemMask, binding);
        if (!HasExactModifiers(effectiveMask, binding.Modifiers))
        {
            // 只记录带修饰键的候选组合，不记录普通文本输入；长按时限制日志频率。
            long mismatchAt = Environment.TickCount64;
            if (((e.RawEvent.Mask | effectiveMask) & KeyboardModifiers) != 0 &&
                mismatchAt - _lastMismatchLogAt >= 1_000)
            {
                _lastMismatchLogAt = mismatchAt;
                AppLog.Info($"Hotkey.ModifierMismatch: key={binding.Key}, mask={e.RawEvent.Mask}, " +
                            $"systemMask={FormatMask(systemMask)}, effectiveMask={effectiveMask}, " +
                            $"required={string.Join('+', binding.Modifiers)}, {GetMetaDiagnosticState()}");
            }
            // 同一主键以普通按键形式再次按下，说明上一轮物理按键已经结束，即使它的
            // KeyUp 曾被漏报也不应继续保留闩锁或抑制下一次普通 KeyUp。
            ResetHotkeyState();
            return;
        }

        // 系统按键自动重复会产生连续 KeyPressed；一次按住只唤醒一次。
        // 若 KeyReleased 被漏掉，则允许过期的闩锁自恢复，避免后续永远无法触发。
        long now = Environment.TickCount64;
        if (Interlocked.CompareExchange(ref _hotkeyDown, 1, 0) != 0)
        {
            long previous = Interlocked.Read(ref _lastHotkeyPressedAt);
            if (now - previous < HotkeyLatchRecoveryMilliseconds)
            {
                if (Interlocked.Exchange(ref _repeatWasLogged, 1) == 0)
                {
                    AppLog.Info($"Hotkey.RepeatIgnored: heldMs={now - previous}, key={binding.Key}");
                }
                SuppressIfSupported(e);
                return;
            }

            AppLog.Warn("检测到快捷键释放事件丢失，已自动恢复按键状态。");
        }

        Interlocked.Exchange(ref _lastHotkeyPressedAt, now);
        Interlocked.Exchange(ref _suppressMainKeyUp, 1);
        SuppressIfSupported(e);
        long sequence = Interlocked.Increment(ref _pressSequence);
        AppLog.Info($"Hotkey.Matched: trigger={sequence}, key={binding.Key}, mask={effectiveMask}, " +
                    $"hookMask={e.RawEvent.Mask}, systemMask={FormatMask(systemMask)}");
        try
        {
            HotkeyPressed?.Invoke(sequence);
        }
        catch (Exception exception)
        {
            AppLog.Error($"Hotkey.DispatchFailed: trigger={sequence}", exception);
        }
    }

    private void OnKeyReleased(object? sender, KeyboardHookEventArgs e)
    {
        Interlocked.Increment(ref _keyboardEventCount);
        Interlocked.Exchange(ref _lastKeyboardEventAt, Environment.TickCount64);
        RecordMetaEvent(e, pressed: false);
        var binding = Volatile.Read(ref _binding);
        if (binding is null)
        {
            return;
        }

        if (e.Data.KeyCode == binding.Key)
        {
            Interlocked.Exchange(ref _hotkeyDown, 0);
            Interlocked.Exchange(ref _lastHotkeyPressedAt, 0);
            // 普通输入同名按键时没有对应的快捷键 KeyDown，不应吞掉它的 KeyUp。
            if (Interlocked.Exchange(ref _suppressMainKeyUp, 0) != 0)
            {
                AppLog.Info($"Hotkey.Released: trigger={Interlocked.Read(ref _pressSequence)}");
                SuppressIfSupported(e);
            }
            Interlocked.Exchange(ref _repeatWasLogged, 0);
            return;
        }

        // 主键 KeyUp 可能在窗口激活切换时丢失；用户松开任意一个所需修饰键时
        // 同样解除按下闩锁。保留 _suppressMainKeyUp，若稍后收到主键 KeyUp 仍应抑制。
        if (IsRequiredModifierKey(binding, e.Data.KeyCode))
        {
            if (Interlocked.Exchange(ref _hotkeyDown, 0) != 0)
            {
                AppLog.Info($"Hotkey.ModifierReleased: trigger={Interlocked.Read(ref _pressSequence)}, key={e.Data.KeyCode}");
            }
            Interlocked.Exchange(ref _lastHotkeyPressedAt, 0);
            Interlocked.Exchange(ref _repeatWasLogged, 0);
        }
    }

    private void ResetHotkeyState()
    {
        Interlocked.Exchange(ref _hotkeyDown, 0);
        Interlocked.Exchange(ref _suppressMainKeyUp, 0);
        Interlocked.Exchange(ref _lastHotkeyPressedAt, 0);
        Interlocked.Exchange(ref _repeatWasLogged, 0);
    }

    private ModifierMask? ReadSystemModifiers()
    {
        try
        {
            return _readSystemModifiers();
        }
        catch (Exception exception)
        {
            long now = Environment.TickCount64;
            if (now - _lastSystemReadFailureAt >= 1_000)
            {
                _lastSystemReadFailureAt = now;
                AppLog.Error("Hotkey.SystemModifiersUnavailable: 保留钩子标记。", exception);
            }
            return null;
        }
    }

    private ModifierMask ResolveModifiers(ModifierMask hookMask, ModifierMask? systemMask, HotkeyBinding binding)
    {
        // GetAsyncKeyState 在桌面隔离/UIPI 等受限情况下也可能全部返回 0。
        // 只有系统正向确认所有必需修饰键都按下时才采用它；单键快捷键保持原规则。
        bool confirmed = binding.Modifiers.Count > 0 && systemMask.HasValue &&
                         binding.Modifiers.All(modifier => (systemMask.Value & modifier) != ModifierMask.None);
        ModifierMask effective = confirmed
            ? (hookMask & ~KeyboardModifiers) | (systemMask!.Value & KeyboardModifiers)
            : hookMask;

        Volatile.Write(ref _lastHookModifiers, (int)(hookMask & KeyboardModifiers));
        Volatile.Write(ref _lastSystemModifiers, systemMask.HasValue ? (int)systemMask.Value : -1);
        Volatile.Write(ref _lastEffectiveModifiers, (int)(effective & KeyboardModifiers));
        Interlocked.Exchange(ref _lastModifierCheckAt, Environment.TickCount64);

        bool recovered = !HasExactModifiers(hookMask, binding.Modifiers) &&
                         HasExactModifiers(effective, binding.Modifiers);
        if (recovered) Interlocked.Increment(ref _modifierRecoveryCount);

        long now = Environment.TickCount64;
        if (systemMask.HasValue && (hookMask & KeyboardModifiers) != (systemMask.Value & KeyboardModifiers) &&
            now - _lastModifierAdjustmentLogAt >= 1_000)
        {
            _lastModifierAdjustmentLogAt = now;
            AppLog.Info($"Hotkey.ModifierState: hookMask={hookMask}, systemMask={systemMask.Value}, " +
                        $"effectiveMask={effective}, systemConfirmed={confirmed}, recovered={recovered}, " +
                        $"required={string.Join('+', binding.Modifiers)}, {GetMetaDiagnosticState()}");
        }
        return effective;
    }

    private void RecordMetaEvent(KeyboardHookEventArgs e, bool pressed)
    {
        if (e.Data.KeyCode is not (KeyCode.VcLeftMeta or KeyCode.VcRightMeta)) return;

        // 长按自动重复不刷屏；不记录普通按键或正文。
        long now = Environment.TickCount64;
        if (pressed && Volatile.Read(ref _lastMetaDown) != 0 && Volatile.Read(ref _lastMetaKey) == (int)e.Data.KeyCode &&
            now - Interlocked.Read(ref _lastMetaEventAt) < 1_000) return;
        Volatile.Write(ref _lastMetaKey, (int)e.Data.KeyCode);
        Volatile.Write(ref _lastMetaDown, pressed ? 1 : 0);
        Interlocked.Exchange(ref _lastMetaEventAt, now);
        AppLog.Info($"Hotkey.Meta{(pressed ? "Pressed" : "Released")}: key={e.Data.KeyCode}, " +
                    $"hookMask={e.RawEvent.Mask}, systemMaskBeforeEvent={FormatMask(ReadSystemModifiers())}");
    }

    private string GetMetaDiagnosticState()
    {
        long changedAt = Interlocked.Read(ref _lastMetaEventAt);
        return $"lastMetaKey={(KeyCode)Volatile.Read(ref _lastMetaKey)}, lastMetaDown={Volatile.Read(ref _lastMetaDown)}, " +
               $"metaEventAgeMs={(changedAt == 0 ? -1 : Environment.TickCount64 - changedAt)}";
    }

    private static string FormatMask(ModifierMask? mask) => mask?.ToString() ?? "unavailable";

    /// <summary>只包含计数和时间，不记录用户输入的按键序列。</summary>
    internal string GetDiagnosticState()
    {
        var binding = Volatile.Read(ref _binding);
        long now = Environment.TickCount64;
        long keyboardAt = Interlocked.Read(ref _lastKeyboardEventAt);
        long mouseAt = Interlocked.Read(ref _lastMousePressAt);
        long modifierAt = Interlocked.Read(ref _lastModifierCheckAt);
        int systemModifiers = Volatile.Read(ref _lastSystemModifiers);
        return $"available={_isAvailable}, running={_hook.IsRunning}, generation={Volatile.Read(ref _hookEnableGeneration)}, " +
               $"key={binding?.Key}, latch={Volatile.Read(ref _hotkeyDown)}, " +
               $"keyboardEvents={Interlocked.Read(ref _keyboardEventCount)}, keyboardAgeMs={(keyboardAt == 0 ? -1 : now - keyboardAt)}, " +
               $"mousePresses={Interlocked.Read(ref _mousePressCount)}, mouseAgeMs={(mouseAt == 0 ? -1 : now - mouseAt)}, " +
               $"triggers={Interlocked.Read(ref _pressSequence)}, modifierRecoveries={Interlocked.Read(ref _modifierRecoveryCount)}, " +
               $"lastHookModifiers={(ModifierMask)Volatile.Read(ref _lastHookModifiers)}, " +
               $"lastSystemModifiers={(systemModifiers < 0 ? "unavailable" : ((ModifierMask)systemModifiers).ToString())}, " +
               $"lastEffectiveModifiers={(ModifierMask)Volatile.Read(ref _lastEffectiveModifiers)}, " +
               $"modifierCheckAgeMs={(modifierAt == 0 ? -1 : now - modifierAt)}, {GetMetaDiagnosticState()}, error={_lastError}";
    }

    private static bool IsRequiredModifierKey(HotkeyBinding binding, KeyCode key)
    {
        return (key is KeyCode.VcLeftControl or KeyCode.VcRightControl &&
                binding.Modifiers.Contains(ModifierMask.Ctrl)) ||
               (key is KeyCode.VcLeftAlt or KeyCode.VcRightAlt &&
                binding.Modifiers.Contains(ModifierMask.Alt)) ||
               (key is KeyCode.VcLeftShift or KeyCode.VcRightShift &&
                binding.Modifiers.Contains(ModifierMask.Shift)) ||
               (key is KeyCode.VcLeftMeta or KeyCode.VcRightMeta &&
                binding.Modifiers.Contains(ModifierMask.Meta));
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
        AppLog.Warn($"Hotkey.Unavailable: {message}");
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
