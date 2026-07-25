using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SmartFilling.BackgroundWorker.Configuration;
using SmartFilling.BackgroundWorker.Models;
using static SmartFilling.BackgroundWorker.Models.TaskStatus;

namespace SmartFilling.BackgroundWorker.Services;

/// <summary>
/// HTTP 终态 handler（D2 改造）：不再依赖 IProgressReporterFactory（P3-3 单一出口由本类直接发）。
/// pull 模式：写 TaskResultStore（JSONL 终态，GET 地基）。
/// push 模式（CompleteReport.push=true 且 CompleteCallbackUrl 非空）：HTTP POST 终态到 completeCallbackUrl，按 WebhookRetry 重试。
/// 三分发（Success/Stopped=取消/Failed=失败）体现在 result 字段（对齐前端 handleFillCompleted/停止态处理）。
/// P3：终态截图（task.FinalScreenshot）随 payload 传递（前端 app.js 已有显示逻辑）。
/// </summary>
public class HttpTaskCompletionHandler : ITaskCompletionHandler
{
    private readonly ILogger<HttpTaskCompletionHandler> _logger;
    private readonly TaskResultStore _resultStore;
    private readonly HttpClient _httpClient;
    private readonly WorkerOptions _options;
    private readonly InMemoryProgressStore _progressStore;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public HttpTaskCompletionHandler(
        ILogger<HttpTaskCompletionHandler> logger,
        TaskResultStore resultStore,
        HttpClient httpClient,
        IOptions<WorkerOptions> options,
        InMemoryProgressStore progressStore)
    {
        _logger = logger;
        _resultStore = resultStore;
        _httpClient = httpClient;
        _options = options.Value;
        _progressStore = progressStore;
    }

    public async Task OnTaskCompleted(FillTask task)
    {
        // ④-3：returnData 全量 JSON 落 Serilog（截断 4KB + 换行清洗，见 FillTaskExtensions）
        _logger.LogInformation("HTTP 任务完成 returnData: {ReturnData}", task.ReturnDataToJson());

        // P3：完成上报统一由 completionHandler 按 task 实际状态三分发（engine 已退化为只发截图，见 ScriptEngineRunner）。
        var (result, status, success) = task.Status == Completed
            ? ("Success", "completed", true)
            : task.FailureType == Engine.Models.FailureType.Cancelled
                ? ("Stopped", "cancelled", false)  // 用户取消→停止语义（result="Stopped"，前端走停止态）
                : ("Failed", "failed", false);      // 真失败→失败语义（不伪装停止）

        var record = new TaskResultRecord
        {
            TaskId = task.TaskId,
            Status = status,
            Success = success,
            ErrorMessage = task.ErrorMessage,
            FailureType = task.FailureType?.ToString(),
            ReturnData = task.ReturnData,
            Stats = task.Stats,
            Screenshot = task.FinalScreenshot,
            Timestamp = DateTime.UtcNow
        };

        // pull: 写 JSONL 终态（GET 地基，跨重启可查）
        if (_options.CompleteReport.Enabled.Pull)
            await _resultStore.AppendAsync(record);

        // push: HTTP POST 终态到 completeCallbackUrl（带 WebhookRetry 重试）
        if (_options.CompleteReport.Enabled.Push && !string.IsNullOrEmpty(task.CompleteCallbackUrl))
            await PostTerminalAsync(task.CompleteCallbackUrl, record, result);

        // 第3轮-C7：终态后清理进度内存（任务结束，进度不再需要；GET 转查 TaskResultStore 终态）
        _progressStore.Cleanup(task.TaskId);
    }

    private async Task PostTerminalAsync(string url, TaskResultRecord record, string result)
    {
        var retry = _options.CompleteReport.PushOptions.Retry;
        int maxAttempts = (retry != null ? retry.Count : 3) + 1;  // 首次 + N 次重试
        int interval = retry?.Interval ?? 1000;
        int timeout = _options.CompleteReport.PushOptions.TimeoutSeconds > 0
            ? _options.CompleteReport.PushOptions.TimeoutSeconds
            : 10;

        var payload = new
        {
            taskId = record.TaskId,
            result,
            resultData = record.ReturnData,
            stats = record.Stats,
            errorMessage = record.ErrorMessage,
            failureType = record.FailureType,
            screenshot = record.Screenshot
        };

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
                var response = await _httpClient.PostAsJsonAsync(url, payload, _jsonOptions, cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    if (attempt > 1)
                        _logger.LogInformation("终态推送重试成功: {TaskId} attempt={Attempt}", record.TaskId, attempt);
                    return;
                }
                _logger.LogWarning("终态推送非 2xx: {TaskId} attempt={Attempt}/{Max} {Status}",
                    record.TaskId, attempt, maxAttempts, (int)response.StatusCode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("终态推送失败: {TaskId} attempt={Attempt}/{Max} {Error}",
                    record.TaskId, attempt, maxAttempts, ex.Message);
            }
            if (attempt < maxAttempts)
                await Task.Delay(interval);
        }
        _logger.LogError("终态推送最终失败: {TaskId} 共尝试 {Attempts} 次（已写 JSONL，GET 仍可查终态）", record.TaskId, maxAttempts);
    }
}
