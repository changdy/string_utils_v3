using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace StrToolkit.Services;

/// <summary>
/// 单实例锁（对应 Electron 的 requestSingleInstanceLock）。
/// 使用命名互斥量 + 命名管道通知已运行实例弹出窗口。
/// </summary>
public static class SingleInstance
{
    private const string MutexName = "StrToolkit.Avalonia.SingleInstance";
    private const string PipeName = "StrToolkit.Avalonia.Pipe";

    private static Mutex? _mutex;
    private static CancellationTokenSource? _cts;

    public static event Action? SecondInstanceLaunched;

    public static bool TryAcquire()
    {
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            return false;
        }
        _cts = new CancellationTokenSource();
        _ = ListenAsync(_cts.Token);
        return true;
    }

    public static void NotifyFirstInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1000);
            using var writer = new StreamWriter(client);
            writer.WriteLine("show");
            writer.Flush();
        }
        catch
        {
            // 忽略：旧实例可能正在退出
        }
    }

    public static void Release()
    {
        _cts?.Cancel();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
    }

    private static async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In);
                await server.WaitForConnectionAsync(token);
                using var reader = new StreamReader(server);
                await reader.ReadLineAsync(token);
                SecondInstanceLaunched?.Invoke();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(500, token);
            }
        }
    }
}
