using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SmartFilling.BackgroundWorker.Models;
using SmartFilling.BackgroundWorker.Services;
using SmartFilling.Engine.Models;
using SmartFilling.Engine.Reporting;
using BWTaskStatus = SmartFilling.BackgroundWorker.Models.TaskStatus;

namespace SmartFilling.BackgroundWorker.Tests;

/// <summary>
/// P3-10 必补：HttpTaskCompletionHandler 三分发单测（completionHandler 按 task 实际状态单一出口发终态）。
/// 依赖全可 mock（IProgressReporterFactory + ITaskProgressReporter），无需真实浏览器/WS。
/// </summary>
public class HttpTaskCompletionHandlerTests
{
    private static HttpTaskCompletionHandler CreateHandler(out ITaskProgressReporter reporter)
    {
        reporter = Substitute.For<ITaskProgressReporter>();
        var factory = Substitute.For<IProgressReporterFactory>();
        factory.CreateForTask(Arg.Any<string>()).Returns(reporter);
        return new HttpTaskCompletionHandler(NullLogger<HttpTaskCompletionHandler>.Instance, factory);
    }

    private static FillTask Task(BWTaskStatus status, FailureType? ft = null) => new()
    {
        TaskId = "t1",
        Status = status,
        FailureType = ft,
        ErrorMessage = status == BWTaskStatus.Completed ? null : "boom",
        ReturnData = status == BWTaskStatus.Completed ? new() { ["billCode"] = "B001" } : null,
        Stats = status == BWTaskStatus.Completed ? new ExecutionStats { AiSteps = 2 } : null
    };

    [Fact]
    public async Task Completed_SendsTaskCompleted_SuccessTrue_ReturnDataStatsPassed()
    {
        var handler = CreateHandler(out var reporter);
        var task = Task(BWTaskStatus.Completed);

        await handler.OnTaskCompleted(task);

        await reporter.Received(1).SendTaskCompletedAsync(Arg.Is<ScriptResult>(s =>
            s!.Success && s.Status == "completed"
            && s.ReturnData != null && s.ReturnData.ContainsKey("billCode")
            && (string)s.ReturnData["billCode"] == "B001"   // C⑥/P7：验值不只验键（ContainsKey 不验值，silent-success 防御）
            && s.Stats != null && s.Stats.AiSteps == 2));
        await reporter.DidNotReceive().SendTaskStoppedAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task Cancelled_SendsTaskStopped_NotTaskCompleted()
    {
        var handler = CreateHandler(out var reporter);
        var task = Task(BWTaskStatus.Failed, FailureType.Cancelled);

        await handler.OnTaskCompleted(task);

        await reporter.Received(1).SendTaskStoppedAsync(Arg.Is<string>(m => m!.Contains("boom")));
        await reporter.DidNotReceive().SendTaskCompletedAsync(Arg.Any<ScriptResult>());
    }

    [Theory]
    [InlineData(FailureType.Deterministic)]
    [InlineData(FailureType.Timeout)]
    [InlineData(FailureType.AiFallback)]
    [InlineData(FailureType.AiPhase)]  // 观察②修复（2026-07-06）：新增 AiPhase 也走 SendTaskCompleted 失败路径（非 Cancelled 即真失败）
    public async Task OtherFailure_SendsTaskCompleted_SuccessFalse_FailureTypePassed(FailureType ft)
    {
        var handler = CreateHandler(out var reporter);
        var task = Task(BWTaskStatus.Failed, ft);

        await handler.OnTaskCompleted(task);

        await reporter.Received(1).SendTaskCompletedAsync(Arg.Is<ScriptResult>(s =>
            !s!.Success && s.Status == "failed" && s.FailureType == ft
            && s.ErrorMessage == "boom"));
        await reporter.DidNotReceive().SendTaskStoppedAsync(Arg.Any<string>());
    }
}
