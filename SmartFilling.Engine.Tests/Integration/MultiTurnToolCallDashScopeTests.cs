using System.Text.Json;
using OpenAI.Chat;
using SmartFilling.Engine.Ai;
using SmartFilling.Engine.Models;
using SmartFilling.Engine.Tests.Helpers;
using Xunit;

namespace SmartFilling.Engine.Tests.Integration;

/// <summary>
/// #27 验证（真实 DashScope）：多轮 tool call 历史重建——
/// OpenAiProvider:118 RawAssistantMessage = new AssistantChatMessage(ChatCompletion) 是否保留 tool_calls、
/// DashScope 第 2 轮是否拒绝（tool result without tool call）。
/// 读 SmartFilling.App/appsettings.Development.json 的 Ai:ApiKey；无 key 则跳过（不 fail）。
/// </summary>
public class MultiTurnToolCallDashScopeTests
{
    private readonly NullLogger _logger = new();

    private static AiOptions? LoadAiOptions()
    {
        // Engine.Tests/bin/Debug/net8.0 → 上 4 级到 repo root → SmartFilling.App/appsettings.Development.json
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "SmartFilling.App", "appsettings.Development.json");
        if (!File.Exists(path))
        {
            Console.WriteLine($"[Skip] 找不到配置: {path}");
            return null;
        }
        var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("AiProvider", out var ai)) return null;
        var apiKey = ai.TryGetProperty("ApiKey", out var k) ? k.GetString() : null;
        if (string.IsNullOrEmpty(apiKey))
        {
            Console.WriteLine("[Skip] AiProvider:ApiKey 为空");
            return null;
        }
        // Development 只覆盖 ApiKey；ModelId/Endpoint 用 base 值（appsettings.json 的 AiProvider 段）
        return new AiOptions
        {
            ApiKey = apiKey!,
            ModelId = ai.TryGetProperty("ModelId", out var m) ? (m.GetString() ?? "deepseek-v3.2") : "deepseek-v3.2",
            Endpoint = ai.TryGetProperty("Endpoint", out var e) ? (e.GetString() ?? "https://dashscope.aliyuncs.com/compatible-mode/v1") : "https://dashscope.aliyuncs.com/compatible-mode/v1",
            CircuitBreakerThreshold = 100
        };
    }

    [Fact]
    public async Task MultiTurnToolCall_SecondTurnNotRejected()
    {
        var options = LoadAiOptions();
        if (options == null) return; // 无 key 跳过

        var provider = new OpenAiProvider(options, _logger);

        var tools = new List<ChatTool>
        {
            ChatTool.CreateFunctionTool(
                "get_time",
                "返回当前时间字符串",
                BinaryData.FromString("{\"type\":\"object\",\"properties\":{}}"))
        };

        // 第 1 轮：要求用工具
        var messages = new List<ChatMessage>
        {
            new UserChatMessage("请调用 get_time 工具告诉我现在时间")
        };

        var resp1 = await provider.SendMessageAsync(messages, tools);
        Assert.NotNull(resp1.ToolCalls);
        Assert.NotEmpty(resp1.ToolCalls);  // DashScope + deepseek-v3.2 应返回 tool_call
        Console.WriteLine($"[Turn1] 返回 {resp1.ToolCalls!.Count} 个 tool_call: {string.Join(",", resp1.ToolCalls.Select(t => t.Name))}");

        // 历史重建（SDK 方式，复刻 AiActionExecutor:95）
        Assert.NotNull(resp1.RawAssistantMessage);  // OpenAiProvider:118 new AssistantChatMessage(ChatCompletion) 应非 null
        messages.Add(resp1.RawAssistantMessage!);
        foreach (var tc in resp1.ToolCalls)
            messages.Add(new ToolChatMessage(tc.Id, "2026-06-18 12:00:00"));

        // 诊断：第 2 轮前的 assistant 消息应含 tool_calls（SDK 重建是否保留）
        var asst = messages.OfType<AssistantChatMessage>().LastOrDefault();
        var tcCount = asst?.ToolCalls?.Count ?? 0;
        Console.WriteLine($"[Hist] 第 2 轮前 assistant 消息 tool_calls 数 = {tcCount}");
        Assert.True(tcCount > 0, "第 2 轮前的 assistant 消息应含 tool_calls（SDK 重建保留）—— 若为 0 说明 new AssistantChatMessage(ChatCompletion) 丢失 tool_calls");

        // 第 2 轮（关键：不被 DashScope 拒 = tool_calls 正确传递）
        var resp2 = await provider.SendMessageAsync(messages, tools);
        Assert.NotNull(resp2);  // 不抛即第 2 轮被接受
        Console.WriteLine($"[Turn2] 成功，返回 {(resp2.ToolCalls?.Count > 0 ? $"{resp2.ToolCalls.Count} 个 tool_call" : "文本: " + (resp2.Text ?? "")[..Math.Min(50, (resp2.Text ?? "").Length)])}");
    }
}
