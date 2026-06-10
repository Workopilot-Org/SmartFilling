namespace SmartFilling.Engine.Models;

public record ExecutionStats
{
    public int DeterministicSteps { get; init; }
    public int AiSteps { get; init; }
    public int AiApiCallCount { get; init; }
    public int AiInputTokens { get; init; }
    public int AiOutputTokens { get; init; }
    public int AiTotalTokens { get; init; }
    public int SnapshotCount { get; init; }
    public int SnapshotTokens { get; init; }
    public int AiScreenshotCount { get; init; }
    public int AiCallsWithScreenshot { get; init; }
    public int ScreenshotTokens { get; init; }
    public int TotalDurationMs { get; init; }
}
