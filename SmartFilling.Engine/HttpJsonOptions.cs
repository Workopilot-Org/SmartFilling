using System.Text.Json;

namespace SmartFilling.Engine;

/// <summary>
/// #20：共享 HTTP 反序列化 JsonSerializerOptions（PropertyNameCaseInsensitive=true）。
/// 对外部 Java/OCR API 的 PascalCase 响应安全兼容（只增匹配宽容度，不破坏现有 camelCase 匹配）。
/// CaptchaService（Engine）/AttachmentClassificationService（App）等 HTTP 反序列化站点共用。
/// </summary>
public static class HttpJsonOptions
{
    public static readonly JsonSerializerOptions CaseInsensitive = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
