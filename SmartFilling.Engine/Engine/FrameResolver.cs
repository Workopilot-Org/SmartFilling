using Microsoft.Playwright;
using SmartFilling.Engine.Models;

namespace SmartFilling.Engine.Engine;

/// <summary>
/// 形态 A（2026-07-02）：iframe 定位改为 selector 链固化，零 Id 反查/零注册表。
/// step.iframe / phase.iframe / detect.iframe 均为 string[] selector 链（根→叶）。
/// 回放按数组逐层 FrameLocator(sel).Locator("body")；chain 空→主文档 body。
/// </summary>
public static class FrameResolver
{
    /// <summary>
    /// 按 selector 链解析 FrameLocator（录制端用：chain 为刚提取的原始 selector，无 {{}}，不替换）。
    /// chain null/空→主文档 body；否则 page.Locator("body") 逐层 .FrameLocator(sel).Locator("body")。
    /// </summary>
    public static ILocator ResolveChain(IPage page, string[]? chain)
    {
        if (chain == null || chain.Length == 0)
            return page.Locator("body");

        ILocator locator = page.Locator("body");
        foreach (var sel in chain)
        {
            if (!string.IsNullOrEmpty(sel))
                locator = locator.FrameLocator(sel).Locator("body");
        }
        return locator;
    }

    /// <summary>
    /// 按 selector 链解析 FrameLocator（回放端用：每层先 ReplaceVars 替换 {{rowIndex}}/{{字段名}}/{{变量}}，再 FrameLocator）。
    /// 替换集中在此（调用方传 ctx.ScopeChain/ctx.Vars），与 step.Selector 由调用方替换语义一致（仅位置不同）。
    /// 无 {{}} 时 ReplaceVars 透传零副作用。
    /// </summary>
    public static ILocator ResolveChain(
        IPage page,
        string[]? chain,
        List<Dictionary<string, object>> scopeChain,
        Dictionary<string, object> vars)
    {
        if (chain == null || chain.Length == 0)
            return page.Locator("body");

        ILocator locator = page.Locator("body");
        foreach (var sel in chain)
        {
            if (string.IsNullOrEmpty(sel)) continue;
            var resolved = VariableHelper.ReplaceVars(sel, scopeChain, vars);
            locator = locator.FrameLocator(resolved).Locator("body");
        }
        return locator;
    }

    /// <summary>
    /// R2-D（a-a-4 批次 E）：逐层校验 iframe 链完整性（某层 count==0 抛 IframeChainBrokenException）。selector 类 detect 评估前调。
    /// 不改既有 ResolveChain 同步签名（避免波及所有调用点）；逐层 ReplaceVars + FrameLocator(body).CountAsync 校验。
    /// </summary>
    public static async Task ValidateChainAsync(IPage page, string[]? chain,
        List<Dictionary<string, object>> scopeChain, Dictionary<string, object> vars)
    {
        if (chain == null || chain.Length == 0) return;  // 主文档
        var locator = page.Locator("body");
        for (int i = 0; i < chain.Length; i++)
        {
            if (string.IsNullOrEmpty(chain[i])) continue;
            var resolved = VariableHelper.ReplaceVars(chain[i], scopeChain, vars);
            var count = await locator.FrameLocator(resolved).Locator("body").CountAsync();
            if (count == 0)
                throw new IframeChainBrokenException($"iframe 链第 {i + 1} 层「{resolved}」未找到（链断裂，count==0）");
            locator = locator.FrameLocator(resolved).Locator("body");
        }
    }
}
