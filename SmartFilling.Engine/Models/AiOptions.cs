namespace SmartFilling.Engine.Models;

public record AiOptions
{
    public string ApiKey { get; init; } = "";
    public string ModelId { get; init; } = "";
    public string Endpoint { get; init; } = "";
    public int CircuitBreakerThreshold { get; init; } = 3;
}
