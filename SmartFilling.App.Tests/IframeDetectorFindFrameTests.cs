using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SmartFilling.App.Recording;
using SmartFilling.Engine.Logging;
using Xunit;
using EngineILogger = SmartFilling.Engine.Logging.ILogger;

namespace SmartFilling.App.Tests;

/// <summary>
/// 形态 A（2026-07-02，放法 X 块A R2-细化 2026-07-14 +ctx）：IframeDetector.FindFrameForTargetAsync 单测--返回 (string[]? chain, bool inferred, string? warning, bool isFragileLayer, IReadOnlyList<MultiFrameHit>? multiFrames, string? ctx)。
/// 🟡 mock 边界声明：mock 只验"遍历/未命中/warning 通道"逻辑（不依赖 frame.FrameElementAsync DOM 提取）；
/// 命中后的 selector 链提取（BuildFrameSelectorAsync 5 阶段 + frame.FrameElementAsync）依赖真实 DOM，由 IframeNestedBrowserTests 真实浏览器验证（不可替代）。
/// </summary>
public class IframeDetectorFindFrameTests
{
    private static IPage CreatePage(out IFrame mainFrame)
    {
        var mainFrameMock = Substitute.For<IFrame>();
        mainFrame = mainFrameMock;
        var mockPage = Substitute.For<IPage>();
        mockPage.MainFrame.Returns(mainFrame);
        mockPage.Frames.Returns(new List<IFrame> { mainFrame });
        return mockPage;
    }

    private static void SetFrames(IPage page, IFrame mainFrame, params IFrame[] children)
    {
        var list = new List<IFrame> { mainFrame };
        list.AddRange(children);
        page.Frames.Returns(list);
    }

    /// <summary>创建 mock IFrame：Locator(any).CountAsync()=locatorCount，EvaluateAsync("body.innerText")=bodyText。</summary>
    private static IFrame CreateFrame(string name, string url, IFrame? parent, int locatorCount, string? bodyText = null)
    {
        var mock = Substitute.For<IFrame>();
        mock.Name.Returns(name);
        mock.Url.Returns(url);
        mock.ParentFrame.Returns(parent);
        var locMock = Substitute.For<ILocator>();
        locMock.CountAsync().Returns(locatorCount);
        mock.Locator(Arg.Any<string>(), Arg.Any<FrameLocatorOptions?>()).Returns(locMock);
        if (bodyText != null)
            mock.EvaluateAsync<string>(Arg.Any<string>(), Arg.Any<object?>()).Returns(bodyText);
        return mock;
    }

    private static IframeDetector NewDetector() => new(Substitute.For<EngineILogger>());

    // ①目标在主文档（所有子 frame 都不命中）-> (null, false, null) 真主文档
    [Fact]
    public async Task Find_TargetInMainDoc_ReturnsFalseNullWarning()
    {
        var pageMock = CreatePage(out var mainFrame);
        var frameA = CreateFrame("a", "http://a", mainFrame, locatorCount: 0);  // 目标不在此 frame
        SetFrames(pageMock, mainFrame, frameA);

        var (chain, inferred, warning, _, _, _) = await NewDetector().FindFrameForTargetAsync(
            pageMock, new FrameTarget { Selector = "//input[@id='x']" });

        Assert.False(inferred);       // 诚实留空=主文档，不退化回旧启发式
        Assert.Null(chain);
        Assert.Null(warning);         // 真主文档（非提取失败），无 warning
    }

    // ②无 iframe（只 MainFrame）-> (null, false, null)
    [Fact]
    public async Task Find_NoIframe_ReturnsFalse()
    {
        var pageMock = CreatePage(out _);

        var (chain, inferred, warning, _, _, _) = await NewDetector().FindFrameForTargetAsync(
            pageMock, new FrameTarget { Selector = "//input" });

        Assert.False(inferred);
        Assert.Null(chain);
        Assert.Null(warning);
    }

    // ③含 {{}} selector -> ContainsVariable，不遍历，直接 (null, false, null)
    [Fact]
    public async Task Find_VariableSelector_DoesNotTraverse()
    {
        var pageMock = CreatePage(out var mainFrame);
        var frameA = CreateFrame("a", "http://a", mainFrame, locatorCount: 1);
        SetFrames(pageMock, mainFrame, frameA);

        var (chain, inferred, warning, _, _, _) = await NewDetector().FindFrameForTargetAsync(
            pageMock, new FrameTarget { Selector = "//input[@id='{{fieldName}}']" });

        Assert.False(inferred);
        Assert.Null(chain);
        Assert.Null(warning);
    }

    // ④无来源（Ref/Selector/Keywords 都空）-> (null, false, null)
    [Fact]
    public async Task Find_NoSource_ReturnsFalse()
    {
        var pageMock = CreatePage(out var mainFrame);
        var frameA = CreateFrame("a", "http://a", mainFrame, locatorCount: 1);
        SetFrames(pageMock, mainFrame, frameA);

        var (chain, inferred, warning, _, _, _) = await NewDetector().FindFrameForTargetAsync(
            pageMock, new FrameTarget { });

        Assert.False(inferred);
        Assert.Null(chain);
        Assert.Null(warning);
    }

    // ⑤target==null -> (null, false, null)
    [Fact]
    public async Task Find_NullTarget_ReturnsFalse()
    {
        var pageMock = CreatePage(out var mainFrame);
        var frameA = CreateFrame("a", "http://a", mainFrame, locatorCount: 1);
        SetFrames(pageMock, mainFrame, frameA);

        var (chain, inferred, warning, _, _, _) = await NewDetector().FindFrameForTargetAsync(
            pageMock, null);

        Assert.False(inferred);
        Assert.Null(chain);
        Assert.Null(warning);
    }

    // ⑥N1/D1：命中 frame 但 FrameElementAsync 返回 null（frame detached/未加载）-> (null, false, warning) 提取失败
    // warning 非空区分"提取失败"vs"真主文档"（都 inferred=false），调用方据 warning 强求助（Z1）。
    [Fact]
    public async Task Find_FrameElementNull_ReturnsWarning()
    {
        var pageMock = CreatePage(out var mainFrame);
        var frameA = CreateFrame("detached", "http://a", mainFrame, locatorCount: 1);  // IsTargetInFrameAsync 命中
        frameA.FrameElementAsync().Returns((IElementHandle?)null);  // FrameElement==null
        SetFrames(pageMock, mainFrame, frameA);

        var (chain, inferred, warning, _, _, _) = await NewDetector().FindFrameForTargetAsync(
            pageMock, new FrameTarget { Ref = "e1" });

        Assert.False(inferred);       // 提取失败（非真主文档）
        Assert.Null(chain);
        Assert.NotNull(warning);      // D1 warning 通道
        Assert.Contains("FrameElement", warning!);  // 区分提取失败 vs 真主文档（断言验值不只验状态）
    }

    // ⑦单 frame IsTargetInFrameAsync 抛异常 -> 视为不命中继续遍历；都不命中 -> 真主文档 (null,false,null)
    [Fact]
    public async Task Find_FrameEvaluateThrows_SkipsAndContinues()
    {
        var pageMock = CreatePage(out var mainFrame);
        var frameA = Substitute.For<IFrame>();
        frameA.Name.Returns("bad");
        frameA.Url.Returns("http://bad");
        frameA.ParentFrame.Returns(mainFrame);
        frameA.EvaluateAsync<string>(Arg.Any<string>(), Arg.Any<object?>())
            .Throws(new Exception("frame 访问失败"));  // keywords 评估抛异常
        var locMockA = Substitute.For<ILocator>();
        locMockA.CountAsync().Returns(0);
        frameA.Locator(Arg.Any<string>(), Arg.Any<FrameLocatorOptions?>()).Returns(locMockA);
        SetFrames(pageMock, mainFrame, frameA);

        var (chain, inferred, warning, _, _, _) = await NewDetector().FindFrameForTargetAsync(
            pageMock, new FrameTarget { Keywords = new[] { "目标文本" } });

        // frameA 抛异常视为不命中 -> 无其他 frame 命中 -> 真主文档
        Assert.False(inferred);
        Assert.Null(chain);
        Assert.Null(warning);  // IsTarget 异常不填 warning（continue 遍历），只有 ExtractFrameChainAsync 失败才填
    }

    // ND8（a-a-4）：2 frame 都含 selector（多 frame 命中）-> multiFrames 列表（>=2）+ warning，不再 silent 选第一个
    [Fact]
    public async Task Find_TwoFramesHit_ReturnsMultiFramesList()
    {
        var pageMock = CreatePage(out var mainFrame);
        var frameA = CreateFrame("frameA", "http://a", mainFrame, locatorCount: 1, "保存成功");
        var frameB = CreateFrame("frameB", "http://b", mainFrame, locatorCount: 1, "操作成功");
        // FrameElementAsync 返 mock handle 让 ExtractFrameChainAsync 走通（BuildFrameSelectorAsync 退化 //iframe[@name]）
        frameA.FrameElementAsync().Returns(Substitute.For<IElementHandle>());
        frameB.FrameElementAsync().Returns(Substitute.For<IElementHandle>());
        SetFrames(pageMock, mainFrame, frameA, frameB);

        var (chain, inferred, warning, _, multiFrames, _) = await NewDetector().FindFrameForTargetAsync(
            pageMock, new FrameTarget { Selector = "//button" });

        Assert.True(inferred);          // 第一命中 chain 作默认（A 兜底）
        Assert.NotNull(chain);
        Assert.NotNull(warning);        // 多 frame warning
        Assert.Contains("2 个 iframe", warning!);  // 断言验值不只验状态
        Assert.NotNull(multiFrames);
        Assert.True(multiFrames!.Count >= 2);
        Assert.Contains(multiFrames, m => m.Info.Name == "frameA");
        Assert.Contains(multiFrames, m => m.Info.Name == "frameB");
    }

    // 放法 X 块A R2-细化（2026-07-14）：脆弱层（位置选择器层）-> ctx 全量经出参给 AI（选 (l) 喂），warning 内 ctxSuffix 给用户截前 50 字。
    [Fact]
    public async Task Find_FragileLayer_ReturnsCtx()
    {
        var pageMock = CreatePage(out var mainFrame);
        // frame 无 name 无 url -> FallbackSelector 产 (//iframe)[1]（位置选择器，AssessFragility=8 脆弱层）
        var frameA = Substitute.For<IFrame>();
        frameA.Name.Returns("");
        frameA.Url.Returns("");
        frameA.ParentFrame.Returns(mainFrame);
        var locMock = Substitute.For<ILocator>();
        locMock.CountAsync().Returns(1);  // IsTargetInFrameAsync 命中
        frameA.Locator(Arg.Any<string>(), Arg.Any<FrameLocatorOptions?>()).Returns(locMock);
        // FrameElementAsync 返 mock handle，EvaluateAsync<string> 返 ctx（CaptureFrameContextAsync 抓全量 outerHTML）
        var handleMock = Substitute.For<IElementHandle>();
        handleMock.EvaluateAsync<string>(Arg.Any<string>(), Arg.Any<object?>()).Returns("id= name= src= html=<iframe src=\"x\"></iframe>");
        frameA.FrameElementAsync().Returns(handleMock);
        SetFrames(pageMock, mainFrame, frameA);

        var (chain, inferred, warning, isFragileLayer, _, ctx) = await NewDetector().FindFrameForTargetAsync(
            pageMock, new FrameTarget { Selector = "//button" });

        Assert.True(inferred);
        Assert.True(isFragileLayer);  // 脆弱层（位置选择器 (//iframe)[1]，AssessFragility=8）
        Assert.NotNull(warning);
        Assert.Contains("iframe 元素属性", warning);  // ctxSuffix 给用户（warning 内，截前 50 字）
        Assert.NotNull(ctx);  // R2-细化：ctx 全量经出参给 AI（选 (l) 喂）
        Assert.Equal("id= name= src= html=<iframe src=\"x\"></iframe>", ctx);  // 验值：全量 ctx（非截断）
    }
}
