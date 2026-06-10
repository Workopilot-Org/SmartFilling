using Microsoft.Playwright;
using NSubstitute;
using SmartFilling.App.Configuration;
using SmartFilling.App.Recording;
using SmartFilling.Engine.Models;
using EngineILogger = SmartFilling.Engine.Logging.ILogger;

namespace SmartFilling.App.Tests;

/// <summary>
/// 形态 A（2026-07-02）：RecordingActionExecutor.ExecuteOperateAsync iframe 反推单测。
/// 验阶段0 覆盖语义（AI 传 iframe 链->ValidateIframeChainAsync 校验）+ 未命中->null 链（不退化旧"第一个非主 frame"盲填）。
/// 🟡 mock 边界声明：mock 验反推/校验逻辑分支，验不出 Playwright 真能在 frame 内解析（FrameElementAsync+FrameLocator 链）--真实浏览器集成另测（IframeNestedBrowserTests）。
/// </summary>
public class RecordingActionExecutorOperateIframeTests
{
    /// <summary>创建 mock page：MainFrame + 子 frame（locatorCount 控制反推命中）+ page.Locator（pageCount 控制阶段1/校验）。</summary>
    private static IPage CreatePage(int pageCount, params (string name, string url, int locatorCount)[] childSpecs)
    {
        var mainFrame = Substitute.For<IFrame>();
        var pageMock = Substitute.For<IPage>();
        pageMock.MainFrame.Returns(mainFrame);

        // page.Locator(any) -> pageLocMock；pageLocMock.Locator(any) -> 自身；FrameLocator -> frameLocMock（其 Locator -> pageLocMock）
        var pageLocMock = Substitute.For<ILocator>();
        pageLocMock.CountAsync().Returns(pageCount);
        pageLocMock.Locator(Arg.Any<string>(), Arg.Any<LocatorLocatorOptions?>()).Returns(pageLocMock);
        var frameLocMock = Substitute.For<IFrameLocator>();
        frameLocMock.Locator(Arg.Any<string>(), Arg.Any<FrameLocatorLocatorOptions?>()).Returns(pageLocMock);
        pageLocMock.FrameLocator(Arg.Any<string>()).Returns(frameLocMock);
        pageMock.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions?>()).Returns(pageLocMock);

        var frames = new List<IFrame> { mainFrame };
        foreach (var (name, url, count) in childSpecs)
        {
            var fm = Substitute.For<IFrame>();
            fm.Name.Returns(name);
            fm.Url.Returns(url);
            fm.ParentFrame.Returns(mainFrame);
            var locMock = Substitute.For<ILocator>();
            locMock.CountAsync().Returns(count);
            fm.Locator(Arg.Any<string>(), Arg.Any<FrameLocatorOptions?>()).Returns(locMock);
            frames.Add(fm);
        }
        pageMock.Frames.Returns(frames);
        return pageMock;
    }

    private static RecordingActionExecutor CreateExecutor() => new(Substitute.For<EngineILogger>(), new AppOptions());

    // 形态 A 覆盖语义：AI 传 iframe 链 + 校验通过（page.Locator 链 CountAsync=1）-> 用 AI 链（不再代码反推）。
    // ref 路径 page.Locator CountAsync=1 -> 阶段1 命中（mock 下不真执行 click，走 elementLocator 非空但后续提取/执行 mock 兜底）。
    [Fact]
    public async Task Operate_AiIframeChainValid_UsesAiChain()
    {
        var pageMock = CreatePage(pageCount: 1);  // page.Locator 链 CountAsync=1 -> ValidateIframeChainAsync 通过 + ref 阶段1 命中
        var aiChain = new[] { "//iframe[@id='ai-frame']" };
        var step = new StepNode { Kind = "step", Name = "s", Action = "click", Iframe = aiChain };
        var args = new Dictionary<string, object> { ["ref"] = "e1" };

        var result = await CreateExecutor().ExecuteOperateAsync(pageMock, step, new ScriptV2(), new List<PhaseItem>(), args, CancellationToken.None);

        // 覆盖语义：校验通过 -> 用 AI 链（result.IframeChain = AI 链），不反推
        Assert.Equal(aiChain, result.IframeChain);
    }

    // 形态 A 覆盖校验失败：AI 传链但 ValidateIframeChainAsync CountAsync=0 找不到 -> OperateResult(false)（不再静默用 AI 链）
    [Fact]
    public async Task Operate_AiIframeChainInvalid_ReturnsFailure()
    {
        var pageMock = CreatePage(pageCount: 0);  // page.Locator 链 CountAsync=0 -> 校验失败
        var step = new StepNode { Kind = "step", Name = "s", Action = "click", Iframe = new[] { "//iframe[@id='bad']" } };
        var args = new Dictionary<string, object> { ["ref"] = "e1" };

        var result = await CreateExecutor().ExecuteOperateAsync(pageMock, step, new ScriptV2(), new List<PhaseItem>(), args, CancellationToken.None);

        Assert.False(result.Success);                 // 校验失败 -> 失败反馈（不静默用脆弱链）
        Assert.Contains("找不到", result.Message);    // 断言验值（反馈内容）
    }

    // 主文档不误进：selector(无 ref) + iframe 无元素（frame CountAsync=0 反推不命中）-> iframeChain=null（不退化旧"第一个非主 frame"盲填）
    [Fact]
    public async Task Operate_SelectorNotInIframe_EmptyChain()
    {
        var pageMock = CreatePage(pageCount: 0, ("f", "http://f", 0));  // frame CountAsync=0 -> 反推不命中
        var step = new StepNode { Kind = "step", Name = "s", Action = "click", Selector = "//button[@id='x']" };
        var args = new Dictionary<string, object>();

        var result = await CreateExecutor().ExecuteOperateAsync(pageMock, step, new ScriptV2(), new List<PhaseItem>(), args, CancellationToken.None);

        // T9（a-a-4 L4544）：反推到主文档 -> 显式 []（非 null；D3 约定：[]=主文档不继承 / null=继承 phase）。原 NullChain 断言随 T9 反转更新为 EmptyChain。
        Assert.NotNull(result.IframeChain);
        Assert.Empty(result.IframeChain);
    }

    // ND7（a-a-4）：selector count==0 + acceptAsIs 命中（isAcceptedAsIs 回调 true）-> record-without-execute（跳录制期动作、落盘 selector+onError=ai-fallback-stop 兜底）
    [Fact]
    public async Task Operate_CountZero_AcceptedAsIs_RecordsWithoutExecute()
    {
        var pageMock = CreatePage(pageCount: 0);  // selector count==0（未找到，模拟动态加载未到）
        var step = new StepNode { Kind = "step", Name = "s", Action = "click", Selector = "//button[@id='x']" };
        var args = new Dictionary<string, object>();
        var steps = new List<PhaseItem>();

        var result = await CreateExecutor().ExecuteOperateAsync(pageMock, step, new ScriptV2(), steps, args, CancellationToken.None,
            isAcceptedAsIs: (t, v) => t == FragileType.StepSelector && v == "//button[@id='x']");

        Assert.True(result.Success);                          // record-without-execute -> 落盘成功（非 count==0 失败）
        Assert.Equal("//button[@id='x']", result.Selector);   // selector 原样落盘
        Assert.Single(steps);                                 // 落盘了 step
        Assert.Equal("ai-fallback-stop", ((StepNode)steps[0]).OnError);  // onError 兜底（断言验值）
    }

    // 2c 改动1（方案B改动1）：selector 多匹配（count>1）-> IsMultiMatch=true，提前 return 不 Add（回放 strict mode 多匹配抛错，录制期挡住）。断言验值不只验不崩。
    [Fact]
    public async Task Operate_SelectorMultiMatch_ReturnsIsMultiMatch_NoAdd()
    {
        var pageMock = CreatePage(pageCount: 3);  // selector 匹配 3 个（page.Locator 链 CountAsync=3）
        var step = new StepNode { Kind = "step", Name = "s", Action = "click", Selector = "//button" };
        var args = new Dictionary<string, object>();
        var steps = new List<PhaseItem>();

        var result = await CreateExecutor().ExecuteOperateAsync(pageMock, step, new ScriptV2(), steps, args, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.IsMultiMatch);   // 断言验值（多匹配信号）
        Assert.Equal(0, steps.Count);       // 不 Add（修 V1 缺陷 #2/#3：silent 落盘）-- 静默成功 bug 专项（多匹配本该挡却落盘=回放崩）
    }
}
