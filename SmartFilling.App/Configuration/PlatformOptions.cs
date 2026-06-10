namespace SmartFilling.App.Configuration;

public class PlatformOptions
{
    public string NetApiUrl { get; set; } = "";
    public string JavaApiUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public int DefaultTimeoutSeconds { get; set; } = 30;
    public int ClassificationPollIntervalMs { get; set; } = 5000;
}
