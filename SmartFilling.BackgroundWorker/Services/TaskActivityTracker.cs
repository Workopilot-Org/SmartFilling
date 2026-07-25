using System.Collections.Concurrent;

namespace SmartFilling.BackgroundWorker.Services;

/// <summary>
/// 任务活动时间跟踪（G1-11/P1）：单例 ConcurrentDictionary&lt;taskId, lastActivity&gt;。
/// 供 /health?taskId=xxx 查询任务是否在跑 + 最近活动时间（App/PlatformAdapter 健康检测用）。
/// Touch 由 ScriptEngine.StepActivity 事件触发（覆盖确定性步骤 + AI fallback + AI action + AI phase 全路径，不依赖 reporter）。
/// Register/Unregister 由 TaskExecutionService 在获取槽位后/终态后调用（与槽位生命周期绑定）。
/// </summary>
public class TaskActivityTracker
{
    private readonly ConcurrentDictionary<string, DateTime> _activities = new();

    public void Register(string taskId, DateTime startTime) => _activities[taskId] = startTime;

    /// <summary>Touch 仅更新已注册任务（不 Register 新 taskId——任务必先经 Register）。</summary>
    public void Touch(string taskId)
    {
        if (_activities.ContainsKey(taskId))
            _activities[taskId] = DateTime.UtcNow;
    }

    public void Unregister(string taskId) => _activities.TryRemove(taskId, out _);

    public DateTime? GetLastActivity(string taskId)
        => _activities.TryGetValue(taskId, out var t) ? t : null;

    public bool IsRegistered(string taskId) => _activities.ContainsKey(taskId);
}
