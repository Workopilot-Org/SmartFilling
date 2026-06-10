using System.Text.Json.Serialization;

namespace SmartFilling.Engine.Models;

public record DetectCondition
{
    public string? Type { get; init; }
    /// <summary>
    /// 录制期元素 ref 编号（aria-ref）。detect 同源 operate：代码据此从 DOM 提取最优 XPath 写入 Selector（附录 R）。
    /// 回放不消费；录制提取后清空，最终脚本不含此字段。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Ref { get; init; }
    /// <summary>
    /// 录制期 iframe 元素 ref 编号（aria-ref，指向 &lt;iframe&gt; 元素本身）。结论14：detect 级（check.detect/wait.until/step.condition）
    /// + phase 级 Condition/LoopCondition + document_ready 都可用 iframeRef 指认 iframe，代码 ResolveIframeFromRefAsync 提取链写入 Iframe。
    /// 回放不消费；录制提取后清空，最终脚本不含此字段。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IframeRef { get; init; }
    /// <summary>
    /// 目标元素选择器（XPath 或 CSS）。支持 {{vars}} 变量替换。
    /// </summary>
    public string? Selector { get; init; }
    public string? Value { get; init; }
    public string[]? Keywords { get; init; }
    public string? Field { get; init; }
    public string? Check { get; init; }
    public string? UrlContains { get; init; }
    public int? Count { get; init; }
    /// <summary>iframe selector 链（根→叶，形态 A），detect 的父上下文。null=未配；[]=强制主文档；有内容=指定链。支持 {{vars}}。</summary>
    public string[]? Iframe { get; init; }

    // 组合条件
    public List<DetectCondition>? All { get; init; }
    public List<DetectCondition>? Any { get; init; }
    public DetectCondition? Not { get; init; }
}
