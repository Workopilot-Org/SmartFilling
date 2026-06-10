using System.Text.Json;
using Microsoft.Playwright;
using SmartFilling.Engine.Models;
using EngineILogger = SmartFilling.Engine.Logging.ILogger;

namespace SmartFilling.App.Recording;

/// <summary>
/// iframe 反推目标描述：按 ref/selector/keywords 在各 frame 内反推目标元素所在 frame。
/// 替代旧启发式"第一个非主 frame"——后者与具体目标无关，无元素锚点的 detect（page_contains/url_changed）
/// 会被静默填成第一个非主 frame，回放去错 frame 读 body → 永不命中。
/// </summary>
public sealed record FrameTarget
{
    public string? Ref;        // aria-ref=eXX（Playwright 全局 ref，跨 frame 唯一）
    public string? Selector;   // 已提取 XPath/CSS（不含 {{}}）
    public string[]? Keywords; // page_contains 目标文本
    /// <summary>selector 含 {{变量}} → 无法在 frame 内静态定位，不反推</summary>
    public bool ContainsVariable => Selector?.Contains("{{") ?? false;
}

/// <summary>
/// 形态 A（2026-07-02）：iframe 定位改为 selector 链固化，零 Id 反查/零注册表/无跨调用状态。
/// 检测到目标元素在 frame F → 沿 F.ParentFrame 收集每层 IFrame → 每层用 frame.FrameElement 直取 &lt;iframe&gt; DOM
/// + 复用 BuildFrameSelectorAsync 5 阶段算 selector → Reverse 成根→叶 string[]。纯函数，无字典/无 Id。
/// </summary>
public class IframeDetector
{
    private readonly EngineILogger _logger;

    public IframeDetector(EngineILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 按目标（ref/selector/keywords）反推目标元素所在的 frame，返回 selector 链（根→叶）。
    /// 遍历 page.Frames（扁平含所有层级，天然覆盖嵌套），跳过 MainFrame，命中第一个含目标的 frame 即停。
    /// 返回 (chain, inferred, warning, isFragileLayer)：
    /// - 命中 frame 且 ExtractFrameChainAsync 成功 → (chain, true, null, false)
    /// - 命中但链含脆弱层（位置选择器层/GUID id/动态锚点，结论13 C1 每层 AssessFragility>=8） → (chain, true, warning含元素属性, true)（DC3 结构化 isFragileLayer，防 caller 字符串匹配）
    /// - 都不命中（真主文档）→ (null, false, null, false)
    /// - 命中 frame 但提取失败（frame detached/未加载/selector 退化，D1/9.6）→ (null, false, warning, false)
    /// 调用方据 warning 区分两种 inferred=false（强求助 vs 真主文档）；据 isFragileLayer 触发含 (f)/(i) 求助。
    /// </summary>
    public async Task<(string[]? chain, bool inferred, string? warning, bool isFragileLayer, IReadOnlyList<MultiFrameHit>? multiFrames, string? ctx)> FindFrameForTargetAsync(
        IPage page, FrameTarget? target)
    {
        // 无目标 / selector 含变量（无法静态定位）/ 三来源皆空 → 不反推，真主文档
        if (target == null || target.ContainsVariable) return (null, false, null, false, null, null);
        if (string.IsNullOrEmpty(target.Ref) && string.IsNullOrEmpty(target.Selector)
            && !(target.Keywords?.Any() ?? false))
            return (null, false, null, false, null, null);

        // ND8（a-a-4，选 A+B·2026-07-08）：遍历全部非主 frame 收集命中（不再命中第一个即 return——原 silent 选 page.Frames 顺序第一个）。
        // >=2 命中 → 多 frame warning + multiFrames 列表（caller 据此 (j) 指认编号 / (i) ref）；单命中保持原 fragile/正常逻辑。
        var hits = new List<(IFrame Frame, string[] Chain, IElementHandle[] Els)>();
        bool extractionFailed = false;  // 任一命中 frame 提取失败（保留原"提取失败 warning"语义，不被多 frame 收集吞）
        string? extractionFailMsg = null;  // 保留失败原因（D1 warning 通道含原因，caller/测试可区分提取失败 vs 真主文档）
        foreach (var frame in page.Frames)
        {
            if (frame == page.MainFrame) continue;

            // IsTargetInFrameAsync 单 frame 异常视为不命中，继续遍历下一个 frame（不因单个 frame 报错而整体失败）
            bool inFrame;
            try { inFrame = await IsTargetInFrameAsync(frame, target); }
            catch (Exception ex) { _logger.LogWarning(ex, $"IsTargetInFrameAsync 单 frame 异常，跳过该 frame"); continue; }
            if (!inFrame) continue;

            // 命中：ExtractFrameChainAsync 单独 try/catch（D1 warning 通道——提取失败记 extractionFailed 跳过该 hit 继续收集；全失败时返提取失败 warning）
            try
            {
                var (chain, els) = await ExtractFrameChainAsync(frame, page);
                hits.Add((frame, chain, els));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"iframe selector 链提取失败（frame url={frame.Url}）");
                extractionFailed = true;
                extractionFailMsg = ex.Message;
            }
        }

        if (hits.Count == 0)
        {
            // 无成功提取的命中：若有提取失败 → 返提取失败 warning（保留 D1 通道 + 原因，原首个命中即返语义）；否则真主文档
            if (extractionFailed) return (null, false, $"⚠️ iframe 提取失败（{extractionFailMsg}），建议 request_help", false, null, null);
            return (null, false, null, false, null, null);  // 遍历完都不命中 → 真主文档
        }

        var first = hits[0];
        // 2b 代码风险门禁：第一命中链中某层若退化成位置选择器 (//iframe)[1]（高脆弱，name/src 都没有）→ isFragileLayer=true。
        // 调用方（operate 阶段0 / save_step L1 / detect③ / fallback场景A）据 isFragileLayer 强求助（含 f/i，BuildHelpQuestion）。
        var fragileIdx = Array.FindIndex(first.Chain, s => SelectorExtractor.AssessFragility(s) >= 8);  // 结论13 C1：每层 AssessFragility（含 GUID id/动态锚点/位置选择器层），>=8 判脆弱（替代前缀匹配，覆盖 GUID）
        bool firstIsFragile = fragileIdx >= 0;

        if (hits.Count >= 2)
        {
            // 多 frame（ND8）：silent 选第一个 → 现 warning + multiFrames 列表。建 FrameInfo（name/url/文本片段），返第一命中 chain 作默认（A 兜底）。
            var multiFrames = new List<MultiFrameHit>(hits.Count);
            for (int i = 0; i < hits.Count; i++)
                multiFrames.Add(new MultiFrameHit(await BuildFrameInfoAsync(hits[i].Frame), hits[i].Chain));
            var warning = $"⚠️ 目标在 {hits.Count} 个 iframe 都出现，无法确定目标 frame（已默认选第 1 个）。请 request_help 用 (j) 输入编号指认目标 frame，或 (i) 传 iframeRef 精确定位";
            return (first.Chain, true, warning, firstIsFragile, multiFrames, null);
        }

        // 单命中：原 fragile/正常逻辑
        if (firstIsFragile)
        {
            var fragileLayer = first.Chain[fragileIdx];
            // DC3（步骤17）：对脆弱层（非命中 frame，可能是祖先层）调 CaptureFrameContextAsync 拼 warning 上下文（元素属性让用户选(a)/(b)生成覆盖链）。
            // els[fragileIdx] 是该脆弱层 iframe 的 IElementHandle（T6 并行数组）；CaptureFrameContextAsync 失败返 null 不阻断（warning 仍含脆弱层信息）。
            var ctx = fragileIdx < first.Els.Length ? await CaptureFrameContextAsync(first.Els[fragileIdx]) : null;
            var ctxSuffix = ctx != null ? $"\niframe 元素属性：{(ctx.Length > 50 ? ctx.Substring(0, 50) + "..." : ctx)}" : "";  // R3：给用户截前 50 字；全量 ctx 经 return 出参给 AI（选 (l) 喂）
            return (first.Chain, true, $"⚠️ iframe selector 链含脆弱层「{fragileLayer}」（位置选择器层/GUID id/动态锚点，无稳定锚点，回放易失效），建议 request_help 提供 iframe 的 id/src/name 或更稳 selector 链{ctxSuffix}", true, null, ctx);  // R2-细化：全量 ctx 经出参给 AI（选 (l) 喂），warning 内 ctxSuffix 给用户截前 50 字
        }
        return (first.Chain, true, null, false, null, null);
    }

    /// <summary>ND8：建 FrameInfo（name/url/body 文本片段截前 50 字）供 (j) 多 frame 求助菜单展示。EvaluateAsync 失败（跨域/未加载）返 null 片段不阻断。</summary>
    private async Task<FrameInfo> BuildFrameInfoAsync(IFrame frame)
    {
        string? snippet = null;
        try
        {
            var body = await frame.EvaluateAsync<string>("document.body.innerText");
            if (!string.IsNullOrEmpty(body)) snippet = body.Length > 50 ? body.Substring(0, 50) + "..." : body;
        }
        catch { /* 跨域/未加载 frame 取不到 body → 片段 null，菜单仍显示 name/url */ }
        return new FrameInfo(string.IsNullOrEmpty(frame.Name) ? null : frame.Name, frame.Url, snippet);
    }

    /// <summary>
    /// 判断目标是否在指定 frame 内（三分支统一用原生 IFrame，在 frame 自身 DOM 上查询——
    /// 不能在父级 body 上下文查，那样查到的是父级元素而非 frame 内元素，单层 iframe 反推会永不命中。防回归 9.3）。
    /// ref 用 frame.Locator("aria-ref=...")（Playwright 全局 ref）；selector 用 frame.Locator(selector)（XPath 必须在正确 frame 内执行）；
    /// keywords 用 frame.EvaluateAsync("document.body.innerText")，与回放 DetectEvaluator.EvaluatePageContainsAsync 一致。
    /// </summary>
    private static async Task<bool> IsTargetInFrameAsync(IFrame frame, FrameTarget target)
    {
        if (!string.IsNullOrEmpty(target.Ref))
            return await frame.Locator($"aria-ref={target.Ref}").CountAsync() > 0;
        if (!string.IsNullOrEmpty(target.Selector))
            return await frame.Locator(target.Selector).CountAsync() > 0;
        if (target.Keywords?.Any() ?? false)
        {
            var text = await frame.EvaluateAsync<string>("document.body.innerText") ?? "";
            return target.Keywords.Any(k => text.Contains(k));
        }
        return false;
    }

    /// <summary>
    /// 核心纯函数：沿 targetFrame.ParentFrame 链向上到 MainFrame 收集每层 IFrame（叶→根），Reverse 成根→叶，
    /// 每层用 frame.FrameElement 直取 &lt;iframe&gt; DOM + BuildFrameSelectorAsync 提取 selector，组成 string[]。
    /// 无任何跨调用状态（无字典、无 Id、无 updatedScript）。
    /// FrameElement==null（frame detached/未加载，非跨域）→ 抛 InvalidOperationException（D1 通道，由 FindFrameForTargetAsync catch 填 warning）。
    /// T6（a-a-4 步骤3，方向1a）：返并行数组 (Sels, Els)——Els[i] 是 Sels[i] 对应层的 IElementHandle，
    /// 供 DC3 CaptureFrameContext（步骤17）拿脆弱层 el 拼 IframeWarning（fragileLayer 可能是祖先层非命中 frame）。
    /// </summary>
    public async Task<(string[] Sels, IElementHandle[] Els)> ExtractFrameChainAsync(IFrame targetFrame, IPage page)
    {
        var chain = new List<IFrame>();
        for (var f = targetFrame; f != null && f != page.MainFrame; f = f.ParentFrame)
            chain.Add(f);
        chain.Reverse();  // 根→叶

        var sels = new List<string>();
        var els = new List<IElementHandle>();
        foreach (var fr in chain)
        {
            // FrameElement==null 入口防御（必须在调 BuildFrameSelectorAsync 之前）：
            // Playwright IFrame.FrameElement 在 frame detached/未加载（非跨域）时返回 null（而非抛）。
            // 不防御 → null IElementHandle 传入 BuildFrameSelectorAsync → 内部 NRE → 被其顶层宽 catch（保留）吞 →
            // 返 FallbackSelector（位置选择器）→ 截胡 D1"提取失败"warning 通道。入口防御在前抛 → FindFrameForTargetAsync catch 填 warning。
            var el = await fr.FrameElementAsync();
            if (el == null)
                throw new InvalidOperationException($"frame.FrameElement 为 null（frame 可能 detached/未加载，非跨域），url={fr.Url}");
            sels.Add(await BuildFrameSelectorAsync(el, fr));
            els.Add(el);  // T6：并行收集层 el（DC3 CaptureFrameContext 用）
        }
        return (sels.ToArray(), els.ToArray());
    }

    /// <summary>
    /// 结论14（a-a-4 步骤3）：用 aria-ref 指认 &lt;iframe&gt; 元素 → 定位其 IFrame → ExtractFrameChainAsync 提取"该 iframe 到 MainFrame"链。
    /// ND3 小测验证 aria-ref 能定位 iframe DOM（Playwright 1.59 AriaSnapshot 给 iframe 生成 [ref=eXX]）。
    /// ⚠️ ND3 验证亦实证**不能复用 FindFrameForTargetAsync**（语义错位）：①顶层 iframe 在 MainFrame DOM，FindFrameForTargetAsync L56 跳 MainFrame → inferred=False；
    /// ②嵌套返"含元素的 frame 链"非"该 iframe 自己的链"。故新建此方法（结论14 use case 2，区别于 FindFrameForTargetAsync 的 use case 1）。
    /// 第42轮 R2：Playwright .NET ElementHandle 跨句柄不保证同引用（== 可能恒 false）→ 先试引用相等，不命中再用 id 属性比对兜底。
    /// 返 (chain, error)：成功 chain 非空 error=null；ref 失效/非 iframe → chain=null + error（caller 走 ref 失效兜底，不 silent）。
    /// </summary>
    public async Task<(string[]? Chain, string? Error)> ResolveIframeFromRefAsync(IPage page, string refValue)
    {
        if (string.IsNullOrEmpty(refValue)) return (null, null);  // 无 ref 不反推（caller 不应调，兜底）

        IElementHandle? el;
        try { el = await page.Locator($"aria-ref={refValue}").ElementHandleAsync(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"iframeRef={refValue} 定位 iframe DOM 异常");
            return (null, $"aria-ref={refValue} 定位失败（{ex.Message}），建议重新 get_snapshot");
        }
        if (el == null)
            return (null, $"aria-ref={refValue} 未找到元素（页面已变化或快照过期），建议重新 get_snapshot 后再用 iframeRef");

        // 找该 iframe 的 IFrame：第一轮引用相等（==），第二轮 id 属性比对（防跨句柄不同引用）
        IFrame? found = null;
        foreach (var f in page.Frames)
        {
            if (f == page.MainFrame) continue;
            try
            {
                var fe = await f.FrameElementAsync();
                if (fe != null && fe == el) { found = f; break; }
            }
            catch { /* 单 frame 异常继续下一个 */ }
        }
        if (found == null)
        {
            // 引用相等未命中 → id 属性比对（ElementHandle 跨句柄可能不同引用，但指向同一 DOM 元素时 id 相同）
            var elId = await TryGetElementIdAsync(el);
            if (!string.IsNullOrEmpty(elId))
            {
                foreach (var f in page.Frames)
                {
                    if (f == page.MainFrame) continue;
                    try
                    {
                        var fe = await f.FrameElementAsync();
                        if (fe == null) continue;
                        var feId = await TryGetElementIdAsync(fe);
                        if (!string.IsNullOrEmpty(feId) && feId == elId) { found = f; break; }
                    }
                    catch { }
                }
            }
        }
        if (found == null)
            return (null, $"aria-ref={refValue} 指向的元素不是 &lt;iframe&gt; 或对应 frame 已卸载，请确认 iframeRef 指向 &lt;iframe&gt; 元素");

        var (chain, _) = await ExtractFrameChainAsync(found, page);
        return (chain, null);
    }

    /// <summary>读元素 id 属性（ResolveIframeFromRefAsync id 比对兜底用；失败/无 id 返 null/空）。</summary>
    private static async Task<string?> TryGetElementIdAsync(IElementHandle el)
    {
        try { return await el.EvaluateAsync<string>("x => x.id || ''"); }
        catch { return null; }
    }

    #region BuildFrameSelectorAsync — 5 阶段选择器提取（XPath）

    /// <summary>iframe 祖先遍历上限（在父 frame DOM 中，层级更浅）</summary>
    private const int MaxIframeAncestorDepth = 10;

    /// <summary>
    /// 从 &lt;iframe&gt; DOM 元素构建最优 selector（5 阶段，XPath 格式）。形态 A：输入直接是 frame.FrameElement（IElementHandle）+ ownerFrame。
    /// 唯一性检查用 ownerFrame.ParentFrame.Locator(selector).CountAsync()==1（iframe 元素定义在 ParentFrame 文档，D2/E1）。
    /// </summary>
    public async Task<string> BuildFrameSelectorAsync(IElementHandle iframeEl, IFrame ownerFrame)
    {
        try
        {
            // 读 iframe 元素完整属性（直取 DOM，无 name/url 反查退化）
            var attrs = await GetIframeAttributesAsync(iframeEl);
            if (attrs == null)
                return FallbackSelector(ownerFrame);

            // 收集各阶段的候选（predicate 是 XPath 谓词片段，如 @name='phone'）
            var stableExact = new List<(string predicate, string attrName)>();
            var unstableAttrs = new List<(string attrName, string attrValue)>();

            // === 阶段一：单属性精确匹配 ===
            foreach (var (attrName, attrValue) in CollectAttrEntries(attrs))
            {
                if (attrName == "src") continue;  // src 走独立策略（路径匹配，不参与组合）
                if (SelectorExtractor.IsStableValue(attrValue, attrName))
                {
                    var xpath = $"//iframe[@{attrName}={SelectorExtractor.XpathEscape(attrValue)}]";
                    if (await IsUniqueInParentContext(ownerFrame, xpath))
                        return xpath;
                    stableExact.Add(($"@{attrName}={SelectorExtractor.XpathEscape(attrValue)}", attrName));
                }
                else
                {
                    unstableAttrs.Add((attrName, attrValue));
                }
            }

            // src（短路径）— src 不使用 IsStableValue，有独立策略
            if (!string.IsNullOrEmpty(ownerFrame.Url))
            {
                try
                {
                    var uri = new Uri(ownerFrame.Url);
                    var srcXpath = $"//iframe[contains(@src,{SelectorExtractor.XpathEscape(uri.AbsolutePath)})]";
                    if (await IsUniqueInParentContext(ownerFrame, srcXpath))
                        return srcXpath;
                }
                catch { }
            }

            // === 阶段二：多属性精确组合（排除 id，稳定 id 应已唯一）===
            var combinable = stableExact.Where(c => c.attrName != "id").ToList();
            if (combinable.Count >= 2)
            {
                for (int i = 0; i < combinable.Count; i++)
                    for (int j = i + 1; j < combinable.Count; j++)
                    {
                        var combined = $"//iframe[{combinable[i].predicate} and {combinable[j].predicate}]";
                        if (await IsUniqueInParentContext(ownerFrame, combined))
                            return combined;
                    }
                if (combinable.Count >= 3)
                {
                    for (int i = 0; i < combinable.Count - 2; i++)
                        for (int j = i + 1; j < combinable.Count - 1; j++)
                            for (int k = j + 1; k < combinable.Count; k++)
                            {
                                var combined = $"//iframe[{combinable[i].predicate} and {combinable[j].predicate} and {combinable[k].predicate}]";
                                if (await IsUniqueInParentContext(ownerFrame, combined))
                                    return combined;
                            }
                }
            }

            // === 阶段三：祖先路径（仅属性锚点，在 iframe DOM 元素上执行）===
            try
            {
                var ancestorResults = await iframeEl.EvaluateAsync<List<Dictionary<string, JsonElement>>>(@"(el) => {
                        function isStable(val) {
                            if (/[a-fA-F0-9]{8}(-[a-fA-F0-9]{4}){3}-[a-fA-F0-9]{12}/.test(val)) return false;
                            if (/\d{6,}/.test(val)) return false;
                            if (/^(ext-gen|layui-layer|rc-[\w]+-)\d+$/.test(val)) return false;
                            if (/^\d+$/.test(val)) return false;
                            if (/^:r[a-z0-9]*:$/.test(val)) return false;
                            return true;
                        }
                        const MAX_DEPTH = " + MaxIframeAncestorDepth + @";
                        const results = [];
                        let depth = 0;
                        let current = el.parentElement;
                        while (current && depth < MAX_DEPTH) {
                            depth++;
                            const tag = current.tagName.toLowerCase();
                            if (current.id && isStable(current.id)) {
                                results.push({attr: 'id', value: current.id, depth, tag});
                            }
                            var nameVal = current.getAttribute('name');
                            if (nameVal && isStable(nameVal)) {
                                results.push({attr: 'name', value: nameVal, depth, tag});
                            }
                            var titleVal = current.getAttribute('title');
                            if (titleVal && isStable(titleVal)) {
                                results.push({attr: 'title', value: titleVal, depth, tag});
                            }
                            for (const attr of current.attributes) {
                                if (attr.name.startsWith('data-') && isStable(attr.value)) {
                                    results.push({attr: attr.name, value: attr.value, depth, tag});
                                }
                            }
                            var directText = '';
                            for (const child of current.childNodes) {
                                if (child.nodeType === 3) directText += child.textContent;
                            }
                            directText = directText.trim();
                            if (directText.length > 0 && directText.length < 50) {
                                results.push({attr: 'text', value: directText, depth, tag});
                            }
                            current = current.parentElement;
                        }
                        return results;
                    }");

                if (ancestorResults != null)
                {
                    var parentContext = ownerFrame.ParentFrame?.Locator("body");
                    if (parentContext != null)
                    {
                        foreach (var anchor in ancestorResults)
                        {
                            var attr = anchor["attr"].GetString();
                            var value = anchor["value"].GetString();
                            string xpath = attr == "text"
                                ? $"//{anchor["tag"].GetString()}[contains(.,{SelectorExtractor.XpathEscape(value!)})]//iframe"
                                : $"//*[@{attr}={SelectorExtractor.XpathEscape(value!)}]//iframe";
                            if (await parentContext.Locator(xpath).CountAsync() == 1)
                                return xpath;
                        }
                    }
                }
            }
            catch { /* 祖先遍历失败，继续下一阶段 */ }

            // === 阶段四：属性包含匹配（合并单+多）===
            var containsCandidates = new List<(string predicate, string attrName)>();
            foreach (var (attrName, attrValue) in unstableAttrs)
            {
                var stablePart = SelectorExtractor.TryExtractStablePart(attrValue, attrName);
                if (stablePart == null || stablePart.Length < 2) continue;

                var predicate = $"contains(@{attrName},{SelectorExtractor.XpathEscape(stablePart)})";
                var xpath = $"//iframe[{predicate}]";
                if (await IsUniqueInParentContext(ownerFrame, xpath))
                    return xpath;
                containsCandidates.Add((predicate, attrName));
            }
            if (containsCandidates.Count >= 2)
            {
                for (int i = 0; i < containsCandidates.Count; i++)
                    for (int j = i + 1; j < containsCandidates.Count; j++)
                    {
                        var combined = $"//iframe[{containsCandidates[i].predicate} and {containsCandidates[j].predicate}]";
                        if (await IsUniqueInParentContext(ownerFrame, combined))
                            return combined;
                    }
            }

            return FallbackSelector(ownerFrame);
        }
        catch (Exception ex)
        {
            // 仅兜底"selector 提取算法失败"返 FallbackSelector；FrameElement==null 异常已在 ExtractFrameChainAsync 入口抛出（结构隔离）
            _logger.LogWarning(ex, "BuildFrameSelectorAsync 5 阶段提取失败，退化 FallbackSelector");
            return FallbackSelector(ownerFrame);
        }
    }

    /// <summary>把属性字典按优先级顺序展开（id/name/title/data-testid 在前，其他 data-* 随后）。</summary>
    private static IEnumerable<(string attrName, string attrValue)> CollectAttrEntries(Dictionary<string, string> attrs)
    {
        var priority = new[] { "id", "name", "title", "data-testid" };
        foreach (var p in priority)
            if (attrs.TryGetValue(p, out var v) && !string.IsNullOrEmpty(v))
                yield return (p, v);
        foreach (var kvp in attrs)
        {
            if (priority.Contains(kvp.Key)) continue;
            if (kvp.Key.StartsWith("data-") && !string.IsNullOrEmpty(kvp.Value))
                yield return (kvp.Key, kvp.Value);
        }
    }

    private static string FallbackSelector(IFrame frame)
    {
        if (!string.IsNullOrEmpty(frame.Name))
            return $"//iframe[@name={SelectorExtractor.XpathEscape(frame.Name)}]";
        if (!string.IsNullOrEmpty(frame.Url))
        {
            try { return $"//iframe[contains(@src,{SelectorExtractor.XpathEscape(new Uri(frame.Url).AbsolutePath)})]"; }
            catch { }
        }
        return "xpath=(//iframe)[1]";
    }

    /// <summary>
    /// 在 iframe 元素所在的父文档（ownerFrame.ParentFrame）内检查 selector 是否唯一（D2/E1）。
    /// iframe 元素定义在 ParentFrame 文档，直接在该文档查唯一性——比祖先链更简单且更可靠。
    /// 防御 ParentFrame==null（理论不会，fr≠MainFrame；兜底返 false 让 BuildFrameSelectorAsync 继续下一阶段）。
    /// </summary>
    private async Task<bool> IsUniqueInParentContext(IFrame ownerFrame, string selector)
    {
        try
        {
            var parentContext = ownerFrame.ParentFrame?.Locator("body");
            if (parentContext == null) return false;
            return await parentContext.Locator(selector).CountAsync() == 1;
        }
        catch { return false; }
    }

    /// <summary>
    /// 直接从 &lt;iframe&gt; DOM 元素读取所有属性（形态 A：frame.FrameElement 直取，消除旧 name/url 反查退化 P1a）。
    /// </summary>
    private static async Task<Dictionary<string, string>?> GetIframeAttributesAsync(IElementHandle iframeEl)
    {
        try
        {
            return await iframeEl.EvaluateAsync<Dictionary<string, string>>(@"(el) => {
                const result = {};
                for (const attr of el.attributes) {
                    result[attr.name] = attr.value;
                }
                return result;
            }");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 2b（阶段2）：读 iframe 元素自身属性（id/src/name）+ outerHTML 全量，拼成 warning 上下文，
    /// 供 AI/用户基于元素属性生成更稳 selector 链覆盖。frame detached/未加载（FrameElement==null）走不了此方法 → 走 D1 warning。
    /// </summary>
    public async Task<string?> CaptureFrameContextAsync(IElementHandle iframeEl)
    {
        try
        {
            return await iframeEl.EvaluateAsync<string>(@"(el) => {
                const id = el.getAttribute('id') || '';
                const name = el.getAttribute('name') || '';
                const src = el.getAttribute('src') || '';
                return 'id=' + id + ' name=' + name + ' src=' + src + ' html=' + el.outerHTML;
            }");
        }
        catch { return null; }
    }

    #endregion
}
