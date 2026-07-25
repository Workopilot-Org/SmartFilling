using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SmartFilling.BackgroundWorker.Configuration;
using SmartFilling.BackgroundWorker.Services;
using SmartFilling.Engine.Models;

namespace SmartFilling.BackgroundWorker.Tests;

/// <summary>
/// HttpProgressReporter 测试（D2）：验 InMemoryProgressStore 写入（pull）+ HTTP POST（push + callbackUrl + Types 过滤）。
/// pull 和 push 两路都受 Types 控制（对齐计划"采集/推送"语义）。
/// </summary>
public class HttpProgressReporterTests
{
    private class StubHandler : HttpMessageHandler
    {
        public List<(string Url, string Body)> Calls { get; } = new();
        private readonly HttpStatusCode _code;
        public StubHandler(HttpStatusCode code = HttpStatusCode.OK) { _code = code; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var body = req.Content != null ? await req.Content.ReadAsStringAsync(ct) : "";
            Calls.Add((req.RequestUri?.ToString() ?? "", body));
            return new HttpResponseMessage(_code);
        }
    }

    private static ProgressReportOptions Opts(bool push, bool pull, bool log = true, bool screenshot = true, bool completed = false) => new()
    {
        Enabled = new ProgressEnabledOptions { Push = push, Pull = pull },
        Types = new TypesOptions { Log = log, Screenshot = screenshot, Completed = completed },
        PushOptions = new ProgressPushOptions { TimeoutSeconds = 5 }
    };

    private static HttpProgressReporter Create(StubHandler stub, ProgressReportOptions opts, out InMemoryProgressStore store)
    {
        store = new InMemoryProgressStore();
        return new HttpProgressReporter("t1", "http://cb/progress/t1", opts, store, new HttpClient(stub), NullLogger<HttpProgressReporter>.Instance);
    }

    [Fact]
    public async Task PullTrue_SendLog_WritesStore()
    {
        var reporter = Create(new StubHandler(), Opts(push: false, pull: true), out var store);
        await reporter.SendLogAsync("hello", "step1");
        var entry = store.Get("t1");
        Assert.NotNull(entry);
        Assert.Single(entry!.Logs);
        Assert.Equal("hello", entry.Logs[0].Message);
        Assert.Equal("step1", entry.Logs[0].StepName);
    }

    [Fact]
    public async Task PullTrue_SendScreenshot_WritesStore_LatestScreenshot()
    {
        var reporter = Create(new StubHandler(), Opts(push: false, pull: true), out var store);
        await reporter.SendScreenshotAsync(Convert.FromBase64String("AAAA"), "desc");
        var entry = store.Get("t1");
        Assert.NotNull(entry);
        Assert.Single(entry!.Screenshots);
        Assert.Equal("AAAA", entry.LatestScreenshot);  // base64
    }

    [Fact]
    public async Task PushTrue_LogType_PostsToCallbackUrl_WithMessage()
    {
        var stub = new StubHandler();
        var reporter = Create(stub, Opts(push: true, pull: false), out _);
        await reporter.SendLogAsync("hello", "step1");

        Assert.Single(stub.Calls);
        Assert.Equal("http://cb/progress/t1", stub.Calls[0].Url);
        var body = JsonDocument.Parse(stub.Calls[0].Body).RootElement;
        Assert.Equal("hello", body.GetProperty("message").GetString());
        Assert.Equal("step1", body.GetProperty("stepName").GetString());
    }

    [Fact]
    public async Task PushFalse_DoesNotPost()
    {
        var stub = new StubHandler();
        var reporter = Create(stub, Opts(push: false, pull: true), out _);
        await reporter.SendLogAsync("hello");
        Assert.Empty(stub.Calls);
    }

    [Fact]
    public async Task TypesLogFalse_DoesNotPost_DoesNotWriteStore()
    {
        var stub = new StubHandler();
        var reporter = Create(stub, Opts(push: true, pull: true, log: false), out var store);
        await reporter.SendLogAsync("hello");
        Assert.Empty(stub.Calls);          // Types.log=false 不 POST
        Assert.Null(store.Get("t1"));      // Types.log=false 也不写内存（对齐采集语义）
    }

    [Fact]
    public async Task ScreenshotTypeFalse_DoesNotPost()
    {
        var stub = new StubHandler();
        var reporter = Create(stub, Opts(push: true, pull: true, screenshot: false), out _);
        await reporter.SendScreenshotAsync(Convert.FromBase64String("AAAA"));
        Assert.Empty(stub.Calls);  // Types.screenshot=false 不 POST
    }

    [Fact]
    public async Task NoCallbackUrl_PushTrue_DoesNotPost_WritesStore()
    {
        var stub = new StubHandler();
        var store = new InMemoryProgressStore();
        // callbackUrl=null（pull-only 模式）
        var reporter = new HttpProgressReporter("t1", null, Opts(push: true, pull: true), store, new HttpClient(stub), NullLogger<HttpProgressReporter>.Instance);
        await reporter.SendLogAsync("hello");
        Assert.Empty(stub.Calls);  // 无 callbackUrl，push 也不 POST
        Assert.Single(store.Get("t1")!.Logs);  // pull 写内存
    }

    // ===== Types.completed 测试 =====

    [Fact]
    public async Task Completed_PullTrue_WritesStore()
    {
        var reporter = Create(new StubHandler(), Opts(push: false, pull: true, completed: true), out var store);
        var result = new ScriptResult { Success = true, ReturnData = new() { ["billCode"] = "B001" }, Stats = new ExecutionStats { AiSteps = 2 } };
        await reporter.SendTaskCompletedAsync(result);

        var entry = store.Get("t1");
        Assert.NotNull(entry);
        Assert.NotNull(entry!.Completed);
        Assert.Equal("Success", entry.Completed!.Result);
        Assert.Equal("B001", entry.Completed.ReturnData!["billCode"]!.ToString());
    }

    [Fact]
    public async Task Completed_PushTrue_PostsToCallbackUrl()
    {
        var stub = new StubHandler();
        var reporter = Create(stub, Opts(push: true, pull: false, completed: true), out _);
        var result = new ScriptResult { Success = true, ReturnData = new() { ["billCode"] = "B001" } };
        await reporter.SendTaskCompletedAsync(result);

        Assert.Single(stub.Calls);
        Assert.Equal("http://cb/progress/t1", stub.Calls[0].Url);
        var body = JsonDocument.Parse(stub.Calls[0].Body).RootElement;
        Assert.Equal("t1", body.GetProperty("taskId").GetString());
        Assert.Equal("Success", body.GetProperty("result").GetString());
        Assert.Equal("B001", body.GetProperty("resultData").GetProperty("billCode").GetString());
    }

    [Fact]
    public async Task Completed_Stopped_SendsStoppedResult()
    {
        var stub = new StubHandler();
        var reporter = Create(stub, Opts(push: true, pull: false, completed: true), out _);
        var result = new ScriptResult { Success = false, FailureType = FailureType.Cancelled, ErrorMessage = "用户取消" };
        await reporter.SendTaskCompletedAsync(result);

        Assert.Single(stub.Calls);
        var body = JsonDocument.Parse(stub.Calls[0].Body).RootElement;
        Assert.Equal("Stopped", body.GetProperty("result").GetString());
        Assert.Equal("用户取消", body.GetProperty("errorMessage").GetString());
    }

    [Fact]
    public async Task Completed_False_DoesNothing()
    {
        var stub = new StubHandler();
        var reporter = Create(stub, Opts(push: true, pull: true, completed: false), out var store);
        var result = new ScriptResult { Success = true };
        await reporter.SendTaskCompletedAsync(result);

        Assert.Empty(stub.Calls);            // Types.completed=false 不 POST
        Assert.Null(store.Get("t1"));        // Types.completed=false 也不写内存
    }
}