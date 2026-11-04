using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace StrToolkit.Services;

/// <summary>
/// 异步诊断输出。钩子回调只入队，文件和调试输出均在后台执行，避免阻塞全局输入。
/// </summary>
internal static class AppLog
{
    private const long MaxFileBytes = 2 * 1024 * 1024;
    private const int RetainedFileCount = 7;
    private static readonly Channel<string> Queue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(4096) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private static readonly Task WriterTask = Task.Run(WriteQueuedAsync);
    private static int _droppedMessages;

    public static string LogDirectory => Path.Combine(SettingsService.UserDataDir, "logs");

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
        string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} " +
                      $"[pid={Environment.ProcessId} tid={Environment.CurrentManagedThreadId}] [{level}] {message}";
        if (!Queue.Writer.TryWrite(line))
        {
            Interlocked.Increment(ref _droppedMessages);
        }
    }

    public static void Shutdown()
    {
        Queue.Writer.TryComplete();
        try
        {
            WriterTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // 诊断失败不应阻止退出。
        }
    }

    private static async Task WriteQueuedAsync()
    {
        StreamWriter? file = null;
        long retryAt = 0;
        int sequence = 0;
        try
        {
            await foreach (string line in Queue.Reader.ReadAllAsync())
            {
                try
                {
                    Debug.WriteLine(line);
                    Trace.WriteLine(line);
                    Console.Error.WriteLine(line);
                }
                catch
                {
                    // 安装版可能没有控制台，仍继续写文件。
                }

                try
                {
                    if (file is not null && file.BaseStream.Position >= MaxFileBytes)
                    {
                        file.Dispose();
                        file = null;
                    }
                    if (file is null)
                    {
                        if (Environment.TickCount64 < retryAt)
                        {
                            continue;
                        }
                        Directory.CreateDirectory(LogDirectory);
                        string path = Path.Combine(LogDirectory,
                            $"str-toolkit-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}-{sequence++}.log");
                        file = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                            FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
                        PruneOldFiles(path);
                    }
                    int dropped = Interlocked.Exchange(ref _droppedMessages, 0);
                    if (dropped != 0)
                    {
                        await file.WriteLineAsync($"{DateTimeOffset.Now:O} [WRN] 日志队列已满，丢弃 {dropped} 条记录。");
                    }
                    await file.WriteLineAsync(line);
                }
                catch (Exception exception)
                {
                    try { file?.Dispose(); } catch { }
                    file = null;
                    retryAt = Environment.TickCount64 + 30_000;
                    try { Trace.WriteLine($"写诊断日志失败: {exception.Message}"); } catch { }
                }
            }
        }
        finally
        {
            try { file?.Dispose(); } catch { }
        }
    }

    private static void PruneOldFiles(string currentPath)
    {
        try
        {
            var files = new DirectoryInfo(LogDirectory).GetFiles("str-toolkit-*.log");
            Array.Sort(files, (left, right) => right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc));
            int retained = 1;
            foreach (var file in files)
            {
                if (string.Equals(file.FullName, currentPath, StringComparison.OrdinalIgnoreCase)) continue;
                if (retained++ >= RetainedFileCount)
                {
                    try { file.Delete(); } catch { }
                }
            }
        }
        catch
        {
            // 清理失败不影响当前日志。
        }
    }
}
