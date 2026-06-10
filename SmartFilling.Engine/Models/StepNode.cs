using System.Text.Json.Serialization;

namespace SmartFilling.Engine.Models;

public record StepNode : PhaseItem
{
    public string Action { get; init; } = "";

    // 基础交互
    /// <summary>
    /// 元素选择器。录制时自动生成 XPath 格式，手工编辑时也可用 CSS。
    /// 支持 {{vars}} 变量替换。
    /// </summary>
    public string? Selector { get; init; }
    public string? Value { get; init; }
    public string? Url { get; init; }
    public string? Key { get; init; }
    public string? Code { get; init; }
    public string? FilePath { get; init; }
    /// <summary>
    /// iframe selector 链（根→叶，每层一个 selector，XPath 或 CSS）。形态 A（2026-07-02）：
    /// 录制时沿 frame.ParentFrame 提取每层 selector 固化为 string[]（无 Id 反查/无 iframeConfig）。
    /// 支持 {{vars}} 变量替换（回放 ResolveChain 内部逐层 ReplaceVars）。
    /// null/省略 = 未配（继承 phase.Iframe）；[]（显式空数组）= 强制主文档（不继承）；有内容 = 指定链。
    /// </summary>
    public string[]? Iframe { get; init; }
    /// <summary>
    /// 录制期 iframe 元素 ref 编号（aria-ref，指向 &lt;iframe&gt; 元素本身）。D-H/结论14：AI 用 aria-ref 指认 iframe 元素，
    /// 代码 ResolveIframeFromRefAsync 定位 iframe DOM → 提取 selector 链写入 Iframe。回放不消费；录制提取后清空，最终脚本不含此字段。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IframeRef { get; init; }
    public string? Field { get; init; }
    public object? StoreAs { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? Property { get; init; }
    public string? ExtractType { get; init; }
    public string? Regex { get; init; }
    public string? Direction { get; init; }
    public int? Amount { get; init; }
    public int? Index { get; init; }
    public bool? UseLast { get; init; }
    public bool? DoubleClick { get; init; }
    public bool? PressEnter { get; init; }
    public bool? SkipIfDataEmpty { get; init; }      // 原 skipIfEmpty（S4 重命名）：fillData 字段为空时跳过
    public bool? SkipIfElementMissing { get; init; } // 原 skipIfMissing（S4 重命名）：元素不存在时跳过
    public bool? Accept { get; init; }
    public string? DialogPromptText { get; init; }

    // screenshot
    public string? Folder { get; init; }           // screenshot 保存文件夹
    public string? Filename { get; init; }          // screenshot 文件名（不含扩展名），空则自动生成
    public string? ScreenshotType { get; init; }    // null 表示 "viewport"，见 switch default 分支
    public bool? SaveToFile { get; init; }          // null 表示 true（保存文件），见 != false 检查
    public string? StoreContent { get; init; }      // null 表示 "path"，见 switch _ 分支
    public List<object>? Args { get; init; }
    public int? PollInterval { get; init; }
    public string? MatchBy { get; init; }

    // captcha 验证码
    public string? CaptchaType { get; init; }
    public string? ImageSelector { get; init; }
    public string? InputSelector { get; init; }
    public string? SliderSelector { get; init; }
    public string? TargetSelector { get; init; }
    public string? BackgroundSelector { get; init; }

    // check 动作
    public DetectCondition? Detect { get; init; }
    public string? Then { get; init; }
    public string? Message { get; init; }
    public string? ToPhase { get; init; }
    public string? ToStep { get; init; }
    public List<StepNode>? CleanupSteps { get; init; }

    // wait
    public int? Ms { get; init; }
    public DetectCondition? Until { get; init; }

    // modifiers
    public DetectCondition? Condition { get; init; }
    public StepFallback? Fallback { get; init; }
    public PreSetup? PreSetup { get; init; }
    public StepRetry? Retry { get; init; }
    public string? OnError { get; init; }
    public int? Timeout { get; init; }
    public int? MaxAiTurns { get; init; }
}
