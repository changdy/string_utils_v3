using System;
using System.IO;
using System.Linq;

namespace StrToolkit.Services;

/// <summary>
/// 首次提供随应用分发的用户脚本包。每个包只尝试安装一次，避免应用升级或用户删除后
/// 又覆盖/恢复用户自行维护的脚本。
/// </summary>
public static class BundledUserScriptInstaller
{
    private const string InstalledStateDirectoryName = ".bundled-user-scripts";

    public static void InstallMissing(string sourceRoot, string targetRoot, string userDataRoot)
    {
        if (!Directory.Exists(sourceRoot))
        {
            return;
        }

        string stateRoot = Path.Combine(userDataRoot, InstalledStateDirectoryName);
        Directory.CreateDirectory(stateRoot);

        foreach (string sourceDirectory in Directory.EnumerateDirectories(sourceRoot)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            string packageId = new DirectoryInfo(sourceDirectory).Name;
            string entryPath = Path.Combine(sourceDirectory, "index.js");
            if (!File.Exists(entryPath))
            {
                continue;
            }

            string markerPath = Path.Combine(stateRoot, packageId + ".installed");
            if (File.Exists(markerPath))
            {
                continue;
            }

            string targetDirectory = Path.Combine(targetRoot, packageId);
            if (!Directory.Exists(targetDirectory))
            {
                string temporaryDirectory = Path.Combine(
                    targetRoot,
                    $".{packageId}.installing-{Guid.NewGuid():N}");
                try
                {
                    CopyDirectory(sourceDirectory, temporaryDirectory);
                    Directory.Move(temporaryDirectory, targetDirectory);
                }
                catch
                {
                    if (Directory.Exists(temporaryDirectory))
                    {
                        Directory.Delete(temporaryDirectory, recursive: true);
                    }
                    throw;
                }
            }

            // 即使同名目录原本就存在，也记录为已处理；它属于用户，不应在下次启动时覆盖。
            File.WriteAllText(markerPath, "");
        }
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        foreach (string sourceFile in Directory.EnumerateFiles(sourceDirectory))
        {
            File.Copy(
                sourceFile,
                Path.Combine(targetDirectory, Path.GetFileName(sourceFile)),
                overwrite: false);
        }
        foreach (string childDirectory in Directory.EnumerateDirectories(sourceDirectory))
        {
            CopyDirectory(
                childDirectory,
                Path.Combine(targetDirectory, new DirectoryInfo(childDirectory).Name));
        }
    }
}
