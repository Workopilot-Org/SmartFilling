using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SmartFilling.BackgroundWorker.Configuration;
using SmartFilling.BackgroundWorker.Models;
using SmartFilling.BackgroundWorker.Services;
using SmartFilling.Engine.Models;
using BWTaskStatus = SmartFilling.BackgroundWorker.Models.TaskStatus;

namespace SmartFilling.BackgroundWorker.Tests;

/// <summary>
/// HttpTaskCompletionHandler 重写测试（D2 改造后不再依赖 IProgressReporterFactory）。
/// 验：① 三分发（Success/Stopped=取消/Failed=失败）→ payload.result 字段值；
///     ② HTTP POST payload 字段值（resultData/stats/failureType/screenshot 真传，不只验"调了 POST"）；
///     ③ WebhookRetry 重试路径（首次失败→按 Retry{Count,Interval} 重试→最终成功/仍失败）；
///     ④ pull 开关（pull=true 写 JSONL AppendAsync，pull=false 不写）。
/// </summary>
public class HttpTaskCompletionHandlerTests
{
    private class StubHandler : HttpMessageHandler
    {
        public List<(string Url, string Body)> Calls { get; } = new();
        private readonly Queue<HttpStatusCode> _codes;
        public StubHandler(params HttpStatusCode[] codes) { _codes = new Queue<HttpStatusCode>(codes); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var body = req.Content != null ? await req.Content.ReadAsStringAsync(ct) : "";
            Calls.Add((req.RequestUri?.ToString() ?? "", body));
            var code = _codes.Count > 0 ? _codes.Dequeue() : HttpStatusCode.OK;
            return new HttpResponseMessage(code);
        }
    }

    private static WorkerOptions Opts(bool push, bool pull, int retryCount = 3) => new()
    {
        CompleteReport = new CompleteReportOptions
        {
            Enabled = new CompleteEnabledOptions { Push = push, Pull = pull },
            PushOptions = new CompletePushOptions
            {
                TimeoutSeconds = 10,
                Retry = new WebhookRetry { Count = retryCount, Interval = 0 }  // Interval=0 测试不 sleep
            }
        }
    };

    private static HttpTaskCompletionHandler CreateHandler(StubHandler stub, WorkerOptions opts,
        out TaskResultStore store, InMemoryProgressStore? progressStore = null)
    {
        store = Substitute.For<TaskResultStore>(Options.Create(new WorkerOptions()), NullLogger<TaskResultStore>.Instance);
        return new HttpTaskCompletionHandler(
            NullLogger<HttpTaskCompletionHandler>.Instance,
            store,
            new HttpClient(stub),
            Options.Create(opts),
            progressStore ?? new InMemoryProgressStore());
    }

    private static FillTask Task(BWTaskStatus status, FailureType? ft = null, string? screenshot = "img-data") => new()
    {
        TaskId = "t1",
        Status = status,
        FailureType = ft,
        ErrorMessage = status == BWTaskStatus.Completed ? null : "boom",
        ReturnData = status == BWTaskStatus.Completed ? new() { ["billCode"] = "B001" } : null,
        Stats = status == BWTaskStatus.Completed ? new ExecutionStats { AiSteps = 2 } : null,
        FinalScreenshot = screenshot,
        CompleteCallbackUrl = "http://callback/complete/t1"
    };

    private static JsonElement PayloadBody(string json)
        => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task Completed_POST_PayloadSuccess_ReturnData_Stats_Screenshot()
    {
        var stub = new StubHandler(HttpStatusCode.OK);
        var handler = CreateHandler(stub, Opts(push: true, pull: true), out var store);
        var task = Task(BWTaskStatus.Completed);

        await handler.OnTaskCompleted(task);

        Assert.Single(stub.Calls);
        Assert.Equal("http://callback/complete/t1", stub.Calls[0].Url);
        var body = PayloadBody(stub.Calls[0].Body);
        Assert.Equal("t1", body.GetProperty("taskId").GetString());
        Assert.Equal("Success", body.GetProperty("result").GetString());
        // 验值不只验字段存在（test-covers-production-deserialize-path）
        Assert.Equal("B001", body.GetProperty("resultData").GetProperty("billCode").GetString());
        Assert.Equal(2, body.GetProperty("stats").GetProperty("aiSteps").GetInt32());
        Assert.Equal("img-data", body.GetProperty("screenshot").GetString());
        await store.Received(1).AppendAsync(Arg.Any<TaskResultRecord>());  // pull=true 写 JSONL
    }

    [Fact]
    public async Task Cancelled_POST_PayloadStopped()
    {
        var stub = new StubHandler(HttpStatusCode.OK);
        var handler = CreateHandler(stub, Opts(push: true, pull: true), out _);
        var task = Task(BWTaskStatus.Failed, FailureType.Cancelled);

        await handler.OnTaskCompleted(task);

        var body = PayloadBody(stub.Calls[0].Body);
        Assert.Equal("Stopped", body.GetProperty("result").GetString());  // 取消→停止语义
    }

    [Theory]
    [InlineData(FailureType.Deterministic)]
    [InlineData(FailureType.Timeout)]
    [InlineData(FailureType.AiFallback)]
    [InlineData(FailureType.AiPhase)]
    public async Task OtherFailure_POST_PayloadFailed_FailureTypePassed(FailureType ft)
    {
        var stub = new StubHandler(HttpStatusCode.OK);
        var handler = CreateHandler(stub, Opts(push: true, pull: true), out _);
        var task = Task(BWTaskStatus.Failed, ft);

        await handler.OnTaskCompleted(task);

        var body = PayloadBody(stub.Calls[0].Body);
        Assert.Equal("Failed", body.GetProperty("result").GetString());
        Assert.Equal(ft.ToString(), body.GetProperty("failureType").GetString());
        Assert.Equal("boom", body.GetProperty("errorMessage").GetString());
    }

    [Fact]
    public async Task Retry_FirstFailThenSucceed_CallsTwice()
    {
        var stub = new StubHandler(HttpStatusCode.InternalServerError, HttpStatusCode.OK);  // 首次 500，二次 200
        var handler = CreateHandler(stub, Opts(push: true, pull: false, retryCount: 3), out _);
        var task = Task(BWTaskStatus.Completed);

        await handler.OnTaskCompleted(task);

        Assert.Equal(2, stub.Calls.Count);  // 首次失败 + 1 次重试成功
    }

    [Fact]
    public async Task Retry_AllFail_Exhausted_CallsFourTimes()
    {
        var stub = new StubHandler(Enumerable.Repeat(HttpStatusCode.InternalServerError, 10).ToArray());  // 全 500
        var handler = CreateHandler(stub, Opts(push: true, pull: false, retryCount: 3), out _);
        var task = Task(BWTaskStatus.Completed);

        await handler.OnTaskCompleted(task);

        // 首次 + 3 次重试 = 4 次（Count=3 → maxAttempts=4）
        Assert.Equal(4, stub.Calls.Count);
    }

    [Fact]
    public async Task PullFalse_DoesNotWriteJsonL()
    {
        var stub = new StubHandler(HttpStatusCode.OK);
        var handler = CreateHandler(stub, Opts(push: true, pull: false), out var store);
        var task = Task(BWTaskStatus.Completed);

        await handler.OnTaskCompleted(task);

        await store.DidNotReceive().AppendAsync(Arg.Any<TaskResultRecord>());
    }

    [Fact]
    public async Task PushFalse_DoesNotPost()
    {
        var stub = new StubHandler(HttpStatusCode.OK);
        var handler = CreateHandler(stub, Opts(push: false, pull: true), out _);
        var task = Task(BWTaskStatus.Completed);

        await handler.OnTaskCompleted(task);

        Assert.Empty(stub.Calls);  // push=false 不 POST（终态只写 JSONL）
    }
}
