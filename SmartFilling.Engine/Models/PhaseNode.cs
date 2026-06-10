namespace SmartFilling.Engine.Models;

public record PhaseNode : PhaseItem
{
    public string Name { get; init; } = "";
    public string? Type { get; init; } // "sequential"(default) / "loop" / "ai"
    public string? AiGoal { get; set; }  // init → set，允许录制完成后 AI 总结更新
    public DetectCondition? Condition { get; init; }
    public string? OnError { get; init; }
    /// <summary>iframe selector 链（根→叶，形态 A）。null=未配（绝对链）；[]=强制主文档；有内容=指定链。支持 {{vars}}。</summary>
    public string[]? Iframe { get; init; }

    // loop phase
    public string? LoopSource { get; init; }
    public DetectCondition? LoopCondition { get; init; }
    public int? MaxLoopCount { get; init; }
    public int? RowIndexOffset { get; init; }

    // ai phase
    public int? MaxAiTurns { get; init; }
    public int? Timeout { get; init; }

    // 子节点列表（step 和 phase 混合）
    public List<PhaseItem> Steps { get; set; } = [];
}
