using System;
using System.IO;
using Jint;
using Jint.Native;

namespace StrToolkit.Solvers;

/// <summary>
/// 通过 Jint 加载 Electron 版格式的 JS 用户脚本（ES Module，导出 solver 对象）。
/// 注意：仅支持纯 JS 逻辑，不支持 Node API / require / CryptoJS 等注入（见 DIFFERENCES.md）。
/// </summary>
public sealed class JsUserScriptSolver : ISolver
{
    private readonly Engine _engine;
    private readonly JsValue _solver;
    private readonly JsValue? _checkFn;
    private readonly JsValue? _transferFn;

    public string Name { get; }
    public string Describe { get; }
    public string? NextStep { get; }
    public bool IsUserScript => true;
    public string? IconBasePath { get; }

    private JsUserScriptSolver(Engine engine, JsValue solver, string scriptPath)
    {
        _engine = engine;
        _solver = solver;
        Name = solver.Get("name").IsString() ? solver.Get("name").AsString() : Path.GetFileNameWithoutExtension(scriptPath);
        Describe = solver.Get("describe").IsString() ? solver.Get("describe").AsString() : Name;
        NextStep = solver.Get("nextStep").IsString() ? solver.Get("nextStep").AsString() : null;
        IconBasePath = Path.Combine(Path.GetDirectoryName(scriptPath) ?? "", Path.GetFileNameWithoutExtension(scriptPath));
        var check = solver.Get("check");
        _checkFn = check.IsUndefined() ? null : check;
        var transfer = solver.Get("transfer");
        _transferFn = transfer.IsUndefined() ? null : transfer;
    }

    public static JsUserScriptSolver Load(string scriptPath)
    {
        var scriptDir = Path.GetDirectoryName(Path.GetFullPath(scriptPath)) ?? ".";
        var engine = new Engine(options =>
        {
            options.EnableModules(scriptDir);
            options.TimeoutInterval(TimeSpan.FromSeconds(10));
            options.LimitRecursion(256);
        });
        var ns = engine.Modules.Import("./" + Path.GetFileName(scriptPath));
        var solver = ns.Get("solver");
        if (solver.IsUndefined() || solver.IsNull())
        {
            throw new InvalidOperationException($"脚本 {scriptPath} 未导出 solver 对象");
        }
        return new JsUserScriptSolver(engine, solver, scriptPath);
    }

    public int Check(string logs, string[] arr, bool jsonFlag)
    {
        if (_checkFn is null)
        {
            return 0;
        }
        try
        {
            lock (_engine)
            {
                var result = _engine.Invoke(_checkFn, _solver, new object[] { logs, arr, jsonFlag });
                return result.IsNumber() ? (int)result.AsNumber() : 0;
            }
        }
        catch (Exception e)
        {
            // 附加脚本身份后抛出，由 ViewModel 统一写入 VS 调试输出
            throw new InvalidOperationException(
                $"用户脚本 check 异常: name={Name}, describe={Describe}", e);
        }
    }

    public string Transfer(string logs, string[] arr, bool jsonFlag)
    {
        if (_transferFn is null)
        {
            return logs;
        }
        try
        {
            lock (_engine)
            {
                var result = _engine.Invoke(_transferFn, _solver, new object[] { logs, arr, jsonFlag });
                return result.IsString() ? result.AsString() : result.ToString();
            }
        }
        catch (Exception e)
        {
            throw new InvalidOperationException(
                $"用户脚本 transfer 异常: name={Name}, describe={Describe}", e);
        }
    }
}
