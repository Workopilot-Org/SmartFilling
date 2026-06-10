using SmartFilling.Engine.Ai;
using SmartFilling.Engine.Models;
using OpenAI.Chat;

namespace SmartFilling.Engine.Tests.Helpers;

/// <summary>
/// Mock IAiProvider，用于测试中控制 AI 行为而不依赖真实 API。
/// </summary>
public class MockAiProvider : IAiProvider
{
    public bool ShouldSucceed { get; set; } = true;
    public string ResultText { get; set; } = "done";
    public bool ReturnDoneToolCall { get; set; } = true;
    public int CallCount { get; private set; }

    public Task<AiResponse> SendMessageAsync(
        List<ChatMessage> messages,
        List<ChatTool>? tools = null,
        CancellationToken ct = default)
    {
        CallCount++;

        if (!ShouldSucceed)
        {
            return Task.FromResult(new AiResponse
            {
                Text = "error",
                InputTokens = 0,
                OutputTokens = 0
            });
        }

        if (ReturnDoneToolCall)
        {
            return Task.FromResult(new AiResponse
            {
                Text = null,
                ToolCalls =
                [
                    new AiToolCall
                    {
                        Id = "call_done",
                        Name = "done",
                        Arguments = new Dictionary<string, object> { { "result", "ok" } }
                    }
                ],
                InputTokens = 100,
                OutputTokens = 50,
                RawAssistantMessage = null
            });
        }

        return Task.FromResult(new AiResponse
        {
            Text = ResultText,
            InputTokens = 100,
            OutputTokens = 50,
            RawAssistantMessage = null
        });
    }
}
