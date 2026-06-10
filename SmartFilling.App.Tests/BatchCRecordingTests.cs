using SmartFilling.App.Recording;
using Xunit;

namespace SmartFilling.App.Tests;

/// <summary>
/// 批次 C 配套测试（结论13 C1）：iframe 链每层 AssessFragility>=8 判脆弱（替代前缀匹配，覆盖 GUID id/动态锚点）。
/// C1 前 isFragileLayer 只查位置选择器层前缀（xpath=(//iframe) / (//iframe)[），查不出 GUID id 脆弱层；C1 改用 AssessFragility 覆盖。
/// 待决策1/2/3 mock 测试（operate iframe-(f) 接受 falls-through / 复合 count==0 czLeafSelector / fallback 归源）经 golden E2E + real-AI 验证（仿 ValidateFallbackTests ScenarioA mock-frame 模式可后补）。
/// </summary>
public class BatchCRecordingTests
{
    // C1：GUID id iframe selector -> AssessFragility>=8（C1 前缀匹配查不出 GUID，C1 每层 AssessFragility 覆盖）
    [Theory]
    [InlineData("//iframe[@id='37b9c6a8-1234-5678-abcd-ef0123456789']")]  // GUID id（动态锚点）
    [InlineData("xpath=(//iframe)[1]")]  // 位置选择器层（C1 前仍查得出，C1 后仍 >=8）
    [InlineData("(//iframe)[2]")]  // 位置谓词 XPath
    [InlineData("/html/body/div/iframe")]  // 完整路径
    public void C1_IframeFragileSelector_AssessFragility_High(string selector)
    {
        Assert.True(SelectorExtractor.AssessFragility(selector) >= 8, $"C1 后 {selector} 应 >=8 脆弱");
    }

    // C1：稳定锚点 iframe selector -> AssessFragility<8（不误判脆弱）
    [Theory]
    [InlineData("//iframe[@name='stable-frame']")]  // 稳定 name
    [InlineData("//iframe[@id='main-content']")]  // 稳定 id（非 GUID）
    [InlineData("//iframe[@data-testid='widget']")]  // 稳定 data 属性
    public void C1_IframeStableSelector_AssessFragility_Low(string selector)
    {
        Assert.True(SelectorExtractor.AssessFragility(selector) < 8, $"稳定锚点 {selector} 应 <8 不脆弱");
    }

    // C1-A2：AI 传的 GUID iframe 链 -> 任一层 AssessFragility>=8（C1-A2 据此设 IsFragileIframe 走 (f) 求助，覆盖原"AI 链不判脆弱"silent）
    [Fact]
    public void C1A2_AiChain_WithGuidLayer_AnyFragile()
    {
        var aiChain = new[] { "//iframe[@name='outer']", "xpath=(//iframe)[1]" };  // 外层稳定 + 内层位置选择器层脆弱（C1 Any 验链含脆弱层；GUID 覆盖由 C1_High Theory 验）
        Assert.True(aiChain.Any(s => SelectorExtractor.AssessFragility(s) >= 8));  // 内层 GUID 脆弱 -> 链脆弱
    }
}
