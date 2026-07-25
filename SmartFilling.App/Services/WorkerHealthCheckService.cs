using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SmartFilling.App.Configuration;
using SmartFilling.App.Hubs;

namespace SmartFilling.App.Services;

/// <summary>
/// Worker 健康检测后台服务（G1-11/P2）：定时扫描 ActiveTaskRegistry，GET Worker /health?taskId=xxx。
/// 连续 MaxConsecutiveFailures 次失败判 Worker 崩溃；lastActivity 超 TaskStallTimeoutSeconds 判任务卡死。
/// 判定后推 AgentHub TaskCompleted(result="Failed", type="fill") 通知前端，然后 Unregister。
/// 模式 C：HealthCheck.Enabled=false 时不启动定时扫描（StartAsync 直接返回）。
/// </summary>
public class WorkerHealthCheckService : IHostedService, IDisposable
{
    private readonly ActiveTaskRegistry _registry;
    private readonly WorkerApiClient _workerClient;
    private readonly IHubContext<AgentHub> _hub;
    private readonly WorkerOptions _options;
    private readonly ILogger<WorkerHealthCheckService> _logger;
    private Timer? _timer;
    private readonly ConcurrentDictionary<string, int> _failureCounts = new();

    public WorkerHealthCheckService(
        ActiveTaskRegistry registry,
        WorkerApiClient workerClient,
        IHubContext<AgentHub> hub,
        IOptions<WorkerOptions> options,
        ILogger<WorkerHealthCheckService> logger)
    {
        _registry = registry;
        _workerClient = workerClient;
        _hub = hub;
        _options = options.Value;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (!_options.HealthCheck.Enabled)
            return Task.CompletedTask;  // 模式 C：关时不启动定时扫描
        var interval = _options.HealthCheck.CheckIntervalSeconds > 0 ? _options.HealthCheck.CheckIntervalSeconds : 30;
        _timer = new Timer(async _ => await SafeCheckAsync(), null,
            TimeSpan.FromSeconds(interval), TimeSpan.FromSeconds(interval));
        _logger.LogInformation("Worker 健康检测已启用，扫描间隔 {Interval}s", interval);
        return Task.CompletedTask;
    }

    private async Task SafeCheckAsync()
    {
        try { await CheckAsync(); }
        catch (Exception ex) { _logger.LogError(ex, "Worker 健康检测扫描异常"); }
    }

    private async Task CheckAsync()
    {
        var taskIds = _registry.GetAllTaskIds().ToList();
        foreach (var taskId in taskIds)
        {
            try
            {
                var health = await _workerClient.HealthCheckAsync(taskId);
                if (health == null || health.TaskStatus != "running")
                {
                    // not_found 或 Worker 不健康 → 计数
                    var count = _failureCounts.AddOrUpdate(taskId, 1, (_, c) => c + 1);
                    if (count >= _options.HealthCheck.MaxConsecutiveFailures)
                        await NotifyFailure(taskId, $"Worker 连接中断（连续 {count} 次检测失败）");
                }
                else
                {
                    _failureCounts.TryRemove(taskId, out _);  // running 重置计数
                    // 检查 lastActivity 是否超时（卡死）
                    if (health.LastActivity.HasValue)
                    {
                        var stalledSeconds = (DateTime.UtcNow - health.LastActivity.Value).TotalSeconds;
                        if (stalledSeconds > _options.HealthCheck.TaskStallTimeoutSeconds)
                            await NotifyFailure(taskId, $"任务卡死（lastActivity {stalledSeconds:F0}s 前未更新）");
                    }
                }
            }
            catch (Exception ex)
            {
                var count = _failureCounts.AddOrUpdate(taskId, 1, (_, c) => c + 1);
                if (count >= _options.HealthCheck.MaxConsecutiveFailures)
                    await NotifyFailure(taskId, $"Worker 连接中断（异常: {ex.Message}）");
            }
        }
    }

    private async Task NotifyFailure(string taskId, string reason)
    {
        _logger.LogWarning("Worker 健康检测判定任务失败: {TaskId} - {Reason}", taskId, reason);
        // 先推前端（result="Failed"），后清理（L2：先推后清防 push 失败时任务已移除致前端收不到通知）
        await _hub.TaskCompleted(taskId, "Failed", errorMessage: reason, type: "fill");
        _registry.Unregister(taskId);
        _failureCounts.TryRemove(taskId, out _);
    }

    public Task StopAsync(CancellationToken ct)
    {
        _timer?.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();
}
