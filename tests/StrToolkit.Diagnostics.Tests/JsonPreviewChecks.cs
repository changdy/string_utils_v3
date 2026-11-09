using StrToolkit.Services;

internal static class JsonPreviewChecks
{
    public static void RunAll()
    {
        CheckAsync().GetAwaiter().GetResult();
        Console.WriteLine("PASS JSON 预览创建失败可见，单引擎失败不阻断另一引擎，成功时无错误提示");
    }

    private static async Task CheckAsync()
    {
        var messages = new List<string>();
        bool fallbackOpened = false;
        var partial = new JsonPreviewService(
            _ => throw new InvalidOperationException("Failed to parse JSON: invalid token"),
            _ => { fallbackOpened = true; return Task.CompletedTask; });
        partial.PreviewFailed += messages.Add;
        await partial.OpenPreviewAsync("invalid");
        Require(fallbackOpened, "JSON Hero 失败阻断了 JSONCrack");
        Require(messages.Count == 1 && messages[0].Contains("JSON Hero：Failed to parse JSON"),
            "创建错误没有传递给界面");

        messages.Clear();
        var failed = new JsonPreviewService(
            _ => Task.FromException(new HttpRequestException("Connection refused")),
            _ => throw new InvalidOperationException("Browser launch failed"));
        failed.PreviewFailed += messages.Add;
        await failed.OpenPreviewAsync("{}");
        Require(messages.Count == 1 && messages[0].Contains("Connection refused") &&
                messages[0].Contains("Browser launch failed"), "失败信息没有合并");

        messages.Clear();
        var success = new JsonPreviewService(_ => Task.CompletedTask, _ => Task.CompletedTask);
        success.PreviewFailed += messages.Add;
        await success.OpenPreviewAsync("{}");
        Require(messages.Count == 0, "成功时显示了错误");

        var unavailable = new JsonPreviewService(new JsonCrackServer(), new JsonHeroService());
        unavailable.PreviewFailed += messages.Add;
        await unavailable.OpenPreviewAsync("{}");
        Require(messages.Count == 1 && messages[0].Contains("尚未启动"), "服务未启动时静默失败");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
