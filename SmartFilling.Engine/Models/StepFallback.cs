using System.Text.Json.Serialization;

namespace SmartFilling.Engine.Models;

public record StepFallback
{
    /// <summary>
    /// 录制期目标元素 ref 编号（aria-ref）。结论8：fallback selector 维度（对齐 DetectCondition.Ref/StepNode），
    /// 代码 ExtractSelectorFromRefAsync 提取 selector 写入 Selector。回放不消费；录制提取后清空，最终脚本不含此字段。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Ref { get; init; }
    /// <summary>
    /// 目标元素选择器（XPath 或 CSS）。支持 {{vars}} 变量替换。
    /// </summary>
    public string? Selector { get; init; }
    public string? Action { get; init; }
    public string? Value { get; init; }
    public string? Code { get; init; }
    /// <summary>覆盖 step 的 iframe selector 链（H.4.7，形态 A 后为 string[] 链）；未配则继承 step.Iframe</summary>
    public string[]? Iframe { get; init; }
    /// <summary>
    /// 录制期 iframe 元素 ref 编号（aria-ref，指向 &lt;iframe&gt; 元素本身）。结论14：fallback iframe 维度（与 Ref 并列，两字段独立），
    /// 代码 ResolveIframeFromRefAsync 提取链写入 Iframe。回放不消费；录制提取后清空，最终脚本不含此字段。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IframeRef { get; init; }
    /// <summary>覆盖 step 的超时（H.4.7）；未配则继承 step.Timeout</summary>
    public int? Timeout { get; init; }
    public StepFallback? Fallback { get; init; }
}
