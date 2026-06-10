using Microsoft.Extensions.Options;
using SmartFilling.BackgroundWorker.Configuration;
using SmartFilling.BackgroundWorker.Models;
using SmartFilling.Engine.Models;
using SmartFilling.Engine.Reporting;

namespace SmartFilling.BackgroundWorker.Services;

public class WsProgressReporterFactory : IProgressReporterFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly WsClientOptions _options;

    public WsProgressReporterFactory(IServiceProvider serviceProvider, IOptions<WsClientOptions> options)
    {
        _serviceProvider = serviceProvider;
        _options = options.Value;
    }

    private AutomationWsClient WsClient =>
        _serviceProvider.GetRequiredService<AutomationWsClient>();

    public ITaskProgressReporter CreateForTask(string taskId)
        => new Reporter(this, taskId);

    private class Reporter : ITaskProgressReporter
    {
        private readonly WsProgressReporterFactory _f;
        private readonly string _taskId;

        public Reporter(WsProgressReporterFactory f, string taskId)
        {
            _f = f;
            _taskId = taskId;
        }

        public async Task SendLogAsync(string message, string? stepName = null)
            => await _f.WsClient.SendAsync(new WsMessage
            {
                Action = "LOG_REPORT",
                Payload = new LogReportPayload
                {
                    TaskId = _taskId,
                    Level = "INFO",
                    StepName = stepName,
                    Message = message
                }
            });

        public async Task SendScreenshotAsync(byte[] screenshot, string? stepDescription = null)
        {
            if (!_f._options.ScreenshotOnProgress) return;
            await _f.WsClient.SendAsync(new WsMessage
            {
                Action = "TASK_PROGRESS",
                Payload = new TaskProgressPayload
                {
                    TaskId = _taskId,
                    Status = "RUNNING",
                    CurrentStep = stepDescription ?? "截图",
                    Screenshot = Convert.ToBase64String(screenshot)
                }
            });
        }

        public Task SendFinalScreenshotAsync(byte[] screenshot) => Task.CompletedTask;

        // P3-6：SendTaskStoppedAsync 改 CompletedTask——消除失败双发（reporter + completionHandler 各发一次 TASK_FINISHED）。
        // WS 终态统一由 WsTaskCompletionHandler 单一出口发（与 SendTaskCompletedAsync 对称，均 CompletedTask）。
        public Task SendTaskStoppedAsync(string reason) => Task.CompletedTask;

        public Task SendTaskCompletedAsync(ScriptResult result) => Task.CompletedTask;
        // WS模式完成上报由WsTaskCompletionHandler统一处理
    }
}
