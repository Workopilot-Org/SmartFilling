using Microsoft.AspNetCore.SignalR;
using SmartFilling.BackgroundWorker.Hubs;
using SmartFilling.Engine.Models;
using SmartFilling.Engine.Reporting;

namespace SmartFilling.BackgroundWorker.Services;

public class SignalRProgressReporterFactory : IProgressReporterFactory
{
    private readonly IHubContext<FillHub> _hub;

    public SignalRProgressReporterFactory(IHubContext<FillHub> hub) => _hub = hub;

    public ITaskProgressReporter CreateForTask(string taskId)
        => new Reporter(_hub, taskId);

    private class Reporter : ITaskProgressReporter
    {
        private readonly IHubContext<FillHub> _hub;
        private readonly string _taskId;

        public Reporter(IHubContext<FillHub> hub, string taskId)
        {
            _hub = hub;
            _taskId = taskId;
        }

        public async Task SendLogAsync(string message, string? stepName = null)
            => await _hub.Clients.Group(_taskId).SendAsync("ReceiveLog",
                new { taskId = _taskId, message, stepName, timestamp = DateTime.Now });

        public async Task SendScreenshotAsync(byte[] screenshot, string? stepDescription = null)
            => await _hub.Clients.Group(_taskId).SendAsync("ReceiveScreenshot",
                new { taskId = _taskId, image = Convert.ToBase64String(screenshot), stepDescription });

        public async Task SendFinalScreenshotAsync(byte[] screenshot)
            => await _hub.Clients.Group(_taskId).SendAsync("ReceiveScreenshot",
                new { taskId = _taskId, image = Convert.ToBase64String(screenshot) });

        public async Task SendTaskStoppedAsync(string reason)
            => await _hub.Clients.Group(_taskId).SendAsync("TaskStopped",
                new { taskId = _taskId, reason });

        public async Task SendTaskCompletedAsync(ScriptResult result)
            => await _hub.Clients.Group(_taskId).SendAsync("TaskCompleted",
                new
                {
                    taskId = _taskId,
                    result = result.Success ? "Success" : "Failed",
                    resultData = result.ReturnData,  // J.2 HTTP：billCode 改名 resultData（成功 returnData 全部数据/null，错误走 errorMessage）
                    stats = result.Stats,
                    errorMessage = result.ErrorMessage,
                    failureType = result.FailureType.ToString()
                });
    }
}
