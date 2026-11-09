using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace StrToolkit.Services;

/// <summary>JSON 预览入口：优先打开 jsonhero，同时打开 jsoncrack（与 Electron 版行为一致）。</summary>
public sealed class JsonPreviewService
{
    private readonly Func<string, Task> _openHero;
    private readonly Func<string, Task> _openCrack;

    public event Action<string>? PreviewFailed;

    public JsonPreviewService(JsonCrackServer jsonCrack, JsonHeroService jsonHero)
        : this(
            async json =>
            {
                if (!jsonHero.IsRunning)
                    throw new InvalidOperationException("本机预览服务尚未启动，请稍后重试。");
                LaunchBrowser(await jsonHero.SaveAsync(json).ConfigureAwait(false));
            },
            json =>
            {
                if (!jsonCrack.IsRunning)
                    throw new InvalidOperationException("本机预览服务尚未启动，请稍后重试。");
                LaunchBrowser(jsonCrack.SaveUrl(json));
                return Task.CompletedTask;
            })
    {
    }

    internal JsonPreviewService(Func<string, Task> openHero, Func<string, Task> openCrack)
    {
        _openHero = openHero;
        _openCrack = openCrack;
    }

    public void OpenPreview(string json) => _ = Task.Run(() => OpenPreviewAsync(json));

    internal async Task OpenPreviewAsync(string json)
    {
        var errors = new List<string>();
        foreach (var (name, open) in new[] { ("JSON Hero", _openHero), ("JSONCrack", _openCrack) })
        {
            try
            {
                await open(json).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                AppLog.Error($"{name} 预览失败", exception);
                errors.Add($"{name}：{exception.Message}");
            }
        }
        if (errors.Count > 0)
            PreviewFailed?.Invoke(string.Join(Environment.NewLine, errors));
    }

    /// <summary>用系统默认浏览器打开 URL（对应 Electron 的 shell.openExternal）。</summary>
    public static void OpenInBrowser(string url)
    {
        try
        {
            LaunchBrowser(url);
        }
        catch (Exception e)
        {
            AppLog.Error("打开浏览器失败", e);
        }
    }

    private static void LaunchBrowser(string url)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        else if (OperatingSystem.IsMacOS())
        {
            Process.Start("open", url);
        }
        else
        {
            Process.Start("xdg-open", url);
        }
    }
}
