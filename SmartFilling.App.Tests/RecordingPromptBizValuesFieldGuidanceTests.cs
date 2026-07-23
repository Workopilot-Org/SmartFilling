using SmartFilling.App.Prompts;
using Xunit;

namespace SmartFilling.App.Tests;

/// <summary>
/// dreamy TD-8 C+D：验证 RecordingPrompt.SystemPrompt 含 bizValues.field 取法指引（防措辞被误删）。
/// 根因：代码层 BuildPhaseStatus 已每轮注入 registeredFields（已注册字段名列表），但提示词未教 AI 用它取 bizValues.field，
/// 致 AI 编造 field 名 → bizErr（操作已执行未落盘）。C+D 补提示词教 AI"field 从 registeredFields 取 + 不编造 + 自动推导"。
/// 断言验措辞存在（不只验不崩），防回归。
/// </summary>
public class RecordingPromptBizValuesFieldGuidanceTests
{
    [Fact]
    public void SystemPrompt_Contains_RegisteredFields_Guidance()
    {
        var prompt = RecordingPrompt.SystemPrompt;
        // C+D 核心：教 AI bizValues.field 从 registeredFields 取（代码已注入该列表到每轮状态 JSON）
        Assert.Contains("registeredFields", prompt);
        Assert.Contains("field 从哪取", prompt);
    }

    [Fact]
    public void SystemPrompt_Contains_DoNotFabricate_Guidance()
    {
        var prompt = RecordingPrompt.SystemPrompt;
        // 防 AI 编造 field 名（TD-8 根因）：不确定时 request_help，不自己编英文名
        Assert.Contains("不要自己编", prompt);  // 实际措辞："不要自己编一个看起来合理的英文名"
        Assert.Contains("request_help", prompt);
    }

    [Fact]
    public void SystemPrompt_Contains_AutoDerive_Guidance()
    {
        var prompt = RecordingPrompt.SystemPrompt;
        // 决策3：fill/type/select value+fieldName 自动推导入 bizValues，无需手动重复传
        Assert.Contains("自动推导", prompt);
    }

    [Fact]
    public void SystemPrompt_Contains_RegisterFirst_Guidance()
    {
        var prompt = RecordingPrompt.SystemPrompt;
        // 未注册字段先用 fill/type 注册再 bizValues 引用
        Assert.Contains("先用 fill/type", prompt);
        Assert.Contains("注册", prompt);
    }

    [Fact]
    public void BuildPromptWithFields_Contains_BizValues_Guidance()
    {
        // BuildPromptWithFields 附加的 value 说明句含 bizValues 指引（fieldsJson 非空时附加）
        var prompt = RecordingPrompt.BuildPromptWithFields("base", "fieldsJson");
        Assert.Contains("bizValues", prompt);
        Assert.Contains("实际值", prompt);
    }
}
