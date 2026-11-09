using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using StrToolkit.Services.JsonHero;

internal static class JsonHeroChecks
{
    // 诊断入口运行在 Avalonia UI 线程，HTTP 检查使用独立线程避免阻塞其同步上下文。
    public static void RunAll() => Task.Run(RunAsync).GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        string remoteJson = NestedJson(512, objects: true);
        using var remoteHttp = new HttpClient(new JsonHandler(remoteJson));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        new ApiEndpoints(new DocumentStore(), new RemoteFetcher(remoteHttp)).Map(app);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            foreach (int depth in new[] { 63, 64, 128, 512 })
            {
                foreach (bool objects in new[] { false, true })
                {
                    string raw = NestedJson(depth, objects);
                    using var created = await PostAsync(client, "/api/create/file", new
                    {
                        filename = $"depth-{depth}.json",
                        rawJson = raw
                    });
                    string id = created.RootElement.GetProperty("data").GetProperty("id").GetString()!;
                    await AssertDocumentAsync(client, id, raw, depth, objects);
                }
            }
            Console.WriteLine("PASS JSON Hero 63、64、128、512 层对象/数组创建、读取和下载保持一致");

            using var linked = await PostAsync(client, "/api/create/url", new
            {
                jsonUrl = "https://jsonhero.test/data.json"
            });
            string linkedId = linked.RootElement.GetProperty("data").GetProperty("id").GetString()!;
            await AssertDocumentAsync(client, linkedId, remoteJson, 512, objects: true);
            using var previewResponse = await client.GetAsync(
                "/api/preview/" + Uri.EscapeDataString("https://jsonhero.test/data.json"));
            using var preview = await ReadSuccessAsync(previewResponse);
            AssertNestedValue(preview.RootElement.GetProperty("data").GetProperty("json"), 512, objects: true);
            Console.WriteLine("PASS JSON Hero 远程文档和 URL 预览支持 512 层 JSON");

            // content 直接位于请求对象内，验证请求包装同样没有挤占文档深度。
            using var publicBody = new StringContent(
                "{\"title\":\"deep-json\",\"content\":" + remoteJson + "}", Encoding.UTF8, "application/json");
            using var publicResponse = await client.PostAsync("/api/public/create", publicBody);
            using var publicCreated = await ReadSuccessAsync(publicResponse);
            string publicId = publicCreated.RootElement.GetProperty("data").GetProperty("id").GetString()!;
            await AssertDocumentAsync(client, publicId, remoteJson, 512, objects: true);

            foreach (bool objects in new[] { false, true })
            {
                string tooDeep = NestedJson(513, objects);
                using var rejected = await client.PostAsJsonAsync("/api/create/file", new
                {
                    filename = "too-deep.json",
                    rawJson = tooDeep
                });
                Require(rejected.StatusCode == HttpStatusCode.BadRequest, "超过深度上限的文件未在创建时返回 400");
                using var error = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
                Require(error.RootElement.GetProperty("error").GetProperty("code").GetString() == "INVALID_JSON",
                    "深度超限没有返回 INVALID_JSON");
                using var tooDeepBody = new StringContent(
                    "{\"title\":\"too-deep\",\"content\":" + tooDeep + "}", Encoding.UTF8, "application/json");
                using var publicRejected = await client.PostAsync("/api/public/create", tooDeepBody);
                Require(publicRejected.StatusCode == HttpStatusCode.BadRequest, "超过深度上限的 content 未在创建时返回 400");
            }
            Console.WriteLine("PASS JSON Hero 请求包装保留文档深度，513 层输入在创建时返回 400");
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static async Task AssertDocumentAsync(HttpClient client, string id, string raw, int depth, bool objects)
    {
        using var response = await client.GetAsync($"/api/docs/{id}");
        using var document = await ReadSuccessAsync(response);
        JsonElement data = document.RootElement.GetProperty("data");
        AssertNestedValue(data.GetProperty("json"), depth, objects);
        if (data.GetProperty("doc").GetProperty("type").GetString() == "raw")
        {
            Require(data.GetProperty("doc").GetProperty("contents").GetString() == raw, "原始 JSON 内容发生变化");
        }
        using var rawResponse = await client.GetAsync($"/api/docs/{id}/raw");
        Require(rawResponse.IsSuccessStatusCode, "深层 JSON 下载失败");
        Require(await rawResponse.Content.ReadAsStringAsync() == raw, "下载的 JSON 内容发生变化");
    }

    private static void AssertNestedValue(JsonElement value, int depth, bool objects)
    {
        for (int i = 0; i < depth; i++)
        {
            value = objects ? value.GetProperty("value") : value[0];
        }
        Require(value.GetString() == "中文 & ? #", "深层 JSON 的层数或叶子值发生变化");
    }

    private static string NestedJson(int depth, bool objects) =>
        string.Concat(Enumerable.Repeat(objects ? "{\"value\":" : "[", depth)) +
        "\"中文 & ? #\"" + new string(objects ? '}' : ']', depth);

    private static async Task<JsonDocument> PostAsync(HttpClient client, string path, object value)
    {
        using var response = await client.PostAsJsonAsync(path, value);
        return await ReadSuccessAsync(response);
    }

    private static async Task<JsonDocument> ReadSuccessAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        Require(response.IsSuccessStatusCode, $"JSON Hero API 返回 {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 1024 });
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }
}
