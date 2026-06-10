using NSubstitute;
using SmartFilling.Engine.Engine;
using SmartFilling.Engine.Logging;
using SmartFilling.Engine.Models;
using SmartFilling.Engine.Reporting;
using SmartFilling.Engine.Tests.Helpers;
using Xunit;

namespace SmartFilling.Engine.Tests.Integration;

/// <summary>
/// P3-10：engine 上报行为单测--P3-1 后 engine 退化为只发截图，完成事件（SendTaskCompletedAsync）
/// 由 completionHandler（TaskExecutionService.finally 的 OnTaskCompleted）单一出口发，engine 不再调。
/// </summary>
public class EngineCompletionReportTests
{
    private static EngineOptions Options() => new()
    {
        MaxScriptDuration = 600000,
        MaxLoopCount = 100,
        StepRetry = new StepRetry { Count = 0 }
    };

    [Fact]
    public async Task Engine_DoesNotSendTaskCompleted_PostP3()
    {
        var reporter = Substitute.For<ITaskProgressReporter>();
        var engine = new ScriptEngine(new NullLogger(), Options(), reporter);

        var script = new ScriptBuilder()
            .AddPhase("main", aiGoal: "测试", steps: [ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "phase_success")])
            .Build();
        MockPageFactory.Create(out var page);

        var result = await engine.ExecuteAsync(script, new(), page);

        Assert.True(result.Success, result.ErrorMessage);
        // P3-1：engine 不再发完成事件（移交 completionHandler）
        await reporter.DidNotReceive().SendTaskCompletedAsync(Arg.Any<ScriptResult>());
    }

    [Fact]
    public async Task Engine_DoesNotSendTaskStopped_OnFailure_PostP3()
    {
        var reporter = Substitute.For<ITaskProgressReporter>();
        var engine = new ScriptEngine(new NullLogger(), Options(), reporter);

        // script_fail -> engine 失败，但不再发 SendTaskStoppedAsync（completionHandler 按 FailureType 分发）
        var script = new ScriptBuilder()
            .AddPhase("main", aiGoal: "测试", steps: [ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "script_fail", message: "失败终止")])
            .Build();
        MockPageFactory.Create(out var page);

        var result = await engine.ExecuteAsync(script, new(), page);

        Assert.False(result.Success);
        await reporter.DidNotReceive().SendTaskStoppedAsync(Arg.Any<string>());
        await reporter.DidNotReceive().SendTaskCompletedAsync(Arg.Any<ScriptResult>());
    }

    [Fact]
    public async Task ReturnData_PluckArrayField_ProducesIdList()
    {
        // loopSource 必补#1：④ 改 ResolveRawValue（PluckPath）后，returnData 的 ResolveReturnDataValue 也调它 ->
        // returnData:{ids:"{{detailtable.id}}"} 取 id 列数组（功能扩展非破坏，回归保护）。
        var reporter = Substitute.For<ITaskProgressReporter>();
        var engine = new ScriptEngine(new NullLogger(), Options(), reporter);
        var fillData = new Dictionary<string, object>
        {
            { "detailtable", new List<object>
                {
                    new Dictionary<string, object> { { "id", "A" } },
                    new Dictionary<string, object> { { "id", "B" } }
                }
            }
        };
        var script = new ScriptBuilder()
            .AddPhase("main", aiGoal: "测试", steps: [ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "phase_success")])
            .WithReturnData(new() { ["ids"] = "{{detailtable.id}}" })
            .Build();
        MockPageFactory.Create(out var page);

        var result = await engine.ExecuteAsync(script, fillData, page);

        Assert.True(result.Success, result.ErrorMessage);
        var ids = Assert.IsType<List<object>>(result.ReturnData!["ids"]);
        Assert.Equal(new[] { "A", "B" }, ids);
    }

    [Fact]
    public async Task ReturnData_UnresolvedPlaceholderOnSuccess_FallsBackToEmpty()
    {
        // 修订6/修订7：成功路径未命中占位符（如 {{lastError}} 仅失败路径写 vars）兜底返 ""，不留字面 {{}}。
        // 深挖核查（2026-06-28）补：保护 ResolveReturnDataValue 的 IsMatch->"" 行为。
        var reporter = Substitute.For<ITaskProgressReporter>();
        var engine = new ScriptEngine(new NullLogger(), Options(), reporter);
        var script = new ScriptBuilder()
            .AddPhase("main", aiGoal: "测试", steps: [ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "phase_success")])
            .WithReturnData(new() { ["err"] = "{{lastError}}" })
            .Build();
        MockPageFactory.Create(out var page);

        var result = await engine.ExecuteAsync(script, new(), page);

        Assert.True(result.Success, result.ErrorMessage);
        // 成功路径 lastError 未写 vars -> 占位符未命中 -> 兜底返 ""（非字面 "{{lastError}}"）
        Assert.Equal("", result.ReturnData!["err"]!.ToString());
    }
}
