using Microsoft.AspNetCore.SignalR;

namespace SmartFilling.App.Hubs;

public class AgentHub : Hub
{
    public async Task JoinTask(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            throw new ArgumentException("任务ID不能为空", nameof(taskId));
        await Groups.AddToGroupAsync(Context.ConnectionId, taskId);
    }

    public async Task LeaveTask(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            throw new ArgumentException("任务ID不能为空", nameof(taskId));
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, taskId);
    }
}

/// <summary>
/// AgentHub 扩展方法（录制 record + 填报 fill 共用）。各方法加 type 参数（默认 record 向后兼容），payload 带 type 供前端按 type 分发。
/// D1：SendLog 加 stepName（前端 ReceiveLog 已解构 stepName，原 payload 不传致 stepTag 恒空）。
/// F1-4/G1-4/P3：TaskCompleted payload 补 failureType/resultData/stats/screenshot（对齐 Worker FillHub 版 + 终态截图）。
/// </summary>
public static class AgentHubExtensions
{
    public static async Task SendLog(this IHubContext<AgentHub> hub, string taskId, string message, string? stepName = null, string type = "record")
    {
        await hub.Clients.Group(taskId).SendAsync("ReceiveLog", new { taskId, message, stepName, timestamp = DateTime.Now, type });
    }

    public static async Task SendScreenshot(this IHubContext<AgentHub> hub, string taskId, string base64Image, string type = "record")
    {
        await hub.Clients.Group(taskId).SendAsync("ReceiveScreenshot", new { taskId, image = base64Image, type });
    }

    public static async Task RequestHelp(this IHubContext<AgentHub> hub, string taskId, string question, string base64Image, string type = "record")
    {
        await hub.Clients.Group(taskId).SendAsync("RequestHelp", new { taskId, question, image = base64Image, type });
    }

    /// <param name="result">"Success" | "Stopped"（取消→停止语义）| "Failed"</param>
    /// <param name="type">"record"（录制，默认）| "fill"（填报，App complete 回调端点用）</param>
    public static async Task TaskCompleted(this IHubContext<AgentHub> hub, string taskId, string result,
        object? script = null, string? errorMessage = null,
        Dictionary<string, object>? resultData = null, object? stats = null,
        string? failureType = null, string? screenshot = null,
        string type = "record")
    {
        await hub.Clients.Group(taskId).SendAsync("TaskCompleted", new
        {
            taskId,
            result,
            script,
            errorMessage,
            resultData,
            stats,
            failureType,
            screenshot,
            type
        });
    }
}
