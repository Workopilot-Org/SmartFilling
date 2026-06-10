using System.Text.Json;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Options;
using SmartFilling.App.Configuration;
using SmartFilling.Engine.Models;

namespace SmartFilling.App.Services;

/// <summary>
/// Worker API 客户端 — 转发填报任务给 Worker
/// </summary>
public class WorkerApiClient
{
    private readonly HttpClient _httpClient;
    private readonly WorkerOptions _options;
    private readonly ILogger<WorkerApiClient> _logger;
    private static readonly JsonSerializerOptions _jsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, PropertyNameCaseInsensitive = true };

    public WorkerApiClient(IOptions<WorkerOptions> options, ILogger<WorkerApiClient> logger)
    {
        _options = options.Value;
        _logger = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(_options.StartTaskTimeoutSeconds) };
    }

    /// <summary>
    /// 转发填报任务给 Worker。④-4：传脚本原始 JSON 文本（List<string>），让 Worker 执行边界做 ScriptLoader 校验；
    /// 非 2xx 抛 HttpRequestException（携状态码 + Worker 返回的 error 明细），App 按码映射（400→BadRequest 透传校验错误）。
    /// </summary>
    public async Task<WorkerFillResponse?> StartFillAsync(
        string documentTypeId,
        string documentTypeName,
        List<string> scripts,
        Dictionary<string, object> fillData,
        string? attachmentBaseUrl = null)
    {
        // 附件 URL 转换：将 fillData 中 file 字段的相对 path 转为绝对 URL
        ConvertAttachmentUrls(fillData, attachmentBaseUrl ?? "");

        var body = new
        {
            DocumentTypeId = documentTypeId,
            DocumentTypeName = documentTypeName,
            Scripts = scripts,
            FillData = fillData
        };

        var url = $"{_options.BaseUrl.TrimEnd('/')}/api/fill/start";
        var response = await _httpClient.PostAsJsonAsync(url, body, _jsonOptions);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            _logger.LogError("Worker 转发失败: {StatusCode} - {Error}", response.StatusCode, error);
            // 反序列化 Worker 返回的 {error:...} 取明细（防前端读到嵌套 JSON 字符串 "Worker 400: {\"error\":...}"）
            string message;
            try
            {
                using var doc = JsonDocument.Parse(error);
                message = doc.RootElement.TryGetProperty("error", out var errProp) ? (errProp.GetString() ?? error) : error;
            }
            catch { message = error; }
            throw new HttpRequestException($"Worker {(int)response.StatusCode}: {message}", null, response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<WorkerFillResponse>(_jsonOptions);
    }

    /// <summary>
    /// 递归遍历 fillData，将 file 类型字段中的相对 url 转为绝对 URL（Worker 下载侧读 url 字段，非 path）
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
        var url = $"{_options.BaseUrl.TrimEnd('/')}/api/fill/stop/{taskId}";
        var response = await _httpClient.PostAsync(url, null);
        return response.IsSuccessStatusCode;
    }
}

public class WorkerFillResponse
{
    public string? TaskId { get; set; }
    public string? Status { get; set; }
    public string? WorkerHubUrl { get; set; }
}
