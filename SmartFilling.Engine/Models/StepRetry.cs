namespace SmartFilling.Engine.Models;

public record StepRetry
{
    public int Count { get; init; } = 1;
    public int Interval { get; init; } = 1000;
}
