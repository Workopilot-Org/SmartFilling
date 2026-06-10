using OpenAI.Chat;
using SmartFilling.Engine.Ai;
using SmartFilling.Engine.Engine;
using SmartFilling.Engine.Models;
using SmartFilling.Engine.Tests.Helpers;
using Xunit;

namespace SmartFilling.Engine.Tests.Integration;

/// <summary>
/// #3 AI 兜底提示词改造测试：instruction 含 JSON 节点 + 敏感字段脱敏。
/// 验证 step fallback instruction 序列化 failedStep 为 JSON（含 "failedStep" key），
/// 敏感字段（password）value 在 instruction 中为 "(已脱敏)" 非明文（#3 B：脱敏判据 IsSensitiveField）。
/// 用 CapturingAiProvider 捕获第一次 SendMessageAsync 收到的 UserChatMessage（instruction）。
/// </summary>
public class AiInstructionMaskTests
{
    private readonly NullLogger _logger = new();

    private static EngineOptions FastOptions() => new()
    {
        MaxScriptDuration = 600000,
        StepRetry = new StepRetry { Count = 0 },
        DefaultTimeout = 1000
    };

    /// <summary>JSON 脚本（经 LoadFromJson 反序列化路径）：fill password 失败 → ai-fallback-stop。</summary>
    private const string ScriptJson = """
        {
          "version": 2, "scriptId": "test", "name": "test",
          "fields": [ { "name": "password", "label": "密码", "type": "string", "source": "system" } ],
          "phases": [
            { "kind": "phase", "name": "main", "type": "sequential", "aiGoal": "登录", "steps": [
              { "kind": "step", "name": "fillPwd", "action": "fill", "description": "填密码",
                "selector": "#pwd", "value": "{{password}}", "field": "password",
                "onError": "ai-fallback-stop", "maxAiTurns": 1 }
            ]}
          ]
        }
        """;

    [Fact]
    public async Task StepFallback_Instruction_ContainsJsonFailedStep()
    {
        var ai = new CapturingAiProvider();
        var script = ScriptLoader.LoadFromJson(ScriptJson);
        MockPageFactory.CreateFailing(out var page);  // fill 失败 → AI 兜底

        var engine = new ScriptEngine(_logger, FastOptions(), ai);
        await engine.ExecuteAsync(script, new() { { "password", "secret123" } }, page);

        Assert.NotNull(ai.FirstInstruction);
        // #3 A：instruction 含 JSON 节点（failedStep key，序列化 step 对象）
        Assert.Contains("failedStep", ai.FirstInstruction);
        Assert.Contains("error", ai.FirstInstruction);  // 错误信息节点
    }

    [Fact]
    public async Task StepFallback_Instruction_MasksSensitivePassword()
    {
        var ai = new CapturingAiProvider();
        var script = ScriptLoader.LoadFromJson(ScriptJson);
        MockPageFactory.CreateFailing(out var page);

        var engine = new ScriptEngine(_logger, FastOptions(), ai);
        await engine.ExecuteAsync(script, new() { { "password", "secret123" } }, page);

        Assert.NotNull(ai.FirstInstruction);
        // #3 B：敏感字段 value 脱敏为占位符，明文不进 AI 上下文
        Assert.True(ai.FirstInstruction.Contains("(已脱敏)"), $"期望 value=(已脱敏)，实际 instruction:\n{ai.FirstInstruction}");
        Assert.DoesNotContain("secret123", ai.FirstInstruction);  // 明文密码不在 instruction
    }

    /// <summary>#3 第 3 轮核查补：ai phase instruction 含 phase JSON payload（递归 phase + 可用数据 + 字段元数据）。</summary>
    [Fact]
    public async Task AiPhase_Instruction_ContainsPhaseJson()
    {
        var ai = new CapturingAiProvider();
        var json = """
        {
          "version": 2, "scriptId": "test", "name": "test",
          "phases": [
            { "kind": "phase", "name": "aiPhase", "type": "ai", "aiGoal": "AI 任务", "maxAiTurns": 1, "steps": [
              { "kind": "step", "name": "aiStep", "action": "ai", "description": "做点什么" }
            ]}
          ]
        }
        """;
        var script = ScriptLoader.LoadFromJson(json);
        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, FastOptions(), ai);

        await engine.ExecuteAsync(script, new(), page);

        Assert.NotNull(ai.FirstInstruction);
        Assert.Contains("\"phase\"", ai.FirstInstruction);  // ai phase payload 含 phase JSON 递归
        Assert.Contains("availableData", ai.FirstInstruction);
        Assert.Contains("fieldMeta", ai.FirstInstruction);
    }

    /// <summary>#3 第 3 轮核查补：phase fallback instruction 含 phase JSON + failedStepIndex 节点。</summary>
    [Fact]
    public async Task PhaseFallback_Instruction_ContainsPhaseJsonAndFailedStepIndex()
    {
        var ai = new CapturingAiProvider();
        var json = """
        {
          "version": 2, "scriptId": "test", "name": "test",
          "phases": [
            { "kind": "phase", "name": "main", "type": "sequential", "aiGoal": "任务", "onError": "ai-fallback-stop", "steps": [
              { "kind": "step", "name": "fillFail", "action": "fill", "description": "填", "selector": "#input", "value": "test", "onError": "stop" }
            ]}
          ]
        }
        """;
        var script = ScriptLoader.LoadFromJson(json);
        MockPageFactory.CreateFailing(out var page);  // fillFail 失败→phase 失败→phaseOnError ai-fallback-stop→PhaseAiFallbackAsync
        var engine = new ScriptEngine(_logger, FastOptions(), ai);

        await engine.ExecuteAsync(script, new(), page);

        Assert.NotNull(ai.FirstInstruction);
        Assert.Contains("\"phase\"", ai.FirstInstruction);  // phase fallback payload 含 phase
        Assert.Contains("failedStepIndex", ai.FirstInstruction);
        Assert.Contains("error", ai.FirstInstruction);
    }

    /// <summary>捕获第一次 SendMessageAsync 的 UserChatMessage 内容（instruction）。返无 ToolCalls → AiActionExecutor 多轮未完成。</summary>
    private sealed class CapturingAiProvider : IAiProvider
    {
        public string? FirstInstruction { get; private set; }

        public Task<AiResponse> SendMessageAsync(List<ChatMessage> messages, List<ChatTool>? tools = null, CancellationToken ct = default)
        {
            if (FirstInstruction == null)
            {
                var userMsg = messages.OfType<UserChatMessage>().FirstOrDefault();
                if (userMsg != null)
                    FirstInstruction = string.Concat(userMsg.Content.Select(p => p.Text));
            }
            // 无 ToolCalls → AiActionExecutor 多轮后 Success=false（maxAiTurns=1 → 1 轮退出）
            return Task.FromResult(new AiResponse { Text = "ok", InputTokens = 1, OutputTokens = 1 });
        }
    }
}
