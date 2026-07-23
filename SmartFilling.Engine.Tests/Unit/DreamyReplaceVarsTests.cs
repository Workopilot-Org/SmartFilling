using SmartFilling.Engine.Engine;
using SmartFilling.Engine.Models;
using Xunit;

namespace SmartFilling.Engine.Tests.Unit;

/// <summary>
/// dreamy D：回放端 9 字段加 ReplaceVars 单测。
/// D1 goalChain（ScriptEngine L1243/L1339）+ aiGoal（L688/L1259/L1354）：GoalStack 含 {{var}} -> 渲染时 ReplaceVars 替换。
/// D2 captcha 5 selector（StepExecutor ExecuteCaptchaAsync 入口）：5 selector 含 {{var}} -> 入口替换后 case 读替换后值。
/// D3 PreSetup.DialogText（StepExecutor L57）：dialog.Message.Contains(ReplaceVars(DialogText))。
/// D4 DialogPromptText（StepExecutor L392）：dialog.AcceptAsync(ReplaceVars(DialogPromptText))。
/// 本文件用 VariableHelper.ReplaceVars 直测 D 的替换逻辑（D 改动核心是"在使用点包 ReplaceVars"），验值（替换后具体字符串）。
/// 完整流程 mock IPage/IDialog 集成测试留后续（D2 需 CaptchaService mock、D3/D4 需触发 page.Dialog 事件，复杂度高）。
/// </summary>
public class DreamyReplaceVarsTests
{
    private static List<Dictionary<string, object>> Scope() => new() { new() };
    private static Dictionary<string, object> Vars(string key, string value) => new() { { key, value } };

    // ===== D1 goalChain + aiGoal =====

    [Fact]
    public void D1_GoalChain_ReplaceVars_PreservesIndent()  // goalChain 带索引缩进，包 g 加 ReplaceVars 保留缩进
    {
        // 模拟 ScriptEngine L1243：Select((g, i) => $"{new string(' ', i*2)}- {ReplaceVars(g, ...)}")
        var goalStack = new[] { "为 {{company}} 填报合同", "填写 {{company}} 详情" };
        var vars = Vars("company", "上海家化");
        // goalStack = [栈底 script.Description, 栈顶 phase]；Reverse -> [phase(i=0 无缩进), script.Description(i=1 缩进 2)]
        var rendered = string.Join("\n", goalStack.Reverse().Select((g, i) =>
            $"{new string(' ', i * 2)}- {VariableHelper.ReplaceVars(g, Scope(), vars)}"));
        Assert.Contains("- 填写 上海家化 详情", rendered);  // 栈顶 phase 无缩进
        Assert.Contains("  - 为 上海家化 填报合同", rendered);  // 栈底 script.Description 缩进 2
        Assert.DoesNotContain("{{company}}", rendered);
    }

    [Fact]
    public void D1_AiGoal_ReplaceVars()
    {
        // 模拟 ScriptEngine L688：## 任务: {ReplaceVars(aiGoal, ...)}
        var aiGoal = "为 {{company}} 公司填报合同";
        var vars = Vars("company", "上海家化");
        var instruction = $"## 任务: {VariableHelper.ReplaceVars(aiGoal, Scope(), vars)}";
        Assert.Equal("## 任务: 为 上海家化 公司填报合同", instruction);
    }

    [Fact]
    public void D1_AiGoal_NoPlaceHolder_Unchanged()  // 无 {{}} 透传零副作用（D 基础）
    {
        var aiGoal = "填写合同信息";
        var vars = Vars("company", "上海家化");
        var instruction = $"## 任务: {VariableHelper.ReplaceVars(aiGoal, Scope(), vars)}";
        Assert.Equal("## 任务: 填写合同信息", instruction);
    }

    [Fact]
    public void D1_GoalChain_VarNotFound_PreservesPlaceHolder()  // var 未找到返回原 {{varName}}（不崩）
    {
        var g = "为 {{unknownVar}} 填报";
        var rendered = VariableHelper.ReplaceVars(g, Scope(), new Dictionary<string, object>());
        Assert.Equal("为 {{unknownVar}} 填报", rendered);
    }

    // ===== D2 captcha 5 selector =====

    [Fact]
    public void D2_Captcha5Selector_ReplaceVars()  // 模拟 D2 入口对 step 本体统一替换
    {
        // 模拟 StepExecutor ExecuteCaptchaAsync L553 入口：step = step with { ImageSelector = ReplaceVars(...), ... }
        var vars = Vars("code", "021");
        var img = VariableHelper.ReplaceVars("//img[@id='img_{{code}}']", Scope(), vars);
        var input = VariableHelper.ReplaceVars("//input[@id='in_{{code}}']", Scope(), vars);
        var slider = VariableHelper.ReplaceVars("//div[@id='sl_{{code}}']", Scope(), vars);
        var target = VariableHelper.ReplaceVars("//div[@id='tg_{{code}}']", Scope(), vars);
        var bg = VariableHelper.ReplaceVars("//div[@id='bg_{{code}}']", Scope(), vars);
        Assert.Equal("//img[@id='img_021']", img);
        Assert.Equal("//input[@id='in_021']", input);
        Assert.Equal("//div[@id='sl_021']", slider);
        Assert.Equal("//div[@id='tg_021']", target);
        Assert.Equal("//div[@id='bg_021']", bg);
    }

    [Fact]
    public void D2_CaptchaSelector_Null_Preserved()  // D2 守卫：null 保持 null（不破坏 ?? selector fallback）
    {
        // 模拟 D2：step.ImageSelector != null ? ReplaceVars(...) : null
        string? imageSelector = null;
        var vars = Vars("code", "021");
        var result = imageSelector != null ? VariableHelper.ReplaceVars(imageSelector, Scope(), vars) : null;
        Assert.Null(result);  // null 保持 null
    }

    // ===== D3 PreSetup.DialogText =====

    [Fact]
    public void D3_DialogText_ReplaceVars_Contains()  // 模拟 L57：dialog.Message.Contains(ReplaceVars(DialogText))
    {
        var dialogText = "确认提交 {{company}} 的合同？";
        var vars = Vars("company", "上海家化");
        var replaced = VariableHelper.ReplaceVars(dialogText, Scope(), vars);
        var dialogMessage = "系统提示：确认提交 上海家化 的合同？";
        Assert.Contains(replaced, dialogMessage);  // 替换后 DialogText 在 dialog.Message 中命中
    }

    [Fact]
    public void D3_DialogText_Null_ShortCircuit()  // D3 保留 == null || 短路（null 不调 ReplaceVars）
    {
        string? dialogText = null;
        // 模拟 L57：dialogText == null || dialog.Message.Contains(ReplaceVars(dialogText))
        bool result = dialogText == null || "any message".Contains(VariableHelper.ReplaceVars(dialogText ?? "", Scope(), new()));
        Assert.True(result);  // null 短路返 true（接受所有 dialog）
    }

    // ===== D4 DialogPromptText =====

    [Fact]
    public void D4_DialogPromptText_ReplaceVars()  // 模拟 L392：dialog.AcceptAsync(ReplaceVars(DialogPromptText ?? ""))
    {
        var promptText = "默认值{{company}}";
        var vars = Vars("company", "上海家化");
        var replaced = VariableHelper.ReplaceVars(promptText ?? "", Scope(), vars);
        Assert.Equal("默认值上海家化", replaced);
    }

    [Fact]
    public void D4_DialogPromptText_Null_EmptyString()  // D4：?? "" 兜 null
    {
        string? promptText = null;
        var vars = Vars("company", "上海家化");
        var replaced = VariableHelper.ReplaceVars(promptText ?? "", Scope(), vars);
        Assert.Equal("", replaced);  // null -> "" -> ReplaceVars("") -> ""
    }
}
