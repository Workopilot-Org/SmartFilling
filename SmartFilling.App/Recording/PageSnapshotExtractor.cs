using System.Text;
using Microsoft.Playwright;
using SmartFilling.Engine.Engine;

namespace SmartFilling.App.Recording;

/// <summary>
/// 页面 Accessibility Tree 快照提取
/// </summary>
public class PageSnapshotExtractor
{
    /// <summary>
    /// 获取主页面或指定 iframe 的 Accessibility Tree 快照（形态 A：iframeChain 为 selector 链，录制端用不替换 overload——chain 无 {{}}）。
    /// </summary>
    public async Task<string> GetSnapshotAsync(IPage page, string[]? iframeChain = null)
    {
        try
        {
            // 多 tab 场景：在快照前输出标签页列表，让 AI 感知 tab 结构（配合 switchTab/closeTab，J.5.3）
            var tabsSection = await BuildTabsSectionAsync(page);

            // 复用 FrameResolver.ResolveChain（支持嵌套 iframe selector 链）
            var target = FrameResolver.ResolveChain(page, iframeChain);

            var snapshot = await target.AriaSnapshotAsync(new() { Mode = AriaSnapshotMode.Ai });
            return tabsSection + (snapshot ?? "空页面");
        }
        catch (Exception ex)
        {
            return $"快照获取失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 构建标签页列表段（多 tab 时输出，单 tab 省略）。标记当前 tab，供 AI 配合 switchTab/closeTab 决策。
    /// </summary>
    private static async Task<string> BuildTabsSectionAsync(IPage page)
    {
        var pages = page.Context.Pages;
        if (pages.Count <= 1) return "";

        int currentIndex = -1;
        for (int i = 0; i < pages.Count; i++)
            if (ReferenceEquals(pages[i], page)) { currentIndex = i; break; }

        var sb = new StringBuilder();
        sb.AppendLine("[标签页]");
        for (int i = 0; i < pages.Count; i++)
        {
            try
            {
                var p = pages[i];
                var mark = i == currentIndex ? " ←当前" : "";
                sb.AppendLine($"  #{i}: {await p.TitleAsync()} - {p.Url}{mark}");
            }
            catch { /* 单个 tab 信息获取失败不影响整体快照 */ }
        }
        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>
    /// 获取元素级别的截图
    /// </summary>
    public async Task<byte[]> ScreenshotElementAsync(IPage page, string? selector, int quality = 70)
    {
        if (quality >= 100)
        {
            // PNG 不压缩
            if (!string.IsNullOrEmpty(selector))
                return await page.Locator(selector).ScreenshotAsync();
            return await page.ScreenshotAsync();
        }
        else
        {
            // JPEG 压缩
            if (!string.IsNullOrEmpty(selector))
                return await page.Locator(selector).ScreenshotAsync(new() { Type = ScreenshotType.Jpeg, Quality = quality });
            return await page.ScreenshotAsync(new() { Type = ScreenshotType.Jpeg, Quality = quality });
        }
    }
}
