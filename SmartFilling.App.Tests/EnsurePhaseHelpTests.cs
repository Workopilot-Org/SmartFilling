using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using NSubstitute;
using SmartFilling.App.Configuration;
using SmartFilling.App.Recording;
using SmartFilling.App.Services;
using SmartFilling.Engine.Ai;
using SmartFilling.Engine.Models;
using EngineILogger = SmartFilling.Engine.Logging.ILogger;

namespace SmartFilling.App.Tests;

/// <summary>
/// ⑥（a-a-4，2026-07-09）EnsurePhaseAsync 求助通道对齐 step 测试：验证 phase.Condition/loopCondition ①②③④ 求助全部可达。
/// 核心回归：#1 (h) 不可达（count-based count==0 返 pce=null 不进旧 if(pce!=null)）+ #2 priority silent（旧弃用 priority 出参）。
/// EnsurePhaseAsync 提 internal 供直调；求助经 public OnRequestHelp event + ReplyHelp 驱动（mock page + mock IAiProvider，仿 ValidateFallbackTests）。
/// 🟡 mock 边界：mock 验 ProcessDetectNodeAsync 出参消费 + 求助触发（err 含求助语义），验不出 Playwright 真校验--真实浏览器另测。
/// </summary>
public class EnsurePhaseHelpTests
{
    private static ScriptService CreateScriptService()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SmartFillingTest_" + Guid.NewGuid().ToString("N")[..8]);
        var mockEnv = Substitute.For<IWebHostEnvironment>();
        mockEnv.ContentRootPath.Returns(tempDir);
        var mockLogger = Substitute.For<ILogger<ScriptService>>();
        return new ScriptService(mockEnv, Options.Create(new AppOptions()), mockLogger);
    }

    private static RecordingEngine CreateEngine(IPage page)
    {
        var mockAi = Substitute.For<IAiProvider>();
        var mockLogger = Substitute.For<EngineILogger>();
        var engine = new RecordingEngine(mockAi, CreateScriptService(), mockLogger, new EngineOptions(), new AppOptions());
        engine.TestInjectedPage = page;
        return engine;
    }

    /// <summary>创建主文档 mock page（page.Locator 链 CountAsync=count，供 detect ③ 存在性/唯一性校验）。无子 frame。</summary>
    private static IPage CreateMainDocPage(int count)
    {
        var mainFrameMock = Substitute.For<IFrame>();
        var browserMock = Substitute.For<IBrowser>();
        browserMock.IsConnected.Returns(true);
        var ctxMock = Substitute.For<IBrowserContext>();
        ctxMock.Browser.Returns(browserMock);
        var pageMock = Substitute.For<IPage>();
        pageMock.MainFrame.Returns(mainFrameMock);
        pageMock.Context.Returns(ctxMock);
        pageMock.Frames.Returns(new List<IFrame> { mainFrameMock });
        var bodyLocMock = Substitute.For<ILocator>();
        bodyLocMock.CountAsync().Returns(count);
        bodyLocMock.Locator(Arg.Any<string>(), Arg.Any<LocatorLocatorOptions?>()).Returns(bodyLocMock);
        pageMock.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions?>()).Returns(bodyLocMock);
        mainFrameMock.Locator(Arg.Any<string>(), Arg.Any<FrameLocatorOptions?>()).Returns(bodyLocMock);
        return pageMock;
    }

    /// <summary>创建 mock page：MainFrame + 子 frame（parent=MainFrame，locatorCount/bodyText 控制反推命中）。供 ① 多 frame picker。</summary>
    private static IPage CreatePageWithFrames(params (string name, string url, int locatorCount, string? bodyText)[] childSpecs)
    {
        var mainFrame = Substitute.For<IFrame>();
        var browserMock = Substitute.For<IBrowser>();
        browserMock.IsConnected.Returns(true);
        var ctxMock = Substitute.For<IBrowserContext>();
        ctxMock.Browser.Returns(browserMock);
        var pageMock = Substitute.For<IPage>();
        pageMock.MainFrame.Returns(mainFrame);
        pageMock.Context.Returns(ctxMock);
        var frames = new List<IFrame> { mainFrame };
        foreach (var (name, url, count, body) in childSpecs)
        {
            var fm = Substitute.For<IFrame>();
            fm.Name.Returns(name);
            fm.Url.Returns(url);
            fm.ParentFrame.Returns(mainFrame);
            fm.FrameElementAsync().Returns(Substitute.For<IElementHandle>());
            var locMock = Substitute.For<ILocator>();
            locMock.CountAsync().Returns(count);
            fm.Locator(Arg.Any<string>(), Arg.Any<FrameLocatorOptions?>()).Returns(locMock);
            if (body != null)
                fm.EvaluateAsync<string>(Arg.Any<string>(), Arg.Any<object?>()).Returns(body);
            frames.Add(fm);
        }
        pageMock.Frames.Returns(frames);
        return pageMock;
    }

    /// <summary>构造 ensure_phase args：phase 名 + 可选 phaseCondition/phaseLoopCondition（DetectCondition 序列化为 JsonElement 经 GetObject 反序列化，走生产路径）。</summary>
    private static Dictionary<string, object> PhaseArgs(string phase, DetectCondition? cond = null, string? condKey = "phaseCondition")
    {
        var args = new Dictionary<string, object> { ["phase"] = phase };
        if (cond != null) args[condKey!] = System.Text.Json.JsonSerializer.SerializeToElement(cond);
        return args;
    }

    private static void WireHelp(RecordingEngine engine, string reply)
    {
        // OnRequestHelp 触发时 ReplyHelp 注入回答；ExecuteRequestHelpAsync 返 "用户回答: <reply>"。OnLog 设 no-op（LogAsync 已有 ?? CompletedTask 兜底，双保险）。
        engine.OnRequestHelp += (a, q, s) => { engine.ReplyHelp(reply); return Task.CompletedTask; };
        engine.OnLog += (a, msg) => Task.CompletedTask;
    }

    // ② 回归 #1：phase.Condition count-based（selector_exists）count==0 -> (h) 求助【可达】（修复前 pce=null 不进旧 if(pce!=null) -> silent 落盘 phase）。
    [Fact]
    public async Task PhaseCondition_CountZero_AcceptAsIsHelp_Reachable()
    {
        var page = CreateMainDocPage(count: 0);  // selector_exists 找不到（count==0）
        var engine = CreateEngine(page);
        WireHelp(engine, "h");  // 用户选 (h) acceptAsIs
        var args = PhaseArgs("p-countzero", new DetectCondition { Type = "selector_exists", Selector = "//missing" });

        var (err, warnings) = await engine.EnsurePhaseAsync(args, CancellationToken.None);

        Assert.NotNull(err);  // #1 核心：求助触发（修复前 err=null silent 创建 phase）
        Assert.Contains("找不到", err!);  // ② (h) 求助语义
        Assert.Contains("已求助", err!);  // 走求助通道（非 silent 落盘）
    }

    // ④ 回归 #2：phase.Condition selector 脆弱（GUID priority 8）+ 唯一（count=1）-> (f) 求助（修复前旧弃用 priority 出参 -> silent 落盘）。
    [Fact]
    public async Task PhaseCondition_Priority8_AcceptFragileHelp()
    {
        var page = CreateMainDocPage(count: 1);  // 唯一（count=1，strict 校验通过）
        var engine = CreateEngine(page);
        WireHelp(engine, "f");  // 用户选 (f) acceptFragile
        var args = PhaseArgs("p-priority8", new DetectCondition { Type = "selector_visible", Selector = "//*[@id='37b9c6a8-1234-5678-abcd-ef0123456789']" });  // GUID -> AssessFragility 8

        var (err, warnings) = await engine.EnsurePhaseAsync(args, CancellationToken.None);

        Assert.NotNull(err);  // #2 核心：priority 求助触发（修复前 silent 落盘）
        Assert.Contains("较脆弱", err!);  // ④ (f) 求助语义
        Assert.Contains("已求助", err!);
    }

    // ③ strict 校验失败（selector_visible count==0 找不到）-> 阻断返 error（不走 (h)，strict 找不到回放必失败）。
    [Fact]
    public async Task PhaseCondition_StrictError_Block()
    {
        var page = CreateMainDocPage(count: 0);  // selector_visible 找不到 -> strict 唯一性校验失败
        var engine = CreateEngine(page);
        var helpCalled = false;
        engine.OnRequestHelp += (a, q, s) => { helpCalled = true; engine.ReplyHelp("h"); return Task.CompletedTask; };  // 不应被调用
        engine.OnLog += (a, msg) => Task.CompletedTask;
        var args = PhaseArgs("p-stricterr", new DetectCondition { Type = "selector_visible", Selector = "//missing" });

        var (err, warnings) = await engine.EnsurePhaseAsync(args, CancellationToken.None);

        Assert.NotNull(err);  // ③ 阻断返 error
        Assert.False(helpCalled);  // strict 校验失败走 ③ 阻断，不求助（区别 ② count-based count==0 走 (h)）
    }

    // ① phase.Condition page_contains 多 frame 命中 -> (j) picker（对齐 step detect③，先定 frame）。
    [Fact]
    public async Task PhaseCondition_MultiFrame_Picker()
    {
        var page = CreatePageWithFrames(
            ("frame-a", "http://a", 0, "提交成功"),
            ("frame-b", "http://b", 0, "操作成功"));  // 2 frame 都含 keywords "成功"
        var engine = CreateEngine(page);
        WireHelp(engine, "j 1");  // 用户 (j) 选 frame 1
        var args = PhaseArgs("p-multiframe", new DetectCondition { Type = "page_contains", Keywords = new[] { "成功" } });

        var (err, warnings) = await engine.EnsurePhaseAsync(args, CancellationToken.None);

        Assert.NotNull(err);  // ① picker 触发
        Assert.Contains("多 frame", err!);  // picker 语义
        Assert.Contains("已求助", err!);
    }

    // ② loopCondition 同款 #1 回归：phase.LoopCondition selector_exists count==0 -> (h) 求助可达（对齐 phase.Condition ②）。
    [Fact]
    public async Task LoopCondition_CountZero_AcceptAsIsHelp_Reachable()
    {
        var page = CreateMainDocPage(count: 0);
        var engine = CreateEngine(page);
        WireHelp(engine, "h");
        var args = PhaseArgs("p-loopcz", new DetectCondition { Type = "selector_exists", Selector = "//missing" }, condKey: "phaseLoopCondition");

        var (err, warnings) = await engine.EnsurePhaseAsync(args, CancellationToken.None);

        Assert.NotNull(err);  // loopCondition ② 同款 #1 回归
        Assert.Contains("找不到", err!);
        Assert.Contains("已求助", err!);
    }

    // 放法 X 块A（2026-07-14）：phase.Condition priority 8 脆弱 + 用户选 (l) -> reply 追加全量代码 HTML（RequestSelectorFragileHelpAsync -> AppendHelpHtmlIfNeeded 喂 outerHTML）。
    // 验值：err（含 reply）含代码抓的真 outerHTML 全量文本（不只验求助触发状态）。
    [Fact]
    public async Task PhaseCondition_Priority8_ChooseL_FeedsFullHtml()
    {
        var outerHtml = "<div class=\"el-submenu__title\" style=\"color:red\"><span>菜单项</span></div>";
        var page = CreateMainDocPageWithHtml(count: 1, html: outerHtml);  // 唯一（count=1）+ EvaluateAsync 返真 outerHTML
        var engine = CreateEngine(page);
        WireHelp(engine, "l");  // 用户选 (l) 用代码已提取的 HTML
        var args = PhaseArgs("p-choosel", new DetectCondition { Type = "selector_visible", Selector = "//*[@id='37b9c6a8-1234-5678-abcd-ef0123456789']" });  // GUID -> AssessFragility 8

        var (err, warnings) = await engine.EnsurePhaseAsync(args, CancellationToken.None);

        Assert.NotNull(err);
        Assert.Contains("较脆弱", err!);  // priority 8 求助
        Assert.Contains(outerHtml, err!);  // 放法 X：选 (l) 喂全量 outerHTML（验值：含完整 HTML 含子级，非合成骨架）
        Assert.Contains("[代码已提取]", err!);  // (l) 措辞
    }

    // 放法 X 块A：phase.Condition priority 8 + 用户选 (b) 没粘 HTML -> 必填提示不喂代码 HTML（b 必填不兜底，后端防绕过）。
    [Fact]
    public async Task PhaseCondition_Priority8_ChooseBNotPasted_RequiredPrompt()
    {
        var outerHtml = "<div class=\"el-submenu__title\">菜单</div>";
        var page = CreateMainDocPageWithHtml(count: 1, html: outerHtml);
        var engine = CreateEngine(page);
        WireHelp(engine, "b");  // 用户选 (b) 但没粘 HTML
        var args = PhaseArgs("p-chooseb", new DetectCondition { Type = "selector_visible", Selector = "//*[@id='37b9c6a8-1234-5678-abcd-ef0123456789']" });

        var (err, warnings) = await engine.EnsurePhaseAsync(args, CancellationToken.None);

        Assert.NotNull(err);
        Assert.Contains("必须填 HTML", err!);  // b 必填提示
        Assert.DoesNotContain(outerHtml, err!);  // 不喂代码 HTML
    }

    /// <summary>放法 X：创建主文档 mock page，body Locator CountAsync=count + EvaluateAsync 返 html（供 ExtractHtmlForHelpAsync 抓 outerHTML）。</summary>
    private static IPage CreateMainDocPageWithHtml(int count, string html)
    {
        var mainFrameMock = Substitute.For<IFrame>();
        var browserMock = Substitute.For<IBrowser>();
        browserMock.IsConnected.Returns(true);
        var ctxMock = Substitute.For<IBrowserContext>();
        ctxMock.Browser.Returns(browserMock);
        var pageMock = Substitute.For<IPage>();
        pageMock.MainFrame.Returns(mainFrameMock);
        pageMock.Context.Returns(ctxMock);
        pageMock.Frames.Returns(new List<IFrame> { mainFrameMock });
        var bodyLocMock = Substitute.For<ILocator>();
        bodyLocMock.CountAsync().Returns(count);
        bodyLocMock.Locator(Arg.Any<string>(), Arg.Any<LocatorLocatorOptions?>()).Returns(bodyLocMock);
        bodyLocMock.EvaluateAsync<string>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<LocatorEvaluateOptions?>()).Returns(html);  // CaptureElementContextAsync 抓 outerHTML
        var frameLocMock = Substitute.For<IFrameLocator>();
        frameLocMock.Locator(Arg.Any<string>(), Arg.Any<FrameLocatorLocatorOptions?>()).Returns(bodyLocMock);
        bodyLocMock.FrameLocator(Arg.Any<string>()).Returns(frameLocMock);
        pageMock.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions?>()).Returns(bodyLocMock);
        mainFrameMock.Locator(Arg.Any<string>(), Arg.Any<FrameLocatorOptions?>()).Returns(bodyLocMock);
        return pageMock;
    }

    // ===== 审查修复（2026-07-14）：OBS-1 (f) 守卫 + T-F2 ComputeHtmlForAiAsync 源分流（直调 internal，纯函数/低 mock）=====

    // OBS-1 方案1：priority9 prioOptions（无 AcceptFragile，menu 不显示 (f)）+ 用户异常回 "f" -> TryAcceptFragileReply 不填集合/不追加"已接受"（修复）。
    // 修复前守卫不查 prioOptions，priority9 回 "f" 误填 _acceptedFragile -> 下轮 acceptFragile=true+同 selector silent 落盘 priority9 极不稳定 selector。
    [Fact]
    public void OBS1_Priority9_AbnormalReplyF_NotAccepted()
    {
        var engine = CreateEngine(CreateMainDocPage(1));
        var prio9Opts = HelpOption.Common5 | HelpOption.UseExtractedHtml;  // priority9 menu（无 (f)）

        var reply = engine.TryAcceptFragileReply("f", isMultiMatch: false, "//input[@id='x']", prio9Opts, FragileType.StepSelector);

        Assert.DoesNotContain("已接受脆弱 selector", reply);  // 修复：priority9 回 f 不追加（修复前会追加致 silent 落盘）
    }

    // OBS-1 无回归：priority8 prioOptions（含 AcceptFragile，menu 显示 (f)）+ 回 "f" -> 追加"已接受"（priority8 正常接受，守卫恒 true 无回归）。
    [Fact]
    public void OBS1_Priority8_ReplyF_Accepted_NoRegression()
    {
        var engine = CreateEngine(CreateMainDocPage(1));
        var prio8Opts = HelpOption.Common5 | HelpOption.AcceptFragile | HelpOption.UseExtractedHtml;  // priority8 menu（有 (f)）

        var reply = engine.TryAcceptFragileReply("f", isMultiMatch: false, "//input[@id='x']", prio8Opts, FragileType.StepSelector);

        Assert.Contains("已接受脆弱 selector", reply);  // 无回归：priority8 回 f 正常追加
    }

    // OBS-1 多匹配：多匹配 prioOptions（Common5 无 AcceptFragile 无 l，menu 无 (f)）+ 回 "f" -> 不追加（多匹配也防误接受）。
    [Fact]
    public void OBS1_MultiMatch_ReplyF_NotAccepted()
    {
        var engine = CreateEngine(CreateMainDocPage(1));
        var mmOpts = HelpOption.Common5;  // 多匹配 menu（无 (f) 无 l）

        var reply = engine.TryAcceptFragileReply("f", isMultiMatch: true, "//input[@id='x']", mmOpts, FragileType.StepSelector);

        Assert.DoesNotContain("已接受脆弱 selector", reply);  // 多匹配回 f 不追加
    }

    // T-F2 路径2 核心：FallbackSelector 源 + result.Element 非 null（step 元素，EvaluateAsync 返 STEP_HTML）-> ComputeHtmlForAiAsync 走 ExtractHtmlForHelpAsync(fallback selector) 抓 FALLBACK_HTML（非 result.Element STEP_HTML）。
    // 验值：返 FALLBACK_HTML（修复后按求助 selector 抓；修复前无源判断走 result.Element 抓 STEP_HTML 与求助 fallback selector 不匹配 silent）。
    [Fact]
    public async Task TF2_FallbackSource_ExtractsFallbackSelectorHtml_NotStepElement()
    {
        var page = CreateMainDocPageWithHtml(count: 1, html: "FALLBACK_HTML");  // page.Locator(fallbackSelector) -> bodyLocMock 返 FALLBACK_HTML + CountAsync=1
        var engine = CreateEngine(page);
        var stepLocMock = Substitute.For<ILocator>();  // result.Element（operate step 元素，与求助 fallback selector 不同元素）
        stepLocMock.EvaluateAsync<string>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<LocatorEvaluateOptions?>()).Returns("STEP_HTML");
        var result = new RecordingActionExecutor.OperateResult(true, "ok", "//step", null, Element: stepLocMock, Priority: 8);
        var modFb = new StepFallback { Selector = "//fb" };  // fallback selector（求助显示的）

        var html = await engine.ComputeHtmlForAiAsync(result, isPriority9: false, FragileType.FallbackSelector, "//fb", modFb, new StepNode());

        Assert.Equal("FALLBACK_HTML", html);  // 走 ExtractHtmlForHelpAsync("//fb") 抓 fallback HTML（非 result.Element 的 STEP_HTML，验 F2 源分流修复）
    }

    // T-F2 priority9：isPriority9=true -> 返 result.HtmlContext（priority9 用 HtmlContext，非 Element/Extract）。
    [Fact]
    public async Task TF2_Priority9_ReturnsHtmlContext()
    {
        var engine = CreateEngine(CreateMainDocPage(1));
        var result = new RecordingActionExecutor.OperateResult(true, "ok", null, null, Priority: 9, HtmlContext: "P9_HTML");

        var html = await engine.ComputeHtmlForAiAsync(result, isPriority9: true, FragileType.StepSelector, "//x", null, new StepNode());

        Assert.Equal("P9_HTML", html);  // priority9 用 result.HtmlContext
    }

    // T-F2 StepSelector 源 + result.Element 非 null -> 走 CaptureElementContextAsync(result.Element)（priority8 step 源零成本，复用 execute 已抓元素）。
    [Fact]
    public async Task TF2_StepSource_UsesResultElement()
    {
        var page = CreateMainDocPage(1);
        var engine = CreateEngine(page);
        var stepLocMock = Substitute.For<ILocator>();
        stepLocMock.EvaluateAsync<string>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<LocatorEvaluateOptions?>()).Returns("STEP_HTML");
        var result = new RecordingActionExecutor.OperateResult(true, "ok", "//step", null, Element: stepLocMock, Priority: 8);

        var html = await engine.ComputeHtmlForAiAsync(result, isPriority9: false, FragileType.StepSelector, "//step", null, new StepNode());

        Assert.Equal("STEP_HTML", html);  // StepSelector 源用 result.Element（step 元素，匹配求助 selector）
    }

    // T-F2 多匹配 -> null（不抓，R13 b 必填消解，此为不抓优化）。
    [Fact]
    public async Task TF2_MultiMatch_ReturnsNull()
    {
        var engine = CreateEngine(CreateMainDocPage(1));
        var result = new RecordingActionExecutor.OperateResult(true, "ok", "//x", null, IsMultiMatch: true, Priority: 8);

        var html = await engine.ComputeHtmlForAiAsync(result, isPriority9: false, FragileType.StepSelector, "//x", null, new StepNode());

        Assert.Null(html);  // 多匹配不抓
    }

    // ===== 审查修复 R14（2026-07-14）：ResolveDetectChainForHelp 链解析（static 纯函数直测，无 mock）=====

    // R14：step.Detect 有 selector + iframe=反推链 -> 返 Detect.Iframe（detect priority8 在非脆弱 iframe 内传链定位抓 HTML 显示 l，对齐 save_step/phase）。
    [Fact]
    public void R14_DetectSelector_ReturnsDetectIframeChain()
    {
        var chain = new[] { "//iframe[@id='stable']" };  // 非脆弱 iframe（稳定 id）
        var step = new StepNode { Action = "check", Detect = new DetectCondition { Type = "selector_exists", Selector = "//input[@id='37b9c6a8-1234-5678-abcd-ef0123456789']", Iframe = chain } };

        var resolved = RecordingEngine.ResolveDetectChainForHelp(step);

        Assert.Equal(chain, resolved);  // 返 Detect 反推链（R14 修复前传 null，非脆弱 iframe 内抓不到不显示 l）
    }

    // R14 顺序：Detect 无 selector、Until 有 -> 返 Until.Iframe（链与 selector 同源 ?? 顺序，取第一个非空 Selector 源）。
    [Fact]
    public void R14_UntilSelector_FallbackToUntilIframeChain()
    {
        var chain = new[] { "//iframe[@id='until-frame']" };
        var step = new StepNode { Action = "wait", Until = new DetectCondition { Type = "selector_exists", Selector = "//wait-target", Iframe = chain } };

        var resolved = RecordingEngine.ResolveDetectChainForHelp(step);

        Assert.Equal(chain, resolved);  // Detect 无 Selector -> 取 Until.Iframe
    }

    // R14 顺序：Detect/Until 都无、Condition 有 -> 返 Condition.Iframe。
    [Fact]
    public void R14_ConditionSelector_FallbackToConditionIframeChain()
    {
        var chain = new[] { "//iframe[@id='cond-frame']" };
        var step = new StepNode { Action = "click", Condition = new DetectCondition { Type = "selector_exists", Selector = "//cond-target", Iframe = chain } };

        var resolved = RecordingEngine.ResolveDetectChainForHelp(step);

        Assert.Equal(chain, resolved);  // Detect/Until 无 Selector -> 取 Condition.Iframe
    }

    // R14 主文档：三源都无 selector -> null（detect selector 在主文档，无 iframe 链，ExtractHtmlForHelpAsync 主文档搜索）。
    [Fact]
    public void R14_NoSelector_ReturnsNull()
    {
        var step = new StepNode { Action = "navigate" };  // 无 detect/until/condition

        var resolved = RecordingEngine.ResolveDetectChainForHelp(step);

        Assert.Null(resolved);  // 无 selector -> null（主文档）
    }
}
