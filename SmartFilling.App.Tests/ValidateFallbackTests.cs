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
/// 24（a-a-4 B，2026-07-08）：ValidateFallbackAsync 场景分流测试。
/// 覆盖：fb=null（入口短路）/ 默认（selector+iframe 都没 -> 跳过校验）/ C′ 场景A extraction-failure（frame FrameElementAsync null->提取失败->通用求助，非 hard-error）。
/// ValidateFallbackAsync 提 internal 供直调；求助经 public OnRequestHelp event + ReplyHelp 驱动（mock page + mock IAiProvider 模式，仿 RecordingEngineDetectIframeTests）。
/// </summary>
public class ValidateFallbackTests
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

    /// <summary>创建 mock page：MainFrame + 可选子 frame（FrameElementAsync 返 handle 或 null 控制提取成功/失败）。</summary>
    private static IPage CreatePageWithFrame(string name, string url, int locatorCount, IElementHandle? frameElement)
    {
        var mainFrame = Substitute.For<IFrame>();
        var browserMock = Substitute.For<IBrowser>();
        browserMock.IsConnected.Returns(true);
        var ctxMock = Substitute.For<IBrowserContext>();
        ctxMock.Browser.Returns(browserMock);
        var pageMock = Substitute.For<IPage>();
        pageMock.MainFrame.Returns(mainFrame);
        pageMock.Context.Returns(ctxMock);
        var fm = Substitute.For<IFrame>();
        fm.Name.Returns(name);
        fm.Url.Returns(url);
        fm.ParentFrame.Returns(mainFrame);
        fm.FrameElementAsync().Returns(frameElement);
        var locMock = Substitute.For<ILocator>();
        locMock.CountAsync().Returns(locatorCount);
        fm.Locator(Arg.Any<string>(), Arg.Any<FrameLocatorOptions?>()).Returns(locMock);
        pageMock.Frames.Returns(new List<IFrame> { mainFrame, fm });
        return pageMock;
    }

    private static IPage CreateEmptyPage()
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
        return pageMock;
    }

    // fb=null -> 入口短路 (null, null, 0)。
    [Fact]
    public async Task NullFallback_ReturnsNullNoError()
    {
        var engine = CreateEngine(CreateEmptyPage());
        var (modFb, err, prio) = await engine.ValidateFallbackAsync(null, 1, new StepNode { Name = "s" }, "click", "d", default);
        Assert.Null(modFb);
        Assert.Null(err);
        Assert.Equal(0, prio);
    }

    // 默认（selector/iframe 都没）-> 跳过校验（结论15：回放期继承 step），err=null prio=0。
    [Fact]
    public async Task Default_NoSelectorNoIframe_SkipsValidation()
    {
        var engine = CreateEngine(CreateEmptyPage());
        var fb = new StepFallback();
        var (modFb, err, prio) = await engine.ValidateFallbackAsync(fb, 1, new StepNode { Name = "s" }, "click", "d", default);
        Assert.Null(err);
        Assert.Equal(0, prio);
        Assert.Null(modFb!.Selector);  // 默认跳过，不改 selector/iframe
        Assert.Null(modFb.Iframe);
    }

    // C′ 场景A extraction-failure：fb 仅 selector，反推时 frame FrameElementAsync null（detached/未加载）-> 提取失败 -> 通用 ExecuteRequestHelpAsync（非 hard-error，AI 可据回复调整）。
    // 验值：err 含"提取失败"+"已求助"（C′ 对齐 step extraction-failure 通用求助，不再 return error 硬阻断）。
    [Fact]
    public async Task ScenarioA_ExtractionFailure_GenericHelpNotHardError()
    {
        // frame f 命中（locatorCount=1）但 FrameElementAsync=null -> ExtractFrameChainAsync 抛 -> FindFrameForTargetAsync 返提取失败 warning + chain null
        var page = CreatePageWithFrame("f", "http://f", locatorCount: 1, frameElement: null);
        var engine = CreateEngine(page);
        // 模拟用户回复 (k) 其它（OnRequestHelp 触发时 ReplyHelp 注入回答，ExecuteRequestHelpAsync 返 "用户回答: k"）
        engine.OnRequestHelp += (a, q, s) => { engine.ReplyHelp("k"); return Task.CompletedTask; };
        engine.OnLog += (a, msg) => Task.CompletedTask;  // ExecuteRequestHelpAsync L1952 await OnLog?.Invoke（OnLog null->await null NRE，既有 latent：生产 OnLog 总设不触发）；设 no-op 规避

        var fb = new StepFallback { Selector = "//input[@id='x']" };
        var (modFb, err, prio) = await engine.ValidateFallbackAsync(fb, 1, new StepNode { Name = "s" }, "click", "d", default);

        Assert.Null(modFb);  // 提取失败返 null fallback（caller 不落盘脆弱链）
        Assert.NotNull(err);
        Assert.Contains("提取失败", err!);  // C′：extraction-failure 语义
        Assert.Contains("已求助", err!);  // 走通用求助（非 hard-error return）
    }

    // A1（a-a-4 测试增强）：C′ 场景A fragile（isFragileLayer）--fallback 仅 selector，反推链含脆弱位置选择器层（(//iframe)[1]）-> RequestIframeFragileHelpAsync（(f)/(i) 求助，对齐 step）。
    // mock：frame 无 name/url + FrameElementAsync 返 Mock<IElementHandle>（EvaluateAsync 返 null -> attrs null -> BuildFrameSelectorAsync L268 FallbackSelector -> xpath=(//iframe)[1] fragile）+ frame.Locator(selector).CountAsync()>0（命中 IsTargetInFrameAsync）。
    // 用户未选 (f)（reply k）-> return error"脆弱未接受"（区别 extraction-failure 的"提取失败"--两条 C′ 场景A 路径分流验证）。
    [Fact]
    public async Task ScenarioA_FragileChain_FragileHelpNotExtractionFailure()
    {
        // frame 无 name/url -> FallbackSelector 产 xpath=(//iframe)[1]（位置选择器层，isFragileLayer）；FrameElementAsync 返 Mock<IElementHandle>（attrs null，不抛 -> 非提取失败）
        var page = CreatePageWithFrame("", "", locatorCount: 1, frameElement: Substitute.For<IElementHandle>());
        var engine = CreateEngine(page);
        engine.OnRequestHelp += (a, q, s) => { engine.ReplyHelp("k"); return Task.CompletedTask; };  // 用户未选 (f)
        engine.OnLog += (a, msg) => Task.CompletedTask;

        var fb = new StepFallback { Selector = "//input[@id='x']" };  // 仅 selector（场景A）-> FindFrameForTargetAsync 反推
        var (modFb, err, prio) = await engine.ValidateFallbackAsync(fb, 1, new StepNode { Name = "s" }, "click", "d", default);

        Assert.Null(modFb);  // 用户未接受 (f) -> 返 null fallback（caller 不落盘脆弱链）
        Assert.NotNull(err);  // fragile + 未接受 (f) -> 返 error 让 AI 据回复调整
        Assert.Contains("脆弱", err!);  // fragile 语义（isFragileLayer 路径）
        Assert.Contains("未接受", err!);  // (f) 未接受出口
        Assert.DoesNotContain("提取失败", err!);  // 区别 extraction-failure（场景A 另一条 C′ 路径，上一个测试）
    }

    // A2 场景B（a-a-4 测试增强）：fb 仅 iframe（AI 传链，无 selector）-> ValidateIframeChainAsync 校验通过 -> 链保留（结论15 selector 回放期继承 step）。
    [Fact]
    public async Task ScenarioB_OnlyIframe_ChainValid_KeepsChain()
    {
        var page = CreatePageWithBodyLocator(count: 1);  // ValidateIframeChainAsync 通过（body 链 count=1 唯一）
        var engine = CreateEngine(page);
        var fb = new StepFallback { Iframe = new[] { "//iframe[@id='x']" } };  // 仅 iframe（场景B）

        var (modFb, err, prio) = await engine.ValidateFallbackAsync(fb, 1, new StepNode { Name = "s" }, "click", "d", default);

        Assert.Null(err);  // ValidateIframeChainAsync 通过（反推成功）
        Assert.Equal(new[] { "//iframe[@id='x']" }, modFb!.Iframe);  // 链保留
    }

    // A2 场景C（a-a-4 测试增强）：fb selector+iframe 都有 -> ValidateSaveStepSelectorAsync（ResolveChain->frame 内 selector 唯一）+ ValidateIframeChainAsync 都通过 -> 两者保留。
    [Fact]
    public async Task ScenarioC_SelectorAndIframe_Valid_KeepsBoth()
    {
        // count=1：ResolveChain(["//iframe[@id='x']"])->bodyLocMock，ValidateSelectorAsync 在其上 Locator(selector).CountAsync()==1 唯一；ValidateIframeChainAsync 同样通过
        var page = CreatePageWithBodyLocator(count: 1);
        var engine = CreateEngine(page);
        var fb = new StepFallback { Selector = "//button[@id='ok']", Iframe = new[] { "//iframe[@id='x']" } };  // 都有（场景C）

        var (modFb, err, prio) = await engine.ValidateFallbackAsync(fb, 1, new StepNode { Name = "s" }, "click", "d", default);

        Assert.Null(err);  // selector + 链校验都通过（反推成功）
        Assert.Equal("//button[@id='ok']", modFb!.Selector);  // selector 保留
        Assert.Equal(new[] { "//iframe[@id='x']" }, modFb!.Iframe);  // 链保留
    }

    /// <summary>A2：创建 mock page 支持 ValidateIframeChainAsync + ResolveChain（page.Locator("body").Locator(chain).CountAsync + FrameLocator(sel).Locator("body") 逐层下钻，bodyLocMock 自返统一 count）。</summary>
    private static IPage CreatePageWithBodyLocator(int count)
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
        // bodyLocMock 自返：Locator(any)->自身 / CountAsync=count / FrameLocator(any)->frameLocMock（其 Locator(any)->bodyLocMock，模拟逐层下钻回到 body 根）
        var bodyLocMock = Substitute.For<ILocator>();
        bodyLocMock.CountAsync().Returns(count);
        bodyLocMock.Locator(Arg.Any<string>(), Arg.Any<LocatorLocatorOptions?>()).Returns(bodyLocMock);
        var frameLocMock = Substitute.For<IFrameLocator>();
        frameLocMock.Locator(Arg.Any<string>(), Arg.Any<FrameLocatorLocatorOptions?>()).Returns(bodyLocMock);
        bodyLocMock.FrameLocator(Arg.Any<string>()).Returns(frameLocMock);
        pageMock.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions?>()).Returns(bodyLocMock);
        return pageMock;
    }
}
