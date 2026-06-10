using SmartFilling.App.Recording;
using Xunit;

namespace SmartFilling.App.Tests;

/// <summary>
/// §8 selector 稳定性检测通用化单测（纯静态方法 IsStableValue/TryExtractStablePart，无需 mock 浏览器）。
/// 验证"按动态信号通用识别"（GUID 片段去锚+大小写 / 长数字 6 位 / 框架前缀清单去 Z_ / :r 修 / hash 加大写）
/// 替代旧"按见过的样例硬编码前缀（Z_/ext-gen/…）"。核心回归：zgy_iframe_&lt;GUID&gt;(带前缀+大写) 判不稳定。
/// test-covers-production-deserialize-path：纯静态方法直接调生产方法，无反序列化层，类型路径=生产。
/// </summary>
public class SelectorExtractorStableValueTests
{
    // ===== 规则1：GUID 片段（去锚+大小写，覆盖纯/带前缀/带后缀）=====

    [Fact]
    public void Guid_WithPrefixAndUppercase_IsUnstable()
    {
        // 🔴 核心回归：zgy_iframe_<GUID>（带前缀+大写 GUID）原整串小写 ^GUID$ 漏判为稳定 → BuildFrameSelectorAsync 生成 GUID selector 回放失效
        Assert.False(SelectorExtractor.IsStableValue("zgy_iframe_88B620E6-1234-5678-9abc-def012345678", "id"));
    }

    [Fact]
    public void Guid_PureLowercase_IsUnstable()
    {
        Assert.False(SelectorExtractor.IsStableValue("88b620e6-1234-5678-9abc-def012345678", "id"));
    }

    [Fact]
    public void Guid_NoHyphen_IsStable()
    {
        // 88B620E6 无连字符 → 不符合 GUID 格式 → 稳定（与有连字符 GUID 区分）
        Assert.True(SelectorExtractor.IsStableValue("88B620E6", "id"));
    }

    // ===== 规则2：长数字 \d{6,}（去 Z_ 依赖）=====

    [Theory]
    [InlineData("Z_1775616840")]  // Z_+时间戳（10 位）→ 不稳定
    [InlineData("prefix_123456")] // 任意前缀+6 位数字 → 不稳定
    [InlineData("12345678")]      // 纯 8 位数字 → 不稳定（规则2 先于规则4）
    public void LongDigits_IsUnstable(string value)
    {
        Assert.False(SelectorExtractor.IsStableValue(value, "id"));
    }

    [Theory]
    [InlineData("Z_123")]   // Z_+3 位 → 稳定（Z_ 已去，短数字 <6）
    [InlineData("Z_1")]     // Z_+1 位 → 稳定（Z_ 已去）
    [InlineData("Z_12345")] // Z_+5 位 → 稳定（边界：<6 位）
    public void ShortDigits_IsStable(string value)
    {
        Assert.True(SelectorExtractor.IsStableValue(value, "id"));
    }

    // ===== 规则3：框架前缀清单（4 条合并去 Z_，短序号兜底）=====

    [Theory]
    [InlineData("ext-gen1234")]   // ExtJS
    [InlineData("layui-layer5")]  // Layui
    [InlineData("rc-table-1")]    // AntDesign（rc-[\w]+-）
    public void FrameworkPrefix_IsUnstable(string value)
    {
        Assert.False(SelectorExtractor.IsStableValue(value, "id"));
    }

    [Fact]
    public void Z_ShortPrefix_IsStable_AfterZ_Removal()
    {
        // Z_ 已从清单去除；Z_1/Z_42 短序号漏判为稳定（已接受代价：Z_<时间戳>6+位被规则2 覆盖）
        Assert.True(SelectorExtractor.IsStableValue("Z_42", "id"));
    }

    // ===== 规则4：纯数字 =====

    [Fact]
    public void PureDigits_IsUnstable()
    {
        Assert.False(SelectorExtractor.IsStableValue("123", "id"));  // 3 位纯数字
    }

    // ===== 规则5：React :r（[a-z]→[a-z0-9]，覆盖 React18 :r1:）=====

    [Fact]
    public void React_r1_IsUnstable_Fixed()
    {
        // 🔴 修复：原 ^:r[a-z]\d*:$ 不匹配 :r1:（r 后直接数字无字母）→ React18 ref 漏判稳定；修复后 [a-z0-9]* 覆盖
        Assert.False(SelectorExtractor.IsStableValue(":r1:", "id"));
    }

    [Fact]
    public void React_r_PureLetter_IsUnstable()
    {
        Assert.False(SelectorExtractor.IsStableValue(":r5:", "id"));
    }

    [Fact]
    public void React_UppercaseR_IsStable()
    {
        // :R1: 大写 R 不匹配（规则要小写 r）→ 稳定
        Assert.True(SelectorExtractor.IsStableValue(":R1:", "id"));
    }

    // ===== 规则6：CSS hash（仅 class，加大写）=====

    [Fact]
    public void CssHash_Uppercase_IsUnstable_ClassOnly()
    {
        // btn_1A2B3C4D（class）→ 8 位含大写 hex → 不稳定
        Assert.False(SelectorExtractor.IsStableValue("btn_1A2B3C4D", "class"));
    }

    [Fact]
    public void CssHash_Short_IsStable()
    {
        // btn_1a2b（class，4 位 <5）→ 稳定
        Assert.True(SelectorExtractor.IsStableValue("btn_1a2b", "class"));
    }

    [Fact]
    public void CssHash_NotClass_IsStable()
    {
        // btn_1A2B3C4D 作为 id（非 class）→ 规则6 不检测 → 稳定（hash 仅 class）
        Assert.True(SelectorExtractor.IsStableValue("btn_1A2B3C4D", "id"));
    }

    // ===== 规则7：URL 参数（仅 src/href，不变）=====

    [Fact]
    public void UrlQuery_IsUnstable_SrcHrefOnly()
    {
        Assert.False(SelectorExtractor.IsStableValue("/path?a=1", "src"));
        Assert.False(SelectorExtractor.IsStableValue("/page?token=xyz", "href"));
    }

    [Fact]
    public void UrlQuery_NotSrcHref_IsStable()
    {
        // /path?a=1 作为 id（非 src/href）→ 规则7 不检测 → 稳定
        Assert.True(SelectorExtractor.IsStableValue("/path?a=1", "id"));
    }

    // ===== 边界 + 业务回归（应判稳定）=====

    [Fact]
    public void Empty_IsUnstable()
    {
        Assert.False(SelectorExtractor.IsStableValue("", "id"));
        Assert.False(SelectorExtractor.IsStableValue(null!, "id"));
    }

    [Theory]
    [InlineData("zgy_add", "id")]        // 业务 id（回归 zgy 场景）
    [InlineData("btn-primary", "class")] // 业务 class
    [InlineData("form-field-3", "id")]   // 业务 id 含短数字
    [InlineData("username", "name")]     // 普通字段名
    public void BusinessValues_AreStable(string value, string attr)
    {
        Assert.True(SelectorExtractor.IsStableValue(value, attr));
    }

    // ===== TryExtractStablePart（去动态部分留稳定片段，用于 contains 匹配）=====

    [Fact]
    public void TryExtract_LongDigits_Z_Prefix()
    {
        // Z_1775616840 → 规则2 Replace → "Z_"（长度 2）→ 返回 "Z_"
        Assert.Equal("Z_", SelectorExtractor.TryExtractStablePart("Z_1775616840", "id"));
    }

    [Fact]
    public void TryExtract_FrameworkPrefix_ToEmpty_Null()
    {
        // ext-gen1234 → 规则3 Replace 整串 → "" → 长度 <2 → null
        Assert.Null(SelectorExtractor.TryExtractStablePart("ext-gen1234", "id"));
    }

    [Fact]
    public void TryExtract_CssHash_StablePrefix()
    {
        // btn_1a2b3c4d（class）→ 去 hash → "btn"（长度 3）
        Assert.Equal("btn", SelectorExtractor.TryExtractStablePart("btn_1a2b3c4d", "class"));
    }

    [Fact]
    public void TryExtract_StableValue_NoMatch_Null()
    {
        // 稳定值无动态部分 → null（无需 contains）
        Assert.Null(SelectorExtractor.TryExtractStablePart("zgy_add", "id"));
    }
}
