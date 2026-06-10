using SmartFilling.App.Recording;
using Xunit;

namespace SmartFilling.App.Tests;

/// <summary>
/// a-cached-hopper（2026-07-16）：字段重名 (b) 换名 hint 构造纯函数单测。
/// 验证 BuildRenameHint 透传用户原话（不截断/不解析）+ 教 AI 两条规则（用户给名→用该名 / 只选 b→AI 起名）。
/// 断言验值不只验状态：精确串验透传位置 + 规则引导文本 + 旧「用户建议」误导措辞回归守护（防改回旧措辞）。
/// BuildRenameHint 提 internal static 供直测（纯函数 string 拼接无副作用，对齐 BuildHelpQuestionTests 惯例）。
/// answerText 传已去 "用户回答:" 前缀的纯回答（模拟 ExecuteOperateAsync L676 提取后的值，非带前缀的 reply 原值）。
/// </summary>
public class RenameHintTests
{
    // 用例 1：只回 b（无具体名）→ 透传原话 "b" + 触发规则②「由 AI 起名」+ 不含旧「用户建议：b」误导措辞。
    [Fact]
    public void OnlyB_PassesThroughAndAiNames()
    {
        var hint = RecordingEngine.BuildRenameHint("preApplyCode", "b");
        Assert.Contains("「preApplyCode」", hint);  // fieldName 透传
        Assert.Contains("用户原话：\"b\"", hint);  // 原话透传不截断（精确串验透传位置）
        Assert.Contains("由你起一个未被占用的字段名", hint);  // 规则②引导
        Assert.DoesNotContain("用户建议", hint);  // 旧「用户建议：b」误导措辞回归守护（防改回旧措辞）
    }

    // 用例 2：回具体名 billNo → 透传 + 触发规则①「用用户给的名」。
    [Fact]
    public void ConcreteName_PassesThroughAndUseGivenName()
    {
        var hint = RecordingEngine.BuildRenameHint("preApplyCode", "billNo");
        Assert.Contains("用户原话：\"billNo\"", hint);  // 原话透传
        Assert.Contains("就使用用户给的那个名字", hint);  // 规则①引导
    }

    // 用例 3：描述式「新名字用billNo,选b」→ 原话整句透传不截断、不解析（验自然语言措辞多样时不依赖正则枚举）+ 规则引导。
    [Fact]
    public void DescriptivePhrase_PassesThroughVerbatim()
    {
        var hint = RecordingEngine.BuildRenameHint("preApplyCode", "新名字用billNo,选b");
        Assert.Contains("用户原话：\"新名字用billNo,选b\"", hint);  // 整句透传不截断/不解析
        Assert.Contains("请按用户原话判断新字段名", hint);  // 规则引导
    }

    // 用例 4：措辞变体「用 billNo」（无前缀）→ 透传 + 引导（验不依赖特定前缀格式）。
    [Fact]
    public void VariantPhrase_PassesThrough()
    {
        var hint = RecordingEngine.BuildRenameHint("preApplyCode", "用 billNo");
        Assert.Contains("用户原话：\"用 billNo\"", hint);  // 措辞变体透传
        Assert.Contains("就使用用户给的那个名字", hint);  // 规则①引导
    }
}
