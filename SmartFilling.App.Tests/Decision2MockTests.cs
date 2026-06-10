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
/// 决策2（2026-07-12 留新对话处理）：待决策1/2/3 + 结论13 C2 detect③/phase (f) 四项 mock 配套测试。
/// 仿 ValidateFallbackTests ScenarioA mock-frame 模式 + OperateDoubleStepTests ExecuteOperateAsync(args) 模式：
/// 经生产 args->工具执行路径（condition/fallback 经 JsonSerializer.SerializeToElement 走生产反序列化）+ 断言验值不只验状态。
///
/// 覆盖（每项含信号产生出参验证 + caller 消费路径验证）：
/// - 待决策1：operate iframe-(f) Round1 求助（IsFragileIframe->RequestIframeFragileHelpAsync 填 StepIframe 集合 + reply F3①）+ Round2 acceptFragile 集合命中 falls-through 落盘 step.Iframe=链 + onError
/// - 待决策2：复合 count==0 czLeafSelector 出参（第一个 count==0 叶 selector）+ operate 复合 (h) 两轮（Round1 selA 接受 -> Round2 selB 触发，验 per-selector gate）
/// - 待决策3：fallback selector 脆弱 priority 8 出参 + save_step fallback 归源（combinedPriority 源=FallbackSelector + 求助指向 fallback.Selector + 填 FallbackSelector 集合）
/// - 结论13 C2：ProcessDetectAsync detect③ iframe 脆弱链 detectIsFragileLayer 出参（验 BUG-1 ProcessDetectAsync 聚合）+ operate detect③ (f) 求助（链落盘 step.Detect.Iframe 路径）
/// - BUG-2：EnsurePhaseAsync loopCondition (h) gate per-selector（Round1 selA 接受 -> Round2 selB 触发，验 loopCondition (h) gate 修复）
/// </summary>
public class Decision2MockTests
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

    private static void WireHelp(RecordingEngine engine, string reply)
    {
        engine.OnRequestHelp += (a, q, s) => { engine.ReplyHelp(reply); return Task.CompletedTask; };
        engine.OnLog += (a, msg) => Task.CompletedTask;
    }

    /// <summary>创建 mock page：MainFrame + 一个子 frame（无 name/url + FrameElementAsync 返 Mock handle -> BuildFrameSelectorAsync 退化 FallbackSelector xpath=(//iframe)[1] 脆弱链）。</summary>
    private static IPage CreatePageWithFragileFrame(int locatorCount)
    {
        var mainFrame = Substitute.For<IFrame>();
        var browserMock = Substitute.For<IBrowser>();
        browserMock.IsConnected.Returns(true);
        var ctxMock = Substitute.For<IBrowserContext>();
        ctxMock.Browser.Returns(browserMock);
        ctxMock.Pages.Returns(new List<IPage>());
        var pageMock = Substitute.For<IPage>();
        pageMock.MainFrame.Returns(mainFrame);
        pageMock.Context.Returns(ctxMock);
        var fm = Substitute.For<IFrame>();
        fm.Name.Returns("");
        fm.Url.Returns("");
        fm.ParentFrame.Returns(mainFrame);
        fm.FrameElementAsync().Returns(Substitute.For<IElementHandle>());
        var locMock = Substitute.For<ILocator>();
        locMock.CountAsync().Returns(locatorCount);
        locMock.ClickAsync(Arg.Any<LocatorClickOptions?>()).Returns(Task.CompletedTask);
        locMock.DblClickAsync(Arg.Any<LocatorDblClickOptions?>()).Returns(Task.CompletedTask);
        locMock.First.Returns(locMock);
        locMock.Last.Returns(locMock);
        locMock.Locator(Arg.Any<string>(), Arg.Any<LocatorLocatorOptions?>()).Returns(locMock);
        fm.Locator(Arg.Any<string>(), Arg.Any<FrameLocatorOptions?>()).Returns(locMock);
        pageMock.Frames.Returns(new List<IFrame> { mainFrame, fm });
        // execute ResolveChain([xpath=(//iframe)[1]]) 逐层下钻 mock（Round2 falls-through 后 execute 用）
        var bodyLocMock = Substitute.For<ILocator>();
        bodyLocMock.CountAsync().Returns(locatorCount);
        bodyLocMock.Locator(Arg.Any<string>(), Arg.Any<LocatorLocatorOptions?>()).Returns(locMock);
        var frameLocMock = Substitute.For<IFrameLocator>();
        frameLocMock.Locator(Arg.Any<string>(), Arg.Any<FrameLocatorLocatorOptions?>()).Returns(locMock);
        bodyLocMock.FrameLocator(Arg.Any<string>()).Returns(frameLocMock);
        pageMock.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions?>()).Returns(bodyLocMock);
        mainFrame.Locator(Arg.Any<string>(), Arg.Any<FrameLocatorOptions?>()).Returns(locMock);
        return pageMock;
    }

    /// <summary>创建 mock page：仅 MainFrame + bodyLocMock 自返（CountAsync=count，Locator->自身，FrameLocator->frameLocMock）。供主文档 selector 校验 + ValidateIframeChainAsync + ResolveChain。</summary>
    private static IPage CreateMainDocPage(int count)
    {
        var mainFrameMock = Substitute.For<IFrame>();
        var browserMock = Substitute.For<IBrowser>();
        browserMock.IsConnected.Returns(true);
        var ctxMock = Substitute.For<IBrowserContext>();
        ctxMock.Browser.Returns(browserMock);
        ctxMock.Pages.Returns(new List<IPage>());
        var pageMock = Substitute.For<IPage>();
        pageMock.MainFrame.Returns(mainFrameMock);
        pageMock.Context.Returns(ctxMock);
        pageMock.Frames.Returns(new List<IFrame> { mainFrameMock });
        var bodyLocMock = Substitute.For<ILocator>();
        bodyLocMock.CountAsync().Returns(count);
        bodyLocMock.ClickAsync(Arg.Any<LocatorClickOptions?>()).Returns(Task.CompletedTask);
        bodyLocMock.DblClickAsync(Arg.Any<LocatorDblClickOptions?>()).Returns(Task.CompletedTask);
        bodyLocMock.First.Returns(bodyLocMock);
        bodyLocMock.Last.Returns(bodyLocMock);
        bodyLocMock.Locator(Arg.Any<string>(), Arg.Any<LocatorLocatorOptions?>()).Returns(bodyLocMock);
        var frameLocMock = Substitute.For<IFrameLocator>();
        frameLocMock.Locator(Arg.Any<string>(), Arg.Any<FrameLocatorLocatorOptions?>()).Returns(bodyLocMock);
        bodyLocMock.FrameLocator(Arg.Any<string>()).Returns(frameLocMock);
        pageMock.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions?>()).Returns(bodyLocMock);
        mainFrameMock.Locator(Arg.Any<string>(), Arg.Any<FrameLocatorOptions?>()).Returns(bodyLocMock);
        return pageMock;
    }

    private static Dictionary<string, object> OperateArgs(string action, string? selector = null, DetectCondition? condition = null, bool acceptFragile = false, bool acceptAsIs = false)
    {
        var args = new Dictionary<string, object> { ["action"] = action, ["name"] = "s", ["description"] = "d" };
        if (selector != null) args["selector"] = selector;
        if (condition != null) args["condition"] = System.Text.Json.JsonSerializer.SerializeToElement(condition);
        if (acceptFragile) args["acceptFragile"] = true;
        if (acceptAsIs) args["acceptAsIs"] = true;
        return args;
    }

    private static Dictionary<string, object> SaveStepArgs(string action, string? selector = null, StepFallback? fallback = null, bool acceptFragile = false, bool acceptAsIs = false)
    {
        var args = new Dictionary<string, object> { ["action"] = action, ["name"] = "s", ["description"] = "d" };
        if (selector != null) args["selector"] = selector;
        if (fallback != null) args["fallback"] = System.Text.Json.JsonSerializer.SerializeToElement(fallback);
        if (acceptFragile) args["acceptFragile"] = true;
        if (acceptAsIs) args["acceptAsIs"] = true;
        return args;
    }

    private static Dictionary<string, object> EnsurePhaseArgs(string phase, DetectCondition? loopCondition = null, bool acceptAsIs = false)
    {
        var args = new Dictionary<string, object> { ["phase"] = phase, ["phaseType"] = "sequential" };
        if (loopCondition != null) args["phaseLoopCondition"] = System.Text.Json.JsonSerializer.SerializeToElement(loopCondition);
        if (acceptAsIs) args["acceptAsIs"] = true;
        return args;
    }

    // ===== 待决策1：operate iframe-(f) 接受两轮 falls-through =====

    // 待决策1 Round1：operate selector 在脆弱 iframe 内（frame 无 name/url -> FallbackSelector xpath=(//iframe)[1] 脆弱链）-> 阶段0 IsFragileIframe=true -> caller RequestIframeFragileHelpAsync(StepIframe) -> 用户选 (f) -> 填 _acceptedFragile(StepIframe, chain) + reply F3①。
    // 验值：ret 含"已接受脆弱 iframe 链"（accepted=true 集合填的信号）+ "acceptFragile=true"（F3① reply 引导）+ TestStepCount==0（求助 return 不落盘，不循环到 maxTurns）。
    [Fact]
    public async Task Decision1_Operate_IframeFragile_Round1_HelpAndAcceptFragile()
    {
        var page = CreatePageWithFragileFrame(locatorCount: 1);  // frame 命中 selector（count=1）+ 脆弱链
        var engine = CreateEngine(page);
        WireHelp(engine, "f");  // 用户选 (f)

        var ret = await engine.ExecuteOperateAsync(OperateArgs("click", "//input[@id='x']"), CancellationToken.None);

        Assert.Equal(0, engine.TestStepCount);  // 求助 return 不落盘（不循环到 maxTurns）
        Assert.Contains("已接受脆弱 iframe 链", ret);  // accepted=true（集合填 StepIframe 的信号，验值非仅状态）
        Assert.Contains("acceptFragile=true", ret);  // F3① reply 引导 AI 走 (a) 反推路径
    }

    // 待决策1 Round2：同 engine（Round1 集合保留）+ acceptFragile=true + 同 selector -> 阶段0 反推同链 + isFragileAccepted(StepIframe, chain) 命中 -> falls-through -> execute click 成功 -> finalStep 落盘 step.Iframe=脆弱链 + onError=ai-fallback-stop。
    // 验值：TestStepCount==1（落盘 1 step，不求助不循环）+ CurrentSteps[0].Iframe==脆弱链 + OnError=="ai-fallback-stop"（R2-B finalStep 统一设）。
    [Fact]
    public async Task Decision1_Operate_IframeFragile_Round2_FallsThrough_LandsChainAndOnError()
    {
        var page = CreatePageWithFragileFrame(locatorCount: 1);
        var engine = CreateEngine(page);
        WireHelp(engine, "f");  // Round1 用户选 (f)
        // Round1：求助 (f) 填集合
        await engine.ExecuteOperateAsync(OperateArgs("click", "//input[@id='x']"), CancellationToken.None);
        Assert.Equal(0, engine.TestStepCount);  // Round1 不落盘

        // Round2：acceptFragile=true + 同 selector -> 集合命中 falls-through -> execute -> 落盘
        var ret2 = await engine.ExecuteOperateAsync(OperateArgs("click", "//input[@id='x']", acceptFragile: true), CancellationToken.None);

        Assert.Equal(1, engine.TestStepCount);  // Round2 落盘 1 step（falls-through 不循环）
        var landed = engine.TestLastStep;
        Assert.NotNull(landed);
        Assert.Equal(new[] { "xpath=(//iframe)[1]" }, landed!.Iframe);  // 落盘脆弱链（验值非仅状态）
        Assert.Equal("ai-fallback-stop", landed.OnError);  // R2-B finalStep iframeFragile 设 onError
    }

    // ===== 待决策2：复合 count==0 czLeafSelector =====

    // 待决策2 出参：复合 all=[leaf1(selA count==0), leaf2(selB count==0)] -> ProcessDetectAsync czLeafSelector=selA（第一个 count==0 叶 selector，复合时非空，caller (h) 求助指向真实叶）。
    // 验值：czLeafSelector=="//selA"（第一个 count==0 叶，非复合顶层 null）。
    [Fact]
    public async Task Decision2_CompositeCountZero_CzLeafSelector_FirstLeaf()
    {
        var page = CreateMainDocPage(count: 0);  // selA/selB 主文档 count==0
        var engine = CreateEngine(page);
        var step = new StepNode { Action = "check", Detect = new DetectCondition
        {
            All = new List<DetectCondition>
            {
                new DetectCondition { Type = "selector_exists", Selector = "//selA" },
                new DetectCondition { Type = "selector_exists", Selector = "//selB" }
            }
        } };

        var (_, err, _, _, countZero, _, czLeafSelector, _, _, _, _) = await engine.ProcessDetectAsync(step);

        Assert.Null(err);
        Assert.True(countZero);  // 复合 count==0
        Assert.Equal("//selA", czLeafSelector);  // 第一个 count==0 叶 selector（验值非仅状态）
    }

    // 待决策2 caller 两轮：复合 all=[selA count==0, selB count==0] -> Round1 (h) 求助 selA 接受 -> Round2 acceptAsIs=true -> selA skip（③段 IsAsIsAccepted）-> selB count==0 -> (h) gate per-selector 触发 selB（验 operate L539 per-selector gate，复合多 count==0 叶子第二个不 silent）。
    // 验值：Round2 ret 含 "//selB"（求助指向 selB，非 selA，证 per-selector gate self-correction）。
    [Fact]
    public async Task Decision2_Operate_CompositeCountZero_Round2_SecondLeaf_Helped()
    {
        var page = CreateMainDocPage(count: 0);
        var engine = CreateEngine(page);
        string lastQ = "";
        engine.OnRequestHelp += (a, q, s) => { lastQ = q; engine.ReplyHelp("h"); return Task.CompletedTask; };  // 两轮都选 (h)，捕获 question 验求助指向的 selector
        engine.OnLog += (a, msg) => Task.CompletedTask;
        var cond = new DetectCondition
        {
            All = new List<DetectCondition>
            {
                new DetectCondition { Type = "selector_exists", Selector = "//selA" },
                new DetectCondition { Type = "selector_exists", Selector = "//selB" }
            }
        };

        // Round1：(h) 求助 selA（czLeafSelector 第一个 count==0 叶）+ 填 _acceptedAsIs(DetectSelector, //selA)
        await engine.ExecuteOperateAsync(OperateArgs("click", "//btn", cond), CancellationToken.None);
        Assert.Equal(0, engine.TestStepCount);  // Round1 求助不落盘
        Assert.Contains("//selA", lastQ);  // Round1 question 含 selA（求助指向真实 count==0 叶，验值非仅状态）

        // Round2：acceptAsIs=true -> selA skip -> selB count==0 -> (h) gate per-selector 触发 selB
        lastQ = "";
        await engine.ExecuteOperateAsync(OperateArgs("click", "//btn", cond, acceptAsIs: true), CancellationToken.None);
        Assert.Equal(0, engine.TestStepCount);  // Round2 求助不落盘
        Assert.Contains("//selB", lastQ);  // Round2 question 含 selB（per-selector gate self-correction，非 selA）
    }

    // ===== 待决策3：fallback-selector 脆弱归源 =====

    // 待决策3 出参：fallback 场景A 仅 selector（GUID priority 8）-> ValidateFallbackAsync priority=8（fallback selector 脆弱信号产生）。
    // 验值：priority==8（fallback GUID selector 脆弱，非 0）。
    [Fact]
    public async Task Decision3_Fallback_GuidSelector_Priority8()
    {
        var page = CreateMainDocPage(count: 1);  // fallback selector 主文档 count==1（场景A 校验通过）
        var engine = CreateEngine(page);
        var fb = new StepFallback { Selector = "//input[@id='37b9c6a8-1234-5678-abcd-ef0123456789']" };  // GUID id -> AssessFragility 8

        var (modFb, err, prio) = await engine.ValidateFallbackAsync(fb, 1, new StepNode { Name = "s" }, "click", "d", default);

        Assert.Null(err);
        Assert.Equal(8, prio);  // fallback GUID selector 脆弱 priority 8（验值非仅状态）
    }

    // 待决策3 caller 归源：save_step step selector 正常（stable）+ fallback selector GUID priority 8 -> MaxPriorityWithSource 源=FallbackSelector -> 求助指向 fallback.Selector + 填 FallbackSelector 集合。
    // 验值：ret 含 fallback GUID selector（求助指向 fallback.Selector 非步 selector）+ TestStepCount==0（求助 return 不落盘）。
    [Fact]
    public async Task Decision3_SaveStep_FallbackFragile_SourceFallback_HelpPointsToFallback()
    {
        var page = CreateMainDocPage(count: 1);  // step selector + fallback selector 主文档 count==1
        var engine = CreateEngine(page);
        string lastQ = "";
        engine.OnRequestHelp += (a, q, s) => { lastQ = q; engine.ReplyHelp("f"); return Task.CompletedTask; };  // 用户选 (f)，捕获 question 验求助指向的 selector
        engine.OnLog += (a, msg) => Task.CompletedTask;
        var fb = new StepFallback { Selector = "//input[@id='37b9c6a8-1234-5678-abcd-ef0123456789']" };  // GUID priority 8

        await engine.ExecuteSaveStep(SaveStepArgs("check", "//input[@id='normal']", fb), CancellationToken.None);

        Assert.Equal(0, engine.TestStepCount);  // 求助 return 不落盘
        Assert.Contains("//input[@id='37b9c6a8-1234-5678-abcd-ef0123456789']", lastQ);  // 求助指向 fallback.Selector（归源正确，非 step selector "//input[@id='normal']"）
    }

    // ===== 结论13 C2：detect③/phase (f) =====

    // 结论13 C2 出参（验 BUG-1 ProcessDetectAsync 聚合）：step.Detect selector_exists + iframe=//iframe[@id='GUID']（AI 传脆弱链）-> ProcessDetectNodeAsync ①AI链分支 C1-A2 设 nodeIsFragileLayer=true -> ProcessDetectAsync 聚合 detectIsFragileLayer=true + IsFragileLayerChain=[GUID 链]。
    // 验值：IsFragileLayer==true + IsFragileLayerChain==[//iframe[@id='GUID']]（BUG-1 修复后聚合，修复前恒 false/null）。
    [Fact]
    public async Task Conclusion13_DetectIframeFragile_PdaAggregates_IsFragileLayerTrue()
    {
        var page = CreateMainDocPage(count: 1);  // ValidateIframeChainAsync 通过 + SelectorExistsAsync count=1
        var engine = CreateEngine(page);
        var guidChain = new[] { "//iframe[@id='37b9c6a8-1234-5678-abcd-ef0123456789']" };
        var step = new StepNode { Action = "check", Detect = new DetectCondition
        {
            Type = "selector_exists", Selector = "//button[@id='ok']", Iframe = guidChain
        } };

        var (_, err, _, _, _, _, _, isFragileLayer, isFragileLayerChain, _, _) = await engine.ProcessDetectAsync(step);

        Assert.Null(err);
        Assert.True(isFragileLayer);  // BUG-1 修复后聚合 detect③ 脆弱链（修复前恒 false）
        Assert.Equal(guidChain, isFragileLayerChain);  // 第一个未接受脆弱链（验值非仅状态）
    }

    // 结论13 C2 caller：operate step.Condition selector_exists + iframe=GUID 脆弱链 -> detectIsFragileLayer=true -> caller L551 detect③ (f) 求助（RequestIframeFragileHelpAsync DetectIframe + 填 DetectIframe 集合）。
    // 验值：ret 含"已接受脆弱 iframe 链"（accepted=true 集合填 DetectIframe）+ TestStepCount==0（求助 return 不落盘，不循环）。
    [Fact]
    public async Task Conclusion13_Operate_DetectIframeFragile_HelpAndAcceptFragile()
    {
        var page = CreateMainDocPage(count: 1);
        var engine = CreateEngine(page);
        WireHelp(engine, "f");  // 用户选 (f)
        var cond = new DetectCondition
        {
            Type = "selector_exists", Selector = "//button[@id='ok']",
            Iframe = new[] { "//iframe[@id='37b9c6a8-1234-5678-abcd-ef0123456789']" }
        };

        var ret = await engine.ExecuteOperateAsync(OperateArgs("click", "//btn", cond), CancellationToken.None);

        Assert.Equal(0, engine.TestStepCount);  // detect③ (f) 求助 return 不落盘（不循环）
        Assert.Contains("已接受脆弱 iframe 链", ret);  // accepted=true（集合填 DetectIframe，验值非仅状态）
    }

    // 结论13 C2 Round2 闭环（验 BUG-1 修复 + A′ self-correction）：Round1 detect③ (f) 接受填 DetectIframe 集合 -> Round2 acceptFragile=true + 同 condition iframe=GUID -> ProcessDetectAsync 聚合 pdaIsFragileLayerChain 查集合跳过已接受链（!IsFragileAccepted=false 不赋值 -> null）-> caller (f) gate `detectIsFragileLayerChain is {Length:>0}` false -> 不求助 (f)（不 loop）+ R2-C 设 onError -> execute 落盘。
    // 验值：Round2 TestStepCount==1（落盘，不循环到 maxTurns）+ ret 不含"已接受脆弱 iframe 链"（不 (f) 求助 = 集合命中闭环）+ 落盘 step.OnError=="ai-fallback-stop"（R2-C detect③ IsFragileLayer 客观兜底）。
    [Fact]
    public async Task Conclusion13_Operate_DetectIframeFragile_Round2_Accepted_NoLoop_LandsOnError()
    {
        var page = CreateMainDocPage(count: 1);  // detect③ ValidateIframeChainAsync 通过 + action selector 主文档 count==1 + execute click 成功
        var engine = CreateEngine(page);
        WireHelp(engine, "f");  // Round1 用户选 (f)
        var guidChain = new[] { "//iframe[@id='37b9c6a8-1234-5678-abcd-ef0123456789']" };
        var cond = new DetectCondition { Type = "selector_exists", Selector = "//button[@id='ok']", Iframe = guidChain };

        // Round1：detect③ (f) 求助 + 填 _acceptedFragile(DetectIframe, SerializeChain(guidChain))
        var ret1 = await engine.ExecuteOperateAsync(OperateArgs("click", "//btn", cond), CancellationToken.None);
        Assert.Equal(0, engine.TestStepCount);  // Round1 求助不落盘
        Assert.Contains("已接受脆弱 iframe 链", ret1);  // Round1 (f) 求助（DetectIframe 集合填）

        // Round2：acceptFragile=true + 同 condition iframe=GUID -> 集合命中 -> (f) gate false -> 不求助 -> execute 落盘
        var ret2 = await engine.ExecuteOperateAsync(OperateArgs("click", "//btn", cond, acceptFragile: true), CancellationToken.None);
        Assert.Equal(1, engine.TestStepCount);  // Round2 落盘（不循环到 maxTurns）
        Assert.DoesNotContain("已接受脆弱 iframe 链", ret2);  // 不 (f) 求助（集合命中闭环，A′ self-correction）
        Assert.Equal("ai-fallback-stop", engine.TestLastStep!.OnError);  // R2-C detect③ IsFragileLayer 客观兜底（已接受链也设 onError）
    }

    // ===== BUG-2：EnsurePhaseAsync loopCondition (h) gate per-selector =====

    // BUG-2 验证：EnsurePhaseAsync loopCondition 复合 all=[selA count==0, selB count==0] -> Round1 (h) 求助 selA 接受 -> Round2 acceptAsIs=true -> selA skip -> selB count==0 -> loopCondition (h) gate per-selector（L2346 修复后）触发 selB。
    // 验值：Round2 ret 含 "//selB"（loopCondition (h) gate per-selector 触发 selB，BUG-2 修复前 acceptAsIs=true 全局阻断 -> selB silent）。
    [Fact]
    public async Task Bug2_EnsurePhase_LoopConditionCountZero_Round2_SecondLeaf_Helped()
    {
        var page = CreateMainDocPage(count: 0);
        var engine = CreateEngine(page);
        WireHelp(engine, "h");  // 两轮都选 (h)
        var loopCond = new DetectCondition
        {
            All = new List<DetectCondition>
            {
                new DetectCondition { Type = "selector_exists", Selector = "//selA" },
                new DetectCondition { Type = "selector_exists", Selector = "//selB" }
            }
        };

        // Round1：loopCondition (h) 求助 selA + 填 _acceptedAsIs(DetectSelector, //selA)。return error 不创建 phase。
        var (err1, _) = await engine.EnsurePhaseAsync(EnsurePhaseArgs("bug2-phase", loopCond), CancellationToken.None);
        Assert.NotNull(err1);
        Assert.Contains("//selA", err1);  // Round1 求助指向 selA

        // Round2：acceptAsIs=true -> selA skip -> selB count==0 -> loopCondition (h) gate per-selector 触发 selB（BUG-2 修复后）
        var (err2, _) = await engine.EnsurePhaseAsync(EnsurePhaseArgs("bug2-phase", loopCond, acceptAsIs: true), CancellationToken.None);
        Assert.NotNull(err2);
        Assert.Contains("//selB", err2);  // Round2 求助指向 selB（per-selector gate，BUG-2 修复前 silent）
    }
}
