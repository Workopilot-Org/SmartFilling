using System.Text.RegularExpressions;
using Microsoft.Playwright;
using SmartFilling.Engine.Logging;
using SmartFilling.Engine.Models;
using SmartFilling.Engine.Reporting;
using SmartFilling.Engine.Services;

namespace SmartFilling.Engine.Engine;

public class StepExecutor
{
    private readonly DetectEvaluator _detectEvaluator;
    private readonly EngineOptions _options;
    private readonly ILogger _logger;
    private readonly AiActionExecutor? _aiActionExecutor;
    private readonly ITaskProgressReporter? _reporter;  // #4：ai action 失败推前端 ReceiveLog
    private readonly CaptchaService? _captchaService;

    public StepExecutor(DetectEvaluator detectEvaluator, EngineOptions options, ILogger logger, CaptchaService? captchaService = null)
    {
        _detectEvaluator = detectEvaluator;
        _options = options;
        _logger = logger;
        _captchaService = captchaService;
    }

    public StepExecutor(DetectEvaluator detectEvaluator, EngineOptions options, ILogger logger, AiActionExecutor aiActionExecutor, ITaskProgressReporter? reporter = null, CaptchaService? captchaService = null)
        : this(detectEvaluator, options, logger, captchaService)
    {
        _aiActionExecutor = aiActionExecutor;
        _reporter = reporter;  // #4：ai action 失败推前端
    }

    public async Task<StepResult?> ExecuteAsync(
        StepNode step,
        ExecutionContext ctx,
        ScriptV2 script,
        string[]? phaseIframe,
        int stepIdx = 0)
    {
        var timeout = step.Timeout ?? script.Settings?.DefaultTimeout ?? _options.DefaultTimeout;

        // selector 统一 {{}} 替换
        var selector = step.Selector != null
            ? VariableHelper.ReplaceVars(step.Selector, ctx.ScopeChain, ctx.Vars)
            : null;

        var frame = FrameResolver.ResolveChain(ctx.ActivePage, IframeChainMerger.Resolve(step.Iframe, phaseIframe), ctx.ScopeChain, ctx.Vars);

        // dialog handler 注册
        EventHandler<IDialog>? dialogHandler = null;
        if (step.PreSetup?.DialogHandler != null)
        {
            dialogHandler = (_, dialog) =>
            {
                ctx.Vars["_lastDialogMessage"] = dialog.Message;
                if (step.PreSetup.DialogText == null || dialog.Message.Contains(step.PreSetup.DialogText))
                {
                    if (step.PreSetup.DialogHandler == "auto_accept") dialog.AcceptAsync();
                    else dialog.DismissAsync();
                }
                else
                {
                    // Y5：dialogText 不匹配时兜底 accept，防止对话框挂起阻塞后续操作
                    dialog.AcceptAsync();
                }
            };
            ctx.ActivePage.Dialog += dialogHandler;
        }

        StepResult? result = null;
        try
        {
            // S5 url_changed 方案B（H.2）：操作类 action 执行前记录当前 URL 作为 url_changed 检查点；
            // 检测类(check/wait/goto)不记——保留上一个操作的检查点，覆盖往返跳转 / loop 跨迭代反复跳转。
            if (step.Action is not "check" and not "wait" and not "goto")
                ctx.Vars["_lastUrl"] = ctx.ActivePage.Url;

            result = step.Action switch
            {
                "navigate" => await ExecuteNavigateAsync(step, ctx, timeout),
                "click" => await ExecuteClickAsync(step, frame, selector, timeout),
                "fill" => await ExecuteFillAsync(step, script, frame, selector, ctx, timeout),
                "type" => await ExecuteTypeAsync(step, script, frame, selector, ctx, timeout),
                "select" => await ExecuteSelectAsync(step, script, frame, selector, ctx, timeout),
                "hover" => await ExecuteHoverAsync(frame, selector, timeout),
                "pressKey" => await ExecutePressKeyAsync(step, frame, selector, ctx, timeout),
                "scroll" => await ExecuteScrollAsync(step, frame, selector, ctx, timeout),
                "upload" => await ExecuteUploadAsync(step, frame, selector, ctx),
                "extract" => await ExecuteExtractAsync(step, frame, selector, ctx, timeout),
                "evaluate" => await ExecuteEvaluateAsync(step, frame, ctx, timeout),
                "wait" => await ExecuteWaitAsync(step, ctx, script, phaseIframe, timeout),
                "check" => await ExecuteCheckAsync(step, ctx, script, phaseIframe),
                "handleDialog" => await ExecuteHandleDialogAsync(step, ctx, timeout),
                "screenshot" => await ExecuteScreenshotAsync(step, ctx, script, phaseIframe, selector, stepIdx),
                "goBack" => await ExecuteGoBackAsync(ctx, timeout),
                "reload" => await ExecuteReloadAsync(ctx, timeout),
                "switchTab" => await ExecuteSwitchTabAsync(step, ctx),
                "closeTab" => await ExecuteCloseTabAsync(step, ctx),
                "ai" => await ExecuteAiAsync(step, ctx, script, phaseIframe),
                "captcha" => await ExecuteCaptchaAsync(step, frame, selector, ctx),
                "goto" => new StepResult(null) { ControlFlow = new(step with { Then = "goto" }) },
                _ => throw new NotSupportedException($"未知的 action: {step.Action}")
            };

        }
        finally
        {
            if (dialogHandler != null)
                ctx.ActivePage.Dialog -= dialogHandler;
        }

        return result;
    }

    private async Task<StepResult?> ExecuteNavigateAsync(StepNode step, ExecutionContext ctx, int timeout)
    {
        var url = VariableHelper.ReplaceVars(step.Url ?? "", ctx.ScopeChain, ctx.Vars);
        await ctx.ActivePage.GotoAsync(url, new() { Timeout = timeout });
        return new StepResult(ctx.ActivePage.Url);
    }

    private async Task<StepResult?> ExecuteClickAsync(StepNode step, ILocator frame, string? selector, int timeout)
    {
        var locator = frame.Locator(selector!);
        var target = step.UseLast == true ? locator.Last : locator.First;
        if (step.DoubleClick == true)
            await target.DblClickAsync(new() { Timeout = timeout });
        else
            await target.ClickAsync(new() { Timeout = timeout });

        if (step.StoreAs == null)
            return null;
        var text = await target.InnerTextAsync();
        return new StepResult(text);
    }

    private async Task<StepResult?> ExecuteFillAsync(StepNode step, ScriptV2 script, ILocator frame, string? selector, ExecutionContext ctx, int timeout)
    {
        var rawValue = VariableHelper.ReplaceVars(step.Value ?? "", ctx.ScopeChain, ctx.Vars);
        var fieldName = step.Field ?? VariableHelper.InferFieldFromValue(step.Value);
        var fieldDef = VariableHelper.GetFieldDefinition(fieldName, script);  // 改动7：递归查找（修 N5）
        var value = VariableHelper.ApplyFormat(VariableHelper.ApplyTransform(rawValue, fieldDef?.Transform), fieldDef?.Format, fieldDef?.Type);
        await frame.Locator(selector!).FillAsync(value, new() { Timeout = timeout });
        if (step.PressEnter == true)
            await frame.Locator(selector!).PressAsync("Enter", new() { Timeout = timeout });

        if (step.StoreAs == null)
            return new StepResult(value);
        var actual = await frame.Locator(selector!).InputValueAsync();
        return new StepResult(actual);
    }

    private async Task<StepResult?> ExecuteTypeAsync(StepNode step, ScriptV2 script, ILocator frame, string? selector, ExecutionContext ctx, int timeout)
    {
        var rawValue = VariableHelper.ReplaceVars(step.Value ?? "", ctx.ScopeChain, ctx.Vars);
        var fieldName = step.Field ?? VariableHelper.InferFieldFromValue(step.Value);
        var fieldDef = VariableHelper.GetFieldDefinition(fieldName, script);  // 改动7：递归查找（修 N5）
        var value = VariableHelper.ApplyFormat(VariableHelper.ApplyTransform(rawValue, fieldDef?.Transform), fieldDef?.Format, fieldDef?.Type);
        await frame.Locator(selector!).PressSequentiallyAsync(value, new() { Timeout = timeout });
        if (step.PressEnter == true)
            await frame.Locator(selector!).PressAsync("Enter", new() { Timeout = timeout });

        if (step.StoreAs == null)
            return new StepResult(value);
        var actual = await frame.Locator(selector!).InputValueAsync();
        return new StepResult(actual);
    }

    private async Task<StepResult?> ExecuteSelectAsync(StepNode step, ScriptV2 script, ILocator frame, string? selector, ExecutionContext ctx, int timeout)
    {
        var rawValue = VariableHelper.ReplaceVars(step.Value ?? "", ctx.ScopeChain, ctx.Vars);
        var fieldName = step.Field ?? VariableHelper.InferFieldFromValue(step.Value);
        var fieldDef = VariableHelper.GetFieldDefinition(fieldName, script);  // N10/F.4.6：select 补 Field 推断
        var value = VariableHelper.ApplyTransform(rawValue, fieldDef?.Transform);
        // 改动7：format 仅 matchBy=value（含默认）且 type=date/number 时应用；label/index 不转防破坏匹配
        if (step.MatchBy is null or "value")
            value = VariableHelper.ApplyFormat(value, fieldDef?.Format, fieldDef?.Type);
        switch (step.MatchBy)
        {
            case "label":
                await frame.Locator(selector!).SelectOptionAsync(
                    new[] { new SelectOptionValue { Label = value } },
                    new() { Timeout = timeout });
                break;
            case "index":
                // Y12：matchBy=index 解析失败抛错（原静默降级为 value 匹配，与 index 意图不符）
                if (!int.TryParse(value, out var idx))
                    throw new ArgumentException($"select matchBy=index 需要整数值，实际: '{value}'");
                await frame.Locator(selector!).SelectOptionAsync(
                    new[] { new SelectOptionValue { Index = idx } },
                    new() { Timeout = timeout });
                break;
            default:
                await frame.Locator(selector!).SelectOptionAsync(new[] { value }, new() { Timeout = timeout });
                break;
        }

        if (step.StoreAs == null)
            return new StepResult(value);
        try
        {
            var actual = await frame.Locator(selector!).InputValueAsync();
            return new StepResult(actual);
        }
        catch (Exception ex) when (ex.Message.Contains("multi-select", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning($"select storeAs 不支持多选（InputValueAsync 对 multi-select 抛错），跳过 storeAs");
            return new StepResult(value);
        }
    }

    private async Task<StepResult?> ExecuteHoverAsync(ILocator frame, string? selector, int timeout)
    {
        await frame.Locator(selector!).HoverAsync(new() { Timeout = timeout });
        return null;
    }

    private async Task<StepResult?> ExecutePressKeyAsync(StepNode step, ILocator frame, string? selector, ExecutionContext ctx, int timeout)
    {
        var key = VariableHelper.ReplaceVars(step.Key ?? "", ctx.ScopeChain, ctx.Vars);
        if (!string.IsNullOrEmpty(selector))
            await frame.Locator(selector).PressAsync(key, new() { Timeout = timeout });
        else
            await ctx.ActivePage.Keyboard.PressAsync(key);
        return null;
    }

    private async Task<StepResult?> ExecuteScrollAsync(StepNode step, ILocator frame, string? selector, ExecutionContext ctx, int timeout)
    {
        // 批次9③：严格二选一（同传/都不传都报错）+ 非法 direction 报错（撤销原 direction 优先软兜底 + Y10 仅都不传 throw）
        var hasSelector = !string.IsNullOrEmpty(selector);
        var hasDirection = !string.IsNullOrEmpty(step.Direction);  // 批次9 核查第9轮🟡3：空串视为未指定（统一 ScriptLoader ValidateScrollExclusive + 录制）
        if (hasSelector && hasDirection)
            throw new ArgumentException("scroll 的 selector 与 direction 互斥，不能同时指定");
        if (!hasSelector && !hasDirection)
            throw new ArgumentException("scroll 需指定 selector（滚到元素）或 direction（页面滚动）");

        if (hasSelector)
        {
            await frame.Locator(selector!).ScrollIntoViewIfNeededAsync(new() { Timeout = timeout });
        }
        else
        {
            var amount = step.Amount ?? 300;
            switch (step.Direction)
            {
                case "down": await ctx.ActivePage.Mouse.WheelAsync(0, amount); break;
                case "up": await ctx.ActivePage.Mouse.WheelAsync(0, -amount); break;
                case "left": await ctx.ActivePage.Mouse.WheelAsync(-amount, 0); break;
                case "right": await ctx.ActivePage.Mouse.WheelAsync(amount, 0); break;
                case "bottom": await ctx.ActivePage.EvaluateAsync("window.scrollTo(0, document.body.scrollHeight)"); break;
                case "top": await ctx.ActivePage.EvaluateAsync("window.scrollTo(0, 0)"); break;
                default: throw new ArgumentException($"scroll 非法 direction: '{step.Direction}'（合法: up/down/left/right/bottom/top）");
            }
        }
        return null;
    }

    private async Task<StepResult?> ExecuteUploadAsync(StepNode step, ILocator frame, string? selector, ExecutionContext ctx)
    {
        // #9：filePath 未配置（null/空串）直接报错（原守卫因空串被 ResolveUploadValue 解析成 [rootPath] 非空数组而失效）
        if (string.IsNullOrEmpty(step.FilePath))
            throw new ArgumentException("upload 未配置 filePath");

        // 附件根目录：UploadRootPath 空→BaseDirectory（Engine 层独立兜底）。
        // 生产环境（Worker/App）启动时 PostConfigure 已把 ContentRootPath 注入 UploadRootPath，故此处读到的即 ContentRoot（与 AttachmentService 下载保存根对齐）；
        // BaseDirectory 仅在 Engine 脱离宿主独立运行（如单元测试）时兜底。
        var rootPath = string.IsNullOrEmpty(_options.UploadRootPath)
            ? AppContext.BaseDirectory
            : _options.UploadRootPath;

        // 优先使用 ResolveRawValue：纯变量引用时直接取原始对象值（如附件数组）
        var rawValue = VariableHelper.ResolveRawValue(step.FilePath, ctx.ScopeChain, ctx.Vars, _logger);  // R2-12：传 logger 记录嵌套解析中断
        string[] files;
        if (rawValue != null)
        {
            files = VariableHelper.ResolveUploadValue(rawValue, rootPath);
        }
        else
        {
            var rawPath = VariableHelper.ReplaceVars(step.FilePath ?? "", ctx.ScopeChain, ctx.Vars);
            files = VariableHelper.ResolveUploadValue(rawPath, rootPath);
        }

        // G.3.3/H.4.2：区分配置缺失（FilePath 未配→报错）vs 附件未下载（path 全空→告警跳过），原一律静默跳过误伤
        if (files.Length == 0)
        {
            if (string.IsNullOrEmpty(step.FilePath))
                throw new ArgumentException("upload 未配置 filePath");
            _logger.LogWarning($"upload 附件未下载或 path 全空（filePath={step.FilePath}），跳过上传");
            return null;
        }
        await frame.Locator(selector!).SetInputFilesAsync(files);
        return null;
    }

    private async Task<StepResult?> ExecuteExtractAsync(StepNode step, ILocator frame, string? selector, ExecutionContext ctx, int timeout)
    {
        object? ext;
        if (step.ExtractType == "url")
            ext = ctx.ActivePage.Url;
        else if (step.ExtractType == "title")
            ext = await ctx.ActivePage.TitleAsync();
        else if (step.ExtractType is not null and not "property")
            throw new NotSupportedException($"不支持的 extractType: {step.ExtractType}");
        else if (step.Property == "count")
            ext = await frame.Locator(selector!).CountAsync();
        else if (step.Property == "checked")
            ext = await frame.Locator(selector!).IsCheckedAsync();
        else if (step.Property == "value")
            ext = await frame.Locator(selector!).InputValueAsync(new() { Timeout = timeout });
        else if (step.Property is "textContent" or "innerText")
            ext = await frame.Locator(selector!).InnerTextAsync(new() { Timeout = timeout });
        else
            ext = await frame.Locator(selector!).GetAttributeAsync(step.Property ?? "", new() { Timeout = timeout });

        if (step.Regex != null && ext is string txt)
        {
            var m = System.Text.RegularExpressions.Regex.Match(txt, step.Regex);
            // #34：无捕获组兜底 Groups[0]（原 Groups[1] 在无捕获组时抛 ArgumentOutOfRangeException）
            ext = m.Success ? (m.Groups.Count > 1 ? m.Groups[1].Value : m.Groups[0].Value) : null;
        }
        return new StepResult(ext);
    }

    private async Task<StepResult?> ExecuteEvaluateAsync(StepNode step, ILocator frame, ExecutionContext ctx, int timeout)
    {
        var code = VariableHelper.ReplaceVars(step.Code ?? "", ctx.ScopeChain, ctx.Vars);
        var args = VariableHelper.ReplaceArgsVars(step.Args, ctx.ScopeChain, ctx.Vars)?.ToArray();
        var result = await frame.EvaluateAsync<object?>(code, args);
        return new StepResult(result);
    }

    private async Task<StepResult?> ExecuteWaitAsync(StepNode step, ExecutionContext ctx, ScriptV2 script, string[]? phaseIframe, int timeout)
    {
        // Y7：ms 与 until 互斥（同时设时 ms 走、until 静默失效），校验报错
        if (step.Ms != null && step.Until != null)
            throw new ArgumentException("wait 不能同时指定 ms 和 until（请二选一）");

        if (step.Ms != null)
        {
            await Task.Delay(step.Ms.Value, ctx.Ct);
            return null;
        }

        if (step.Until != null)
        {
            var pollInterval = step.PollInterval ?? _options.WaitCheckInterval;
            var effectiveTimeout = step.Timeout ?? timeout;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!await _detectEvaluator.EvaluateAsync(step.Until, ctx.ActivePage, script, phaseIframe, ctx.Vars, ctx.ScopeChain))
            {
                if (sw.ElapsedMilliseconds > effectiveTimeout)
                {
                    // S5 改动6.3 诊断性错误：补 url 跳转确定性事实（当前URL vs 上一操作前URL），供 AI fallback 判断"没跳转/跳了但 until 未满足"
                    var prevUrl = ctx.Vars.TryGetValue("_lastUrl", out var lu2) ? lu2?.ToString() : null;
                    var curUrl = ctx.ActivePage.Url;
                    var jumped = !string.Equals(prevUrl, curUrl, StringComparison.Ordinal);
                    throw new TimeoutException($"等待条件超时({effectiveTimeout}ms)。页面URL={curUrl}" +
                        (prevUrl != null ? $"，上一操作前URL={prevUrl}，" + (jumped ? "已发生跳转（url_changed=true，但 until 条件仍未满足）" : "未发生跳转（url_changed=false）") : ""));

                }

                await Task.Delay(pollInterval, ctx.Ct);
            }
        }
        return null;
    }

    private async Task<StepResult?> ExecuteCheckAsync(StepNode step, ExecutionContext ctx, ScriptV2 script, string[]? phaseIframe)
    {
        if (step.Detect == null) return null;
        var trigger = await _detectEvaluator.EvaluateAsync(step.Detect, ctx.ActivePage, script, phaseIframe, ctx.Vars, ctx.ScopeChain);
        return trigger ? new StepResult(null) { ControlFlow = new(step) } : null;
    }

    private async Task<StepResult?> ExecuteHandleDialogAsync(StepNode step, ExecutionContext ctx, int timeout)
    {
        var dialogTcs = new TaskCompletionSource<IDialog>();
        EventHandler<IDialog> handler = (_, d) => dialogTcs.TrySetResult(d);
        ctx.ActivePage.Dialog += handler;
        try
        {
            // #25：链接 ctx.Ct，任务取消时等待可被取消（原未链接，取消请求挂起至 timeout）
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Ct);
            cts.CancelAfter(timeout);
            cts.Token.Register(() => dialogTcs.TrySetCanceled());
            var dialog = await dialogTcs.Task;
            ctx.Vars["_lastDialogMessage"] = dialog.Message;
            if (step.Accept ?? true)
                await dialog.AcceptAsync(step.DialogPromptText);
            else
                await dialog.DismissAsync();
            return new StepResult(dialog.Message);
        }
        finally
        {
            ctx.ActivePage.Dialog -= handler;
        }
    }

    private async Task<StepResult?> ExecuteScreenshotAsync(StepNode step, ExecutionContext ctx,
        ScriptV2 script, string[]? phaseIframe, string? selector, int stepIdx)
    {
        // 1. 根据 ScreenshotType 截图到内存
        byte[] bytes;
        switch (step.ScreenshotType)
        {
            case "fullPage":
                bytes = await ctx.ActivePage.ScreenshotAsync(new() { FullPage = true });
                break;
            case "element":
                if (selector == null) throw new ArgumentException("element 截图需要 selector");
                var frame = FrameResolver.ResolveChain(ctx.ActivePage, IframeChainMerger.Resolve(step.Iframe, phaseIframe), ctx.ScopeChain, ctx.Vars);
                bytes = await frame.Locator(selector).ScreenshotAsync();
                break;
            default: // null 或 "viewport"
                bytes = await ctx.ActivePage.ScreenshotAsync();
                break;
        }

        // 2. 保存文件（saveToFile 默认 true）
        string? filePath = null;
        if (step.SaveToFile != false)
        {
            // 三级覆盖：step.folder → script.Settings.Screenshot.ActionDefaultFolder → appsettings Screenshot.ActionDefaultFolder
            var folder = step.Folder != null
                ? VariableHelper.ReplaceVars(step.Folder, ctx.ScopeChain, ctx.Vars)
                : script.Settings?.Screenshot?.ActionDefaultFolder
                    ?? _options.Screenshot?.ActionDefaultFolder
                    ?? "screenshots/";
            string filename;
            if (!string.IsNullOrEmpty(step.Filename))
            {
                var resolved = VariableHelper.ReplaceVars(step.Filename, ctx.ScopeChain, ctx.Vars);
                filename = resolved.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    ? resolved : $"{resolved}.png";
            }
            else
            {
                var segments = new List<string>();
                if (ctx.TaskId == null) segments.Add("recording");
                if (!string.IsNullOrEmpty(ctx.ScriptId)) segments.Add(ctx.ScriptId);
                if (!string.IsNullOrEmpty(ctx.TaskId)) segments.Add(ctx.TaskId);
                if (!string.IsNullOrEmpty(ctx.PhaseName)) segments.Add(ctx.PhaseName);
                segments.Add(step.Name ?? stepIdx.ToString() ?? "unknown");
                segments.Add(DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                filename = $"{string.Join("_", segments)}.png";
            }
            filePath = System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, filename));
            var dir = System.IO.Path.GetDirectoryName(filePath);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(filePath, bytes);
        }

        // 3. 无 storeAs → 仅保存文件，返回 null
        if (step.StoreAs == null)
            return null;

        // 4. 根据 storeContent 决定存什么到变量
        return step.StoreContent switch
        {
            "dataUrl" => new StepResult($"data:image/png;base64,{Convert.ToBase64String(bytes)}"),
            "both" => new StepResult(new Dictionary<string, object>
            {
                ["dataUrl"] = $"data:image/png;base64,{Convert.ToBase64String(bytes)}",
                ["path"] = filePath ?? throw new InvalidOperationException(
                    "storeContent=both 但未保存文件（saveToFile=false 时不能用 both）")
            }),
            _ => new StepResult(filePath ?? throw new InvalidOperationException(
                "storeContent=path 但未保存文件（saveToFile=false 时不能为 path）"))
        };
    }

    private async Task<StepResult?> ExecuteGoBackAsync(ExecutionContext ctx, int timeout)
    {
        await ctx.ActivePage.GoBackAsync(new() { Timeout = timeout });
        return null;
    }

    private async Task<StepResult?> ExecuteReloadAsync(ExecutionContext ctx, int timeout)
    {
        await ctx.ActivePage.ReloadAsync(new() { Timeout = timeout });
        return null;
    }

    private async Task<StepResult?> ExecuteSwitchTabAsync(StepNode step, ExecutionContext ctx)
    {
        var pages = ctx.Page.Context.Pages;
        var idx = step.Index ?? -1;
        if (idx == -1) idx = pages.Count - 1;
        if (idx >= 0 && idx < pages.Count)
        {
            ctx.ActivePage = pages[idx];
            await ctx.ActivePage.BringToFrontAsync();
        }
        else
        {
            throw new InvalidOperationException($"switchTab 索引 {idx} 越界（共 {pages.Count} 个标签页）");  // 批次8：撤销 Y14 静默，越界抛错触发 onError（原保持当前页致后续步骤全在错误 tab）
        }
        return new StepResult(ctx.ActivePage.Url);
    }

    private async Task<StepResult?> ExecuteCloseTabAsync(StepNode step, ExecutionContext ctx)
    {
        // 批次8：撤销 Y14 静默跳过，越界抛错触发 onError（与 switchTab 统一）
        var pages = ctx.Page.Context.Pages;
        if (step.Index.HasValue)
        {
            if (step.Index.Value >= 0 && step.Index.Value < pages.Count)
                await pages[step.Index.Value].CloseAsync();
            else
                throw new InvalidOperationException($"closeTab 索引 {step.Index.Value} 越界（共 {pages.Count} 个标签页）");
        }
        else
            await ctx.ActivePage.CloseAsync();
        return null;
    }

    private async Task<StepResult?> ExecuteAiAsync(StepNode step, ExecutionContext ctx, ScriptV2 script, string[]? phaseIframe)
    {
        if (_aiActionExecutor == null)
            throw new InvalidOperationException("ai action 需要 IAiProvider，但未配置");

        var aiDescription = VariableHelper.ReplaceVars(step.Description ?? "", ctx.ScopeChain, ctx.Vars);
        var aiMaxTurns = step.MaxAiTurns ?? _options.MaxAiTurns;
        var aiTotalTimeout = _options.DefaultAiTimeout;  // H.5.2/H.4.4：AI 用独立超时，不套 step.Timeout（确定性操作超时套到多轮 AI 必超时）
        var aiIframe = IframeChainMerger.Resolve(step.Iframe, phaseIframe);

        var aiResult = await _aiActionExecutor.ExecuteAsync(
            ctx.ActivePage, ctx, aiDescription, aiIframe, script,
            aiMaxTurns, aiTotalTimeout, ctx.Vars, ctx.ScopeChain,
            AiScene.AiAction, ct: ctx.Ct);  // #3 D：ai action 场景（仅 BaseSystemPrompt）

        if (!aiResult.Success)
        {
            // #4：ai action 失败推前端（fire-and-forget 不阻塞 throw；含原因/轮次/token）。日志路径不脱敏。
            _ = _reporter?.SendLogAsync($"❌ AI step 失败: {aiResult.ErrorMessage} 轮次={aiResult.TurnsUsed} token={aiResult.InputTokens + aiResult.OutputTokens}");
            throw new InvalidOperationException($"AI step 失败: {aiResult.ErrorMessage}");
        }

        return new StepResult(aiResult.ResultValue)
        {
            AiTurnsUsed = aiResult.TurnsUsed,
            AiInputTokens = aiResult.InputTokens,
            AiOutputTokens = aiResult.OutputTokens,
            SnapshotCount = aiResult.SnapshotCount,
            AiScreenshotCount = aiResult.AiScreenshotCount
        };
    }

    private async Task<StepResult?> ExecuteCaptchaAsync(StepNode step, ILocator frame, string? selector, ExecutionContext ctx)
    {
        if (_captchaService == null)
            throw new InvalidOperationException("captcha action 需要 CaptchaService，请通过 DI 注入");

        var captchaService = _captchaService;

        // H.4.5：删内层 retry 循环——captcha 不自建 retry，失败直接抛错由外层 ExecuteStepWithRetryAndFallbackAsync 统一 retry（重新执行含截图全流程，语义保留）
        switch (step.CaptchaType)
        {
                    case "text":
                        {
                            var imgSelector = step.ImageSelector ?? selector ?? throw new ArgumentException("captcha text 需要 imageSelector");
                            var screenshot = await frame.Locator(imgSelector).ScreenshotAsync();
                            var result = await captchaService.ClassifyAsync(screenshot, ctx.Ct);
                            if (!string.IsNullOrEmpty(result) && step.InputSelector != null)
                                await frame.Locator(step.InputSelector).FillAsync(result);
                            return new StepResult(result);
                        }
                    case "slide":
                        {
                            var targetSelector = step.TargetSelector ?? throw new ArgumentException("captcha slide 需要 targetSelector");
                            var bgSelector = step.BackgroundSelector ?? throw new ArgumentException("captcha slide 需要 backgroundSelector");
                            var targetScreenshot = await frame.Locator(targetSelector).ScreenshotAsync();
                            var bgScreenshot = await frame.Locator(bgSelector).ScreenshotAsync();
                            var offset = await captchaService.SlideMatchAsync(targetScreenshot, bgScreenshot, ctx.Ct);
                            var sliderSelector = step.SliderSelector ?? throw new ArgumentException("captcha slide 需要 sliderSelector");
                            var slider = frame.Locator(sliderSelector);
                            var box = await slider.BoundingBoxAsync();
                            if (box != null)
                            {
                                await ctx.ActivePage.Mouse.MoveAsync(box.X + box.Width / 2, box.Y + box.Height / 2);
                                await ctx.ActivePage.Mouse.DownAsync();
                                await ctx.ActivePage.Mouse.MoveAsync(box.X + offset, box.Y + box.Height / 2, new() { Steps = 10 });
                                await ctx.ActivePage.Mouse.UpAsync();
                            }
                            return new StepResult($"slide:{offset}");
                        }
                    case "pixel":
                        {
                            var sliderSelector = step.SliderSelector ?? throw new ArgumentException("captcha pixel 需要 sliderSelector");
                            // F2：ddddocr slide_comparison 契约——target（不带缺口完整背景图）+ background（带缺口背景图）对比找缺口位置。
                            // 原代码截同一 bgSelector 两次（两图恒等→偏移≈0，pixel 型必失败），改为截两不同元素。
                            var targetSelector = step.TargetSelector ?? throw new ArgumentException("captcha pixel 需要 targetSelector（不带缺口的完整背景图）");
                            var bgSelector = step.BackgroundSelector ?? throw new ArgumentException("captcha pixel 需要 backgroundSelector（带缺口的背景图）");
                            var targetScreenshot = await frame.Locator(targetSelector).ScreenshotAsync();
                            var bgScreenshot = await frame.Locator(bgSelector).ScreenshotAsync();
                            var offset = await captchaService.SlideComparisonAsync(targetScreenshot, bgScreenshot, ctx.Ct);
                            var slider = frame.Locator(sliderSelector);
                            var box = await slider.BoundingBoxAsync();
                            if (box != null)
                            {
                                await ctx.ActivePage.Mouse.MoveAsync(box.X + box.Width / 2, box.Y + box.Height / 2);
                                await ctx.ActivePage.Mouse.DownAsync();
                                await ctx.ActivePage.Mouse.MoveAsync(box.X + offset, box.Y + box.Height / 2, new() { Steps = 10 });
                                await ctx.ActivePage.Mouse.UpAsync();
                            }
                            return new StepResult($"pixel:{offset}");
                        }
                    case "click":
                        {
                            // ④-1/3.F：click 补全（按提示顺序点击指定文字）。原仅 foreach 点所有 detection 坐标，不用 Text/不匹配/不按序（ClickDetectionResult.Text 原 dead）。
                            var imgSelector = step.ImageSelector ?? selector ?? throw new ArgumentException("captcha click 需要 imageSelector");
                            var imgLocator = frame.Locator(imgSelector);
                            var screenshot = await imgLocator.ScreenshotAsync();
                            var detection = await captchaService.DetectClickAsync(screenshot, ctx.Ct);
                            var plan = await ResolveClickOrderAsync(step, frame, captchaService, detection, ctx);
                            if (plan.Order.Count == 0)
                                throw new InvalidOperationException("未能解析点选验证码的点击顺序（提示文字）");
                            // ④-5：图像像素坐标 → 页面坐标，叠加 imageSelector 的 BoundingBox 偏移（Headless 默认 DPR=1，CSS 像素 1:1；Playwright BoundingBox 即便 iframe 内也返回主视口坐标）。
                            var box = await imgLocator.BoundingBoxAsync() ?? throw new InvalidOperationException("无法获取验证码元素位置（BoundingBox）");
                            // 在候选集中按序匹配（路径 B 候选=非顶部行，避免误点提示行字）；每字点一次，点过的移除。
                            var remaining = new List<DetectedItem>(plan.Candidates);
                            foreach (var word in plan.Order)
                            {
                                var match = remaining.FirstOrDefault(i => (i.Text ?? "").Trim().Equals(word, StringComparison.OrdinalIgnoreCase))
                                    ?? throw new InvalidOperationException($"未匹配到目标字：{word}");
                                remaining.Remove(match);
                                await ctx.ActivePage.Mouse.ClickAsync(box.X + match.X, box.Y + match.Y);
                                await Task.Delay(300);
                            }
                            return new StepResult($"click:{plan.Order.Count}/{plan.Candidates.Count}");
                        }
                    default:
                        throw new NotSupportedException($"不支持的 captchaType: {step.CaptchaType}");
                }
    }

    /// <summary>
    /// 解析点选验证码点击顺序与候选集（3.F）。
    /// 路径 A（targetSelector 提供）：读提示元素 textContent（DOM 最准）；空（canvas）则截图 OCR；去引导词 + 分割。候选 = 全部 detection.Items（提示在独立元素，不在图内）。
    /// 路径 B（无 targetSelector，提示在图内）：顶部行（最小 Y 连续组）按 X 排序 = 提示序列（Order）；其余 = 可点击字候选（Candidates）。
    ///   注意：路径 B 必须在候选集（非顶部行）中匹配，否则会误点提示行字（报告 3.F「其余=可点击字候选」语义；伪代码 detection.Items.FirstOrDefault 为简化，实际按候选集）。
    /// 路径 B 启发式对非顶部提示的少数验证码不准 → click 抛错→外层 retry/AI fallback（ExecuteStepWithRetryAndFallbackAsync）。
    /// </summary>
    private async Task<ClickPlan> ResolveClickOrderAsync(StepNode step, ILocator frame, CaptchaService captchaService, ClickDetectionResult detection, ExecutionContext ctx)
    {
        // 路径 A：targetSelector 指向提示文字元素（设计方案 :646 click=提示文字元素）
        if (!string.IsNullOrEmpty(step.TargetSelector))
        {
            var promptLocator = frame.Locator(step.TargetSelector);
            string text = "";
            try { text = await promptLocator.TextContentAsync() ?? ""; } catch { text = ""; }
            if (string.IsNullOrWhiteSpace(text))
            {
                // canvas/图片渲染，textContent 读不到 → 截图 OCR
                try
                {
                    var promptShot = await promptLocator.ScreenshotAsync();
                    text = await captchaService.ClassifyAsync(promptShot, ctx.Ct);
                }
                catch { text = ""; }
            }
            return new ClickPlan(ParseClickPrompt(text), detection.Items);
        }

        // 路径 B：无 targetSelector，提示在图内 → 顶部行=提示（Order，去除引导词字符），其余=候选（Candidates）
        var topRow = SelectTopRowItems(detection.Items);
        var topRefs = new HashSet<DetectedItem>(topRow, ReferenceEqualityComparer.Instance);
        var candidates = detection.Items.Where(i => !topRefs.Contains(i)).ToList();
        var order = StripGuideChars(topRow.OrderBy(i => i.X).Select(i => i.Text ?? ""));
        return new ClickPlan(order, candidates);
    }

    /// <summary>点击计划：Order=按序要点的字，Candidates=在这些候选框中匹配（路径 B=非顶部行）。</summary>
    private sealed record ClickPlan(List<string> Order, List<DetectedItem> Candidates);

    /// <summary>路径 B：从顶部行文本去除点选验证码常见引导词字符（请依次点击/按顺序/找出/选择 等），保留目标字（按出现顺序）。</summary>
    private static readonly HashSet<char> ClickGuideChars = new("请依次按顺序点击找出选择为把给的对中图下方：:.,，、、 \t");
    public static List<string> StripGuideChars(IEnumerable<string> texts)
        => texts.SelectMany(s => s.Where(c => !ClickGuideChars.Contains(c))).Select(c => c.ToString()).ToList();

    /// <summary>路径 A：解析提示文字为点击字序列（去引导词前缀 + 按分隔符分割，保留 token 整体）。</summary>
    public static List<string> ParseClickPrompt(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        // 去常见引导词（贪心匹配，防"请按顺序点击"等变体）
        var cleaned = Regex.Replace(text, @"(请依次点击|请按.{0,4}点击|请点击|依次点击|点击|请找出|找出|请选择|选择)", "");
        cleaned = cleaned.TrimStart('：', ':', ' ', '　', '\t');
        // 按分隔符（顿号/空格/逗号/中文逗号/斜杠/竖线）分割
        var tokens = cleaned.Split(['、', ' ', '　', ',', '，', '/', '|'], StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>();
        foreach (var t in tokens)
        {
            var s = t.Trim(' ', '　', '\t', ':', '：');
            if (!string.IsNullOrEmpty(s)) result.Add(s);
        }
        return result;
    }

    /// <summary>路径 B：从 detection 框取顶部一行（最小 Y 连续组），按 X 升序。启发式容差 = max(8, Y 跨度/4)。</summary>
    public static List<DetectedItem> SelectTopRowItems(List<DetectedItem> items)
    {
        if (items == null || items.Count == 0) return [];
        var orderedByY = items.OrderBy(i => i.Y).ToList();
        var minY = orderedByY[0].Y;
        var maxY = orderedByY[^1].Y;
        var yTolerance = Math.Max(8, (maxY - minY) / 4);  // 提示行通常独立成行于最顶部，覆盖顶部约 1/4
        return orderedByY.Where(i => i.Y <= minY + yTolerance).OrderBy(i => i.X).ToList();
    }
}
