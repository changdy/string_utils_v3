using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace StrToolkit.Services;

/// <summary>
/// In-process JSON Hero API and static frontend host. Documents live only in memory
/// and disappear when StrToolkit exits.
/// </summary>
public sealed class JsonHeroService
{
    public const int PortStart = 13001;
    public const int PortEnd = 13101;

    private const int MaxDocuments = 500;
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(24);
    private static readonly char[] IdAlphabet =
        "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz".ToCharArray();

    private static readonly HttpClient RemoteHttp = CreateRemoteHttpClient();
    private static readonly HttpClient LoopbackHttp = new() { Timeout = TimeSpan.FromSeconds(3) };

    private readonly ConcurrentDictionary<string, StoredDocument> _documents = new();
    private WebApplication? _app;
    private string? _frontendDirectory;

    public int Port { get; private set; } = PortStart;
    public string? BaseUrl { get; private set; }
    public bool IsRunning => _app is not null && BaseUrl is not null;

    /// <summary>Directory copied beside the application during build/publish.</summary>
    public static string FrontendDir => Path.Combine(AppContext.BaseDirectory, "jsonhero-frontend");

    public async Task StartAsync()
    {
        if (IsRunning)
        {
            return;
        }

        _frontendDirectory = FindFrontendDirectory();

        for (int port = PortStart; port <= PortEnd; port++)
        {
            WebApplication? app = null;
            try
            {
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions
                {
                    Args = Array.Empty<string>(),
                    ContentRootPath = AppContext.BaseDirectory
                });
                builder.Logging.ClearProviders();
                builder.Services.AddCors(options => options.AddDefaultPolicy(BuildCorsPolicy));
                builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));

                app = builder.Build();
                app.UseCors();
                ConfigureRoutes(app);
                ConfigureFrontend(app, _frontendDirectory);

                await app.StartAsync();
                _app = app;
                Port = port;
                BaseUrl = $"http://127.0.0.1:{port}";

                string frontendStatus = _frontendDirectory is null
                    ? "frontend assets not found; API-only mode"
                    : $"frontend={_frontendDirectory}";
                Console.WriteLine($"[jsonhero] Server running at {BaseUrl} ({frontendStatus})");
                return;
            }
            catch (Exception e) when (port < PortEnd)
            {
                if (app is not null)
                {
                    await app.DisposeAsync();
                }
                Console.Error.WriteLine($"[jsonhero] Port {port} unavailable: {e.Message}");
            }
        }

        Console.Error.WriteLine($"[jsonhero] No available port in range {PortStart}-{PortEnd}");
    }

    /// <summary>Creates an in-memory document through the HTTP API and returns its browser URL.</summary>
    public async Task<string> SaveAsync(string jsonStr)
    {
        if (BaseUrl is null)
        {
            throw new InvalidOperationException("jsonhero server not started");
        }

        string title = $"json-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        using var response = await LoopbackHttp.PostAsJsonAsync(
            $"{BaseUrl}/api/create/file",
            new CreateFileRequest(title, jsonStr));
        string body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ReadApiError(body, "Failed to create JSON Hero document"));
        }

        using var parsed = JsonDocument.Parse(body);
        string redirect = parsed.RootElement.GetProperty("data").GetProperty("redirect").GetString()
                          ?? throw new InvalidOperationException("JSON Hero response did not contain a redirect");
        return await GetFrontendBaseUrlAsync() + redirect;
    }

    public async Task StopAsync()
    {
        var app = _app;
        _app = null;
        BaseUrl = null;
        if (app is null)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await app.StopAsync(timeout.Token);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    private void ConfigureRoutes(WebApplication app)
    {
        app.MapGet("/api/health", () => Success(new { status = "ok", documents = _documents.Count }));

        app.MapPost("/api/create/file", async (HttpContext context) =>
        {
            var request = await ReadJsonBodyAsync<CreateFileRequest>(context);
            if (request is null || string.IsNullOrWhiteSpace(request.Filename) || string.IsNullOrWhiteSpace(request.RawJson))
            {
                return Error(StatusCodes.Status400BadRequest, "BAD_REQUEST", "Missing required fields");
            }

            try
            {
                var document = CreateRawDocument(request.Filename, request.RawJson, null, false);
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
                    document = CreateRawDocument(title ?? "Untitled", contents, ttl, readOnly);
                }
                return Success(new { id = document.Id, redirect = $"/j/{document.Id}" });
            }
            catch (Exception e) when (e is FormatException or ArgumentException or JsonException or HttpRequestException or System.Xml.XmlException)
            {
                return Error(StatusCodes.Status400BadRequest, "CREATE_FAILED", e.Message);
            }
        });

        app.MapGet("/api/docs/{id}/raw", async (string id) =>
        {
            if (!TryGetDocument(id, out var document))
            {
                return Error(StatusCodes.Status404NotFound, "NOT_FOUND", $"Document {id} not found");
            }

            try
            {
                string raw = document.Type == DocumentType.Raw
                    ? document.Contents!
                    : await FetchTextAsync(document.Url!);
                _ = JsonDocument.Parse(raw);
                return Results.Text(raw, "application/json", Encoding.UTF8);
            }
            catch (Exception e) when (e is JsonException or HttpRequestException)
            {
                return Error(StatusCodes.Status502BadGateway, "FETCH_ERROR", e.Message);
            }
        });

        app.MapGet("/api/docs/{id}", async (HttpContext context, string id) =>
        {
            if (!TryGetDocument(id, out var document))
            {
                return Error(StatusCodes.Status404NotFound, "NOT_FOUND", $"Document {id} not found");
            }

            try
            {
                string raw = document.Type == DocumentType.Raw
                    ? document.Contents!
                    : await FetchTextAsync(document.Url!);
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
            if (!TryGetDocument(id, out var document))
            {
                return Error(StatusCodes.Status404NotFound, "NOT_FOUND", "No document with that slug");
            }
            if (document.ReadOnly)
            {
                return Error(StatusCodes.Status403Forbidden, "READ_ONLY", "Document is read-only");
            }

            var updated = document with { Title = request.Title };
            _documents[id] = updated;
            return Success(ToApiDocument(updated));
        });

        app.MapDelete("/api/docs/{id}", (string id) =>
        {
            if (!TryGetDocument(id, out var document))
            {
                return Error(StatusCodes.Status404NotFound, "NOT_FOUND", "Document not found");
            }
            if (document.ReadOnly)
            {
                return Error(StatusCodes.Status403Forbidden, "READ_ONLY", "Document is read-only");
            }

            _documents.TryRemove(id, out _);
            return Success(new { redirect = "/" });
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

            var document = CreateRawDocument(
                request.Title,
                request.Content.GetRawText(),
                ttl,
                request.ReadOnly ?? false);
            return Success(new { id = document.Id, title = document.Title, location = $"/j/{document.Id}" });
        });

        app.MapGet("/api/preview/{**encodedUrl}", async (string encodedUrl) =>
        {
            try
            {
                string uri = Uri.UnescapeDataString(encodedUrl);
                object preview = await CreateUriPreviewAsync(uri);
                return Success(preview);
            }
            catch
            {
                return Error(StatusCodes.Status500InternalServerError, "PREVIEW_ERROR", "Unable to preview this URL");
            }
        });
    }

    private static void ConfigureFrontend(WebApplication app, string? frontendDirectory)
    {
        if (frontendDirectory is not null)
        {
            var provider = new PhysicalFileProvider(frontendDirectory);
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = provider });
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = provider,
                ServeUnknownFileTypes = false
            });

            string indexFile = Path.Combine(frontendDirectory, "index.html");
            app.MapFallback(async context =>
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.SendFileAsync(indexFile);
            });
            return;
        }

        app.MapFallback(() => Results.Content(
            "<html><body><h1>JSON Hero API is running</h1><p>Build json-hero-frontend to enable the browser UI.</p></body></html>",
            "text/html",
            Encoding.UTF8,
            StatusCodes.Status503ServiceUnavailable));
    }

    private StoredDocument CreateRawDocument(string title, string contents, TimeSpan? ttl, bool readOnly)
    {
        _ = JsonDocument.Parse(contents);
        var document = new StoredDocument(
            CreateId(),
            title,
            readOnly,
            DocumentType.Raw,
            contents,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.Add(ttl ?? DefaultTtl));
        StoreDocument(document);
        return document;
    }

    private StoredDocument CreateUrlDocument(string title, string url, TimeSpan? ttl, bool readOnly)
    {
        var document = new StoredDocument(
            CreateId(),
            title,
            readOnly,
            DocumentType.Url,
            null,
            url,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.Add(ttl ?? DefaultTtl));
        StoreDocument(document);
        return document;
    }

    private async Task<StoredDocument> CreateFromUrlOrTextAsync(
        string urlOrText,
        string? title,
        TimeSpan? ttl,
        bool readOnly,
        bool ingest)
    {
        if (TryNormalizeRemoteUri(urlOrText, out var uri))
        {
            if (ingest)
            {
                return CreateRawDocument(title ?? uri.AbsoluteUri, await FetchTextAsync(uri.AbsoluteUri), ttl, readOnly);
            }
            return CreateUrlDocument(title ?? uri.AbsoluteUri, uri.AbsoluteUri, ttl, readOnly);
        }

        try
        {
            return CreateRawDocument(title ?? "Untitled", urlOrText, ttl, readOnly);
        }
        catch (JsonException) when (LooksLikeXml(urlOrText))
        {
            string json = ConvertXmlToJson(urlOrText);
            return CreateRawDocument(title ?? "Untitled", json, ttl, readOnly);
        }
        catch (JsonException)
        {
            throw new ArgumentException("Value is neither a valid URL, JSON document, nor XML document");
        }
    }

    private void StoreDocument(StoredDocument document)
    {
        CleanExpiredDocuments();
        if (_documents.Count >= MaxDocuments)
        {
            foreach (string id in _documents.Values
                         .OrderBy(item => item.CreatedAt)
                         .Take(Math.Max(1, _documents.Count - MaxDocuments + 1))
                         .Select(item => item.Id))
            {
                _documents.TryRemove(id, out _);
            }
        }
        _documents[document.Id] = document;
    }

    private bool TryGetDocument(string id, out StoredDocument document)
    {
        if (_documents.TryGetValue(id, out document!) && document.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return true;
        }

        _documents.TryRemove(id, out _);
        document = null!;
        return false;
    }

    private void CleanExpiredDocuments()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var item in _documents)
        {
            if (item.Value.ExpiresAt <= now)
            {
                _documents.TryRemove(item.Key, out _);
            }
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

    private async Task<string> GetFrontendBaseUrlAsync()
    {
        if (_frontendDirectory is not null || BaseUrl is null)
        {
            return BaseUrl ?? throw new InvalidOperationException("jsonhero server not started");
        }

        const string viteUrl = "http://127.0.0.1:5173";
        try
        {
            using var response = await LoopbackHttp.GetAsync(viteUrl);
            if (response.IsSuccessStatusCode)
            {
                return viteUrl;
            }
        }
        catch
        {
            // The API-only fallback page will explain how to build the frontend.
        }
        return BaseUrl;
    }

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

    private static string ReadApiError(string json, string fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("error").GetProperty("message").GetString() ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

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

    private static string CreateId()
    {
        Span<byte> bytes = stackalloc byte[12];
        RandomNumberGenerator.Fill(bytes);
        Span<char> chars = stackalloc char[12];
        for (int i = 0; i < chars.Length; i++)
        {
            chars[i] = IdAlphabet[bytes[i] % IdAlphabet.Length];
        }
        return new string(chars);
    }

    private static HttpClient CreateRemoteHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 JSONHero-Local/1.0");
        return client;
    }

    private static async Task<string> FetchTextAsync(string url)
    {
        string normalized = NormalizeSpecialUrl(url);
        using var response = await RemoteHttp.GetAsync(normalized);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static bool TryNormalizeRemoteUri(string value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) &&
            parsed.Scheme is "http" or "https" or "ipfs" or "git")
        {
            uri = new Uri(NormalizeSpecialUrl(parsed.AbsoluteUri));
            return true;
        }
        uri = null!;
        return false;
    }

    private static string NormalizeSpecialUrl(string url)
    {
        var uri = new Uri(url);
        if (uri.Scheme == "ipfs")
        {
            string path = string.IsNullOrEmpty(uri.Host)
                ? uri.AbsolutePath.TrimStart('/')
                : uri.Host + uri.AbsolutePath;
            return $"https://ipfs.io/ipfs/{path.TrimStart('/')}";
        }
        if (uri.Scheme == "git")
        {
            return $"https://{uri.Host}{uri.AbsolutePath.Replace(".git", "", StringComparison.OrdinalIgnoreCase)}";
        }
        return uri.AbsoluteUri;
    }

    private static async Task<object> CreateUriPreviewAsync(string value)
    {
        if (!TryNormalizeRemoteUri(value, out var uri))
        {
            throw new ArgumentException("Invalid preview URL");
        }

        string url = uri.AbsoluteUri;
        using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
        using var headResponse = await RemoteHttp.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead);
        string mimeType = headResponse.Content.Headers.ContentType?.MediaType ?? "";
        long? size = headResponse.Content.Headers.ContentLength;

        if (headResponse.IsSuccessStatusCode && mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return new { url, contentType = "image", mimeType, size };
        }

        using var response = await RemoteHttp.GetAsync(url);
        response.EnsureSuccessStatusCode();
        mimeType = response.Content.Headers.ContentType?.MediaType ?? mimeType;

        if (mimeType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            string rawJson = await response.Content.ReadAsStringAsync();
            JsonElement json = JsonSerializer.Deserialize<JsonElement>(rawJson);
            return new { url, contentType = "json", json };
        }
        if (mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return new { url, contentType = "image", mimeType, size = response.Content.Headers.ContentLength };
        }

        string html = await response.Content.ReadAsStringAsync();
        string? title = ReadHtmlTitle(html);
        string? description = ReadMetaContent(html, "description") ?? ReadMetaContent(html, "og:description");
        string? image = MakeAbsoluteUrl(url, ReadMetaContent(html, "og:image"));
        string? icon = MakeAbsoluteUrl(url, ReadLinkHref(html, "icon"));
        return new
        {
            url,
            contentType = "html",
            mimeType = string.IsNullOrEmpty(mimeType) ? "text/html" : mimeType,
            title,
            description,
            icon = icon is null ? null : new { url = icon },
            image = image is null ? null : new { url = image }
        };
    }

    private static string? ReadHtmlTitle(string html)
    {
        var match = Regex.Match(html, @"<title\b[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? WebUtility.HtmlDecode(Regex.Replace(match.Groups[1].Value, @"\s+", " ").Trim()) : null;
    }

    private static string? ReadMetaContent(string html, string key)
    {
        foreach (Match tag in Regex.Matches(html, @"<meta\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var attributes = ReadHtmlAttributes(tag.Value);
            if ((attributes.TryGetValue("property", out string? property) || attributes.TryGetValue("name", out property)) &&
                string.Equals(property, key, StringComparison.OrdinalIgnoreCase) &&
                attributes.TryGetValue("content", out string? content))
            {
                return WebUtility.HtmlDecode(content);
            }
        }
        return null;
    }

    private static string? ReadLinkHref(string html, string relation)
    {
        foreach (Match tag in Regex.Matches(html, @"<link\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var attributes = ReadHtmlAttributes(tag.Value);
            if (attributes.TryGetValue("rel", out string? rel) &&
                rel.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(relation, StringComparer.OrdinalIgnoreCase) &&
                attributes.TryGetValue("href", out string? href))
            {
                return WebUtility.HtmlDecode(href);
            }
        }
        return null;
    }

    private static Dictionary<string, string> ReadHtmlAttributes(string tag)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(tag, @"([:\w-]+)\s*=\s*([\""'])(.*?)\2", RegexOptions.Singleline))
        {
            result[match.Groups[1].Value] = match.Groups[3].Value;
        }
        return result;
    }

    private static string? MakeAbsoluteUrl(string baseUrl, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }
        return Uri.TryCreate(new Uri(baseUrl), candidate, out var resolved) ? resolved.AbsoluteUri : candidate;
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

    private static string? FindFrontendDirectory()
    {
        string sourceBuild = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..", "json-hero-frontend", "dist"));
        foreach (string candidate in new[] { FrontendDir, sourceBuild })
        {
            if (File.Exists(Path.Combine(candidate, "index.html")))
            {
                return candidate;
            }
        }
        return null;
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

    private enum DocumentType
    {
        Raw,
        Url
    }

    private sealed record StoredDocument(
        string Id,
        string Title,
        bool ReadOnly,
        DocumentType Type,
        string? Contents,
        string? Url,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt);

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
