namespace SmartFilling.Engine.Logging;

public interface ILogger
{
    void LogDebug(string message);
    void LogInformation(string message);
    void LogWarning(string message);
    void LogError(string message);
    void LogWarning(Exception ex, string message);
}
