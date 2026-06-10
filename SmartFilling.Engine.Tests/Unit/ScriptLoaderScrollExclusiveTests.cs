using SmartFilling.Engine.Engine;
using SmartFilling.Engine.Models;
using SmartFilling.Engine.Tests.Helpers;

namespace SmartFilling.Engine.Tests.Unit;

/// <summary>
/// 批次9④ ScriptLoader.ValidateScrollExclusive 加载期校验（scroll selector/direction 严格二选一）。
/// 逻辑等同回放③ + schema⑤ + 录制①（多层兜底）；手写脚本写错加载期即拦，不等到运行时。
/// 断言验值（错误消息含"互斥"/"需指定"），不只验抛异常。
/// </summary>
public class ScriptLoaderScrollExclusiveTests
{
    private static ScriptV2 BuildScriptWithScroll(string? selector, string? direction)
    {
        var step = ScriptBuilder.Step("scroll", selector: selector, direction: direction, name: "scroll-step");
        return new ScriptBuilder()
            .AddPhase("main", "sequential", steps: new List<PhaseItem> { step }, aiGoal: "test")
            .Build();
    }

    [Fact]
    public void Scroll_BothSelectorAndDirection_FailsValidation()
    {
        var script = BuildScriptWithScroll("//*[@id='x']", "down");
        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.Validate(script));
        Assert.Contains("互斥", ex.Message);
    }

    [Fact]
    public void Scroll_NeitherSelectorNorDirection_FailsValidation()
    {
        var script = BuildScriptWithScroll(null, null);
        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.Validate(script));
        Assert.Contains("需指定", ex.Message);
    }

    [Fact]
    public void Scroll_SelectorOnly_PassesValidation()
    {
        var script = BuildScriptWithScroll("//*[@id='x']", null);
        ScriptLoader.Validate(script);  // 不抛即 PASS（selector 模式合法）
    }

    [Fact]
    public void Scroll_DirectionOnly_PassesValidation()
    {
        var script = BuildScriptWithScroll(null, "bottom");
        ScriptLoader.Validate(script);  // 不抛即 PASS（direction 模式合法）
    }

    [Fact]
    public void Scroll_EmptyDirectionString_TreatedAsUnspecified_NeitherFails()
    {
        // 核查第9轮🟡3 / C⑤：direction:"" 空串四层判据统一为「视为未指定」（ScriptLoader !IsNullOrEmpty）。
        // selector 空且 direction="" → 两者都视为未指定 → 需指定之一 → 报错（边界 silent-success：空串不能伪装成"指定了 direction"）。
        var script = BuildScriptWithScroll(null, "");
        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.Validate(script));
        Assert.Contains("需指定", ex.Message);
    }

    [Fact]
    public void Scroll_EmptyDirectionString_TreatedAsUnspecified_SelectorOnlyPasses()
    {
        // direction="" + selector 已设 → 视为 selector-only（合法），不误判为"同传"。
        var script = BuildScriptWithScroll("//*[@id='x']", "");
        ScriptLoader.Validate(script);  // 不抛即 PASS（空串 direction 不算同传）
    }

    [Fact]
    public void Scroll_NestedInLoopPhase_Validated()
    {
        // 嵌套 phase 内的 scroll 也校验（ValidateScrollExclusive 递归）
        var step = ScriptBuilder.Step("scroll", selector: "//a", direction: "up", name: "nested-scroll");
        var loopPhase = new PhaseNode
        {
            Kind = "phase", Name = "loop", Type = "loop", LoopSource = "items",
            AiGoal = "loop", Steps = new List<PhaseItem> { step }
        };
        var script = new ScriptBuilder()
            .AddPhase("outer", "sequential", steps: new List<PhaseItem> { loopPhase }, aiGoal: "outer")
            .Build();
        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.Validate(script));
        Assert.Contains("互斥", ex.Message);
    }
}
