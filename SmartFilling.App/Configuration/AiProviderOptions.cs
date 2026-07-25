using SmartFilling.Engine.Models;

namespace SmartFilling.App.Configuration;

public class AiProviderOptions
{
    public string ApiKey { get; set; } = "";
    public string ModelId { get; set; } = "deepseek-v3.2";
    public string Endpoint { get; set; } = "https://dashscope.aliyuncs.com/compatible-mode/v1";
    /// <summary>模型 API 错误处理（重试 + 断路器）。绑定 AiProvider:ModelApiErrorOptions 段。</summary>
    public ModelApiErrorOptions? ModelApiErrorOptions { get; set; }
}
