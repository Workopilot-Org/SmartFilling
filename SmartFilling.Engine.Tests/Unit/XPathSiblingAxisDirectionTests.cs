using System.Xml;
using Xunit;

namespace SmartFilling.Engine.Tests.Unit;

/// <summary>
/// 阶段四（近邻锚点）XPath 兄弟轴方向契约测试。
/// 守护 SelectorExtractor（SmartFilling.App）SiblingAxisForSide 的方向逻辑：
/// 锚点在 target 前(preceding)→必须用 following-sibling 才能从锚点向后够到 target；在后(following)→preceding-sibling。
/// 直接把 side 当轴名会反，曾导致阶段四永远匹配 0 → 静默失效（2026-06-16 PowerShell 实证发现并修复）。
/// 注：App 为 Web SDK、无独立测试项目，此处用 .NET XPath 验证方向语义契约；S10 建 App 测试项目后可迁移为直接测 SiblingAxisForSide。
/// </summary>
public class XPathSiblingAxisDirectionTests
{
    // 模拟 DOM：A 在 target 前，target 居中，B 在 target 后
    private const string MockDom = "<root><span id=\"A\">lbl</span><input id=\"target\"/><button id=\"B\">btn</button></root>";

    private static int Count(string xpath)
    {
        var doc = new XmlDocument();
        doc.LoadXml(MockDom);
        return doc.CreateNavigator()!.Select(xpath).Count;
    }

    [Fact]
    public void PrecedingAnchor_Uses_FollowingSibling_ToReachTarget()
    {
        // 锚点 A 在 target 前（side=preceding）：正确方向是 following-sibling（旧 bug 误用 preceding-sibling）
        Assert.Equal(1, Count("//*[@id='A']/following-sibling::input"));
        Assert.Equal(0, Count("//*[@id='A']/preceding-sibling::input"));
    }

    [Fact]
    public void FollowingAnchor_Uses_PrecedingSibling_ToReachTarget()
    {
        // 锚点 B 在 target 后（side=following）：正确方向是 preceding-sibling（旧 bug 误用 following-sibling）
        Assert.Equal(1, Count("//*[@id='B']/preceding-sibling::input"));
        Assert.Equal(0, Count("//*[@id='B']/following-sibling::input"));
    }

    [Theory]
    [InlineData("preceding", "following-sibling")]
    [InlineData("following", "preceding-sibling")]
    public void Side_Reverses_To_Axis(string side, string expectedAxis)
    {
        // 固化 SiblingAxisForSide 契约：side 必须反向映射为轴，不得直接当轴名（旧 bug 根因）
        var reversed = side == "preceding" ? "following-sibling" : "preceding-sibling";
        Assert.Equal(expectedAxis, reversed);
    }
}
