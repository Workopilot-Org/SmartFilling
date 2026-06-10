using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace SmartFilling.Engine.Services;

public record CaptchaServiceOptions
{
    public string Url { get; init; } = "http://localhost:8000";  // ④-1/3.B：8080 → 8000（对齐自写 mini_server）
}

public class CaptchaService
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;

    public CaptchaService(CaptchaServiceOptions options)
    {
        _baseUrl = options.Url.TrimEnd('/');
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    // 文字识别：POST /ocr {image:b64, png_fix:true} → {result:"文字"}（png_fix 默认开，提升透明 PNG 识别率）。
    // 修订5：支持 CancellationToken，cancel 即返回（不等 30s 超时）。
    public async Task<string> ClassifyAsync(byte[] image, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object>
        {
            ["image"] = Convert.ToBase64String(image),
            ["png_fix"] = true
        };
        var resp = await _httpClient.PostAsJsonAsync($"{_baseUrl}/ocr", body, ct);
        resp.EnsureSuccessStatusCode();
        var env = await resp.Content.ReadFromJsonAsync<OcrEnvelope>(SmartFilling.Engine.HttpJsonOptions.CaseInsensitive, ct);
        return env?.Result ?? "";
    }

    // 滑块匹配：POST /slide_match → result.target_x（缺口 x 偏移）；simple_target=true（_simple_template_match）。
    public async Task<int> SlideMatchAsync(byte[] targetImage, byte[] backgroundImage, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object>
        {
            ["target_image"] = Convert.ToBase64String(targetImage),
            ["background_image"] = Convert.ToBase64String(backgroundImage),
            ["simple_target"] = true
        };
        var resp = await _httpClient.PostAsJsonAsync($"{_baseUrl}/slide_match", body, ct);
        resp.EnsureSuccessStatusCode();
        var env = await resp.Content.ReadFromJsonAsync<SlideMatchEnvelope>(SmartFilling.Engine.HttpJsonOptions.CaseInsensitive, ct);
        return env?.Result?.TargetX ?? 0;
    }

    // 滑块比较：POST /slide_comparison → result.target[0]（x 位移）。
    public async Task<int> SlideComparisonAsync(byte[] targetImage, byte[] backgroundImage, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object>
        {
            ["target_image"] = Convert.ToBase64String(targetImage),
            ["background_image"] = Convert.ToBase64String(backgroundImage)
        };
        var resp = await _httpClient.PostAsJsonAsync($"{_baseUrl}/slide_comparison", body, ct);
        resp.EnsureSuccessStatusCode();
        var env = await resp.Content.ReadFromJsonAsync<SlideComparisonEnvelope>(SmartFilling.Engine.HttpJsonOptions.CaseInsensitive, ct);
        return env?.Result?.Target?.FirstOrDefault() ?? 0;
    }

    // 点选检测：POST /det → result.items[{x,y,text}]（mini_server 已做 detection + 每框 crop + classification）。
    public async Task<ClickDetectionResult> DetectClickAsync(byte[] image, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object> { ["image"] = Convert.ToBase64String(image) };
        var resp = await _httpClient.PostAsJsonAsync($"{_baseUrl}/det", body, ct);
        resp.EnsureSuccessStatusCode();
        var env = await resp.Content.ReadFromJsonAsync<ClickEnvelope>(SmartFilling.Engine.HttpJsonOptions.CaseInsensitive, ct);
        return env?.Result ?? new ClickDetectionResult();
    }

    // 响应模型（对齐 mini_server 契约）。
    // 关键坑：①请求体用 Dictionary<string,object>（非匿名对象）——CamelCase naming policy 对已小写含下划线的 key
    //           （target_image/png_fix/simple_target）不转换，原样保留下划线（§7.2 实测确认）；
    //         ②响应里 target_x/target_y 含下划线，CaseInsensitive 不去下划线，故用 [JsonPropertyName] 显式映射。
    private record OcrEnvelope(string? Result);
    private record SlideMatchEnvelope(SlideMatchData? Result);
    private record SlideMatchData(
        [property: JsonPropertyName("target_x")] int TargetX,
        [property: JsonPropertyName("target_y")] int TargetY,
        int[]? Target);
    private record SlideComparisonEnvelope(SlideComparisonData? Result);
    private record SlideComparisonData(int[]? Target);
    private record ClickEnvelope(ClickDetectionResult? Result);
}

public record ClickDetectionResult
{
    public List<DetectedItem> Items { get; init; } = [];
}

public record DetectedItem
{
    public int X { get; init; }
    public int Y { get; init; }
    public string? Text { get; init; }  // mini_server /det 的 detection+classification 返回每个目标文字（click 补全匹配用）
}
