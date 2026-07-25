namespace SmartFilling.Engine.Models;

public record AiOptions
{
    public string ApiKey { get; init; } = "";
    public string ModelId { get; init; } = "";
    public string Endpoint { get; init; } = "";
    /// <summary>模型 API 错误处理（重试 + 断路器）。null 时 OpenAiProvider 用 ModelApiErrorOptions 默认值。</summary>
    public ModelApiErrorOptions? ModelApiErrorOptions { get; init; }
}
