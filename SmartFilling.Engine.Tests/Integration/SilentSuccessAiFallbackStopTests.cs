using SmartFilling.Engine.Engine;
using SmartFilling.Engine.Models;
using SmartFilling.Engine.Tests.Helpers;
using Xunit;

namespace SmartFilling.Engine.Tests.Integration;

/// <summary>
/// #2 silent-success bug 回归测试。
/// 场景：step 级 ai-fallback-stop + AI 兜底失败 → status=failed（原仅设 ctx.AiFallbackFailed 未让 scriptFailed 生效 → status 误判 completed，silent-success）。
/// 须经生产反序列化路径（LoadFromJson JSON），断言验 status=failed + 后续 step 未执行（不只验状态——silent-success 正是"status 看似对、行为错"）。
/// </summary>
public class SilentSuccessAiFallbackStopTests
{
    private readonly NullLogger _logger = new();

    private static EngineOptions FastOptions() => new()
    {
        MaxScriptDuration = 600000,
        StepRetry = new StepRetry { Count = 0 },
        DefaultTimeout = 1000
    };

    /// <summary>JSON 脚本：fill 失败 + onError=ai-fallback-stop + maxAiTurns=1，后跟一个 check（后续 step）。
    /// 经 LoadFromJson 反序列化（生产路径），非 ScriptBuilder 强类型构造。</summary>
    private const string ScriptJson = """
        {
          "version": 2, "scriptId": "test", "name": "test",
          "fields": [ { "name": "F_X", "label": "X", "type": "string" } ],
          "phases": [
            { "kind": "phase", "name": "main", "type": "sequential", "aiGoal": "测试", "steps": [
              { "kind": "step", "name": "fillFail", "action": "fill", "description": "填",
                "selector": "#input", "value": "{{F_X}}", "field": "F_X",
                "onError": "ai-fallback-stop", "maxAiTurns": 1 },
              { "kind": "step", "name": "checkAfter", "action": "check", "description": "后续",
                "detect": { "type": "always" }, "then": "phase_success" }
            ]}
          ]
        }
        """;

    [Fact]
    public async Task StepAiFallbackStop_AiFails_StatusFailed_NotCompleted()
    {
        var mockAi = new MockAiProvider { ShouldSucceed = false };  // AI 兜底失败（返无 ToolCalls → 多轮未完成）
        var script = ScriptLoader.LoadFromJson(ScriptJson);  // 经 JSON 反序列化路径
        MockPageFactory.CreateFailing(out var page);  // fill 抛 TimeoutException → 触发 ai-fallback-stop

        var engine = new ScriptEngine(_logger, FastOptions(), mockAi);
        var result = await engine.ExecuteAsync(script, new() { { "F_X", "v" } }, page);

        // #2 核心：status=failed（原 silent-success 误判 completed）
        Assert.False(result.Success, $"期望 failed，实际 status={result.Status}（silent-success bug 未修复？）");
        Assert.Equal("failed", result.Status);
        // #2：ctx.AiFallbackFailed=true（仅 ai-fallback-stop 失败设）→ FailureType=AiFallback
        Assert.Equal(FailureType.AiFallback, result.FailureType);
    }

    [Fact]
    public async Task StepAiFallbackStop_AiFails_SubsequentStepNotExecuted()
    {
        var mockAi = new MockAiProvider { ShouldSucceed = false };
        var script = ScriptLoader.LoadFromJson(ScriptJson);
        MockPageFactory.CreateFailing(out var page);

        var engine = new ScriptEngine(_logger, FastOptions(), mockAi);
        var result = await engine.ExecuteAsync(script, new() { { "F_X", "v" } }, page);

        // 后续 checkAfter 未执行（throw 在 fillFail 传播，phase 中止）：ExecutionLog 不含 check 的成功记录
        Assert.False(result.Success);
        Assert.DoesNotContain(result.ExecutionLog, l => l.Action == "check" && l.Status == "success");
        Assert.DoesNotContain(result.ExecutionLog, l => l.StepIndex == 1);  // checkAfter 是 step index 1，未执行
    }

    /// <summary>#2 第 3 轮核查补：ai-fallback-skip 失败**不设** AiFallbackFailed → 后续他 step 确定性失败时 FailureType=Deterministic（不误判 AiFallback）。</summary>
    private const string SkipJson = """
        {
          "version": 2, "scriptId": "test", "name": "test",
          "phases": [
            { "kind": "phase", "name": "main", "type": "sequential", "aiGoal": "测试", "steps": [
              { "kind": "step", "name": "fillSkip", "action": "fill", "description": "填1",
                "selector": "#input", "value": "test", "onError": "ai-fallback-skip", "maxAiTurns": 1 },
              { "kind": "step", "name": "fillStop", "action": "fill", "description": "填2",
                "selector": "#input2", "value": "test", "onError": "stop" }
            ]}
          ]
        }
        """;

    [Fact]
    public async Task StepAiFallbackSkip_AiFails_NotSetAiFallbackFailed_FailureTypeDeterministic()
    {
        var mockAi = new MockAiProvider { ShouldSucceed = false };  // step1 AI 兜底失败
        var script = ScriptLoader.LoadFromJson(SkipJson);
        MockPageFactory.CreateFailing(out var page);  // fillSkip 失败→AI兜底失败→skip继续；fillStop 失败→stop→scriptFailed

        var engine = new ScriptEngine(_logger, FastOptions(), mockAi);
        var result = await engine.ExecuteAsync(script, new(), page);

        Assert.False(result.Success);
        // #2：ai-fallback-skip 失败不设 AiFallbackFailed → 后续 fillStop 确定性失败 → FailureType=Deterministic（不误判 AiFallback）
        Assert.Equal(FailureType.Deterministic, result.FailureType);
    }

    /// <summary>TD-P6：phase 级 ai-fallback-skip + PhaseAiFallback 失败不设 AiFallbackFailed（第 4 轮 🟡-4 修复）→ 后续 phase 确定性失败 → Deterministic（与 step 级对称）。
    /// phase1 fillFail 失败→onError=stop(step)throw→PhaseResult(false)→主循环 phaseOnError=ai-fallback-skip→PhaseAiFallbackAsync→mockAi 失败→PhaseResult(false) 不设 AiFallbackFailed→continue；
    /// phase2 fillFail2 失败→stop→scriptFailed→BuildResult 据 ctx.AiFallbackFailed(=false)→Deterministic。</summary>
    private const string PhaseSkipJson = """
        {
          "version": 2, "scriptId": "test", "name": "test",
          "phases": [
            { "kind": "phase", "name": "phase1", "type": "sequential", "aiGoal": "P1", "onError": "ai-fallback-skip", "maxAiTurns": 1, "steps": [
              { "kind": "step", "name": "fillFail", "action": "fill", "description": "填", "selector": "#input", "value": "test", "onError": "stop" }
            ]},
            { "kind": "phase", "name": "phase2", "type": "sequential", "aiGoal": "P2", "steps": [
              { "kind": "step", "name": "fillFail2", "action": "fill", "description": "填2", "selector": "#input2", "value": "test", "onError": "stop" }
            ]}
          ]
        }
        """;

    [Fact]
    public async Task PhaseAiFallbackSkip_AiFails_NotSetAiFallbackFailed_FailureTypeDeterministic()
    {
        var mockAi = new MockAiProvider { ShouldSucceed = false };  // phase1 AI 兜底失败
        var script = ScriptLoader.LoadFromJson(PhaseSkipJson);
        MockPageFactory.CreateFailing(out var page);  // phase1 fillFail 失败→phase fallback 失败→skip；phase2 fillFail2 失败→stop→scriptFailed

        var engine = new ScriptEngine(_logger, FastOptions(), mockAi);
        var result = await engine.ExecuteAsync(script, new(), page);

        Assert.False(result.Success);
        // TD-P6：phase 级 ai-fallback-skip 失败不设 AiFallbackFailed（第 4 轮 🟡-4 对称修复）→ phase2 确定性失败 → Deterministic
        Assert.Equal(FailureType.Deterministic, result.FailureType);
    }

    /// <summary>观察④修复（2026-07-06）：loop phase 内 step ai-fallback-stop + AI 兜底失败 → status=failed。
    /// #2 修复（throw 在 ExecuteStepWithRetryAndFallbackAsync:984-988）被 sequential + loop 共用；
    /// 之前仅 sequential 有测（4 Fact 都 sequential），loop 靠共用方法 + 两端 catch 对称逻辑等价，此 Fact 补 loop 运行时覆盖。</summary>
    private const string LoopScriptJson = """
        {
          "version": 2, "scriptId": "test", "name": "test",
          "fields": [ { "name": "items", "label": "项", "type": "array", "fields": [ { "name": "F_X", "label": "X", "type": "string" } ] } ],
          "phases": [
            { "kind": "phase", "name": "loop1", "type": "loop", "loopSource": "items", "aiGoal": "循环填", "steps": [
              { "kind": "step", "name": "fillFail", "action": "fill", "description": "填",
                "selector": "#input", "value": "{{F_X}}", "field": "F_X",
                "onError": "ai-fallback-stop", "maxAiTurns": 1 }
            ]}
          ]
        }
        """;

    [Fact]
    public async Task LoopPhase_StepAiFallbackStop_AiFails_StatusFailed()
    {
        var mockAi = new MockAiProvider { ShouldSucceed = false };
        var script = ScriptLoader.LoadFromJson(LoopScriptJson);
        MockPageFactory.CreateFailing(out var page);

        var engine = new ScriptEngine(_logger, FastOptions(), mockAi);
        // items 1 行 → loop 迭代 → fillFail 失败 → ai-fallback-stop → AI 兜底失败 → #2 throw → loop catch → PhaseResult(false) → scriptFailed
        var result = await engine.ExecuteAsync(script, new() { { "items", new List<object> { new Dictionary<string, object> { { "F_X", "v" } } } } }, page);

        Assert.False(result.Success, $"期望 failed，实际 status={result.Status}");
        Assert.Equal("failed", result.Status);
        Assert.Equal(FailureType.AiFallback, result.FailureType);  // step ai-fallback-stop 失败设 ctx.AiFallbackFailed → AiFallback
    }
}
