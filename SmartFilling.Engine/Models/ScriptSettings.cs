namespace SmartFilling.Engine.Models;

public record ScriptSettings
{
    public int? DefaultTimeout { get; init; }
    public StepRetry? StepRetry { get; init; }
    public string? OnError { get; init; }
    public Viewport? Viewport { get; init; }
    public int? MaxAiTurns { get; init; }
    public ScreenshotOptions? Screenshot { get; init; }
    public int? MaxScriptDuration { get; init; }
    public int? MaxLoopCount { get; init; }
    /// <summary>同一 gotoKey 反复跳转上限（决策9：脚本级覆盖引擎默认；per-gotoKey 局部死循环保护）</summary>
    public int? MaxPhaseGotoCount { get; init; }
    /// <summary>脚本级 goto toPhase 全局累计上限（决策9：脚本级覆盖；不按 phase 重置，专防跨 phase 流浪）</summary>
    public int? MaxPhaseJumpCount { get; init; }
    /// <summary>重跑(rerun)配置（决策13/T.14：row_rerun/phase_rerun，脚本级覆盖引擎默认）</summary>
    public RerunOptions? Rerun { get; init; }
    /// <summary>步骤间固定等待(ms)：每个 step 完成后、下一步开始前等待。0或负数=不等待。脚本级覆盖 EngineOptions.StepIntervalMs。
    /// 适用范围：改变状态类 action（fill/click/navigate/ai 等）后等待；wait/check/extract/screenshot/goto 跳过（不改变页面状态）；evaluate 参与。</summary>
    public int? StepIntervalMs { get; init; }
}

public record Viewport
{
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;
}
