using SmartFilling.App.Recording;
using Xunit;

namespace SmartFilling.App.Tests;

/// <summary>
/// 24（a-a-4 B，2026-07-08）：BuildHelpQuestion HelpOption 矩阵测试。
/// 验证各 options 组合渲染对应求助字母 (a)-(k)——silent-success 防护：选项漏渲染→用户无法选→求助失灵/silent 走错分支。
/// BuildHelpQuestion 提 internal static 供直调（纯函数 string 拼接，无副作用/无 mock）。
/// </summary>
public class BuildHelpQuestionTests
{
    private static string Build(HelpOption options, IReadOnlyList<FrameInfo>? frames = null, string[]? iframeChain = null, string? selector = null, string? warning = "警告") =>
        RecordingEngine.BuildHelpQuestion(1, "p", "s", "click", "描述", warning, selector, iframeChain, options, frames);

    // Common5 → (a)(b)(c)(d)(e) + 始终(k)；未传的 (f)-(j) 不渲染（验不含，防多余选项误导）。
    [Fact]
    public void Common5_RendersABCDE_K_OthersAbsent()
    {
        var q = Build(HelpOption.Common5);
        Assert.Contains("(a) 重新定位", q);
        Assert.Contains("(b) AI 分析 HTML", q);
        Assert.Contains("(c) 手写 selector", q);
        Assert.Contains("(d) ai 节点", q);
        Assert.Contains("(e) 跳过", q);
        Assert.Contains("(k) 其它", q);  // T5：(k) 始终含（方法体 options|=Other）
        Assert.DoesNotContain("(f) 使用脆弱", q);
        Assert.DoesNotContain("(g) 改用 evaluate", q);
        Assert.DoesNotContain("(h) 用当前", q);
        Assert.DoesNotContain("(i) 用 ref 指认", q);
        Assert.DoesNotContain("(j) 指认目标 frame", q);
        Assert.DoesNotContain("(l) 用代码已提取的 HTML", q);  // 放法 X 块A：Common5 不渲染 (l)（防 silent 多余选项）
    }

    // 放法 X 块A（2026-07-14）：UseExtractedHtml -> 渲染 (l)（priority 9/8 脆弱/iframe 脆弱层场景）。
    [Fact]
    public void UseExtractedHtml_RendersL() => Assert.Contains("(l) 用代码已提取的 HTML", Build(HelpOption.Common5 | HelpOption.UseExtractedHtml));

    [Fact]
    public void AcceptFragile_RendersF() => Assert.Contains("(f) 使用脆弱", Build(HelpOption.Common5 | HelpOption.AcceptFragile));

    [Fact]
    public void AcceptAsIs_RendersH() => Assert.Contains("(h) 用当前", Build(HelpOption.Common5 | HelpOption.AcceptAsIs));

    [Fact]
    public void IframeRef_RendersI() => Assert.Contains("(i) 用 ref 指认", Build(HelpOption.Common5 | HelpOption.IframeRef));

    [Fact]
    public void EvaluateJs_RendersG() => Assert.Contains("(g) 改用 evaluate", Build(HelpOption.Common5 | HelpOption.EvaluateJs));

    // FramePicker + frames → (j) + frame 列表（含无 name/snippet 的兜底渲染）。
    [Fact]
    public void FramePicker_RendersJFrameList()
    {
        var frames = new List<FrameInfo>
        {
            new("frame-a", "http://a", "内容A"),
            new(null, "http://b", null),
        };
        var q = Build(HelpOption.Common5 | HelpOption.FramePicker, frames);
        Assert.Contains("(j) 指认目标 frame", q);
        Assert.Contains("1. [name=frame-a] url=http://a 含\"内容A\"", q);
        Assert.Contains("2. [name=(无)] url=http://b", q);  // 无 name/snippet 兜底
    }

    // FramePicker 无 frames → 不渲染 (j)（HasFlag + frames 非空双条件）。
    [Fact]
    public void FramePicker_NoFrames_NoJ() => Assert.DoesNotContain("(j) 指认目标 frame", Build(HelpOption.Common5 | HelpOption.FramePicker));

    // None → 只 (k)（兜底，回复可不带字母）。
    [Fact]
    public void None_OnlyK()
    {
        var q = Build(HelpOption.None);
        Assert.Contains("(k) 其它", q);
        Assert.DoesNotContain("(a) 重新定位", q);  // Common5 未传
    }

    // warning/selector/iframeChain 渲染（验值，不只验选项字母）。
    [Fact]
    public void WarningSelectorIframeChain_Rendered()
    {
        var q = Build(HelpOption.Common5, iframeChain: new[] { "//iframe[@id='x']" }, selector: "#btn");
        Assert.Contains("⚠️ 警告", q);
        Assert.Contains("已生成 selector：#btn", q);
        Assert.Contains("iframe 链：//iframe[@id='x']", q);
    }
}
