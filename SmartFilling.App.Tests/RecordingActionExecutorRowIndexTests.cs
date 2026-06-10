using SmartFilling.App.Recording;
using Xunit;

namespace SmartFilling.App.Tests;

/// <summary>
/// 决策 D3/F⑤：录制期 loop 内多独立 input selector 行索引参数化纯逻辑单测。
/// 直接调 ParametrizeRowIndex（不依赖浏览器），验参数化正则结果（属性优先 → 位置回退 → 多匹配歧义）。
/// 对齐回放 ScriptEngine loop（{{rowIndex}} 经 ReplaceVars 替换为 rowIdx+offset）。
/// 对应计划 F⑤ 6 用例 + mode 覆盖。
/// </summary>
public class RecordingActionExecutorRowIndexTests
{
    // ===== 自动模式（mode=null：属性优先 → 回退位置 → 多匹配歧义）=====

    [Fact]
    public void Attr_RowIndex_Single_Parametrized()
    {
        var (sel, amb) = RecordingActionExecutor.ParametrizeRowIndex("//*[@rowindex='1' and @colname='F_X']", null);
        Assert.Equal("//*[@rowindex='{{rowIndex}}' and @colname='F_X']", sel);
        Assert.False(amb);
    }

    [Fact]
    public void Attr_DataRow_Single_Parametrized()
    {
        var (sel, amb) = RecordingActionExecutor.ParametrizeRowIndex("//tr[@data-row='2']//input", null);
        Assert.Equal("//tr[@data-row='{{rowIndex}}']//input", sel);
        Assert.False(amb);
    }

    [Fact]
    public void Position_Tr_Single_Parametrized()
    {
        var (sel, amb) = RecordingActionExecutor.ParametrizeRowIndex("//tbody/tr[3]/td/input", null);
        Assert.Equal("//tbody/tr[{{rowIndex}}]/td/input", sel);
        Assert.False(amb);
    }

    [Fact]
    public void NoRowIndex_Unchanged()
    {
        // 同 input 场景（无行索引）→ 不变，不歧义
        var (sel, amb) = RecordingActionExecutor.ParametrizeRowIndex("//input[@id='file']", null);
        Assert.Equal("//input[@id='file']", sel);
        Assert.False(amb);
    }

    [Fact]
    public void MultipleBrackets_RowColumnAmbiguous()
    {
        // tr[1] + td[2] = 多[数字]，行列分不清 → 歧义（触发 request_help 兜底），selector 不变
        var (sel, amb) = RecordingActionExecutor.ParametrizeRowIndex("//tbody/tr[1]/td[2]/input", null);
        Assert.Equal("//tbody/tr[1]/td[2]/input", sel);
        Assert.True(amb);
    }

    [Fact]
    public void AttrAndPosition_AttrPriority()
    {
        // 属性优先：@rowindex='1' 命中（count=1）→ 参数化属性并返回，不碰 tr[2]（位置回退不触发）
        var (sel, amb) = RecordingActionExecutor.ParametrizeRowIndex("//*[@rowindex='1']//tr[2]", null);
        Assert.Equal("//*[@rowindex='{{rowIndex}}']//tr[2]", sel);
        Assert.False(amb);
    }

    // ===== mode 显式覆盖 =====

    [Fact]
    public void ModeAttr_ForcesAttrOnly_NoPositionFallback()
    {
        // mode=attr：仅属性模式，无属性行索引 → 不回退位置 → 不变（即便有 tr[3]）
        var (sel, amb) = RecordingActionExecutor.ParametrizeRowIndex("//tbody/tr[3]/td/input", "attr");
        Assert.Equal("//tbody/tr[3]/td/input", sel);
        Assert.False(amb);
    }

    [Fact]
    public void ModePosition_ForcesPosition_IgnoresAttrPriority()
    {
        // mode=position：仅位置模式，忽略属性优先 → 参数化 tr[2]（不动 @rowindex='1'）
        var (sel, amb) = RecordingActionExecutor.ParametrizeRowIndex("//*[@rowindex='1']//tr[2]", "position");
        Assert.Equal("//*[@rowindex='1']//tr[{{rowIndex}}]", sel);
        Assert.False(amb);
    }

    [Fact]
    public void MultipleAttr_Ambiguous()
    {
        // 多个属性行索引 = 歧义（无法确定哪个是行索引）
        var (sel, amb) = RecordingActionExecutor.ParametrizeRowIndex("//*[@rowindex='1' and @data-row='2']", null);
        Assert.Equal("//*[@rowindex='1' and @data-row='2']", sel);
        Assert.True(amb);
    }
}
