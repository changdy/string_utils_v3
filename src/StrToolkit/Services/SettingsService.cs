using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StrToolkit.Services;

public sealed class AppSettings
{
    [JsonPropertyName("accelerator")]
    public string Accelerator { get; set; } = "CommandOrControl+Alt+D";

    [JsonPropertyName("skipList")]
    public List<string> SkipList { get; set; } = new();

    [JsonPropertyName("autoLaunch")]
    public bool AutoLaunch { get; set; }
}

/// <summary>JSON 文件配置持久化，对应 Electron 版的 electron-store。</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _filePath;

    public AppSettings Settings { get; }

    public static string UserDataDir
    {
        get
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(baseDir, "str-toolkit-avalonia");
        }
    }

    public static string UserScriptDir => Path.Combine(UserDataDir, "user-scripts");

    public SettingsService()
    {
        Directory.CreateDirectory(UserDataDir);
        Directory.CreateDirectory(UserScriptDir);
        _filePath = Path.Combine(UserDataDir, "settings.json");
        Settings = Load();
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_filePath)) ?? new AppSettings();
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"读取配置失败: {e.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(_filePath, JsonSerializer.Serialize(Settings, Options));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"保存配置失败: {e.Message}");
        }
    }
}
