using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using SmartFilling.Engine.Ai;
using SmartFilling.Engine.Logging;
using SmartFilling.Engine.Models;
using SmartFilling.Engine.Reporting;
using SmartFilling.Engine.Services;

namespace SmartFilling.Engine.Engine;

public class ScriptEngine
{
    private readonly ILogger _logger;
    private readonly EngineOptions _options;
    private readonly StepExecutor _stepExecutor;
    private readonly DetectEvaluator _detectEvaluator;
    private readonly AiActionExecutor? _aiActionExecutor;
    private readonly ITaskProgressReporter? _progressReporter;

    public ScriptEngine(ILogger logger, EngineOptions options, Services.CaptchaService? captchaService = null)
    {
        _logger = logger;
        _options = options;
        _detectEvaluator = new DetectEvaluator(logger);
        _stepExecutor = new StepExecutor(_detectEvaluator, options, logger, captchaService);
    }

    public ScriptEngine(ILogger logger, EngineOptions options, IAiProvider aiProvider, Services.CaptchaService? captchaService = null)
        : this(logger, options, captchaService)
    {
        // #4：无 reporter 场景，AiActionExecutor/StepExecutor 的 reporter 传 null（_logger 仍启用，文件日志生效）
        _aiActionExecutor = new AiActionExecutor(aiProvider, logger, null, _options.Compression);
        _stepExecutor = new StepExecutor(_detectEvaluator, options, logger, _aiActionExecutor, null, captchaService);
    }

    public ScriptEngine(ILogger logger, EngineOptions options, IAiProvider aiProvider, ITaskProgressReporter progressReporter, Services.CaptchaService? captchaService = null)
        : this(logger, options, captchaService)  // #4 改：委托基构造（不委托上方 4 参带 AI 构造——否则 AiActionExecutor 在 reporter 注入前已创建，拿不到 reporter）
    {
        _progressReporter = progressReporter;
        // #4：带 reporter 场景，AiActionExecutor/StepExecutor 注入 reporter（可观测性：每轮工具日志推前端 / ai action 失败推前端）
        _aiActionExecutor = new AiActionExecutor(aiProvider, logger, progressReporter, _options.Compression);
        _stepExecutor = new StepExecutor(_detectEvaluator, options, logger, _aiActionExecutor, progressReporter, captchaService);
    }

    public ScriptEngine(ILogger logger, EngineOptions options, ITaskProgressReporter progressReporter, Services.CaptchaService? captchaService = null)
        : this(logger, options, captchaService)
    {
        _progressReporter = progressReporter;
    }

    /// <summary>
    /// 执行脚本主入口。浏览器由调用方管理（Worker 的 ScriptEngineRunner 或 App 的 RecordingEngine）。
    /// </summary>
    public async Task<ScriptResult> ExecuteAsync(
        ScriptV2 script,
        Dictionary<string, object> fillData,
        IPage page,
        CancellationToken ct = default,
        string? taskId = null)
    {
        // 应用 viewport
        var vp = script.Settings?.Viewport ?? _options.Viewport;
        if (vp != null)
            await page.SetViewportSizeAsync(vp.Width, vp.Height);

        // 超时令牌
        var maxDuration = script.Settings?.MaxScriptDuration ?? _options.MaxScriptDuration;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(maxDuration);

        var ctx = new ExecutionContext { Page = page, ActivePage = page, Ct = timeoutCts.Token, TaskId = taskId };
        ctx.ScriptId = script.ScriptId;
        // Q4：入口深度归一 fillData（含嵌套），无论 HTTP/WS 哪个入口传入，下游只见 List<object>/Dictionary/标量。
        // ② 消除 WS string-JSON 源头后本归一兜底；对已归一数据幂等穿过（修订1：无 JsonElement 的 Dictionary 原样返回不复制）。
        NormalizeFillData(fillData);
        ctx.ScopeChain.Add(fillData);
        ctx.Vars["_lastUrl"] = page.Url;
        ctx.GoalStack.Push(script.Description ?? script.Name);  // 改动6.4：目标链栈底=脚本整体目标

        var totalSw = Stopwatch.StartNew();
        bool scriptFailed = false;
        string? lastErrorMessage = null;  // S5 message 透传：记录失败 phase 的 Message，透传到 ScriptResult（原 L157 硬编码丢失）
        try
        {
            for (int i = 0; i < script.Phases.Count; i++)
            {
                ctx.Ct.ThrowIfCancellationRequested();
                var item = script.Phases[i];

                if (item is not PhaseNode phase) continue;

                // phase 条件检测
                if (phase.Condition != null)
                {
                    bool met;
                    try { met = await _detectEvaluator.EvaluateAsync(phase.Condition, ctx.ActivePage, script, phase.Iframe, ctx.Vars, ctx.ScopeChain); }
                    catch (Exception ex) { _logger.LogWarning($"Phase {phase.Name} 条件检测失败: {ex.Message}"); met = false; }
                    if (!met)
                    {
                        _logger.LogInformation($"Phase 跳过 | phase={phase.Name} 条件不满足 → 跳过 ({i + 1}/{script.Phases.Count})");
                        continue;
                    }
                }

                PhaseResult? r;
                if (phase.Type == "loop")
                    r = await ExecuteLoopPhaseAsync(ctx, script, i, phase, fillData);
                else if (phase.Type == "ai")
                    r = await ExecuteAiPhaseAsync(ctx, script, i, phase, fillData);
                else
                    r = await ExecutePhaseAsync(ctx, script, i, phase, fillData);

                if (r?.Success == false)
                {
                    // 观察②修复（2026-07-06）：ai phase 主路径失败统一标记 → FailureType 推断为 AiPhase
                    //（原落 Deterministic，与枚举注释"未触发 AI"矛盾——ai phase 明确触发了 AI 主路径）
                    if (phase.Type == "ai") ctx.AiPhaseFailed = true;
                    // #54：script_fail 直接终止脚本（不走 phase.onError，避免被 phase ai-fallback 误修复）；与 script_success 对称
                    if (r?.Status == "script_fail")
                    {
                        lastErrorMessage = r?.Message; scriptFailed = true;
                        break;
                    }
                    var phaseOnError = GetOnError(phase, script);
                    if (phaseOnError is "ai-fallback-stop" or "ai-fallback-skip")
                    {
                        if (phase.Type == "ai")
                        {
                            // ai phase：自身已是 AI 多轮交互，降级为基础策略（stop/skip）
                            if (phaseOnError == "ai-fallback-skip") continue;
                            lastErrorMessage = r?.Message; scriptFailed = true;  // S5 phase 失败传播：原 break 漏设 scriptFailed → 脚本误判成功
                            break; // ai-fallback-stop → stop
                        }

                        // 非 ai phase：Phase 级 AI fallback
                        var aiResult = await PhaseAiFallbackAsync(ctx, script, phase, fillData, r);
                        if (aiResult?.Success == true)
                        {
                            ctx.CompletedPhases.Add(phase.AiGoal ?? phase.Name);
                            continue;
                        }
                        // AI fallback 也失败
                        if (phaseOnError == "ai-fallback-stop")
                        {
                            lastErrorMessage = aiResult?.Message ?? r?.Message; scriptFailed = true;  // S5 phase 失败传播
                            break;
                        }
                        // ai-fallback-skip：跳过此 phase 继续
                        continue;
                    }
                    if (phaseOnError == "skip") continue;
                    lastErrorMessage = r?.Message; scriptFailed = true;  // S5 phase 失败传播
                    break; // stop
                }

                if (r?.Success != false)
                {
                    ctx.CompletedPhases.Add(phase.AiGoal ?? phase.Name);

                    if (r?.Type == "goto")
                    {
                        var loc = FindPhase(script.Phases, r.ToPhase);
                        if (loc != null) { i = loc.Path[0] - 1; continue; }
                    }

                    if (r?.Status == "script_success") break;
                    if (r?.Status == "script_fail") { scriptFailed = true; break; }
                }
            }

            if (scriptFailed)
            {
                var failMsg = lastErrorMessage ?? "脚本执行失败终止";
                ctx.Vars["lastError"] = failMsg;  // 改动5：失败写 lastError 进 vars，供脚本 returnData 声明 error: "{{lastError}}" 取值
                _logger.LogInformation($"脚本执行完成 | status=failed phases={ctx.CompletedPhases.Count}/{script.Phases.Count} 耗时={totalSw.ElapsedMilliseconds}ms 原因={failMsg}");
                await ReportFinalScreenshotAsync(ctx, false);
                // #2：不显式传 FailureType.Deterministic——让 BuildResult 据 ctx.AiFallbackFailed 推断
                // （step/phase AI 兜底失败设 ctx.AiFallbackFailed=true → AiFallback；确定性失败 → Deterministic）。
                // 原显式 Deterministic 覆盖了 ctx.AiFallbackFailed，致 FailureType 永远 Deterministic。
                return BuildResult(ctx, script, false, failMsg, totalSw);
            }

            _logger.LogInformation($"脚本执行完成 | status=completed phases={ctx.CompletedPhases.Count}/{script.Phases.Count} 耗时={totalSw.ElapsedMilliseconds}ms");
            await ReportFinalScreenshotAsync(ctx, true);
            return BuildResult(ctx, script, true, null, totalSw);
        }
        catch (OperationCanceledException)
        {
            var isTimeout = !ct.IsCancellationRequested;
            var msg = isTimeout ? "脚本执行超时" : "用户取消任务";
            ctx.Vars["lastError"] = msg;  // 改动5：失败写 lastError
            await ReportFinalScreenshotAsync(ctx, false);
            return BuildResult(ctx, script, false, msg, totalSw, isTimeout ? FailureType.Timeout : FailureType.Cancelled);
        }
        catch (Exception ex)
        {
            ctx.Vars["lastError"] = ex.Message;  // 改动5：失败写 lastError
            await ReportFinalScreenshotAsync(ctx, false);
            return BuildResult(ctx, script, false, ex.Message, totalSw, FailureType.Deterministic);
        }
    }

    /// <summary>Q4：递归归一 fillData 顶层各值中的 JsonElement（含嵌套）。</summary>
    private static void NormalizeFillData(Dictionary<string, object> fillData)
    {
        foreach (var key in fillData.Keys.ToList())
            fillData[key] = NormalizeValue(fillData[key]);
    }

    /// <summary>
    /// Q4 递归归一单个值：JsonElement→CLR；嵌套 Dictionary/List 递归；已归一原样穿过。
    /// 修订1：对无 JsonElement 的已归一 Dictionary 原样返回（不 new 复制，避免大 fillData 内存翻倍 + GC）。
    /// </summary>
    private static object NormalizeValue(object? v) => v switch
    {
        JsonElement je => VariableHelper.NormalizeJsonElement(je),                                         // JsonElement → CLR（NormalizeJsonElement 已递归）
        byte[] arr => arr,                                                                                  // 防御：byte[] 实现 IList，避免被拆成 List<object>
        IDictionary<string, object> dict when dict.Values.All(x => x is not JsonElement) => dict,           // 已归一原样返回（幂等不复制）
        IDictionary<string, object> dict => new Dictionary<string, object>(dict.Select(kv => new KeyValuePair<string, object>(kv.Key, NormalizeValue(kv.Value)))),
        System.Collections.IList list => list.Cast<object>().Select(NormalizeValue).ToList(),               // 嵌套数组：递归每项
        _ => v ?? ""                                                                                         // 标量（含已归一）/null
    };

    #region Phase 执行

    /// <summary>检查浏览器是否仍然健康（连接且页面未关闭）</summary>
    async Task EnsureBrowserHealthyAsync(ExecutionContext ctx)
    {
        if (ctx.Page.Context.Browser == null || !ctx.Page.Context.Browser.IsConnected)
            throw new InvalidOperationException("浏览器已断开连接");
        if (ctx.Page.IsClosed)
            throw new InvalidOperationException("页面已关闭");
    }

    /// <summary>检测是否为浏览器崩溃异常</summary>
    static bool IsBrowserCrashException(Exception ex)
    {
        var msg = ex.Message ?? "";
        return msg.Contains("closed", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("disconnected", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Target closed", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Browser closed", StringComparison.OrdinalIgnoreCase);
    }

    async Task<PhaseResult?> ExecutePhaseAsync(ExecutionContext ctx, ScriptV2 script, int phaseIdx, PhaseNode phase, Dictionary<string, object> fillData, int startStep = 0)
    {
        ctx.PhaseName = phase.Name;
        ctx.GoalStack.Push(phase.AiGoal ?? phase.Name);  // 改动6.4：目标链入口 push
        try {
        await EnsureBrowserHealthyAsync(ctx);
        var firstStart = startStep;   // ④-2：首次=startStep（goto toPhase/toStep 用），phase_rerun 重跑后归 0
        int phaseRerunCount = 0;       // ④-2：方法局部、跨重跑累加（嵌套子 phase 经新栈帧独立预算，仿 loop）

    SEQ_TOP:
        for (int j = firstStart; j < phase.Steps.Count; j++)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            var item = phase.Steps[j];

            // 嵌套 phase（保存/恢复 PhaseName，避免嵌套 phase 污染父 phase 的文件名）
            if (item is PhaseNode nestedPhase)
            {
                var savedPhaseName = ctx.PhaseName;
                var nestedResult = await ExecuteNestedPhaseAsync(ctx, script, j, nestedPhase, fillData);
                ctx.PhaseName = savedPhaseName;
                if (nestedResult?.Type == "goto")
                {
                    var targetIdx = phase.Steps.FindIndex(c => c is PhaseNode p && p.Name == nestedResult.ToPhase);
                    if (targetIdx >= 0) { j = targetIdx - 1; continue; }
                    return nestedResult; // 向上传递
                }
                if (nestedResult?.Status == "script_success" || nestedResult?.Status == "script_fail")
                    return nestedResult;
                continue;
            }

            // step 节点
            var step = (StepNode)item;
            if (!await ShouldExecuteStepAsync(ctx, script, phase, step)) continue;

            StepResult? result;
            try
            {
                result = await ExecuteStepWithRetryAndFallbackAsync(ctx, script, phase, phaseIdx, step, j, fillData);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // J.3.1 守卫：浏览器崩溃重新 throw（不构造 PhaseResult，否则崩溃触发错误的 AI fallback）
                if (IsBrowserCrashException(ex)) throw;
                // 步骤级 onError=stop 抛出异常 → 检查 phase 级 onError
                var phaseOnError = GetOnError(phase, script);
                if (phaseOnError == "skip")
                {
                    _logger.LogWarning($"Sequential phase step 失败跳过 | phase={phase.Name} step={step.Description} error={ex.Message}");
                    continue;
                }
                // H.3.2：构造诊断 PhaseResult（含 step 失败信息），让主循环按 phaseOnError 走 phase 级 fallback
                // （原 throw 直达顶层 catch → 脚本失败，sequential phase 无法触发 PhaseAiFallbackAsync）
                return new PhaseResult(false) { Message = ex.Message, FailedStepIndex = j };
            }
            if (step.StoreAs != null && result?.Value != null)
                VariableHelper.StoreVars(ctx.Vars, step.StoreAs, result.Value);

            // 累积 AI 统计（ai action 步骤）
            AccumulateAiStats(ctx, result);

            if (step.Action == "check" && result?.ControlFlow != null)
            {
                var cf = result.ControlFlow;
                // goto toStep: 在当前 phase 内修改循环变量
                if (cf.Then == "goto" && cf.ToPhase == null && cf.ToStep != null)
                {
                    var si = phase.Steps.FindIndex(c => c is StepNode s && s.Name == cf.ToStep);
                    if (si >= 0)
                    {
                        var stepGotoKey = $"step:{phase.Name}:{cf.ToStep}";
                        ctx.GotoCounter[stepGotoKey] = ctx.GotoCounter.GetValueOrDefault(stepGotoKey) + 1;
                        var maxGotoStep = script.Settings?.MaxPhaseGotoCount ?? _options.MaxPhaseGotoCount;
                        if (ctx.GotoCounter[stepGotoKey] > maxGotoStep)
                            throw new InvalidOperationException($"goto toStep 死循环: {cf.ToStep}（超过上限 {maxGotoStep}）");
                        j = si - 1;
                        continue;
                    }
                }
                if (cf.Then == "phase_rerun")
                {
                    // ④-2：sequential phase_rerun 就地处理。原委托 HandleControlFlowAsync 走递归 ExecutePhaseAsync，
                    // 每栈帧 r 归零、for 死代码、无全局上限——病态脚本（如 check 持续 true）自旋到 MaxScriptDuration 占满并发槽。
                    // 仿 loop phase_rerun：goto 键清理 + 方法局部计数 + goto SEQ_TOP（C# goto 不能跨方法，故搬回 ExecutePhaseAsync 内联）。
                    foreach (var gk in ctx.GotoCounter.Keys.Where(k => k.StartsWith($"step:{phase.Name}:") || k.StartsWith($"{phaseIdx}:")).ToList())
                        ctx.GotoCounter.Remove(gk);
                    phaseRerunCount++;
                    var maxPhaseRerun = script.Settings?.Rerun?.MaxPhaseRerunCount ?? _options.Rerun?.MaxPhaseRerunCount ?? 2;
                    if (phaseRerunCount > maxPhaseRerun)
                        return new PhaseResult(false) { FailedStepIndex = j, Message = $"超过最大 phase 重跑次数({maxPhaseRerun})" };
                    await Task.Delay(script.Settings?.Rerun?.Interval ?? _options.Rerun?.Interval ?? 1000, ctx.Ct);
                    firstStart = 0;   // 重跑整个 phase 从头（与原递归不传 startStep 一致）
                    goto SEQ_TOP;     // 必须跳回，不 fall-through 到 HandleControlFlowAsync（否则残留 case 二次处理）
                }
                var flowResult = await HandleControlFlowAsync(ctx, script, phase, phaseIdx, phase.Steps, j, cf, fillData);
                if (flowResult != null) return flowResult;
            }
        }
        return new PhaseResult(true);
        } finally { ctx.GoalStack.Pop(); }  // 改动6.4：目标链出口 pop（try-finally 覆盖所有 return/throw，防漏 pop）
    }

    async Task<PhaseResult?> ExecuteNestedPhaseAsync(ExecutionContext ctx, ScriptV2 script, int phaseIdx, PhaseNode phase, Dictionary<string, object> fillData)
    {
        if (phase.Condition != null)
        {
            bool met;
            try { met = await _detectEvaluator.EvaluateAsync(phase.Condition, ctx.ActivePage, script, phase.Iframe, ctx.Vars, ctx.ScopeChain); }
            catch (Exception ex) { _logger.LogWarning($"Phase {phase.Name} 条件检测失败: {ex.Message}"); met = false; }  // R2-D：不加 when（phase 跳过非失败，IframeChainBrokenException 不穿透致 phase 失败）+ 加日志保 observability
            if (!met) return null;
        }

        if (phase.Type == "loop")
            return await ExecuteLoopPhaseAsync(ctx, script, phaseIdx, phase, fillData);
        if (phase.Type == "ai")
            return await ExecuteAiPhaseAsync(ctx, script, phaseIdx, phase, fillData);
        return await ExecutePhaseAsync(ctx, script, phaseIdx, phase, fillData);
    }

    // ===== DC10/DC13（a-a-4 new_row_appears 加固）辅助方法 =====
    /// <summary>DC10：递归遍历 phase.Steps 的 check step，在 detect 树（all/any/not 嵌套）中找第一个 new_row_appears 节点。
    /// 补顶层 gap——原 FirstOrDefault 只找顶层 check.Detect.Type，嵌套 all/any/not 内的 new_row_appears 漏设基线 → DetectEvaluator return false silent 失效。</summary>
    private static DetectCondition? TryFindNewRowAppearsNode(List<PhaseItem> steps)
    {
        foreach (var step in steps.OfType<StepNode>())
        {
            if (step.Action == "check" && step.Detect != null)
            {
                var found = FindNewRowAppearsInTree(step.Detect);
                if (found != null) return found;
            }
            // D-loopCondition（a-a-4 选 a，2026-07-10）：补搜 step.Condition（check 守卫外，对所有 step 类型——fill/click 等的 Condition 也可能是 new_row_appears 基线源）。
            // 修复 loop phase step.Condition-only new_row_appears 基线=0 silent（TryFind 漏搜 condition → tableSel=null → Locator(null!) → catch baseRowCount=0 → step 恒执行）。
            if (step.Condition != null)
            {
                var found = FindNewRowAppearsInTree(step.Condition);
                if (found != null) return found;
            }
        }
        return null;
    }
    /// <summary>DC10 递归核心：深度优先找 new_row_appears 节点（all/any/not 嵌套均可达）。</summary>
    private static DetectCondition? FindNewRowAppearsInTree(DetectCondition? d)
    {
        if (d == null) return null;
        if (d.Type == "new_row_appears") return d;
        if (d.All != null) foreach (var c in d.All) { var f = FindNewRowAppearsInTree(c); if (f != null) return f; }
        if (d.Any != null) foreach (var c in d.Any) { var f = FindNewRowAppearsInTree(c); if (f != null) return f; }
        if (d.Not != null) return FindNewRowAppearsInTree(d.Not);
        return null;
    }
    /// <summary>DC13：Push 基线行数到 _lastRowCountStack（每行迭代开头，与 ScopeChain.Insert 同步）。嵌套 loop 各自独立栈层（Peek 取当前 loop 基线）。</summary>
    internal static void PushRowCountStack(ExecutionContext ctx, int baseRowCount)
    {
        if (ctx.Vars.TryGetValue("_lastRowCountStack", out var stkObj) && stkObj is Stack<int> stk)
            stk.Push(baseRowCount);
        else
            ctx.Vars["_lastRowCountStack"] = new Stack<int>(new[] { baseRowCount });
    }
    /// <summary>DC13：Pop 栈顶基线（每行迭代结束/出口，与 ScopeChain.RemoveAt 同步）。守卫：栈不存在/空时不操作（防 leak 的对称 Pop，嵌套 loop 隔离）。</summary>
    internal static void PopRowCountStack(ExecutionContext ctx)
    {
        if (ctx.Vars.TryGetValue("_lastRowCountStack", out var stkObj) && stkObj is Stack<int> stk && stk.Count > 0)
            stk.Pop();
    }

    async Task<PhaseResult> ExecuteLoopPhaseAsync(ExecutionContext ctx, ScriptV2 script, int phaseIdx, PhaseNode phase, Dictionary<string, object> fillData)
    {
        ctx.PhaseName = phase.Name;
        ctx.GoalStack.Push(phase.AiGoal ?? phase.Name);  // 改动6.4：目标链入口 push
        try {
        var rows = VariableHelper.GetLoopRows(phase.LoopSource, ctx.ScopeChain);
        var maxLoopCount = phase.MaxLoopCount ?? script.Settings?.MaxLoopCount ?? _options.MaxLoopCount;
        int rowIdx = 0;
        int rowRerunCount = 0;  // 决策13/T.14：row_rerun 计数（原 rowRetryCount）
        int phaseRerunCount = 0;  // 决策13：phase_rerun 计数
        bool isRetry = false;

        LOOP_TOP:
        while (true)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            // 修订8：超 maxLoopCount 即失败（loopSource 应配 maxLoopCount≥预期行数；loopCondition 达上限=死循环超限）。
            // 正确配置不会达上限——只让异常/错误配置显式失败（原 G.3.4 达上限 Success+告警会静默截断丢数据）。
            if (rowIdx >= maxLoopCount)
                return new PhaseResult(false) { FailedStepIndex = -1, Message = $"loop '{phase.Name}' 超过最大循环次数 {maxLoopCount}" };

            if (!isRetry && phase.LoopCondition != null)
            {
                bool loopCond;
                try { loopCond = await _detectEvaluator.EvaluateAsync(phase.LoopCondition, ctx.ActivePage, script, phase.Iframe, ctx.Vars, ctx.ScopeChain); }
                catch (Exception ex) { _logger.LogWarning($"Phase {phase.Name} loopCondition 检测失败: {ex.Message}"); loopCond = false; }  // R2-D：加日志保 observability
                if (!loopCond) break;
            }
            isRetry = false;
            if (phase.LoopSource != null && rowIdx >= rows.Count) break;

            ctx.Vars["rowIndex"] = rowIdx + (phase.RowIndexOffset ?? 0);

            // 记录表格行数供 new_row_appears 使用（R4 A′：与判断端 DetectEvaluator EvaluateAsync 的 new_row_appears frame 同源——
            // 用 newRowDetect.Iframe 链 + tableSel ReplaceVars，否则 iframe 内 table 主文档找不到 → _lastRowCount 恒 0 → new_row_appears 误判）
            // DC10（a-a-4）：递归找 new_row_appears 节点（补顶层 gap——原 FirstOrDefault 只找顶层，嵌套 all/any/not 内的 new_row_appears 漏设基线 → silent 失效）。
            // DC13（a-a-4）：_lastRowCount 改栈 _lastRowCountStack（每行迭代 Push/Pop，嵌套 loop 隔离——原单 key 共享，内层覆盖外层 → 外层比对错 silent）。
            var newRowDetect = TryFindNewRowAppearsNode(phase.Steps);
            var tableSel = VariableHelper.ReplaceVars(newRowDetect?.Selector, ctx.ScopeChain, ctx.Vars);  // DC9 后 selector 必填（schema 强制）；null 时 Locator 抛→catch 基线 0
            var tableFrame = FrameResolver.ResolveChain(ctx.ActivePage, IframeChainMerger.Resolve(newRowDetect?.Iframe, phase.Iframe), ctx.ScopeChain, ctx.Vars);
            int baseRowCount;
            try { baseRowCount = await tableFrame.Locator(tableSel!).CountAsync(); }
            catch { baseRowCount = 0; }

            var rowData = rows.ElementAtOrDefault(rowIdx);
            if (rowData != null)
            {
                ctx.ScopeChain.Insert(0, rowData);
                PushRowCountStack(ctx, baseRowCount);  // DC13：每行迭代开头 Push（与 ScopeChain.Insert 同步）
            }

            for (int j = 0; j < phase.Steps.Count; j++)
            {
                ctx.Ct.ThrowIfCancellationRequested();

                if (phase.Steps[j] is PhaseNode nestedPhase)
                {
                    var savedPhaseName = ctx.PhaseName;
                    var nestedResult = await ExecuteNestedPhaseAsync(ctx, script, j, nestedPhase, fillData);
                    ctx.PhaseName = savedPhaseName;
                    if (nestedResult?.Type == "goto")
                    {
                        var targetIdx = phase.Steps.FindIndex(c => c is PhaseNode p && p.Name == nestedResult.ToPhase);
                        if (targetIdx >= 0) { j = targetIdx - 1; continue; }
                        if (rowData != null && ctx.ScopeChain.Count > 1) ctx.ScopeChain.RemoveAt(0); if (rowData != null) PopRowCountStack(ctx);
                        return nestedResult;
                    }
                    if (nestedResult?.Status == "script_fail" || nestedResult?.Status == "script_success")
                    {
                        // #24：loop 内嵌套 phase 的 script_success/script_fail 均向上传播（与 sequential :232 对称）
                        if (rowData != null && ctx.ScopeChain.Count > 1) ctx.ScopeChain.RemoveAt(0); if (rowData != null) PopRowCountStack(ctx);
                        return nestedResult;
                    }
                    continue;
                }

                var step = (StepNode)phase.Steps[j];
                if (!await ShouldExecuteStepAsync(ctx, script, phase, step)) continue;

                StepResult? result;
                try
                {
                    result = await ExecuteStepWithRetryAndFallbackAsync(ctx, script, phase, phaseIdx, step, j, fillData);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // TD-P11（2026-07-06）：J.3.1 守卫（与 sequential :289 对称）——浏览器崩溃重新 throw，不构造 PhaseResult（否则崩溃触发错误的 AI fallback，在崩溃浏览器上无意义操作；#23 漏加此守卫）
                    if (IsBrowserCrashException(ex)) throw;
                    // 步骤级 onError=stop 抛出异常 → 检查 phase 级 onError
                    var phaseOnError = GetOnError(phase, script);
                    if (phaseOnError == "skip")
                    {
                        _logger.LogWarning($"Loop phase step 失败跳过 | phase={phase.Name} step={step.Description} error={ex.Message}");
                        continue;
                    }
                    // #23：原 throw 直达顶层 catch→脚本失败，loop phase 无法触发 PhaseAiFallbackAsync；改 return PhaseResult(false) 让主循环 phaseOnError 生效（与 sequential H.3.2 对称）
                    if (rowData != null && ctx.ScopeChain.Count > 1) ctx.ScopeChain.RemoveAt(0); if (rowData != null) PopRowCountStack(ctx);
                    return new PhaseResult(false) { Message = ex.Message, FailedStepIndex = j };
                }
                if (step.StoreAs != null && result?.Value != null)
                    VariableHelper.StoreVars(ctx.Vars, step.StoreAs, result.Value);

                // 累积 AI 统计（ai action 步骤）
                AccumulateAiStats(ctx, result);

                if (step.Action == "check" && result?.ControlFlow != null)
                {
                    var cf = result.ControlFlow;
                    switch (cf.Then)
                    {
                        case "continue":  // 决策13：原 next（下一行 / loop 下一次迭代）
                            goto NEXT_ITERATION;
                        case "nothing":  // 决策13：原 continue/default（no-op，继续下一 step）
                            break;
                        case "break":
                            if (rowData != null && ctx.ScopeChain.Count > 1) ctx.ScopeChain.RemoveAt(0); if (rowData != null) PopRowCountStack(ctx);  // S5 H.4.6 break 对称 pop + rowData 守卫
                            goto LOOP_DONE;
                        case "row_rerun":  // 决策13：原 loop phase_retry，重跑当前行（MaxRowRerunCount）
                            foreach (var gk in ctx.GotoCounter.Keys.Where(k => k.StartsWith($"step:{phase.Name}:") || k.StartsWith($"{phaseIdx}:")).ToList())
                                ctx.GotoCounter.Remove(gk);
                            rowRerunCount++;
                            var maxRowRerun = script.Settings?.Rerun?.MaxRowRerunCount ?? _options.Rerun?.MaxRowRerunCount ?? 2;
                            if (rowRerunCount > maxRowRerun)
                            {
                                // 耗尽：走当前 check step 的 onError（决策13：AI 修当前行 / 跳行 / 停）
                                var rreMsg = VariableHelper.ReplaceVars(cf.Message ?? $"行 {rowIdx} 超过最大重跑次数({maxRowRerun})", ctx.ScopeChain, ctx.Vars);
                                var rreOnError = GetOnError(step, phase, script);
                                if (rreOnError is "ai-fallback-stop" or "ai-fallback-skip")
                                {
                                    StepResult? rreAi = null;
                                    try { rreAi = await AiFallbackAsync(ctx, script, phase, phaseIdx, step, fillData, new InvalidOperationException(rreMsg), rreOnError); }
                                    catch (OperationCanceledException) { throw; }
                                    catch { rreAi = null; }
                                    if (rreAi != null) break;  // AI 成功，继续下一 step
                                    if (rreOnError == "ai-fallback-skip") { goto NEXT_ITERATION; }  // 跳过当前行（RemoveAt+Pop 全由 NEXT_ITERATION L642 统一收尾，镜像 L519 continue；此处行内 RemoveAt 会与 L642 双 RemoveAt 致嵌套 scope 错位）
                                    if (rowData != null && ctx.ScopeChain.Count > 1) ctx.ScopeChain.RemoveAt(0); if (rowData != null) PopRowCountStack(ctx);
                                    var rreAiErr = ctx.Vars.TryGetValue("_lastAiFallbackError", out var rreAe) ? rreAe?.ToString() : null;  // 观察③修复：补 AI 失败原因（AiFallbackAsync return null 丢失 ErrorMessage）
                                    return new PhaseResult(false) { FailedStepIndex = j, Message = $"行 {rowIdx} 重跑耗尽且 AI 失败: {rreMsg}{(string.IsNullOrEmpty(rreAiErr) ? "" : $"（AI 原因: {rreAiErr}）")}" };
                                }
                                if (rreOnError == "skip") { goto NEXT_ITERATION; }  // 跳过当前行（RemoveAt+Pop 全由 NEXT_ITERATION L642 统一收尾，镜像 L519 continue；此处行内 RemoveAt 会双 RemoveAt 致嵌套 scope 错位）
                                if (rowData != null && ctx.ScopeChain.Count > 1) ctx.ScopeChain.RemoveAt(0); if (rowData != null) PopRowCountStack(ctx);
                                return new PhaseResult(false) { FailedStepIndex = j, Message = rreMsg };
                            }
                            var rowRerunInterval = script.Settings?.Rerun?.Interval ?? _options.Rerun?.Interval ?? 1000;
                            await Task.Delay(rowRerunInterval, ctx.Ct);
                            isRetry = true; rowIdx--;
                            if (rowData != null && ctx.ScopeChain.Count > 1) ctx.ScopeChain.RemoveAt(0); if (rowData != null) PopRowCountStack(ctx);
                            goto RETRY_ITERATION;
                        case "phase_rerun":  // 决策13：重跑整个 loop（MaxPhaseRerunCount）
                            // 附带修复1：补 goto 键清理（与 row_rerun / sequential phase_rerun 对称；原遗漏致重跑后 goto 计数残留误触 MaxPhaseGotoCount）
                            foreach (var gk in ctx.GotoCounter.Keys.Where(k => k.StartsWith($"step:{phase.Name}:") || k.StartsWith($"{phaseIdx}:")).ToList())
                                ctx.GotoCounter.Remove(gk);
                            phaseRerunCount++;
                            var maxPhaseRerun = script.Settings?.Rerun?.MaxPhaseRerunCount ?? _options.Rerun?.MaxPhaseRerunCount ?? 2;
                            if (phaseRerunCount > maxPhaseRerun)
                            {
                                if (rowData != null && ctx.ScopeChain.Count > 1) ctx.ScopeChain.RemoveAt(0); if (rowData != null) PopRowCountStack(ctx);
                                return new PhaseResult(false) { FailedStepIndex = j, Message = $"超过最大 phase 重跑次数({maxPhaseRerun})" };
                            }
                            var phaseRerunInterval = script.Settings?.Rerun?.Interval ?? _options.Rerun?.Interval ?? 1000;
                            await Task.Delay(phaseRerunInterval, ctx.Ct);
                            // 重置行索引与计数，重新开始整个 loop
                            if (rowData != null && ctx.ScopeChain.Count > 1) ctx.ScopeChain.RemoveAt(0); if (rowData != null) PopRowCountStack(ctx);
                            rowIdx = 0; rowRerunCount = 0;
                            goto LOOP_TOP;
                        case "step_error":  // 决策13：check 检测到业务错误，按本 step onError 4 选项
                            {
                                var seMsg = VariableHelper.ReplaceVars(cf.Message ?? "check 检测到业务错误", ctx.ScopeChain, ctx.Vars);
                                var seOnError = GetOnError(step, phase, script);
                                if (seOnError == "skip") break;  // 跳过，继续下一 step
                                if (seOnError is "ai-fallback-stop" or "ai-fallback-skip")
                                {
                                    StepResult? seAi = null;
                                    try { seAi = await AiFallbackAsync(ctx, script, phase, phaseIdx, step, fillData, new InvalidOperationException(seMsg), seOnError); }
                                    catch (OperationCanceledException) { throw; }
                                    catch { seAi = null; }
                                    if (seAi != null) break;  // AI 成功，继续下一 step
                                    if (seOnError == "ai-fallback-skip") break;  // skip 继续
                                    if (rowData != null && ctx.ScopeChain.Count > 1) ctx.ScopeChain.RemoveAt(0); if (rowData != null) PopRowCountStack(ctx);
                                    var seAiErr = ctx.Vars.TryGetValue("_lastAiFallbackError", out var seAe) ? seAe?.ToString() : null;  // 观察③修复：补 AI 失败原因
                                    return new PhaseResult(false) { FailedStepIndex = j, Message = $"{seMsg}{(string.IsNullOrEmpty(seAiErr) ? "" : $"（AI 原因: {seAiErr}）")}" };
                                }
                                if (rowData != null && ctx.ScopeChain.Count > 1) ctx.ScopeChain.RemoveAt(0); if (rowData != null) PopRowCountStack(ctx);
                                return new PhaseResult(false) { FailedStepIndex = j, Message = seMsg };
                            }
                        case "phase_error":  // 决策13：原 phase_fail
                            if (cf.CleanupSteps != null)
                                foreach (var s in cf.CleanupSteps)
                                    await ExecuteStepWithRetryAndFallbackAsync(ctx, script, phase, phaseIdx, s, -1, fillData);
                            if (rowData != null && ctx.ScopeChain.Count > 1) ctx.ScopeChain.RemoveAt(0); if (rowData != null) PopRowCountStack(ctx);
                            return new PhaseResult(false) { FailedStepIndex = j, Message = VariableHelper.ReplaceVars(cf.Message ?? "循环中止", ctx.ScopeChain, ctx.Vars) };
                        case "phase_success":  // 决策13：补，loop 内检测到成功条件提前结束当前 phase 视为成功（与 sequential HandleControlFlowAsync 对称，原走 default=no-op 不对称）
                            if (rowData != null && ctx.ScopeChain.Count > 1) ctx.ScopeChain.RemoveAt(0); if (rowData != null) PopRowCountStack(ctx);
                            return new PhaseResult(true) { Status = "phase_success" };
                        case "script_success":  // 决策13：新增，loop 内传播脚本成功
                            if (rowData != null && ctx.ScopeChain.Count > 1) ctx.ScopeChain.RemoveAt(0); if (rowData != null) PopRowCountStack(ctx);
                            return new PhaseResult(true) { Status = "script_success" };
                        case "script_fail":
                            if (cf.CleanupSteps != null)
                                foreach (var s in cf.CleanupSteps)
                                    await ExecuteStepWithRetryAndFallbackAsync(ctx, script, phase, phaseIdx, s, -1, fillData);
                            if (rowData != null && ctx.ScopeChain.Count > 1) ctx.ScopeChain.RemoveAt(0); if (rowData != null) PopRowCountStack(ctx);
                            return new PhaseResult(false) { Status = "script_fail", Message = VariableHelper.ReplaceVars(cf.Message ?? "脚本失败终止", ctx.ScopeChain, ctx.Vars) };
                        case "goto":
                            if (cf.ToPhase == null && cf.ToStep != null)
                            {
                                var si = phase.Steps.FindIndex(c => c is StepNode s && s.Name == cf.ToStep);
                                if (si >= 0)
                                {
                                    var stepGotoKey = $"step:{phase.Name}:{cf.ToStep}";
                                    ctx.GotoCounter[stepGotoKey] = ctx.GotoCounter.GetValueOrDefault(stepGotoKey) + 1;
                                    var maxGotoLoop = script.Settings?.MaxPhaseGotoCount ?? _options.MaxPhaseGotoCount;
                                    if (ctx.GotoCounter[stepGotoKey] > maxGotoLoop)
                                        throw new InvalidOperationException($"goto toStep 死循环: {cf.ToStep}（超过上限 {maxGotoLoop}）");
                                    j = si - 1;
                                }
                                continue;
                            }
                            if (cf.ToPhase != null)
                            {
                                var localIdx = phase.Steps.FindIndex(c => c is PhaseNode p && p.Name == cf.ToPhase);
                                if (localIdx >= 0) { j = localIdx - 1; continue; }
                                if (rowData != null && ctx.ScopeChain.Count > 1) ctx.ScopeChain.RemoveAt(0); if (rowData != null) PopRowCountStack(ctx);
                                return new PhaseResult(true) { Type = "goto", ToPhase = cf.ToPhase, ToStep = cf.ToStep };
                            }
                            continue;
                        default:
                            break;  // nothing/no-op，继续下一 step
                    }
                }
            }

        NEXT_ITERATION:
            rowRerunCount = 0;
            if (rowData != null && ctx.ScopeChain.Count > 1) ctx.ScopeChain.RemoveAt(0); if (rowData != null) PopRowCountStack(ctx);

        RETRY_ITERATION:
            rowIdx++;
        }

        // 修订8：删除原 G.3.4「达 maxLoopCount=Success+告警」逻辑——超限已在循环顶部 return 失败；
        // 到此仅可能经 break（loopCondition false / loopSource rows 完）正常终止 = 成功。

    LOOP_DONE:
        return new PhaseResult(true);
        } finally { ctx.GoalStack.Pop(); }  // 改动6.4：目标链出口 pop
    }

    async Task<PhaseResult?> ExecuteAiPhaseAsync(ExecutionContext ctx, ScriptV2 script, int phaseIdx, PhaseNode phase, Dictionary<string, object> fillData)
    {
        ctx.PhaseName = phase.Name;
        ctx.GoalStack.Push(phase.AiGoal ?? phase.Name);  // 改动6.4：目标链入口 push
        try {
        if (_aiActionExecutor == null)
        {
            _logger.LogWarning($"ai phase '{phase.Name}' 需要 IAiProvider，但未配置");
            return new PhaseResult(false) { Message = "未配置 AI Provider" };
        }

        // #3 A：ai phase instruction 改 JSON 节点（phase JSON 递归 + 可用数据 + 字段元数据 + storeAs）。
        // phase JSON 不 ReplaceVars（step.Value 保持 {{password}} 占位符，非明文，安全）；可用数据 BuildMaskedAllData 已脱敏敏感字段。
        var fieldMeta = BuildFieldMeta(ExtractFieldNamesFromPhaseRecursive(phase), script);
        var availableData = BuildMaskedAllData(ctx, script);
        var allStoreAs = CollectAllStoreAs(phase.Steps);
        var aiGoal = phase.AiGoal ?? phase.Name;
        var payload = new
        {
            phase,
            availableData,
            fieldMeta,
            storeAs = allStoreAs.Count > 0 ? allStoreAs : null
        };
        var instruction = $"""
            ## 任务: {aiGoal}
            ## 任务数据（JSON）
            {JsonSerializer.Serialize(payload, _aiPayloadJsonOptions)}
            """;

        var maxTurns = phase.MaxAiTurns ?? script.Settings?.MaxAiTurns ?? _options.MaxAiTurns;
        var totalTimeout = phase.Timeout ?? _options.MaxScriptDuration;

        var aiResult = await _aiActionExecutor.ExecuteAsync(
            ctx.ActivePage, ctx, instruction, phase.Iframe, script,
            maxTurns, totalTimeout, ctx.Vars, ctx.ScopeChain,
            AiScene.AiPhase, ct: ctx.Ct);

        // 累积 AI 统计
        ctx.AiSteps++;
        ctx.AiApiCallCount += aiResult.TurnsUsed;
        ctx.AiInputTokens += aiResult.InputTokens;
        ctx.AiOutputTokens += aiResult.OutputTokens;
        ctx.SnapshotCount += aiResult.SnapshotCount;
        ctx.AiScreenshotCount += aiResult.AiScreenshotCount;
        ctx.SnapshotTokens += aiResult.SnapshotTokens;
        ctx.AiCallsWithScreenshot += aiResult.AiCallsWithScreenshot;
        ctx.ScreenshotTokens += aiResult.ScreenshotTokens;

        if (!aiResult.Success)
        {
            // TD-P10（2026-07-06）：ai phase 失败终态推送（与 step/phase fallback 对称——4 个 AI 场景原本唯一缺终态推送，#4 改动清单当时漏列 ai phase）
            _ = _progressReporter?.SendLogAsync($"❌ ai phase 失败: {aiResult.ErrorMessage}");
            var onError = GetOnError(phase, script);
            if (onError == "skip") return new PhaseResult(false);
            return new PhaseResult(false) { Message = $"ai phase 失败: {aiResult.ErrorMessage}" };
        }

        // storeAs 处理
        // B4：ai phase 多 storeAs 拆分（递归 CollectAllStoreAs，原 Count==1 限制丢失多变量；仿 phase fallback）
        var storeAsMap = CollectAllStoreAs(phase.Steps);
        if (aiResult.ResultValue != null && storeAsMap.Count > 0)
        {
            if (aiResult.ResultValue is Dictionary<string, object> resultMap)
            {
                foreach (var kv in storeAsMap)
                {
                    if (resultMap.TryGetValue(kv.Key, out var val))
                        ctx.Vars[kv.Key] = val;
                    else
                        _logger.LogWarning($"AI 返回结果缺少 storeAs 变量 '{kv.Key}'（{kv.Value}），未存入 vars");  // R2-4：缺 key 告警（原静默跳过）
                }
            }
            else if (aiResult.ResultValue is System.Text.Json.JsonElement je
                     && je.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var kv in storeAsMap)
                    if (je.TryGetProperty(kv.Key, out var propVal))
                        ctx.Vars[kv.Key] = propVal.ValueKind == System.Text.Json.JsonValueKind.String
                            ? propVal.GetString() ?? ""
                            : propVal.GetRawText();
                    else
                        _logger.LogWarning($"AI 返回结果缺少 storeAs 变量 '{kv.Key}'（{kv.Value}），未存入 vars");  // R2-4：JsonElement 分支与 Dictionary 分支对称告警
            }
            else if (storeAsMap.Count == 1)
            {
                ctx.Vars[storeAsMap.First().Key] = aiResult.ResultValue;
            }
        }

        _ = _progressReporter?.SendLogAsync($"✅ ai phase 完成（轮次 {aiResult.TurnsUsed}）");  // TD-P10：ai phase 成功终态推送（与 step/phase fallback 对称）
        return new PhaseResult(true);
        } finally { ctx.GoalStack.Pop(); }  // 改动6.4：目标链出口 pop
    }

    #endregion

    #region 控制流

    async Task<PhaseResult?> HandleControlFlowAsync(ExecutionContext ctx, ScriptV2 script, PhaseNode phase, int phaseIdx, List<PhaseItem> steps, int stepIdx, ControlFlowResult cf, Dictionary<string, object> fillData)
    {
        switch (cf.Then)
        {
            case "nothing":  // 决策13：原 continue（no-op，继续下一步）
                return null;
            case "phase_success":
                return new PhaseResult(true) { Status = "phase_success" };  // 🟢-3：与 loop switch 一致显式标 Status（功能无变化，主循环按 Success 分支处理）
            case "script_success":
                return new PhaseResult(true) { Status = "script_success" };
            case "step_error":  // 决策13：check 检测到业务错误，按本 step onError 4 选项
                {
                    var seMsg = VariableHelper.ReplaceVars(cf.Message ?? "check 检测到业务错误", ctx.ScopeChain, ctx.Vars);
                    var seOnError = GetOnError(cf.Step, phase, script);
                    if (seOnError == "skip") return null;
                    if (seOnError is "ai-fallback-stop" or "ai-fallback-skip")
                    {
                        StepResult? seAi = null;
                        try { seAi = await AiFallbackAsync(ctx, script, phase, phaseIdx, cf.Step, fillData, new InvalidOperationException(seMsg), seOnError); }
                        catch (OperationCanceledException) { throw; }
                        catch { seAi = null; }
                        if (seAi != null) return null;  // AI 成功，继续下一 step
                        if (seOnError == "ai-fallback-skip") return null;  // skip 继续
                        return new PhaseResult(false) { FailedStepIndex = stepIdx, Message = seMsg };
                    }
                    return new PhaseResult(false) { FailedStepIndex = stepIdx, Message = seMsg };
                }
            case "phase_error":  // 决策13：原 phase_fail
                if (cf.CleanupSteps != null)
                    foreach (var s in cf.CleanupSteps)
                        await ExecuteStepWithRetryAndFallbackAsync(ctx, script, phase, phaseIdx, s, -1, fillData);
                _logger.LogWarning($"Phase 失败 | phase={phase.Name} 已完成={stepIdx}/{steps.Count}步\n" +
                    $"\t失败 step: step[{stepIdx}] | 原因: {cf.Message ?? "phase_error"}");
                return new PhaseResult(false) { FailedStepIndex = stepIdx, Message = VariableHelper.ReplaceVars(cf.Message ?? "phase_error", ctx.ScopeChain, ctx.Vars) };
            case "script_fail":
                if (cf.CleanupSteps != null)
                    foreach (var s in cf.CleanupSteps)
                        await ExecuteStepWithRetryAndFallbackAsync(ctx, script, phase, phaseIdx, s, -1, fillData);
                _logger.LogError($"脚本失败终止 | phase={phase.Name} 原因: {cf.Message ?? "script_fail"}");
                return new PhaseResult(false) { Status = "script_fail", Message = VariableHelper.ReplaceVars(cf.Message ?? "脚本失败终止", ctx.ScopeChain, ctx.Vars) };
            case "phase_rerun":  // ④-2：sequential phase_rerun 已在 ExecutePhaseAsync 就地处理（SEQ_TOP + goto），此处不可达
                throw new InvalidOperationException("phase_rerun 已在 ExecutePhaseAsync 就地处理，HandleControlFlowAsync 不应收到");
            case "goto":
                return await HandleGotoAsync(ctx, script, phase, phaseIdx, steps, stepIdx, cf);
            default:
                return null;
        }
    }

    async Task<PhaseResult?> HandleGotoAsync(ExecutionContext ctx, ScriptV2 script, PhaseNode phase, int phaseIdx, List<PhaseItem> steps, int stepIdx, ControlFlowResult cf)
    {
        // toStep 单独使用：已在 ExecutePhaseAsync/ExecuteLoopPhaseAsync 调用方处理
        if (cf.ToPhase == null && cf.ToStep != null)
            return null;

        if (cf.ToPhase != null)
        {
            // 脚本级 goto toPhase 计数器（maxPhaseJumpCount）
            ctx.PhaseJumpCount++;
            var maxPhaseJump = script.Settings?.MaxPhaseJumpCount ?? _options.MaxPhaseJumpCount;
            if (ctx.PhaseJumpCount > maxPhaseJump)
                throw new InvalidOperationException($"脚本级 goto toPhase 超过上限({maxPhaseJump}): {cf.ToPhase}");

            // phase 内 goto 共享计数器（maxPhaseGotoCount）
            var gotoKey = $"{phaseIdx}:{cf.ToPhase}:{cf.ToStep}";
            ctx.GotoCounter[gotoKey] = ctx.GotoCounter.GetValueOrDefault(gotoKey) + 1;
            var maxGoto = script.Settings?.MaxPhaseGotoCount ?? _options.MaxPhaseGotoCount;
            if (ctx.GotoCounter[gotoKey] > maxGoto)
                throw new InvalidOperationException($"goto 死循环: {gotoKey}");

            var loc = FindPhase(script.Phases, cf.ToPhase);
            if (loc == null) throw new InvalidOperationException($"goto 目标 phase '{cf.ToPhase}' 不存在");

            var targetPhase = loc.Chain.Count > 0 ? loc.Chain[^1] : null;
            if (targetPhase == null) return null;

            var startSi = cf.ToStep != null
                ? targetPhase.Steps.FindIndex(c => c is StepNode s && s.Name == cf.ToStep) : 0;

            // S5 M.2.9 goto toPhase 作用域重建：传 ScopeChain[^1]=fillData（栈底）作新 phase 的行数据；
            // 内层 loop scope 由调用方（ExecuteLoopPhaseAsync 的 RemoveAt(0)）清除。
            if (targetPhase.Type == "loop")
                return await ExecuteLoopPhaseAsync(ctx, script, loc.Path[0], targetPhase, ctx.ScopeChain.Count > 0 ? ctx.ScopeChain[^1] : new Dictionary<string, object>());
            return await ExecutePhaseAsync(ctx, script, loc.Path[0], targetPhase, ctx.ScopeChain.Count > 0 ? ctx.ScopeChain[^1] : new Dictionary<string, object>(), startSi >= 0 ? startSi : 0);
        }
        return null;
    }

    #endregion

    #region Step 执行辅助

    async Task<bool> ShouldExecuteStepAsync(ExecutionContext ctx, ScriptV2 script, PhaseNode phase, StepNode step)
    {
        if (step.SkipIfDataEmpty == true)
        {
            var fieldName = step.Field ?? VariableHelper.InferFieldFromValue(step.Value);
            if (fieldName != null && !VariableHelper.FieldExists(ctx.ScopeChain, fieldName))
            {
                ctx.Log.Add(new StepLog { PhaseName = phase.Name, Action = step.Action, Selector = step.Selector, Status = "skipped", Error = $"skipIfDataEmpty: {fieldName}" });
                return false;
            }
        }

        if (step.Condition != null)
        {
            try
            {
                return await _detectEvaluator.EvaluateAsync(step.Condition, ctx.ActivePage, script, IframeChainMerger.Resolve(step.Iframe, phase.Iframe), ctx.Vars, ctx.ScopeChain);
            }
            catch (Exception ex) when (ex is not IframeChainBrokenException) { return false; }  // R2-D BUG-67-1：IframeChainBrokenException 穿透 → L924 onError 触发（step.Condition dead write 解除）
        }

        if (step.SkipIfElementMissing == true && step.Selector != null)
        {
            var selector = VariableHelper.ReplaceVars(step.Selector, ctx.ScopeChain, ctx.Vars);
            var frame = FrameResolver.ResolveChain(ctx.ActivePage, IframeChainMerger.Resolve(step.Iframe, phase.Iframe), ctx.ScopeChain, ctx.Vars);
            try { if (await frame.Locator(selector).CountAsync() == 0)
            {
                ctx.Log.Add(new StepLog { PhaseName = phase.Name, Action = step.Action, Selector = step.Selector, Status = "skipped", Error = "skipIfElementMissing" });
                return false;
            } }
            catch { return false; }
        }

        return true;
    }

    async Task<StepResult?> ExecuteStepWithRetryAndFallbackAsync(ExecutionContext ctx, ScriptV2 script, PhaseNode phase, int phaseIdx, StepNode step, int stepIdx, Dictionary<string, object> fillData)
    {
        var retryCount = step.Retry?.Count ?? script.Settings?.StepRetry?.Count ?? _options.StepRetry?.Count ?? 0;
        var retryInterval = step.Retry?.Interval ?? script.Settings?.StepRetry?.Interval ?? _options.StepRetry?.Interval ?? 1000;
        Exception? lastError = null;
        var stepSw = Stopwatch.StartNew();

        for (int r = 0; r <= retryCount; r++)
        {
            try
            {
                var result = await _stepExecutor.ExecuteAsync(step, ctx, script, phase.Iframe, stepIdx);
                ctx.Log.Add(new StepLog
                {
                    PhaseName = phase.Name, StepIndex = stepIdx,
                    Action = step.Action, Selector = step.Selector,
                    DurationMs = stepSw.ElapsedMilliseconds,
                    Status = r > 0 ? "retry" : "success", RetryCount = r
                });

                var completeFields = BuildStepFieldSummary(step, ctx);  // #5：步骤字段摘要（与前端 FormatStepForLog 共用，三处字段对齐）
                var desc = Truncate(step.Description, 60);
                _logger.LogInformation($"step 完成 | phase={phase.Name} step={stepIdx}{(string.IsNullOrEmpty(desc) ? "" : " description=" + desc)} action={step.Action} selector={step.Selector}{(string.IsNullOrEmpty(completeFields) ? "" : " " + completeFields)} 耗时={stepSw.ElapsedMilliseconds}ms");

                // 进度上报 + 截图时机：OnPhaseProgress
                if (_progressReporter != null)
                {
                    try
                    {
                        var stepMessage = FormatStepForLog(step, ctx, stepSw.ElapsedMilliseconds);  // #5：按 action 格式化（含 step.Name/value/wait ms·until/navigate url/storeAs/耗时）
                        await _progressReporter.SendLogAsync(stepMessage);  // #5：stepName 不再单独传（[step name] 已在 message 内，前端 stepTag 前缀废弃）
                        if (_options.Screenshot?.OnPhaseProgress == true)
                        {
                            var screenshot = await CompressScreenshotAsync(ctx.ActivePage);
                            await _progressReporter.SendScreenshotAsync(screenshot, step.Description ?? step.Action);
                        }
                    }
                    catch { /* 进度上报失败不影响主流程 */ }
                }

                return result;
            }
            catch (Exception ex)
            {
                lastError = ex;

                // 浏览器崩溃直接标记失败，不重试
                if (IsBrowserCrashException(ex))
                    throw;

                // 意外对话框自动处理：action 超时后 accept pending dialog 再重试
                // Playwright dialog 事件会阻塞页面操作，超时通常意味着有未处理对话框
                // 通过注册一次性 handler 检测并关闭
                if (ex is TimeoutException || ex.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
                {
                    var dialogHandled = false;
                    using var dlgCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Ct);
                    dlgCts.CancelAfter(2000);  // J.3.4：链接 ctx.Ct，取消任务时 500ms 等待可取消
                    try
                    {
                        EventHandler<IDialog> tempHandler = async (_, d) =>
                        {
                            dialogHandled = true;
                            ctx.Vars["_lastDialogMessage"] = d.Message;
                            await d.AcceptAsync();
                        };
                        ctx.ActivePage.Dialog += tempHandler;
                        try
                        {
                            // 等待一小段时间看是否有 pending dialog 被捕获
                            await Task.Delay(500, dlgCts.Token);
                        }
                        finally
                        {
                            ctx.ActivePage.Dialog -= tempHandler;
                        }
                    }
                    catch { /* 忽略 */ }

                    if (dialogHandled)
                        _logger.LogWarning($"step 执行超时后检测到意外对话框，已自动 accept 关闭");
                }

                if (r < retryCount)
                {
                    ctx.Log.Add(new StepLog
                    {
                        PhaseName = phase.Name, StepIndex = stepIdx,
                        Action = step.Action, Selector = step.Selector,
                        DurationMs = stepSw.ElapsedMilliseconds,
                        Status = "retry", RetryCount = r, Error = ex.Message
                    });
                    var desc = Truncate(step.Description, 60);
                    _logger.LogWarning($"step 重试 | phase={phase.Name} step={stepIdx}{(string.IsNullOrEmpty(desc) ? "" : " description=" + desc)} action={step.Action} retry={r}/{retryCount} 错误={ex.Message}");
                    await Task.Delay(retryInterval, ctx.Ct);
                }
            }
        }

        // fallback
        if (step.Fallback != null)
        {
            try
            {
                var fbResult = await ExecuteFallbackAsync(ctx, script, phase, step, step.Fallback);
                ctx.Log.Add(new StepLog
                {
                    PhaseName = phase.Name, StepIndex = stepIdx,
                    Action = step.Action, Selector = step.Selector,
                    DurationMs = stepSw.ElapsedMilliseconds,
                    Status = "success", RetryCount = retryCount, Error = "fallback"
                });
                return fbResult;
            }
            catch (Exception ex) { lastError = ex; }
        }

        // onError 策略
        var onError = GetOnError(step, phase, script);
        if (onError == "skip")
        {
            ctx.Log.Add(new StepLog
            {
                PhaseName = phase.Name, StepIndex = stepIdx,
                Action = step.Action, Selector = step.Selector,
                DurationMs = stepSw.ElapsedMilliseconds,
                Status = "failed", RetryCount = retryCount, Error = lastError?.Message
            });
            var desc = Truncate(step.Description, 60);
            _logger.LogWarning($"step 跳过 | phase={phase.Name} step={stepIdx}{(string.IsNullOrEmpty(desc) ? "" : " description=" + desc)} action={step.Action} 错误={lastError?.Message} 策略=skip");
            return null;
        }

        if (onError is "ai-fallback-stop" or "ai-fallback-skip")
        {
            if (step.Action == "ai")
            {
                // ai action：自身已是 AI 多轮交互，降级为基础策略
                if (onError == "ai-fallback-skip")
                {
                    ctx.Log.Add(new StepLog
                    {
                        PhaseName = phase.Name, StepIndex = stepIdx,
                        Action = step.Action, Selector = step.Selector,
                        DurationMs = stepSw.ElapsedMilliseconds,
                        Status = "skipped", RetryCount = retryCount, Error = lastError?.Message
                    });
                    var desc = Truncate(step.Description, 60);
                    _logger.LogWarning($"step 跳过 (ai action 不触发 AI fallback) | phase={phase.Name} step={stepIdx}{(string.IsNullOrEmpty(desc) ? "" : " description=" + desc)} action={step.Action}");
                    return null;
                }
                // ai-fallback-stop → 降级为 stop，落到下面 throw
            }
            else
            {
                var aiResult = await AiFallbackAsync(ctx, script, phase, phaseIdx, step, fillData, lastError ?? new InvalidOperationException($"step 失败: {step.Action}"), onError);
                // #2 silent-success 修复：step AI 兜底失败的处理。
                // - ai-fallback-stop + AI 兜底失败 → throw 传播到 ExecutePhaseAsync/ExecuteLoopPhaseAsync catch → PhaseResult(false) → 主循环 scriptFailed=true → status=failed。
                //   原仅设 ctx.AiFallbackFailed（该标记仅 FailureType 细分用，status=completed 时根本不读它）→ status 误判 completed（silent-success，worker 日志实证 step 兜底失败却 status=completed）。
                // - ai-fallback-skip + AI 兜底失败 → return null 继续下一 step（skip 语义=放弃，不停）。
                // 仅 ai-fallback-stop 失败设 AiFallbackFailed（FailureType 细分）；ai-fallback-skip 失败不再设
                //   （原两者都设致后续他 step 确定性失败时误判 FailureType=AiFallback，实为混合/确定性失败）。
                if (aiResult == null && onError == "ai-fallback-stop")
                {
                    ctx.AiFallbackFailed = true;
                    // TD-P12（2026-07-06，方案①+补诊断）：throw message 不拼 lastError.Message——
                    // fallback 链尽头崩溃时 lastError.Message 含"Target closed"，拼进 throw message 会污染下游 IsBrowserCrashException（message-based 判断）致误判 rethrow（崩溃异常本在 retry :884 rethrow，但 fallback :949 catch 无守卫，崩溃经此路径进 #2）。
                    // 故 throw message 保持简略；step 失败原因（lastError.Message）单独 LogError + 推前端保留诊断（AI 失败原因 aiResult.ErrorMessage 已在 AiFallbackAsync:1227/1229 记/推，此处不重复）。
                    _logger.LogError($"step '{step.Name}' 失败且 AI 兜底失败 | 原始错误: {lastError?.Message}");
                    _ = _progressReporter?.SendLogAsync($"❌ step '{step.Name}' 失败: {lastError?.Message}（AI 兜底也失败）");
                    throw new InvalidOperationException($"step '{step.Name}' 失败且 AI 兜底失败", lastError);
                }
                ctx.Log.Add(new StepLog
                {
                    PhaseName = phase.Name, StepIndex = stepIdx,
                    Action = step.Action, Selector = step.Selector,
                    DurationMs = stepSw.ElapsedMilliseconds,
                    Status = aiResult != null ? "success" : "failed",
                    RetryCount = retryCount, Error = aiResult != null ? "ai-fallback" : lastError?.Message
                });
                return aiResult;  // aiResult==null 仅 ai-fallback-skip 走到这（return null 继续下一 step）
            }
        }

        // stop
        // OnStepFailure: 保存截图到文件 + 通过 Reporter 上报
        string? failureScreenshotPath = null;
        if (_options.Screenshot?.OnStepFailure == true)
        {
            try
            {
                var screenshot = await CompressScreenshotAsync(ctx.ActivePage);
                var folder = _options.Screenshot?.OnStepFailureFolder ?? "logs/";
                var segments = new List<string> { "failure" };
                if (!string.IsNullOrEmpty(script.ScriptId)) segments.Add(script.ScriptId);
                if (!string.IsNullOrEmpty(ctx.TaskId)) segments.Add(ctx.TaskId);
                if (!string.IsNullOrEmpty(phase.Name)) segments.Add(phase.Name);
                if (!string.IsNullOrEmpty(step.Name)) segments.Add(step.Name);
                segments.Add(DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                var failureFilename = $"{string.Join("_", segments)}.png";
                var relativePath = System.IO.Path.Combine(folder, failureFilename);
                failureScreenshotPath = System.IO.Path.GetFullPath(relativePath);
                var dir = System.IO.Path.GetDirectoryName(failureScreenshotPath);
                if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                await File.WriteAllBytesAsync(failureScreenshotPath, screenshot);
                _logger.LogWarning($"step 失败截图已保存: {failureScreenshotPath}");
                if (_progressReporter != null)
                    await _progressReporter.SendScreenshotAsync(screenshot, $"step 失败: {step.Description ?? step.Action}");
            }
            catch { /* 截图失败不影响主流程 */ }
        }

        ctx.Log.Add(new StepLog
        {
            PhaseName = phase.Name, StepIndex = stepIdx,
            Action = step.Action, Selector = step.Selector,
            DurationMs = stepSw.ElapsedMilliseconds,
            Status = "failed", RetryCount = retryCount, Error = lastError?.Message,
            ScreenshotPath = failureScreenshotPath
        });
        var failFields = BuildStepFieldSummary(step, ctx);  // #5：步骤字段摘要（与前端/完成日志共用，含 value/key/filePath/captchaType 等全部字段，不止 value）
        var failDesc = Truncate(step.Description, 60);
        _logger.LogError($"step 失败 | phase={phase.Name} step={stepIdx}{(string.IsNullOrEmpty(failDesc) ? "" : " description=" + failDesc)} action={step.Action} selector={step.Selector}\n" +
            $"\t输入: {(string.IsNullOrEmpty(failFields) ? "(无)" : failFields)} | 错误: {lastError?.Message} | 策略: onError={onError}\n" +
            $"\t耗时: {stepSw.ElapsedMilliseconds}ms");
        _ = _progressReporter?.SendLogAsync($"❌ step 失败: {step.Name ?? step.Action} {lastError?.Message}");  // #5：步骤失败推前端（fire-and-forget 不阻塞 throw）
        throw lastError!;
    }

    async Task<StepResult?> ExecuteFallbackAsync(ExecutionContext ctx, ScriptV2 script, PhaseNode phase, StepNode step, StepFallback fallback)
    {
        // H.4.7：fallback 继承 step 的 Action/Field/Iframe/Timeout（非硬编码 click；Field 影响 transform）
        var fallbackStep = new StepNode
        {
            Kind = "step",
            Action = fallback.Action ?? step.Action,
            Selector = fallback.Selector ?? step.Selector,  // 结论7：fallback selector 继承 step.Selector（对齐同块 Action/Iframe/Timeout 继承；原直接赋值 null 致回放 fallback 元素定位失败）
            Value = fallback.Value,
            Code = fallback.Code,
            Field = step.Field,
            Iframe = IframeChainMerger.Resolve(fallback.Iframe, step.Iframe),
            Timeout = fallback.Timeout ?? step.Timeout,
        };
        // #51：多级 fallback 链失败判定改 try-catch（原 result==null 既漏抛异常失败，又误伤成功无返回值的 fallback）
        try
        {
            return await _stepExecutor.ExecuteAsync(fallbackStep, ctx, script, phase.Iframe);
        }
        catch (Exception) when (fallback.Fallback != null)
        {
            return await ExecuteFallbackAsync(ctx, script, phase, step, fallback.Fallback);
        }
    }

    /// <summary>#3 B：判断字段是否敏感（source=system 或 key 名单），用于 instruction 序列化时对该字段 value/url/filePath 脱敏。
    /// TD-P2：脱敏判据统一抽到 <see cref="SensitiveKeys.IsSensitive"/>（ScriptEngine + AiActionExecutor 共用），此为包装。</summary>
    private static bool IsSensitiveField(string? fieldName, ScriptV2 script)
        => SensitiveKeys.IsSensitive(fieldName, script);

    /// <summary>#3：AI instruction payload JSON 序列化选项（缩进 AI 易读 + 跳过 null 字段清洁 + 中文不转义 AI 可读）。</summary>
    private static readonly JsonSerializerOptions _aiPayloadJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All)
    };

    /// <summary>#3：构建字段元数据（结构化，进 instruction payload 的 fieldMeta）。递归提取 phase 内字段。</summary>
    private static List<object> BuildFieldMeta(HashSet<string> fieldNames, ScriptV2 script) => fieldNames
        .Select(fn => script.Fields.FirstOrDefault(f => f.Name == fn))
        .Where(fd => fd != null)
        .Select(fd => (object)new
        {
            name = fd!.Name,
            label = fd.Label,
            type = fd.Type,
            uiComponent = fd.UiComponent ?? "input",
            format = fd.Format,
            options = fd.Options,
            description = fd.Description
        })
        .ToList();

    /// <summary>#3 B：构建单个 step 的脱敏 JSON 对象（敏感字段的 value/url/filePath → "(已脱敏)"，非敏感 → ReplaceVars 明文）。
    /// selector 始终 ReplaceVars（定位用）。给 AI 路径脱敏（日志路径不脱敏，见 FormatStepForLog）。</summary>
    private object BuildStepForAi(StepNode step, ExecutionContext ctx, ScriptV2 script)
    {
        var sensitive = IsSensitiveField(step.Field, script);
        string? maskValue(string? raw) => raw == null ? null : (sensitive ? "(已脱敏)" : VariableHelper.ReplaceVars(raw, ctx.ScopeChain, ctx.Vars));
        return new
        {
            name = step.Name,
            action = step.Action,
            description = step.Description,
            selector = step.Selector != null ? VariableHelper.ReplaceVars(step.Selector, ctx.ScopeChain, ctx.Vars) : null,
            value = maskValue(step.Value),
            url = maskValue(step.Url),
            filePath = maskValue(step.FilePath),
            field = step.Field,
            iframe = step.Iframe
        };
    }

    /// <summary>
    /// F8(R5-2)：构建脱敏后的可用数据列表——敏感字段（source=system 或 key 名单）值用占位符，明文不进 AI 上下文（仅 fallback 指令脱敏，日志/确定性执行层保持明文）。
    /// 双轨识别：① script.Fields 中 Source==system 的字段名；② key 名单兜底（password/username/token/secret/apikey，Worker 注入的凭据可能不在 fields）。
    /// </summary>
    private static List<string> BuildMaskedAllData(ExecutionContext ctx, ScriptV2 script)
    {
        var allData = new List<string>();
        // 修订3：非标量值（List/Dict）序列化为 JSON 可读结构（原 val?.ToString() 进 AI 上下文是类型名垃圾"System.Collections.Generic.List..."）
        string Mask(string key, object? val)
        {
            if (SensitiveKeys.IsSensitive(key, script)) return "(系统变量，值已脱敏)";  // TD-P2：统一脱敏判据
            return val switch
            {
                null => "",
                string s => s,
                JsonElement => "(JsonElement 未归一)",  // 防御（Q4 入口归一后不应出现）
                _ => JsonSerializer.Serialize(val)     // List/Dict/标量 → JSON 可读结构
            };
        };
        foreach (var scope in ctx.ScopeChain)
            foreach (var kv in scope)
                if (!kv.Key.StartsWith("_")) allData.Add($"- {kv.Key}: {Mask(kv.Key, kv.Value)}");
        foreach (var kv in ctx.Vars)
            if (!kv.Key.StartsWith("_")) allData.Add($"- {kv.Key}: {Mask(kv.Key, kv.Value)}");
        return allData;
    }

    async Task<StepResult?> AiFallbackAsync(ExecutionContext ctx, ScriptV2 script, PhaseNode phase, int phaseIdx, StepNode step, Dictionary<string, object> fillData, Exception error, string onError)
    {
        if (_aiActionExecutor == null)
        {
            _logger.LogWarning($"AI fallback 需要 IAiProvider，但未配置");
            if (onError == "ai-fallback-skip") return null;
            throw error;
        }

        var stepIdx = phase.Steps.ToList().FindIndex(c => c == step);
        var completedInPhase = phase.Steps.Take(stepIdx > 0 ? stepIdx : 0).Count();
        var remainingInPhase = phase.Steps.Skip(stepIdx + 1).Count();
        // P3-1: 自动提取字段名，替代 phase.RelevantFields
        var fieldNames = ExtractFieldNamesFromPhase(phase);
        _logger.LogInformation($"AI 兜底启动 | phase={phase.Name} 目标={phase.AiGoal ?? phase.Name}\n" +
            $"\t上下文: {completedInPhase}步已完成, {remainingInPhase}步剩余, 可用字段=[{string.Join(", ", fieldNames)}]");

        // #3 A/B：step fallback instruction 改 JSON 节点（failedStep 脱敏 + error + 可用数据 + 字段元数据 + storeAs）。
        // 边界护栏/越界约束移到 StepFallbackAddition（system 级，#3 D），instruction 仅留任务数据（payload JSON）。
        var availableData = BuildMaskedAllData(ctx, script);
        var allStoreAs = CollectAllStoreAs(phase.Steps.Skip(stepIdx).ToList());
        // 改动6.4：目标链（栈底 script.Description → 外层 phase → 当前 phase）
        var goalChain = ctx.GoalStack.Count > 0
            ? string.Join("\n", ctx.GoalStack.Reverse().Select((g, i) => $"{new string(' ', i * 2)}- {g}"))
            : "- (无)";
        var payload = new
        {
            failedStep = BuildStepForAi(step, ctx, script),  // 敏感字段 value/url/filePath → "(已脱敏)"
            error = error.Message,
            completedPhases = ctx.CompletedPhases,
            availableData,
            fieldMeta = BuildFieldMeta(fieldNames, script),
            storeAs = allStoreAs.Count > 0 ? allStoreAs : null
        };
        var instruction = $"""
            ## 兜底使命
            你是兜底执行者，目标是达成 phase 业务目标；当前 step 失败阻碍目标；基于页面+数据自主诊断修复，**包括必要时重做填值/提交等前面操作**，不要局限于让当前 step 机械成功。
            ## 任务目标链
            {goalChain}
            ## 任务: {phase.AiGoal ?? phase.Name}
            ## 任务数据（JSON）
            {JsonSerializer.Serialize(payload, _aiPayloadJsonOptions)}
            """;

        var maxTurns = step.MaxAiTurns ?? phase.MaxAiTurns ?? script.Settings?.MaxAiTurns ?? _options.MaxAiTurns;
        var timeout = _options.DefaultAiTimeout;  // H.4.4：step 级 AI fallback 用独立超时

        var aiResult = await _aiActionExecutor.ExecuteAsync(
            ctx.ActivePage, ctx, instruction, IframeChainMerger.Resolve(step.Iframe, phase.Iframe), script,
            maxTurns, timeout, ctx.Vars, ctx.ScopeChain,
            AiScene.StepFallback, ct: ctx.Ct);

        // 累积 AI 统计
        ctx.AiSteps++;
        ctx.AiApiCallCount += aiResult.TurnsUsed;
        ctx.AiInputTokens += aiResult.InputTokens;
        ctx.AiOutputTokens += aiResult.OutputTokens;
        ctx.SnapshotCount += aiResult.SnapshotCount;
        ctx.AiScreenshotCount += aiResult.AiScreenshotCount;
        ctx.SnapshotTokens += aiResult.SnapshotTokens;
        ctx.AiCallsWithScreenshot += aiResult.AiCallsWithScreenshot;
        ctx.ScreenshotTokens += aiResult.ScreenshotTokens;

        if (aiResult.Success)
        {
            _logger.LogInformation($"AI 兜底成功 | phase={phase.Name} 轮次={aiResult.TurnsUsed} token={aiResult.InputTokens + aiResult.OutputTokens}");
            _ = _progressReporter?.SendLogAsync($"✅ AI 兜底成功（轮次 {aiResult.TurnsUsed}）");  // #4：成功推前端
        }
        else
        {
            _logger.LogError($"AI 兜底失败 | phase={phase.Name}\n" +
                $"\t原因: {aiResult.ErrorMessage} | 策略: onError={onError}");
            _ = _progressReporter?.SendLogAsync($"❌ AI 兜底失败: {aiResult.ErrorMessage}");  // O3（第 2 轮核查）：step fallback 失败推前端（#4 可观测性精神，让用户实时看到兜底失败而非等最终 failed）
            ctx.Vars["_lastAiFallbackError"] = aiResult.ErrorMessage;  // 观察③修复（2026-07-06）：失败原因存 vars，供 loop rre/se 分支 Message 拼接（return null 丢失 ErrorMessage，原 PhaseResult.Message 不含 AI 原因）
        }

        return aiResult.Success ? new StepResult(aiResult.ResultValue)
        {
            AiTurnsUsed = aiResult.TurnsUsed,
            AiInputTokens = aiResult.InputTokens,
            AiOutputTokens = aiResult.OutputTokens,
            SnapshotCount = aiResult.SnapshotCount,
            AiScreenshotCount = aiResult.AiScreenshotCount,
            SnapshotTokens = aiResult.SnapshotTokens,
            AiCallsWithScreenshot = aiResult.AiCallsWithScreenshot,
            ScreenshotTokens = aiResult.ScreenshotTokens
        } : null;
    }

    /// <summary>
    /// Phase 级 AI fallback：将整个 phase 的执行上下文交给 AI
    /// </summary>
    async Task<PhaseResult?> PhaseAiFallbackAsync(ExecutionContext ctx, ScriptV2 script,
        PhaseNode phase, Dictionary<string, object> fillData, PhaseResult? failedResult = null)
    {
        if (_aiActionExecutor == null)
        {
            _logger.LogWarning($"Phase AI fallback 需要 IAiProvider，但未配置");
            return new PhaseResult(false) { Message = "未配置 AI Provider" };
        }

        var phaseGoal = phase.AiGoal ?? phase.Name;
        var maxTurns = phase.MaxAiTurns ?? script.Settings?.MaxAiTurns ?? _options.MaxAiTurns;
        var totalTimeout = phase.Timeout ?? _options.DefaultAiTimeout;  // J.3.3：phase 级 fallback 超时用 DefaultAiTimeout（phase.Timeout 可能不足）

        _logger.LogInformation($"Phase AI 兜底启动 | phase={phase.Name} 目标={phaseGoal}");

        // 构建字段元数据（使用自动提取，P3-1 替代 phase.RelevantFields）
        var relevantFieldNames = ExtractFieldNamesFromPhaseRecursive(phase);

        // 三段式信息（A8：基于 FailedStepIndex 分段）
        var failedIdx = failedResult?.FailedStepIndex ?? -1;
        var errorMsg = failedResult?.Message ?? "未知错误";

        // #3 A/B：phase fallback instruction 改 JSON 节点（phase JSON + failedStepIndex + error + 可用数据 + 字段元数据 + storeAs）。
        // phase JSON 不 ReplaceVars（step.Value 保持 {{password}} 占位符，非明文，安全）；约束移到 PhaseFallbackAddition（system 级，#3 D）。
        var availableData = BuildMaskedAllData(ctx, script);
        var allStoreAs = CollectAllStoreAs(phase.Steps.Skip(failedIdx >= 0 ? failedIdx : 0).ToList());
        var goalChain = ctx.GoalStack.Count > 0
            ? string.Join("\n", ctx.GoalStack.Reverse().Select((g, i) => $"{new string(' ', i * 2)}- {g}"))
            : "- (无)";
        var payload = new
        {
            phase,  // 整个 phase JSON（含已完成/失败/待完成步骤结构）
            failedStepIndex = failedIdx,
            error = errorMsg,
            completedPhases = ctx.CompletedPhases,
            availableData,
            fieldMeta = BuildFieldMeta(relevantFieldNames, script),
            storeAs = allStoreAs.Count > 0 ? allStoreAs : null
        };
        var instruction = $"""
            ## 任务目标链
            {goalChain}
            ## 任务: {phaseGoal}
            ## 任务数据（JSON，phase 含已完成/失败/待完成步骤结构，从 failedStepIndex 起完成到 phase 末尾）
            {JsonSerializer.Serialize(payload, _aiPayloadJsonOptions)}
            """;

        ctx.AiFallbackFailed = false;

        var aiResult = await _aiActionExecutor.ExecuteAsync(
            ctx.ActivePage, ctx, instruction, phase.Iframe, script,
            maxTurns, totalTimeout, ctx.Vars, ctx.ScopeChain,
            AiScene.PhaseFallback, ct: ctx.Ct);

        // 累积 AI 统计
        ctx.AiSteps++;
        ctx.AiApiCallCount += aiResult.TurnsUsed;
        ctx.AiInputTokens += aiResult.InputTokens;
        ctx.AiOutputTokens += aiResult.OutputTokens;
        ctx.SnapshotCount += aiResult.SnapshotCount;
        ctx.AiScreenshotCount += aiResult.AiScreenshotCount;
        ctx.SnapshotTokens += aiResult.SnapshotTokens;
        ctx.AiCallsWithScreenshot += aiResult.AiCallsWithScreenshot;
        ctx.ScreenshotTokens += aiResult.ScreenshotTokens;

        if (!aiResult.Success)
        {
            // 第 4 轮核查 🟡-4：仅 phaseOnError=ai-fallback-stop 失败设 AiFallbackFailed（与 step 级 #2 对称）；
            // ai-fallback-skip 失败不设（skip 语义=放弃，避免后续他 phase 确定性失败时误判 FailureType=AiFallback）
            var phaseOnError = GetOnError(phase, script);
            if (phaseOnError == "ai-fallback-stop") ctx.AiFallbackFailed = true;
            _logger.LogError($"Phase AI 兜底失败 | phase={phase.Name}\n" +
                $"\t原因: {aiResult.ErrorMessage}");
            _ = _progressReporter?.SendLogAsync($"❌ Phase AI 兜底失败: {aiResult.ErrorMessage}");  // O3 对称：phase fallback 失败推前端
            return new PhaseResult(false) { Message = $"Phase AI fallback 失败: {aiResult.ErrorMessage}" };
        }

        // 存值：按 storeAsMap 拆分存入 vars
        var storeAsMap = CollectAllStoreAs(phase.Steps.Skip(failedIdx >= 0 ? failedIdx : 0).ToList());
        if (aiResult.ResultValue != null && storeAsMap.Count > 0)
        {
            if (aiResult.ResultValue is Dictionary<string, object> resultMap)
            {
                foreach (var kv in storeAsMap)
                {
                    if (resultMap.TryGetValue(kv.Key, out var val))
                        ctx.Vars[kv.Key] = val;
                    else
                        _logger.LogWarning($"AI 返回结果缺少 storeAs 变量 '{kv.Key}'（{kv.Value}），未存入 vars");  // R2-4：缺 key 告警（原静默跳过）
                }
            }
            else if (aiResult.ResultValue is System.Text.Json.JsonElement je
                     && je.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var kv in storeAsMap)
                    if (je.TryGetProperty(kv.Key, out var propVal))
                        ctx.Vars[kv.Key] = propVal.ValueKind == System.Text.Json.JsonValueKind.String
                            ? propVal.GetString() ?? ""
                            : propVal.GetRawText();
                    else
                        _logger.LogWarning($"AI 返回结果缺少 storeAs 变量 '{kv.Key}'（{kv.Value}），未存入 vars");  // R2-4：JsonElement 分支与 Dictionary 分支对称告警
            }
            else if (storeAsMap.Count == 1)
            {
                ctx.Vars[storeAsMap.First().Key] = aiResult.ResultValue;
            }
        }

        _logger.LogInformation($"Phase AI 兜底成功 | phase={phase.Name} 轮次={aiResult.TurnsUsed} token={aiResult.InputTokens + aiResult.OutputTokens}");
        _ = _progressReporter?.SendLogAsync($"✅ Phase AI 兜底成功（轮次 {aiResult.TurnsUsed}）");  // #4：成功推前端
        return new PhaseResult(true);
    }

    #endregion

    #region 辅助方法

    record PhaseLocation(List<int> Path, List<PhaseNode> Chain);

    PhaseLocation? FindPhase(List<PhaseItem> items, string? name)
    {
        if (name == null) return null;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] is PhaseNode p && p.Name == name)
                return new PhaseLocation([i], [p]);
            if (items[i] is PhaseNode nested && nested.Steps.Count > 0)
            {
                var found = FindPhase(nested.Steps, name);
                if (found != null)
                {
                    found.Path.Insert(0, i);
                    found.Chain.Insert(0, nested);
                    return found;
                }
            }
        }
        return null;
    }

    string GetOnError(StepNode step, PhaseNode phase, ScriptV2 script) =>
        step.OnError ?? phase.OnError ?? script.Settings?.OnError ?? _options.OnError ?? "stop";

    string GetOnError(PhaseNode phase, ScriptV2 script) =>
        phase.OnError ?? script.Settings?.OnError ?? _options.OnError ?? "stop";

    static void AccumulateAiStats(ExecutionContext ctx, StepResult? result)
    {
        if (result == null || result.AiTurnsUsed == 0) return;
        ctx.AiSteps++;
        ctx.AiApiCallCount += result.AiTurnsUsed;
        ctx.AiInputTokens += result.AiInputTokens;
        ctx.AiOutputTokens += result.AiOutputTokens;
        ctx.SnapshotCount += result.SnapshotCount;
        ctx.AiScreenshotCount += result.AiScreenshotCount;
        ctx.SnapshotTokens += result.SnapshotTokens;
        ctx.AiCallsWithScreenshot += result.AiCallsWithScreenshot;
        ctx.ScreenshotTokens += result.ScreenshotTokens;
    }

    /// <summary>#5：构建步骤的 action 特有字段摘要（value/ms/until/url/key/filePath/captchaType/code/direction/index/...）。
    /// 前端 FormatStepForLog（SendLogAsync → HTTP ReceiveLog + WS LOG_REPORT）+ worker 文件日志（步骤完成/失败）三处共用，
    /// 字段对齐避免各写一套 switch（三处同步）。长字段（code/description）截断防日志膨胀。
    /// 日志路径不脱敏（按脱敏总原则——用户自填数据前端/文件可见用于诊断）。</summary>
    private static string Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length > max ? s[..max] + "…" : s);

    internal static string BuildStepFieldSummary(StepNode step, ExecutionContext ctx)
    {
        var parts = new List<string>();
        switch (step.Action)
        {
            case "fill":
            case "type":
            case "select":  // N4：select 与 fill/type 同性质，显 value
                if (step.Value != null) parts.Add($"value={VariableHelper.ReplaceVars(step.Value, ctx.ScopeChain, ctx.Vars)}");
                break;
            case "wait":
                if (step.Ms != null) parts.Add($"ms={step.Ms}");
                if (step.Until != null) parts.Add($"until={step.Until.Type}");
                break;
            case "navigate":
                if (step.Url != null) parts.Add($"url={VariableHelper.ReplaceVars(step.Url, ctx.ScopeChain, ctx.Vars)}");
                break;
            case "extract":
                if (step.StoreAs != null) parts.Add("storeAs=yes");
                if (!string.IsNullOrEmpty(step.ExtractType) && step.ExtractType != "property") parts.Add($"extractType={step.ExtractType}");
                if (!string.IsNullOrEmpty(step.Property)) parts.Add($"property={step.Property}");
                break;
            case "pressKey":
                if (step.Key != null) parts.Add($"key={VariableHelper.ReplaceVars(step.Key, ctx.ScopeChain, ctx.Vars)}");
                break;
            case "upload":
                if (step.FilePath != null) parts.Add($"filePath={step.FilePath}");
                break;
            case "captcha":
                if (step.CaptchaType != null) parts.Add($"captchaType={step.CaptchaType}");
                break;
            case "evaluate":
                if (step.Code != null) parts.Add($"code={Truncate(VariableHelper.ReplaceVars(step.Code, ctx.ScopeChain, ctx.Vars), 60)}");
                break;
            case "scroll":
                if (step.Direction != null) parts.Add($"direction={step.Direction}");
                if (step.Amount != null) parts.Add($"amount={step.Amount}");
                break;
            case "switchTab":
            case "closeTab":
                if (step.Index != null) parts.Add($"index={step.Index}");
                break;
            case "click":
                if (step.DoubleClick == true) parts.Add("doubleClick=true");
                if (step.UseLast == true) parts.Add("useLast=true");
                break;
            case "check":
                if (step.Detect?.Type != null) parts.Add($"detect={step.Detect.Type}");
                if (step.Then != null) parts.Add($"then={step.Then}");
                break;
            case "handleDialog":
                if (step.Accept != null) parts.Add($"accept={step.Accept}");
                break;
            case "screenshot":
                if (step.ScreenshotType != null) parts.Add($"screenshotType={step.ScreenshotType}");
                break;
            case "goto":
                if (step.ToPhase != null) parts.Add($"toPhase={step.ToPhase}");
                if (step.ToStep != null) parts.Add($"toStep={step.ToStep}");
                break;
        }
        return string.Join(" ", parts);
    }

    /// <summary>#5：按 action 格式化步骤进度日志 message（前端 ReceiveLog/LOG_REPORT）。
    /// 步骤进度推前端（日志路径，按脱敏总原则不脱敏——用户自填数据前端可见用于诊断）。
    /// 字段提取复用 BuildStepFieldSummary（与 worker 文件日志字段对齐，单一源头）。</summary>
    internal static string FormatStepForLog(StepNode step, ExecutionContext ctx, long durationMs)
    {
        var name = string.IsNullOrEmpty(step.Name) ? step.Action : step.Name;
        var parts = new List<string> { $"[phase {ctx.PhaseName}] [step {name}]" };
        // description 字段紧跟步骤名后（所有 action 含 ai；ai 不再在 BuildStepFieldSummary 重复加，见改动 2）
        if (!string.IsNullOrEmpty(step.Description)) parts.Add($"description={Truncate(step.Description, 60)}");
        parts.Add($"action={step.Action}");
        if (step.Selector != null) parts.Add($"selector={step.Selector}");
        var summary = BuildStepFieldSummary(step, ctx);
        if (!string.IsNullOrEmpty(summary)) parts.Add(summary);
        parts.Add($"耗时={durationMs}ms");
        return string.Join(" ", parts);
    }

    ScriptResult BuildResult(ExecutionContext ctx, ScriptV2? script, bool success, string? errorMessage, Stopwatch totalSw, FailureType failureType = FailureType.None)
    {
        var stats = new ExecutionStats
        {
            DeterministicSteps = ctx.Log.Count(l => l.Status == "success" && l.RetryCount == 0),
            AiSteps = ctx.AiSteps,
            AiApiCallCount = ctx.AiApiCallCount,
            AiInputTokens = ctx.AiInputTokens,
            AiOutputTokens = ctx.AiOutputTokens,
            AiTotalTokens = ctx.AiInputTokens + ctx.AiOutputTokens,
            SnapshotCount = ctx.SnapshotCount,
            AiScreenshotCount = ctx.AiScreenshotCount,
            SnapshotTokens = ctx.SnapshotTokens,
            AiCallsWithScreenshot = ctx.AiCallsWithScreenshot,
            ScreenshotTokens = ctx.ScreenshotTokens,
            TotalDurationMs = (int)totalSw.ElapsedMilliseconds
        };

        // 失败时如果未显式指定 failureType，从 ctx 推断（ai phase 主路径失败 > AI 兜底失败 > 确定性失败）
        var resolvedFailureType = success
            ? FailureType.None
            : (failureType != FailureType.None
                ? failureType
                : (ctx.AiPhaseFailed ? FailureType.AiPhase
                    : (ctx.AiFallbackFailed ? FailureType.AiFallback : FailureType.Deterministic)));

        return new ScriptResult
        {
            Success = success,
            ExecutionLog = ctx.Log,
            ErrorMessage = errorMessage,
            Status = success ? "completed" : "failed",
            ReturnData = BuildReturnData(script, ctx, _logger),  // 改动5/修订6：按脚本 returnData 从 vars 组装（成功失败均组装；失败时 lastError 已写入 vars；传 logger R2-12 告警生效）
            TotalDurationMs = (int)totalSw.ElapsedMilliseconds,
            Vars = ctx.Vars,
            Stats = stats,
            FailureType = resolvedFailureType
        };
    }

    /// <summary>
    /// 改动5：按脚本顶层 returnData 声明从 ctx.Vars/作用域链组装返回数据字典（成功/失败均组装）。
    /// 值为纯变量引用 "{{var}}"（含 .key 嵌套）→ 取原始对象（保留 byte[]/集合/字典结构，不 ToString 拍平）；
    /// 否则（混合文本/字面值字符串）→ ReplaceVars 文本替换；非字符串 JSON 字面值（数字/bool/数组/对象）原样。
    /// 复用 VariableHelper.ResolveRawValue（M.2.13，已支持 .key 嵌套取原始值）。
    /// </summary>
    static Dictionary<string, object>? BuildReturnData(ScriptV2? script, ExecutionContext ctx, ILogger? logger = null)
    {
        if (script?.ReturnData is not { Count: > 0 } rd) return null;

        var result = new Dictionary<string, object>();
        foreach (var kv in rd)
            result[kv.Key] = ResolveReturnDataValue(kv.Value, ctx, logger);  // 修订6：传 logger（R2-12 嵌套中断告警生效）
        return result;
    }

    static readonly Regex ReturnDataPlaceholderRegex = new(@"\{\{(\w+(?:\.\w+)*)\}\}", RegexOptions.Compiled);

    static object ResolveReturnDataValue(object? value, ExecutionContext ctx, ILogger? logger = null)
    {
        // 从脚本 JSON 反序列化的值是 JsonElement（STJ 默认 object→JsonElement），需先归一化为托管类型再变量替换
        if (value is JsonElement je)
        {
            if (je.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return "";
            // #47：归一化为 CLR 托管类型（原 je.Clone() 塞 JsonElement 进 dict，下游序列化二次序列化/类型丢失）
            value = VariableHelper.NormalizeJsonElement(je);
        }

        if (value is string s && s.Length > 0)
        {
            var raw = VariableHelper.ResolveRawValue(s, ctx.ScopeChain, ctx.Vars, logger);  // 修订6：传 logger（R2-12 嵌套解析中断告警生效）
            if (raw != null) return raw;
            var replaced = VariableHelper.ReplaceVars(s, ctx.ScopeChain, ctx.Vars);
            // lastError 兜底（第5轮 ④-3）：未命中占位符（如成功路径 {{lastError}}——lastError 仅失败时写 vars）不留字面占位符，
            // 返 ""（仅 returnData 路径，不动 step ReplaceVars 的"未命中保留字面"全局约定）
            if (ReturnDataPlaceholderRegex.IsMatch(replaced)) return "";
            return replaced;
        }
        return value ?? "";
    }

    /// <summary>P3-1：最终截图（OnTaskComplete/OnTaskFailure 时机）。完成事件（成功/失败/停止）移交 completionHandler（TaskExecutionService.finally 的 OnTaskCompleted）按 task 实际状态单一出口发出，本方法不再发 SendTaskCompletedAsync/SendTaskStoppedAsync。</summary>
    async Task ReportFinalScreenshotAsync(ExecutionContext ctx, bool success)
    {
        if (_progressReporter == null) return;

        try
        {
            // 截图时机：OnTaskComplete / OnTaskFailure
            var screenshotConfig = _options.Screenshot;
            if ((success && screenshotConfig?.OnTaskComplete == true) ||
                (!success && screenshotConfig?.OnTaskFailure == true))
            {
                var screenshot = await CompressScreenshotAsync(ctx.ActivePage);
                await _progressReporter.SendFinalScreenshotAsync(screenshot);
            }
        }
        catch { /* 进度上报失败不影响主流程 */ }
    }

    private async Task<byte[]> CompressScreenshotAsync(IPage page)
    {
        var quality = _options.Screenshot?.CompressQuality ?? 70;
        if (quality >= 100)
            return await page.ScreenshotAsync();
        return await page.ScreenshotAsync(new() { Type = ScreenshotType.Jpeg, Quality = quality });
    }

    #endregion

    #region 字段自动提取（P3-1: 替代 phase.RelevantFields）

    /// <summary>
    /// step 级 fallback：只提取直接 StepNode，跳过子 PhaseNode
    /// </summary>
    static HashSet<string> ExtractFieldNamesFromPhase(PhaseNode phase)
    {
        var names = new HashSet<string>();
        foreach (var item in phase.Steps)
        {
            if (item is StepNode step)
            {
                if (step.Field != null) names.Add(step.Field);
                if (step.Value != null)
                    foreach (Match m in Regex.Matches(step.Value, @"\{\{(\w+(?:\.\w+)*)\}\}"))
                    {
                        var fullKey = m.Groups[1].Value;
                        var dotIdx = fullKey.IndexOf('.');
                        names.Add(dotIdx > 0 ? fullKey[..dotIdx] : fullKey);
                    }
                if (step.Description != null)
                    foreach (Match m in Regex.Matches(step.Description, @"\{\{(\w+(?:\.\w+)*)\}\}"))
                    {
                        var fullKey = m.Groups[1].Value;
                        var dotIdx = fullKey.IndexOf('.');
                        names.Add(dotIdx > 0 ? fullKey[..dotIdx] : fullKey);
                    }
                if (step.Selector != null)
                    foreach (Match m in Regex.Matches(step.Selector, @"\{\{(\w+(?:\.\w+)*)\}\}"))
                    {
                        var fullKey = m.Groups[1].Value;
                        var dotIdx = fullKey.IndexOf('.');
                        names.Add(dotIdx > 0 ? fullKey[..dotIdx] : fullKey);
                    }
                if (step.FilePath != null)
                    foreach (Match m in Regex.Matches(step.FilePath, @"\{\{(\w+(?:\.\w+)*)\}\}"))
                    {
                        var fullKey = m.Groups[1].Value;
                        var dotIdx = fullKey.IndexOf('.');
                        names.Add(dotIdx > 0 ? fullKey[..dotIdx] : fullKey);
                    }
            }
            // PhaseNode 跳过（子 phase 有自己的 fallback）
        }
        return names;
    }

    /// <summary>
    /// phase 级 fallback：递归提取所有层级的 StepNode
    /// </summary>
    static HashSet<string> ExtractFieldNamesFromPhaseRecursive(PhaseNode phase)
    {
        var names = new HashSet<string>();
        foreach (var item in phase.Steps)
        {
            if (item is StepNode step)
            {
                if (step.Field != null) names.Add(step.Field);
                if (step.Value != null)
                    foreach (Match m in Regex.Matches(step.Value, @"\{\{(\w+(?:\.\w+)*)\}\}"))
                    {
                        var fullKey = m.Groups[1].Value;
                        var dotIdx = fullKey.IndexOf('.');
                        names.Add(dotIdx > 0 ? fullKey[..dotIdx] : fullKey);
                    }
                if (step.Description != null)
                    foreach (Match m in Regex.Matches(step.Description, @"\{\{(\w+(?:\.\w+)*)\}\}"))
                    {
                        var fullKey = m.Groups[1].Value;
                        var dotIdx = fullKey.IndexOf('.');
                        names.Add(dotIdx > 0 ? fullKey[..dotIdx] : fullKey);
                    }
                if (step.Selector != null)
                    foreach (Match m in Regex.Matches(step.Selector, @"\{\{(\w+(?:\.\w+)*)\}\}"))
                    {
                        var fullKey = m.Groups[1].Value;
                        var dotIdx = fullKey.IndexOf('.');
                        names.Add(dotIdx > 0 ? fullKey[..dotIdx] : fullKey);
                    }
                if (step.FilePath != null)
                    foreach (Match m in Regex.Matches(step.FilePath, @"\{\{(\w+(?:\.\w+)*)\}\}"))
                    {
                        var fullKey = m.Groups[1].Value;
                        var dotIdx = fullKey.IndexOf('.');
                        names.Add(dotIdx > 0 ? fullKey[..dotIdx] : fullKey);
                    }
            }
            else if (item is PhaseNode nested)
            {
                foreach (var n in ExtractFieldNamesFromPhaseRecursive(nested)) names.Add(n);
            }
        }
        return names;
    }

    #endregion

    #region AI fallback 辅助方法

    /// <summary>递归收集所有层级的 storeAs。
    /// #3：ExpandStepsToDescription/DescribeDetect/FormatStoreAs 已删（instruction 改 JSON 节点，不再文本展开）；
    /// CollectAllStoreAs 保留——648（ExecuteAiPhaseAsync）/PhaseAiFallbackAsync 存值 resultMap.TryGetValue 用，删了 storeAs 变量静默不存（silent 回归）。</summary>
    private Dictionary<string, string> CollectAllStoreAs(List<PhaseItem> items)
    {
        var result = new Dictionary<string, string>();
        foreach (var item in items)
        {
            if (item is StepNode step && step.StoreAs != null)
            {
                if (step.StoreAs is string s)
                    result[s] = step.Description ?? step.Action;
                else if (step.StoreAs is System.Text.Json.JsonElement je)
                {
                    // 与 StoreVars 同源：STJ 把 object? 字段反序列化为 JsonElement，单 storeAs 是 JsonElement(String)
                    if (je.ValueKind == System.Text.Json.JsonValueKind.String)
                        result[je.GetString() ?? ""] = step.Description ?? step.Action;
                    else if (je.ValueKind == System.Text.Json.JsonValueKind.Object)
                        foreach (var prop in je.EnumerateObject())
                            result[prop.Name] = prop.Value.GetString() ?? prop.Name;
                }
            }
            else if (item is PhaseNode phase && phase.Steps?.Count > 0)
            {
                foreach (var kv in CollectAllStoreAs(phase.Steps))
                    result[kv.Key] = kv.Value;
            }
        }
        return result;
    }

    #endregion
}
