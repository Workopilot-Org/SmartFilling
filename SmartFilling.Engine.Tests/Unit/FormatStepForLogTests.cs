using SmartFilling.Engine.Engine;
using SmartFilling.Engine.Models;
using SmartFilling.Engine.Reporting;
using SmartFilling.Engine.Tests.Helpers;
using NSubstitute;
using Xunit;

namespace SmartFilling.Engine.Tests.Unit;

/// <summary>
/// TD-P3：FormatStepForLog 按 action 格式化输出测试（fill/wait/navigate/extract 4 个核心 action）。
/// FormatStepForLog 改 internal + Engine InternalsVisibleTo(Engine.Tests) 供直接调用（不需 mock page）。
/// 断言验具体格式字段（value/ms/until/url/storeAs/耗时），不只验被调。日志路径不脱敏（按脱敏总原则）。
/// </summary>
public class FormatStepForLogTests
{
    private static SmartFilling.Engine.Engine.ExecutionContext Ctx(params Dictionary<string, object>[] scopes)
    {
        var ctx = new SmartFilling.Engine.Engine.ExecutionContext { PhaseName = "TestPhase" };
        foreach (var s in scopes) ctx.ScopeChain.Add(s);
        return ctx;
    }

    [Fact]
    public void Fill_IncludesAction_Value_Selector_Duration()
    {
        var step = ScriptBuilder.Step("fill", selector: "#input", value: "{{name}}", name: "fillName");
        var ctx = Ctx(new Dictionary<string, object> { { "name", "Alice" } });

        var msg = ScriptEngine.FormatStepForLog(step, ctx, 150);

        Assert.Contains("[phase TestPhase]", msg);
        Assert.Contains("fillName", msg);
        Assert.Contains("action=fill", msg);
        Assert.Contains("selector=#input", msg);
        Assert.Contains("value=Alice", msg);  // {{name}} 经 ReplaceVars 替换为实际值（日志路径不脱敏）
        Assert.Contains("耗时=150ms", msg);
    }

    [Fact]
    public void Wait_IncludesMs_And_UntilType()
    {
        var step = ScriptBuilder.Step("wait", ms: 500,
            until: ScriptBuilder.Detect("selector_visible", selector: "#x"), name: "waitX");
        var ctx = Ctx();

        var msg = ScriptEngine.FormatStepForLog(step, ctx, 200);

        Assert.Contains("waitX", msg);
        Assert.Contains("action=wait", msg);
        Assert.Contains("ms=500", msg);
        Assert.Contains("until=selector_visible", msg);
        Assert.Contains("耗时=200ms", msg);
    }

    [Fact]
    public void Navigate_IncludesReplacedUrl()
    {
        var step = ScriptBuilder.Step("navigate", url: "{{baseUrl}}/login", name: "navLogin");
        var ctx = Ctx(new Dictionary<string, object> { { "baseUrl", "http://app" } });

        var msg = ScriptEngine.FormatStepForLog(step, ctx, 300);

        Assert.Contains("navLogin", msg);
        Assert.Contains("action=navigate", msg);
        Assert.Contains("url=http://app/login", msg);  // {{baseUrl}} 经 ReplaceVars
        Assert.Contains("耗时=300ms", msg);
    }

    [Fact]
    public void Extract_IncludesStoreAsMarker()
    {
        var step = ScriptBuilder.Step("extract", selector: "#code", storeAs: "billCode", name: "extractCode");
        var ctx = Ctx();

        var msg = ScriptEngine.FormatStepForLog(step, ctx, 100);

        Assert.Contains("extractCode", msg);
        Assert.Contains("action=extract", msg);
        Assert.Contains("storeAs=yes", msg);
        Assert.Contains("耗时=100ms", msg);
    }

    [Fact]
    public void Select_IncludesValue_SymmetricWithFill()
    {
        // N4（第 2 轮核查）：select 与 fill/type 同列，显 value
        var step = ScriptBuilder.Step("select", selector: "#sel", value: "{{opt}}", name: "selOpt");
        var ctx = Ctx(new Dictionary<string, object> { { "opt", "选项A" } });

        var msg = ScriptEngine.FormatStepForLog(step, ctx, 80);

        Assert.Contains("selOpt", msg);
        Assert.Contains("action=select", msg);
        Assert.Contains("value=选项A", msg);
    }

    [Fact]
    public void PressKey_IncludesKey()
    {
        var step = ScriptBuilder.Step("pressKey", key: "Enter", name: "pkEnter");
        var ctx = Ctx();

        var msg = ScriptEngine.FormatStepForLog(step, ctx, 30);

        Assert.Contains("pkEnter", msg);
        Assert.Contains("action=pressKey", msg);
        Assert.Contains("key=Enter", msg);
    }

    [Fact]
    public void Upload_IncludesFilePath()
    {
        // filePath 原样显示（不 ReplaceVars——上传路径可能是变量引用→数组，语义复杂，日志显配置值即可）
        var step = ScriptBuilder.Step("upload", filePath: "/data/a.pdf", name: "upFile");
        var ctx = Ctx();

        var msg = ScriptEngine.FormatStepForLog(step, ctx, 40);

        Assert.Contains("upFile", msg);
        Assert.Contains("action=upload", msg);
        Assert.Contains("filePath=/data/a.pdf", msg);
    }

    [Fact]
    public void Captcha_IncludesCaptchaType()
    {
        var step = ScriptBuilder.Step("captcha", captchaType: "slide", name: "capSlide");
        var ctx = Ctx();

        var msg = ScriptEngine.FormatStepForLog(step, ctx, 500);

        Assert.Contains("capSlide", msg);
        Assert.Contains("action=captcha", msg);
        Assert.Contains("captchaType=slide", msg);
    }

    [Fact]
    public void Scroll_IncludesDirectionAndAmount()
    {
        var step = ScriptBuilder.Step("scroll", direction: "down", amount: 300, name: "scDown");
        var ctx = Ctx();

        var msg = ScriptEngine.FormatStepForLog(step, ctx, 20);

        Assert.Contains("scDown", msg);
        Assert.Contains("action=scroll", msg);
        Assert.Contains("direction=down", msg);
        Assert.Contains("amount=300", msg);
    }

    [Fact]
    public void Check_IncludesDetectTypeAndThen()
    {
        var step = ScriptBuilder.Step("check", detect: ScriptBuilder.Detect("selector_visible", selector: "#x"), then: "phase_success", name: "chkVisible");
        var ctx = Ctx();

        var msg = ScriptEngine.FormatStepForLog(step, ctx, 10);

        Assert.Contains("chkVisible", msg);
        Assert.Contains("action=check", msg);
        Assert.Contains("detect=selector_visible", msg);
        Assert.Contains("then=phase_success", msg);
    }

    [Fact]
    public void Evaluate_TruncatesLongCode()
    {
        // code 超长截断（>60 字符加 …），防日志膨胀
        var longCode = new string('x', 80);
        var step = ScriptBuilder.Step("evaluate", code: longCode, name: "evalLong");
        var ctx = Ctx();

        var msg = ScriptEngine.FormatStepForLog(step, ctx, 15);

        Assert.Contains("evalLong", msg);
        Assert.Contains("action=evaluate", msg);
        Assert.Contains("code=", msg);
        Assert.Contains("…", msg);  // 截断标记
        Assert.DoesNotContain(new string('x', 80), msg);  // 完整 80 字符不进日志（截断了）
    }

    [Fact]
    public void WithDescription_AddsDescriptionFieldAfterStepName()
    {
        // 改动1：description 字段紧跟 [step name] 后、action 前
        var step = ScriptBuilder.Step("wait", ms: 500, name: "pause_001", description: "固定等待500ms");
        var ctx = Ctx();

        var msg = ScriptEngine.FormatStepForLog(step, ctx, 511);

        Assert.Contains("[step pause_001]", msg);
        Assert.Contains("description=固定等待500ms", msg);
        Assert.True(msg.IndexOf("description=") < msg.IndexOf("action="), "description 应在 action 前");
    }

    [Fact]
    public void LongDescription_TruncatesWithEllipsis()
    {
        // 改动0/1：description 超 60 字符截断加 …
        var longDesc = new string('x', 80);
        var step = ScriptBuilder.Step("wait", ms: 100, name: "longDesc", description: longDesc);
        var ctx = Ctx();

        var msg = ScriptEngine.FormatStepForLog(step, ctx, 50);

        Assert.Contains("…", msg);
        Assert.DoesNotContain(new string('x', 80), msg);  // 完整 80 字符不进日志（截断了）
    }

    [Fact]
    public void DescriptionExactly60_NotTruncated()
    {
        // 截断边界：恰好 60 字符不截断（s.Length > max 即 60>60=false）
        var desc60 = new string('y', 60);
        var step = ScriptBuilder.Step("wait", ms: 100, name: "desc60", description: desc60);
        var ctx = Ctx();

        var msg = ScriptEngine.FormatStepForLog(step, ctx, 50);

        Assert.DoesNotContain("…", msg);
        Assert.Contains(desc60, msg);  // 完整 60 字符保留
    }

    [Fact]
    public void DescriptionWithVarPlaceholder_NotReplaced()
    {
        // 改动1：description 原样显示，不 ReplaceVars（占位符 {{var}} 保留）
        var step = ScriptBuilder.Step("wait", ms: 100, name: "withVar", description: "等待 {{username}} 登录");
        var ctx = Ctx(new Dictionary<string, object> { { "username", "Alice" } });

        var msg = ScriptEngine.FormatStepForLog(step, ctx, 50);

        Assert.Contains("{{username}}", msg);  // 占位符原样显示
        Assert.DoesNotContain("Alice", msg);  // 不替换
    }

    [Fact]
    public void AiAction_DescriptionFieldNotDuplicated()
    {
        // 改动2：删 BuildStepFieldSummary 的 ai 分支后，description 只由 FormatStepForLog 前置一次，不重复
        var step = ScriptBuilder.Step("ai", description: "处理下拉", name: "aiStep");
        var ctx = Ctx();

        var msg = ScriptEngine.FormatStepForLog(step, ctx, 8500);

        var count = msg.Split("description=").Length - 1;  // "description=" 出现次数
        Assert.Equal(1, count);
    }

    [Fact]
    public void NoDescription_OmitsField()
    {
        // 改动1：无 description 时不加 description 字段（不留空字段）
        var step = ScriptBuilder.Step("wait", ms: 100, name: "noDesc");
        var ctx = Ctx();

        var msg = ScriptEngine.FormatStepForLog(step, ctx, 50);

        Assert.DoesNotContain("description=", msg);
    }

    [Fact]
    public async Task FailureLog_UsesStepNotChinese()
    {
        // 改动6：step 失败推前端"步骤失败"->"step 失败"（L1110），守护防回退
        var reporter = Substitute.For<ITaskProgressReporter>();
        var script = new ScriptBuilder()
            .AddPhase("Main", steps:
            [
                ScriptBuilder.Step("fill", selector: "#input", value: "test", name: "failStep")
            ])
            .Build();

        MockPageFactory.CreateFailing(out var page);  // fill 失败 -> onError=stop -> L1110
        var engine = new ScriptEngine(new NullLogger(), new EngineOptions
        {
            MaxScriptDuration = 600000,
            StepRetry = new StepRetry { Count = 0 },
            DefaultTimeout = 1000
        }, reporter);

        await engine.ExecuteAsync(script, new(), page);

        await reporter.Received().SendLogAsync(
            Arg.Is<string>(s => s!.Contains("❌") && s.Contains("step 失败") && !s.Contains("步骤")),
            Arg.Any<string?>());
    }
}
