namespace SmartFilling.Engine.Models;

/// <summary>
/// loopElement 配置（DOM 驱动循环，loop-variable-support 功能2）。
/// PhaseNode.LoopElement 非 null 时走 ExecuteLoopElementPhaseAsync（形态 A），与 LoopSource 互斥。
/// </summary>
public record LoopElementConfig
{
    /// <summary>DOM 元素选择器（行选择器，必填）。匹配多元素，按匹配数循环。支持 {{vars}}/{{_rowIndex}}（嵌套时含外层行号）。</summary>
    public string Selector { get; init; } = "";
    /// <summary>累积目标变量名（可选，非 field.name）。无则纯操作不累积；有则每行数据追加到此数组（后续 loopSource 可经 vars fallback 引用）。</summary>
    public string? StoreAs { get; init; }
    /// <summary>"table"（默认，对象数组，step.storeAs 做字段）/ "array"（标量数组 或 数组数组，值来源唯一）。仅 StoreAs 存在时有意义。</summary>
    public string? StoreStructure { get; init; }
};

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
    /// <summary>DOM 驱动循环配置（loop-variable-support 功能2）。与 LoopSource 互斥。非 null 时走 ExecuteLoopElementPhaseAsync。</summary>
    public LoopElementConfig? LoopElement { get; init; }

    // ai phase
    public int? MaxAiTurns { get; init; }
    public int? Timeout { get; init; }

    // 子节点列表（step 和 phase 混合）
    public List<PhaseItem> Steps { get; set; } = [];
}
