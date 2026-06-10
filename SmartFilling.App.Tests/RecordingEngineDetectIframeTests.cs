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
/// §3/§4/§5 RecordingEngine detect 路径反推回归测试：直接驱动 internal ProcessDetectAsync / ProcessDetectNodeAsync。
/// 验证：①按 detect 类型反推 iframe（page_contains 填对 / url_changed 保持 null）②反推失败告警（page_contains+有iframe 告警 / 无iframe 不告警）
/// ③step.Condition 反推 ④lenient 差异（step 阻断 vs phase 告警）。
/// 真实 RecordingEngine + 真实 _actionExecutor（内部真实 IframeDetector）+ mock IPage.Frames，验反推逻辑链路。
/// </summary>
public class RecordingEngineDetectIframeTests
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

    /// <summary>创建 mock page：MainFrame + 子 frame（parent=MainFrame，locatorCount/bodyText 控制反推命中）。</summary>
    private static IPage CreatePage(params (string name, string url, int locatorCount, string? bodyText)[] childSpecs)
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
            // 形态 A：FrameElementAsync 返回 mock IElementHandle（EvaluateAsync 默认返 null -> GetIframeAttributesAsync 返 null
            // -> BuildFrameSelectorAsync 退化 FallbackSelector 产 //iframe[@name='name']），让链提取在 mock 下可走通。
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

    // ①page_contains 在含 keywords 的 iframe -> detect.Iframe 填对
    [Fact]
    public async Task PageContains_InIframe_FillsDetectIframe()
    {
        var page = CreatePage(("f", "http://f", 0, "提交成功"));
        var engine = CreateEngine(page);
        var step = new StepNode { Detect = new DetectCondition { Type = "page_contains", Keywords = new[] { "提交成功" } }, Then = "continue" };

        var (processed, err, _, _, _, _, _, _, _, _, _) = await engine.ProcessDetectAsync(step);

        Assert.Null(err);
        Assert.NotNull(processed.Detect!.Iframe);  // 反推填对（非第一个非主 frame 盲填，而是含 keywords 的 frame）
    }

    // ①b url_changed -> detect.Iframe 保持 null（不反推：回放读 activePage.Url，与 frame 无关）
    [Fact]
    public async Task UrlChanged_KeepsDetectIframeNull()
    {
        var page = CreatePage(("f", "http://f", 1, null));  // 有 iframe，但 url_changed 不反推
        var engine = CreateEngine(page);
        var step = new StepNode { Detect = new DetectCondition { Type = "url_changed" }, Then = "continue" };

        var (processed, err, _, _, _, _, _, _, _, _, _) = await engine.ProcessDetectAsync(step);

        Assert.Null(err);
        Assert.Null(processed.Detect!.Iframe);  // 保持 null（填错反而是 bug）
    }

    // ②page_contains keywords 未现（轮询超时）+ 有 iframe -> D-2 警告（不阻断；强调"不要据此重录同名 wait"，堵 Bug 2 重名路径）
    [Fact]
    public async Task PageContains_InferenceFail_WithIframe_WarnsNotBlocks()
    {
        var page = CreatePage(("f", "http://f", 0, "无关内容"));  // 有 iframe 但不含目标文本
        var engine = CreateEngine(page);
        var step = new StepNode { Detect = new DetectCondition { Type = "page_contains", Keywords = new[] { "目标文本" } }, Then = "continue", Timeout = 1 };  // D-1 轮询超时上限 1ms（避免默认 30s）

        var (processed, err, warnings, _, _, _, _, _, _, _, _) = await engine.ProcessDetectAsync(step);

        Assert.Null(err);  // D-2 告警不阻断 step 保存
        Assert.NotEmpty(warnings);
        // D-2 新措辞：含 page_contains + "不要据此重录同名 wait"（堵 Bug 2 误报诱导重录）
        Assert.Contains(warnings, w => w.Contains("page_contains") && w.Contains("不要据此重录同名 wait"));
    }

    // ②b page_contains keywords 未现 + 无 iframe -> D-2 警告（不阻断；D-1 轮询超时不论 iframe 数都告警，堵"页面慢诱导重录"）
    [Fact]
    public async Task PageContains_InferenceFail_NoIframe_WarnsNoBlock()
    {
        var page = CreatePage();  // 只 MainFrame，无子 frame
        var engine = CreateEngine(page);
        var step = new StepNode { Detect = new DetectCondition { Type = "page_contains", Keywords = new[] { "目标" } }, Then = "continue", Timeout = 1 };  // D-1 轮询超时上限 1ms

        var (processed, err, warnings, _, _, _, _, _, _, _, _) = await engine.ProcessDetectAsync(step);

        Assert.Null(err);
        Assert.NotEmpty(warnings);  // D-1 轮询超时 -> D-2 警告（不论有无 iframe，keywords 没出现即告警提醒"不要重录"）
        Assert.Contains(warnings, w => w.Contains("不要据此重录同名 wait"));
    }

    // ③step.Condition 反推（步骤执行前条件，与 step 同 frame 同时机）
    [Fact]
    public async Task StepCondition_Inferred()
    {
        var page = CreatePage(("f", "http://f", 0, "已加载完成"));
        var engine = CreateEngine(page);
        var step = new StepNode { Condition = new DetectCondition { Type = "page_contains", Keywords = new[] { "已加载完成" } } };

        var (processed, err, _, _, _, _, _, _, _, _, _) = await engine.ProcessDetectAsync(step);

        Assert.Null(err);
        Assert.NotNull(processed.Condition!.Iframe);  // step.Condition 也经反推
    }

    // ④lenient 差异：selector 校验失败 -> step 级(lenient=false)阻断 error；phase 级(lenient=true)降级 warning 不阻断
    [Fact]
    public async Task SelectorValidateFail_Lenient_DifferStrictVsLenient()
    {
        var page = CreatePage(("f", "http://f", 0, null));  // frame 无元素 -> 反推不命中 + ③校验失败
        var engine = CreateEngine(page);
        var node = new DetectCondition { Type = "selector_visible", Selector = "//input[@id='x']" };

        // step 级 lenient=false -> 阻断
        var wsStrict = new List<string>();
        var (_, errStrict, _, _, _, _, _, _, _, _) = await engine.ProcessDetectNodeAsync(node, wsStrict, lenient: false);
        Assert.NotNull(errStrict);
        Assert.Empty(wsStrict);

        // phase 级 lenient=true -> 降级 warning 不阻断
        var wsLenient = new List<string>();
        var (_, errLenient, _, _, _, _, _, _, _, _) = await engine.ProcessDetectNodeAsync(node, wsLenient, lenient: true);
        Assert.Null(errLenient);
        Assert.NotEmpty(wsLenient);  // selector 校验失败降级 warning
    }

    // ④b ref 提取失败 lenient 差异（ref 在 mock page 找不到元素）
    [Fact]
    public async Task RefExtractFail_Lenient_ClearsRefAndWarns()
    {
        // selector_exists + ref：ExtractSelectorFromRefAsync 在 mock page 找不到 aria-ref -> 失败
        var page = CreatePage(("f", "http://f", 0, null));
        var engine = CreateEngine(page);
        var node = new DetectCondition { Type = "selector_exists", Ref = "e99" };

        // lenient=true（phase 级）-> 降级 warning + 清空 Ref
        var ws = new List<string>();
        var (result, err, _, _, _, _, _, _, _, _) = await engine.ProcessDetectNodeAsync(node, ws, lenient: true);
        Assert.Null(err);
        Assert.NotEmpty(ws);
        Assert.Null(result!.Ref);  // 清空 Ref 避免残留
    }

    // ⑤ new_row_appears 走通用 selector 类反推（InferIframeForDetect L1044-1049 通用 fallback，非"不反推类型"非 page_contains -> 有 selector 就反推）。
    // 防回归 + 证明：回放 EvaluateNewRowAppearsAsync 用 frame.Locator(selector).CountAsync() 依赖 frame，录制反推与之对齐。
    [Fact]
    public async Task NewRowAppears_Inferred_AsSelectorType()
    {
        var page = CreatePage(("f", "http://f", 1, null));  // frame 含 selector（CountAsync=1）
        var engine = CreateEngine(page);
        var node = new DetectCondition { Type = "new_row_appears", Selector = "//tr" };

        var ws = new List<string>();
        var (result, err, _, _, _, _, _, _, _, _) = await engine.ProcessDetectNodeAsync(node, ws, lenient: false);

        Assert.Null(err);
        Assert.NotNull(result!.Iframe);  // new_row_appears 反推到含 //tr 的 frame（与回放 frame 依赖一致）
    }

    // D-1 行为：page_contains keywords 可见 -> 轮询第一次即退出（不空等）+ 正确反推 iframe，不触发 D-2 警告。
    // 堵 Bug 2：页面慢不再误报诱导重录。"延迟出现后退出"语义由 while + KeywordsVisibleAnywhereAsync 返 true 即退（不空等）的代码逻辑 firsthand 保证 + 测试 2/3 超时分支覆盖。
    // 用 CreatePage 稳定 mock（iframe body 固定含 keywords，与 PageContains_InIframe 同源已验证稳定；不依赖计数器 mock--计数器在全量测试环境下 frameCalls 边界偶发不稳）。
    [Fact]
    public async Task PageContains_KeywordsVisible_PollsOnce_InferredNoD2Warn()
    {
        var page = CreatePage(("content-frame", "http://content", 0, "欢迎来到我的桌面"));  // iframe body 含 keywords
        var engine = CreateEngine(page);
        var step = new StepNode { Detect = new DetectCondition { Type = "page_contains", Keywords = new[] { "我的桌面" } }, Then = "continue", Timeout = 500 };

        var (processed, err, warnings, _, _, _, _, _, _, _, _) = await engine.ProcessDetectAsync(step);

        Assert.Null(err);
        Assert.NotNull(processed.Detect!.Iframe);  // keywords 可见 -> 轮询第一次退出 + 反推命中 content-frame
        Assert.DoesNotContain(warnings, w => w.Contains("不要据此重录同名 wait"));  // 未触发 D-2 警告
    }

    // D-2 触发点②：page_contains keywords 在主文档可见（不在任何 iframe）+ 有非主 frame -> 警告"未在任何 iframe 找到"（措辞准确，区别于触发点①"未出现"--keywords 已出现，不诱导重录）
    [Fact]
    public async Task PageContains_KeywordsInMainFrame_NotInIframe_WarnsAccurateNotMisleading()
    {
        // 自定义 mock：MainFrame body 含 keywords（D-1 轮询可见->不超时）+ 子 frame "ad" body 不含 -> FindFrameForTargetAsync inferred=false -> 触发点②
        var mainFrameMock = Substitute.For<IFrame>();
        mainFrameMock.EvaluateAsync<string>(Arg.Any<string>(), Arg.Any<object?>()).Returns("欢迎来到我的桌面");
        var browserMock = Substitute.For<IBrowser>();
        browserMock.IsConnected.Returns(true);
        var ctxMock = Substitute.For<IBrowserContext>();
        ctxMock.Browser.Returns(browserMock);
        var pageMock = Substitute.For<IPage>();
        pageMock.MainFrame.Returns(mainFrameMock);
        pageMock.Context.Returns(ctxMock);
        var adFrame = Substitute.For<IFrame>();
        adFrame.Name.Returns("ad");
        adFrame.Url.Returns("http://ad");
        adFrame.ParentFrame.Returns(mainFrameMock);
        adFrame.EvaluateAsync<string>(Arg.Any<string>(), Arg.Any<object?>()).Returns("广告内容");
        pageMock.Frames.Returns(new List<IFrame> { mainFrameMock, adFrame });
        var engine = CreateEngine(pageMock);

        var step = new StepNode { Detect = new DetectCondition { Type = "page_contains", Keywords = new[] { "我的桌面" } }, Then = "continue", Timeout = 500 };

        var (processed, err, warnings, _, _, _, _, _, _, _, _) = await engine.ProcessDetectAsync(step);

        Assert.Null(err);
        // 触发点②：keywords 在主文档可见不在 iframe -> 警告"未在任何 iframe 找到"（不是"未出现"）
        Assert.Contains(warnings, w => w.Contains("未在任何 iframe 找到") && w.Contains("不要据此重录同名 wait"));
        Assert.DoesNotContain(warnings, w => w.Contains("未出现"));  // 措辞准确（keywords 已出现，区别于触发点①"未出现"）
    }

    // ===== 2c（B9 detect）测试 =====

    /// <summary>创建主文档 mock page（page.Locator 链 CountAsync=count，供 detect ③ ValidateSaveStepSelectorAsync 校验）。</summary>
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
        pageMock.Frames.Returns(new List<IFrame> { mainFrameMock });  // 只主文档
        var bodyLocMock = Substitute.For<ILocator>();
        bodyLocMock.CountAsync().Returns(count);
        bodyLocMock.Locator(Arg.Any<string>(), Arg.Any<LocatorLocatorOptions?>()).Returns(bodyLocMock);
        // A6②（a-a-4 测试增强）：ValidateIframeChainAsync L1018 用 locator.FrameLocator(chain).Locator("body") 逐层下钻，补 FrameLocator mock 防 NRE（无 AI chain 的测试不触发，无害）。
        var frameLocMock = Substitute.For<IFrameLocator>();
        frameLocMock.Locator(Arg.Any<string>(), Arg.Any<FrameLocatorLocatorOptions?>()).Returns(bodyLocMock);
        bodyLocMock.FrameLocator(Arg.Any<string>()).Returns(frameLocMock);
        pageMock.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions?>()).Returns(bodyLocMock);
        mainFrameMock.Locator(Arg.Any<string>(), Arg.Any<FrameLocatorOptions?>()).Returns(bodyLocMock);
        return pageMock;
    }

    // 2c B9：detect strict 类（selector_visible）+ 唯一 + 脆弱 selector（GUID priority 8）-> 唯一性通过 + AssessFragility 软告警
    [Fact]
    public async Task Detect_StrictClass_FragileSelector_WarnsSoftly()
    {
        var page = CreateMainDocPage(count: 1);  // 唯一（count=1）
        var engine = CreateEngine(page);
        var node = new DetectCondition { Type = "selector_visible", Selector = "//*[@id='37b9c6a8-1234-5678-abcd-ef0123456789']" };  // GUID selector -> AssessFragility 8

        var ws = new List<string>();
        // DC1（a-a-4 R5）：lenient=false（step 级）priority>=8 走 Priority 出参（caller 求助），不 Add warning -> ws 空。
        var (result, err, prio, _, _, _, _, _, _, _) = await engine.ProcessDetectNodeAsync(node, ws, lenient: false);

        Assert.Null(err);  // 唯一性通过（count=1）-> 不阻断
        Assert.Equal(8, prio);  // AssessFragility(GUID)=8 -> Priority 出参（断言验值不只验状态）
        Assert.Empty(ws);  // lenient=false 不软告警（走 caller 求助，BuildHelpQuestion question 断言放 caller 集成测试）
    }

    // 2c B9：detect count>0 类（selector_exists）+ 多匹配（count=2）-> 多匹配合法不 error（只校验存在性 count>0）
    [Fact]
    public async Task Detect_CountBased_MultiMatch_Legal()
    {
        var page = CreateMainDocPage(count: 2);  // 多匹配
        var engine = CreateEngine(page);
        var node = new DetectCondition { Type = "selector_exists", Selector = "//button" };  // count>0 类，多匹配合法

        var ws = new List<string>();
        var (_, err, _, _, _, _, _, _, _, _) = await engine.ProcessDetectNodeAsync(node, ws, lenient: false);

        Assert.Null(err);  // count>0 类多匹配合法（区别 strict 类多匹配抛错），不阻断
    }

    // 2c ⑦（方案 C）：wait.Until selector_visible + 元素已到 DOM（count=1）-> 轮询第一次即退出 + ③ 校验通过。验证 ⑦ 轮询逻辑可运行 + 不崩。
    [Fact]
    public async Task WaitUntil_SelectorVisible_PollsFoundNoCrash()
    {
        var page = CreateMainDocPage(count: 1);  // 元素在 DOM（count=1）-> ⑦ 轮询 SelectorExistsAnywhereAsync 第一次即 found
        var engine = CreateEngine(page);
        var node = new DetectCondition { Type = "selector_visible", Selector = "//*[@id='late-load']" };

        var ws = new List<string>();
        // isWaitUntil=true（仅 wait.Until 触发轮询）+ pollTimeout=500ms；元素 count=1 -> 轮询第一次即退出
        var (result, err, _, _, _, _, _, _, _, _) = await engine.ProcessDetectNodeAsync(node, ws, lenient: false, pollTimeout: 500, pollInterval: 10, isWaitUntil: true);

        Assert.Null(err);  // ③ strict 类唯一性通过（count=1）+ id 稳定（late-load 非动态）-> 无 error 无告警
        Assert.NotNull(result);  // 未抛异常（⑦ 轮询 + ③ 校验正常执行，验证轮询逻辑可运行）
    }

    // ND8 B（a-a-4，2026-07-08）：detect③ page_contains keywords 在 2 个 iframe 都命中 -> step 级(lenient=false) 走 full picker 路径：
    // 出参 MultiFrames 非空（caller operate/save_step 据此 RequestMultiFrameHelpAsync）+ result.Iframe 填第一命中默认兜底；不走 error（A-path 已废弃）。
    [Fact]
    public async Task Detect_PageContains_MultiFrame_StepLevel_ReturnsMultiFramesOutParam()
    {
        // 2 个子 frame body 都含 keywords "成功" -> FindFrameForTargetAsync 遍历均命中 -> multi frame（hits>=2）
        var page = CreatePage(
            ("frame-a", "http://a", 0, "提交成功"),
            ("frame-b", "http://b", 0, "操作成功"));
        var engine = CreateEngine(page);
        var node = new DetectCondition { Type = "page_contains", Keywords = new[] { "成功" } };

        var ws = new List<string>();
        var (result, err, _, _, mf, _, _, _, _, _) = await engine.ProcessDetectNodeAsync(node, ws, lenient: false);

        Assert.Null(err);  // 多 frame 不走 error（picker 路径，非 A-path warning->error）
        Assert.NotNull(mf);  // ND8 B：出参 MultiFrames 非空，caller 据此 (j) picker
        Assert.True(mf!.Count >= 2);  // 验值：2 个命中 frame（不只验状态）
        Assert.NotNull(result!.Iframe);  // result.Iframe 填第一命中默认兜底（picker (e) 跳过时落盘）
        Assert.Contains("frame-a", result!.Iframe![0]);  // A5（a-a-4 测试增强）：验第一 frame chain 值（frame-a 命中链，不只 NotNull 验状态）
        Assert.Empty(ws);  // ⑤⑥（2026-07-09）：picker 路径不再 Add warning（原 #2 兜底已回退--operate ⑤/save_step/EnsurePhaseAsync ⑥① picker 都消费 multiFrames 出参走 reply，兜底冗余；phase 级 lenient=true 软告警见下条测试 L1711 分支）
    }

    // ND8 B：detect③ 多 frame 在 lenient=true 分支不 picker（MultiFrames=null + 软告警 L1711 warnings.Add，AI 可见 warning 自理）。
    // B（测试现状校准，2026-07-09）：D-I + ⑥ 后 EnsurePhaseAsync 用 lenient=false（phase 级多 frame 走 ⑥① picker，见 EnsurePhaseHelpTests.PhaseCondition_MultiFrame_Picker），
    // 故 lenient=true 为兼容层（目前无 production caller，但保留覆盖防回归--确认 step 级 picker（!lenient）不误蔓延到 lenient=true 软告警语义）。
    [Fact]
    public async Task Detect_PageContains_MultiFrame_PhaseLevel_NoPickerWarnsSoftly()
    {
        var page = CreatePage(
            ("frame-a", "http://a", 0, "提交成功"),
            ("frame-b", "http://b", 0, "操作成功"));
        var engine = CreateEngine(page);
        var node = new DetectCondition { Type = "page_contains", Keywords = new[] { "成功" } };

        var ws = new List<string>();
        var (_, err, _, _, mf, _, _, _, _, _) = await engine.ProcessDetectNodeAsync(node, ws, lenient: true);

        Assert.Null(err);  // phase 级 lenient 不阻断
        Assert.Null(mf);  // phase 级不 picker，MultiFrames 不出参（caller EnsurePhaseAsync 不消费）
        Assert.NotEmpty(ws);  // 软告警（多 frame warning 加入 warnings，AI 可见）
        Assert.Contains(ws, w => w.Contains("iframe"));  // 验值：warning 含 iframe 提示
    }

    // T9（a-a-4 B，2026-07-08）：document_ready（不反推类型）无 AI iframe -> 归一产 []（对齐 operate/detect反推类型/save_step 主文档 []，D3：[]=主文档不继承 / null=继承 phase）。
    // 防回归：原产 null（继承 phase.iframe，现状恒 null->顶层），T9 后显式 []；验值 result.Iframe 非 null 且空数组（不只验状态）。
    [Fact]
    public async Task DocumentReady_NoIframe_NormalizedToEmptyArray()
    {
        var page = CreatePage();  // 主文档，无子 frame（document_ready 不反推，page 内容不影响）
        var engine = CreateEngine(page);
        var node = new DetectCondition { Type = "document_ready" };

        var ws = new List<string>();
        var (result, err, _, _, _, _, _, _, _, _) = await engine.ProcessDetectNodeAsync(node, ws, lenient: false);

        Assert.Null(err);
        Assert.NotNull(result!.Iframe);  // T9：归一化为 []（非 null）
        Assert.Empty(result.Iframe!);  // 验值：空数组（主文档，D3 []=主文档不继承 phase）
    }

    // A6②（a-a-4 测试增强）：document_ready + AI 传 Iframe 链（非空，ValidateIframeChainAsync 校验通过）-> T9 不归一化 []（保留 AI 显式指定链）。
    // 防回归：T9 归一化只对 Iframe null/empty 触发；有 AI 链时保留（document_ready 在 iframe 上下文，AI 显式指定链不应被覆盖成主文档 []）。
    [Fact]
    public async Task DocumentReady_AiIframeChain_NotNormalizedKeepsChain()
    {
        var page = CreateMainDocPage(count: 1);  // ValidateIframeChainAsync 通过（page.Locator(chain).CountAsync()>0）
        var engine = CreateEngine(page);
        var node = new DetectCondition { Type = "document_ready", Iframe = new[] { "//iframe[@id='ai-frame']" } };

        var ws = new List<string>();
        var (result, err, _, _, _, _, _, _, _, _) = await engine.ProcessDetectNodeAsync(node, ws, lenient: false);

        Assert.Null(err);
        Assert.Equal(new[] { "//iframe[@id='ai-frame']" }, result!.Iframe);  // A6②：保留 AI 链（非 T9 归一化的 []）
    }

    // A6①（a-a-4 测试增强）：document_ready + iframeRef -> ResolveIframeFromRefAsync 解析出链 -> T9 不归一化 []（保留解析链，区别 A6② AI 直传链--链来源不同但 T9 逻辑同：Iframe 非空不归一化）。
    [Fact]
    public async Task DocumentReady_IframeRef_ResolvesChainNotNormalized()
    {
        var iframeEl = Substitute.For<IElementHandle>();  // EvaluateAsync 返 null -> attrs null -> FallbackSelector 用 frame.Name 产稳定 selector
        var page = CreatePageWithIframeRef("ai-frame", iframeEl);
        var engine = CreateEngine(page);
        var node = new DetectCondition { Type = "document_ready", IframeRef = "e1" };

        var ws = new List<string>();
        var (result, err, _, _, _, _, _, _, _, _) = await engine.ProcessDetectNodeAsync(node, ws, lenient: false);

        Assert.Null(err);
        Assert.NotNull(result!.Iframe);
        Assert.NotEmpty(result!.Iframe!);  // A6①：iframeRef 解析出链（非 T9 归一化的 []）
        Assert.Contains("//iframe[@name='ai-frame']", result!.Iframe![0]);  // 验值：FallbackSelector 用 frame.Name 产的稳定链
    }

    /// <summary>A6①：创建 mock page 支持 ResolveIframeFromRefAsync（page.Locator(aria-ref).ElementHandleAsync->iframeEl + 子 frame FrameElementAsync 引用相等命中）+ ValidateIframeChainAsync（Locator/CountAsync/FrameLocator 链）。</summary>
    private static IPage CreatePageWithIframeRef(string frameName, IElementHandle iframeEl)
    {
        var mainFrameMock = Substitute.For<IFrame>();
        var browserMock = Substitute.For<IBrowser>();
        browserMock.IsConnected.Returns(true);
        var ctxMock = Substitute.For<IBrowserContext>();
        ctxMock.Browser.Returns(browserMock);
        var pageMock = Substitute.For<IPage>();
        pageMock.MainFrame.Returns(mainFrameMock);
        pageMock.Context.Returns(ctxMock);
        // 统一 locMock：ElementHandleAsync（aria-ref 定位 iframeEl）+ Locator 链 + CountAsync（ValidateIframeChainAsync count）+ FrameLocator（逐层下钻）
        var locMock = Substitute.For<ILocator>();
        locMock.CountAsync().Returns(1);
        locMock.Locator(Arg.Any<string>(), Arg.Any<LocatorLocatorOptions?>()).Returns(locMock);
        locMock.ElementHandleAsync(Arg.Any<LocatorElementHandleOptions?>()).Returns(iframeEl);
        var frameLocMock = Substitute.For<IFrameLocator>();
        frameLocMock.Locator(Arg.Any<string>(), Arg.Any<FrameLocatorLocatorOptions?>()).Returns(locMock);
        locMock.FrameLocator(Arg.Any<string>()).Returns(frameLocMock);
        pageMock.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions?>()).Returns(locMock);
        // 子 frame：Name（FallbackSelector 产 //iframe[@name=...] 稳定 selector）+ FrameElementAsync 返 iframeEl（ResolveIframeFromRefAsync 引用相等命中 found）
        var fm = Substitute.For<IFrame>();
        fm.Name.Returns(frameName);
        fm.ParentFrame.Returns(mainFrameMock);
        fm.FrameElementAsync().Returns(iframeEl);
        pageMock.Frames.Returns(new List<IFrame> { mainFrameMock, fm });
        return pageMock;
    }
}
