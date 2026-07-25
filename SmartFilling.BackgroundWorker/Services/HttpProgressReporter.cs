using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SmartFilling.BackgroundWorker.Configuration;
using SmartFilling.Engine.Models;
using SmartFilling.Engine.Reporting;

namespace SmartFilling.BackgroundWorker.Services;

/// <summary>
/// HTTP 进度推送 reporter（D2：实现 ITaskProgressReporter）。
/// 持有 taskId + progressCallbackUrl + ProgressReport 配置。
/// 产生 log/screenshot/completed 时：push 模式（push=true 且 callbackUrl 非空 且 Types 过滤）→ HTTP POST 到 progressCallbackUrl（不重试）；
/// pull 模式（pull=true 且 Types 过滤）→ 写 InMemoryProgressStore 供 GET 查。
/// pull 和 push 两路都受 Types 控制（对齐计划"采集/推送"语义）。
/// 终态由 HttpTaskCompletionHandler 单一出口发（P3-3），但 Types.Completed 控制是否也通过 ProgressReport 通道额外推送/存储。
/// </summary>
public class HttpProgressReporter : ITaskProgressReporter
{
    private readonly string _taskId;
    private readonly string? _progressCallbackUrl;
    private readonly ProgressReportOptions _options;
    private readonly InMemoryProgressStore _store;
    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpProgressReporter> _logger;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public HttpProgressReporter(
        string taskId,
        string? progressCallbackUrl,
        ProgressReportOptions options,
        InMemoryProgressStore store,
        HttpClient httpClient,
        ILogger<HttpProgressReporter> logger)
    {
        _taskId = taskId;
        _progressCallbackUrl = progressCallbackUrl;
        _options = options;
        _store = store;
        _httpClient = httpClient;
        _logger = logger;
    }

    // 条件判断集中：pull 和 push 两路都受 Types 控制（对齐计划"采集/推送"语义）
    private bool ShouldCollect(bool typesFlag) => _options.Enabled.Pull && typesFlag;
    private bool ShouldPush(bool typesFlag) => _options.Enabled.Push && !string.IsNullOrEmpty(_progressCallbackUrl) && typesFlag;

    public async Task SendLogAsync(string message, string? stepName = null)
    {
        if (ShouldCollect(_options.Types.Log))
            _store.AddLog(_taskId, message, stepName);
        if (ShouldPush(_options.Types.Log))
            await PostProgressAsync(message, stepName, null);
    }

    public async Task SendScreenshotAsync(byte[] screenshot, string? stepDescription = null)
    {
        if (screenshot == null || screenshot.Length == 0) return;
        var base64 = Convert.ToBase64String(screenshot);
        if (ShouldCollect(_options.Types.Screenshot))
            _store.AddScreenshot(_taskId, base64, stepDescription);
        if (ShouldPush(_options.Types.Screenshot))
            await PostProgressAsync(null, stepDescription, base64);
    }

    public Task SendFinalScreenshotAsync(byte[] screenshot)
        => SendScreenshotAsync(screenshot, "终态截图");

    // 终态由 HttpTaskCompletionHandler 单一出口发（P3-3），但 Types.Completed 控制是否也通过 ProgressReport 通道额外推送/存储
    public async Task SendTaskCompletedAsync(ScriptResult result)
    {
        var resultStr = result.Success ? "Success"
            : result.FailureType == FailureType.Cancelled ? "Stopped"
            : "Failed";

        if (ShouldCollect(_options.Types.Completed))
            _store.AddCompleted(_taskId, resultStr, result.ErrorMessage, result.FailureType.ToString(),
                result.ReturnData, result.Stats);
        if (ShouldPush(_options.Types.Completed))
            await PostCompletedAsync(resultStr, result);
    }

    private async Task PostProgressAsync(string? message, string? stepName, string? screenshot)
    {
        try
        {
            var timeoutSeconds = _options.PushOptions.TimeoutSeconds > 0 ? _options.PushOptions.TimeoutSeconds : 5;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            var body = new { message, stepName, screenshot };
            var response = await _httpClient.PostAsJsonAsync(_progressCallbackUrl, body, _jsonOptions, cts.Token);
            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("进度推送非 2xx: {TaskId} {Status}", _taskId, (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            // 进度推送失败不阻断任务执行（进度是辅助可观测，终态独立保证）
            _logger.LogWarning("进度推送失败: {TaskId} {Error}", _taskId, ex.Message);
        }
    }

    private async Task PostCompletedAsync(string result, ScriptResult scriptResult)
    {
        try
        {
            var timeoutSeconds = _options.PushOptions.TimeoutSeconds > 0 ? _options.PushOptions.TimeoutSeconds : 5;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            var body = new
            {
                taskId = _taskId,
                result,
                resultData = scriptResult.ReturnData,
                stats = scriptResult.Stats,
                errorMessage = scriptResult.ErrorMessage,
                failureType = scriptResult.FailureType.ToString()
            };
            var response = await _httpClient.PostAsJsonAsync(_progressCallbackUrl, body, _jsonOptions, cts.Token);
            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("终态推送（ProgressReport）非 2xx: {TaskId} {Status}", _taskId, (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("终态推送（ProgressReport）失败: {TaskId} {Error}", _taskId, ex.Message);
        }
    }
}