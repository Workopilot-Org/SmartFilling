using Jint;
using Microsoft.Playwright;
using SmartFilling.Engine.Logging;
using SmartFilling.Engine.Models;

namespace SmartFilling.Engine.Engine;

public class DetectEvaluator
{
    private readonly ILogger _logger;

    public DetectEvaluator(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 评估 detect 条件。detect 字段中的 selector/value/keyword/keywords 会做 {{}} 替换。
    /// </summary>
    public async Task<bool> EvaluateAsync(
        DetectCondition detect,
        IPage activePage,
        ScriptV2 script,
        string[]? phaseIframe,
        Dictionary<string, object> vars,
        List<Dictionary<string, object>> scopeChain)
    {
        // 组合条件
        if (detect.All != null)
        {
            foreach (var c in detect.All)
                if (!await EvaluateAsync(c, activePage, script, phaseIframe, vars, scopeChain)) return false;
            return true;
        }
        if (detect.Any != null)
        {
            foreach (var c in detect.Any)
                if (await EvaluateAsync(c, activePage, script, phaseIframe, vars, scopeChain)) return true;
            return false;
        }
        if (detect.Not != null)
            return !await EvaluateAsync(detect.Not, activePage, script, phaseIframe, vars, scopeChain);

        var type = detect.Type;
        if (type == null) return false;

        var frame = FrameResolver.ResolveChain(activePage, IframeChainMerger.Resolve(detect.Iframe, phaseIframe), scopeChain, vars);

        // R2-D（a-a-4 批次 E）：selector 类 detect（检查元素状态，链断=技术故障非业务 false）评估前校验链，断裂抛 IframeChainBrokenException 穿透触发 onError（根治 detect③ dead write）
        var isSelectorBased = type is "selector_visible" or "selector_exists" or "selector_gone" or "selector_enabled"
            or "selector_value" or "selector_text" or "selector_checked" or "selector_selected" or "selector_count" or "new_row_appears";
        if (isSelectorBased && (detect.Iframe is { Length: > 0 } || phaseIframe is { Length: > 0 }))
            await FrameResolver.ValidateChainAsync(activePage, IframeChainMerger.Resolve(detect.Iframe, phaseIframe), scopeChain, vars);

        // detect 字段统一 {{}} 替换
        var dSelector = detect.Selector != null
            ? VariableHelper.ReplaceVars(detect.Selector, scopeChain, vars)
            : null;
        var dValue = detect.Value != null
            ? VariableHelper.ReplaceVars(detect.Value, scopeChain, vars)
            : null;
        var dKeywords = detect.Keywords?.Select(k => VariableHelper.ReplaceVars(k, scopeChain, vars)).ToList();
        // S5 F.10.2：page_exists 的 urlContains 也做 {{}} 替换（原仅 url_contains.value 替换，同语义不一致）
        var dUrlContains = detect.UrlContains != null
            ? VariableHelper.ReplaceVars(detect.UrlContains, scopeChain, vars)
            : null;

        try
        {
            return type switch
            {
                "url_changed" => activePage.Url != (vars.TryGetValue("_lastUrl", out var last) ? last?.ToString() : ""),
                "url_contains" => activePage.Url.Contains(dValue ?? ""),
                "selector_visible" => await frame.Locator(dSelector!).IsVisibleAsync(),
                "selector_exists" => await frame.Locator(dSelector!).CountAsync() > 0,
                "selector_gone" => await frame.Locator(dSelector!).CountAsync() == 0,
                "selector_enabled" => await frame.Locator(dSelector!).IsEnabledAsync(),
                "iframe_exists" => await EvaluateIframeExistsAsync(frame, dSelector),
                "page_exists" => activePage.Context.Pages.Any(p => dUrlContains == null || p.Url.Contains(dUrlContains)),
                "dialog_contains" => EvaluateDialogContains(dKeywords, vars),
                "page_contains" => await EvaluatePageContainsAsync(frame, dKeywords),
                "document_ready" => await frame.EvaluateAsync<bool>("document.readyState === 'complete'"),  // #1：评估目标 frame 的 readyState === 'complete'（frame 由 FrameResolver 按 detect.Iframe 解析：主文档 body 或 iframe 链 body）
                "selector_value" => await frame.Locator(dSelector!).InputValueAsync() == dValue,
                "selector_text" => await EvaluateSelectorTextAsync(frame, dSelector, dKeywords),
                "selector_checked" => await frame.Locator(dSelector!).IsCheckedAsync(),
                "selector_selected" => await EvaluateSelectorSelectedAsync(frame, dSelector, dValue),
                "selector_count" => await EvaluateSelectorCount(frame, dSelector, detect.Count),
                "new_row_appears" => await EvaluateNewRowAppearsAsync(frame, dSelector, vars),
                "js" => EvaluateJsCondition(detect.Check!, scopeChain, vars),
                "data_exists" => VariableHelper.FieldExists(scopeChain, detect.Field),
                "always" => true,
                _ => throw new NotSupportedException($"未知的 detect 类型: {type}")
            };
        }
        catch (Exception ex) when (ex is not IframeChainBrokenException)  // R2-D：IframeChainBrokenException 穿透 → check/condition/wait onError 触发（不吞成 false）
        {
            _logger.LogWarning($"detect '{type}' 评估失败: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> EvaluateIframeExistsAsync(ILocator contextLocator, string? dSelector)
    {
        // 形态 A：iframe_exists 用 selector（必填，要检查的 iframe 元素）在父上下文链（contextLocator=ResolveChain(detect.Iframe)）内查找。
        // 删 FrameId 分支（加载期 ValidateDetectParams 校验 selector 必填，D4）。
        if (dSelector == null) return false;
        return await contextLocator.Locator(dSelector).CountAsync() > 0;
    }

    private bool EvaluateDialogContains(List<string>? keywords, Dictionary<string, object> vars)
    {
        if (keywords == null) return false;
        if (!vars.TryGetValue("_lastDialogMessage", out var msg) || msg == null) return false;
        var msgStr = msg.ToString() ?? "";
        return keywords.Any(k => msgStr.Contains(k));
    }

    private async Task<bool> EvaluatePageContainsAsync(ILocator frame, List<string>? keywords)
    {
        if (keywords == null) return false;
        try
        {
            var text = await frame.EvaluateAsync<string>("document.body.innerText");
            return keywords.Any(k => text.Contains(k));
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> EvaluateSelectorTextAsync(ILocator frame, string? selector, List<string>? keywords)
    {
        if (selector == null || keywords == null) { _logger.LogWarning("detect selector_text 缺少 selector 或 keywords，返回 false"); return false; }  // R2-5：缺参告警（原静默 false，与 selector_count 一致）
        try
        {
            var text = await frame.Locator(selector).InnerTextAsync();
            return keywords.Any(k => text.Contains(k));
        }
        catch { return false; }
    }

    /// <summary>G.3.2：selector_count 未指定 count 时告警+返回 false（原 Count??0 致"忘填=元素数为0才true"反直觉）</summary>
    private async Task<bool> EvaluateSelectorCount(ILocator frame, string? selector, int? count)
    {
        if (count == null) { _logger.LogWarning("detect selector_count 未指定 count，返回 false"); return false; }
        return await frame.Locator(selector!).CountAsync() == count.Value;
    }

    private async Task<bool> EvaluateSelectorSelectedAsync(ILocator frame, string? selector, string? value)
    {
        if (selector == null || value == null) { _logger.LogWarning("detect selector_selected 缺少 selector 或 value，返回 false"); return false; }  // R2-5
        try
        {
            var select = frame.Locator(selector);
            var selectedValues = await select.EvaluateAsync<string[]>("el => Array.from(el.selectedOptions).map(o => o.value)");
            return selectedValues.Contains(value);
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> EvaluateNewRowAppearsAsync(ILocator frame, string? selector, Dictionary<string, object> vars)
    {
        // DC9 S3（a-a-4）：selector=null 显式失败（schema/校验层已挡空 selector，到不了这里；ref 提取后空/手编遗漏时兜底）。
        // 不依赖 Playwright Locator(null) 抛错——否则 silent 兜底基线=0→currentCount>0 永远 true→loop 死循环。
        if (string.IsNullOrEmpty(selector)) { _logger.LogWarning("detect new_row_appears 缺少 selector（数行基线），返回 false"); return false; }
        try
        {
            var currentCount = await frame.Locator(selector).CountAsync();
            // DC13（a-a-4）：读 _lastRowCountStack 栈顶（当前 loop 基线）。两端协同——ScriptEngine 每 row Push、出口 Pop；此处 Peek 取当前 loop 基线，嵌套 loop 隔离。
            if (vars.TryGetValue("_lastRowCountStack", out var stkObj) && stkObj is Stack<int> stk && stk.Count > 0)
                return currentCount > stk.Peek();
            _logger.LogWarning("detect new_row_appears 基线栈 _lastRowCountStack 未设（非 loop phase？DC14 schema 应挡），返回 false");
            return false;
        }
        catch
        {
            return false;
        }
    }

    private bool EvaluateJsCondition(string expression, List<Dictionary<string, object>> scopeChain, Dictionary<string, object> vars)
    {
        try
        {
            var engine = new Jint.Engine();

            // S5 F.10.4：合并作用域栈（内层覆盖外层），让 loop 内 js detect 能访问当前行 rowData
            // （原仅取 [^1] 外层 fillData，loop 内拿不到当前行 rowData = Y4 根因）
            var fillData = new Dictionary<string, object>();
            for (int i = scopeChain.Count - 1; i >= 0; i--)
                foreach (var kv in scopeChain[i])
                    fillData[kv.Key] = kv.Value;  // 从外层[^1]到内层[0]，内层后填覆盖
            engine.SetValue("fillData", fillData);
            engine.SetValue("vars", vars);

            return engine.Evaluate(expression).AsBoolean();
        }
        catch
        {
            return false;
        }
    }
}
