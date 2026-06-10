using SmartFilling.Engine.Logging;

namespace SmartFilling.Engine.Tests.Helpers;

public class NullLogger : ILogger
{
    public void LogDebug(string message) { }
    public void LogInformation(string message) { }
    public void LogWarning(string message) { }
    public void LogError(string message) { }
    public void LogWarning(Exception ex, string message) { }
}
