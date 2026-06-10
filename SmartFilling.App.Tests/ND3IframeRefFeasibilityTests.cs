using System.Text.RegularExpressions;
using Microsoft.Playwright;
using NSubstitute;
using SmartFilling.App.Recording;
using Xunit;
using EngineILogger = SmartFilling.Engine.Logging.ILogger;
using Match = System.Text.RegularExpressions.Match;

namespace SmartFilling.App.Tests;

/// <summary>
/// ND3 小测（2026-07-07 三轮核查待决策项）：验证 AriaSnapshotAsync(Mode=Ai) 生成的 accessibility tree
/// 是否给 &lt;iframe&gt; 元素本身分配 [ref=eXX] 标记——判定结论14 iframeRef 机制（用 ref 指认 iframe DOM，
/// ResolveIframeFromRefAsync → page.Locator("aria-ref=X") 定位 iframe）技术可行性。
///
/// 风险：accessibility tree 可能"穿透" iframe 节点（只展开其内部内容，不给 iframe 元素本身分配 ref）→
/// iframeRef 选项 silent 空 locator。本测试输出完整 snapshot 供肉眼分析 iframe 节点是否带 ref。
/// </summary>
public class ND3IframeRefFeasibilityTests
{
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

    private static async Task<(IPage page, IBrowser browser)> OpenAsync(IPlaywright playwright)
    {
        var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        var fileUrl = "file:///" + FindTestPage("iframe-nested.html").Replace('\\', '/');
        await page.GotoAsync(fileUrl);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        return (page, browser);
    }

    [Fact]
    public async Task ND3_Verify_IframeElement_HasAriaRef()
    {
        var playwright = await Playwright.CreateAsync();
        var (page, browser) = await OpenAsync(playwright);
        try
        {
            var extractor = new PageSnapshotExtractor();

            // 主文档 snapshot（含 outer iframe，同源会展开内部）
            var mainSnapshot = await extractor.GetSnapshotAsync(page, null);
            // outer iframe 内 snapshot（含 nested iframe）
            var outerChain = new[] { "//iframe[@id='outer']" };
            var inOuterSnapshot = await extractor.GetSnapshotAsync(page, outerChain);

            // DOM iframe 元素数（宿主 1 个 outer + outer 内 1 个 nested）
            var hostIframeCount = await page.Locator("iframe").CountAsync();

            // 探测：snapshot 里是否出现 "iframe" 字样（iframe 节点是否进 tree）
            bool mainHasIframeWord = mainSnapshot.Contains("iframe", StringComparison.OrdinalIgnoreCase);
            bool outerHasIframeWord = inOuterSnapshot.Contains("iframe", StringComparison.OrdinalIgnoreCase);

            // 探测：所有 [ref=eXX] 标记
            var mainRefs = Regex.Matches(mainSnapshot, @"ref=(e\d+)");
            var outerRefs = Regex.Matches(inOuterSnapshot, @"ref=(e\d+)");
            var mainRefList = string.Join(", ", mainRefs.Cast<Match>().Select(m => m.Value));
            var outerRefList = string.Join(", ", outerRefs.Cast<Match>().Select(m => m.Value));

            // 关键探测：找含 "iframe" 的行 + 附近 ref（iframe 节点带不带 ref）
            var iframeLinesMain = ExtractLinesAround(mainSnapshot, "iframe");
            var iframeLinesOuter = ExtractLinesAround(inOuterSnapshot, "iframe");

            var outPath = Path.Combine(AppContext.BaseDirectory, "nd3-output.txt");
            await File.WriteAllTextAsync(outPath,
                $"=== DOM iframe count (host doc): {hostIframeCount} ===\n" +
                $"=== main snapshot 含 'iframe': {mainHasIframeWord} | ref 数: {mainRefs.Count} [{mainRefList}] ===\n" +
                $"=== in-outer snapshot 含 'iframe': {outerHasIframeWord} | ref 数: {outerRefs.Count} [{outerRefList}] ===\n\n" +
                $"=== 含 'iframe' 的行（main）===\n{iframeLinesMain}\n\n" +
                $"=== 含 'iframe' 的行（in-outer）===\n{iframeLinesOuter}\n\n" +
                $"=== MAIN SNAPSHOT (full) ===\n{mainSnapshot}\n\n" +
                $"=== IN-OUTER SNAPSHOT (full) ===\n{inOuterSnapshot}\n");

            Assert.NotEmpty(mainSnapshot);  // 宽松断言：主要靠输出文件分析
        }
        finally
        {
            await page.CloseAsync();
            await browser.CloseAsync();
        }
    }

    [Fact]
    public async Task ND3_Verify_AriaRef_LocatesIframeElement()
    {
        // 终判：Playwright 1.59 snapshot ref 到底怎么定位？试多种 selector 语法
        // 直接 page.AriaSnapshotAsync（排除 PageSnapshotExtractor 中间层干扰）
        var playwright = await Playwright.CreateAsync();
        var (page, browser) = await OpenAsync(playwright);
        try
        {
            // 直接拿主文档 snapshot（含 [ref=e1..e6]，e4=宿主textbox, e6=outer iframe）
            var snapshot = await page.AriaSnapshotAsync(new() { Mode = AriaSnapshotMode.Ai });

            string Probe(string selector)
            {
                try
                {
                    var c = page.Locator(selector).CountAsync().GetAwaiter().GetResult();
                    if (c == 0) return $"{selector} → count=0";
                    var tag = page.Locator(selector).First.EvaluateAsync<string>("el => el.tagName").GetAwaiter().GetResult();
                    var id = page.Locator(selector).First.GetAttributeAsync("id").GetAwaiter().GetResult();
                    return $"{selector} → count={c}, tag={tag}, id={id}";
                }
                catch (Exception ex) { return $"{selector} → 异常: {ex.Message.Split('\n')[0]}"; }
            }

            // e4 = 宿主 textbox（普通元素）；e6 = outer iframe（iframe 元素）
            var probes = new[]
            {
                // === 普通 element ref（e4）===
                Probe("aria-ref=e4"),
                Probe("internal:ref=e4"),
                // === iframe element ref（e6）===
                Probe("aria-ref=e6"),
                Probe("internal:ref=e6"),
                // === iframe 内 element ref（f1e4 = inner textbox）===
                Probe("aria-ref=f1e4"),
                Probe("internal:ref=f1e4"),
            };

            var outPath = Path.Combine(AppContext.BaseDirectory, "nd3-locate-output.txt");
            await File.WriteAllTextAsync(outPath,
                $"=== Playwright 实际版本：1.59.0（NuGet）===\n" +
                $"=== 主文档 snapshot（直接 page.AriaSnapshotAsync）===\n{snapshot}\n\n" +
                $"=== selector 探测（e4=宿主textbox / e6=outer iframe / f1e4=inner textbox）===\n" +
                string.Join("\n", probes) + "\n");

            Assert.NotEmpty(snapshot);  // 宽松：靠输出分析

            // 硬断言（结论 14 iframeRef 回归基线）：iframe 元素本身能被 aria-ref 定位
            // 注意：ref 映射由本次 AriaSnapshotAsync 建立，须立即用（再次 snapshot 会覆盖，见 session 生命周期注释）
            Assert.Equal(1, await page.Locator("aria-ref=e6").CountAsync());  // outer iframe 唯一
            Assert.Equal("IFRAME", await page.Locator("aria-ref=e6").First.EvaluateAsync<string>("el => el.tagName"));
            Assert.Equal("outer", await page.Locator("aria-ref=e6").First.GetAttributeAsync("id"));
            // 对照：普通元素 + iframe 内元素（证明 aria-ref 机制整体工作）
            Assert.Equal(1, await page.Locator("aria-ref=e4").CountAsync());  // 宿主 textbox
            Assert.Equal("host-input", await page.Locator("aria-ref=e4").First.GetAttributeAsync("id"));
            Assert.Equal(1, await page.Locator("aria-ref=f1e4").CountAsync());  // iframe 内 textbox（跨 frame）
            Assert.Equal("inner-input", await page.Locator("aria-ref=f1e4").First.GetAttributeAsync("id"));
        }
        finally
        {
            await page.CloseAsync();
            await browser.CloseAsync();
        }
    }

    private static string ExtractLinesAround(string text, string keyword)
    {
        var lines = text.Split('\n');
        var hits = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                hits.Add($"  L{i}: {lines[i].TrimEnd()}");
            }
        }
        return hits.Count == 0 ? "(无含 iframe 的行)" : string.Join("\n", hits);
    }

    [Fact]
    public async Task ND3_Verify_FindFrameForTargetAsync_IframeRef()
    {
        // 验证 FindFrameForTargetAsync 对 iframeRef 的处理（结论14 复用前提——审查发现的实施验证点）
        // 关键：FindFrameForTargetAsync L56 跳过 MainFrame——iframe 元素在父 frame DOM：
        //  - 顶层 iframe（outer, ref=e6, 在 MainFrame）→ 跳过 MainFrame 可能找不到
        //  - 嵌套 iframe（nested, ref=f1e5, 在 outer frame）→ outer frame 命中
        var playwright = await Playwright.CreateAsync();
        var (page, browser) = await OpenAsync(playwright);
        try
        {
            _ = await page.AriaSnapshotAsync(new() { Mode = AriaSnapshotMode.Ai });  // 建立 ref 映射
            var detector = new IframeDetector(Substitute.For<EngineILogger>());

            var (chain1, inf1, warn1, _, _, _) = await detector.FindFrameForTargetAsync(page, new FrameTarget { Ref = "e6" });
            var (chain2, inf2, warn2, _, _, _) = await detector.FindFrameForTargetAsync(page, new FrameTarget { Ref = "f1e5" });

            var outPath = Path.Combine(AppContext.BaseDirectory, "nd3-findframe-ref.txt");
            await File.WriteAllTextAsync(outPath,
                $"=== ref=e6（outer iframe, 在 MainFrame DOM, FindFrameForTargetAsync L56 跳过 MainFrame）===\n" +
                $"  inferred: {inf1}, warning: {warn1 ?? "(null)"}, chain: {(chain1 != null ? string.Join(" > ", chain1) : "(null)")}\n\n" +
                $"=== ref=f1e5（nested iframe, 在 outer frame DOM）===\n" +
                $"  inferred: {inf2}, warning: {warn2 ?? "(null)"}, chain: {(chain2 != null ? string.Join(" > ", chain2) : "(null)")}\n");

            Assert.NotEmpty(outPath);  // 宽松，靠输出分析
        }
        finally
        {
            await page.CloseAsync();
            await browser.CloseAsync();
        }
    }
}
