using System;
using System.Text.Json;

namespace StrToolkit.Solvers;

/// <summary>JSON 预览：交给 JsonPreviewService 打开本地 jsonhero / jsoncrack。</summary>
public sealed class JsonViewSolver : ISolver
{
    private readonly Action<string> _openPreview;

    public JsonViewSolver(Action<string> openPreview)
    {
        _openPreview = openPreview;
    }

    public string Name => "json-view";
    public string Describe => "使用json hero预览";
    public string? NextStep => null;

    public int Check(string logs, string[] arr, bool jsonFlag)
    {
        if (!jsonFlag)
        {
            return 0;
        }
        try
        {
            using var _ = JsonDocument.Parse(logs);
            return 100;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    public string Transfer(string logs, string[] arr, bool jsonFlag)
    {
        _openPreview(logs);
        return logs;
    }
}
