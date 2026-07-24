using System;
using System.IO;

namespace StrToolkit.Solvers;

public sealed record UserScriptPackage(
    string Id,
    string ModuleRoot,
    string EntryPath,
    string? IconBasePath)
{
    public static UserScriptPackage FromLegacyFile(string scriptPath)
    {
        string fullPath = Path.GetFullPath(scriptPath);
        string moduleRoot = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException($"无法确定脚本目录: {scriptPath}");
        string id = Path.GetFileNameWithoutExtension(fullPath);
        return new UserScriptPackage(
            id,
            moduleRoot,
            fullPath,
            Path.Combine(moduleRoot, id));
    }

    public static UserScriptPackage FromDirectory(string packageDirectory)
    {
        string root = Path.GetFullPath(packageDirectory);
        string id = new DirectoryInfo(root).Name;
        string entryPath = Path.Combine(root, "index.js");
        if (!File.Exists(entryPath))
        {
            throw new FileNotFoundException("用户脚本包缺少固定入口 index.js", entryPath);
        }

        return new UserScriptPackage(
            id,
            root,
            entryPath,
            File.Exists(Path.Combine(root, "icon.svg")) ||
            File.Exists(Path.Combine(root, "icon.png"))
                ? Path.Combine(root, "icon")
                : null);
    }
}
