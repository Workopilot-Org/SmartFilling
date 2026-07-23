using SmartFilling.App.Recording;
using Xunit;

namespace SmartFilling.App.Tests;

/// <summary>
/// dreamy F：operate {{}} 守卫纯函数单测（CheckUnresolvedRecordVar + TrySubstituteFromRecordVars）。
/// F1 守卫：selector/value/url 含未解析 {{业务变量}} -> 返对应字段名（caller request_help）；系统凭据排除；navigate 不拦 selector；ref 路径放行。
/// F2 缓存：recordVarValues 命中则替换返 true（不问用户）；未命中返 false。
/// 断言验值（返字段名/替换后值），不只验状态。
/// 注：ExecuteOperateAsync 完整守卫流程（含 acceptAsIs/(h) 出口）需 mock IPage，留集成测试；本文件测纯函数逻辑。
/// </summary>
public class OperateVarGuardTests
{
    // ===== CheckUnresolvedRecordVar =====

    [Fact]
    public void Selector_With_PlaceHolder_Returns_Selector()
    {
        var field = RecordingActionExecutor.CheckUnresolvedRecordVar("click", "//div[normalize-space(.)='{{preApplyCode}}']", null, null);
        Assert.Equal("selector", field);
    }

    [Fact]
    public void Value_Fill_With_PlaceHolder_Returns_Value()
    {
        var field = RecordingActionExecutor.CheckUnresolvedRecordVar("fill", "//div", "{{preApplyCode}}", null);
        Assert.Equal("value", field);
    }

    [Fact]
    public void Value_Type_With_PlaceHolder_Returns_Value()  // type 也守卫（B 修原漏 type 对称）
    {
        var field = RecordingActionExecutor.CheckUnresolvedRecordVar("type", "//div", "{{suffix}}", null);
        Assert.Equal("value", field);
    }

    [Fact]
    public void Url_Navigate_With_PlaceHolder_Returns_Url()  // D-6：navigate 拦 url 不拦 selector
    {
        var field = RecordingActionExecutor.CheckUnresolvedRecordVar("navigate", null, "{{baseUrl}}/list", null);
        Assert.Equal("url", field);
    }

    [Fact]
    public void Navigate_DoesNot_Return_Selector()  // D-6：navigate 即使 selector 含 {{}} 也不返 selector（navigate 用 url 不用 selector）
    {
        var field = RecordingActionExecutor.CheckUnresolvedRecordVar("navigate", "//div[{{x}}]", "https://example.com", null);
        Assert.NotEqual("selector", field);
    }

    [Fact]
    public void SystemCredential_Username_NotGuarded()  // 系统凭据 {{username}} 排除
    {
        var field = RecordingActionExecutor.CheckUnresolvedRecordVar("fill", "//div", "{{username}}", null);
        Assert.Null(field);  // 不拦（由 ResolveSystemVars 替换）
    }

    [Fact]
    public void SystemCredential_Password_NotGuarded()
    {
        var field = RecordingActionExecutor.CheckUnresolvedRecordVar("fill", "//div", "{{password}}", null);
        Assert.Null(field);
    }

    [Fact]
    public void MixedSystemAndBiz_Guarded()  // 含系统凭据 + 业务变量 -> 仍拦（业务变量需守卫）
    {
        var field = RecordingActionExecutor.CheckUnresolvedRecordVar("fill", "//div", "{{username}}-{{companyCode}}", null);
        Assert.Equal("value", field);  // 含非系统凭据 companyCode -> 拦
    }

    [Fact]
    public void RefPath_NotGuardSelector()  // ref 路径放行（ExtractAsync 提取实际 selector 覆盖 {{}}）
    {
        var field = RecordingActionExecutor.CheckUnresolvedRecordVar("click", "//div[{{x}}]", null, "e27");
        Assert.Null(field);  // refValue 非空 -> 不返 selector
    }

    [Fact]
    public void NoPlaceHolder_Returns_Null()
    {
        var field = RecordingActionExecutor.CheckUnresolvedRecordVar("click", "//div[@id='btn']", null, null);
        Assert.Null(field);
    }

    [Fact]
    public void Click_NoSelector_NoGuard()
    {
        var field = RecordingActionExecutor.CheckUnresolvedRecordVar("click", null, null, null);
        Assert.Null(field);
    }

    // ===== TrySubstituteFromRecordVars（缓存复用）=====

    [Fact]
    public void Cache_Hit_Replaces_And_Returns_True()  // 缓存命中 -> 替换返 true（不问用户）
    {
        var cache = new Dictionary<string, string> { { "preApplyCode", "SQS2026071300003" } };
        var sel = "//div[normalize-space(.)='{{preApplyCode}}']";
        var ok = RecordingActionExecutor.TrySubstituteFromRecordVars(sel, null, "selector", cache, out var cachedSel, out var cachedVal);
        Assert.True(ok);
        Assert.Contains("SQS2026071300003", cachedSel);
        Assert.DoesNotContain("{{preApplyCode}}", cachedSel);
    }

    [Fact]
    public void Cache_Miss_Returns_False()  // 缓存未命中 -> 返 false（问用户）
    {
        var cache = new Dictionary<string, string>();  // 空
        var sel = "//div[{{preApplyCode}}]";
        var ok = RecordingActionExecutor.TrySubstituteFromRecordVars(sel, null, "selector", cache, out var cachedSel, out _);
        Assert.False(ok);
        Assert.Equal(sel, cachedSel);  // 未替换
    }

    [Fact]
    public void Cache_Null_Returns_False()  // recordVarValues=null -> 返 false
    {
        var sel = "//div[{{preApplyCode}}]";
        var ok = RecordingActionExecutor.TrySubstituteFromRecordVars(sel, null, "selector", null, out var cachedSel, out _);
        Assert.False(ok);
        Assert.Equal(sel, cachedSel);
    }

    [Fact]
    public void Cache_PartialHit_Returns_False()  // 部分 {{var}} 命中、部分未命中 -> 返 false（仍问用户）
    {
        var cache = new Dictionary<string, string> { { "a", "1" } };  // 只有 a，无 b
        var sel = "//div[{{a}}]/span[{{b}}]";
        var ok = RecordingActionExecutor.TrySubstituteFromRecordVars(sel, null, "selector", cache, out var cachedSel, out _);
        Assert.False(ok);
        Assert.Contains("1", cachedSel);  // a 被替换
        Assert.Contains("{{b}}", cachedSel);  // b 保留
    }

    [Fact]
    public void Cache_Value_Hit()  // value 缓存命中
    {
        var cache = new Dictionary<string, string> { { "companyCode", "021" } };
        var ok = RecordingActionExecutor.TrySubstituteFromRecordVars(null, "备注：{{companyCode}}", "value", cache, out _, out var cachedVal);
        Assert.True(ok);
        Assert.Equal("备注：021", cachedVal);
    }

    [Fact]
    public void Cache_Url_Hit()
    {
        var cache = new Dictionary<string, string> { { "baseUrl", "https://example.com" } };
        var ok = RecordingActionExecutor.TrySubstituteFromRecordVars(null, "{{baseUrl}}/list", "url", cache, out _, out var cachedVal);
        Assert.True(ok);
        Assert.Equal("https://example.com/list", cachedVal);
    }
}
