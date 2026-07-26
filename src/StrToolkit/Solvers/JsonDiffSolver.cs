using System;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace StrToolkit.Solvers;

/// <summary>JSON Diff：输入必须是包含两个对象的数组，用 VSCode 做可视化对比。</summary>
public sealed class JsonDiffSolver : ISolver
{
    private static readonly JsonSerializerOptions DiffJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly Action<string, string> _openDiff;

    public JsonDiffSolver(Action<string, string> openDiff)
    {
        _openDiff = openDiff;
    }

    public string Name => "json-diff";
    public string Describe => "使用VSCode对比JSON差异";
    public string? NextStep => null;

    public int Check(string logs, string[] arr, bool jsonFlag)
    {
        if (!jsonFlag)
        {
            return 0;
        }
        try
        {
            using var doc = JsonDocument.Parse(logs);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 2)
            {
                return 0;
            }
            var first = root[0];
            if (first.ValueKind != JsonValueKind.Object)
            {
                return 0;
            }
            if (first.EnumerateObject().Count() <= 1)
            {
                return 0;
            }
            return 200;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    public string Transfer(string logs, string[] arr, bool jsonFlag)
    {
        using var doc = JsonDocument.Parse(logs);
        var obj1 = JsonSerializer.Serialize(doc.RootElement[0], DiffJsonOptions);
        var obj2 = JsonSerializer.Serialize(doc.RootElement[1], DiffJsonOptions);
        _openDiff(obj1, obj2);
        return "正在打开差异对比... (如未找到VSCode将无法打开)";
    }
}
