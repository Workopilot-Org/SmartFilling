namespace SmartFilling.Engine.Logging;

public record StepLog
{
    public string? PhaseName { get; init; }
    public int StepIndex { get; init; }
    public string? Action { get; init; }
    public string? Selector { get; init; }
    public long DurationMs { get; init; }
    public string Status { get; init; } = "success"; // success / retry / failed / skipped
    public int RetryCount { get; init; }
    public string? Error { get; init; }
    public string? ScreenshotPath { get; init; }
}
