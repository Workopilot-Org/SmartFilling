using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartFilling.BackgroundWorker.Configuration;
using SmartFilling.BackgroundWorker.Services;

namespace SmartFilling.BackgroundWorker.Tests;

/// <summary>
/// HttpProgressReporterFactory 测试（D2）：验 CreateForTask(taskId, callbackUrl) 创建正确 reporter + callbackUrl 透传。
/// </summary>
public class HttpProgressReporterFactoryTests
{
    private static HttpProgressReporterFactory CreateFactory(HttpClient httpClient, out InMemoryProgressStore store, ProgressReportOptions? opts = null)
    {
        store = new InMemoryProgressStore();
        var workerOpts = new WorkerOptions { ProgressReport = opts ?? new ProgressReportOptions { Enabled = new ProgressEnabledOptions { Push = true, Pull = true }, Types = new TypesOptions { Log = true } } };
        return new HttpProgressReporterFactory(store, httpClient, Options.Create(workerOpts), NullLoggerFactory.Instance);
    }

    private class StubHandler : HttpMessageHandler
    {
        public List<(string Url, string Body)> Calls { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var body = req.Content != null ? await req.Content.ReadAsStringAsync(ct) : "";
            Calls.Add((req.RequestUri?.ToString() ?? "", body));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task CreateForTask_WithCallbackUrl_ReporterPostsToThatUrl()
    {
        var stub = new StubHandler();
        var factory = CreateFactory(new HttpClient(stub), out _, new ProgressReportOptions { Enabled = new ProgressEnabledOptions { Push = true, Pull = false }, Types = new TypesOptions { Log = true } });

        var reporter = factory.CreateForTask("task-42", "http://cb/progress/task-42");
        await reporter.SendLogAsync("hi", "step");

        Assert.Single(stub.Calls);
        Assert.Equal("http://cb/progress/task-42", stub.Calls[0].Url);  // callbackUrl 透传
        var body = JsonDocument.Parse(stub.Calls[0].Body).RootElement;
        Assert.Equal("hi", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task CreateForTask_NullCallbackUrl_ReporterPullOnly()
    {
        var stub = new StubHandler();
        var factory = CreateFactory(new HttpClient(stub), out var store, new ProgressReportOptions { Enabled = new ProgressEnabledOptions { Push = true, Pull = true }, Types = new TypesOptions { Log = true } });

        var reporter = factory.CreateForTask("t1", null);
        await reporter.SendLogAsync("hi");

        Assert.Empty(stub.Calls);  // 无 callbackUrl 不 POST
        Assert.Single(store.Get("t1")!.Logs);  // pull 写内存
    }

    [Fact]
    public void CreateForTask_DefaultCallbackUrl_PullOnly()
    {
        var stub = new StubHandler();
        var factory = CreateFactory(new HttpClient(stub), out _, new ProgressReportOptions { Enabled = new ProgressEnabledOptions { Push = true, Pull = true }, Types = new TypesOptions { Log = true } });

        // 不传 callbackUrl（默认 null）→ pull-only reporter
        var reporter = factory.CreateForTask("t1");
        Assert.NotNull(reporter);
    }
}
