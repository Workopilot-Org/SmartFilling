using SmartFilling.Engine.Logging;

namespace SmartFilling.Engine.Models;

/// <summary>失败类型枚举，用于区分不同失败原因（运维诊断/监控告警）</summary>
public enum FailureType
{
    /// <summary>任务成功</summary>
    None,
    /// <summary>确定性失败：step action 执行失败，未触发 AI 或 AI 未被配置</summary>
    Deterministic,
    /// <summary>AI Fallback 失败：触发了 AI 兜底但 AI 也失败</summary>
    AiFallback,
    /// <summary>AI Phase 失败：ai phase（主路径 AI 自主执行）失败——区别于 AiFallback（兜底），此为 AI 主路径执行未达成</summary>
    AiPhase,
    /// <summary>超时：totalTimeout 触发</summary>
    Timeout,
    /// <summary>取消：用户主动取消</summary>
    Cancelled
}

public record ScriptResult
{
    public bool Success { get; init; }
    public List<StepLog> ExecutionLog { get; init; } = [];
    public Dictionary<string, object>? ReturnData { get; init; }
    public string? ErrorMessage { get; init; }
    public Dictionary<string, object>? Vars { get; init; }
    public int TotalDurationMs { get; init; }
    public ExecutionStats? Stats { get; init; }
    public string? Status { get; init; } // "completed" / "failed"
    /// <summary>失败类型（仅 Success=false 时有意义）</summary>
    public FailureType FailureType { get; init; }
}

public record PhaseResult(bool Success)
{
    public string? Message { get; init; }
    public string? Type { get; init; } // "goto"
    public string? ToPhase { get; init; }
    public string? ToStep { get; init; }
    public List<int>? TargetPath { get; init; }
    public string? Status { get; init; } // "phase_success" / "script_success" / "phase_fail" / "script_fail"
    public int? FailedStepIndex { get; init; } // A8: 失败步骤索引（-1 表示未知）
}

public record StepResult(object? Value = null)
{
    public ControlFlowResult? ControlFlow { get; init; }
    public int AiTurnsUsed { get; init; }
    public int AiInputTokens { get; init; }
    public int AiOutputTokens { get; init; }
    public int SnapshotCount { get; init; }
    public int AiScreenshotCount { get; init; }
    public int SnapshotTokens { get; init; }
    public int AiCallsWithScreenshot { get; init; }
    public int ScreenshotTokens { get; init; }
}

public record ControlFlowResult(StepNode Step)
{
    public string Then => Step.Then ?? "nothing";
    public string? Message => Step.Message;
    public string? ToPhase => Step.ToPhase;
    public string? ToStep => Step.ToStep;
    public List<StepNode>? CleanupSteps => Step.CleanupSteps;
}
