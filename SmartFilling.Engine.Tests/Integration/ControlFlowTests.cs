using SmartFilling.Engine.Engine;
using SmartFilling.Engine.Models;
using SmartFilling.Engine.Tests.Helpers;
using Xunit;

namespace SmartFilling.Engine.Tests.Integration;

/// <summary>
/// 控制流集成测试：check then 值、phase condition、嵌套、超时/取消、onError。
/// 所有 step 使用 check(always) 不需要真实 DOM 操作。
/// </summary>
public class ControlFlowTests
{
    private readonly NullLogger _logger = new();

    private static EngineOptions DefaultOptions() => new()
    {
        MaxScriptDuration = 600000,
        StepRetry = new StepRetry { Count = 0 }
    };

    #region Check Then 值

    [Fact]
    public async Task Check_Continue_ProceedsToNextStep()
    {
        // then=continue 后继续执行下一个 check(always) then=phase_success
        var script = new ScriptBuilder()
            .AddPhase("Main", steps:
            [
                ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "continue"),
                ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "phase_success")
            ])
            .Build();

        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, DefaultOptions());

        var result = await engine.ExecuteAsync(script, new(), page);

        Assert.True(result.Success, result.ErrorMessage);
    }

    [Fact]
    public async Task Check_PhaseSuccess_EndsPhase()
    {
        // then=phase_success，当前 phase 提前结束，result.Success=true
        var script = new ScriptBuilder()
            .AddPhase("Main", steps:
            [
                ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "phase_success")
            ])
            .Build();

        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, DefaultOptions());

        var result = await engine.ExecuteAsync(script, new(), page);

        Assert.True(result.Success, result.ErrorMessage);
    }

    [Fact]
    public async Task Check_PhaseSuccess_WithBillCode()
    {
        // 改动5：billCode 从 step 级（CheckStep.billCode，已删）移到脚本顶层 returnData。
        // extract storeAs=billCode 存入 vars；脚本 returnData 声明 billCode:"{{billCode}}" 引用 vars 原始值组装返回。
        var script = new ScriptBuilder()
            .AddPhase("Main", steps:
            [
                ScriptBuilder.Step("extract", extractType: "url", storeAs: "billCode"),
                ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "phase_success")
            ])
            .WithReturnData(new Dictionary<string, object> { ["billCode"] = "{{billCode}}" })
            .Build();

        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, DefaultOptions());

        var result = await engine.ExecuteAsync(script, new(), page);

        Assert.True(result.Success, result.ErrorMessage);
        // extract url 返回 "about:blank"，通过 storeAs 存入 vars
        Assert.Equal("about:blank", result.Vars?["billCode"]?.ToString());
        // 改动5：脚本级 returnData 从 vars 组装，billCode 引用取原始值
        Assert.Equal("about:blank", result.ReturnData?["billCode"]?.ToString());
    }

    [Fact]
    public async Task Check_PhaseFail_EndsPhaseFail()
    {
        // then=phase_fail + message，当前 phase 失败
        var script = new ScriptBuilder()
            .AddPhase("Main", onError: "skip", steps:
            [
                ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "phase_fail",
                    message: "业务校验失败")
            ])
            .Build();

        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, DefaultOptions());

        var result = await engine.ExecuteAsync(script, new(), page);

        // phase_fail 使当前 phase 返回失败，但由于外层没有后续 phase，仍为 success
        // 实际上 ExecuteAsync 中 phase 失败后检查 GetOnError，onError=skip 则 continue
        // phase_fail 的 PhaseResult.Success=false，主循环中 r?.Success == false，onError=skip 则继续
        // 但 phase_fail 返回后不会再执行后续步骤
        Assert.True(result.Success);
    }

    [Fact]
    public async Task Check_ScriptSuccess_EndsScript()
    {
        // then=script_success，后续 phase 不执行
        var script = new ScriptBuilder()
            .AddPhase("A", steps:
            [
                ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "script_success")
            ])
            .AddPhase("B", steps:
            [
                ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "phase_success")
            ])
            .Build();

        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, DefaultOptions());

        var result = await engine.ExecuteAsync(script, new(), page);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("completed", result.Status);
        // Phase B 不应被执行
        Assert.DoesNotContain(result.ExecutionLog, l => l.PhaseName == "B");
    }

    [Fact]
    public async Task Check_ScriptFail_EndsScriptFail()
    {
        // then=script_fail 使脚本立即终止
        // 引擎行为：script_fail 的 PhaseResult.Success=false，主循环 break，返回 Success=true
        // 后续 phase 不应被执行
        var script = new ScriptBuilder()
            .AddPhase("Main", steps:
            [
                ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "script_fail",
                    message: "致命错误终止")
            ])
            .AddPhase("B", steps:
            [
                ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "phase_success")
            ])
            .Build();

        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, DefaultOptions());

        var result = await engine.ExecuteAsync(script, new(), page);

        // S5 修复（phase 失败传播）：script_fail → scriptFailed=true → 脚本失败 Success=false（原"误判成功"bug 已修）
        Assert.False(result.Success);
        // Phase B 不应被执行
        Assert.DoesNotContain(result.ExecutionLog, l => l.PhaseName == "B");
    }

    #endregion

    #region Phase Condition

    [Fact]
    public async Task Phase_ConditionMet_Executes()
    {
        // phase condition=always 执行
        var script = new ScriptBuilder()
            .AddPhase("Main", condition: ScriptBuilder.Detect("always"), steps:
            [
                ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "phase_success")
            ])
            .Build();

        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, DefaultOptions());

        var result = await engine.ExecuteAsync(script, new(), page);

        Assert.True(result.Success, result.ErrorMessage);
    }

    [Fact]
    public async Task Phase_ConditionNotMet_Skips()
    {
        // phase condition=data_exists(field="missing") 不满足，phase 跳过
        var script = new ScriptBuilder()
            .AddPhase("Main", condition: ScriptBuilder.Detect("data_exists", field: "missing"), steps:
            [
                ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "phase_success")
            ])
            .Build();

        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, DefaultOptions());

        var result = await engine.ExecuteAsync(script, new(), page);

        // Phase 被跳过，没有 phase_success 产生，但脚本本身仍视为完成
        Assert.True(result.Success);
        Assert.Empty(result.ExecutionLog);
    }

    #endregion

    #region OnError

    [Fact]
    public async Task OnError_Stop_TerminatesScript()
    {
        // phase onError=stop，step 失败（MockLocator 抛异常）后终止脚本
        var script = new ScriptBuilder()
            .AddPhase("Main", onError: "stop", steps:
            [
                ScriptBuilder.Step("fill", selector: "#input", value: "test"),
                ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "phase_success")
            ])
            .Build();

        MockPageFactory.CreateFailing(out var page);
        var engine = new ScriptEngine(_logger, DefaultOptions());

        var result = await engine.ExecuteAsync(script, new(), page);

        Assert.False(result.Success);
    }

    #endregion

    #region Loop Then 值

    [Fact]
    public async Task Check_ThenNext_InLoop_Iterates()
    {
        // loop phase 中 then=next 跳到下一行，遍历完所有行后 phase 成功
        var script = new ScriptBuilder()
            .AddPhase("Loop", type: "loop", loopSource: "items", steps:
            [
                ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "next")
            ])
            .Build();

        var fillData = new Dictionary<string, object>
        {
            { "items", new List<Dictionary<string, object>>
            {
                new() { { "name", "row1" } },
                new() { { "name", "row2" } },
                new() { { "name", "row3" } }
            }
            }
        };

        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, DefaultOptions());

        var result = await engine.ExecuteAsync(script, fillData, page);

        Assert.True(result.Success, result.ErrorMessage);
    }

    [Fact]
    public async Task Check_ThenBreak_InLoop_ExitsLoop()
    {
        // loop phase 中 then=break 退出循环
        var script = new ScriptBuilder()
            .AddPhase("Loop", type: "loop", loopSource: "items", steps:
            [
                ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "break")
            ])
            .Build();

        var fillData = new Dictionary<string, object>
        {
            { "items", new List<Dictionary<string, object>>
            {
                new() { { "name", "row1" } },
                new() { { "name", "row2" } },
                new() { { "name", "row3" } }
            }
            }
        };

        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, DefaultOptions());

        var result = await engine.ExecuteAsync(script, fillData, page);

        Assert.True(result.Success, result.ErrorMessage);
    }

    #endregion
}
