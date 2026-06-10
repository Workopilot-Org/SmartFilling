using System.Text.Json;
using Microsoft.Extensions.Options;
using SmartFilling.BackgroundWorker.Configuration;
using SmartFilling.BackgroundWorker.Models;  // BUG-A：ApiResult<T> / ScriptApiResponse（兄弟命名空间 Services 不跨兄弟解析，须显式 using）
using SmartFilling.Engine;                    // BUG-A：HttpJsonOptions（父命名空间；using 子 SmartFilling.Engine.Engine 不导入父）
using SmartFilling.Engine.Engine;
using SmartFilling.Engine.Models;

namespace SmartFilling.BackgroundWorker.Services;

/// <summary>
/// WS 模式下从远程平台 API 获取 v2 格式脚本。
/// 每次调用均从平台拉取，不做内存缓存。
/// </summary>
public class ScriptService
{
    private readonly HttpClient _httpClient;
    private readonly PlatformOptions _platformOptions;
    private readonly WorkerOptions _workerOptions;
    private readonly ILogger<ScriptService> _logger;

    public ScriptService(
        HttpClient httpClient,
        IOptions<PlatformOptions> platformOptions,
        IOptions<WorkerOptions> workerOptions,
        ILogger<ScriptService> logger)
    {
        _httpClient = httpClient;
        _platformOptions = platformOptions.Value;
        _workerOptions = workerOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// 从平台 API 获取指定 scriptId 的脚本内容，解析为 ScriptV2 列表。
    /// 平台返回两层包装：① {code,msg,data} 外层信封（ApiResult&lt;T&gt;）；② data[*].scirptTotalContent 内层字符串（真正脚本内容，二次反序列化）。
    /// v1 firsthand + 用户确认：接口与 v1 一致，仅 scirptTotalContent 内容由 v1 步骤换为 v2 脚本（DEC-1：内部是 [{完整ScriptV2},...] 数组）。
    /// </summary>
    public async Task<List<ScriptV2>> FetchScriptsAsync(string scriptId, CancellationToken ct)
    {
        var url = $"{_platformOptions.JavaApiUrl}/fill/fillScript/getScriptContent/{scriptId}";
        _logger.LogInformation("从平台拉取脚本: {Url}", url);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(
            _platformOptions.DefaultTimeoutSeconds > 0 ? _platformOptions.DefaultTimeoutSeconds : 60));

        // API-KEY header（HttpClient 是共享 singleton，不能改 DefaultRequestHeaders，用 HttpRequestMessage；v1 独占 new HttpClient 才用 DefaultRequestHeaders）
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(_platformOptions.ApiKey))
            req.Headers.Add("API-KEY", _platformOptions.ApiKey);
        var response = await _httpClient.SendAsync(req, timeoutCts.Token);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        _logger.LogDebug("平台返回脚本 JSON（前 500 字符）: {Preview}", body.Length > 500 ? body[..500] : body);

        // ① 解 {code,msg,data} 信封（复用 HttpJsonOptions.CaseInsensitive，与 CaptchaService/AttachmentClassificationService 一致）
        var envelope = JsonSerializer.Deserialize<ApiResult<List<ScriptApiResponse>>>(body, HttpJsonOptions.CaseInsensitive)
            ?? throw new InvalidOperationException("平台返回非有效 JSON 信封");

        // ② DEC-2: 校验 code==200（比 v1 健壮，v1 只查 Data==null；防 code=500 + Data 非空 silent 用错数据）
        if (envelope.Code != 200)
            throw new InvalidOperationException($"平台返回错误 code={envelope.Code}, msg={envelope.Msg}");
        if (envelope.Data == null || envelope.Data.Count == 0)
            throw new InvalidOperationException($"脚本 {scriptId} 不存在（data 为空）");

        // ③ DEC-1: scirptTotalContent 字符串内是 ScriptV2 数组（每元素完整脚本），逐元素解析（不兼容单对象，非数组抛错 fail-fast）
        var scripts = new List<ScriptV2>();
        foreach (var item in envelope.Data)
        {
            if (string.IsNullOrWhiteSpace(item.ScriptTotalContent))
                throw new InvalidOperationException($"脚本 {item.ScriptId ?? scriptId} 的 scirptTotalContent 为空");
            // 观察1: ! 消除 CS8604（IsNullOrWhiteSpace 守卫已保证非空，编译器不感知）
            using var contentDoc = JsonDocument.Parse(item.ScriptTotalContent!, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });  // CommentHandling.Skip 与 ScriptLoader 一致（支持脚本内注释）
            if (contentDoc.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException($"脚本 {item.ScriptId ?? scriptId} 的 scirptTotalContent 不是数组（DEC-1 期望 [{{ScriptV2}}, ...]）");
            foreach (var el in contentDoc.RootElement.EnumerateArray())
                scripts.Add(ScriptLoader.LoadFromJson(el.GetRawText(), _workerOptions.IgnoreNullFieldsOnScriptValidation));  // DEC-4: 完整 ScriptV2 全校验（schema+业务）+ IgnoreNullFieldsOnScriptValidation 宽松 null
        }
        _logger.LogInformation("成功解析 {Count} 个 ScriptV2 脚本", scripts.Count);

        return scripts;
    }
}
