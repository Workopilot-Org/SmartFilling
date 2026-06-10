namespace SmartFilling.App.Recording;

/// <summary>
/// 通用求助菜单选项（a-a-4 P2.5，T5 配套）：位枚举，场景按需组合 options 传 BuildHelpQuestion。
/// (k) Other = T5 兜底，BuildHelpQuestion 方法体自动 options|=Other 保证始终含（回复不含字母→作(k)其它，AI 自由分析不填集合）。
/// Common5 = 公共 5 选项 (a-e)，绝大多数场景含。
/// </summary>
[System.Flags]
public enum HelpOption
{
    None = 0,
    Relocate = 1 << 0,        // (a) 重新定位
    AnalyzeHtml = 1 << 1,     // (b) 粘贴 HTML
    ManualSelector = 1 << 2,  // (c) 手写 selector
    AiAction = 1 << 3,        // (d) ai 节点
    Skip = 1 << 4,            // (e) 跳过
    AcceptFragile = 1 << 5,   // (f) 使用脆弱的（acceptFragile，跳 AssessFragility）
    EvaluateJs = 1 << 6,      // (g) 改 evaluate+check(js)
    AcceptAsIs = 1 << 7,      // (h) 用当前值跳过校验（acceptAsIs，跳存在性）
    IframeRef = 1 << 8,       // (i) 用 ref 指认 iframe
    FramePicker = 1 << 9,     // (j) 指认目标 frame（多 frame 时）
    Other = 1 << 10,          // (k) 其它（自由描述，AI 分析后处理）
    UseExtractedHtml = 1 << 11, // (l) 用代码已提取的 HTML（放法 X 块A，2026-07-14）：仅能提取 HTML 的场景显示（selector: priority 9/priority 8 脆弱；iframe: 脆弱层）。选(l)->代码 HTML 全量喂 AI；不并入 Common5。
    Common5 = Relocate | AnalyzeHtml | ManualSelector | AiAction | Skip,
}

/// <summary>
/// (j) 多 frame 求助时传的 frame 列表项（ND8）：name/url/文本片段（截前 50 字），让用户据特征选编号。
/// </summary>
public record FrameInfo(string? Name, string? Url, string? InnerTextSnippet);

/// <summary>
/// ND8（a-a-4，选 A+B·2026-07-08）：FindFrameForTargetAsync 多 frame 命中项——FrameInfo（求助菜单展示）+ 该 frame 的 selector 链 Chain。
/// 用户 (j) 选编号 N → caller 用 multiFrames[N-1].Chain 落盘（避免重提取）。>=2 命中时 FindFrameForTargetAsync 填列表返。
/// </summary>
public record MultiFrameHit(FrameInfo Info, string[] Chain);
