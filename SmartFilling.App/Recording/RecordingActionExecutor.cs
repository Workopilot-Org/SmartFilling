using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using SmartFilling.App.Configuration;
using SmartFilling.App.Models;
using SmartFilling.Engine.Engine;
using SmartFilling.Engine.Models;
using EngineILogger = SmartFilling.Engine.Logging.ILogger;

namespace SmartFilling.App.Recording;

/// <summary>
/// 录制操作执行器 — 处理 AI Tool Calling → Playwright 执行 → 提取 selector → 保存 step
/// </summary>
public class RecordingActionExecutor
{
    private readonly SelectorExtractor _selectorExtractor;
    private readonly IframeDetector _iframeDetector;
    private readonly PageSnapshotExtractor _snapshotExtractor = new();
    private readonly EngineILogger _logger;
    private readonly AppOptions _appOptions;
    private readonly Func<string, string?>? _systemVarResolver;  // F8：operate 执行层 {{password}}→真实值
    private readonly List<AttachmentInfo>? _attachments;  // 批次7-C7：录制期附件（upload 执行层 SetInputFiles 用）
    private readonly string? _uploadRootPath;  // 批次7-A1：附件根目录（_attachments.Path 相对，需拼接绝对路径）

    public RecordingActionExecutor(EngineILogger logger, AppOptions appOptions, Func<string, string?>? systemVarResolver = null, List<AttachmentInfo>? attachments = null, string? uploadRootPath = null)
    {
        _logger = logger;
        _appOptions = appOptions;
        _systemVarResolver = systemVarResolver;
        _attachments = attachments;  // 批次7-C7：录制期附件（upload 执行层用）
        _uploadRootPath = uploadRootPath;  // 批次7-A1：附件根目录（_attachments.Path 相对 uploads/xxx，需 rootPath 拼绝对）
        // E2 时序：IframeDetector 需 _logger，必须在 _logger 赋值后构造（原字段初始化器在构造体前执行，_logger 仍 null）。
        _selectorExtractor = new SelectorExtractor();
        _iframeDetector = new IframeDetector(_logger);
    }

    /// <summary>F8：把 value 中的 {{系统变量}} 替换为真实值（仅系统凭据 password/username，填浏览器用；其他变量保留占位符由回放引擎填）。</summary>
    private string ResolveSystemVars(string? value)
    {
        if (string.IsNullOrEmpty(value) || _systemVarResolver == null) return value ?? "";
        return System.Text.RegularExpressions.Regex.Replace(value, @"\{\{(\w+)\}\}", m =>
        {
            var resolved = _systemVarResolver(m.Groups[1].Value);
            return resolved ?? m.Value;
        });
    }

    /// <summary>
    /// 批次3：合成录制期 fieldDef（args 本次 tool call 即时数据 + _script.Fields 已注册字段，双源）。
    /// 首次录制时 _script.Fields 尚未注册当前字段（注册在 ExecuteActionAsync 之后），故 args 是唯一来源；
    /// 重复录制时 _script.Fields 已注册，作兜底。双源对冲 AI 因对话压缩漏传的风险。
    /// args 的 string 值经 OpenAiProvider.ParseJsonElement 归一化为 CLR string，GetValueOrDefault().ToString() 返回纯字符串（无引号）。
    /// ⚠️ 首次录制 baseDef=null 且 AI 不传 fieldType → Type=null（format 依赖 Type），由批次4/5 的 ResolveFieldTypeAsync 现场从 DOM 补（重大1 修法B）。
    /// </summary>
    internal static FieldDefinition? ResolveRecordingFieldDef(string? fieldName, ScriptV2? script, Dictionary<string, object>? args)
    {
        var baseDef = (fieldName != null && script != null) ? VariableHelper.GetFieldDefinition(fieldName, script) : null;
        var fmt = args?.GetValueOrDefault("fieldFormat")?.ToString();
        var tr = args?.GetValueOrDefault("fieldTransform")?.ToString();
        var tp = args?.GetValueOrDefault("fieldType")?.ToString();
        if (baseDef == null && string.IsNullOrEmpty(fmt) && string.IsNullOrEmpty(tr) && string.IsNullOrEmpty(tp)) return null;
        return (baseDef ?? new FieldDefinition { Name = fieldName ?? "" }) with
        {
            Format = !string.IsNullOrEmpty(fmt) ? fmt : baseDef?.Format,
            Transform = !string.IsNullOrEmpty(tr) ? tr : baseDef?.Transform,
            Type = !string.IsNullOrEmpty(tp) ? tp : baseDef?.Type,
        };
    }

    /// <summary>
    /// 决策 D3/F①：把 loop 内多独立 input（明细每行各一框）selector 的行索引参数化为 {{rowIndex}}，
    /// 对齐回放 ScriptEngine loop（ctx.Vars["rowIndex"]=rowIdx+offset，{{rowIndex}} 经 VariableHelper.ReplaceVars 替换为当前行索引）。
    /// 多模式：属性优先（@rowindex/@data-row/@data-index=数字）→ 回退位置（任意 tag[数字]，如 tr[3]）。
    /// 多匹配=歧义（属性行索引多个 / 位置多[数字]=行列分不清）→ ambiguous=true（ExecuteOperateAsync 返 OperateResult(false) → AI 调 request_help 兜底）。
    /// 注：posRe 用 \w+\[\d+\]（覆盖 tr/td 等 tag，不只 tr），以准确识别"多[数字]=行列歧义"（计划 F⑤ test5 tr[1]/td[2] 期望 ambiguous）。
    /// </summary>
    internal static (string Selector, bool Ambiguous) ParametrizeRowIndex(string selector, string? mode)
    {
        // 属性：@rowindex/@data-row/@data-index = 数字（可选引号）；尾部 (?:['""])? 消费闭合引号避免残留双引号
        var attrRe = new Regex(@"@(rowindex|data-row|data-index)\s*=\s*['""]?(\d+)(?:['""])?", RegexOptions.IgnoreCase);
        if (mode is null or "attr")
        {
            var am = attrRe.Matches(selector);
            if (am.Count == 1) return (attrRe.Replace(selector, "@$1='{{rowIndex}}'"), false);
            if (am.Count > 1) return (selector, true);  // 多个属性行索引 = 歧义
        }
        // 位置：任意 tag[数字]（tr[3]/td[2]）；多[数字]=行列分不清
        var posRe = new Regex(@"(\w+)\[(\d+)\]");
        if (mode is null or "position")
        {
            var pm = posRe.Matches(selector);
            if (pm.Count == 1) return (posRe.Replace(selector, "$1[{{rowIndex}}]"), false);
            if (pm.Count > 1) return (selector, true);  // 行/列分不清
        }
        return (selector, false);  // 无行索引（同 input 场景），不变
    }

    /// <summary>
    /// 批次4（重大1 修法B）：首次录制 fieldDef.Type 缺失（baseDef=null + AI 不传 fieldType）时，format 转换依赖 Type 会失效；
    /// 现场从 DOM 推断 Type 补全（input[number]→number、date/datetime-local→date、其余→string），让录制期 format 生效 = 回放转换值。
    /// 复用 ExtractFieldFromElementAsync 同款 inputType→Type 映射。
    /// </summary>
    private static async Task<string?> ResolveFieldTypeAsync(ILocator? loc, FieldDefinition? fieldDef)
    {
        if (!string.IsNullOrEmpty(fieldDef?.Type)) return fieldDef!.Type;
        if (string.IsNullOrEmpty(fieldDef?.Format)) return null;  // 无 format 约束，Type 无意义（ApplyFormat 透传原值）
        if (loc == null) return null;
        try
        {
            var tagAndType = await loc.EvaluateAsync<string>("el => (el.tagName||'').toLowerCase() + '|' + (el.type||'')");
            var idx = tagAndType.IndexOf('|');
            var inputType = idx >= 0 ? tagAndType[(idx + 1)..].ToLowerInvariant() : "";
            return inputType switch
            {
                "number" => "number",
                "date" or "datetime-local" => "date",
                _ => "string"
            };
        }
        catch
        {
            return null;  // 元素消失/框架异常不阻断 fill（ApplyFormat type=null 透传原值；silent 降级可接受 P8）
        }
    }

    /// <summary>批次7-A1：解析附件 Path 为可上传的本地绝对路径。AttachmentService 存 Path="uploads/xxx"（相对 ContentRoot），需 rootPath 拼绝对；绝对路径直接用。与回放 StepExecutor 的 ResolveFilePath 同源（UploadRootPath ?? BaseDirectory + TrimStart）。</summary>
    private string ResolveAttachmentPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        if (System.IO.Path.IsPathRooted(path)) return path;
        var root = string.IsNullOrEmpty(_uploadRootPath) ? AppContext.BaseDirectory : _uploadRootPath;
        return System.IO.Path.Combine(root, path.TrimStart('/'));
    }

    /// <summary>
    /// 执行交互操作（operate 工具）
    /// </summary>
    public async Task<OperateResult> ExecuteOperateAsync(
        IPage page,
        StepNode step,
        ScriptV2 script,
        List<PhaseItem> steps,
        Dictionary<string, object> args,
        CancellationToken ct,
        string? loopSource = null,
        bool acceptFragile = false,
        Func<FragileType, string?, bool>? isAcceptedAsIs = null,  // ND7（a-a-4）：acceptAsIs 集合查询回调（_acceptedAsIs 在 RecordingEngine，阶段1 count==0 record-without-execute 用）
        Func<FragileType, string?, bool>? isFragileAccepted = null)  // 待决策1（a-a-4）：acceptFragile 集合查询回调（_acceptedFragile 在 RecordingEngine，阶段0 iframe 脆弱链集合命中 falls-through 用，镜像 isAcceptedAsIs）
    {
        var action = step.Action;
        var value = step.Action == "navigate" ? step.Url : step.Value;
        var refValue = args.GetValueOrDefault("ref")?.ToString();
        var selector = step.Selector;

        // 批次2：pressKey 无 selector 且无 aria-ref → 全局键盘按键（设计四层一致支持；修录制端外层元素定位流程拦截、内层全局键盘分支不可达的既存 bug）
        if (action == "pressKey" && string.IsNullOrEmpty(refValue) && string.IsNullOrEmpty(selector))
        {
            try
            {
                await ExecuteActionAsync(page, page.Locator("body"), action, null, value, step, ct, fieldDef: null, loopSource: null);
            }
            catch (Exception ex)
            {
                return new OperateResult(false, $"操作执行失败: {ex.Message}", null, null);
            }
            step = step with { Selector = null };
            steps.Add(step);
            return new OperateResult(true, $"已按键 {step.Key ?? "Enter"}", null, null);
        }

        // 批次9①：scroll 二选一校验 + direction 模式分流（direction 不强制元素定位，走非元素分支）
        if (action == "scroll")
        {
            var hasSelector = !string.IsNullOrEmpty(selector);
            var hasDirection = !string.IsNullOrEmpty(step.Direction);  // 批次9 核查第9轮🟡3：空串视为未指定（统一 ScriptLoader/回放）
            if (hasSelector && hasDirection)
                return new OperateResult(false, "scroll 的 selector 与 direction 互斥，二选一", null, null);
            if (!hasSelector && !hasDirection)
                return new OperateResult(false, "scroll 需指定 selector 或 direction 之一", null, null);
            if (hasDirection)
            {
                try
                {
                    await ExecuteActionAsync(page, page.Locator("body"), action, null, value, step, ct, fieldDef: null, loopSource: null);
                }
                catch (Exception ex)
                {
                    return new OperateResult(false, $"操作执行失败: {ex.Message}", null, null);
                }
                step = step with { Selector = null };
                steps.Add(step);
                return new OperateResult(true, $"已滚动 {step.Direction}", null, null);
            }
            // hasSelector：落到下面元素定位流程
        }

        // 非元素操作不需要定位
        if (action is "navigate" or "goBack" or "reload" or "closeTab" or "switchTab")
        {
            // 批次8：switchTab 块移入 try（越界改 throw，被同一 catch 接住 → OperateResult(false) 问用户；与 closeTab 统一）
            try
            {
                // switchTab：切到指定标签页（index，-1=最后一个），返回 NewPage 让 RecordingEngine 更新当前页
                if (action == "switchTab")
                {
                    var swPages = page.Context.Pages;
                    var idx = step.Index ?? -1;
                    if (idx == -1) idx = swPages.Count - 1;
                    if (idx < 0 || idx >= swPages.Count)
                        throw new InvalidOperationException($"switchTab 索引 {idx} 越界（共 {swPages.Count} 个标签页，用 get_snapshot 查看所有标签页）");
                    var newPage = swPages[idx];
                    await newPage.BringToFrontAsync();
                    step = step with { Selector = null };
                    steps.Add(step);
                    return new OperateResult(true, $"已切换到标签页 {idx}（共 {swPages.Count} 个）", null, null, null, newPage);
                }
                await ExecuteActionAsync(page, page.Locator("body"), action, null, value, step, ct, fieldDef: null, loopSource: null);
                step = step with { Selector = null };
                steps.Add(step);
                return new OperateResult(true, $"已执行 {action}", null, null);
            }
            catch (Exception ex)
            {
                return new OperateResult(false, $"操作执行失败: {ex.Message}", null, null);
            }
        }

        // ========== 阶段 0：iframe 反推（前移）==========
        // 形态 A：step.Iframe 是 selector 链（非 Guid）。
        // - AI 传了链 → ValidateIframeChainAsync 校验覆盖（每层父文档唯一定位 iframe 元素）；校验失败→失败反馈。
        // - AI 没传 → 代码 ExtractFrameChainAsync 反推。warning 非空（提取失败/高脆弱）→ 立即返失败走 L647 强求助（Z1，不走后续阶段）。
        // 反推不出（真主文档）→ iframeChain=null（不退化回旧"第一个非主 frame"启发式）。
        string[]? iframeChain = null;
        try
        {
            if (step.Iframe is { Length: > 0 } aiChain)
            {
                var (valid, validationWarning) = await ValidateIframeChainAsync(page, aiChain);
                if (valid)
                {
                    // C1-A2（结论13）：AI 传的链若是脆弱链（AssessFragility>=8），设 IsFragileIframe+IframeWarning+IframeChain，走与反推脆弱链同款 (f) 求助（caller 据 IsFragileIframe 走 RequestIframeFragileHelpAsync + 填 StepIframe 集合）
                    if (aiChain.Any(s => SelectorExtractor.AssessFragility(s) >= 8))
                    {
                        var aiChainWarning = $"⚠️ AI 传的 iframe 链含脆弱层「{string.Join(" > ", aiChain)}」（GUID/动态锚点/位置选择器层，回放易失效），建议 request_help 提供 iframe 的 id/src/name 或更稳 selector 链";
                        return new OperateResult(false, aiChainWarning, null, aiChain) { IsFragileIframe = true, IframeWarning = aiChainWarning, MultiFrames = null };
                    }
                    iframeChain = aiChain;  // 校验通过且不脆弱，用 AI 链覆盖代码反推
                }
                else
                    return new OperateResult(false, validationWarning ?? "AI 传的 iframe 链校验失败，请检查或留空让代码自动提取", null, null);
            }
            else
            {
                FrameTarget? target = !string.IsNullOrEmpty(refValue) ? new() { Ref = refValue }
                    : (!string.IsNullOrEmpty(selector) && !selector.Contains("{{")) ? new() { Selector = selector }
                    : null;
                if (target != null)
                {
                    var (chain, inferred, warning, isFragileLayer, multiFrames, ctx) = await _iframeDetector.FindFrameForTargetAsync(page, target);
                    if (warning != null)
                    {
                        // 待决策1 (a)（a-a-4）：iframe 脆弱链集合命中（用户已接受此脆弱链）→ 不返 IsFragileIframe，falls-through 让后续阶段用此 chain 落盘（镜像 selector L434 if(!acceptFragile) return 两层跳过 + caller 集合精控）。
                        // multiFrames 守卫：多 frame 须走 (j) picker（caller），不能 acceptFragile falls-through 第一命中链。onError 由 R2-B finalStep 统一设（不在此设）。SerializeChain 在此内联 string.Join（与 RecordingEngine.SerializeChain 同语义，跨类不可达）。
                        bool iframeFragileAccepted = isFragileLayer && chain is { Length: > 0 } && acceptFragile
                            && multiFrames is not { Count: >= 2 }
                            && isFragileAccepted != null && isFragileAccepted(FragileType.StepIframe, string.Join(" > ", chain));
                        if (!iframeFragileAccepted)
                        {
                            // Z1：提取失败/高脆弱/多 frame → 立即返失败让 caller L710 兜住（不走后续阶段）。
                            // DC3（a-a-4）：IsFragileIframe 结构化（不靠 warning 字符串匹配，W4），caller L710 据此走 BuildHelpQuestion 含 (f)/(i)；IframeWarning 透传 warning 文本。
                            // 第1轮核查#2：传 chain（非 null）让 caller 能填 StepIframe 集合（W2）+ BuildHelpQuestion 显示脆弱链。脆弱时 chain 非空；提取失败时 chain=null（caller IsFragileIframe=false 不走 f/i 填集合）。
                            // ND8（a-a-4）：multiFrames（多 frame 命中）传 caller 走 (j) 指认编号 picker（caller RequestMultiFrameHelpAsync）。
                            return new OperateResult(false, warning, null, chain) { IsFragileIframe = isFragileLayer, IframeWarning = warning, MultiFrames = multiFrames, IframeCtx = ctx };  // R2-细化：IframeCtx=脆弱层 ctx 全量（选 (l) 喂 AI）；aiChain 路径（L246）无 ctx 不设（默认 null，不显示 l）
                        }
                        // iframeFragileAccepted=true → falls through（inferred=true 下方 iframeChain=chain 赋值落盘）
                    }
                    if (inferred) iframeChain = chain;
                    else iframeChain = Array.Empty<string>();  // T9（a-a-4 L4544）：反推到主文档（inferred=false+warning=null）→ 显式 []（D3 约定：[]=主文档不继承 / null=继承 phase）；target==null 路径保持 null（不适用，W2 限定）
                }
                // target==null（navigate/pressKey 全局/scroll direction 等无元素锚点）→ iframeChain=null
            }
        }
        catch (Exception ex)
        {
            // 3.3：不静默"按主文档处理"。未预期异常 → 强求助（避免 iframe 提取异常被掩盖导致目标在 iframe 但走主文档→回放失败）
            _logger.LogWarning(ex, "iframe 反推未预期异常");
            return new OperateResult(false, $"⚠️ iframe 反推失败（{ex.Message}），建议 request_help", null, null);
        }

        // ========== 阶段 1：定位元素（缺陷1 修复：selector 路径进 iframe）==========
        ILocator? elementLocator = null;

        if (!string.IsNullOrEmpty(refValue))
        {
            // aria-ref 是 Playwright 全局 ref（跨 frame 唯一），page.Locator 即可定位 iframe 内元素，无需 frame 上下文
            try
            {
                elementLocator = page.Locator($"aria-ref={refValue}");
                if (await elementLocator.CountAsync() == 0)
                    return new OperateResult(false,
                        $"aria-ref={refValue} 未找到元素。可能页面已变化或快照过期。" +
                        $"建议：调用 get_snapshot 获取最新页面快照，再用新的 ref 重试。",
                        null, iframeChain);
            }
            catch (Exception ex)
            {
                return new OperateResult(false,
                    $"aria-ref 定位失败: {ex.Message}。建议调用 get_snapshot 获取最新快照后重试。",
                    null, iframeChain);
            }
        }
        else if (!string.IsNullOrEmpty(selector))
        {
            // 缺陷1 修复：无 ref 时用 selector，必须进 iframe（原 page.Locator 主文档，iframe 内元素必"未找到元素"）。
            // XPath 必须在正确 frame 内执行；iframeChain 现来自阶段0 反推。
            try
            {
                var frame = ResolveFrame(page, iframeChain);
                elementLocator = frame.Locator(selector);
                var matchCount = await elementLocator.CountAsync();
                if (matchCount == 0)
                {
                    // ND7（a-a-4）：acceptAsIs 命中（用户选过 (h)，元素动态加载未到）→ record-without-execute（跳录制期动作，落盘 selector+onError 兜底，回放期再验证）。
                    if (isAcceptedAsIs != null && isAcceptedAsIs(FragileType.StepSelector, selector))
                    {
                        var asIsStep = step with { Selector = selector, Value = value, Iframe = iframeChain, OnError = string.IsNullOrEmpty(step.OnError) ? "ai-fallback-stop" : step.OnError };
                        steps.Add(asIsStep);
                        return new OperateResult(true, $"已接受未找到的 selector「{selector}」（acceptAsIs，录制期元素未加载），落盘 + onError=ai-fallback-stop 兜底，回放期再验证", selector, iframeChain);
                    }
                    return new OperateResult(false, $"selector={selector} 未找到元素", selector, iframeChain) { IsCountZero = true };  // ND7：IsCountZero 标记让 caller 求助给 (h)
                }
                // 2c 改动1（方案B改动1）：多匹配（count>1）求助——回放 fill/select 等 Playwright strict mode 多匹配抛错，
                // 录制期挡住让 AI/用户解决唯一性。useLast=true 时多匹配接受（回放取最后）；acceptFragile 不放宽多匹配（D6：必须解决唯一性）。
                if (matchCount > 1 && step.UseLast != true)
                    return new OperateResult(true, $"selector={selector} 匹配 {matchCount} 个元素（多匹配，回放 strict mode 会抛错）", selector, iframeChain, elementLocator) { IsMultiMatch = true };
            }
            catch (Exception ex)
            {
                return new OperateResult(false, $"selector 定位失败: {ex.Message}", null, iframeChain);
            }
        }

        // ========== 阶段 2：删除（iframe 反推已前移到阶段0；iframeChain 已在阶段0 算出）==========

        // ========== 阶段 3：提取候选 selector ==========
        string? extractedSelector = selector;
        List<string>? candidateSelectors = null;
        bool needsAiAction = false;
        bool needsFallback = false;
        int? extractedPriority = null;  // V1：捕获 priority 信号传 RecordingEngine（priority 8 纯路径→代码触发 L647）

        if (elementLocator != null)
        {
            // DC7（a-a-4，选 B·2026-07-03 用户拍板 + D6 尊重手写）：selector 入口原样保留 AI selector（不调 ExtractAsync 覆盖）；
            // 仅 ref 入口调 ExtractAsync（ref→selector 转换，需重新提取）。selector 入口直接 AssessFragility 算 priority + needsFallback（priority>=7，复刻 ExtractAsync 语义）。
            if (!string.IsNullOrEmpty(refValue))
            {
                try
                {
                    // 在正确的 frame 上下文中提取选择器
                    var frame = ResolveFrame(page, iframeChain);
                    var selectorResult = await _selectorExtractor.ExtractAsync(elementLocator, frame);
                    extractedSelector = selectorResult.Selector;
                    candidateSelectors = selectorResult.Candidates;
                    needsAiAction = selectorResult.NeedsAiAction;
                    needsFallback = selectorResult.NeedsFallback;
                    extractedPriority = selectorResult.Priority;
                }
                catch (Exception ex) { _logger.LogWarning(ex, "selector 提取失败，回退使用 AI 提供的 selector"); }
            }
            else if (!string.IsNullOrEmpty(selector))
            {
                // DC7：selector 入口——extractedSelector 保持 = selector（L318 默认，原样保留 AI 手写），直接 AssessFragility 算 priority（不调 ExtractAsync）。
                // priority>=7（contains/纯位置/动态锚点）→ needsFallback（onError=ai-fallback-stop 兜底）；L385 priority==8 求助路径 + L453 validatedSelector=extractedSelector??selector 天然兼容。
                extractedPriority = SelectorExtractor.AssessFragility(selector);
                needsFallback = extractedPriority >= 7;
            }
        }

        // 无法唯一定位 → 不再静默保存为 ai action
        // #40/🟢-2：用共享 GetBool 解析（原 is bool 对 JsonElement 恒 false；曾内联双分支，现统一调 RecordingEngine.GetBool）
        var forceAiAction = RecordingEngine.GetBool(args, "forceAiAction") == true;

        if (needsAiAction && !forceAiAction)
        {
            // DC5（a-a-4，P1/P2 三轮核查 R1 三方一致 + ND5 选A）：priority 9（7阶段全失败）改返 Success=true + Priority=9，
            // 走外层 L696 求助分支（非 L710 失败分流）+ BuildHelpQuestion Common5（priority 9 无 (f)，ND5 选A 多匹配优先无 f）。
            // T11：HtmlContext 承载元素 HTML（CaptureElementContextAsync 产），caller 拼 warning 给 BuildHelpQuestion（用户看 HTML 才能选 (b)）。
            var htmlContext = elementLocator != null
                ? await _selectorExtractor.CaptureElementContextAsync(elementLocator)
                : null;
            var message = $"无法提取稳定 selector（7 阶段候选全部失败）";
            return new OperateResult(true, message, null, iframeChain, Priority: 9, HtmlContext: htmlContext);
        }

        if (needsAiAction && forceAiAction)
        {
            // N4：映射 ai 专属字段 + 保留录制上下文（原 ai StepNode 构造丢失 iframe/field/ai 专属字段）
            int? aiMaxTurns = null;
            if (args.TryGetValue("maxAiTurns", out var matEl))
            {
                if (matEl is JsonElement matJe && matJe.ValueKind == JsonValueKind.Number) aiMaxTurns = matJe.GetInt32();
                else if (matEl is int matI) aiMaxTurns = matI;
            }
            var aiStep = new StepNode
            {
                Kind = "step",
                Name = step.Name,
                Action = "ai",
                Description = step.Description ?? $"执行 {action} 操作",
                Value = value,
                Iframe = iframeChain,           // 保留 iframe 上下文（string[] 链）
                Field = step.Field,           // 保留关联字段
                MaxAiTurns = aiMaxTurns,      // ai 轮次
                OnError = args.GetValueOrDefault("onError")?.ToString(),
            };
            steps.Add(aiStep);
            return new OperateResult(true, "已保存为 ai action（AI 显式确认），录制期执行推进页面", null, iframeChain) { NeedsAiExecution = true };
        }

        // ========== 2c G3.4：priority 8（纯路径高脆弱）门禁配套——检测点在 steps.Add/阶段5 之前 ==========
        // 修 V1 缺陷 #1（无接受出口→循环）/#2（门禁在 Add 后→重复 step）/#3（priority 8 silent 落盘）/#4（priority 8 ai action 不可达）。
        // D9②：每个 priority 8 都求助（无周期标志），防同元素循环纯靠 acceptFragile（用户选 (f) 后下轮 acceptFragile=true+同 selector 跳过检测落盘）。
        // priority 9（needsAiAction，提取不出）已在上方分支处理；此处仅 priority==8（唯一但脆弱）。
        if (extractedPriority == 8)
        {
            if (forceAiAction)
            {
                // 用户选 (d) ai 节点：存 aiStep + 标记外层录制期执行（推进页面，否则录制断链）。修 V1 缺陷 #4（priority 8 可达 ai action）。
                int? aiMaxTurns8 = null;
                if (args.TryGetValue("maxAiTurns", out var mat8El))
                {
                    if (mat8El is JsonElement mat8Je && mat8Je.ValueKind == JsonValueKind.Number) aiMaxTurns8 = mat8Je.GetInt32();
                    else if (mat8El is int mat8I) aiMaxTurns8 = mat8I;
                }
                var aiStep8 = new StepNode
                {
                    Kind = "step",
                    Name = step.Name,
                    Action = "ai",
                    Description = step.Description ?? $"执行 {action} 操作",
                    Value = value,
                    Iframe = iframeChain,
                    Field = step.Field,
                    MaxAiTurns = aiMaxTurns8,
                    OnError = args.GetValueOrDefault("onError")?.ToString(),
                };
                steps.Add(aiStep8);
                return new OperateResult(true, "已保存为 ai action（priority 8 用户选 ai 节点，录制期执行推进页面）", null, iframeChain) { NeedsAiExecution = true };
            }
            if (!acceptFragile)
            {
                // 提前 return：不构造 finalStep、不 Add、不执行阶段5（修缺陷 #2/#3：silent 落盘）。
                // 带 Priority 信号让外层门禁求助（选项 a-f，含 (f) 使用脆弱的）；用户选 (f) 后下轮 acceptFragile=true+同 selector 跳过此检测落盘。
                return new OperateResult(true, $"⚠️ 此 selector 较脆弱（priority=8，纯路径/完整路径，DOM 微调即失效）", extractedSelector, iframeChain, elementLocator) { Priority = extractedPriority };
            }
            // acceptFragile=true（用户选 (f) 后下轮）：接受脆弱，falls through 到阶段4/5/6 落盘 + onError=ai-fallback-stop 兜底（needsFallback 已由 ExtractAsync priority>=7 设）
        }

        // ========== 阶段 4：验证定位能力（不执行操作，不改变页面状态） ==========
        string? validatedSelector = null;

        if (candidateSelectors?.Count > 0 && !string.IsNullOrEmpty(refValue))
        {
            // 有 ref 时：在正确 frame 内依次验证候选 selector 的定位能力
            var frame = ResolveFrame(page, iframeChain);
            foreach (var candidate in candidateSelectors)
            {
                try
                {
                    var loc = frame.Locator(candidate);
                    var matchCount = await loc.CountAsync();
                    // 唯一性优先：Count==1 直接接受（不要求 IsVisibleAsync——合法隐藏元素如折叠面板/隐藏 tab 内输入框会被误杀）
                    if (matchCount == 1)
                    {
                        validatedSelector = candidate;
                        break;
                    }
                    // S4b useLast 放宽（改动3 路径1）：AI 明确传 useLast=true（任务描述说"最后一个/最新一条"）时，
                    // 多匹配也接受（回放时 useLast 取最后一个元素），不当作 needsAiAction 失败
                    if (matchCount > 1 && step.UseLast == true)
                    {
                        validatedSelector = candidate;
                        break;
                    }
                }
                catch (Exception ex) { _logger.LogWarning(ex, $"候选 selector 验证失败，尝试下一个候选: {candidate}"); }
            }
        }
        else
        {
            // 无 ref 时：使用提取的最优 selector
            validatedSelector = extractedSelector ?? selector;
        }

        // ========== 阶段 5：执行操作（只执行一次） ==========
        if (validatedSelector != null)
        {
            try
            {
                var frame = ResolveFrame(page, iframeChain);
                var fieldDef = ResolveRecordingFieldDef(step.Field, script, args);
                await ExecuteActionAsync(page, frame, action, validatedSelector, value, step, ct, fieldDef, loopSource);
                extractedSelector = validatedSelector;
            }
            catch (Exception ex)
            {
                return new OperateResult(false, $"操作执行失败: {ex.Message}", extractedSelector, iframeChain);
            }
        }
        else if (!string.IsNullOrEmpty(refValue))
        {
            // 候选 selector 全部验证失败 → 不再静默用 aria-ref 兜底，返回 failure 问用户
            var candidatesInfo = candidateSelectors != null && candidateSelectors.Count > 0
                ? string.Join("\n", candidateSelectors.Select(s => $"  {s}"))
                : "  (无候选)";

            var message = $"操作 [{action}] \"{step.Description}\" 的候选 selector 全部验证失败（可能匹配多个或零个元素）。\n\n" +
                          $"候选 selector：\n{candidatesInfo}\n\n" +
                          $"请选择处理方式：\n" +
                          $"(a) 描述元素特征或提供更精确的 selector\n" +
                          $"(b) 若意图是操作最后一个匹配元素，重新调 operate 加 useLast=true（多匹配时回放取最后）\n" +
                          $"(c) 同意保存为 ai action（回放时由 AI 处理）\n" +
                          $"(d) 跳过此步骤";

            return new OperateResult(false, message, extractedSelector, iframeChain);
        }
        else
        {
            return new OperateResult(false, "无法定位元素", null, iframeChain);
        }

        // ========== 阶段 6：参数化 + 保存 ==========
        var fieldName = args.GetValueOrDefault("fieldName")?.ToString();
        if ((action == "fill" || action == "select") && fieldName != null && value != null)
            value = $"{{{{{fieldName}}}}}";

        if (action == "click" && fieldName != null && extractedSelector != null
            && extractedSelector.StartsWith("//") && (extractedSelector.Contains("normalize-space") || extractedSelector.Contains("contains(.")))
        {
            // 精确文本：normalize-space(.)='xxx'
            var match = System.Text.RegularExpressions.Regex.Match(extractedSelector,
                @"normalize-space\(\.\)\s*=\s*['""]([^'""]*)['""]");
            if (match.Success)
            {
                extractedSelector = extractedSelector.Substring(0, match.Index)
                    + $"normalize-space(.)='{{{{{fieldName}}}}}'"
                    + extractedSelector.Substring(match.Index + match.Length);
            }
            else
            {
                // 文本锚点（阶段三/四）产出的 selector 含 contains(.,'xxx')：门控已放宽让其进入本块参数化。
                // 先匹配精确 normalize-space，未命中再匹配 contains，两者不会共存于同一 selector。
                match = System.Text.RegularExpressions.Regex.Match(extractedSelector,
                    @"contains\(\.\s*,\s*['""]([^'""]*)['""]\)");
                if (match.Success)
                {
                    extractedSelector = extractedSelector.Substring(0, match.Index)
                        + $"contains(.,'{{{{{fieldName}}}}}')"
                        + extractedSelector.Substring(match.Index + match.Length);
                }
            }
        }

        // 决策 D3/F①：loop phase 内 + AI 传 rowIndexed=true → 多独立 input（明细每行各一框）selector 行索引参数化。
        // 对齐回放 ScriptEngine loop（ctx.Vars["rowIndex"]=rowIdx+offset，{{rowIndex}} 经 ReplaceVars 替换）。
        // 多模式：属性优先（@rowindex/@data-row/@data-index）→ 回退位置（tr[N]）；多[数字]=行列分不清 → request_help 兜底（AI 看 OperateResult(false) 调 request_help）。
        if (loopSource != null && RecordingEngine.GetBool(args, "rowIndexed") == true && extractedSelector != null)
        {
            var mode = args.GetValueOrDefault("rowIndexMode")?.ToString();
            var (parametrized, ambiguous) = ParametrizeRowIndex(extractedSelector, mode);
            if (ambiguous)
                return new OperateResult(false, "selector 有多个行索引候选，无法确定行索引位置。请 request_help 确认明细行的行索引定位（@rowindex 属性 / tr 位置 / 其他）", extractedSelector, iframeChain);
            extractedSelector = parametrized;
            // iframe 变量决策：loop 内对 iframe 链每层做同款 rowIndex 参数化（与 selector 完全一致）。
            // 不参数化 fieldName（业务字段值只进 step.Selector/Value，永远不进 iframe 链——职责分离）。
            if (iframeChain != null)
            {
                var newChain = new string[iframeChain.Length];
                for (int i = 0; i < iframeChain.Length; i++)
                {
                    var (pChain, ambChain) = ParametrizeRowIndex(iframeChain[i], mode);
                    if (ambChain)
                        return new OperateResult(false, $"iframe 链第 {i + 1} 层「{iframeChain[i]}」有多个行索引候选，无法确定行索引位置。请 request_help 确认 iframe 的行索引定位", extractedSelector, iframeChain);
                    newChain[i] = pChain;
                }
                iframeChain = newChain;
            }
        }

        // R2-B（a-a-4 批次 E）：finalStep 一处判 iframeChain 脆弱（>=8）设 onError=ai-fallback-stop（统一 (a) 反推链 + (b) AI 链两路径，删阶段0 单独设；IsNullOrEmpty 守卫保留 caller 已设的 R2-C detect③ onError）。
        bool iframeFragile = iframeChain is { Length: > 0 } && iframeChain.Any(s => SelectorExtractor.AssessFragility(s) >= 8);
        var finalStep = step with
        {
            Selector = extractedSelector, Value = value, Iframe = iframeChain,
            // #10 + R2-B：脆弱 selector 或 iframe 链 → 自动设 onError=ai-fallback-stop
            OnError = (needsFallback || iframeFragile) && string.IsNullOrEmpty(step.OnError) ? "ai-fallback-stop" : step.OnError,
        };
        steps.Add(finalStep);

        var finalMessage = $"已执行并保存步骤: {action} {extractedSelector}";
        if (needsFallback)
        {
            var priorityDesc = extractedSelector != null
                ? (extractedSelector.Contains("contains(@")
                    ? "阶段五 属性包含匹配"
                    : extractedSelector.StartsWith("/html/")
                        ? "阶段六 完整路径"
                        : extractedSelector.Contains("contains(.,")
                            ? "阶段三/四 文本锚点"
                            : "脆弱选择器")
                : "脆弱选择器";
            finalMessage += $"\n\n⚠️ 注意：此 selector 较脆弱（{priorityDesc}），" +
                           $"DOM 结构微调可能导致回放失败。\n" +
                           $"已自动设置 onError=ai-fallback-stop。\n" +
                           $"如果你能找到更稳定的 selector，建议调 get_snapshot 后重试。";
        }

        return new OperateResult(true, finalMessage, extractedSelector, iframeChain, elementLocator) { Priority = extractedPriority };
    }

    /// <summary>
    /// 保存非交互步骤（save_step 工具）
    /// </summary>
    public SaveStepResult ExecuteSaveStep(StepNode step, List<PhaseItem> steps)
    {
        steps.Add(step);
        return new SaveStepResult(true, $"已保存步骤: [{step.Action}] {step.Description}");
    }

    /// <summary>
    /// 截图查看
    /// </summary>
    public async Task<(byte[] Screenshot, string Message)> ExecuteScreenshotAsync(IPage page, string? selector, int quality = 70)
    {
        var screenshot = await _snapshotExtractor.ScreenshotElementAsync(page, selector, quality);
        return (screenshot, $"[截图已返回，{screenshot.Length} bytes]");
    }

    /// <summary>
    /// 获取页面快照
    /// </summary>
    public async Task<string> ExecuteGetSnapshotAsync(IPage page, string[]? iframeChain = null)
    {
        return await _snapshotExtractor.GetSnapshotAsync(page, iframeChain);
    }

    /// <summary>
    /// 执行单个 Playwright 操作
    /// </summary>
    private async Task ExecuteActionAsync(IPage page, ILocator frame, string action, string? selector, string? value, StepNode step, CancellationToken ct, FieldDefinition? fieldDef = null, string? loopSource = null)
    {
        // 批次6：dialog handler 注册（对齐回放 StepExecutor L48-66；录制不收集 _lastDialogMessage，AcceptAsync/DismissAsync fire-and-forget）
        EventHandler<IDialog>? dialogHandler = null;
        if (step.PreSetup?.DialogHandler != null)
        {
            dialogHandler = (_, dialog) =>
            {
                if (step.PreSetup.DialogText == null || dialog.Message.Contains(step.PreSetup.DialogText))
                {
                    if (step.PreSetup.DialogHandler == "auto_accept") dialog.AcceptAsync();
                    else dialog.DismissAsync();
                }
                else dialog.AcceptAsync();  // Y5 兜底：dialogText 不匹配时 accept 防对话框挂起阻塞后续操作
            };
            page.Dialog += dialogHandler;
        }
        var loc = !string.IsNullOrEmpty(selector) ? frame.Locator(selector) : null;
        try
        {
        switch (action)
        {
            case "click":
                {
                    // 批次2：对齐回放 StepExecutor（useLast 取最后 / doubleClick 双击）
                    var target = step.UseLast == true ? loc!.Last : loc!.First;
                    if (step.DoubleClick == true)
                        await target.DblClickAsync();
                    else
                        await target.ClickAsync();
                    break;
                }
            case "fill":
                {
                    // 批次4：对齐回放——ApplyTransform/ApplyFormat（format 依赖 Type；首次录制 Type 缺失由 ResolveFieldTypeAsync 现场从 DOM 补，重大1 修法B）
                    var rawValue = ResolveSystemVars(value);  // 录制语义：仅系统凭据，业务变量保留占位符（不改用 ReplaceVars）
                    var ft = await ResolveFieldTypeAsync(loc, fieldDef);
                    var fillValue = VariableHelper.ApplyFormat(VariableHelper.ApplyTransform(rawValue, fieldDef?.Transform), fieldDef?.Format, ft);
                    await loc!.FillAsync(fillValue);
                    if (step.PressEnter == true) await loc!.PressAsync("Enter");
                    break;
                }
            case "type":
                {
                    // 批次4：同 fill，FillAsync 换 PressSequentiallyAsync（对齐 StepExecutor）
                    var rawValue = ResolveSystemVars(value);
                    var ft = await ResolveFieldTypeAsync(loc, fieldDef);
                    var typeValue = VariableHelper.ApplyFormat(VariableHelper.ApplyTransform(rawValue, fieldDef?.Transform), fieldDef?.Format, ft);
                    await loc!.PressSequentiallyAsync(typeValue);
                    if (step.PressEnter == true) await loc!.PressAsync("Enter");
                    break;
                }
            case "select":
                {
                    // 批次5：对齐回放 StepExecutor（matchBy 三分支 + transform/format；不照搬 storeAs/多选守卫）
                    var rawValue = ResolveSystemVars(value);
                    var sv = VariableHelper.ApplyTransform(rawValue, fieldDef?.Transform);
                    if (step.MatchBy is null or "value")
                    {
                        var ft = await ResolveFieldTypeAsync(loc, fieldDef);  // 重大1 修法B：matchBy=value + date/number format 依赖 Type
                        sv = VariableHelper.ApplyFormat(sv, fieldDef?.Format, ft);
                    }
                    switch (step.MatchBy)
                    {
                        case "label": await loc!.SelectOptionAsync(new[] { new SelectOptionValue { Label = sv } }); break;
                        case "index":
                            // matchBy=index 解析失败抛错（对齐回放），被 ExecuteOperateAsync catch 接住 → OperateResult(false) 让 AI 重试
                            if (!int.TryParse(sv, out var idx))
                                throw new ArgumentException($"select matchBy=index 需要整数，实际: '{sv}'");
                            await loc!.SelectOptionAsync(new[] { new SelectOptionValue { Index = idx } }); break;
                        default: await loc!.SelectOptionAsync(new[] { sv }); break;
                    }
                    break;
                }
            case "hover": await loc!.HoverAsync(); break;
            case "pressKey":
                {
                    // 批次2：对齐回放 StepExecutor——有 selector → 元素级 loc.PressAsync；无 selector → 全局键盘（外层 L57 分流可达）
                    var key = step.Key ?? "Enter";
                    if (loc != null)
                        await loc.PressAsync(key);
                    else
                        await page.Keyboard.PressAsync(key);
                    break;
                }
            case "scroll":
                await ExecuteScrollAsync(page, loc, step);
                break;
            case "upload":
                {
                    // 批次7-A1 决策A：执行层用注入的 _attachments（AI 传 fieldName 关联附件字段；value 不再作 path）
                    if (_attachments == null || _attachments.Count == 0)
                        throw new ArgumentException("upload 需要附件：请在录制前通过前端上传附件文件");
                    var files = _attachments
                        .Select(a => ResolveAttachmentPath(a.Path))
                        .Where(p => !string.IsNullOrEmpty(p))
                        .ToArray();
                    if (files.Length == 0)
                        throw new ArgumentException("附件路径均为空，无法上传");
                    // 批次7-loop：loopSource=attachments（顶层附件 loop）模拟回放 loop——逐个 SetInputFiles 触发 JS change 累积（JS 附件列表场景，同 input）；
                    //             非 loop/单附件：一次 SetInputFiles（multiple input 场景）。适用边界决策①：仅顶层 loopSource=attachments；明细附件录模板不验证（①a）。
                    if (loopSource == "attachments" && files.Length > 1)
                    {
                        foreach (var f in files)
                            await loc!.SetInputFilesAsync(new[] { f });
                    }
                    else
                    {
                        await loc!.SetInputFilesAsync(files);
                    }
                    break;
                }
            case "navigate":
                if (string.IsNullOrWhiteSpace(value))
                    throw new ArgumentException("navigate 操作需要提供 URL（通过 url 参数）");
                await page.GotoAsync(value);
                break;
            case "goBack": await page.GoBackAsync(); break;
            case "reload": await page.ReloadAsync(); break;
            case "closeTab":
                var pages = page.Context.Pages;
                // 对齐回放 StepExecutor.ExecuteCloseTabAsync：有 index 关指定 tab、无 index 关当前页（原写死 pages[^1] 与回放不一致，附录 AJ 修）
                if (step.Index.HasValue)
                {
                    if (step.Index.Value >= 0 && step.Index.Value < pages.Count)
                        await pages[step.Index.Value].CloseAsync();
                    else
                        throw new InvalidOperationException($"closeTab 索引 {step.Index.Value} 越界（共 {pages.Count} 个标签页，用 get_snapshot 查看所有标签页）");  // 越界报失败让 AI 重试（对齐 switchTab L73）；回放 L494-495 告警跳过是回放容错，录制该反馈
                }
                else
                {
                    await page.CloseAsync();
                }
                break;
            default:
                throw new NotSupportedException($"录制期未知 action: {action}");  // R3-5：未知 action 抛错（原静默跳过+返回成功；operate 工具 enum 已约束故低危，防御性）
        }
        // 操作后等待：让页面动画/异步渲染稳定（原硬编码 500ms，现可配置 RecordActionDelay；选C：改读 AppOptions）
        await Task.Delay(_appOptions.RecordActionDelay, ct);
        }
        finally
        {
            // 批次6：每次操作后注销 handler（不跨操作泄漏；Delay 在 try 内 cover 延迟弹的 dialog）
            if (dialogHandler != null) page.Dialog -= dialogHandler;
        }
    }

    /// <summary>
    /// 滚动执行：有 selector 时滚动到元素可见；否则按 direction/amount 页面滚动。
    /// direction: up/down/left/right/bottom/top（direction 模式必填，二选一校验后非空），amount 默认 300；bottom/top 忽略 amount 直达底/顶。
    /// </summary>
    private static async Task ExecuteScrollAsync(IPage page, ILocator? loc, StepNode step)
    {
        if (loc != null)
        {
            await loc.ScrollIntoViewIfNeededAsync();
            return;
        }
        // 批次9②：direction 必非空（ExecuteOperateAsync ① 分流已校验二选一，direction 模式 selector=null→loc=null 走此分支）；非法 direction 抛错（去原 ?? "down" 兜底 + _ 默认向下）
        var direction = step.Direction!.ToLowerInvariant();
        var amount = step.Amount ?? 300;
        if (direction == "bottom" || direction == "top")
        {
            var target = direction == "bottom" ? "document.body.scrollHeight" : "0";
            await page.EvaluateAsync($"window.scrollTo(0, {target})");
            return;
        }
        var (dx, dy) = direction switch
        {
            "up" => (0, -amount),
            "down" => (0, amount),
            "left" => (-amount, 0),
            "right" => (amount, 0),
            _ => throw new ArgumentException($"scroll 非法 direction: '{direction}'（合法: up/down/left/right/bottom/top）"),
        };
        await page.Mouse.WheelAsync(dx, dy);
    }

    /// <summary>
    /// 从 DOM 元素自动提取字段属性
    /// </summary>
    public static async Task<FieldDefinition?> ExtractFieldFromElementAsync(
        string fieldName, string fieldLabel, ILocator? element)
    {
        if (element == null) return null;

        var def = new FieldDefinition { Name = fieldName, Label = fieldLabel };

        try
        {
            // Type：从 input[type] 推断
            var tagName = (await element.EvaluateAsync<string>("el => el.tagName"))?.ToLower();
            var inputType = (await element.EvaluateAsync<string>("el => el.type || ''"))?.ToLower();

            def = def with
            {
                // UiComponent：标签推断
                UiComponent = tagName switch
                {
                    "select" => "select",
                    "textarea" => "textarea",
                    "input" when inputType == "file" => "upload",
                    "input" when inputType == "checkbox" => "checkbox",
                    "input" when inputType == "radio" => "radio",
                    _ => "input"
                },
                // Type：input[type] 推断
                Type = inputType switch
                {
                    "number" => "number",
                    "date" or "datetime-local" => "date",
                    "file" => "file",
                    "checkbox" or "radio" => "boolean",
                    _ => "string"
                },
                // Required（4 层检测：HTML5 原生 → 自身伪元素 → 关联 label 伪元素 → 父容器 class）
                Required = await element.EvaluateAsync<bool>(@"el => {
    if (el.required === true || el.getAttribute('aria-required') === 'true') return true;
    function hasRequiredPseudo(e) {
        try {
            for (var p of ['::before','::after']) {
                var s = getComputedStyle(e, p);
                var c = s.content || '';
                if (c === 'none' || c === 'normal') continue;
                if (/[*＊必填]|required/i.test(c)) return true;
            }
        } catch(x) {}
        return false;
    }
    if (hasRequiredPseudo(el)) return true;
    var labels = el.id ? Array.from(document.querySelectorAll('label')).filter(function(l){ return l.htmlFor === el.id; }) : [];
    if (labels.length === 0) { var p = el.parentElement; if (p && p.tagName === 'LABEL') labels = [p]; }
    if (labels.length === 0) { var c = el.closest('.ant-form-item,.el-form-item,.van-field,.a-form-item,.ivu-form-item,.mu-form-item,.layui-form-item'); if (c) labels = c.querySelectorAll('label'); }
    for (var lbl of labels) { if (hasRequiredPseudo(lbl)) return true; }
    var fi = el.closest('.ant-form-item,.el-form-item,.van-field,.a-form-item,.ivu-form-item,.mu-form-item,.layui-form-item,[class*=required],[class*=Required]');
    if (fi && /(^|\s)(ant-form-item-required|is-required|van-field--required|a-form-item-required|form-item-required|layui-form-required|required)(\s|$)/i.test(fi.className)) return true;
    return false;
}")
                    ? true : null,
                // Placeholder
                Placeholder = await element.EvaluateAsync<string>("el => el.placeholder || ''") is string ph and not "" ? ph : null,
                // MaxLength
                MaxLength = await element.EvaluateAsync<int?>("el => el.maxLength > 0 ? el.maxLength : null"),
                // Multiple
                Multiple = await element.EvaluateAsync<bool>("el => el.multiple === true") ? true : null,
                // Min / Max（number/date）
                Min = await element.EvaluateAsync<double?>("el => { var v = el.min; return v ? parseFloat(v) : null; }"),
                Max = await element.EvaluateAsync<double?>("el => { var v = el.max; return v ? parseFloat(v) : null; }"),
            };

            // Options：原生 select 的 option 列表
            if (tagName == "select")
            {
                var opts = await element.EvaluateAsync<List<string>>(
                    "el => Array.from(el.options).map(o => o.textContent.trim()).filter(t => t)");
                if (opts?.Count > 0) def = def with { Options = opts };
            }
        }
        catch { /* DOM 提取失败不影响录制 */ }

        return def;
    }

    /// <summary>
    /// 解析 iframe selector 链上下文（形态 A：复用 FrameResolver.ResolveChain，录制端用不替换 overload——chain 无 {{}}）。
    /// </summary>
    internal static ILocator ResolveFrame(IPage page, string[]? chain)
    {
        return FrameResolver.ResolveChain(page, chain);
    }

    // ===== 附录 R：detect 同源 operate 的能力暴露（selector 提取 / iframe 检测 / selector 校验）=====

    /// <summary>
    /// detect selector 提取（与 operate 阶段1+3 同源）：给定 ref，用 aria-ref 定位元素 + SelectorExtractor 提取最优 XPath。
    /// Y1（V1 延伸）：保留 priority 信号（原丢 result.Priority，save_step priority 8→L647 通道无信号源）。
    /// 形态 A：iframeId(string) → chain(string[]?)。
    /// </summary>
    public async Task<(string? Selector, int Priority, string? Error)> ExtractSelectorFromRefAsync(IPage page, string refValue, string[]? chain)
    {
        try
        {
            var locator = page.Locator($"aria-ref={refValue}");
            if (await locator.CountAsync() == 0)
                return (null, 0, $"aria-ref={refValue} 未找到元素，可能页面已变化或快照过期。建议 get_snapshot 获取最新快照后用新 ref 重试");
            var frame = ResolveFrame(page, chain);
            var result = await _selectorExtractor.ExtractAsync(locator, frame);
            if (string.IsNullOrEmpty(result.Selector))
                return (null, 0, $"aria-ref={refValue} 7 阶段提取均未能生成稳定 selector。建议 get_snapshot 后换 ref，或改用 page_contains/url_changed");
            return (result.Selector, result.Priority, null);
        }
        catch (Exception ex)
        {
            return (null, 0, $"aria-ref 定位/提取失败: {ex.Message}");
        }
    }

    /// <summary>
    /// detect iframe 按目标反推（与 operate 阶段0 同源）：按 ref/selector/keywords 在各 frame 反推目标所在 frame，返回 selector 链。
    /// 薄封装透传 warning（N3，不丢）；形态 A：签名 (string[]? chain, bool inferred, string? warning)，无 script。
    /// </summary>
    public async Task<(string[]? Chain, bool Inferred, string? Warning, bool IsFragileLayer, IReadOnlyList<MultiFrameHit>? MultiFrames, string? Ctx)> FindFrameForTargetAsync(IPage page, FrameTarget target)
    {
        try
        {
            return await _iframeDetector.FindFrameForTargetAsync(page, target);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "iframe 反推失败（detect 路径），跳过");
            return (null, false, $"⚠️ iframe 反推异常: {ex.Message}", false, null, null);
        }
    }

    /// <summary>
    /// 结论14（a-a-4 步骤3）：用 aria-ref 指认 iframe 元素 → 提取该 iframe 到 MainFrame 的 selector 链。
    /// 薄封装透传 _iframeDetector.ResolveIframeFromRefAsync（与 FindFrameForTargetAsync wrapper 同款异常兜底；error 非空时 caller 走 ref 失效兜底不 silent）。
    /// </summary>
    public async Task<(string[]? Chain, string? Error)> ResolveIframeFromRefAsync(IPage page, string refValue)
    {
        try
        {
            return await _iframeDetector.ResolveIframeFromRefAsync(page, refValue);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"iframeRef={refValue} 反推失败，跳过");
            return (null, $"⚠️ iframeRef 反推异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 2a 统一校验关卡（阶段2a，提前到阶段1）：校验单 selector 在 frame 内的定位态。
    /// 返回 (Unique/Multiple(count, useLast 放宽)/Empty)。从 operate 候选验证抽取（行为不变，纯重构）。
    /// </summary>
    public static async Task<SelectorValidation> ValidateSelectorAsync(IPage page, ILocator frame, string selector, bool useLast)
    {
        var count = await frame.Locator(selector).CountAsync();
        if (count == 1) return SelectorValidation.Unique;
        if (count == 0) return SelectorValidation.Empty;
        // count > 1
        return useLast ? SelectorValidation.UniqueUseLast : SelectorValidation.Multiple(count);
    }

    /// <summary>
    /// 2c（G7.2 改动3 / B9）：save_step / captcha / fallback 路径 selector 校验薄封装——复用 ValidateSelectorAsync + ResolveFrame。
    /// save_step 是非交互步骤（不执行操作），但 screenshot/extract/evaluate/ai/captcha 需 selector 定位元素，
    /// 回放 Playwright strict mode 多匹配抛错 → 录制期挡住。变量 selector（含 {{}}）不校验（运行时才解析）。
    /// 返回 (valid, warning)：Unique→(true,null)；Empty/Multiple→(false, 具体反馈)。
    /// </summary>
    public static async Task<(bool Valid, string? Warning)> ValidateSaveStepSelectorAsync(IPage page, string[]? iframeChain, string selector, bool useLast = false)
    {
        if (string.IsNullOrEmpty(selector) || selector.Contains("{{")) return (true, null);
        try
        {
            var frame = ResolveFrame(page, iframeChain);
            var v = await ValidateSelectorAsync(page, frame, selector, useLast);
            if (v.IsUnique) return (true, null);
            var iframeDesc = iframeChain is { Length: > 0 } ic ? $"，iframe 链={string.Join(" > ", ic)}" : "";
            if (v.Count == 0) return (false, $"❌ selector「{selector}」找不到元素{iframeDesc}。请：(a) 用 ref 指认元素让代码提取真实 selector；(b) 检查 iframe 链；(c) request_help");
            return (false, $"❌ selector「{selector}」匹配 {v.Count} 个元素（多匹配，回放 strict mode 会抛错）{iframeDesc}。请：(a) 用 ref 指认让代码提取唯一 selector；(b) 提供更精确 selector；(c) request_help");
        }
        catch (Exception)
        {
            return (false, $"❌ selector「{selector}」校验异常（iframe 链={string.Join(" > ", iframeChain ?? [])}）");  // 兜底：ResolveFrame/CountAsync 异常不崩
        }
    }

    /// <summary>
    /// 形态 A：校验 iframe selector 链（逐层串联：每层在父文档唯一定位 iframe 元素）。
    /// 用于 operate 阶段0/save_step 优先级0 校验 AI 传的链。返回 (valid, warning)。
    /// </summary>
    public static async Task<(bool Valid, string? Warning)> ValidateIframeChainAsync(IPage page, string[] chain)
    {
        ILocator locator = page.Locator("body");
        for (int i = 0; i < chain.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(chain[i]))
                return (false, $"iframe 链第 {i + 1} 层为空字符串");
            int count;
            try { count = await locator.Locator(chain[i]).CountAsync(); }
            catch (Exception ex) { return (false, $"iframe 链第 {i + 1} 层「{chain[i]}」校验异常: {ex.Message}"); }
            if (count == 0)
                return (false, $"iframe 链第 {i + 1} 层「{chain[i]}」在父文档找不到 iframe 元素");
            if (count > 1)
                return (false, $"iframe 链第 {i + 1} 层「{chain[i]}」在父文档匹配 {count} 个 iframe 元素（非唯一）");
            locator = locator.FrameLocator(chain[i]).Locator("body");
        }
        return (true, null);
    }

    /// <summary>
    /// detect selector 存在性校验（与 operate 阶段4 同源）：校验 selector 在指定链能定位到元素。
    /// </summary>
    public async Task<bool> SelectorExistsAsync(IPage page, string selector, string[]? chain)
    {
        try
        {
            var frame = ResolveFrame(page, chain);
            return await frame.Locator(selector).CountAsync() > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>2a 统一校验结果。</summary>
    public record SelectorValidation
    {
        public bool IsUnique { get; init; }
        public int Count { get; init; }
        public bool UseLast { get; init; }
        public static readonly SelectorValidation Unique = new() { IsUnique = true, Count = 1 };
        public static readonly SelectorValidation UniqueUseLast = new() { IsUnique = true, Count = 1, UseLast = true };
        public static SelectorValidation Multiple(int count) => new() { IsUnique = false, Count = count };
        public static readonly SelectorValidation Empty = new() { IsUnique = false, Count = 0 };
    }

    public record OperateResult(bool Success, string Message, string? Selector, string[]? IframeChain, ILocator? Element = null, IPage? NewPage = null, int? Priority = null, bool NeedsAiExecution = false, bool IsMultiMatch = false,
        string? IframeWarning = null, bool IsFragileIframe = false, string? HtmlContext = null, IReadOnlyList<MultiFrameHit>? MultiFrames = null, bool IsCountZero = false, string? IframeCtx = null);  // DC3 IsFragileIframe 结构化（脆弱层，结论13 C1）+ T11 HtmlContext（priority 9 元素 HTML）+ ND8 MultiFrames（多 frame (j)）+ ND7 IsCountZero（count==0 求助给 (h) acceptAsIs）+ 放法X IframeCtx（iframe 脆弱层 ctx 全量，选 (l) 喂 AI；R2-细化 A）
    public record SaveStepResult(bool Success, string Message, string? Selector = null, int? Priority = null);  // T12（a-a-4）：save_step iframe 求助（fragile/countZero/multiFrame）在 ExecuteSaveStep 前用局部变量处理 + 早 return，不经本 result。原拟对齐 OperateResult 的 IframeWarning/IsFragileIframe 字段经五轮核查为 dead field（无写入/读取点），已删（2026-07-09）
}
