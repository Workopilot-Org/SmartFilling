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

public static class AgentHubExtensions
{
    public static async Task SendLog(this IHubContext<AgentHub> hub, string taskId, string message)
    {
        await hub.Clients.Group(taskId).SendAsync("ReceiveLog", new { taskId, message, timestamp = DateTime.Now });
    }

    public static async Task SendScreenshot(this IHubContext<AgentHub> hub, string taskId, string base64Image)
    {
        await hub.Clients.Group(taskId).SendAsync("ReceiveScreenshot", new { taskId, image = base64Image });
    }

    public static async Task RequestHelp(this IHubContext<AgentHub> hub, string taskId, string question, string base64Image)
    {
        await hub.Clients.Group(taskId).SendAsync("RequestHelp", new { taskId, question, image = base64Image });
    }

    public static async Task TaskCompleted(this IHubContext<AgentHub> hub, string taskId, string result, object? script = null, string? errorMessage = null)
    {
        // #38 决策6 方案b：统一封装 TaskCompleted（Success/Cancelled/Failed 三态），payload {taskId, result, script, errorMessage} 与原 RecordController 三处内联一致
        await hub.Clients.Group(taskId).SendAsync("TaskCompleted", new { taskId, result, script, errorMessage });
    }
}
