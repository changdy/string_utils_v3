using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using StrToolkit.Services;
using StrToolkit.Solvers;
using StrToolkit.ViewModels;

internal static class FunctionalChecks
{
    public static void RunAll()
    {
        using var config = new TestConfig();
        TestSolverNames(config);
        TestVisibility(config);
        TestJsonDiff();
        TestManualJsonDiff(config);
    }

    private static void TestSolverNames(TestConfig config)
    {
        var vm = new MainWindowViewModel(config.CreateSettings());
        var original = new TestSolver("test-primary");
        Require(vm.AddSolver(original), "合法名称被拒绝");
        Require(!vm.AddSolver(new TestSolver("test-primary")), "重名处理器未被拒绝");
        Require(!vm.AddSolver(new TestSolver("")), "空名称未被拒绝");
        Require(!vm.AddSolver(new TestSolver(" \t")), "空白名称未被拒绝");
        Require(vm.Solvers.Count == 1 && vm.VisibleSolvers.Count == 1, "拒绝注册后仍改变了列表");
        Require(ReferenceEquals(vm.Solvers[0].Solver, original), "重名注册替换了原处理器");
        vm.SelectSolver(vm.Solvers[0]);
        vm.BodyText = "original-input";
        Require(vm.Execute() == "original-input" && original.Executions == 1, "原处理器无法正常执行");
        Console.WriteLine("PASS 空名/重名处理器被拒绝，原处理器保持可用");
    }

    private static void TestVisibility(TestConfig config)
    {
        const string name = "test-visible";
        var settings = config.CreateSettings();
        var vm = new MainWindowViewModel(settings);
        var solver = new TestSolver(name);
        vm.AddSolver(solver);
        var item = vm.Solvers.Single();
        vm.SelectSolver(item);
        vm.SetSolverHovered(item, true);
        vm.SetSolverVisible(name, false);
        vm.SetSolverVisible(name, false);
        Require(!item.IsVisible && !item.IsSelected && !item.IsPulledOut, "隐藏后仍可见、选中或悬停抽出");
        Require(vm.VisibleSolvers.Count == 0, "隐藏后工具栏仍占位");
        Require(vm.Execute() is null && solver.Executions == 0, "隐藏后仍可执行");
        Require(settings.Settings.SkipList.Count(x => x == name) == 1, "重复隐藏产生了重复配置");
        AppSettings saved = config.ReadSaved();
        Require(saved.SkipList.Contains(name), "隐藏配置未写入文件");
        var reloaded = new MainWindowViewModel(config.CreateSettings(saved));
        reloaded.AddSolver(new TestSolver(name));
        Require(!reloaded.Solvers.Single().IsVisible && reloaded.VisibleSolvers.Count == 0, "重建后未恢复隐藏状态");

        // 兼容旧配置中可能残留的重复项；显示时应全部清除。
        settings.Settings.SkipList.Add(name);
        vm.SetSolverVisible(name, true);
        Require(item.IsVisible && vm.VisibleSolvers.Single() == item, "恢复显示后工具栏未更新");
        saved = config.ReadSaved();
        Require(!saved.SkipList.Contains(name), "恢复显示后仍有隐藏配置残留");
        reloaded = new MainWindowViewModel(config.CreateSettings(saved));
        reloaded.AddSolver(new TestSolver(name));
        Require(reloaded.Solvers.Single().IsVisible, "重建后未恢复显示状态");
        vm.SelectSolver(item);
        vm.BodyText = "visible-again";
        Require(vm.Execute() == "visible-again" && solver.Executions == 1, "恢复显示后无法执行");
        Console.WriteLine("PASS 显隐、工具栏、选中/执行状态及配置持久化保持一致");
    }

    private static void TestJsonDiff()
    {
        var opened = new List<(string Left, string Right)>();
        var solver = new JsonDiffSolver((left, right) => opened.Add((left, right)));
        const string valid = "[{\"id\":1,\"name\":\"前\"},{\"id\":2,\"name\":\"后\"}]";
        Require(solver.Check(valid, new[] { valid }, true) > 0, "合法双对象未被推荐");
        solver.Transfer(valid, new[] { valid }, true);
        Require(opened.Count == 1, "合法双对象没有触发对比");
        using (var left = JsonDocument.Parse(opened[0].Left))
        using (var right = JsonDocument.Parse(opened[0].Right))
        {
            Require(left.RootElement.GetProperty("id").GetInt32() == 1 &&
                    right.RootElement.GetProperty("id").GetInt32() == 2, "传给对比入口的对象不正确");
        }

        foreach (string invalid in new[]
                 {
                     "{}", "[]", "[{}]", "[{},{},{}]", "[{\"id\":1,\"name\":\"a\"},2]",
                     "[2,{}]", "[{},null]", "[[],{}]", "not-json"
                 })
        {
            Require(solver.Check(invalid, new[] { invalid }, true) == 0, "非法输入被自动推荐");
            bool rejected = false;
            try { solver.Transfer(invalid, new[] { invalid }, true); }
            catch (ArgumentException) { rejected = true; }
            catch (JsonException) { rejected = true; }
            Require(rejected && opened.Count == 1, "非法输入仍然触发了外部对比");
        }

        // 推荐规则要求首个对象有多个字段，但手动执行仍应支持单字段或空对象。
        const string manual = "[{\"id\":1},{}]";
        Require(solver.Check(manual, new[] { manual }, true) == 0, "推荐规则发生意外变化");
        solver.Transfer(manual, new[] { manual }, true);
        Require(opened.Count == 2, "合法的手动对比被推荐规则错误阻止");
        Console.WriteLine("PASS JSON Diff 接受双对象、拒绝非法输入，并保留手动执行范围");
    }

    private static void TestManualJsonDiff(TestConfig config)
    {
        int opened = 0;
        var vm = new MainWindowViewModel(config.CreateSettings());
        vm.AddSolver(new JsonDiffSolver((_, _) => opened++));
        vm.SelectSolver(vm.Solvers.Single());
        const string invalid = "[{\"id\":1,\"name\":\"a\"},42]";
        vm.BodyText = invalid;
        Require(vm.Execute() is null, "手动执行非法输入仍返回了可复制结果");
        Require(opened == 0 && vm.BodyText == invalid, "手动执行非法输入改变了正文或启动了对比");
        vm.BodyText = "[{},{}]";
        Require(vm.Execute() is not null && opened == 1, "手动执行合法双对象失败");
        Console.WriteLine("PASS 手动点击 JSON Diff 时同样校验输入，拒绝后保留正文");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TestSolver(string name) : ISolver
    {
        public string Name => name;
        public string Describe => name;
        public string? NextStep => null;
        public int Executions { get; private set; }
        public int Check(string logs, string[] arr, bool jsonFlag) => 1;
        public string Transfer(string logs, string[] arr, bool jsonFlag)
        {
            Executions++;
            return logs;
        }
    }

    private sealed class TestConfig : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "StrToolkit.Tests-" + Guid.NewGuid().ToString("N"));
        private string FilePath => Path.Combine(_directory, "settings.json");

        public TestConfig() => Directory.CreateDirectory(_directory);

        public SettingsService CreateSettings(AppSettings? settings = null)
        {
            // 绕过会读取真实配置、安装用户脚本的默认构造函数；仍调用实际的 Save 实现。
            var service = (SettingsService)RuntimeHelpers.GetUninitializedObject(typeof(SettingsService));
            typeof(SettingsService).GetField("_filePath", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(service, FilePath);
            typeof(SettingsService).GetField("<Settings>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(service, settings ?? new AppSettings());
            return service;
        }

        public AppSettings ReadSaved() => JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath))!;

        public void Dispose()
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
            Directory.Delete(_directory);
        }
    }
}
