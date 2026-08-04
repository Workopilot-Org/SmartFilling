using SmartFilling.Engine.Engine;
using SmartFilling.Engine.Models;
using SmartFilling.Engine.Tests.Helpers;
using Xunit;

namespace SmartFilling.Engine.Tests.Integration;

/// <summary>
/// goto action（step.Action == "goto"）执行测试。区别于 GotoIntegrationTests（check then: "goto"）。
/// 验证 control flow 块条件去 step.Action == "check" 限制（L336/L545）后，goto action 的 ControlFlow 被正确处理。
/// 既有 bug：goto action 产生 ControlFlow 但 control flow 块条件限 step.Action == "check"，goto 不进入，
/// ControlFlow 丢弃，goto 不跳转（走到插入点 j++）。
/// </summary>
public class GotoActionTests
{
    private readonly NullLogger _logger = new();

    private static EngineOptions DefaultOptions(int maxGoto = 20) => new()
    {
        MaxPhaseGotoCount = maxGoto,
        MaxScriptDuration = 600000,
        StepRetry = new StepRetry { Count = 0 }
    };

    [Fact]
    public async Task GotoAction_ToStep_SkipsIntermediateSteps()
    {
        // goto action toStep 跳过中间 phase_error check，直接到 target
        // 既有 bug：goto 不跳转 → phase_error check 执行 → phase 失败（result.Success=false）
        // 修复后：goto 跳转 → phase_error check 跳过 → phase 成功（result.Success=true）
        var script = new ScriptBuilder()
            .AddPhase("A", steps:
            [
                ScriptBuilder.Step("goto", toStep: "target", name: "gotoTarget"),
                ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "phase_error", message: "should be skipped"),
                ScriptBuilder.Step("fill", selector: "//x", value: "a", name: "target")
            ])
            .Build();

        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, DefaultOptions());

        var result = await engine.ExecuteAsync(script, new(), page);

        Assert.True(result.Success, result.ErrorMessage);
        // goto 跳过 index 1（phase_error check），直接到 index 2（target fill）
        Assert.DoesNotContain(result.ExecutionLog, l => l.StepIndex == 1 && l.Action == "check");
        Assert.Contains(result.ExecutionLog, l => l.StepIndex == 2 && l.Action == "fill");
    }

    [Fact]
    public async Task GotoAction_ToPhase_JumpsCorrectly()
    {
        // goto action toPhase 递归执行目标 phase（HandleGotoAsync）
        // 既有 bug：goto 不跳转 → phase C 只由主循环执行（ExecutionLog Count(C)==1）
        // 修复后：goto 跳转 → phase C 递归执行 + 主循环执行（ExecutionLog Count(C)>=2）
        var script = new ScriptBuilder()
            .AddPhase("A", steps:
            [
                ScriptBuilder.Step("goto", toPhase: "C", name: "gotoC")
            ])
            .AddPhase("B", steps:
            [
                ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "phase_success")
            ])
            .AddPhase("C", steps:
            [
                ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "phase_success")
            ])
            .Build();

        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, DefaultOptions());

        var result = await engine.ExecuteAsync(script, new(), page);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains(result.ExecutionLog, l => l.PhaseName == "A");
        // goto 递归执行 phase C + 主循环执行 phase C = ≥2 次
        Assert.True(result.ExecutionLog.Count(l => l.PhaseName == "C") >= 2,
            $"期望 phase C 出现 ≥2 次（递归+主循环），实际 {result.ExecutionLog.Count(l => l.PhaseName == "C")} 次");
    }
}
