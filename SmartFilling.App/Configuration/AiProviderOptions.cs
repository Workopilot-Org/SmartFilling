namespace SmartFilling.App.Configuration;

public class AiProviderOptions
{
    public string ApiKey { get; set; } = "";
    public string ModelId { get; set; } = "deepseek-v3.2";
    public string Endpoint { get; set; } = "https://dashscope.aliyuncs.com/compatible-mode/v1";
}
