using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace StrToolkit.Services;

/// <summary>JSON 预览入口：优先打开 jsonhero，同时打开 jsoncrack（与 Electron 版行为一致）。</summary>
public sealed class JsonPreviewService
{
    private readonly JsonCrackServer _jsonCrack;
    private readonly JsonHeroService _jsonHero;

    public JsonPreviewService(JsonCrackServer jsonCrack, JsonHeroService jsonHero)
    {
        _jsonCrack = jsonCrack;
        _jsonHero = jsonHero;
    }

    public void OpenPreview(string json)
    {
        _ = Task.Run(async () =>
        {
            if (_jsonHero.IsRunning)
            {
                try
                {
                    OpenInBrowser(await _jsonHero.SaveAsync(json));
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"[jsonhero] failed to open url: {e.Message}");
                }
            }
            if (_jsonCrack.IsRunning)
            {
                OpenInBrowser(_jsonCrack.SaveUrl(json));
            }
        });
    }

    /// <summary>用系统默认浏览器打开 URL（对应 Electron 的 shell.openExternal）。</summary>
    public static void OpenInBrowser(string url)
    {
        try
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
        catch (Exception e)
        {
            Console.Error.WriteLine($"打开浏览器失败: {e.Message}");
        }
    }
}
