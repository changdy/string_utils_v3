using System;
using System.Diagnostics;
using System.IO;

namespace StrToolkit.Services;

/// <summary>调用 VSCode 打开 JSON 差异对比（对应 Electron 版的 open-diff）。</summary>
public static class VsCodeDiffService
{
    public static void OpenDiff(string json1, string json2)
    {
        try
        {
            string timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            string random = Guid.NewGuid().ToString("N")[..6];
            string file1 = Path.Combine(Path.GetTempPath(), $"json-diff-{timestamp}-{random}-1.json");
            string file2 = Path.Combine(Path.GetTempPath(), $"json-diff-{timestamp}-{random}-2.json");
            File.WriteAllText(file1, json1);
            File.WriteAllText(file2, json2);

            if (!TryRun(OperatingSystem.IsWindows() ? "code.cmd" : "code", file1, file2))
            {
                string[] fallbackPaths = OperatingSystem.IsWindows()
                    ? new[]
                    {
                        Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "",
                            "Programs", "Microsoft VS Code", "bin", "code.cmd")
                    }
                    : OperatingSystem.IsMacOS()
                        ? new[]
                        {
                            "/usr/local/bin/code",
                            "/Applications/Visual Studio Code.app/Contents/Resources/app/bin/code"
                        }
                        : new[] { "/usr/bin/code", "/usr/local/bin/code", "/snap/bin/code" };

                bool opened = false;
                foreach (var p in fallbackPaths)
                {
                    if (TryRun(p, file1, file2))
                    {
                        opened = true;
                        break;
                    }
                }
                if (!opened)
                {
                    Console.Error.WriteLine("Failed to open VSCode diff: code command not found");
                }
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Failed to open diff: {e.Message}");
        }
    }

    private static bool TryRun(string command, string file1, string file2)
    {
        try
        {
            bool isWindowsBatchFile = OperatingSystem.IsWindows()
                && (command.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                    || command.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));
            string? batchFile = isWindowsBatchFile ? ResolveWindowsBatchFile(command) : null;
            if (isWindowsBatchFile && batchFile is null)
            {
                return false;
            }

            var psi = new ProcessStartInfo
            {
                FileName = isWindowsBatchFile
                    ? Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe"
                    : command,
                UseShellExecute = false,
                CreateNoWindow = OperatingSystem.IsWindows(),
                WindowStyle = OperatingSystem.IsWindows()
                    ? ProcessWindowStyle.Hidden
                    : ProcessWindowStyle.Normal
            };

            if (isWindowsBatchFile)
            {
                // Batch files must run through cmd.exe. CreateNoWindow prevents the
                // command prompt window from flashing before VS Code opens.
                psi.ArgumentList.Add("/d");
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add("call");
                psi.ArgumentList.Add(batchFile!);
            }

            psi.ArgumentList.Add("--diff");
            psi.ArgumentList.Add(file1);
            psi.ArgumentList.Add(file2);
            using var process = Process.Start(psi);
            return process is not null;
        }
        catch
        {
            return false;
        }
    }

    private static string? ResolveWindowsBatchFile(string command)
    {
        if (File.Exists(command))
        {
            return Path.GetFullPath(command);
        }

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate = Path.Combine(directory.Trim('"'), command);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
