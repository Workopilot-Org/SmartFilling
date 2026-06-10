using OpenAI.Chat;

namespace SmartFilling.Engine.Models;

public record AiResponse
{
    public string? Text { get; init; }
    public List<AiToolCall>? ToolCalls { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    /// <summary>
    /// 原始 AssistantChatMessage（可直接添加到消息历史）
    /// </summary>
    public AssistantChatMessage? RawAssistantMessage { get; init; }
    /// <summary>
    /// 保持 ParseJsonElement 返回的 JsonElement 所引用的 JsonDocument 存活，
    /// 直到 AiResponse（及其 ToolCalls.Arguments）被消费完毕。
    /// </summary>
    public List<IDisposable>? OwnedResources { get; init; }
}

public record AiToolCall
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public Dictionary<string, object>? Arguments { get; init; }
}

public record AiExecutionResult
{
    public bool Success { get; init; }
    public object? ResultValue { get; init; }
    public string? ErrorMessage { get; init; }
    public int TurnsUsed { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int SnapshotCount { get; init; }
    public int AiScreenshotCount { get; init; }
    public int SnapshotTokens { get; init; }
    public int AiCallsWithScreenshot { get; init; }
    public int ScreenshotTokens { get; init; }
}
