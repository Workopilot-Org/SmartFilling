using SmartFilling.App.Recording;
using Xunit;

namespace SmartFilling.App.Tests;

/// <summary>
/// 2c B6 AssessFragility 单测：selector 字符串脆弱度粗分级（复用 IsStableValue 判动态值锚点）。
/// 断言验值（priority 数值），不只验不崩。回归：GUID→8 / 稳定 id→1 / name→2 / data→3 / 文本→4 / 其他稳定属性→5 / contains→7 / 纯位置→8 / 空→9。
/// 🔴 GUID selector 误判稳定是 silent 高危（zgy 框架案例根因），专项回归。
/// </summary>
public class AssessFragilityTests
{
    [Theory]
    [InlineData("//*[@id='username']", 1)]                      // 稳定 id
    [InlineData("//*[@name='phone']", 2)]                       // 稳定 name
    [InlineData("//*[@data-testid='submit']", 3)]               // 稳定 data-*
    [InlineData("//*[@id='37b9c6a8-1234-5678-abcd-ef0123456789']", 8)]  // GUID id 动态 → 8
    [InlineData("//*[@id='zgy_frame_menu']", 1)]                // 含下划线但稳定（非 GUID/长数字/框架前缀）
    [InlineData("//*[@id='ext-gen1234']", 8)]                   // 框架前缀+序号动态 → 8
    [InlineData("//*[@id='Z_1775616840']", 8)]                   // 长数字（6位+）动态 → 8
    [InlineData("//*[normalize-space(.)='登录']", 4)]           // 精确文本
    [InlineData("//*[@aria-label='关闭']", 5)]                  // 其他稳定属性
    [InlineData("//*[@placeholder='请输入']", 5)]               // 其他稳定属性
    [InlineData("//*[contains(concat(' ',@class,' '),' btn-primary ')]", 5)]  // class 精确匹配（ExtractAsync priority 5）
    [InlineData("//*[contains(@class,'btn')]", 7)]              // contains 属性 → 7
    [InlineData("//*[contains(.,'提交')]", 7)]                  // contains 文本 → 7
    [InlineData("/html/body/div[2]/button", 8)]                 // 完整路径 → 8
    [InlineData("(//iframe)[1]", 8)]                            // 位置谓词 → 8
    [InlineData(".btn-primary", 7)]                             // CSS selector → 7（保守，onError 兜底）
    [InlineData("", 9)]                                         // 空 → 9
    [InlineData("   ", 9)]                                      // 空白 → 9
    public void AssessFragility_Returns_Expected_Priority(string selector, int expected)
    {
        var actual = SelectorExtractor.AssessFragility(selector);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void AssessFragility_Null_Returns_9()
    {
        Assert.Equal(9, SelectorExtractor.AssessFragility(null));
    }

    [Fact]
    public void AssessFragility_GuidId_High_Fragility()  // 🔴 GUID selector 误判稳定是 silent 高危（zgy 案例根因），专项回归
    {
        // GUID id 不应判稳定（IsStableValue 拦截）→ priority 8（高脆弱），不 silent 落盘
        var prio = SelectorExtractor.AssessFragility("//*[@id='37b9c6a8-1234-5678-abcd-ef0123456789']");
        Assert.True(prio >= 8, $"GUID id selector 应判高脆弱（>=8），实际 {prio}——误判稳定会 silent 落盘");
    }

    [Fact]
    public void AssessFragility_StableId_Low_Priority()  // 稳定 id 不求助（priority 1-6，priority 8 才求助）
    {
        var prio = SelectorExtractor.AssessFragility("//*[@id='username']");
        Assert.InRange(prio, 1, 6);
    }

    [Fact]
    public void AssessFragility_Combined_Takes_Best_Anchor()  // 组合 selector 取最优锚点分级
    {
        // id 动态（完整 GUID）+ name 稳定 → name 锚点有效 → priority 2（不因 id 动态降级到 8）
        var prio = SelectorExtractor.AssessFragility("//*[@id='37b9c6a8-1234-5678-abcd-ef0123456789']//*[@name='phone']");
        Assert.Equal(2, prio);
    }
}
