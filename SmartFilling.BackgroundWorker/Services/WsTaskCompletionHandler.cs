using SmartFilling.BackgroundWorker.Configuration;
using SmartFilling.BackgroundWorker.Models;
using static SmartFilling.BackgroundWorker.Models.TaskStatus;

namespace SmartFilling.BackgroundWorker.Services;

public class WsTaskCompletionHandler : ITaskCompletionHandler
{
    private readonly AutomationWsClient _wsClient;
    private readonly IConfiguration _config;
    private readonly ILogger<WsTaskCompletionHandler> _logger;

    public WsTaskCompletionHandler(AutomationWsClient wsClient, IConfiguration config, ILogger<WsTaskCompletionHandler> logger)
    {
        _wsClient = wsClient;
        _config = config;
        _logger = logger;
    }

    public async Task OnTaskCompleted(FillTask task)
    {
        // ④-3：returnData 全量 JSON 落 Serilog（WS 原完全无 returnData 日志；截断 4KB + 换行清洗，见 FillTaskExtensions）
        _logger.LogInformation("WS 任务完成 returnData: {ReturnData}", task.ReturnDataToJson());
        var status = task.Status == Completed ? "SUCCESS" : "FAILED";
        await _wsClient.SendAsync(new WsMessage
        {
            Action = "TASK_FINISHED",
            ClientId = _wsClient.ClientId,
            Payload = new TaskProgressPayload
            {
                TaskId = task.TaskId,
                Status = status,
                CurrentStep = status == "SUCCESS" ? "任务完成" : (task.ErrorMessage ?? "任务失败"),  // P3：失败填 errorMessage（原字面量"任务失败"，平台读 currentStep 可见具体错误）
                // J.2 WS 模式：保留 billCode（平台兼容，从 returnData 字典取单值）；resultData 装 returnData 完整字典（成功）
                BillCode = status == "SUCCESS" ? task.ReturnData?.GetValueOrDefault("billCode")?.ToString() : null,
                // #16 决策4：失败时 resultData 组装 {errorMessage, failureType}（顶层 payload 零侵入，平台按 result/success 区分业务字典 vs 错误对象；对齐设计方案:1845 已 documented 契约）
                ResultData = status == "SUCCESS" ? task.ReturnData : (object)new { errorMessage = task.ErrorMessage, failureType = task.FailureType?.ToString() },
                Screenshot = _config.GetValue("WsClient:ScreenshotOnComplete", true) ? task.FinalScreenshot : null
            }
        });
    }
}
