using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;

namespace StrToolkit.Services.JsonHero;

/// <summary>JSON Hero SPA 资源发现、静态托管及开发服务器回退。</summary>
internal sealed class FrontendHost
{
    private static readonly HttpClient DefaultLoopbackHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    private readonly HttpClient _loopbackHttp;

    public FrontendHost(HttpClient? loopbackHttp = null)
    {
        _loopbackHttp = loopbackHttp ?? DefaultLoopbackHttp;
    }

    public static string FrontendDir => Path.Combine(AppContext.BaseDirectory, "jsonhero-frontend");

    public string? Directory { get; private set; }

    public void RefreshDirectory()
    {
        Directory = FindFrontendDirectory();
    }

    public void Map(WebApplication app)
    {
        if (Directory is not null)
        {
            var provider = new PhysicalFileProvider(Directory);
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = provider });
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = provider,
                ServeUnknownFileTypes = false
            });

            string indexFile = Path.Combine(Directory, "index.html");
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

    public async Task<string> ResolveBrowserBaseUrlAsync(
        string apiBaseUrl,
        CancellationToken cancellationToken = default)
    {
        if (Directory is not null)
        {
            return apiBaseUrl;
        }

        const string viteUrl = "http://127.0.0.1:5173";
        try
        {
            using var response = await _loopbackHttp.GetAsync(viteUrl, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return viteUrl;
            }
        }
        catch
        {
            // API-only fallback page explains how to build the frontend.
        }
        return apiBaseUrl;
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
}
