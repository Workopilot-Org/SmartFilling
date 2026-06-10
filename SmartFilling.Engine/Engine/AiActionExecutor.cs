using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.Playwright;
using OpenAI.Chat;
using SmartFilling.Engine.Ai;
using SmartFilling.Engine.Logging;
using SmartFilling.Engine.Models;
using SmartFilling.Engine.Prompts;
using SmartFilling.Engine.Reporting;

namespace SmartFilling.Engine.Engine;

/// <summary>#3 D：AI 执行场景（替代原 bool isFallback），决定 systemPrompt 追加哪个 FallbackAddition。</summary>
public enum AiScene { AiAction, StepFallback, AiPhase, PhaseFallback }

/// <summary>
/// AI 统一执行引擎：ai action / AI fallback / ai phase 共用。
/// 管理多轮 AI 循环、工具调用分发、对话压缩。
/// </summary>
public class AiActionExecutor
{
    private readonly IAiProvider _aiProvider;
    private readonly ILogger _logger;
    private readonly ITaskProgressReporter? _reporter;  // #4：推前端 ReceiveLog（原 _logger 是 dead field，一并启用）
    private readonly ConversationCompressor _compressor;
    private readonly CompressionOptions? _compression;
    private byte[]? _lastScreenshot;

    /// <summary>F8(f)：敏感字段（source=system 或 key 名单）的真实值（每次 ExecuteAsync 从 scopeChain/vars 收集），
    /// toolResult/evaluate 脱敏判据用。不依赖 AI 传 field（AI fill/type/select 工具无 field 参数）。
    /// TD-P2：敏感 key 名单/判据统一用 <see cref="SensitiveKeys"/>（与 ScriptEngine 共用，原两处重复定义合并）。</summary>
    private HashSet<string> _sensitiveValues = new();

    /// <summary>F8(f)：toolResult 回传前对敏感字段的 value 脱敏（防明文经 messages 历史重发 LLM）。
    /// 判据：value 命中已知敏感字段真实值（_sensitiveValues）→ 脱敏。不依赖 AI 传 field（AI 工具无 field 参数，旧判据恒不触发）。</summary>
    private string MaskToolResultValue(string value)
        => !string.IsNullOrEmpty(value) && _sensitiveValues.Contains(value) ? "(敏感值已填)" : value;

    /// <summary>F8(f)：收集敏感字段（source=system 或 key 名单）的真实值（scopeChain/vars 中），供 toolResult/evaluate 脱敏比对。</summary>
    private static HashSet<string> CollectSensitiveValues(ExecutionContext ctx, ScriptV2? script)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        void Collect(IEnumerable<KeyValuePair<string, object>> kv)
        {
            foreach (var e in kv)
            {
                if (e.Key.StartsWith("_")) continue;
                if (SensitiveKeys.IsSensitive(e.Key, script) && e.Value is string s && !string.IsNullOrEmpty(s))  // TD-P2：统一判据
                    values.Add(s);
            }
        }
        foreach (var scope in ctx.ScopeChain) Collect(scope);
        Collect(ctx.Vars);
        return values;
    }

    public AiActionExecutor(IAiProvider aiProvider, ILogger logger, ITaskProgressReporter? reporter = null, CompressionOptions? compression = null)
    {
        _aiProvider = aiProvider;
        _logger = logger;
        _reporter = reporter;
        _compressor = new ConversationCompressor();
        _compression = compression;
    }

    /// <summary>
    /// 执行 AI 任务（ai action / AI fallback / ai phase 共用）
    /// </summary>
    public async Task<AiExecutionResult> ExecuteAsync(
        IPage page,
        ExecutionContext ctx,
        string instruction,
        string[]? iframeChain,
        ScriptV2? script,
        int maxTurns,
        int totalTimeout,
        Dictionary<string, object> vars,
        List<Dictionary<string, object>> scopeChain,
        AiScene scene,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        _sensitiveValues = CollectSensitiveValues(ctx, script);  // F8(f)：刷新敏感字段真实值（toolResult/evaluate 脱敏判据）
        // #3 D：按场景选 FallbackAddition（AiAction/AiPhase 仅 BaseSystemPrompt；StepFallback/PhaseFallback 追加对应约束）
        var addition = scene switch
        {
            AiScene.StepFallback => AiActionPrompts.StepFallbackAddition,
            AiScene.PhaseFallback => AiActionPrompts.PhaseFallbackAddition,
            _ => ""
        };
        var systemPrompt = AiActionPrompts.BaseSystemPrompt + addition;
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(instruction)
        };

        var tools = GetToolDefinitions();
        int turnsUsed = 0;
        int totalInputTokens = 0;
        int totalOutputTokens = 0;
        int snapshotCount = 0;
        int aiScreenshotCount = 0;
        int snapshotTokens = 0;
        int aiCallsWithScreenshot = 0;
        int screenshotTokens = 0;

        try
        {
            for (int turn = 0; turn < maxTurns; turn++)
            {
                ct.ThrowIfCancellationRequested();

                if (sw.ElapsedMilliseconds > totalTimeout)
                    return new AiExecutionResult { Success = false, ErrorMessage = "AI 执行超时", TurnsUsed = turnsUsed };

                // 对话压缩
                messages = _compressor.CompressHistory(messages, _compression ?? new CompressionOptions { Threshold = 30, MinimumPreserved = 5, PreserveInitialUserMessages = true });
                _compressor.CompressOldSnapshots(messages);

                var response = await _aiProvider.SendMessageAsync(messages, tools, ct);
                turnsUsed++;
                totalInputTokens += response.InputTokens;
                totalOutputTokens += response.OutputTokens;

                // 无工具调用 — 文本回复
                if (response.ToolCalls == null || response.ToolCalls.Count == 0)
                {
                    messages.Add(new AssistantChatMessage(response.Text ?? ""));
                    continue;
                }

                // 处理工具调用
                // 使用 OpenAiProvider 返回的 RawAssistantMessage（从 ChatCompletion 构造）
                messages.Add(response.RawAssistantMessage ?? new AssistantChatMessage(response.Text ?? ""));

                bool done = false;
                object? resultValue = null;
                bool hasSnapshot = false;
                bool hasScreenshot = false;

                foreach (var tc in response.ToolCalls)
                {
                    if (tc.Name == "done")
                    {
                        resultValue = tc.Arguments?.GetValueOrDefault("result");
                        // 调研#9（2026-06-29，诊断实证）：DashScope 对 done 工具 result 参数（schema 无 type 约束）的对象返回值
                        // 序列化成 JSON 字符串（CLR string，诊断见 type=String value={"token":...}），而非 JSON 对象。
                        // StoreVars 多变量拆分期望对象结构（Dictionary/JsonElement(Object)）；若 result 是 JSON 对象字符串，
                        // 解析成 JsonElement(Object)，配合 StoreVars value 归一化（VariableHelper L304）完成多变量拆分。
                        // 否则 ai action 对象 storeAs 静默不 populate（silent-success）。
                        if (resultValue is string resultStr && resultStr.TrimStart().StartsWith("{"))
                        {
                            try { resultValue = System.Text.Json.JsonDocument.Parse(resultStr).RootElement.Clone(); }
                            catch { /* 非合法 JSON，保持原 string（单变量 storeAs 仍整体存） */ }
                        }
                        done = true;
                        messages.Add(new ToolChatMessage(tc.Id, "任务完成"));
                        break;
                    }

                    var toolResult = await ExecuteToolCallAsync(ctx, tc, iframeChain, script, vars, scopeChain, ct);

                    if (tc.Name == "get_snapshot")
                    {
                        _compressor.RegisterSnapshotId(tc.Id);
                        snapshotCount++;
                        hasSnapshot = true;
                    }

                    if (tc.Name == "screenshot" && _lastScreenshot != null)
                    {
                        aiScreenshotCount++;
                        hasScreenshot = true;
                        var base64 = Convert.ToBase64String(_lastScreenshot);
                        var mimeType = _lastScreenshot.Length > 1 && _lastScreenshot[0] == 0xFF ? "image/jpeg" : "image/png";
                        messages.Add(new ToolChatMessage(tc.Id, "[截图内容见下方图片]"));
                        messages.Add(new UserChatMessage(
                            ChatMessageContentPart.CreateTextPart("[截图]"),
                            ChatMessageContentPart.CreateImagePart(new Uri($"data:{mimeType};base64,{base64}"))));
                        _lastScreenshot = null;
                    }
                    else
                    {
                        messages.Add(new ToolChatMessage(tc.Id, toolResult));
                    }
                }

                // #4：该轮 AI 工具摘要推前端 ReceiveLog（fire-and-forget 不阻塞 AI 循环；🤖 前缀在推送端，handler 无法区分 AI 日志）。日志路径不脱敏（按脱敏总原则）。
                // N3（第 2 轮核查）：补 token（计划 #4 要求 _logger/推送含工具名/结果/轮次/token）
                var toolSummary = string.Join(", ", response.ToolCalls.Select(t => t.Name));
                var tokenSoFar = totalInputTokens + totalOutputTokens;
                _logger.LogInformation($"🤖 AI 轮次 {turnsUsed} | 工具: {toolSummary} | 累计 token={tokenSoFar}");
                _ = _reporter?.SendLogAsync($"🤖 [轮次 {turnsUsed}] 调用工具: {toolSummary} | 累计 token={tokenSoFar}");

                // I.1.1：消费完 response 后 Dispose OwnedResources（ToolCalls.Arguments 已 ParseJsonElement 深拷贝、RawAssistantMessage=AssistantChatMessage(response) 不引用 JsonDocument），防 JsonDocument 泄漏
                if (response.OwnedResources != null)
                    foreach (var d in response.OwnedResources) d.Dispose();

                if (hasSnapshot)
                    snapshotTokens += response.InputTokens + response.OutputTokens;
                if (hasScreenshot)
                {
                    aiCallsWithScreenshot++;
                    screenshotTokens += response.InputTokens + response.OutputTokens;
                }

                if (done)
                {
                    return new AiExecutionResult
                    {
                        Success = true,
                        ResultValue = resultValue,
                        TurnsUsed = turnsUsed,
                        InputTokens = totalInputTokens,
                        OutputTokens = totalOutputTokens,
                        SnapshotCount = snapshotCount,
                        AiScreenshotCount = aiScreenshotCount,
                        SnapshotTokens = snapshotTokens,
                        AiCallsWithScreenshot = aiCallsWithScreenshot,
                        ScreenshotTokens = screenshotTokens
                    };
                }
            }

            return new AiExecutionResult
            {
                Success = false,
                ErrorMessage = $"AI 在 {maxTurns} 轮内未完成任务",
                TurnsUsed = turnsUsed,
                InputTokens = totalInputTokens,
                OutputTokens = totalOutputTokens,
                SnapshotCount = snapshotCount,
                AiScreenshotCount = aiScreenshotCount,
                SnapshotTokens = snapshotTokens,
                AiCallsWithScreenshot = aiCallsWithScreenshot,
                ScreenshotTokens = screenshotTokens
            };
        }
        catch (OperationCanceledException)
        {
            return new AiExecutionResult { Success = false, ErrorMessage = "AI 执行被取消", TurnsUsed = turnsUsed };
        }
        catch (Exception ex)
        {
            return new AiExecutionResult { Success = false, ErrorMessage = ex.Message, TurnsUsed = turnsUsed };
        }
    }

    #region 工具定义

    private List<ChatTool> GetToolDefinitions()
    {
        return
        [
            ChatTool.CreateFunctionTool("click", "点击元素", BinaryData.FromString("""{"type":"object","properties":{"selector":{"type":"string","description":"CSS/XPath 选择器"},"ref":{"type":"string","description":"快照中的元素引用编号，如 e27。优先于 selector"}},"required":[]}""")),
            ChatTool.CreateFunctionTool("fill", "清空后输入文本", BinaryData.FromString("""{"type":"object","properties":{"selector":{"type":"string"},"ref":{"type":"string","description":"快照中的元素引用编号，优先于 selector"},"value":{"type":"string"}},"required":["value"]}""")),
            ChatTool.CreateFunctionTool("type", "追加输入文本（不清空）", BinaryData.FromString("""{"type":"object","properties":{"selector":{"type":"string"},"ref":{"type":"string","description":"快照中的元素引用编号，优先于 selector"},"value":{"type":"string"}},"required":["value"]}""")),
            ChatTool.CreateFunctionTool("select", "下拉选择", BinaryData.FromString("""{"type":"object","properties":{"selector":{"type":"string"},"ref":{"type":"string","description":"快照中的元素引用编号，优先于 selector"},"value":{"type":"string"}},"required":["value"]}""")),
            ChatTool.CreateFunctionTool("press", "按键", BinaryData.FromString("""{"type":"object","properties":{"key":{"type":"string","description":"键值（Enter/Tab/Escape 等）"},"selector":{"type":"string"},"ref":{"type":"string","description":"快照中的元素引用编号，优先于 selector"}},"required":["key"]}""")),
            ChatTool.CreateFunctionTool("hover", "鼠标悬停", BinaryData.FromString("""{"type":"object","properties":{"selector":{"type":"string"},"ref":{"type":"string","description":"快照中的元素引用编号，优先于 selector"}},"required":[]}""")),
            ChatTool.CreateFunctionTool("scroll", "滚动", BinaryData.FromString("""{"type":"object","properties":{"selector":{"type":"string"},"ref":{"type":"string","description":"快照中的元素引用编号，优先于 selector"},"direction":{"type":"string","description":"up/down/left/right/bottom/top"},"amount":{"type":"integer"}}}""")),
            ChatTool.CreateFunctionTool("navigate", "导航到 URL", BinaryData.FromString("""{"type":"object","properties":{"url":{"type":"string"}},"required":["url"]}""")),
            ChatTool.CreateFunctionTool("upload", "上传文件", BinaryData.FromString("""{"type":"object","properties":{"selector":{"type":"string"},"ref":{"type":"string","description":"快照中的元素引用编号，优先于 selector"},"filePath":{"type":"string"}},"required":["filePath"]}""")),
            ChatTool.CreateFunctionTool("goBack", "浏览器后退", BinaryData.FromString("""{"type":"object","properties":{},"required":[]}""")),
            ChatTool.CreateFunctionTool("switchTab", "切换标签页", BinaryData.FromString("""{"type":"object","properties":{"index":{"type":"integer","description":"标签页索引，-1为最后一个"}},"required":["index"]}""")),
            ChatTool.CreateFunctionTool("closeTab", "关闭标签页", BinaryData.FromString("""{"type":"object","properties":{},"required":[]}""")),
            ChatTool.CreateFunctionTool("evaluate", "执行 JavaScript 代码", BinaryData.FromString("""{"type":"object","properties":{"code":{"type":"string","description":"JS 代码"}},"required":["code"]}""")),
            ChatTool.CreateFunctionTool("get_snapshot", "获取页面 Accessibility Tree 快照", BinaryData.FromString("""{"type":"object","properties":{"iframe":{"type":"array","items":{"type":"string"},"description":"可选，iframe selector 链（根→叶）快照范围；传空数组=整页快照（fallback rediscover 用）"}},"required":[]}""")),
            ChatTool.CreateFunctionTool("screenshot", "截图查看页面或元素", BinaryData.FromString("""{"type":"object","properties":{"selector":{"type":"string","description":"可选，截取指定元素"},"ref":{"type":"string","description":"快照中的元素引用编号，优先于 selector"},"quality":{"type":"integer","description":"JPEG质量0-100，默认70。验证码识别时用100"}},"required":[]}""")),
            ChatTool.CreateFunctionTool("done", "声明任务完成", BinaryData.FromString("""{"type":"object","properties":{"result":{"description":"可选返回值"}},"required":[]}""")),
        ];
    }

    #endregion

    #region 工具执行

    private async Task<string> ExecuteToolCallAsync(
        ExecutionContext ctx, AiToolCall toolCall, string[]? iframeChain, ScriptV2? script,
        Dictionary<string, object> vars, List<Dictionary<string, object>> scopeChain,
        CancellationToken ct)
    {
        var args = toolCall.Arguments ?? [];
        try
        {
            var activePage = ctx.ActivePage;  // J.3.5：用 ctx.ActivePage（switchTab 写回后下一轮读最新）
            var targetFrame = iframeChain != null && iframeChain.Length > 0
                ? FrameResolver.ResolveChain(activePage, iframeChain, scopeChain, vars)
                : activePage.Locator("body");

            // S5 url_changed 方案B 边界（H.2/N.2.2）：会改 URL 的操作工具执行前记 _lastUrl（与 StepExecutor 一致）；
            // 只读工具(get_snapshot/screenshot)跳过。保证 AI fallback 执行的操作也被后续 url_changed 检测覆盖。
            // ⚠️N.2.2 原述"执行后记"，此处按方案B 语义用"执行前记"（操作前URL 作检查点，url_changed 才能检测到本次操作导致的跳转）
            if (toolCall.Name is not "get_snapshot" and not "screenshot")
                vars["_lastUrl"] = activePage.Url;

            return toolCall.Name switch
            {
                "click" => await ExecuteAiClickAsync(activePage, targetFrame, args),
                "fill" => await ExecuteAiFillAsync(activePage, targetFrame, args),
                "type" => await ExecuteAiTypeAsync(activePage, targetFrame, args),
                "select" => await ExecuteAiSelectAsync(activePage, targetFrame, args),
                "press" => await ExecuteAiPressAsync(activePage, targetFrame, args),
                "hover" => await ExecuteAiHoverAsync(activePage, targetFrame, args),
                "scroll" => await ExecuteAiScrollAsync(activePage, targetFrame, args),
                "navigate" => await ExecuteAiNavigateAsync(activePage, args),
                "upload" => await ExecuteAiUploadAsync(activePage, targetFrame, args),
                "goBack" => await ExecuteAiGoBackAsync(activePage),
                "switchTab" => await ExecuteAiSwitchTabAsync(ctx, args),
                "closeTab" => await ExecuteAiCloseTabAsync(activePage),
                "evaluate" => await ExecuteAiEvaluateAsync(targetFrame, args),
                "get_snapshot" => await ExecuteAiGetSnapshotAsync(activePage, iframeChain, scopeChain, vars, args),
                "screenshot" => await ExecuteAiScreenshotAsync(activePage, targetFrame, args),
                _ => $"未知工具: {toolCall.Name}"
            };
        }
        catch (Exception ex)
        {
            if (IsBrowserCrashException(ex)) throw;
            return $"工具执行失败({toolCall.Name}): {ex.Message}";
        }
    }

    /// <summary>检测是否为浏览器崩溃异常（与 ScriptEngine 保持一致）</summary>
    private static bool IsBrowserCrashException(Exception ex)
    {
        var msg = ex.Message ?? "";
        return msg.Contains("closed", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("disconnected", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Target closed", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Browser closed", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> ExecuteAiClickAsync(IPage page, ILocator frame, Dictionary<string, object> args)
    {
        var loc = ResolveLocator(page, frame, args);
        await loc.ClickAsync();
        return $"已点击: {DescribeLocator(args)}";
    }

    private async Task<string> ExecuteAiFillAsync(IPage page, ILocator frame, Dictionary<string, object> args)
    {
        var loc = ResolveLocator(page, frame, args);
        var value = args.GetValueOrDefault("value")?.ToString() ?? "";
        await loc.FillAsync(value);
        return $"已填写: {DescribeLocator(args)} = {MaskToolResultValue(value)}";
    }

    private async Task<string> ExecuteAiTypeAsync(IPage page, ILocator frame, Dictionary<string, object> args)
    {
        var loc = ResolveLocator(page, frame, args);
        var value = args.GetValueOrDefault("value")?.ToString() ?? "";
        await loc.PressSequentiallyAsync(value);
        return $"已输入: {DescribeLocator(args)} += {MaskToolResultValue(value)}";
    }

    private async Task<string> ExecuteAiSelectAsync(IPage page, ILocator frame, Dictionary<string, object> args)
    {
        var loc = ResolveLocator(page, frame, args);
        var value = args.GetValueOrDefault("value")?.ToString() ?? "";
        await loc.SelectOptionAsync(new[] { value });
        return $"已选择: {DescribeLocator(args)} = {MaskToolResultValue(value)}";
    }

    private async Task<string> ExecuteAiPressAsync(IPage page, ILocator frame, Dictionary<string, object> args)
    {
        var key = args.GetValueOrDefault("key")?.ToString() ?? "Enter";
        var refValue = args.GetValueOrDefault("ref")?.ToString();
        var selector = args.GetValueOrDefault("selector")?.ToString();
        if (!string.IsNullOrEmpty(refValue))
            await page.Locator($"aria-ref={refValue}").PressAsync(key);
        else if (!string.IsNullOrEmpty(selector))
            await frame.Locator(selector).PressAsync(key);
        else
            await page.Keyboard.PressAsync(key);
        return $"已按键: {key}";
    }

    private async Task<string> ExecuteAiHoverAsync(IPage page, ILocator frame, Dictionary<string, object> args)
    {
        var loc = ResolveLocator(page, frame, args);
        await loc.HoverAsync();
        return $"已悬停: {DescribeLocator(args)}";
    }

    private async Task<string> ExecuteAiScrollAsync(IPage page, ILocator frame, Dictionary<string, object> args)
    {
        var refValue = args.GetValueOrDefault("ref")?.ToString();
        var selector = args.GetValueOrDefault("selector")?.ToString();
        var direction = args.GetValueOrDefault("direction")?.ToString();
        var amount = args.GetValueOrDefault("amount") is JsonElement ae && ae.ValueKind == JsonValueKind.Number ? ae.GetInt32() : 300;

        if (!string.IsNullOrEmpty(refValue))
            await page.Locator($"aria-ref={refValue}").ScrollIntoViewIfNeededAsync();
        else if (!string.IsNullOrEmpty(selector))
            await frame.Locator(selector).ScrollIntoViewIfNeededAsync();
        else if (direction == "down") await page.Mouse.WheelAsync(0, amount);
        else if (direction == "up") await page.Mouse.WheelAsync(0, -amount);
        else if (direction == "bottom") await page.EvaluateAsync("window.scrollTo(0, document.body.scrollHeight)");

        var target = !string.IsNullOrEmpty(refValue) ? $"aria-ref={refValue}" : selector ?? direction ?? "down";
        return $"已滚动: {target}";
    }

    private async Task<string> ExecuteAiNavigateAsync(IPage page, Dictionary<string, object> args)
    {
        var url = args.GetValueOrDefault("url")?.ToString() ?? "";
        await page.GotoAsync(url);
        return $"已导航: {url}";
    }

    private async Task<string> ExecuteAiUploadAsync(IPage page, ILocator frame, Dictionary<string, object> args)
    {
        var loc = ResolveLocator(page, frame, args);
        var filePath = args.GetValueOrDefault("filePath")?.ToString() ?? "";
        await loc.SetInputFilesAsync(filePath);
        return $"已上传: {DescribeLocator(args)}";
    }

    private async Task<string> ExecuteAiGoBackAsync(IPage page)
    {
        await page.GoBackAsync();
        return "已后退";
    }

    private async Task<string> ExecuteAiSwitchTabAsync(ExecutionContext ctx, Dictionary<string, object> args)
    {
        var index = args.GetValueOrDefault("index") is JsonElement ie && ie.ValueKind == JsonValueKind.Number ? ie.GetInt32() : -1;
        var pages = ctx.ActivePage.Context.Pages;
        if (index == -1) index = pages.Count - 1;
        if (index >= 0 && index < pages.Count)
        {
            await pages[index].BringToFrontAsync();
            ctx.ActivePage = pages[index];  // J.3.5：写回 ctx.ActivePage，后续确定性 step 用新 page
        }
        return $"已切换标签页: {index}";
    }

    private async Task<string> ExecuteAiCloseTabAsync(IPage page)
    {
        await page.CloseAsync();
        return "已关闭标签页";
    }

    private async Task<string> ExecuteAiEvaluateAsync(ILocator frame, Dictionary<string, object> args)
    {
        var code = args.GetValueOrDefault("code")?.ToString() ?? "";
        var result = await frame.EvaluateAsync<object?>(code);
        // F8(f)：evaluate 可执行任意 JS（如 document.querySelector('input[type=password]').value 读密码框），
        // 返回值扫描敏感字段真实值后脱敏，防明文经 messages 历史重发 LLM。
        return MaskEvaluateResult(result?.ToString() ?? "null");
    }

    /// <summary>F8(f)：evaluate 返回值若命中敏感字段真实值则脱敏（精确匹配，或包含长度≥4 的敏感值），防明文经 messages 历史重发 LLM。</summary>
    private string MaskEvaluateResult(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var sv in _sensitiveValues)
        {
            if (string.IsNullOrEmpty(sv)) continue;
            if (text.Equals(sv, StringComparison.Ordinal) || (sv.Length >= 4 && text.Contains(sv, StringComparison.Ordinal)))
                return "(结果含敏感值，已脱敏)";
        }
        return text;
    }

    private async Task<string> ExecuteAiGetSnapshotAsync(IPage page, string[]? iframeChain,
        List<Dictionary<string, object>> scopeChain, Dictionary<string, object> vars, Dictionary<string, object> args)
    {
        // 多 tab 场景：快照前输出标签页列表（与录制端 PageSnapshotExtractor 一致，J.5.3 双路径同步）
        var tabsSection = await BuildTabsSectionAsync(page);

        // R2-A（a-a-4 批次 E B1）：AI 传 iframe（数组）→ 用 AI 的链；传空数组 → 整页（rediscover）；不传 → 默认 iframeChain（现状）
        string[]? effective = iframeChain;
        if (args.TryGetValue("iframe", out var aiIframeRaw) && aiIframeRaw is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Array && je.GetArrayLength() == 0) effective = null;  // AI 显式传空 = 整页（rediscover）
            else if (je.ValueKind == JsonValueKind.Array) effective = je.EnumerateArray().Select(a => a.ToString()!).ToArray();  // AI 传指定链
        }
        var frame = effective is { Length: > 0 } ? FrameResolver.ResolveChain(page, effective, scopeChain, vars) : page.Locator("body");

        var snapshot = await frame.AriaSnapshotAsync(new() { Mode = AriaSnapshotMode.Ai });
        return tabsSection + (snapshot ?? "空页面");
    }

    /// <summary>构建标签页列表段（多 tab 输出，单 tab 省略），标记当前 tab。与录制端 PageSnapshotExtractor 保持一致。</summary>
    private static async Task<string> BuildTabsSectionAsync(IPage page)
    {
        var pages = page.Context.Pages;
        if (pages.Count <= 1) return "";
        int currentIndex = -1;
        for (int i = 0; i < pages.Count; i++)
            if (ReferenceEquals(pages[i], page)) { currentIndex = i; break; }

        var sb = new System.Text.StringBuilder();
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

    private async Task<string> ExecuteAiScreenshotAsync(IPage page, ILocator frame, Dictionary<string, object> args)
    {
        var refValue = args.GetValueOrDefault("ref")?.ToString();
        var selector = args.GetValueOrDefault("selector")?.ToString();
        var quality = args.GetValueOrDefault("quality") is JsonElement qe && qe.ValueKind == JsonValueKind.Number ? qe.GetInt32() : 70;
        byte[] screenshot;
        if (quality >= 100)
        {
            // PNG 不压缩
            if (!string.IsNullOrEmpty(refValue))
                screenshot = await page.Locator($"aria-ref={refValue}").ScreenshotAsync();
            else if (!string.IsNullOrEmpty(selector))
                screenshot = await frame.Locator(selector).ScreenshotAsync();
            else
                screenshot = await page.ScreenshotAsync();
        }
        else
        {
            // JPEG 压缩
            if (!string.IsNullOrEmpty(refValue))
                screenshot = await page.Locator($"aria-ref={refValue}").ScreenshotAsync(new() { Type = ScreenshotType.Jpeg, Quality = quality });
            else if (!string.IsNullOrEmpty(selector))
                screenshot = await frame.Locator(selector).ScreenshotAsync(new() { Type = ScreenshotType.Jpeg, Quality = quality });
            else
                screenshot = await page.ScreenshotAsync(new() { Type = ScreenshotType.Jpeg, Quality = quality });
        }

        _lastScreenshot = screenshot;
        return $"[截图已返回，{screenshot.Length} bytes]";
    }

    /// <summary>
    /// 解析定位器：优先用 aria-ref，否则用 CSS selector
    /// </summary>
    private static ILocator ResolveLocator(IPage page, ILocator frame, Dictionary<string, object> args)
    {
        var refValue = args.GetValueOrDefault("ref")?.ToString();
        if (!string.IsNullOrEmpty(refValue))
            return page.Locator($"aria-ref={refValue}");
        var selector = args.GetValueOrDefault("selector")?.ToString() ?? "";
        return frame.Locator(selector);
    }

    /// <summary>
    /// 描述定位方式（用于日志）
    /// </summary>
    private static string DescribeLocator(Dictionary<string, object> args)
    {
        var refValue = args.GetValueOrDefault("ref")?.ToString();
        if (!string.IsNullOrEmpty(refValue))
            return $"aria-ref={refValue}";
        return args.GetValueOrDefault("selector")?.ToString() ?? "";
    }

    #endregion
}
