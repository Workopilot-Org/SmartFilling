using System.ClientModel;
using System.Threading;
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
    private readonly int _maxRetryCount;
    private readonly int _retryBaseInterval;
    private readonly int _circuitBreakerThreshold;
    /// <summary>
    /// 连续失败计数。App 端 OpenAiProvider 是 Singleton（多用户并发录制共享），用 Interlocked 无锁同步防竞态；
    /// Worker 端 per-task 新实例无竞态，Interlocked 无害。
    /// </summary>
    private int _consecutiveFailures;

    /// <summary>测试可观测性入口：当前连续失败计数（Volatile.Read 原子读）。</summary>
    internal int ConsecutiveFailuresForTest => Volatile.Read(ref _consecutiveFailures);

    public OpenAiProvider(AiOptions options, ILogger logger)
    {
        _logger = logger;
        var errOpts = options.ModelApiErrorOptions ?? new ModelApiErrorOptions();
        _maxRetryCount = errOpts.MaxRetryCount;
        _retryBaseInterval = errOpts.RetryBaseInterval;
        _circuitBreakerThreshold = errOpts.CircuitBreakerThreshold;

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
        var options = new ChatCompletionOptions();
        if (tools != null)
        {
            foreach (var tool in tools)
                options.Tools.Add(tool);
        }

        // attemptFactory: 一次"构建消息→调用 CompleteChatAsync→解析响应"尝试（含 Debug 打印）。
        // ChatCompletionOptions 在循环外创建一次，重试期间复用（不可变、不 disposable，复用安全）。
        return await RetryWithBreakerAsync(async (attempt, token) =>
        {
            var ownedDocs = new List<IDisposable>();
            // Debug: 打印完整消息列表（每次尝试打印一次——重试时可观测到同一批消息被重发）
            _logger.LogDebug($"[AI Messages] 发送 {messages.Count} 条消息 (尝试 {attempt + 1}/{_maxRetryCount + 1}):");
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

            var completion = await _client.CompleteChatAsync(messages, options, token);
            var response = completion.Value;

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
        }, ct);
    }

    /// <summary>
    /// 重试 + 断路器协同核心逻辑。提 internal 供单测注入"总是抛 503"的 attemptFactory 验证计数=1（防一次失败放大成 N 次计数致断路器提前误触发）。
    /// attemptFactory 第1参 attempt = 当前尝试次数（0-based）；第2参 ct 透传。
    /// </summary>
    /// <remarks>
    /// 断路器前置检查（循环前一次，重试期间不重复——provider 单线程调用，重试期间无外部增量改变 _consecutiveFailures；
    /// App 端 Singleton 并发场景用 Volatile.Read 读最新值）。断路器触发直接抛 InvalidOperationException，不进入重试循环。
    /// 共 _maxRetryCount 次重试 + 1 次初始调用（for 条件 attempt &lt;= _maxRetryCount，attempt &lt; _maxRetryCount 决定是否重试）。
    /// 协同关键：重试 continue 分支不计 _consecutiveFailures，仅"重试用尽或不可重试"计 1 次 → 一次逻辑失败 = 1 次计数。
    /// </remarks>
    internal async Task<AiResponse> RetryWithBreakerAsync(Func<int, CancellationToken, Task<AiResponse>> attemptFactory, CancellationToken ct)
    {
        // 断路器前置检查
        if (Volatile.Read(ref _consecutiveFailures) >= _circuitBreakerThreshold)
            throw new InvalidOperationException($"AI 断路器触发：连续失败 {_consecutiveFailures} 次");

        for (int attempt = 0; attempt <= _maxRetryCount; attempt++)
        {
            try
            {
                var result = await attemptFactory(attempt, ct);
                // 成功清零（原子操作）——一次成功即重置断路器连续失败计数
                Interlocked.Exchange(ref _consecutiveFailures, 0);
                return result;
            }
            catch (OperationCanceledException) { throw; }  // ct 取消，不重试不计数
            catch (InvalidOperationException) { throw; }    // 不重试不计数（含断路器自身在 try 外已拦截；此 catch 兜底 attemptFactory 抛出的其他 IOE，如 SDK 校验类错误）
            catch (Exception ex)
            {
                // 重试期间不计 _consecutiveFailures（continue 分支），仅"重试用尽或不可重试"计 1 次
                // → 保证一次逻辑失败 = 1 次计数（防一次失败放大成 MaxRetryCount+1 次致断路器提前误触发）
                // 注：ownedDocs 在 attemptFactory 内部为局部变量，抛异常时未装入 AiResponse；
                //     attemptFactory 抛异常唯一路径是 CompleteChatAsync（那时 JsonDocument.Parse 未执行，ownedDocs 为空），
                //     故此处无需释放资源。若未来 attemptFactory 在 CompleteChatAsync 成功后扩展抛异常路径，需在其内部 try-catch 释放 ownedDocs。
                if (attempt < _maxRetryCount && IsRetryable(ex))
                {
                    _logger.LogWarning($"AI 调用失败,第{attempt + 1}次重试（共{_maxRetryCount}次）: {ex.Message}");
                    await Task.Delay(ComputeBackoffDelay(_retryBaseInterval, attempt), ct);
                    continue;
                }
                // 用尽或不可重试：计 1 次并抛
                var snap = Interlocked.Increment(ref _consecutiveFailures);
                _logger.LogWarning($"AI API 调用失败 ({snap}/{_circuitBreakerThreshold}): {ex.Message}");
                throw;
            }
        }

        // 理论不可达：for 循环必经 return 或 throw。兜底防编译器"非所有路径返回"告警。
        throw new InvalidOperationException("AI 调用重试循环异常退出");
    }

    /// <summary>
    /// 重置断路器连续失败计数。供总结等"失败不影响主流程"的路径调用，
    /// 防止 OpenAiProvider（App 端 Singleton）累积触发断路器后阻塞主流程录制。
    /// </summary>
    public void ResetBreaker() => Interlocked.Exchange(ref _consecutiveFailures, 0);

    /// <summary>
    /// 判定异常是否可重试。分类：
    /// - OperationCanceledException / InvalidOperationException：在 catch 子句中硬拦截，不进入此方法（保守 false 兜底）。
    /// - ClientResultException（OpenAI SDK，基于 System.ClientModel）：按 .Status 分类，429/5xx 可重试，4xx(非429) 不可重试。
    /// - HttpRequestException：.StatusCode 有值按 HTTP 码分类；为 null（纯网络层，无 status）可重试。
    /// - 其他未知：保守 false。
    /// </summary>
    internal static bool IsRetryable(Exception ex)
    {
        // ct 取消 / 断路器自身：不重试（catch 硬拦截，此处兜底）
        if (ex is OperationCanceledException) return false;
        if (ex is InvalidOperationException) return false;

        // HttpRequestException：优先按 StatusCode 分类（OpenAI SDK 偶发带 status 的 HTTP 层异常）
        if (ex is System.Net.Http.HttpRequestException hre)
        {
            if (!hre.StatusCode.HasValue) return true;  // 纯网络层（连接失败/抖动），无 status → 可重试
            return IsRetryableStatus((int)hre.StatusCode.Value);
        }

        // ClientResultException：OpenAI SDK 2.9.1 基于 System.ClientModel 的标准异常（有 .Status int 取 HTTP 码）
        if (ex is ClientResultException cre)
        {
            return IsRetryableStatus(cre.Status);
        }

        // 保守不重试
        return false;
    }

    /// <summary>HTTP status 可重试判定：429（限流）/ 5xx（服务端临时故障）可重试，其余不可重试。提 internal 供测试直测 429/5xx/4xx 分类。</summary>
    internal static bool IsRetryableStatus(int status) => status == 429 || (status >= 500 && status < 600);

    /// <summary>
    /// 指数退避计算：baseMs * 2^attempt，封顶 60000ms 防过长。
    /// attempt=0 → baseMs；attempt=1 → baseMs*2；attempt=2 → baseMs*4 ...
    /// 用位移 1&lt;&lt;attempt 计算 2^attempt（attempt&lt;=30 内安全，C# int 位移 31 位以上溢出，但 _maxRetryCount 不会到 30）。
    /// </summary>
    internal static int ComputeBackoffDelay(int baseMs, int attempt) =>
        Math.Min(baseMs * (1 << attempt), 60000);

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
