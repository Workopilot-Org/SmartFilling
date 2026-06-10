using NSubstitute;
using OpenAI.Chat;
using SmartFilling.Engine.Ai;
using SmartFilling.Engine.Engine;
using SmartFilling.Engine.Models;
using SmartFilling.Engine.Reporting;
using SmartFilling.Engine.Tests.Helpers;
using Xunit;

namespace SmartFilling.Engine.Tests.Integration;

/// <summary>
/// #4 AI 可观测性 + #5 日志格式测试。
/// #4：AiActionExecutor 每轮工具调用推前端 ReceiveLog（带 🤖 前缀，推送端加）。
/// #5：FormatStepForLog 按 action 格式化（含 step.Name/action/耗时），SendLogAsync 第二参数不再传 step.Name（[step name] 已在 message 内，默认 null）。
/// 用 NSubstitute mock ITaskProgressReporter，Received 验证 SendLogAsync 被调（断言 message 内容，不只验被调）。
/// </summary>
public class AiObservabilityTests
{
    private readonly NullLogger _logger = new();

    private static EngineOptions FastOptions() => new()
    {
        MaxScriptDuration = 600000,
        StepRetry = new StepRetry { Count = 0 },
        DefaultTimeout = 1000
    };

    [Fact]
    public async Task AiAction_EachTurn_PushesRobotLogToFrontend()
    {
        // #4：ai action 执行，AiActionExecutor 每轮推 🤖 工具日志
        var mockAi = new MockAiProvider { ShouldSucceed = true, ReturnDoneToolCall = true };
        var reporter = Substitute.For<ITaskProgressReporter>();
        var script = new ScriptBuilder()
            .AddPhase("Main", steps:
            [
                ScriptBuilder.Step("ai", description: "做点什么", maxAiTurns: 5, name: "aiStep")
            ])
            .Build();

        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, FastOptions(), mockAi, reporter);

        await engine.ExecuteAsync(script, new(), page);

        // #4：推送端给 message 加 🤖 前缀（非前端 handler 加--handler 无法区分 AI 日志）
        await reporter.Received().SendLogAsync(Arg.Is<string>(s => s!.Contains("🤖")), Arg.Any<string?>());
    }

    [Fact]
    public async Task AiFallback_Success_PushesSuccessLog()
    {
        // #4：AI 兜底成功推 ✅（AiFallbackAsync 成功路径）
        var mockAi = new MockAiProvider { ShouldSucceed = true, ReturnDoneToolCall = true };
        var reporter = Substitute.For<ITaskProgressReporter>();
        var script = new ScriptBuilder()
            .AddPhase("Main", steps:
            [
                ScriptBuilder.Step("fill", selector: "#input", value: "test", onError: "ai-fallback-stop", maxAiTurns: 3),
                ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "phase_success")
            ])
            .Build();

        MockPageFactory.CreateFailing(out var page);  // fill 失败 -> AI 兜底 -> mockAi 成功
        var engine = new ScriptEngine(_logger, FastOptions(), mockAi, reporter);

        var result = await engine.ExecuteAsync(script, new(), page);

        await reporter.Received().SendLogAsync(Arg.Is<string>(s => s!.Contains("✅") && s.Contains("AI 兜底成功")), Arg.Any<string?>());
    }

    [Fact]
    public async Task StepProgress_PushesFormattedLog_WithStepName()
    {
        // #5：step 进度推 FormatStepForLog 格式化 message（含 step.Name + action + 耗时），第二参数不再传 step.Name（[step name] 在 message 内，默认 null）
        var reporter = Substitute.For<ITaskProgressReporter>();
        var script = new ScriptBuilder()
            .AddPhase("Main", steps:
            [
                ScriptBuilder.Step("check", detect: ScriptBuilder.Detect("always"), then: "phase_success", name: "checkDone")
            ])
            .Build();

        MockPageFactory.Create(out var page);
        // 4 参带 reporter 构造（无 AI，check 不需 AI）
        var engine = new ScriptEngine(_logger, FastOptions(), reporter);

        var result = await engine.ExecuteAsync(script, new(), page);

        Assert.True(result.Success, result.ErrorMessage);
        // #5：message 含 step.Name（checkDone）+ action=check + 耗时（FormatStepForLog 输出）
        await reporter.Received().SendLogAsync(
            Arg.Is<string>(s => s!.Contains("checkDone") && s.Contains("action=check") && s.Contains("耗时")),
            Arg.Any<string?>());
    }

    [Fact]
    public async Task AiPhase_Failure_PushesFailureLog()
    {
        // TD-P10：ai phase 失败推 ❌（ExecuteAiPhaseAsync 失败路径，与 step/phase fallback 对称）
        var mockAi = new MockAiProvider { ShouldSucceed = false };  // 返无 ToolCalls -> ai phase 失败
        var reporter = Substitute.For<ITaskProgressReporter>();
        var script = new ScriptBuilder()
            .AddPhase("aiPhase", type: "ai", aiGoal: "AI 任务", maxAiTurns: 1, steps:
            [
                ScriptBuilder.Step("ai", description: "做点什么", name: "aiStep")
            ])
            .Build();

        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, FastOptions(), mockAi, reporter);

        await engine.ExecuteAsync(script, new(), page);

        await reporter.Received().SendLogAsync(Arg.Is<string>(s => s!.Contains("❌") && s.Contains("ai phase 失败")), Arg.Any<string?>());
    }

    [Fact]
    public async Task AiPhase_Success_PushesSuccessLog()
    {
        // TD-P10：ai phase 成功推 ✅（与 step/phase fallback 成功推送对称）
        var mockAi = new MockAiProvider { ShouldSucceed = true, ReturnDoneToolCall = true };
        var reporter = Substitute.For<ITaskProgressReporter>();
        var script = new ScriptBuilder()
            .AddPhase("aiPhase", type: "ai", aiGoal: "AI 任务", maxAiTurns: 5, steps:
            [
                ScriptBuilder.Step("ai", description: "做点什么", name: "aiStep")
            ])
            .Build();

        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, FastOptions(), mockAi, reporter);

        await engine.ExecuteAsync(script, new(), page);

        await reporter.Received().SendLogAsync(Arg.Is<string>(s => s!.Contains("✅") && s.Contains("ai phase 完成")), Arg.Any<string?>());
    }
}
