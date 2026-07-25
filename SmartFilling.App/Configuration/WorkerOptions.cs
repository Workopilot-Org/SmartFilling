namespace SmartFilling.App.Configuration;

public class WorkerOptions
{
    public string BaseUrl { get; set; } = "http://localhost:5000";
    public int StartTaskTimeoutSeconds { get; set; } = 30;

    /// <summary>503 退避重试（决策10：上游 503 退避重试，不做公平调度；对象格式 {Count,Interval} 与 CompleteReport.PushOptions.Retry 风格一致）。</summary>
    public WorkerBusyRetryOptions WorkerBusyRetry { get; set; } = new();

    /// <summary>Worker 健康检测（G1-11/P2：定时 GET /health?taskId=xxx，连续失败判崩溃，lastActivity 超时判卡死，推 AgentHub 通知前端）。</summary>
    public HealthCheckOptions HealthCheck { get; set; } = new();
}

public class WorkerBusyRetryOptions
{
    public int Count { get; set; } = 3;
    public int Interval { get; set; } = 1000;
}

public class HealthCheckOptions
{
    /// <summary>是否启用健康检测（模式 C：关时不登记/不扫描，但 ActiveTaskRegistry 单例始终创建）。</summary>
    public bool Enabled { get; set; } = false;
    /// <summary>扫描间隔秒。</summary>
    public int CheckIntervalSeconds { get; set; } = 30;
    /// <summary>连续失败次数判 Worker 崩溃。</summary>
    public int MaxConsecutiveFailures { get; set; } = 3;
    /// <summary>lastActivity 超时秒判任务卡死（G1-11：默认 600s=MaxScriptDuration；最小值建议 = DefaultAiTimeout 180s 避免误判）。</summary>
    public int TaskStallTimeoutSeconds { get; set; } = 600;
}
