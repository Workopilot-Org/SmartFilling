using SmartFilling.Engine.Reporting;

namespace SmartFilling.BackgroundWorker.Services;

public interface IProgressReporterFactory
{
    ITaskProgressReporter CreateForTask(string taskId);
}
