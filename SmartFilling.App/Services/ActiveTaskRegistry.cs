using System.Collections.Concurrent;

namespace SmartFilling.App.Services;

/// <summary>
/// App 活跃任务注册表（P2）：单例 ConcurrentDictionary（仅存 taskId 存在性）。
/// StartDynamic 提交后 Register(taskId)；complete 回调端点收 Worker 终态后 Unregister(taskId)。
/// 模式 C（第8轮决策修正）：Register 无条件执行（与 HealthCheck.Enabled 无关——修复 Bug：门控导致 HealthCheck.Enabled=false 时进度推送全 404）。
/// WorkerHealthCheckService 定时遍历注册表 GET Worker /health?taskId=xxx 检测崩溃/卡死——卡死判定用 Worker 返回的 health.LastActivity 自算（不读本地注册表时间）。
/// 注：第10轮核查发现 LastSeen/StartTime 字段 + Get 方法无读写点（dead field，silent-success），已删——本表仅作"存在性集合"，不存时间戳（任务活动时间由 Worker TaskActivityTracker 经 /health 返回）。
/// </summary>
public class ActiveTaskRegistry
{
    private readonly ConcurrentDictionary<string, byte> _tasks = new();

    public void Register(string taskId) => _tasks[taskId] = 0;

    public bool Exists(string taskId) => _tasks.ContainsKey(taskId);

    public ICollection<string> GetAllTaskIds() => _tasks.Keys;

    public void Unregister(string taskId) => _tasks.TryRemove(taskId, out _);
}
