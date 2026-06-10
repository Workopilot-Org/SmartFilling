using SmartFilling.Engine.Engine;
using Xunit;

namespace SmartFilling.Engine.Tests.Unit;

/// <summary>
/// 形态 A（2026-07-02）：IframeChainMerger.Resolve D3 三态合并语义单测（纯逻辑，断言验值不只验状态）。
/// D3 选 (b)：null=未配（继承 phase）；[]=显式主文档（不继承）；有内容=指定链。
/// </summary>
public class IframeChainMergerTests
{
    [Fact]
    public void Resolve_BothNull_ReturnsNull()
    {
        Assert.Null(IframeChainMerger.Resolve(null, null));
    }

    [Fact]
    public void Resolve_StepContentNullPhase_UsesStep()
    {
        var step = new[] { "//iframe[@id='a']" };
        Assert.Equal(step, IframeChainMerger.Resolve(step, null));
    }

    [Fact]
    public void Resolve_StepNullContentPhase_InheritsPhase()
    {
        var phase = new[] { "//iframe[@id='b']" };
        Assert.Equal(phase, IframeChainMerger.Resolve(null, phase));
    }

    [Fact]
    public void Resolve_StepEmptyArray_D3ExplicitMainDoc_DoesNotInheritPhase()
    {
        // D3 关键：[]（显式空数组）= 强制主文档（不继承 phase），区别于 null（继承）
        var phase = new[] { "//iframe[@id='phase-frame']" };
        var result = IframeChainMerger.Resolve(Array.Empty<string>(), phase);
        Assert.NotNull(result);
        Assert.Empty(result);  // 返 []（主文档），非 phase 链
    }

    [Fact]
    public void Resolve_BothContent_UsesStep_AbsoluteChainNotConcat()
    {
        // 绝对链，非前缀拼接：step 有内容用 step（不拼 phase）
        var step = new[] { "//iframe[@id='step-a']" };
        var phase = new[] { "//iframe[@id='phase-x']" };
        var result = IframeChainMerger.Resolve(step, phase);
        Assert.Single(result!);
        Assert.Equal("//iframe[@id='step-a']", result![0]);
    }
}
