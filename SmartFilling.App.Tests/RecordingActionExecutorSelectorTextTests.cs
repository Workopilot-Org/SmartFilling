using SmartFilling.App.Recording;
using Xunit;

namespace SmartFilling.App.Tests;

/// <summary>
/// dreamy A：ParametrizeSelectorText 纯函数单测（click selector 文本锚点参数化）。
/// D-2 放宽正则：normalize-space 覆盖 . / 空 / text() 三种第一参数；contains 覆盖 . 与 text()。
/// D-1 per-anchor 守卫：移除前置 selector.Contains("{{")，改 match 后查目标锚点引号内文本是否已含 {{}}（属性锚点 bizValues 产的 {{}} 不影响文本锚点参数化）。
/// R4-1 预过滤器：contains(text()) 也被 contains( 覆盖。
/// 断言验值（含/不含具体占位符），不只验不崩。
/// </summary>
public class RecordingActionExecutorSelectorTextTests
{
    [Fact]
    public void NormalizeSpace_NoDot_Parametrized()  // 核心回归：不带点 normalize-space()='xxx'
    {
        var sel = "//*[normalize-space()='C021_上海家化电子商务有限公司']";
        var result = RecordingActionExecutor.ParametrizeSelectorText(sel, "company", null);
        Assert.Contains("normalize-space(.)='{{company}}'", result);
        Assert.DoesNotContain("C021_上海家化", result);
    }

    [Fact]
    public void NormalizeSpace_Dot_NotRegress()  // 带点不回归
    {
        var sel = "//*[normalize-space(.)='登录']";
        var result = RecordingActionExecutor.ParametrizeSelectorText(sel, "loginLabel", null);
        Assert.Contains("normalize-space(.)='{{loginLabel}}'", result);
    }

    [Fact]
    public void NormalizeSpace_TextFunc_Parametrized()  // D-2：normalize-space(text())='xxx' 覆盖
    {
        var sel = "//*[normalize-space(text())='登录']";
        var result = RecordingActionExecutor.ParametrizeSelectorText(sel, "loginLabel", null);
        Assert.Contains("normalize-space(.)='{{loginLabel}}'", result);
        Assert.DoesNotContain("normalize-space(text())='登录'", result);
    }

    [Fact]
    public void Contains_Dot_Parametrized()
    {
        var sel = "//*[contains(.,'提交')]";
        var result = RecordingActionExecutor.ParametrizeSelectorText(sel, "submitLabel", null);
        Assert.Contains("contains(.,'{{submitLabel}}')", result);
    }

    [Fact]
    public void Contains_TextFunc_Parametrized()  // D-2 + R4-1：contains(text(),'xxx') 覆盖（预过滤器 contains( 放行 + 正则匹配）
    {
        var sel = "//*[contains(text(),'提交')]";
        var result = RecordingActionExecutor.ParametrizeSelectorText(sel, "submitLabel", null);
        Assert.Contains("contains(.,'{{submitLabel}}')", result);
        Assert.DoesNotContain("contains(text(),'提交')", result);
    }

    [Fact]
    public void NoTextAnchor_Unchanged()  // 无文本锚点 -> 不变
    {
        var sel = "//*[@id='submitBtn']";
        var result = RecordingActionExecutor.ParametrizeSelectorText(sel, "submit", null);
        Assert.Equal(sel, result);
    }

    [Fact]
    public void FieldName_Null_Unchanged()
    {
        var sel = "//*[normalize-space(.)='登录']";
        var result = RecordingActionExecutor.ParametrizeSelectorText(sel, null, null);
        Assert.Equal(sel, result);
    }

    [Fact]
    public void NonXPath_Unchanged()  // 非 // 开头 -> 不变（预过滤器 StartsWith("//")）
    {
        var sel = ".btn-primary";
        var result = RecordingActionExecutor.ParametrizeSelectorText(sel, "btn", null);
        Assert.Equal(sel, result);
    }

    [Fact]
    public void AlreadyParametrized_Skipped()  // D-1 per-anchor：目标锚点已含 {{}} 则跳过
    {
        var sel = "//*[normalize-space(.)='{{company}}']";
        var result = RecordingActionExecutor.ParametrizeSelectorText(sel, "company", null);
        Assert.Equal(sel, result);  // 不重复参数化
    }

    [Fact]
    public void PerAnchor_BizValueInAttrDoesNotSkipText()  // D-1：selector 同时含属性 {{companyCode}} + 文本锚点 '登录' -> A 仍参数化文本锚点
    {
        var sel = "//div[@id='dc_{{companyCode}}' and normalize-space(.)='登录']";
        var result = RecordingActionExecutor.ParametrizeSelectorText(sel, "loginLabel", null);
        Assert.Contains("normalize-space(.)='{{loginLabel}}'", result);  // 文本锚点参数化
        Assert.Contains("dc_{{companyCode}}", result);  // 属性锚点 {{}} 保留（不被 A 破坏）
    }

    [Fact]
    public void Cache_Filled_On_Match()  // F4：命中即填缓存 recordVarValues[fieldName] = 实际文本
    {
        var cache = new Dictionary<string, string>();
        var sel = "//*[normalize-space(.)='登录']";
        var result = RecordingActionExecutor.ParametrizeSelectorText(sel, "loginLabel", cache);
        Assert.True(cache.ContainsKey("loginLabel"));
        Assert.Equal("登录", cache["loginLabel"]);
    }

    [Fact]
    public void Cache_Null_NoFill()  // 纯函数测试传 null 不影响
    {
        var sel = "//*[normalize-space(.)='登录']";
        var result = RecordingActionExecutor.ParametrizeSelectorText(sel, "loginLabel", null);
        Assert.Contains("{{loginLabel}}", result);
    }
}
