using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Options;
using SmartFilling.App.Configuration;
using SmartFilling.Engine.Models;

namespace SmartFilling.App.Services;

/// <summary>
/// Worker API 客户端 — 协议剥离后转发填报任务给 Worker（POST /tasks 带 taskId + 回调 URL）。
/// 503 退避重试（决策10）：收 Worker 503 按指数退避重试 N 次（WorkerBusyRetry 配置），耗尽仍 503 抛 HttpRequestException(503) 给 FillController catch 透传。
/// </summary>
public class WorkerApiClient
{
    private readonly HttpClient _httpClient;
    private readonly WorkerOptions _options;
    private readonly ILogger<WorkerApiClient> _logger;
    private static readonly JsonSerializerOptions _jsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, PropertyNameCaseInsensitive = true };

    public WorkerApiClient(HttpClient httpClient, IOptions<WorkerOptions> options, ILogger<WorkerApiClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// 转发填报任务给 Worker。④-4：传脚本原始 JSON 文本，让 Worker 执行边界做 ScriptLoader 校验。
    /// taskId + progressCallbackUrl + completeCallbackUrl 随请求体传入（决策11-A + 第3轮-C2 push 模式）。
    /// </summary>
    public async Task<WorkerFillResponse?> StartFillAsync(
        string taskId,
        string documentTypeId,
        string documentTypeName,
        List<string> scripts,
        Dictionary<string, object> fillData,
        string? progressCallbackUrl = null,
        string? completeCallbackUrl = null,
        string? attachmentBaseUrl = null)
    {
        // 附件 URL 转换：将 fillData 中 file 字段的相对 path 转为绝对 URL（Worker 砍 AttachmentBaseUrl，只收绝对 URL，B3）
        ConvertAttachmentUrls(fillData, attachmentBaseUrl ?? "");

        var body = new
        {
            TaskId = taskId,
            DocumentTypeId = documentTypeId,
            DocumentTypeName = documentTypeName,
            Scripts = scripts,
            FillData = fillData,
            ProgressCallbackUrl = progressCallbackUrl,
            CompleteCallbackUrl = completeCallbackUrl
        };

        var url = $"{_options.BaseUrl.TrimEnd('/')}/tasks";
        var retry = _options.WorkerBusyRetry;
        int maxAttempts = (retry?.Count ?? 3) + 1;  // 首次 + N 次退避重试
        int interval = retry?.Interval > 0 ? retry.Interval : 1000;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.StartTaskTimeoutSeconds));
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.PostAsJsonAsync(url, body, _jsonOptions, cts.Token);
            }
            catch (Exception ex) when (ex is not HttpRequestException)
            {
                // 网络错误（连接拒绝/超时）也退避重试（Worker 可能重启中）
                if (attempt < maxAttempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(interval * Math.Pow(2, attempt - 1)));
                    continue;
                }
                throw new HttpRequestException($"Worker 连接失败: {ex.Message}", ex, System.Net.HttpStatusCode.ServiceUnavailable);
            }

            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<WorkerFillResponse>(_jsonOptions);

            // 503 = 并发槽位满，退避重试
            if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable && attempt < maxAttempts)
            {
                _logger.LogWarning("Worker 503 槽位满，第 {Attempt} 次退避重试", attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(interval * Math.Pow(2, attempt - 1)));
                continue;
            }

            // 非 503 非 2xx，或 503 已耗尽 → 抛（App FillController 按 StatusCode 映射：400→BadRequest 透传，503→ServiceUnavailable 透传）
            var error = await response.Content.ReadAsStringAsync();
            _logger.LogError("Worker 转发失败: {StatusCode} - {Error}", response.StatusCode, error);
            string message;
            try
            {
                using var doc = JsonDocument.Parse(error);
                message = doc.RootElement.TryGetProperty("error", out var errProp) ? (errProp.GetString() ?? error) : error;
            }
            catch { message = error; }
            throw new HttpRequestException($"Worker {(int)response.StatusCode}: {message}", null, response.StatusCode);
        }

        throw new HttpRequestException("Worker 503 退避重试耗尽", null, System.Net.HttpStatusCode.ServiceUnavailable);
    }

    /// <summary>
    /// 递归遍历 fillData，将 file 类型字段中的相对 url 转为绝对 URL（Worker 下载侧读 url 字段，非 path）。
    /// </summary>
    internal static void ConvertAttachmentUrls(Dictionary<string, object> data, string baseUrl)
    {
        if (string.IsNullOrEmpty(baseUrl)) return;
        baseUrl = baseUrl.TrimEnd('/');

        foreach (var key in data.Keys.ToList())
        {
            var value = data[key];
            if (value is List<object> list)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] is Dictionary<string, object> obj)
                    {
                        // 检查是否为附件对象（含 url 字段）
                        if (obj.ContainsKey("url") && obj.ContainsKey("name"))
                        {
                            // 转换 url 字段：Worker 下载侧读 url（AttachmentService 取 att["url"]），原转 path 字段错位→对下载无效
                            if (obj.TryGetValue("url", out var url) && url is string urlStr
                                && !string.IsNullOrEmpty(urlStr) && !urlStr.StartsWith("http"))
                            {
                                obj["url"] = $"{baseUrl}/{urlStr.TrimStart('/')}";
                            }
                        }
                        else
                        {
                            ConvertAttachmentUrls(obj, baseUrl);
                        }
                    }
                }
            }
            else if (value is Dictionary<string, object> nested)
            {
                ConvertAttachmentUrls(nested, baseUrl);
            }
        }
    }

    public async Task<bool> StopFillAsync(string taskId)
    {
        var url = $"{_options.BaseUrl.TrimEnd('/')}/tasks/{taskId}:cancel";
        var response = await _httpClient.PostAsync(url, null);
        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// 健康检测（P1/F6）：GET Worker /health?taskId=xxx，返回 {status, taskStatus, lastActivity}。
    /// WorkerHealthCheckService 定时扫描活跃任务注册表用。
    /// </summary>
    public async Task<WorkerHealthResponse?> HealthCheckAsync(string taskId)
    {
        var url = $"{_options.BaseUrl.TrimEnd('/')}/health?taskId={Uri.EscapeDataString(taskId)}";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var response = await _httpClient.GetAsync(url, cts.Token);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<WorkerHealthResponse>(_jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Worker 健康检测失败: {TaskId} {Error}", taskId, ex.Message);
            return null;
        }
    }
}

public class WorkerFillResponse
{
    public string? TaskId { get; set; }
    public string? Status { get; set; }
    // WorkerHubUrl 已删（前端不再直连 Worker，填报进度经 App AgentHub 推送）
}

public record WorkerHealthResponse(string Status, string TaskStatus, DateTime? LastActivity);
