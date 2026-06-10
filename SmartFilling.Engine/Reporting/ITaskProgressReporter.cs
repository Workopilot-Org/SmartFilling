namespace SmartFilling.Engine.Reporting;

public interface ITaskProgressReporter
{
    Task SendLogAsync(string message, string? stepName = null);
    Task SendScreenshotAsync(byte[] screenshot, string? stepDescription = null);
    Task SendFinalScreenshotAsync(byte[] screenshot);
    Task SendTaskStoppedAsync(string reason);
    Task SendTaskCompletedAsync(Models.ScriptResult result);
}
