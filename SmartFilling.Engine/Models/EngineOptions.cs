namespace SmartFilling.Engine.Models;

public record EngineOptions
{
    public bool Headless { get; init; } = true;
    public int SlowMo { get; init; }
    public Viewport? Viewport { get; init; }
    public bool DebugMode { get; init; }
    public ScreenshotOptions? Screenshot { get; init; }
    public string? BrowserPath { get; init; }
    public int DefaultTimeout { get; init; } = 30000;
    public StepRetry? StepRetry { get; init; }
    public string? OnError { get; init; } = "stop";
    public int WaitCheckInterval { get; init; } = 500;
    public int MaxLoopCount { get; init; } = 100;
    public int MaxPhaseGotoCount { get; init; } = 20;
    public int MaxPhaseJumpCount { get; init; } = 10;
    public int MaxAiTurns { get; init; } = 30;
    /// <summary>AI 兜底（ai action / step 级 fallback / phase 级 fallback）的独立总超时（ms）。不套用 step.Timeout（确定性操作超时套到多轮 AI 必超时）。默认 180000（3 分钟）。</summary>
    public int DefaultAiTimeout { get; init; } = 180000;
    public int MaxScriptDuration { get; init; } = 600000;
    /// <summary>重跑(rerun)配置：row_rerun/phase_rerun 的次数与间隔（决策13/T.14，替代废弃的 MaxPhaseRetries）</summary>
    public RerunOptions? Rerun { get; init; } = new();
    public CompressionOptions? Compression { get; init; }
    /// <summary>附件根目录。空(null/"")时由 Worker/App 启动 PostConfigure 注入 ContentRootPath（确保附件下载保存与 upload action 查找用同一根目录）。设为 set 以支持启动时注入。</summary>
    public string? UploadRootPath { get; set; }
}

/// <summary>重跑(rerun)配置（决策13/T.14）：row_rerun=loop 当前行重跑，phase_rerun=整个 phase 重跑</summary>
public record RerunOptions
{
    /// <summary>loop 当前行重跑(row_rerun)上限</summary>
    public int MaxRowRerunCount { get; init; } = 2;
    /// <summary>整个 phase 重跑(phase_rerun)上限</summary>
    public int MaxPhaseRerunCount { get; init; } = 2;
    /// <summary>重跑间隔(ms)，重跑前等待页面恢复</summary>
    public int Interval { get; init; } = 1000;
}

public record CompressionOptions
{
    public int Threshold { get; init; } = 30;
    public int MinimumPreserved { get; init; } = 5;
    public bool PreserveInitialUserMessages { get; init; } = true;
}
