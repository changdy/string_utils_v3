using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace StrToolkit.Services.JsonHero;

/// <summary>远程 URL 规范化、内容抓取及 URL 预览元数据解析。</summary>
internal sealed class RemoteFetcher
{
    private static readonly HttpClient DefaultHttpClient = CreateHttpClient();
    private readonly HttpClient _httpClient;

    public RemoteFetcher(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? DefaultHttpClient;
    }

    public async Task<string> FetchTextAsync(string url, CancellationToken cancellationToken = default)
    {
        string normalized = NormalizeSpecialUrl(url);
        using var response = await _httpClient.GetAsync(normalized, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public bool TryNormalizeUri(string value, out Uri uri)
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

    public async Task<object> CreatePreviewAsync(
        string value,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeUri(value, out var uri))
        {
            throw new ArgumentException("Invalid preview URL");
        }

        string url = uri.AbsoluteUri;
        using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
        using var headResponse = await _httpClient.SendAsync(
            headRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        string mimeType = headResponse.Content.Headers.ContentType?.MediaType ?? "";
        long? size = headResponse.Content.Headers.ContentLength;

        if (headResponse.IsSuccessStatusCode && mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return new { url, contentType = "image", mimeType, size };
        }

        using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        mimeType = response.Content.Headers.ContentType?.MediaType ?? mimeType;

        if (mimeType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            string rawJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            JsonElement json = JsonSerializer.Deserialize<JsonElement>(rawJson);
            return new { url, contentType = "json", json };
        }
        if (mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return new { url, contentType = "image", mimeType, size = response.Content.Headers.ContentLength };
        }

        string html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 JSONHero-Local/1.0");
        return client;
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
}
