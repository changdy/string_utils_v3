using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using StrToolkit.Services.JsonHero;

namespace StrToolkit.Services;

/// <summary>
/// JSON Hero 进程内服务门面：只负责编排服务器生命周期、API、文档存储和前端托管。
/// </summary>
public sealed class JsonHeroService
{
    public const int PortStart = 13001;
    public const int PortEnd = 13101;

    private static readonly HttpClient DefaultLoopbackHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    private readonly ApiEndpoints _apiEndpoints;
    private readonly FrontendHost _frontendHost;
    private readonly HttpClient _loopbackHttp;
    private WebApplication? _app;

    public JsonHeroService()
        : this(
            new DocumentStore(),
            new RemoteFetcher(),
            new FrontendHost(),
            DefaultLoopbackHttp)
    {
    }

    internal JsonHeroService(
        DocumentStore documentStore,
        RemoteFetcher remoteFetcher,
        FrontendHost frontendHost,
        HttpClient loopbackHttp)
    {
        _apiEndpoints = new ApiEndpoints(documentStore, remoteFetcher);
        _frontendHost = frontendHost;
        _loopbackHttp = loopbackHttp;
    }

    public int Port { get; private set; } = PortStart;
    public string? BaseUrl { get; private set; }
    public bool IsRunning => _app is not null && BaseUrl is not null;

    /// <summary>Directory copied beside the application during build/publish.</summary>
    public static string FrontendDir => FrontendHost.FrontendDir;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            return;
        }

        _frontendHost.RefreshDirectory();

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
                ApiEndpoints.AddCors(builder.Services);
                builder.WebHost.UseSockets(options => options.IOQueueCount = 0);
                builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));

                app = builder.Build();
                app.UseCors();
                _apiEndpoints.Map(app);
                _frontendHost.Map(app);

                await app.StartAsync(cancellationToken).ConfigureAwait(false);
                _app = app;
                Port = port;
                BaseUrl = $"http://127.0.0.1:{port}";

                string frontendStatus = _frontendHost.Directory is null
                    ? "frontend assets not found; API-only mode"
                    : $"frontend={_frontendHost.Directory}";
                Console.WriteLine($"[jsonhero] Server running at {BaseUrl} ({frontendStatus})");
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (app is not null)
                {
                    await app.DisposeAsync().ConfigureAwait(false);
                }
                throw;
            }
            catch (Exception e) when (port < PortEnd)
            {
                if (app is not null)
                {
                    await app.DisposeAsync().ConfigureAwait(false);
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
        using var response = await _loopbackHttp.PostAsJsonAsync(
            $"{BaseUrl}/api/create/file",
            new { filename = title, rawJson = jsonStr });
        string body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ReadApiError(body, "Failed to create JSON Hero document"));
        }

        using var parsed = JsonDocument.Parse(body);
        string redirect = parsed.RootElement.GetProperty("data").GetProperty("redirect").GetString()
                          ?? throw new InvalidOperationException("JSON Hero response did not contain a redirect");
        return await _frontendHost.ResolveBrowserBaseUrlAsync(BaseUrl) + redirect;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
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
            await app.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }

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
}
