using SmartFilling.Engine.Engine;
using SmartFilling.Engine.Models;
using SmartFilling.Engine.Tests.Helpers;
using Xunit;

namespace SmartFilling.Engine.Tests.Integration;

/// <summary>
/// 跳过逻辑集成测试：skipIfDataEmpty、condition。
/// 所有 step 使用 check(always) 不需要真实 DOM 操作。
/// </summary>
public class SkipLogicTests
{
    private readonly NullLogger _logger = new();

    private static EngineOptions DefaultOptions() => new()
    {
        MaxScriptDuration = 600000,
        StepRetry = new StepRetry { Count = 0 }
    };

    #region SkipIfDataEmpty

    [Fact]
    public async Task SkipIfDataEmpty_FieldNull_Skips()
    {
        // fillData 中 field=null, skipIfDataEmpty=true，step 被跳过，后续 step 执行
        var script = new ScriptBuilder()
            .AddPhase("Main", steps:
            [
                ScriptBuilder.Step("fill", selector: "#input", value: "{{optionalField}}",
                    skipIfDataEmpty: true),
                ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "phase_success")
            ])
            .Build();

        var fillData = new Dictionary<string, object>
        {
            { "optionalField", null! }
        };

        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, DefaultOptions());

        var result = await engine.ExecuteAsync(script, fillData, page);

        // Step 1 被 skipIfDataEmpty 跳过（值为 null），Step 2 执行 phase_success
        Assert.True(result.Success, result.ErrorMessage);
    }

    [Fact]
    public async Task SkipIfDataEmpty_FieldMissing_Skips()
    {
        // fillData 中没有该字段，step 被跳过
        var script = new ScriptBuilder()
            .AddPhase("Main", steps:
            [
                ScriptBuilder.Step("fill", selector: "#input", value: "{{optionalField}}",
                    skipIfDataEmpty: true),
                ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "phase_success")
            ])
            .Build();

        // fillData 中没有 optionalField
        var fillData = new Dictionary<string, object>();

        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, DefaultOptions());

        var result = await engine.ExecuteAsync(script, fillData, page);

        // Step 1 被 skipIfDataEmpty 跳过（字段不存在），Step 2 执行 phase_success
        Assert.True(result.Success, result.ErrorMessage);
    }

    [Fact]
    public async Task SkipIfDataEmpty_FieldZero_NotSkipped()
    {
        // 关键语义：值为 0 不跳过（0 != null），step 正常执行
        // fill 在 CreateFailing mock 上执行抛 TimeoutException，onError=stop（默认）使脚本失败
        // 如果 skipIfDataEmpty 跳过了，脚本会成功（执行 Step 2 phase_success）
        // 验证：脚本失败 → 说明 fill 确实被执行了（没有被 skipIfDataEmpty 跳过）
        var script = new ScriptBuilder()
            .AddPhase("Main", steps:
            [
                ScriptBuilder.Step("fill", selector: "#input", value: "{{amount}}",
                    skipIfDataEmpty: true),
                ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "phase_success")
            ])
            .Build();

        var fillData = new Dictionary<string, object>
        {
            { "amount", 0 }
        };

        MockPageFactory.CreateFailing(out var page);
        var engine = new ScriptEngine(_logger, DefaultOptions());

        var result = await engine.ExecuteAsync(script, fillData, page);

        // fill 未被 skipIfDataEmpty 跳过（0 != null），执行时异常，脚本失败
        Assert.False(result.Success);
    }

    [Fact]
    public async Task SkipIfDataEmpty_FieldPresent_NotSkipped()
    {
        // 字段有值，step 不被 skipIfDataEmpty 跳过，正常执行
        // fill 在 CreateFailing mock 上执行抛 TimeoutException，onError=stop 使脚本失败
        // 如果 skipIfDataEmpty 跳过了，脚本会成功（执行 Step 2 phase_success）
        // 验证：脚本失败 → 说明 fill 确实被执行了
        var script = new ScriptBuilder()
            .AddPhase("Main", steps:
            [
                ScriptBuilder.Step("fill", selector: "#input", value: "{{optionalField}}",
                    skipIfDataEmpty: true),
                ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "phase_success")
            ])
            .Build();

        var fillData = new Dictionary<string, object>
        {
            { "optionalField", "hello" }
        };

        MockPageFactory.CreateFailing(out var page);
        var engine = new ScriptEngine(_logger, DefaultOptions());

        var result = await engine.ExecuteAsync(script, fillData, page);

        // fill 未被 skipIfDataEmpty 跳过（字段存在），执行时异常，脚本失败
        Assert.False(result.Success);
    }

    #endregion

    #region Step Condition

    [Fact]
    public async Task Condition_JsTrue_Executes()
    {
        // condition js=true 的 step 执行
        var script = new ScriptBuilder()
            .AddPhase("Main", steps:
            [
                ScriptBuilder.Step("check",
                    detect: ScriptBuilder.Detect("always"),
                    then: "phase_success",
                    condition: ScriptBuilder.Detect("js", check: "true"))
            ])
            .Build();

        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, DefaultOptions());

        var result = await engine.ExecuteAsync(script, new(), page);

        Assert.True(result.Success, result.ErrorMessage);
    }

    [Fact]
    public async Task Condition_JsFalse_Skips()
    {
        // condition js=false 的 step 跳过，后续 step 仍执行
        var script = new ScriptBuilder()
            .AddPhase("Main", steps:
            [
                // 这个 step 因 condition=false 被跳过（不会触发 phase_success）
                ScriptBuilder.Step("check",
                    detect: ScriptBuilder.Detect("always"),
                    then: "script_fail",
                    message: "不应执行此步骤",
                    condition: ScriptBuilder.Detect("js", check: "false")),
                // 这个 step 正常执行
                ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "phase_success")
            ])
            .Build();

        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, DefaultOptions());

        var result = await engine.ExecuteAsync(script, new(), page);

        // 第一个 check step 因 condition=false 被跳过，不会触发 script_fail
        // 第二个 check step 执行 phase_success
        Assert.True(result.Success, result.ErrorMessage);
    }

    #endregion
}
