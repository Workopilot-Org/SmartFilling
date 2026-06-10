using Microsoft.Playwright;
using NSubstitute;
using SmartFilling.App.Recording;
using SmartFilling.Engine.Engine;
using Xunit;
using EngineILogger = SmartFilling.Engine.Logging.ILogger;

namespace SmartFilling.App.Tests;

/// <summary>
/// 形态 A（2026-07-02）真实浏览器集成测试：tests/testpages/iframe-nested.html 三层嵌套（host→outer→nested）。
/// 验证 FindFrameForTargetAsync 返回 selector 链 string[]（根→叶）+ FrameResolver.ResolveChain 用链定位嵌套元素。
/// 这是录制→回放链路的关键证据（mock 验不出 frame.FrameElementAsync + 5 阶段提取 + FrameLocator 链定位）。
/// 不需 AI（直接调 IframeDetector），DashScope ApiKey 缺失不影响。
/// </summary>
public class IframeNestedBrowserTests
{
    private static string TestPagePath => FindTestPage("iframe-nested.html");

    private static string FindTestPage(string name)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "tests", "testpages", name);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }
        throw new FileNotFoundException($"未找到 tests/testpages/{name}");
    }

    private static async Task<IPage> OpenNestedPageAsync(IPlaywright playwright)
    {
        var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        var fileUrl = "file:///" + TestPagePath.Replace('\\', '/');
        await page.GotoAsync(fileUrl);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        return page;
    }

    private static IframeDetector NewDetector() => new(Substitute.For<EngineILogger>());

    // 形态 A 核心：deep-input 在 nested frame → FindFrameForTargetAsync 返回 [outer-sel, nested-sel] 链
    [Fact]
    public async Task FindFrame_DeepInput_ReturnsNestedChain()
    {
        var playwright = await Playwright.CreateAsync();
        var page = await OpenNestedPageAsync(playwright);
        try
        {
            var (chain, inferred, warning, _, _, _) = await NewDetector().FindFrameForTargetAsync(
                page, new FrameTarget { Selector = "//input[@id='deep-input']" });

            Assert.True(inferred, "应反推命中含 #deep-input 的 nested（最内层）frame");
            Assert.Null(warning);
            Assert.NotNull(chain);
            Assert.True(chain!.Length >= 2, $"嵌套应产 ≥2 层链（outer+nested），实际 {chain.Length}");  // 断言验值（链长度）
        }
        finally
        {
            await page.CloseAsync();
            await page.Context.Browser!.CloseAsync();
        }
    }

    // 形态 A 录制→回放闭环：chain 经 FrameResolver.ResolveChain 定位到 #deep-input（selector 链桥梁闭环关键证据）
    [Fact]
    public async Task FindFrame_NestedChain_ResolveChainLocatesDeepInput()
    {
        var playwright = await Playwright.CreateAsync();
        var page = await OpenNestedPageAsync(playwright);
        try
        {
            var (chain, inferred, warning, _, _, _) = await NewDetector().FindFrameForTargetAsync(
                page, new FrameTarget { Selector = "//input[@id='deep-input']" });

            Assert.True(inferred);
            Assert.NotNull(chain);

            var resolved = FrameResolver.ResolveChain(page, chain);
            var deepInputCount = await resolved.Locator("//input[@id='deep-input']").CountAsync();
            Assert.True(deepInputCount > 0, "ResolveChain 应按 [outer,nested] 链定位到 #deep-input（录制→回放闭环）");
        }
        finally
        {
            await page.CloseAsync();
            await page.Context.Browser!.CloseAsync();
        }
    }

    // deep keywords（"深层消息" 仅在 nested body）→ 命中 nested → 链
    [Fact]
    public async Task FindFrame_DeepKeywords_HitsNestedFrame()
    {
        var playwright = await Playwright.CreateAsync();
        var page = await OpenNestedPageAsync(playwright);
        try
        {
            var (chain, inferred, warning, _, _, _) = await NewDetector().FindFrameForTargetAsync(
                page, new FrameTarget { Keywords = new[] { "深层消息" } });

            Assert.True(inferred, "深层消息仅在 nested frame body，应反推命中 nested");
            Assert.Null(warning);
            Assert.NotNull(chain);
        }
        finally
        {
            await page.CloseAsync();
            await page.Context.Browser!.CloseAsync();
        }
    }

    // 回归：目标在主文档（#host-input）→ 不命中任何 frame → (null, false, null) 真主文档
    [Fact]
    public async Task FindFrame_HostInputInMainDoc_ReturnsFalseNullWarning()
    {
        var playwright = await Playwright.CreateAsync();
        var page = await OpenNestedPageAsync(playwright);
        try
        {
            var (chain, inferred, warning, _, _, _) = await NewDetector().FindFrameForTargetAsync(
                page, new FrameTarget { Selector = "//input[@id='host-input']" });

            Assert.False(inferred);       // 目标在主文档，不退化回旧"第一个非主 frame"盲填
            Assert.Null(chain);
            Assert.Null(warning);         // 真主文档（非提取失败）
        }
        finally
        {
            await page.CloseAsync();
            await page.Context.Browser!.CloseAsync();
        }
    }

    // 中间层：#inner-input 在 outer frame → 反推命中 outer → 单层链 [outer-sel]
    [Fact]
    public async Task FindFrame_InnerInput_HitsOuterFrame()
    {
        var playwright = await Playwright.CreateAsync();
        var page = await OpenNestedPageAsync(playwright);
        try
        {
            var (chain, inferred, warning, _, _, _) = await NewDetector().FindFrameForTargetAsync(
                page, new FrameTarget { Selector = "//input[@id='inner-input']" });

            Assert.True(inferred);
            Assert.Null(warning);
            Assert.NotNull(chain);
            Assert.True(chain!.Length >= 1);
        }
        finally
        {
            await page.CloseAsync();
            await page.Context.Browser!.CloseAsync();
        }
    }
}
