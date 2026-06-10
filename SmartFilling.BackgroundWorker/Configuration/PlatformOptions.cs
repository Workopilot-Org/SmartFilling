namespace SmartFilling.BackgroundWorker.Configuration;

public class PlatformOptions
{
    public string JavaApiUrl { get; set; } = "";
    public string? AttachmentBaseUrl { get; set; }
    public string ApiKey { get; set; } = "";
    public int DefaultTimeoutSeconds { get; set; } = 30;
}
