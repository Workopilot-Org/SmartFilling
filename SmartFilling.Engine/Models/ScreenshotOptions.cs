namespace SmartFilling.Engine.Models;

public record ScreenshotOptions
{
    public bool OnPhaseProgress { get; init; }
    public bool OnStepFailure { get; init; }
    public bool OnTaskComplete { get; init; }
    public bool OnTaskFailure { get; init; }
    public int CompressQuality { get; init; } = 70;
    public string ActionDefaultFolder { get; init; } = "screenshots/";
    public string OnStepFailureFolder { get; init; } = "logs/";
}
