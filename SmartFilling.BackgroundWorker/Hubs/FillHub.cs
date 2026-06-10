using Microsoft.AspNetCore.SignalR;

namespace SmartFilling.BackgroundWorker.Hubs;

public class FillHub : Hub
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
