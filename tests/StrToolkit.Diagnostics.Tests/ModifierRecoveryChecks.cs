using System.Reflection;
using SharpHook;
using SharpHook.Native;
using StrToolkit.Services;

internal static class ModifierRecoveryChecks
{
    private const ModifierMask CtrlAlt = ModifierMask.LeftCtrl | ModifierMask.LeftAlt;

    public static void RunAll()
    {
        TestMissedWinRelease();
        TestRealExtraModifiers();
        TestUnavailableSystemState();
        TestConfiguredWinAndSingleKey();
    }

    private static void TestMissedWinRelease()
    {
        ModifierMask system = ModifierMask.None;
        using var input = new Input("Ctrl+Alt+D", () => system);
        input.Press(KeyCode.VcLeftMeta, ModifierMask.LeftMeta);
        system = ModifierMask.LeftMeta;
        Require(!input.Press(KeyCode.VcD, ModifierMask.LeftMeta).SuppressEvent && input.Triggers == 0,
            "Win+D 被错误识别为 Ctrl+Alt+D");
        input.Release(KeyCode.VcD, ModifierMask.LeftMeta);

        // 故意遗漏 Win 的释放事件：复现故障日志中的 Ctrl+Alt+Win 残留标记。
        system = CtrlAlt;
        var recovered = input.Press(KeyCode.VcD, CtrlAlt | ModifierMask.LeftMeta | ModifierMask.CapsLock);
        Require(input.Triggers == 1 && recovered.SuppressEvent, "Win 已松开时未恢复快捷键");
        Require((recovered.RawEvent.Mask & ModifierMask.LeftMeta) != 0, "修复篡改了原始事件标记");
        input.Press(KeyCode.VcD, CtrlAlt | ModifierMask.LeftMeta);
        Require(input.Triggers == 1, "恢复后长按重复触发");
        Require(input.Release(KeyCode.VcD, CtrlAlt | ModifierMask.LeftMeta).SuppressEvent,
            "恢复后没有抑制对应的主键释放");
        input.Press(KeyCode.VcD, CtrlAlt | ModifierMask.LeftMeta);
        Require(input.Triggers == 2, "释放后再次按下没有恢复");
        input.Release(KeyCode.VcD, CtrlAlt);

        // Win 真正的释放事件随后到达，下一次仍可正常触发。
        input.Release(KeyCode.VcLeftMeta, CtrlAlt);
        input.Press(KeyCode.VcD, CtrlAlt);
        Require(input.Triggers == 3, "Win 释放到达后正常快捷键失效");
        Console.WriteLine("PASS Win+D 后遗漏 Win 释放时自动恢复，重复抑制及再次唤醒正常");
    }

    private static void TestRealExtraModifiers()
    {
        ModifierMask system = CtrlAlt | ModifierMask.LeftMeta;
        using var input = new Input("Ctrl+Alt+D", () => system);
        foreach (ModifierMask extra in new[] { ModifierMask.LeftMeta, ModifierMask.RightMeta, ModifierMask.LeftShift })
        {
            system = CtrlAlt | extra;
            Require(!input.Press(KeyCode.VcD, CtrlAlt | extra).SuppressEvent, "真正的额外修饰键被吞掉");
            input.Release(KeyCode.VcD, system);
            // 即使钩子漏报了额外修饰键，系统确认它仍按下时也不应触发。
            Require(!input.Press(KeyCode.VcD, CtrlAlt).SuppressEvent, "钩子漏报额外修饰键导致误触发");
            input.Release(KeyCode.VcD, system);
        }
        Require(input.Triggers == 0, "真正按住 Win/Shift 时错误触发快捷键");
        system = CtrlAlt;
        input.Press(KeyCode.VcD, CtrlAlt | ModifierMask.LeftShift);
        Require(input.Triggers == 1, "已松开的 Shift 残留标记没有恢复");
        Console.WriteLine("PASS 真正按住左右 Win 或 Shift 时仍精确拒绝，已释放的残留可恢复");
    }

    private static void TestUnavailableSystemState()
    {
        ModifierMask? system = null;
        bool fail = false;
        using var input = new Input("Ctrl+Alt+D", () => fail ? throw new InvalidOperationException("test-provider-failure") : system);
        foreach (ModifierMask? unconfirmed in new ModifierMask?[] { null, ModifierMask.None, ModifierMask.LeftCtrl })
        {
            system = unconfirmed;
            Require(!input.Press(KeyCode.VcD, CtrlAlt | ModifierMask.LeftMeta).SuppressEvent,
                "系统无法确认全部必要修饰键时错误清除了 Win");
            input.Release(KeyCode.VcD, CtrlAlt);
            input.Press(KeyCode.VcD, CtrlAlt);
            input.Release(KeyCode.VcD, CtrlAlt);
        }
        Require(input.Triggers == 3, "系统不可用或未能确认时改变了原有匹配行为");
        fail = true;
        input.Press(KeyCode.VcD, CtrlAlt);
        Require(input.Triggers == 4, "系统读取异常破坏了快捷键监听");
        input.Release(KeyCode.VcD, CtrlAlt);
        fail = false;
        system = ModifierMask.RightCtrl | ModifierMask.RightAlt;
        input.Press(KeyCode.VcD, CtrlAlt | ModifierMask.RightMeta);
        Require(input.Triggers == 5, "右侧 Ctrl/Alt 不能确认或修复残留 Win");
        Console.WriteLine("PASS 系统状态不可用、全零、部分缺失或读取异常时安全回退，左右修饰键均可确认");
    }

    private static void TestConfiguredWinAndSingleKey()
    {
        ModifierMask system = ModifierMask.RightMeta;
        using (var input = new Input("Meta+D", () => system))
        {
            input.Press(KeyCode.VcD, ModifierMask.RightMeta | ModifierMask.LeftAlt);
            Require(input.Triggers == 1, "配置要求的 Win 键被移除或残留 Alt 没有恢复");
            input.Release(KeyCode.VcD, ModifierMask.RightMeta);
            system = ModifierMask.RightMeta | ModifierMask.LeftCtrl;
            input.Press(KeyCode.VcD, ModifierMask.RightMeta);
            Require(input.Triggers == 1, "Win 快捷键忽略了实际额外按住的 Ctrl");
        }
        using (var input = new Input("D", () => ModifierMask.None))
        {
            Require(!input.Press(KeyCode.VcD, ModifierMask.LeftMeta).SuppressEvent && input.Triggers == 0,
                "系统全零采样使单键快捷键吞掉 Win+D");
            input.Release(KeyCode.VcD, ModifierMask.LeftMeta);
            input.Press(KeyCode.VcD, ModifierMask.None);
            Require(input.Triggers == 1, "单键快捷键原有行为发生变化");
        }
        Console.WriteLine("PASS 自定义 Win 快捷键保留必要修饰键，单键快捷键保持原规则");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Input : IDisposable
    {
        private readonly HotkeyService _service;
        public int Triggers { get; private set; }

        public Input(string accelerator, Func<ModifierMask?> readSystemModifiers)
        {
            _service = new HotkeyService(readSystemModifiers);
            Require(_service.Register(accelerator), "测试快捷键无法解析");
            typeof(HotkeyService).GetField("_isAvailable", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(_service, true);
            _service.HotkeyPressed += _ => Triggers++;
        }

        public KeyboardHookEventArgs Press(KeyCode key, ModifierMask mask) => Send("OnKeyPressed", key, mask);
        public KeyboardHookEventArgs Release(KeyCode key, ModifierMask mask) => Send("OnKeyReleased", key, mask);

        private KeyboardHookEventArgs Send(string method, KeyCode key, ModifierMask mask)
        {
            var args = new KeyboardHookEventArgs(new UioHookEvent
            {
                Mask = mask,
                Keyboard = new KeyboardEventData { KeyCode = key }
            });
            typeof(HotkeyService).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(_service, new object?[] { null, args });
            return args;
        }

        public void Dispose() => _service.Dispose();
    }
}
