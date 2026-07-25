using SmartFilling.Engine.Reporting;

namespace SmartFilling.BackgroundWorker.Services;

public interface IProgressReporterFactory
{
    /// <param name="progressCallbackUrl">进度回调 URL（per-task，由请求体传入；push 模式 + 此非空才 HTTP POST，第3轮-C1）。</param>
    ITaskProgressReporter CreateForTask(string taskId, string? progressCallbackUrl = null);
}
