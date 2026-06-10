namespace SmartFilling.BackgroundWorker.Configuration;

public class WsClientOptions
{
    public string ServerUrl { get; set; } = "";
    public string ClientId { get; set; } = "";
    public int HeartbeatIntervalSeconds { get; set; } = 30;
    public int TaskPollIntervalSeconds { get; set; } = 30;
    public int PongTimeoutSeconds { get; set; } = 60;
    public int MaxPongTimeouts { get; set; } = 3;
    public int ReconnectIntervalSeconds { get; set; } = 5;
    public int MaxDisconnectBufferMessages { get; set; } = 100;
    public bool RequirePong { get; set; } = true;
    public bool ScreenshotOnProgress { get; set; } = false;
    public bool ScreenshotOnComplete { get; set; } = true;
}
