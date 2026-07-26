using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace StrToolkit.Services;

/// <summary>
/// 使用 Kestrel 托管 jsoncrack 静态资源 + JSON 缓存接口（对应 Electron 版的 jsoncrack-starter.js）。
/// 静态资源目录：应用目录下的 json-crack（复用 Electron 项目 build:jsoncrack 的产物）。
/// </summary>
public sealed class JsonCrackServer
{
    public const int PortStart = 9987;
    public const int PortEnd = 10087;

    private readonly ConcurrentDictionary<string, (string Json, DateTime Expiry)> _cache = new();
    private WebApplication? _app;

    public int Port { get; private set; } = PortStart;
    public bool IsRunning { get; private set; }

    public static string AssetsDir => Path.Combine(AppContext.BaseDirectory, "json-crack");

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(AssetsDir))
        {
            Console.Error.WriteLine($"[json-crack] 静态资源目录不存在: {AssetsDir}，JSON 预览不可用。" +
                                    "请将 Electron 项目的 json-crack 构建产物复制到该目录。");
            return;
        }

        for (int port = PortStart; port <= PortEnd; port++)
        {
            WebApplication? app = null;
            try
            {
                var builder = WebApplication.CreateBuilder();
                builder.Logging.ClearProviders();
                builder.WebHost.ConfigureKestrel(o => o.Listen(System.Net.IPAddress.Loopback, port));
                app = builder.Build();

                var fileProvider = new PhysicalFileProvider(AssetsDir);
                app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
                app.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = fileProvider,
                    ServeUnknownFileTypes = true
                });

                app.MapGet("/api/json-str", (string uuid) =>
                {
                    CleanExpired();
                    return _cache.TryGetValue(uuid, out var entry) ? entry.Json : "";
                });

                await app.StartAsync(cancellationToken).ConfigureAwait(false);
                _app = app;
                Port = port;
                IsRunning = true;
                Console.WriteLine($"[json-crack] Server running at http://127.0.0.1:{port}/");
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
            catch (IOException)
            {
                // 端口被占用，尝试下一个
                if (app is not null)
                {
                    await app.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        Console.Error.WriteLine($"[json-crack] No available port in range {PortStart}-{PortEnd}");
    }

    /// <summary>缓存 JSON 并返回预览 URL。</summary>
    public string SaveUrl(string json)
    {
        string uuid = Guid.NewGuid().ToString("N")[..12];
        _cache[uuid] = (json, DateTime.UtcNow.AddMinutes(10));
        return $"http://127.0.0.1:{Port}/editor.html?json=http://127.0.0.1:{Port}/api/json-str?uuid={uuid}";
    }

    private void CleanExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var pair in _cache)
        {
            if (pair.Value.Expiry < now)
            {
                _cache.TryRemove(pair.Key, out _);
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var app = _app;
        _app = null;
        IsRunning = false;
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
}
