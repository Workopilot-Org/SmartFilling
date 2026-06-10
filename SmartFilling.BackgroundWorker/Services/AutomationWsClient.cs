using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using EngineILogger = SmartFilling.Engine.Logging.ILogger;
using SmartFilling.BackgroundWorker.Configuration;
using SmartFilling.BackgroundWorker.Models;
using SmartFilling.Engine.Models;
using ModelTaskStatus = SmartFilling.BackgroundWorker.Models.TaskStatus;

namespace SmartFilling.BackgroundWorker.Services;

/// <summary>
/// WS 客户端核心 — 从 v1 AutomationWsClient 复制改造。
/// 保留 ~60%：连接管理、心跳、缓冲、幂等、密码解密、设备上线、任务退回。
/// 改造 ~40%：HandleExecuteTask 改用 v2 FillTask + ScriptV2，移除 MCP 引用。
/// </summary>
public class AutomationWsClient : IHostedService
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AutomationWsClient> _logger;
    private readonly WsClientOptions _options;
    private readonly TaskConcurrencyManager _concurrencyManager;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _pollLock = new(1, 1);

    private ClientWebSocket? _ws;
    private readonly List<string> _bufferList = [];
    private readonly object _bufferLock = new();
    private readonly ConcurrentDictionary<string, DateTime> _processedMsgIds = new();
    /// <summary>R5-5：消息去重记录保留窗口，超期清理避免长运行会话无界内存增长</summary>
    private static readonly TimeSpan _processedMsgRetention = TimeSpan.FromHours(1);
    private readonly TaskOrchestrationService _orchestrationService;  // #6：取消统一入口（替代 _taskCancellations 双轨，runner 监听 orchestration 的 taskCt）

    private Timer? _heartbeatTimer;
    private Timer? _pollTimer;
    private CancellationTokenSource? _loopCts;

    private long _lastPongTicks = DateTime.UtcNow.Ticks;  // R2-1：用 long ticks + Interlocked 保证跨线程原子（DateTime 是 struct，Volatile.Write/Read 仅支持引用类型）
    private int _pongTimeoutCount;

    public string ClientId => _options.ClientId;

    public AutomationWsClient(
        IServiceScopeFactory scopeFactory,
        ILogger<AutomationWsClient> logger,
        IOptions<WsClientOptions> wsOptions,
        TaskConcurrencyManager concurrencyManager,
        TaskOrchestrationService orchestrationService)  // #6：补注入（HandleTerminateTask 改调 CancelByTaskId 统一取消入口）
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = wsOptions.Value;
        _concurrencyManager = concurrencyManager;
        _orchestrationService = orchestrationService;
    }

    // ===== IHostedService =====

    public async Task StartAsync(CancellationToken ct)
    {
        _logger.LogInformation("WS 客户端启动，目标: {Url}, ClientId: {ClientId}", _options.ServerUrl, _options.ClientId);
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = ConnectWithRetryAsync(_loopCts.Token);
        await Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _logger.LogInformation("WS 客户端停止...");
        _heartbeatTimer?.Dispose();
        _pollTimer?.Dispose();
        _loopCts?.Cancel();

        if (_ws?.State == WebSocketState.Open)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "应用关闭", ct); }
            catch { }
        }
        _ws?.Dispose();
    }

    // ===== 连接与重连 =====

    private async Task ConnectWithRetryAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _ws?.Dispose();
                _ws = new ClientWebSocket();

                await _ws.ConnectAsync(new Uri(_options.ServerUrl), ct);
                _logger.LogInformation("WS 连接成功");
                attempt = 0;

                await SendAsync(new WsMessage { Action = "DEVICE_ONLINE", ClientId = _options.ClientId }, ct);

                StartHeartbeat();
                StartPollTimer();
                await FlushBufferAsync(ct);
                await ReceiveLoopAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                attempt++;
                var delay = Math.Min(_options.ReconnectIntervalSeconds * Math.Pow(2, attempt - 1), 60);
                delay += Random.Shared.NextDouble() * 1.0;
                _logger.LogWarning("WS 断开，第 {Attempt} 次重连，{Delay:F1}s 后重试。原因: {Error}", attempt, delay, ex.Message);

                StopHeartbeat();
                StopPollTimer();

                try { await Task.Delay(TimeSpan.FromSeconds(delay), ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    // ===== 消息发送 =====

    public async Task SendAsync(WsMessage message, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(message.ClientId))
            message.ClientId = _options.ClientId;
        var json = JsonSerializer.Serialize(message, _jsonOptions);

        await _sendLock.WaitAsync(ct);
        try
        {
            if (_ws?.State == WebSocketState.Open)
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
            }
            else
            {
                lock (_bufferLock)
                {
                    if (_bufferList.Count >= _options.MaxDisconnectBufferMessages)
                        _bufferList.RemoveAt(0);
                    _bufferList.Add(json);
                }
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // ===== 消息接收 =====

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close)
                return;

            var json = Encoding.UTF8.GetString(ms.ToArray());
            await HandleMessageAsync(json, ct);
        }
    }

    private async Task HandleMessageAsync(string json, CancellationToken ct)
    {
        try
        {
            var message = JsonSerializer.Deserialize<WsMessage>(json, SmartFilling.Engine.HttpJsonOptions.CaseInsensitive);  // F7：#20 防御性 caseInsensitive（对平台 WS 消息 PascalCase 安全兼容）
            if (message == null || string.IsNullOrEmpty(message.Action)) return;

            // 幂等检查
            if (!string.IsNullOrEmpty(message.MsgId) && !_processedMsgIds.TryAdd(message.MsgId, DateTime.UtcNow))
                return;

            switch (message.Action)
            {
                case "EXECUTE_TASK":
                    _pollTcs?.TrySetResult(true);
                    try
                    {
                        var execPayload = DeserializePayload<ExecuteTaskPayload>(message.Payload);
                        if (execPayload != null)
                            _ = HandleExecuteTask(execPayload, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "EXECUTE_TASK 消息处理异常");
                        try
                        {
                            using var doc = JsonDocument.Parse(json);
                            var taskId = doc.RootElement.GetProperty("payload").GetProperty("taskId").GetString();
                            if (!string.IsNullOrEmpty(taskId))
                                await FailTaskAsync(taskId, $"任务处理异常: {ex.Message}");
                        }
                        catch { }
                    }
                    break;

                case "NO_TASK":
                    _pollTcs?.TrySetResult(false);
                    break;

                case "PONG":
                case "HEARTBEAT":
                    Interlocked.Exchange(ref _lastPongTicks, DateTime.UtcNow.Ticks);  // R2-1：跨线程（Timer 心跳 vs ReceiveLoop）原子写
                    Interlocked.Exchange(ref _pongTimeoutCount, 0);
                    break;

                case "TERMINATE_TASK":
                    var termPayload = DeserializePayload<TerminatePayload>(message.Payload);
                    if (termPayload != null)
                        await HandleTerminateTask(termPayload);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理 WS 消息异常");
        }
    }

    private static T? DeserializePayload<T>(object? payload)
    {
        if (payload == null) return default;
        var json = payload is JsonElement je ? je.GetRawText() : JsonSerializer.Serialize(payload, _jsonOptions);
        return JsonSerializer.Deserialize<T>(json, SmartFilling.Engine.HttpJsonOptions.CaseInsensitive);  // F7：#20 防御性 caseInsensitive
    }

    // ===== 任务处理（v2 改造核心） =====

    private async Task HandleExecuteTask(ExecuteTaskPayload payload, CancellationToken ct)
    {
        _logger.LogInformation("收到 EXECUTE_TASK: {TaskId}", payload.TaskId);

        // ① 并发检查
        if (_concurrencyManager.IsBusy)
        {
            await RevertTaskAsync(payload.TaskId);
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var taskExecutionService = scope.ServiceProvider.GetRequiredService<TaskExecutionService>();

        // ② DEC-5: FetchScripts 前置（获取 + LoadFromJson 全校验 fail-fast；只依赖 scriptId，独立于密码/fillData 链）
        if (string.IsNullOrEmpty(payload.ScriptId))
        {
            await FailTaskAsync(payload.TaskId, "任务 payload 缺少 scriptId，无法获取脚本");
            return;
        }
        List<ScriptV2> scripts;
        try
        {
            var scriptService = scope.ServiceProvider.GetRequiredService<ScriptService>();
            scripts = await scriptService.FetchScriptsAsync(payload.ScriptId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "脚本获取失败: {TaskId}, ScriptId: {ScriptId}", payload.TaskId, payload.ScriptId);
            await FailTaskAsync(payload.TaskId, $"脚本获取失败: {ex.Message}");
            return;
        }
        if (scripts.Count == 0)
        {
            await FailTaskAsync(payload.TaskId, $"脚本获取结果为空，scriptId: {payload.ScriptId}");
            return;
        }

        // ③ 解密密码
        string decryptedPassword;
        try { decryptedPassword = DecryptPassword(payload.Password, payload.Username); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "密码解密失败: {TaskId}", payload.TaskId);
            await FailTaskAsync(payload.TaskId, $"密码解密失败: {ex.Message}");
            return;
        }

        // ④ DEC-3 层2: 构建 fillData + 下载附件（提取 internal helper，不碰 ExecuteAsync/浏览器）
        Dictionary<string, object> fillData;
        try { fillData = await BuildFillDataAndDownloadAttachmentsAsync(payload, decryptedPassword, scope); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "附件下载失败: {TaskId}", payload.TaskId);
            await FailTaskAsync(payload.TaskId, $"附件下载失败: {ex.Message}");
            return;
        }

        // ⑤ FillTask + ExecuteAsync（删 fileList 块；删 Attachments 赋值——FillTask.Attachments 字段已删）
        // R3-7：尾部加 try-catch——FillTask 构造/ExecuteAsync 启动阶段抛异常会变未观察 task（任务静默失败、平台看任务卡住），
        // 此处兜底上报 FailTaskAsync（各子步骤已有独立 catch，这里仅保护"构建+启动"阶段）
        try
        {
            var task = new FillTask
            {
                TaskId = payload.TaskId,
                Scripts = scripts,
                FillData = fillData,
                Status = ModelTaskStatus.Running
            };

            // #6：移除 WS 自建 _taskCancellations 双轨（runner 监听 orchestration 的 taskCt，WS 自建 CTS 无人监听→取消永远无效）；
            // 取消统一走 orchestration.CancelByTaskId（见 HandleTerminateTask），与 HTTP 路径一致
            var started = await taskExecutionService.ExecuteAsync(task);
            if (!started)
            {
                await RevertTaskAsync(payload.TaskId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "任务启动失败: {TaskId}", payload.TaskId);
            await FailTaskAsync(payload.TaskId, $"任务启动失败: {ex.Message}");
        }
    }

    /// <summary>
    /// DEC-3 层2: 构建 fillData + 下载附件，提取为 internal static helper 供测试接线（不碰 ExecuteAsync/浏览器，避开 HandleExecuteTask private + 重依赖的高 mock 成本）。
    /// 修复1: env/engineLogger/engineOptions/platformOptions 从 scope 解析（原内联声明随 fileList 块删除失效；构造函数无 _env/_engineOptions 注入字段，须 scope 解析）。
    /// 修复2: timeoutSeconds 用 platformOptions.DefaultTimeoutSeconds（EngineOptions 无此属性，只有 DefaultTimeout ms；HTTP FillController:87 正确用 _platformOptions）。
    /// ISSUE-J: attachmentBaseUrl 用 platformOptions.AttachmentBaseUrl（与 HTTP FillController:86 统一，替代原 _configuration["Platform:AttachmentBaseUrl"]）。
    /// 平台附件在 params 的 file 字段（[{name,url}]），fileList 是 v1 遗留已废弃——附件下载改走 fillData 路径（DownloadAttachmentsInFillDataAsync 回填 path，upload 取 path）。
    /// </summary>
    internal static async Task<Dictionary<string, object>> BuildFillDataAndDownloadAttachmentsAsync(
        ExecuteTaskPayload payload, string decryptedPassword, IServiceScope scope)
    {
        // 构建 FillData
        // ②：非 string 值改调 NormalizeJsonElement（与 HTTP 链路 NormalizeJsonElement 产出对齐），
        // 原 GetRawText 把所有结构化数据拍平成 JSON 字符串，导致 loopSource GetLoopRows 取不到、{{obj.field}} 替换失效（P0 WS 链路）。
        var fillData = payload.Params?.ToDictionary(
            kvp => kvp.Key,
            kvp => (object)(kvp.Value.ValueKind == JsonValueKind.String
                ? kvp.Value.GetString() ?? ""
                : SmartFilling.Engine.Engine.VariableHelper.NormalizeJsonElement(kvp.Value))) ?? new Dictionary<string, object>();

        if (!string.IsNullOrEmpty(payload.Username))
            fillData["username"] = payload.Username;
        if (!string.IsNullOrEmpty(decryptedPassword))
            fillData["password"] = decryptedPassword;

        // 下载附件（对齐 HTTP FillController:88；fillData 的 file 字段附件被下载、path 回填、upload 取 path）
        var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        var engineLogger = scope.ServiceProvider.GetRequiredService<EngineILogger>();
        var engineOptions = scope.ServiceProvider.GetRequiredService<IOptions<EngineOptions>>().Value;
        var platformOptions = scope.ServiceProvider.GetRequiredService<IOptions<PlatformOptions>>().Value;  // ISSUE-J + 修复2
        var timeoutSeconds = platformOptions.DefaultTimeoutSeconds > 0 ? platformOptions.DefaultTimeoutSeconds : 60;
        await AttachmentService.DownloadAttachmentsInFillDataAsync(
            fillData, engineOptions.UploadRootPath ?? env.ContentRootPath, engineLogger, platformOptions.AttachmentBaseUrl, timeoutSeconds);
        return fillData;
    }

    private static string DecryptPassword(string encrypted, string username)
    {
        const int IvSize = 12;
        const int TagSize = 16;

        var keyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(username));
        var combined = Convert.FromBase64String(encrypted);

        var iv = new byte[IvSize];
        Buffer.BlockCopy(combined, 0, iv, 0, IvSize);

        var ciphertextLength = combined.Length - IvSize - TagSize;
        var ciphertext = new byte[ciphertextLength];
        var tag = new byte[TagSize];
        Buffer.BlockCopy(combined, IvSize, ciphertext, 0, ciphertextLength);
        Buffer.BlockCopy(combined, IvSize + ciphertextLength, tag, 0, TagSize);

        var plaintext = new byte[ciphertextLength];
        using var aes = new AesGcm(keyBytes, TagSize);
        aes.Decrypt(iv, ciphertext, tag, plaintext);

        return Encoding.UTF8.GetString(plaintext);
    }

    private async Task RevertTaskAsync(string taskId)
    {
        _logger.LogWarning("任务退回: {TaskId}", taskId);
        try
        {
            await SendAsync(new WsMessage
            {
                Action = "TASK_FINISHED",
                Payload = new TaskProgressPayload { TaskId = taskId, Status = "WAIT_EXECUTE", CurrentStep = "并发槽位已满，任务退回" }
            });
        }
        catch (Exception ex) { _logger.LogError(ex, "任务退回失败: {TaskId}", taskId); }
    }

    private async Task FailTaskAsync(string taskId, string errorMessage)
    {
        _logger.LogWarning("任务失败: {TaskId}, 原因: {Error}", taskId, errorMessage);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var completionHandler = scope.ServiceProvider.GetRequiredService<ITaskCompletionHandler>();
            var failTask = new FillTask { TaskId = taskId, Status = ModelTaskStatus.Failed, ErrorMessage = errorMessage };
            await completionHandler.OnTaskCompleted(failTask);
        }
        catch (Exception ex) { _logger.LogError(ex, "任务失败回调异常: {TaskId}", taskId); }
    }

    private Task HandleTerminateTask(TerminatePayload payload)
    {
        _logger.LogInformation("收到 TERMINATE_TASK: {TaskId}, 原因: {Reason}", payload.TaskId, payload.Reason);
        // #6：改调 orchestration.CancelByTaskId（命中 runner 监听的 taskCt）；原取消 WS 自建 _taskCancellations 的 cts（runner 不监听）→ TERMINATE_TASK 静默丢弃
        _orchestrationService.CancelByTaskId(payload.TaskId);
        return Task.CompletedTask;
    }

    // ===== 心跳 =====

    private void StartHeartbeat()
    {
        Interlocked.Exchange(ref _lastPongTicks, DateTime.UtcNow.Ticks);  // R2-1
        Interlocked.Exchange(ref _pongTimeoutCount, 0);
        _heartbeatTimer = new Timer(async _ => await SendHeartbeatAsync(), null,
            TimeSpan.FromSeconds(_options.HeartbeatIntervalSeconds),
            TimeSpan.FromSeconds(_options.HeartbeatIntervalSeconds));
    }

    private void StopHeartbeat() => _heartbeatTimer?.Dispose();

    /// <summary>R5-5：清理过期的消息去重记录（保留窗口外的移除），由心跳周期触发。</summary>
    private void CleanupProcessedMsgIds()
    {
        var cutoff = DateTime.UtcNow - _processedMsgRetention;
        foreach (var kv in _processedMsgIds)
            if (kv.Value < cutoff)
                _processedMsgIds.TryRemove(kv.Key, out _);
    }

    private async Task SendHeartbeatAsync()
    {
        try
        {
            CleanupProcessedMsgIds();  // R5-5：心跳周期清理过期去重记录，避免长运行会话无界内存增长
            if (_options.RequirePong && (DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastPongTicks))).TotalSeconds > _options.PongTimeoutSeconds)
            {
                var count = Interlocked.Increment(ref _pongTimeoutCount);  // R2-1：原子自增（Timer vs ReceiveLoop 并发）
                if (count >= _options.MaxPongTimeouts)
                {
                    _logger.LogWarning("PONG 超时 {Count} 次，触发重连", count);
                    _ws?.Abort();
                    return;
                }
            }

            await SendAsync(new WsMessage
            {
                Action = "HEARTBEAT",
                Payload = new HeartbeatPayload { Status = _concurrencyManager.Status }
            });
        }
        catch (Exception ex) { _logger.LogWarning(ex, "心跳发送失败"); }
    }

    // ===== 任务轮询 =====

    private TaskCompletionSource<bool>? _pollTcs;

    private void StartPollTimer()
    {
        _pollTimer = new Timer(async _ => await PollTasksAsync(), null,
            TimeSpan.FromSeconds(_options.TaskPollIntervalSeconds),
            TimeSpan.FromSeconds(_options.TaskPollIntervalSeconds));
    }

    private void StopPollTimer() => _pollTimer?.Dispose();

    internal async Task PollTasksAsync()
    {
        if (!_pollLock.Wait(0)) return;
        try
        {
            if (_concurrencyManager.IsBusy) return;

            while (!_concurrencyManager.IsBusy)
            {
                _pollTcs = new TaskCompletionSource<bool>();

                await SendAsync(new WsMessage { Action = "FETCH_TASK", Payload = new FetchTaskPayload() });

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                cts.Token.Register(() => _pollTcs.TrySetResult(false));

                var hasTask = await _pollTcs.Task;
                if (!hasTask) break;
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "轮询异常"); }
        finally { _pollLock.Release(); }
    }

    // ===== 缓冲补发 =====

    private async Task FlushBufferAsync(CancellationToken ct)
    {
        List<string> toFlush;
        lock (_bufferLock)
        {
            if (_bufferList.Count == 0) return;
            toFlush = [.. _bufferList];
            _bufferList.Clear();
        }

        for (int i = 0; i < toFlush.Count; i++)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(toFlush[i]);
                await _sendLock.WaitAsync(ct);
                try
                {
                    if (_ws?.State == WebSocketState.Open)
                        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
                    else
                        throw new InvalidOperationException("WS 未连接");
                }
                finally { _sendLock.Release(); }
            }
            catch
            {
                lock (_bufferLock) { _bufferList.InsertRange(0, toFlush.Skip(i)); }
                return;
            }
        }
    }
}
