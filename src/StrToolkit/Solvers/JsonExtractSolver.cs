using System.Linq;
using System.Text.Json;

namespace StrToolkit.Solvers;

public sealed class JsonExtractSolver : ISolver
{
    public string Name => "json-extract";
    public string Describe => "JSON中抽取数据,优先id";
    public string? NextStep => "sort-distinct";

    public int Check(string logs, string[] arr, bool jsonFlag)
    {
        if (!jsonFlag || !logs.StartsWith('['))
        {
            return 0;
        }
        try
        {
            using var doc = JsonDocument.Parse(logs);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return 0;
            }
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    return 0;
                }
                if (item.EnumerateObject().Count() != 1)
                {
                    return 0;
                }
            }
            return 300;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    public string Transfer(string logs, string[] arr, bool jsonFlag)
    {
        using var doc = JsonDocument.Parse(logs);
        var results = doc.RootElement.EnumerateArray().Select(item =>
        {
            if (item.TryGetProperty("id", out var idValue))
            {
                return ValueToString(idValue);
            }
            var first = item.EnumerateObject().FirstOrDefault();
            return ValueToString(first.Value);
        });
        return string.Join("\n", results);
    }

    /// <summary>使用原始文本保留大整数精度（对应 Electron 版的 json-bigint）。</summary>
    private static string ValueToString(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.GetRawText();
}
