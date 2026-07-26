using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace StrToolkit.Services.JsonHero;

/// <summary>JSON Hero HTTP API 的路由、请求校验与响应契约。</summary>
internal sealed class ApiEndpoints
{
    private readonly DocumentStore _documentStore;
    private readonly RemoteFetcher _remoteFetcher;

    public ApiEndpoints(DocumentStore documentStore, RemoteFetcher remoteFetcher)
    {
        _documentStore = documentStore;
        _remoteFetcher = remoteFetcher;
    }

    public static void AddCors(IServiceCollection services)
    {
        services.AddCors(options => options.AddDefaultPolicy(BuildCorsPolicy));
    }

    public void Map(WebApplication app)
    {
        app.MapGet("/api/health", () => Success(new { status = "ok", documents = _documentStore.Count }));

        app.MapPost("/api/create/file", async (HttpContext context) =>
        {
            var request = await ReadJsonBodyAsync<CreateFileRequest>(context);
            if (request is null || string.IsNullOrWhiteSpace(request.Filename) || string.IsNullOrWhiteSpace(request.RawJson))
            {
                return Error(StatusCodes.Status400BadRequest, "BAD_REQUEST", "Missing required fields");
            }

            try
            {
                var document = _documentStore.CreateRaw(request.Filename, request.RawJson, null, false);
                return Success(new { id = document.Id, redirect = $"/j/{document.Id}" });
            }
            catch (JsonException e)
            {
                return Error(StatusCodes.Status400BadRequest, "INVALID_JSON", $"Failed to parse JSON: {e.Message}");
            }
        });

        app.MapPost("/api/create/url", async (HttpContext context) =>
        {
            var request = await ReadJsonBodyAsync<CreateUrlRequest>(context);
            if (request is null || string.IsNullOrWhiteSpace(request.JsonUrl))
            {
                return Error(StatusCodes.Status400BadRequest, "BAD_REQUEST", "jsonUrl is required");
            }

            try
            {
                var document = await CreateFromUrlOrTextAsync(request.JsonUrl, request.Title, null, false, false);
                return Success(new { id = document.Id, redirect = $"/j/{document.Id}" });
            }
            catch (Exception e) when (e is ArgumentException or JsonException or HttpRequestException or System.Xml.XmlException)
            {
                return Error(StatusCodes.Status400BadRequest, "CREATE_FAILED", e.Message);
            }
        });

        app.MapGet("/api/create", async (HttpContext context) =>
        {
            string? url = context.Request.Query["url"].FirstOrDefault();
            string? encodedJson = context.Request.Query["j"].FirstOrDefault();
            string? title = context.Request.Query["title"].FirstOrDefault();
            string? ttlText = context.Request.Query["ttl"].FirstOrDefault();
            bool readOnly = ParseBoolean(context.Request.Query["readonly"].FirstOrDefault());
            // 保留上游 API 的历史拼写，前端仍使用 injest。
            bool ingest = ParseBoolean(context.Request.Query["injest"].FirstOrDefault());

            if (url is null && encodedJson is null)
            {
                return Results.Redirect("/");
            }

            if (!TryParseTtl(ttlText, out TimeSpan? ttl, out string? ttlError))
            {
                return Error(StatusCodes.Status400BadRequest, "BAD_REQUEST", ttlError!);
            }

            try
            {
                StoredDocument document;
                if (url is not null)
                {
                    document = await CreateFromUrlOrTextAsync(url, title, ttl, readOnly, ingest);
                }
                else
                {
                    string normalized = encodedJson!.Replace(' ', '+');
                    string contents = Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
                    document = _documentStore.CreateRaw(title ?? "Untitled", contents, ttl, readOnly);
                }
                return Success(new { id = document.Id, redirect = $"/j/{document.Id}" });
            }
            catch (Exception e) when (e is FormatException or ArgumentException or JsonException or HttpRequestException or System.Xml.XmlException)
            {
                return Error(StatusCodes.Status400BadRequest, "CREATE_FAILED", e.Message);
            }
        });

        app.MapGet("/api/docs/{id}/raw", async (HttpContext context, string id) =>
        {
            if (!_documentStore.TryGet(id, out var document))
            {
                return Error(StatusCodes.Status404NotFound, "NOT_FOUND", $"Document {id} not found");
            }

            try
            {
                string raw = document.Type == DocumentType.Raw
                    ? document.Contents!
                    : await _remoteFetcher.FetchTextAsync(document.Url!, context.RequestAborted);
                using var _ = JsonDocument.Parse(raw);
                return Results.Text(raw, "application/json", Encoding.UTF8);
            }
            catch (Exception e) when (e is JsonException or HttpRequestException)
            {
                return Error(StatusCodes.Status502BadGateway, "FETCH_ERROR", e.Message);
            }
        });

        app.MapGet("/api/docs/{id}", async (HttpContext context, string id) =>
        {
            if (!_documentStore.TryGet(id, out var document))
            {
                return Error(StatusCodes.Status404NotFound, "NOT_FOUND", $"Document {id} not found");
            }

            try
            {
                string raw = document.Type == DocumentType.Raw
                    ? document.Contents!
                    : await _remoteFetcher.FetchTextAsync(document.Url!, context.RequestAborted);
                JsonElement json = JsonSerializer.Deserialize<JsonElement>(raw);
                string? path = NormalizePath(context.Request.Query["path"].FirstOrDefault());
                bool minimal = ParseBoolean(context.Request.Query["minimal"].FirstOrDefault());
                return Success(new { doc = ToApiDocument(document), json, path, minimal });
            }
            catch (Exception e) when (e is JsonException or HttpRequestException)
            {
                return Error(StatusCodes.Status502BadGateway, "FETCH_ERROR", e.Message);
            }
        });

        app.MapPost("/api/docs/{id}/update", async (HttpContext context, string id) =>
        {
            var request = await ReadJsonBodyAsync<UpdateTitleRequest>(context);
            if (request is null || string.IsNullOrWhiteSpace(request.Title))
            {
                return Error(StatusCodes.Status400BadRequest, "BAD_REQUEST", "expected title");
            }

            DocumentMutationResult result = _documentStore.TryUpdateTitle(id, request.Title, out var updated);
            return result switch
            {
                DocumentMutationResult.Success => Success(ToApiDocument(updated)),
                DocumentMutationResult.ReadOnly => Error(
                    StatusCodes.Status403Forbidden,
                    "READ_ONLY",
                    "Document is read-only"),
                _ => Error(StatusCodes.Status404NotFound, "NOT_FOUND", "No document with that slug")
            };
        });

        app.MapDelete("/api/docs/{id}", (string id) =>
        {
            DocumentMutationResult result = _documentStore.TryDelete(id);
            return result switch
            {
                DocumentMutationResult.Success => Success(new { redirect = "/" }),
                DocumentMutationResult.ReadOnly => Error(
                    StatusCodes.Status403Forbidden,
                    "READ_ONLY",
                    "Document is read-only"),
                _ => Error(StatusCodes.Status404NotFound, "NOT_FOUND", "Document not found")
            };
        });

        app.MapMethods("/api/public/create", new[] { HttpMethods.Options }, (HttpContext context) =>
        {
            SetPublicCorsHeaders(context.Response);
            return Results.NoContent();
        });

        app.MapPost("/api/public/create", async (HttpContext context) =>
        {
            SetPublicCorsHeaders(context.Response);
            var request = await ReadJsonBodyAsync<PublicCreateRequest>(context);
            if (request is null || string.IsNullOrWhiteSpace(request.Title) ||
                request.Content.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                return Error(StatusCodes.Status400BadRequest, "BAD_REQUEST", "Missing title or content");
            }
            if (!TryParseTtl(request.Ttl?.ToString(), out TimeSpan? ttl, out string? ttlError))
            {
                return Error(StatusCodes.Status400BadRequest, "BAD_REQUEST", ttlError!);
            }

            var document = _documentStore.CreateRaw(
                request.Title,
                request.Content.GetRawText(),
                ttl,
                request.ReadOnly ?? false);
            return Success(new { id = document.Id, title = document.Title, location = $"/j/{document.Id}" });
        });

        app.MapGet("/api/preview/{**encodedUrl}", async (HttpContext context, string encodedUrl) =>
        {
            try
            {
                string uri = Uri.UnescapeDataString(encodedUrl);
                object preview = await _remoteFetcher.CreatePreviewAsync(uri, context.RequestAborted);
                return Success(preview);
            }
            catch
            {
                return Error(StatusCodes.Status500InternalServerError, "PREVIEW_ERROR", "Unable to preview this URL");
            }
        });
    }

    private async Task<StoredDocument> CreateFromUrlOrTextAsync(
        string urlOrText,
        string? title,
        TimeSpan? ttl,
        bool readOnly,
        bool ingest)
    {
        if (_remoteFetcher.TryNormalizeUri(urlOrText, out var uri))
        {
            if (ingest)
            {
                return _documentStore.CreateRaw(
                    title ?? uri.AbsoluteUri,
                    await _remoteFetcher.FetchTextAsync(uri.AbsoluteUri),
                    ttl,
                    readOnly);
            }
            return _documentStore.CreateUrl(title ?? uri.AbsoluteUri, uri.AbsoluteUri, ttl, readOnly);
        }

        try
        {
            return _documentStore.CreateRaw(title ?? "Untitled", urlOrText, ttl, readOnly);
        }
        catch (JsonException) when (LooksLikeXml(urlOrText))
        {
            string json = ConvertXmlToJson(urlOrText);
            return _documentStore.CreateRaw(title ?? "Untitled", json, ttl, readOnly);
        }
        catch (JsonException)
        {
            throw new ArgumentException("Value is neither a valid URL, JSON document, nor XML document");
        }
    }

    private static object ToApiDocument(StoredDocument document) => document.Type == DocumentType.Raw
        ? new
        {
            id = document.Id,
            title = document.Title,
            readOnly = document.ReadOnly,
            type = "raw",
            contents = document.Contents
        }
        : new
        {
            id = document.Id,
            title = document.Title,
            readOnly = document.ReadOnly,
            type = "url",
            url = document.Url
        };

    private static async Task<T?> ReadJsonBodyAsync<T>(HttpContext context)
    {
        try
        {
            return await context.Request.ReadFromJsonAsync<T>(new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static IResult Success(object data) => Results.Json(new { success = true, data });

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { success = false, error = new { code, message } }, statusCode: status);

    private static bool TryParseTtl(string? value, out TimeSpan? ttl, out string? error)
    {
        ttl = null;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }
        if (!int.TryParse(value, out int seconds))
        {
            error = "ttl must be a number";
            return false;
        }
        if (seconds < 60)
        {
            error = "ttl must be at least 60 seconds";
            return false;
        }
        ttl = TimeSpan.FromSeconds(seconds);
        return true;
    }

    private static bool ParseBoolean(string? value) =>
        bool.TryParse(value, out bool result) && result;

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        return path.StartsWith("$.", StringComparison.Ordinal) ? path : $"$.{path}";
    }

    private static bool LooksLikeXml(string value) => value.TrimStart().StartsWith('<');

    private static string ConvertXmlToJson(string xml)
    {
        XDocument document = XDocument.Parse(xml);
        if (document.Root is null)
        {
            throw new System.Xml.XmlException("Invalid XML");
        }
        object converted = new Dictionary<string, object?>
        {
            [document.Root.Name.LocalName] = ConvertXmlElement(document.Root)
        };
        return JsonSerializer.Serialize(converted);
    }

    private static object? ConvertXmlElement(XElement element)
    {
        var children = element.Elements().ToList();
        var attributes = element.Attributes().ToDictionary(item => item.Name.LocalName, item => (object?)item.Value);
        if (children.Count == 0 && attributes.Count == 0)
        {
            return element.Value;
        }

        var result = new Dictionary<string, object?>();
        if (attributes.Count > 0)
        {
            result["$attributes"] = attributes;
        }
        foreach (var group in children.GroupBy(item => item.Name.LocalName))
        {
            var values = group.Select(ConvertXmlElement).ToList();
            result[group.Key] = values.Count == 1 ? values[0] : values;
        }
        if (!string.IsNullOrWhiteSpace(element.Value) && children.Count == 0)
        {
            result["$value"] = element.Value;
        }
        return result;
    }

    private static void BuildCorsPolicy(CorsPolicyBuilder policy)
    {
        policy.SetIsOriginAllowed(origin =>
            Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.IsLoopback)
            .AllowAnyHeader()
            .AllowAnyMethod();
    }

    private static void SetPublicCorsHeaders(HttpResponse response)
    {
        response.Headers.AccessControlAllowOrigin = "*";
        response.Headers.AccessControlAllowMethods = "POST";
        response.Headers.AccessControlAllowHeaders = "Content-Type";
        response.Headers.AccessControlMaxAge = "86400";
    }

    private sealed record CreateFileRequest(
        [property: JsonPropertyName("filename")] string Filename,
        [property: JsonPropertyName("rawJson")] string RawJson);

    private sealed record CreateUrlRequest(
        [property: JsonPropertyName("jsonUrl")] string JsonUrl,
        [property: JsonPropertyName("title")] string? Title);

    private sealed record UpdateTitleRequest(
        [property: JsonPropertyName("title")] string Title);

    private sealed class PublicCreateRequest
    {
        [JsonPropertyName("title")]
        public string Title { get; init; } = "";

        [JsonPropertyName("content")]
        public JsonElement Content { get; init; }

        [JsonPropertyName("ttl")]
        public int? Ttl { get; init; }

        [JsonPropertyName("readOnly")]
        public bool? ReadOnly { get; init; }
    }
}
