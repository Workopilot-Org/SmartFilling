using SmartFilling.Engine.Engine;
using SmartFilling.Engine.Services;

namespace SmartFilling.Engine.Tests.Unit;

/// <summary>
/// captcha click 补全的纯函数单测（3.F 路径 A 解析提示 / 路径 B 顶部行选择）。
/// ResolveClickOrderAsync 本身依赖 ILocator/CaptchaService（需真实浏览器），其核心逻辑抽为纯函数在此覆盖。
/// </summary>
public class CaptchaClickParserTests
{
    #region ParseClickPrompt（路径 A）

    [Fact]
    public void ParseClickPrompt_StripsGuidePhrase_AndSplitsBySpace()
    {
        var result = StepExecutor.ParseClickPrompt("请依次点击：苹 蕉 书");
        Assert.Equal(new[] { "苹", "蕉", "书" }, result);
    }

    [Fact]
    public void ParseClickPrompt_SplitsByEnumerationComma()
    {
        var result = StepExecutor.ParseClickPrompt("请按顺序点击：苹、蕉、书");
        Assert.Equal(new[] { "苹", "蕉", "书" }, result);
    }

    [Fact]
    public void ParseClickPrompt_HandlesChineseCommaAndColon()
    {
        var result = StepExecutor.ParseClickPrompt("请点击：苹，蕉，书");
        Assert.Equal(new[] { "苹", "蕉", "书" }, result);
    }

    [Fact]
    public void ParseClickPrompt_EmptyReturnsEmpty()
    {
        Assert.Empty(StepExecutor.ParseClickPrompt(""));
        Assert.Empty(StepExecutor.ParseClickPrompt("   "));
        Assert.Empty(StepExecutor.ParseClickPrompt("请依次点击："));  // 仅引导词，无目标
    }

    [Fact]
    public void ParseClickPrompt_NoGuidePhrase_StillSplits()
    {
        // 无引导词前缀（如 canvas OCR 只返回字序列），仍按分隔符切分
        var result = StepExecutor.ParseClickPrompt("苹 蕉 书");
        Assert.Equal(new[] { "苹", "蕉", "书" }, result);
    }

    #endregion

    #region StripGuideChars（路径 B 去引导词）

    [Fact]
    public void StripGuideChars_RemovesPromptPhraseKeepsTargets()
    {
        // 顶部行 detection 文本（按 X 序）：请/依/次/点/击/苹/蕉/书 → 去引导词后 = 苹/蕉/书
        var order = StepExecutor.StripGuideChars(new[] { "请", "依", "次", "点", "击", "苹", "蕉", "书" });
        Assert.Equal(new[] { "苹", "蕉", "书" }, order);
    }

    [Fact]
    public void StripGuideChars_AllTargets_NoGuidePhrase()
    {
        // 顶部行仅目标字（无引导词，生成测试图常用）→ 原样保留
        var order = StepExecutor.StripGuideChars(new[] { "苹", "蕉", "书" });
        Assert.Equal(new[] { "苹", "蕉", "书" }, order);
    }

    #endregion

    #region SelectTopRowItems（路径 B）

    [Fact]
    public void SelectTopRow_PicksTopCluster_SortedByX()
    {
        // 顶部行（提示，Y=30）3 字 + 下方目标行（Y=150）2 字；应只取顶部行按 X 排序
        var items = new List<DetectedItem>
        {
            new() { X = 100, Y = 30, Text = "蕉" },
            new() { X = 60, Y = 30, Text = "苹" },
            new() { X = 140, Y = 30, Text = "书" },
            new() { X = 80, Y = 150, Text = "苹" },
            new() { X = 200, Y = 150, Text = "书" }
        };

        var top = StepExecutor.SelectTopRowItems(items);

        Assert.Equal(3, top.Count);
        Assert.Equal(new[] { "苹", "蕉", "书" }, top.Select(i => i.Text).ToArray());  // 按 X 升序：60,100,140
    }

    [Fact]
    public void SelectTopRow_SingleRow_ReturnsAll()
    {
        var items = new List<DetectedItem>
        {
            new() { X = 10, Y = 50, Text = "A" },
            new() { X = 30, Y = 50, Text = "B" }
        };

        var top = StepExecutor.SelectTopRowItems(items);

        Assert.Equal(2, top.Count);  // 单行，全部属于顶部
    }

    [Fact]
    public void SelectTopRow_EmptyReturnsEmpty()
    {
        Assert.Empty(StepExecutor.SelectTopRowItems(new List<DetectedItem>()));
    }

    #endregion
}
