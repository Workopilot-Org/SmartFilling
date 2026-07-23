using SmartFilling.App.Recording;
using Xunit;

namespace SmartFilling.App.Tests;

/// <summary>
/// dreamy B：ParametrizeValue 纯函数单测（fill/type/select 整值参数化，修原 L536 漏 type）。
/// 决策24 守卫：value 已含 {{}}（bizValues 已处理）则跳过。
/// F4 缓存：命中即填 recordVarValues[fieldName] = 参数化前原值。
/// 断言验值，不只验状态。
/// </summary>
public class RecordingActionExecutorValueParametrizeTests
{
    [Theory]
    [InlineData("fill", "张三", "username", "{{username}}")]
    [InlineData("type", "-玉泽", "suffix", "{{suffix}}")]  // type 修原漏（原 L536 只 fill/select）
    [InlineData("select", "1", "option", "{{option}}")]
    public void Value_Parametrized(string action, string value, string fieldName, string expected)
    {
        var result = RecordingActionExecutor.ParametrizeValue(action, value, fieldName, null);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Click_NotParametrized()  // click 不参数化 value
    {
        var result = RecordingActionExecutor.ParametrizeValue("click", "someValue", "field", null);
        Assert.Equal("someValue", result);
    }

    [Fact]
    public void AlreadyHasPlaceHolder_Skipped()  // 决策24 守卫：value 已含 {{}} 跳过
    {
        var result = RecordingActionExecutor.ParametrizeValue("fill", "{{company}}", "company", null);
        Assert.Equal("{{company}}", result);
    }

    [Fact]
    public void FieldName_Null_Unchanged()
    {
        var result = RecordingActionExecutor.ParametrizeValue("fill", "张三", null, null);
        Assert.Equal("张三", result);
    }

    [Fact]
    public void Value_Null_Unchanged()
    {
        var result = RecordingActionExecutor.ParametrizeValue("fill", null, "field", null);
        Assert.Null(result);
    }

    [Fact]
    public void Cache_Filled_With_Original_Value()  // F4：填参数化前原值
    {
        var cache = new Dictionary<string, string>();
        var result = RecordingActionExecutor.ParametrizeValue("fill", "张三", "username", cache);
        Assert.Equal("{{username}}", result);
        Assert.True(cache.ContainsKey("username"));
        Assert.Equal("张三", cache["username"]);  // 原值（参数化前）
    }

    [Fact]
    public void Cache_NotFilled_When_Skipped()  // 守卫跳过时不填缓存
    {
        var cache = new Dictionary<string, string>();
        var result = RecordingActionExecutor.ParametrizeValue("fill", "{{company}}", "company", cache);
        Assert.False(cache.ContainsKey("company"));
    }
}
