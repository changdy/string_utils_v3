using System;
using System.Diagnostics;

namespace StrToolkit.Services;

/// <summary>
/// 统一诊断输出：写入 Debug/Trace（Visual Studio「输出 → 调试」可见）以及 Console.Error。
/// </summary>
internal static class AppLog
{
    public static void Error(string message)
    {
        Write("ERR", message);
    }

    public static void Error(string message, Exception ex)
    {
        Write("ERR", $"{message}\n{ex}");
    }

    public static void Warn(string message)
    {
        Write("WRN", message);
    }

    public static void Info(string message)
    {
        Write("INF", message);
    }

    private static void Write(string level, string message)
    {
        string line = $"[StrToolkit][{level}] {message}";
        // VS 调试时「输出」窗口默认显示 Debug/Trace
        Debug.WriteLine(line);
        Trace.WriteLine(line);
        // 从终端/管道启动时仍可见
        Console.Error.WriteLine(line);
    }
}
