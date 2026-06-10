using System.ClientModel;
using OpenAI.Chat;
using SmartFilling.Engine.Logging;
using SmartFilling.Engine.Models;

namespace SmartFilling.Engine.Ai;

/// <summary>
/// OpenAI 兼容实现。通过配置 Endpoint 指向 DashScope 或其他兼容端点。
/// </summary>
public class OpenAiProvider : IAiProvider
{
    private readonly ChatClient _client;
    private readonly ILogger _logger;
    private int _consecutiveFailures;
    private readonly int _circuitBreakerThreshold;

    public OpenAiProvider(AiOptions options, ILogger logger)
    {
        _logger = logger;
        _circuitBreakerThreshold = options.CircuitBreakerThreshold;

        _client = new ChatClient(
            model: options.ModelId,
            credential: new ApiKeyCredential(options.ApiKey),
            options: new OpenAI.OpenAIClientOptions
            {
                Endpoint = string.IsNullOrEmpty(options.Endpoint) ? null : new Uri(options.Endpoint)
            }
        );
    }

    public async Task<AiResponse> SendMessageAsync(
        List<ChatMessage> messages,
        List<ChatTool>? tools = null,
        CancellationToken ct = default)
    {
        // 断路器检查
        if (_consecutiveFailures >= _circuitBreakerThreshold)
            throw new InvalidOperationException($"AI 断路器触发：连续失败 {_consecutiveFailures} 次");

        var ownedDocs = new List<IDisposable>();
        var options = new ChatCompletionOptions();
        if (tools != null)
        {
            foreach (var tool in tools)
                options.Tools.Add(tool);
        }

        try
        {
            // Debug: 打印完整消息列表
            _logger.LogDebug($"[AI Messages] 发送 {messages.Count} 条消息:");
            for (int i = 0; i < messages.Count; i++)
            {
                var msg = messages[i];
                var role = msg.GetType().Name;
                string content;
                if (msg is SystemChatMessage sys)
                    content = sys.Content?[0]?.Text ?? "";
                else if (msg is UserChatMessage user)
                    content = user.Content?[0]?.Text ?? "[image]";
                else if (msg is AssistantChatMessage asst)
                    content = asst.Content?[0]?.Text ?? (asst.ToolCalls?.Count > 0 ? $"[{asst.ToolCalls.Count} tool_calls]" : "");
                else if (msg is ToolChatMessage tool)
                    content = $"({tool.ToolCallId}) {tool.Content?[0]?.Text ?? ""}";
                else
                    content = msg.ToString() ?? "";
                _logger.LogDebug($"  [{i + 1}] {role}: \"{content}\"");
            }

            var completion = await _client.CompleteChatAsync(messages, options, ct);
            var response = completion.Value;

            _consecutiveFailures = 0; // 成功后重置

            var text = response.Content.FirstOrDefault()?.Text;
            var inputTokens = response.Usage?.InputTokenCount ?? 0;
            var outputTokens = response.Usage?.OutputTokenCount ?? 0;

            // 解析工具调用
            List<AiToolCall>? toolCalls = null;
            if (response.FinishReason == ChatFinishReason.ToolCalls && response.ToolCalls.Count > 0)
            {
                toolCalls = [];
                foreach (var tc in response.ToolCalls)
                {
                    var args = new Dictionary<string, object>();
                    if (tc.FunctionArguments != null)
                    {
                        try
                        {
                            var json = System.Text.Json.JsonDocument.Parse(tc.FunctionArguments.ToString());
                            ownedDocs.Add(json);
                            foreach (var prop in json.RootElement.EnumerateObject())
                            {
                                args[prop.Name] = ParseJsonElement(prop.Value);
                            }
                        }
                        catch { /* 解析失败时忽略 */ }
                    }

                    toolCalls.Add(new AiToolCall
                    {
                        Id = tc.Id,
                        Name = tc.FunctionName,
                        Arguments = args
                    });
                }
            }

            return new AiResponse
            {
                Text = text,
                ToolCalls = toolCalls,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                RawAssistantMessage = new AssistantChatMessage(response),
                OwnedResources = ownedDocs
            };
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            _logger.LogWarning($"AI API 调用失败 ({_consecutiveFailures}/{_circuitBreakerThreshold}): {ex.Message}");
            throw;
        }
    }

    private static object ParseJsonElement(System.Text.Json.JsonElement element)
    {
        return element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => element.GetString() ?? "",
            // Number/Object/Array 保持 JsonElement，消费端按需提取
            System.Text.Json.JsonValueKind.Number => element,
            System.Text.Json.JsonValueKind.Object => element,
            System.Text.Json.JsonValueKind.Array => element,
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.Null => "",
            _ => element.ToString()
        };
    }
}
