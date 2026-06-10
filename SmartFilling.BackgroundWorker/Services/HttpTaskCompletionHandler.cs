using SmartFilling.BackgroundWorker.Models;
using SmartFilling.Engine.Models;
using SmartFilling.Engine.Reporting;
using static SmartFilling.BackgroundWorker.Models.TaskStatus;

namespace SmartFilling.BackgroundWorker.Services;

public class HttpTaskCompletionHandler : ITaskCompletionHandler
{
    private readonly ILogger<HttpTaskCompletionHandler> _logger;
    private readonly IProgressReporterFactory _reporterFactory;

    public HttpTaskCompletionHandler(ILogger<HttpTaskCompletionHandler> logger, IProgressReporterFactory reporterFactory)
    {
        _logger = logger;
        _reporterFactory = reporterFactory;
    }

    public async Task OnTaskCompleted(FillTask task)
    {
        // ④-3：returnData 全量 JSON 落 Serilog（截断 4KB + 换行清洗，见 FillTaskExtensions），让日志轮询验证法可直读 returnData 值
        _logger.LogInformation("HTTP 任务完成 returnData: {ReturnData}", task.ReturnDataToJson());

        // P3：完成上报统一由 completionHandler 按 task 实际状态三分发（engine 已退化为只发截图，见 ScriptEngine.ReportFinalScreenshotAsync）。
        // 一次治本：失败不再伪装停止（FailureType=Cancelled 才是用户取消→停止语义；其余失败→失败语义）。
        var reporter = _reporterFactory.CreateForTask(task.TaskId);
        var result = new ScriptResult
        {
            Success = task.Status == Completed,
            Status = task.Status == Completed ? "completed" : "failed",
            ErrorMessage = task.ErrorMessage,
            FailureType = task.FailureType ?? FailureType.None,
            ReturnData = task.ReturnData,
            Stats = task.Stats
        };
        if (task.Status == Completed)
            await reporter.SendTaskCompletedAsync(result);                         // 成功
        else if (task.FailureType == FailureType.Cancelled)
            await reporter.SendTaskStoppedAsync(task.ErrorMessage ?? "任务已取消"); // 用户取消→停止语义
        else
            await reporter.SendTaskCompletedAsync(result);                         // 真失败→失败语义（不伪装停止）
    }
}
