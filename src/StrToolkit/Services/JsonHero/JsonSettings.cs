using System.Text.Json;

namespace StrToolkit.Services.JsonHero;

/// <summary>统一 JSON 文档深度，并为 HTTP 请求和响应的包装对象预留空间。</summary>
internal static class JsonSettings
{
    public const int MaxDocumentDepth = 512;

    public static readonly JsonDocumentOptions DocumentOptions = new()
    {
        MaxDepth = MaxDocumentDepth
    };

    public static readonly JsonSerializerOptions DocumentSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = MaxDocumentDepth
    };

    // 响应格式为 { success, data: { json, ... } }，比原始文档多两层。
    public static readonly JsonSerializerOptions ResponseSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = MaxDocumentDepth + 2
    };
}
