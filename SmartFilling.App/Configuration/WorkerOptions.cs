namespace SmartFilling.App.Configuration;

public class WorkerOptions
{
    public string BaseUrl { get; set; } = "http://localhost:5000";
    public int StartTaskTimeoutSeconds { get; set; } = 30;
}
