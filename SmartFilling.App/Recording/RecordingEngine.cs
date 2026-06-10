using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using SmartFilling.App.Configuration;
using SmartFilling.App.Hubs;
using SmartFilling.App.Prompts;
using SmartFilling.App.Services;
using SmartFilling.App.Models;
using SmartFilling.Engine.Ai;
using SmartFilling.Engine.Engine;
using SmartFilling.Engine.Models;
using SmartFilling.Engine.Reporting;
using EngineILogger = SmartFilling.Engine.Logging.ILogger;

namespace SmartFilling.App.Recording;

/// <summary>
/// 录制引擎 — Playwright SDK 直连 + AI Tool Calling，生成 v2 脚本
/// </summary>
public class RecordingEngine
{
    private readonly IAiProvider _aiProvider;
    private readonly ScriptService _scriptService;
    private readonly EngineILogger _logger;
    private readonly EngineOptions _engineOptions;
    private readonly AppOptions _appOptions;
    private readonly ConversationCompressor _compressor;
    private readonly RecordingActionExecutor _actionExecutor;
    private readonly AiActionExecutor _aiActionExecutor;  // B2（2c）：priority 8/求助选项 d 录制期执行 ai action（推进页面，否则录制断链）
    private readonly SelectorExtractor _selectorExtractor = new();  // 放法 X 块A R5：priority 8 step/detect/save_step/phase 抓元素 outerHTML 喂 (l)/(b)（CaptureElementContextAsync 现抓真 outerHTML 全量）

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage? _page;
    private ScriptV2 _script = new();
    private List<ChatMessage> _messages = [];
    // Phase 级管理：动态积累，不预规划
    private readonly List<PhaseNode> _phases = [];
    private string _currentPhaseName = "";
    private int _totalSteps = 0;
    private string? _taskDescription;  // 存储任务描述，用于每轮更新初始用户消息
    private int _lastPageCount = 1;
    private string[]? _lastIframeChain;  // 形态 A：最近一次 operate 反推的 iframe selector 链（get_snapshot 传参用，9.2 成败都更新）
    // D-A 集合机制（a-a-4 结论1/2，P1.2）：acceptFragile（用户选(f) 接受脆弱 selector/iframe，跳 AssessFragility）+
    // acceptAsIs（用户选(h) 用当前值，跳存在性校验）两集合共享 FragileType 6 类枚举，存 (FragileType, 值) 元组。
    // step 落盘后清空（ClearAcceptedSets）；求助返回（未落盘）不清空（保留下轮重试命中）。
    private readonly HashSet<(FragileType Type, string Value)> _acceptedFragile = new();
    private readonly HashSet<(FragileType Type, string Value)> _acceptedAsIs = new();
    private bool _isDone;
    // 取消原因标记（判据：区分总超时 vs 手动取消）。Stop 端点 MarkManualCancel 标记手动取消；空=cts 自动到期→总超时。
    // 单 CTS 模式（RecordController 的 cts 既是超时又是手动取消）下，ct.IsCancellationRequested 无法区分二者（区别于 ScriptEngine 的 linked CTS + 外部 ct 判据），故用此字段。
    // ??= 幂等：手动 Stop 与总超时几乎同时发生时，谁先设谁归属，两终态都抛 RecordingCancelledException，不会 silent-success。
    private string? _cancelReason;
    private TaskCompletionSource<string>? _pendingHelpTcs;
    private string? _username;  // F8：系统凭据（RecordAsync 设），operate 执行层替换 {{username}} 填浏览器用
    private string? _password;  // F8：系统凭据，明文不进 AI 上下文（BuildDataHints 仅占位符提示）
    private List<AttachmentInfo>? _attachments;  // 批次7-C3：录制期附件（RecordController.Start 传入；upload 执行层 SetInputFiles + BuildDataHints 提示用）

    // 辅助属性
    private PhaseNode CurrentPhase => FindPhase(_phases, _currentPhaseName)
        ?? _phases.LastOrDefault()
        ?? new PhaseNode { Kind = "phase", Name = "main", Type = "sequential", Steps = [] };
    private List<PhaseItem> CurrentSteps => CurrentPhase.Steps!;
    private int StepCount => _totalSteps;
    internal int TestStepCount => CurrentSteps.Count;  // ⑤（2026-07-09）：供 App.Tests 断言"无双 step"（fronted 求助 return 前不落盘 → Count==0）

    internal StepNode? TestLastStep => CurrentSteps.Count > 0 ? CurrentSteps[^1] as StepNode : null;  // 决策2（2026-07-12）：供 App.Tests 断言落盘 step.Iframe/OnError 值（验值不只验状态）

    public event Func<string, string, Task>? OnLog;
    public event Func<string, string, Task>? OnScreenshot;
    public event Func<string, string, string, Task>? OnRequestHelp;

    /// <summary>OnLog 加固（核查轮2/3 #4，2026-07-08）：OnLog null 时 OnLog?.Invoke 返 null Task，`await (Task)null` 抛 NRE。
    /// 统一走此 helper 返 Task.CompletedTask 兜底——生产 RecordController 总注册 OnLog 不触发，测试/未来调用方不设 OnLog 时防崩（22 处调用点加固）。</summary>
    private Task LogAsync(string message)
    {
        // E-1（方向E 日志，2026-07-15）：LogAsync 输出落 Serilog（Information 级，两环境可见）。
        // 原 LogAsync 仅 OnLog->SignalR 推前端 ReceiveLog 不经 Serilog，app-*.log 找不到"录制提示/selector 脆弱/多匹配"等文本，排查靠间接推断。
        _logger.LogInformation(message);
        var t = OnLog?.Invoke("", message);
        return t ?? Task.CompletedTask;
    }

    /// <summary>E-1（方向E 日志）：序列化 tool call 参数为 JSON 字符串（落 Serilog 审计）。
    /// 排除 value（敏感 fillData/PII）。args 值经 OpenAiProvider.ParseJsonElement 归一化：
    /// String->CLR string / Number/Object/Array->JsonElement / True/False->bool / Null->""。
    /// JsonSerializer.Serialize 对 JsonElement 值可靠序列化（输出原始 JSON）；提 internal static 供测试直测（排除 value + JsonElement 序列化）。</summary>
    internal static string SerializeArgs(Dictionary<string, object>? args)
    {
        if (args == null || args.Count == 0) return "{}";
        var filtered = args.Where(kv => kv.Key != "value").ToDictionary(kv => kv.Key, kv => kv.Value);
        return System.Text.Json.JsonSerializer.Serialize(filtered);
    }

    public RecordingEngine(IAiProvider aiProvider, ScriptService scriptService, EngineILogger logger, EngineOptions engineOptions, AppOptions appOptions, List<AttachmentInfo>? attachments = null)
    {
        _aiProvider = aiProvider;
        _scriptService = scriptService;
        _logger = logger;
        _engineOptions = engineOptions;
        _appOptions = appOptions;
        _attachments = attachments;  // 批次7-C4：录制期附件（upload 执行层 + BuildDataHints 用）
        _compressor = new ConversationCompressor();
        _actionExecutor = new RecordingActionExecutor(_logger, _appOptions, ResolveSystemVar, attachments, _engineOptions.UploadRootPath);  // 批次7-C6/A1：传 attachments + UploadRootPath（附件 Path 相对需 rootPath）
        _aiActionExecutor = new AiActionExecutor(_aiProvider, _logger, new RecordingAiReporter(this), _engineOptions.Compression);  // B2（2c）+ #4 TD-5：录制期执行 ai action + 注入 reporter 适配器（OnLog event 推 ReceiveLog）
    }

    /// <summary>#4 TD-5：供 RecordingAiReporter 触发 OnLog（event 只能在声明类 Invoke）。taskId 由 RecordController 闭包覆盖（OnLog 第1参忽略），加 🤖 前缀。</summary>
    private Task OnAiReporterLogAsync(string message)
    {
        if (OnLog != null) return OnLog.Invoke("", "🤖 " + message);
        return Task.CompletedTask;
    }

    /// <summary>#4 TD-5：录制端 AiActionExecutor 可观测性适配器。App 层无 ITaskProgressReporter 实现（Worker 端接口），
    /// 借 OnLog event 推 ReceiveLog（RecordController 订阅 → hubContext.SendLog）。其他方法 no-op（录制期 AI 仅推工具日志即可，终态/截图由 RecordingEngine 自身管）。</summary>
    private sealed class RecordingAiReporter : ITaskProgressReporter
    {
        private readonly RecordingEngine _engine;
        public RecordingAiReporter(RecordingEngine engine) => _engine = engine;
        public Task SendLogAsync(string message, string? stepName = null) => _engine.OnAiReporterLogAsync(message);
        public Task SendScreenshotAsync(byte[] screenshot, string? stepDescription = null) => Task.CompletedTask;
        public Task SendFinalScreenshotAsync(byte[] screenshot) => Task.CompletedTask;
        public Task SendTaskStoppedAsync(string reason) => Task.CompletedTask;
        public Task SendTaskCompletedAsync(ScriptResult result) => Task.CompletedTask;
    }

    /// <summary>Stop 端点调用：标记本次取消为"手动停止"（必须在 cts.Cancel 前）。区分总超时（空=自动到期）。幂等 ??= 防双设。</summary>
    public void MarkManualCancel(string reason = "用户取消录制") => _cancelReason ??= reason;

    /// <summary>测试注入：绕过真实浏览器创建（生产不调用，_page 默认 null 走正常创建）。App.Tests 用 InternalsVisibleTo 访问。</summary>
    internal IPage? TestInjectedPage { set => _page = value; }

    public async Task<ScriptV2> RecordAsync(
        string documentTypeId,
        string taskDescription,
        string? prerequisiteScriptId,
        bool headless,
        string? userResponse,
        CancellationToken ct,
        string? username = null,
        string? password = null)
    {
        var sw = Stopwatch.StartNew();
        _isDone = false;
        _username = username;  // F8：存系统凭据，供 operate 执行层替换 {{username}}/{{password}}
        _password = password;

        try
        {
            // 初始化浏览器
            if (_page == null)
            {
                await LogAsync("正在启动浏览器...");
                _playwright = await Playwright.CreateAsync();
                _browser = await _playwright.Chromium.LaunchAsync(new() { Headless = headless });
                var context = await _browser.NewContextAsync(new()
                {
                    ViewportSize = _engineOptions.Viewport != null ? new ViewportSize { Width = _engineOptions.Viewport.Width, Height = _engineOptions.Viewport.Height } : null
                });
                _page = await context.NewPageAsync();

                // 执行前置脚本（C：修前置 silent-success——弃用 GetScript + if(preScript!=null) 静默跳过，改用 GetScriptWithErrors 四分支）
                if (!string.IsNullOrEmpty(prerequisiteScriptId))
                {
                    var (preScript, preErrors, fileExists) = _scriptService.GetScriptWithErrors(prerequisiteScriptId);
                    if (!fileExists)
                    {
                        // ① 文件不存在：前置可选（用户选「无」直接开始属正常，非病态），跳过 + 日志可观测（防 silent 跳过）
                        await LogAsync($"前置脚本 {prerequisiteScriptId} 不存在，跳过");
                    }
                    else if (preScript == null)
                    {
                        // ② 结构坏/JSON 损坏（fileExists=true 但 DeserializeOnly 抛→GetScriptWithErrors 返回 null）：throw Failed（用 scriptId 兜底，结构坏拿不到 script.Name）
                        throw new PrerequisiteScriptFailedException($"前置脚本 {prerequisiteScriptId} 加载失败（文件损坏/JSON 格式错误）");
                    }
                    else if (preErrors.Count > 0)
                    {
                        // ③ 带病（业务校验失败，如缺 aiGoal）：throw Failed（错误消息能拼 script.Name）
                        throw new PrerequisiteScriptFailedException($"前置脚本 {preScript.Name} 校验失败: {string.Join("; ", preErrors)}");
                    }
                    else
                    {
                        // ④ 合法：执行（须保留执行失败处理——ExecuteAsync Success=false→ct 取消走 Cancelled / 真实失败 throw Failed；勿误删，否则破坏 Cancel_DuringPrerequisiteScript 测试）
                        await LogAsync($"执行前置脚本: {preScript.Name}");
                        var engine = new ScriptEngine(_logger, _engineOptions);
                        var fillData = new Dictionary<string, object>();
                        if (!string.IsNullOrEmpty(username)) fillData["username"] = username;
                        if (!string.IsNullOrEmpty(password)) fillData["password"] = password;
                        var preResult = await engine.ExecuteAsync(preScript, fillData, _page, ct);
                        if (!preResult.Success)
                        {
                            // 前置脚本因取消/超时返回 Success=false（ExecuteAsync catch(OCE) 不抛，返回失败结果）：
                            // ct 取消（用户停止/总超时）则走 Cancelled；未取消则前置脚本真实失败→Failed（P3-9）
                            ct.ThrowIfCancellationRequested();
                            throw new PrerequisiteScriptFailedException($"前置脚本 {preScript.Name} 执行失败: {preResult.ErrorMessage}");
                        }
                    }
                }
            }

            // 初始化脚本
            if (string.IsNullOrEmpty(_script.ScriptId))
            {
                _script = new ScriptV2
                {
                    ScriptId = Guid.NewGuid().ToString("N")[..8],
                    DocumentTypeId = documentTypeId,
                    Name = $"录制-{DateTime.Now:yyyyMMdd-HHmmss}",
                    Phases = []
                };
                _phases.Clear();
                _currentPhaseName = "";
                _totalSteps = 0;

                // 存储任务描述
                _taskDescription = $"{taskDescription}{BuildDataHints(username, password, _attachments)}";

                // 初始化 AI 对话（D1：fields 读取已移除——无设计背书的遗留，prompt 直接用 SystemPrompt）
                _messages.Clear();
                var prompt = RecordingPrompt.BuildPromptWithFields(RecordingPrompt.SystemPrompt, null);
                _messages.Add(new SystemChatMessage(prompt));
                _messages.Add(new UserChatMessage(
                    $"任务描述: {_taskDescription}\n\n" +
                    $"[当前录制状态]\n{BuildPhaseStatus()}\n\n" +
                    $"请开始录制。先获取页面快照了解当前页面状态。"));
            }

            // 用户响应（录制中 AI 询问后）
            if (!string.IsNullOrEmpty(userResponse))
            {
                _messages.Add(new UserChatMessage($"用户回答: {userResponse}\n\n请继续录制。"));
            }

            // AI 录制循环
            var tools = GetRecordingTools();
            int maxTurns = _appOptions.MaxRecordTurns;

            for (int turn = 0; turn < maxTurns; turn++)
            {
                ct.ThrowIfCancellationRequested();
                EnsureBrowserHealthy();

                var countBefore = _messages.Count;
                _messages = _compressor.CompressHistory(_messages, _engineOptions.Compression ?? new CompressionOptions { Threshold = 30, MinimumPreserved = 5, PreserveInitialUserMessages = true });
                _compressor.CompressOldSnapshots(_messages);
                if (_messages.Count < countBefore)
                    await LogAsync($"对话已压缩: {countBefore} → {_messages.Count} 条消息");

                // 原地更新初始用户消息的状态部分（FindIndex 查找替代 _messages[1]）
                var statusJson = BuildPhaseStatus();
                var initialMsgIdx = _messages.FindIndex(m => m is UserChatMessage);
                if (initialMsgIdx >= 0)
                {
                    _messages[initialMsgIdx] = new UserChatMessage(
                        $"任务描述: {_taskDescription}\n\n" +
                        $"[当前录制状态]\n{statusJson}\n\n" +
                        $"请继续录制。");
                }

                var response = await _aiProvider.SendMessageAsync(_messages, tools, ct);

                // 推送 AI 文本到前端（全部推送，包括附带工具调用的文本）
                var text = response.Text?.Trim();
                if (!string.IsNullOrEmpty(text))
                    await LogAsync(text);

                if (response.RawAssistantMessage != null)
                    _messages.Add(response.RawAssistantMessage);
                else if (response.ToolCalls == null || response.ToolCalls.Count == 0)
                {
                    _messages.Add(new AssistantChatMessage(response.Text ?? ""));
                    if (response.OwnedResources != null) foreach (var d in response.OwnedResources) d.Dispose();  // N.2.3：防 JsonDocument 泄漏
                    continue;
                }

                foreach (var tc in response.ToolCalls)
                {
                    var result = await ExecuteRecordingToolAsync(tc, ct);
                    _messages.Add(new ToolChatMessage(tc.Id, result));

                    if (tc.Name == "get_snapshot")
                        _compressor.RegisterSnapshotId(tc.Id);
                    if (_isDone) break;
                }
                if (response.OwnedResources != null) foreach (var d in response.OwnedResources) d.Dispose();  // N.2.3：每轮 Dispose 防 JsonDocument 泄漏
                if (_isDone) break;
            }

            if (!_isDone)
            {
                // 边界修复（2026-06-29 核查发现）：request_help 在最后一轮（turn=maxTurns-1）被取消时，ExecuteRequestHelpAsync catch(OCE) 吞掉返回字符串，
                // 但循环已结束（没有"下一轮"ThrowIfCancellationRequested 传播取消）→ 会误报 MaxTurnsExhausted。此处补判 ct：若已取消则抛 OCE→catch(OCE) 按 _cancelReason 走 ManualStop/Timeout。
                ct.ThrowIfCancellationRequested();
                await LogAsync($"录制达到最大轮次限制 ({maxTurns})");
                // maxTurns 耗尽（跑满 MaxRecordTurns 未 done）原走 return finalScript→Success（silent-success：与填报 AiActionExecutor 轮次耗尽即失败不对称），
                // 改为外抛 MaxTurnsExhausted→RecordController catch→TaskCompleted(Cancelled)。
                // 🔴 此 throw 在 try 块内（RecordAsync try 范围），会被同 try 的 catch(Exception) 捕获→吞走 Success，故 catch(Exception) 必须加 `if(ex is RecordingCancelledException) throw;` 兜底外抛（见下）。
                throw new RecordingCancelledException(RecordingCancelReason.MaxTurnsExhausted, $"录制达到最大轮次限制({maxTurns}轮)");
            }

            // 录制成功完成时，AI 总结每个 phase 的 AiGoal 和 Script Description
            var finalScript = GetCurrentScript();
            if (_isDone)
            {
                await LogAsync("正在生成阶段目标总结...");
                finalScript = await SummarizePhaseGoalsAsync(finalScript, taskDescription, ct);
                await LogAsync("阶段目标总结完成");
            }
            return finalScript;
        }
        catch (OperationCanceledException)
        {
            // 区分超时/手动：单 CTS 模式下 ct.IsCancellationRequested 无法区分（超时和手动都让同 token 取消），改用 _cancelReason 字段判据
            // （Stop 端点 MarkManualCancel 标记手动取消；空=cts 自动到期→总超时）。区别于 ScriptEngine 的 linked CTS + 外部 ct 判据（不可照搬）。
            var isTimeout = string.IsNullOrEmpty(_cancelReason);
            var minutes = (int)Math.Round(_engineOptions.MaxScriptDuration / 60000.0);
            var reason = isTimeout ? RecordingCancelReason.Timeout : RecordingCancelReason.ManualStop;
            var msg = isTimeout ? $"录制超时({minutes}分钟)" : _cancelReason!;
            await LogAsync(isTimeout ? "录制超时" : "录制被用户取消");
            // 外抛 RecordingCancelledException→RecordController catch→TaskCompleted(Cancelled, 已录脚本, msg)。
            // 注意：catch 块内抛出的异常不会被同 try 的其他 catch 捕获（catch 块是排他的），直接向上传播到 RecordController，无需兜底。
            throw new RecordingCancelledException(reason, msg);
        }
        catch (Exception ex)
        {
            // 🔴 兜底外抛：try 块内 maxTurns 耗尽抛的 RecordingCancelledException（非 OCE，不走 catch(OCE)）会被此处捕获→吞走 Success（silent-success），
            // 必须先判断 RecordingCancelledException 外抛→Cancelled。放在浏览器崩溃/前置脚本失败检查前。
            if (ex is RecordingCancelledException) throw;
            if (IsBrowserCrashException(ex)) throw;   // 浏览器崩溃直接外抛，不返回部分脚本
            // P3-9：前置脚本失败外抛（PrerequisiteScriptFailedException）→ RecordController Failed → 前端"录制失败"如实上报（原被吞走伪装录制成功）
            if (ex is PrerequisiteScriptFailedException) throw;
            await LogAsync($"录制异常: {ex.Message}");
            return GetCurrentScript();
        }
    }

    private static string BuildDataHints(string? username, string? password, List<AttachmentInfo>? attachments)
    {
        var hints = new List<string>();
        // F8(R5-2)：系统凭据（username/password）占位符化——明文不进 AI 提示（避免发往外部 LLM）。
        // AI 录登录时 value 用 {{username}}/{{password}}，operate 执行层（RecordingActionExecutor）替换真实值填浏览器、step.Value 存占位符。
        if (!string.IsNullOrEmpty(username)) hints.Add($"用户名（username）: 系统变量，填用户名框时 value 用 {{{{username}}}}（回放由引擎填实际值，此处不展示明文）");
        if (!string.IsNullOrEmpty(password)) hints.Add($"密码（password）: 系统变量，填密码框时 value 用 {{{{password}}}}（回放由引擎填实际值，此处不展示明文）");
        // 批次7-C5：附件提示（AI 录 upload 传 fieldName 关联附件字段；filePath 存 {{fieldName}}，回放期 fillData 动态附件）
        if (attachments != null && attachments.Count > 0)
        {
            var fileList = string.Join("\n", attachments.Select(a => $"- {a.Name}（路径: {a.Path}）"));
            hints.Add($"用户上传了附件（{attachments.Count} 个）:\n{fileList}\n录 upload 操作时传 fieldName 关联附件字段（脚本存 {{{{fieldName}}}}，回放期动态附件）");
        }
        return hints.Count > 0 ? $"\n\n可用数据:\n{string.Join("\n", hints)}" : "";
    }

    /// <summary>F8：系统变量解析——operate 执行层把 {{username}}/{{password}} 替换为真实值填浏览器（step.Value 仍存占位符，回放引擎填）。</summary>
    private string? ResolveSystemVar(string name) => name switch
    {
        "username" => _username,
        "password" => _password,
        _ => null
    };

    public async Task StopAsync()
    {
        try { await _browser?.CloseAsync()!; } catch { }
        _playwright?.Dispose();
        _browser = null;
        _page = null;
    }

    public ScriptV2 GetCurrentScript()
    {
        var phases = _phases.Count > 0
            ? _phases.Select(p => p with { Steps = p.Steps != null ? new List<PhaseItem>(p.Steps) : [] }).Cast<PhaseItem>().ToList()
            : [new PhaseNode { Kind = "phase", Name = "main", Type = "sequential", Steps = [] }];
        return _script with { Phases = phases };
    }

    /// <summary>
    /// 响应 AI 的 help 请求。返回 true 表示成功注入回答，false 表示当前未在等待或已结束。
    /// </summary>
    public bool ReplyHelp(string answer)
    {
        var tcs = _pendingHelpTcs;
        if (tcs == null || tcs.Task.IsCompleted) return false;
        return tcs.TrySetResult(answer);
    }

    /// <summary>检查浏览器是否仍然健康（连接且页面未关闭）</summary>
    private void EnsureBrowserHealthy()
    {
        if (_page == null
            || _page.Context.Browser == null
            || !_page.Context.Browser.IsConnected)
            throw new InvalidOperationException("浏览器已断开连接");
        if (_page.IsClosed)
            throw new InvalidOperationException("页面已关闭");
    }

    /// <summary>检测是否为浏览器崩溃异常（英文=Playwright 抛；中文=EnsureBrowserHealthy 主动检测抛，2026-06-29 补防 silent-success）</summary>
    internal static bool IsBrowserCrashException(Exception ex)
    {
        var msg = ex.Message ?? "";
        return msg.Contains("closed", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("disconnected", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Target closed", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Browser closed", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("浏览器已断开", StringComparison.Ordinal)   // EnsureBrowserHealthy 中文消息（_page==null/Context.Browser==null/!IsConnected）
            || msg.Contains("页面已关闭", StringComparison.Ordinal);    // EnsureBrowserHealthy 中文消息（_page.IsClosed）
    }

    #region 工具定义

    private List<ChatTool> GetRecordingTools()
    {
        return
        [
            ChatTool.CreateFunctionTool("operate", "执行交互操作（click/fill/type/select/hover/pressKey/scroll/upload/navigate/goBack/reload/switchTab/closeTab）",
                BinaryData.FromString("""{"type":"object","properties":{"action":{"type":"string","enum":["click","fill","type","select","hover","pressKey","scroll","upload","navigate","goBack","reload","switchTab","closeTab"]},"selector":{"type":"string","description":"XPath 选择器（ref 不可用时备用）"},"ref":{"type":"string","description":"快照中的元素引用编号，如 e27。有 ref 时优先使用 ref 而非 selector"},"value":{"type":"string"},"url":{"type":"string","description":"navigate 操作的目标 URL"},"key":{"type":"string"},"description":{"type":"string"},"fieldName":{"type":"string","description":"关联的字段名，用于参数化和字段注册"},"fieldLabel":{"type":"string","description":"字段中文标签，用于字段注册"},"fieldType":{"type":"string","enum":["string","number","date","boolean","file","array"],"description":"字段数据类型：string（默认）/number/date/boolean/file/array。覆盖代码推断"},"fieldUiComponent":{"type":"string","enum":["input","textarea","select","radio","checkbox","upload","click-choose","datepicker","hidden","table"],"description":"UI组件：input/textarea/select/radio/checkbox/upload/click-choose/datepicker/hidden/table。覆盖代码推断"},"fieldSource":{"type":"string","enum":["user","system","computed"],"description":"数据来源：user（默认）/system（系统变量如username/password，不询问用户）/computed"},"fieldRequired":{"type":"boolean","description":"代码已检测HTML5 required、aria-required、CSS伪元素星号、关联label星号、UI框架required class。未检测到时会提示⚠️，此时传true"},"fieldOptions":{"type":"array","items":{"type":"string"},"description":"代码已提取原生select选项。自定义select未提取到时会提示⚠️，此时传选项数组"},"fieldDescription":{"type":"string","description":"字段说明，描述字段含义和填写规则"},"fieldFormat":{"type":"string","description":"格式约束（date: YYYY-MM-DD, number: #,##0.00）"},"fieldTransform":{"type":"string","enum":["trim","upper","lower"],"description":"值变换规则（trim/upper/lower 三选一；date/number 格式转换用 fieldFormat 字段）"},"fieldPattern":{"type":"string","description":"正则校验（如手机号 ^1\\d{10}$）"},"fieldPlaceholder":{"type":"string","description":"输入提示文本（覆盖代码提取）"},"fieldMultiple":{"type":"boolean","description":"是否多选/多文件"},"fieldFields":{"type":"array","description":"table类型字段的子字段数组，每列一个对象：{name,label,type,uiComponent?,required?,source?,description?,format?,options?,placeholder?,pattern?}","items":{"type":"object","properties":{"name":{"type":"string"},"label":{"type":"string"},"type":{"type":"string"},"uiComponent":{"type":"string"},"required":{"type":"boolean"},"source":{"type":"string"},"description":{"type":"string"},"format":{"type":"string"},"options":{"type":"array","items":{"type":"string"}},"placeholder":{"type":"string"},"pattern":{"type":"string"}},"required":["name","label"]}},"fieldItems":{"type":"object","description":"简单数组字段的元素类型定义","properties":{"type":{"type":"string","description":"元素类型：string/number/date/boolean"},"format":{"type":"string"},"options":{"type":"array","items":{"type":"string"}},"min":{"type":"number"},"max":{"type":"number"}}},"forceAiAction":{"type":"boolean","description":"7阶段selector全失败后，明确保存为ai action"},"acceptFragile":{"type":"boolean","description":"priority 8 脆弱selector/iframe求助后用户选(f)使用脆弱的，下轮operate/save_step传true+同selector/iframe：代码跳过脆弱检测(仍查唯一性/存在性)、落盘+自动onError=ai-fallback-stop。仅对刚求助过的同selector/iframe传；换selector/iframe必须不传(重置)，否则全局绕过脆弱检测"},"acceptAsIs":{"type":"boolean","description":"selector/iframe 找不到(count==0)求助选(h)用当前值后，下轮operate传true+同selector/iframe：代码跳过存在性校验直接落盘+onError=ai-fallback-stop。仅对刚求助过的同值传；换值必须不传(重置)。多匹配(count>1)不适用(回放必失败)"},"phase":{"type":"string","description":"当前 phase 名称。首次传入时自动创建新 phase"},"phaseType":{"type":"string","enum":["sequential","loop","ai"],"description":"phase 类型 sequential(顺序)/loop(循环)/ai(AI自主)，仅首次传入 phase 时需要"},"phaseLoopSource":{"type":"string","description":"loop phase 的循环数据源字段名"},"parentPhase":{"type":"string","description":"嵌套 phase 的父 phase 名称（顶层 phase 不传）"},"pressEnter":{"type":"boolean","description":"fill/type 后按回车"},"skipIfDataEmpty":{"type":"boolean","description":"fillData 字段为空时跳过（null/空串/空数组→跳，0/false→不跳）"},"skipIfElementMissing":{"type":"boolean","description":"元素不存在时跳过"},"condition":{"type":"object","description":"步骤执行前条件检测（detect 对象，必填 type，字段见 check detect 类型表，不满足则跳过本步骤）"},"preSetup":{"type":"object","description":"执行前设置：{dialogHandler:auto_accept或auto_dismiss, dialogText?:prompt输入}"},"retry":{"type":"object","description":"重试配置：{count:次数, interval?:间隔ms}"},"timeout":{"type":"integer","description":"步骤超时(ms)"},"maxAiTurns":{"type":"integer","description":"forceAiAction=true 保存为 ai action 时的 AI 最大轮次"},"doubleClick":{"type":"boolean","description":"click 双击"},"useLast":{"type":"boolean","description":"selector 多匹配时取最后一个"},"rowIndexed":{"type":"boolean","description":"loop 内多独立 input（明细每行各一框），代码将 selector 行索引→{{rowIndex}}。同 input/JS 列表不传"},"rowIndexMode":{"type":"string","enum":["attr","position"],"description":"行索引模式：attr(@rowindex/@data-row)/position(tr[N])。省略=自动（属性优先回退位置）"},"storeAs":{"type":"string","description":"存储变量名。click 存元素 innerText、fill/type 存页面回读实际值、select 存选中值、switchTab 存切换后 URL。任务描述明确要求存这些值才传，否则用 extract。⚠️ closeTab/hover/pressKey/scroll/upload/goBack/reload 不支持 storeAs（配了无效，会被 schema 拒绝）"},"fallback":{"type":"object","description":"备选方案（selector 失败时，缺省继承 step 的 action/field/iframe/timeout）","properties":{"ref":{"type":"string","description":"录制期元素 ref（aria-ref），代码提取为 selector；最终脚本不含"},"selector":{"type":"string"},"action":{"type":"string"},"value":{"type":"string"},"code":{"type":"string"},"iframe":{"type":"array","items":{"type":"string"}},"iframeRef":{"type":"string","description":"iframe 元素 ref（aria-ref），代码提取为 iframe 链；最终脚本不含"},"timeout":{"type":"integer"}}},"name":{"type":"string","description":"步骤名（必传）：基于 action+对象起简短英文名如 fillUsername/clickLogin/navigateLogin/waitLoginLoad/checkDesktop，同 phase 内唯一，goto toStep 目标"},"onError":{"type":"string","enum":["stop","skip","ai-fallback-stop","ai-fallback-skip"],"description":"步骤失败策略 stop 停止(默认,向上传播致脚本失败)/skip 跳过/ai-fallback-stop AI兜底后停/ai-fallback-skip AI兜底后跳过。⚠️ script_fail 是 then 字段的值不要填此；wait 超时想让脚本失败用 stop"},"iframe":{"type":"array","items":{"type":"string"},"description":"iframe selector 链覆盖（根→叶）。常规留空让代码自动提取；仅在收到'iframe 脆弱/提取失败'warning 时，基于 warning 元素属性（id/src/name）生成更稳链、经 iframe 参数覆盖"},"iframeRef":{"type":"string","description":"iframe 元素的 aria-ref（指向 <iframe> 元素本身）。iframe 脆弱/提取失败求助选(i)用 ref 指认 iframe 后，下轮传 iframeRef：代码用 aria-ref 定位 iframe DOM 提取链。仅对刚求助过的同 iframe 传"},"matchBy":{"type":"string","enum":["value","label","index"],"description":"select 匹配方式 value(按值)/label(按可见文本)/index(按0-based序号)，仅 select 用"},"direction":{"type":"string","enum":["up","down","left","right","bottom","top"],"description":"scroll 方向"},"amount":{"type":"integer","description":"scroll 滚动量(px)，默认300"},"index":{"type":"integer","description":"switchTab/closeTab 标签页索引（switchTab 不传则自动检测新tab）"},"phaseCondition":{"type":"object","description":"phase 执行前条件检测（仅首次传入 phase 名时生效）"},"phaseLoopCondition":{"type":"object","description":"loop phase 每轮继续条件（仅首次传入 phase 名时生效）"},"phaseOnError":{"type":"string","enum":["stop","skip","ai-fallback-stop","ai-fallback-skip"],"description":"phase 错误策略 stop/skip/ai-fallback-stop/ai-fallback-skip（仅首次传入 phase 名时生效）"},"phaseMaxLoopCount":{"type":"integer","description":"loop phase 最大循环次数（仅首次传入 phase 名时生效）"},"phaseRowIndexOffset":{"type":"integer","description":"loop phase 行索引偏移量（仅首次传入 phase 名时生效）"},"phaseMaxAiTurns":{"type":"integer","description":"ai phase AI 总轮次预算（仅首次传入 phase 名时生效）"},"phaseTimeout":{"type":"integer","description":"phase 超时(ms)（仅首次传入 phase 名时生效）"},"phaseAiGoal":{"type":"string","description":"phase 业务目标（R3-2 必填，首次创建 phase 时传入简短目标如「填写合同信息」，用于 AI fallback/ai phase；缺失会导致脚本加载失败）"}},"required":["action","description","name"],"allOf":[{"if":{"properties":{"action":{"const":"scroll"}}},"then":{"oneOf":[{"required":["selector"],"not":{"required":["direction"]}},{"required":["direction"],"not":{"required":["selector"]}}]}}]}""")),
            ChatTool.CreateFunctionTool("save_step", "保存非交互步骤（check/captcha/evaluate/extract/wait/ai/goto/handleDialog/screenshot）",
                BinaryData.FromString("""{"type":"object","properties":{"stepType":{"type":"string","enum":["check","captcha","evaluate","extract","wait","ai","goto","handleDialog","screenshot"],"description":"非交互步骤类型 check验证/captcha验证码/evaluate执行JS/extract提取/wait等待/aiAI步骤/goto跳转/handleDialog对话框/screenshot截图"},"description":{"type":"string"},"name":{"type":"string","description":"步骤名（必传）：基于 action+对象起简短英文名如 fillUsername/clickLogin/navigateLogin/waitLoginLoad/checkDesktop，同 phase 内唯一，goto toStep 目标"},"ref":{"type":"string","description":"快照中的元素引用编号（如 e27）。selector 类 detect/check 优先用 ref 让代码提取稳定 selector（与 operate 同源）"},"selector":{"type":"string","description":"XPath 选择器"},"value":{"type":"string"},"toPhase":{"type":"string","description":"goto目标phase名"},"toStep":{"type":"string","description":"goto目标step名"},"accept":{"type":"boolean","description":"handleDialog是否接受"},"dialogPromptText":{"type":"string","description":"prompt对话框输入文本"},"screenshotType":{"type":"string","enum":["viewport","fullPage","element"],"description":"截图类型：viewport(可见区域，默认)/fullPage(整页)/element(元素截图)"},"saveToFile":{"type":"boolean","description":"是否保存文件到磁盘，默认true"},"storeContent":{"type":"string","enum":["path","dataUrl","both"],"description":"storeAs存什么：path(文件路径，默认)/dataUrl(图片数据)/both(两者)。saveToFile=false时只能为dataUrl"},"detect":{"type":"object","description":"check的detect条件。selector 类检测优先用 ref 指认元素（代码自动提取 selector），等页面状态用 page_contains/url_changed"},"then":{"type":"string","enum":["nothing","continue","step_error","phase_success","phase_error","phase_rerun","row_rerun","script_success","script_fail","goto","break"],"description":"check的then值（nothing=通过；continue=下一行/loop下一迭代；step_error=按本step onError处理；phase_success/phase_error=phase成功/触发phase onError；phase_rerun/row_rerun=重跑整个phase/当前行；script_success/script_fail=脚本成功/失败；goto=跳转；break=跳出循环）。⚠️ 值域与 onError 不交叉，script_fail 等只属 then，勿跨填 onError"},"timeout":{"type":"integer","description":"wait until 的轮询超时上限(ms)，任务描述明确要求超时(如最多等30秒)时传，否则省略用默认"},"pollInterval":{"type":"integer","description":"wait until 的轮询间隔(ms)，任务描述明确指定(如每500毫秒检查一次)才传，否则省略用默认500ms"},"ms":{"type":"number","description":"wait等待毫秒"},"code":{"type":"string","description":"evaluate的JS代码"},"property":{"type":"string","description":"extract提取的属性名"},"regex":{"type":"string","description":"extract正则"},"storeAs":{"description":"存储变量名。ai action 必须用 object 格式 {\"变量名\": \"描述\"}，其他 action 用字符串","oneOf":[{"type":"string"},{"type":"object","additionalProperties":{"type":"string"}}]},"onError":{"type":"string","enum":["stop","skip","ai-fallback-stop","ai-fallback-skip"],"description":"步骤失败策略 stop 停止(默认,向上传播致脚本失败)/skip 跳过/ai-fallback-stop AI兜底后停/ai-fallback-skip AI兜底后跳过。⚠️ script_fail 是 then 字段的值不要填此；wait 超时想让脚本失败用 stop"},"field":{"type":"string","description":"关联的字段名（ai/extract/check可用）"},"iframe":{"type":"array","items":{"type":"string"},"description":"iframe selector 链覆盖（根→叶）。常规留空让代码自动提取；仅在收到'iframe 脆弱/提取失败'warning 时基于元素属性生成更稳链覆盖"},"iframeRef":{"type":"string","description":"iframe 元素的 aria-ref（指向 <iframe> 元素本身）。iframe 脆弱/提取失败求助选(i)用 ref 指认 iframe 后，下轮传 iframeRef：代码用 aria-ref 定位 iframe DOM 提取链。仅对刚求助过的同 iframe 传"},"skipIfDataEmpty":{"type":"boolean","description":"fillData字段为空时跳过（null/空串/空数组→跳，0/false→不跳）"},"skipIfElementMissing":{"type":"boolean","description":"元素不存在时跳过"},"acceptFragile":{"type":"boolean","description":"priority 8 脆弱selector/iframe求助后用户选(f)使用脆弱的，下轮operate/save_step传true+同selector/iframe：代码跳过脆弱检测(仍查唯一性/存在性)、落盘+自动onError=ai-fallback-stop。仅对刚求助过的同selector/iframe传；换selector/iframe必须不传(重置)"},"acceptAsIs":{"type":"boolean","description":"selector/iframe 找不到(count==0)求助选(h)用当前值后，下轮save_step传true+同selector/iframe：代码跳过存在性校验直接落盘+onError=ai-fallback-stop。仅对刚求助过的同值传；换值必须不传(重置)。多匹配(count>1)不适用"},"captchaType":{"type":"string","enum":["text","slide","pixel","click"],"description":"验证码类型"},"imageSelector":{"type":"string","description":"验证码图片选择器（text/click）"},"inputSelector":{"type":"string","description":"输入框选择器（text）"},"sliderSelector":{"type":"string","description":"滑块按钮选择器（slide/pixel）"},"targetSelector":{"type":"string","description":"滑块小图/提示文字选择器（slide/pixel/click）"},"backgroundSelector":{"type":"string","description":"背景大图选择器（slide/pixel）"},"message":{"type":"string","description":"check失败消息（phase_fail/script_fail）"},"until":{"type":"object","description":"wait条件等待的detect条件JSON。selector 类等待优先用 ref 指认元素（代码自动提取 selector）；等页面状态用 page_contains/url_changed；等 iframe/页面加载完成（readyState=complete）用 document_ready（until.iframe 可省略=继承默认 frame/主页面；[]=主页面 / [selector链]=指定 iframe）"},"extractType":{"type":"string","enum":["url","title","property"],"description":"extract 提取类型 url(当前URL)/title(页面标题)/property(元素属性,默认)"},"cleanupSteps":{"type":"array","description":"check失败时的清理步骤数组（phase_fail/script_fail时）"},"phase":{"type":"string","description":"当前 phase 名称。首次传入时自动创建新 phase"},"phaseType":{"type":"string","enum":["sequential","loop","ai"],"description":"phase 类型 sequential(顺序)/loop(循环)/ai(AI自主)，仅首次传入 phase 时需要"},"phaseLoopSource":{"type":"string","description":"loop phase 的循环数据源字段名"},"parentPhase":{"type":"string","description":"嵌套 phase 的父 phase 名称（顶层 phase 不传）"},"maxAiTurns":{"type":"integer","description":"ai action 的 AI 最大轮次"},"condition":{"type":"object","description":"步骤执行前条件检测（detect 对象，必填 type，字段见 check detect 类型表，不满足则跳过本步骤）"},"preSetup":{"type":"object","description":"执行前设置：{dialogHandler:auto_accept或auto_dismiss, dialogText?:prompt输入}"},"retry":{"type":"object","description":"重试配置：{count:次数, interval?:间隔ms}"},"fallback":{"type":"object","description":"备选方案（selector 失败时，缺省继承 step 的 action/field/iframe/timeout）","properties":{"ref":{"type":"string","description":"录制期元素 ref（aria-ref），代码提取为 selector；最终脚本不含"},"selector":{"type":"string"},"action":{"type":"string"},"value":{"type":"string"},"code":{"type":"string"},"iframe":{"type":"array","items":{"type":"string"}},"iframeRef":{"type":"string","description":"iframe 元素 ref（aria-ref），代码提取为 iframe 链；最终脚本不含"},"timeout":{"type":"integer"}}},"args":{"type":"array","description":"evaluate 的 JS 参数列表"},"folder":{"type":"string","description":"screenshot 保存文件夹（覆盖配置，不传则自动生成）"},"filename":{"type":"string","description":"screenshot 文件名（不含扩展名，不传则自动生成）"},"phaseCondition":{"type":"object","description":"phase 执行前条件检测（仅首次传入 phase 名时生效）"},"phaseLoopCondition":{"type":"object","description":"loop phase 每轮继续条件（仅首次传入 phase 名时生效）"},"phaseOnError":{"type":"string","enum":["stop","skip","ai-fallback-stop","ai-fallback-skip"],"description":"phase 错误策略 stop/skip/ai-fallback-stop/ai-fallback-skip（仅首次传入 phase 名时生效）"},"phaseMaxLoopCount":{"type":"integer","description":"loop phase 最大循环次数（仅首次传入 phase 名时生效）"},"phaseRowIndexOffset":{"type":"integer","description":"loop phase 行索引偏移量（仅首次传入 phase 名时生效）"},"phaseMaxAiTurns":{"type":"integer","description":"ai phase AI 总轮次预算（仅首次传入 phase 名时生效）"},"phaseTimeout":{"type":"integer","description":"phase 超时(ms)（仅首次传入 phase 名时生效）"},"phaseAiGoal":{"type":"string","description":"phase 业务目标（R3-2 必填，首次创建 phase 时传入简短目标，用于 AI fallback/ai phase）"}},"required":["stepType","description","name"]}""")),
            ChatTool.CreateFunctionTool("screenshot", "截图查看页面内容",
                BinaryData.FromString("""{"type":"object","properties":{"selector":{"type":"string"},"quality":{"type":"integer","description":"JPEG质量0-100，默认70。验证码识别时用100"}},"required":[]}""")),
            ChatTool.CreateFunctionTool("get_snapshot", "获取页面 Accessibility Tree 快照",
                BinaryData.FromString("""{"type":"object","properties":{},"required":[]}""")),
            ChatTool.CreateFunctionTool("request_help", "向用户请求帮助",
                BinaryData.FromString("""{"type":"object","properties":{"question":{"type":"string"}},"required":["question"]}""")),
            ChatTool.CreateFunctionTool("done", "录制完成",
                BinaryData.FromString("""{"type":"object","properties":{"summary":{"type":"string"}},"required":[]}""")),
        ];
    }

    #endregion

    #region 工具执行

    internal async Task<string> ExecuteRecordingToolAsync(Engine.Models.AiToolCall tc, CancellationToken ct)
    {
        // E-1（方向E 日志）：tool call 参数落 Serilog（Information 级）--AI 调 operate/save_step 传的 fieldName/fieldLabel/selector/ref 等可事后审计。
        // SerializeArgs 排除 value（敏感 fillData/PII；username/password 的 value 是 {{username}} 占位符安全，但其他业务字段 value 是真实数据）。
        _logger.LogInformation($"[ToolCall] {tc.Name}: {SerializeArgs(tc.Arguments)}");
        var args = tc.Arguments ?? [];
        try
        {
            switch (tc.Name)
            {
                case "operate": return await ExecuteOperateAsync(args, ct);
                case "save_step": return await ExecuteSaveStep(args, ct);
                case "screenshot": return await ExecuteScreenshotAsync(args);
                case "get_snapshot": return await ExecuteGetSnapshotAsync();
                case "request_help": return await ExecuteRequestHelpAsync(args, ct);
                case "done": return await ExecuteDoneAsync(args);
                default: return $"未知工具: {tc.Name}";
            }
        }
        catch (Exception ex)
        {
            if (IsBrowserCrashException(ex)) throw;
            return $"工具执行失败({tc.Name}): {ex.Message}";
        }
    }

    internal async Task<string> ExecuteOperateAsync(Dictionary<string, object> args, CancellationToken ct)
    {
        // 处理 phase 参数（在创建 step 之前）
        var (phaseError, phaseWarnings) = await EnsurePhaseAsync(args, ct);
        if (phaseError != null) return phaseError;

        if (_page == null) return "浏览器未启动";
        var action = args.GetValueOrDefault("action")?.ToString() ?? "";
        var description = args.GetValueOrDefault("description")?.ToString() ?? "";

        var step = new StepNode
        {
            Kind = "step",
            Name = args.GetValueOrDefault("name")?.ToString() ?? $"step-{StepCount + 1}",
            Action = action,
            Selector = args.GetValueOrDefault("selector")?.ToString(),
            Value = action == "upload" ? null : args.GetValueOrDefault("value")?.ToString(),  // 批次7-A2：upload 不存 Value（回放读 FilePath；执行层用 _attachments）
            Url = action == "navigate"
                ? args.GetValueOrDefault("url")?.ToString() ?? args.GetValueOrDefault("value")?.ToString()
                : null,
            Key = args.GetValueOrDefault("key")?.ToString(),
            Description = description,
            Field = args.GetValueOrDefault("fieldName")?.ToString(),  // A1: fieldName → step.Field
            // 批次7-A2：upload 时 FilePath 存占位符（废弃 N3 的 FilePath=value 映射；回放 StepExecutor 读 FilePath 解析 fillData 动态附件）。
            // 批次7-loop 修复（核查第9轮🟠1）：loopSource=attachments（loop 真迭代）存 {{path}}——回放每行 rowData={name,url,path}，{{path}} 取当前行单附件 path；
            //   原 {{fieldName}} 在 rowData 无键→落根作用域取完整数组→每轮传全部 N 文件（bug）。非 loop 单步存 {{fieldName}}（fillData[fieldName] 对象数组一次 SetInputFiles 全部）。
            FilePath = action == "upload"
                ? (CurrentPhase.LoopSource == "attachments" ? "{{path}}" : $"{{{{{args.GetValueOrDefault("fieldName")?.ToString() ?? ""}}}}}")
                : null,
            // S4 改动3：补读 step 修饰符 + action 专属字段（补 Schema 让 AI 能传 + 补读取让存，缺一不可；I.2 半截修复）。
            // maxAiTurns 不在此读——forceAiAction=true 时由 RecordingActionExecutor 的 ai step 分支独立从 args 读取。
            Iframe = GetStringArray(args, "iframe"),  // 形态 A：iframe selector 链（AI 仅 warning 时传覆盖）
            IframeRef = args.GetValueOrDefault("iframeRef")?.ToString(),  // 结论14（第5轮#15）：step级 iframeRef（(i)选项落地，下方解析提取链后清空）
            OnError = args.GetValueOrDefault("onError")?.ToString(),
            MatchBy = args.GetValueOrDefault("matchBy")?.ToString(),
            Direction = args.GetValueOrDefault("direction")?.ToString(),
            Timeout = GetInt(args, "timeout"),
            Amount = GetInt(args, "amount"),
            Index = GetInt(args, "index"),
            PressEnter = GetBool(args, "pressEnter"),
            UseLast = GetBool(args, "useLast"),
            DoubleClick = GetBool(args, "doubleClick"),
            SkipIfDataEmpty = GetBool(args, "skipIfDataEmpty"),
            SkipIfElementMissing = GetBool(args, "skipIfElementMissing"),
            Condition = GetObject<Engine.Models.DetectCondition>(args, "condition"),
            PreSetup = GetObject<Engine.Models.PreSetup>(args, "preSetup"),
            Retry = GetObject<Engine.Models.StepRetry>(args, "retry"),
            Fallback = GetObject<Engine.Models.StepFallback>(args, "fallback"),
            StoreAs = args.GetValueOrDefault("storeAs"),  // 附录 AE：click/fill/type/select 任务描述驱动存值（原 operate 未暴露 storeAs）
        };

        // ④-8 录制层：storeAs 撞名检查（字段名/系统保留字——静默遮蔽取错值）；校验层 ScriptLoader 落盘兜底
        var operateStoreAsCollision = CheckStoreAsCollision(step.StoreAs);
        if (operateStoreAsCollision != null) return operateStoreAsCollision;

        // 结论14 step级（第5轮核查#15 修复）：step.IframeRef 非空且未传 iframe → ResolveIframeFromRefAsync 提取链（(i)选项落地，防 StepNode.IframeRef dead field）。
        // 落盘三分支简化（step级非 lenient）：成功清空 IframeRef 落盘 Iframe / 失败返 error 让 AI 重试。
        if (!string.IsNullOrEmpty(step.IframeRef) && (step.Iframe == null || step.Iframe.Length == 0) && _page != null)
        {
            var (stepIframeChain, stepIframeErr) = await _actionExecutor.ResolveIframeFromRefAsync(_page, step.IframeRef);
            if (stepIframeErr != null) return $"❌ iframeRef={step.IframeRef} 提取失败: {stepIframeErr}";
            step = step with { Iframe = stepIframeChain, IframeRef = null };
        }

        // W1（a-a-4 P1.1）：acceptFragile/acceptAsIs 前移到 ProcessDetectAsync 前（原 L501 在后致 P1.1 acceptFragile 形参引用未声明变量 CS0165 / detect 永不跳 AssessFragility）。
        var acceptFragile = GetBool(args, "acceptFragile") == true;
        var acceptAsIs = GetBool(args, "acceptAsIs") == true;  // ND7（a-a-4）：count==0 跳存在性标志

        // D12（2c，2026-07-03 用户拍板）：operate step.Condition 补 ProcessDetectAsync 反推（对齐 save_step）。
        // 否则 operate step.Condition.Selector/Iframe（含 ref）录制期不反推不校验 → AI 传 ref 落盘但回放 detect 不消费 ref → silent 回归入口。
        // 步骤13（a-a-4）：ProcessDetectAsync 5 元（+MaxPriority+CountZero）+ acceptFragile 形参（P1.1 对策三件套贯通）。
        var (operateDetectStep, operateDetectErr, operateDetectWarnings, detectPriority, detectCountZero, detectMultiFrames, detectCzLeafSelector, detectIsFragileLayer, detectIsFragileLayerChain, detectFragileLeafSelector, detectIsFragileLayerCtx) = await ProcessDetectAsync(step, acceptFragile, ct);
        if (operateDetectErr != null) return operateDetectErr;
        step = operateDetectStep;

        // ⑤（a-a-4，2026-07-09）operate 双 step 彻底方案：前移 detect③/countZero/detect priority 8 三条 detect 信号求助到 ExecuteOperateAsync 落盘前。
        // 原求助在 caller L727+（_totalSteps++ L529 + execute L530 落盘 finalStep 之后）→ AI 重传又录一个 step → 脚本双 step → 回放 action 执行 2 遍（违背"确定性执行 0 token"）。
        // 前移后：三条 detect 信号求助在 execute 前，第一次不 execute 不落盘，AI 重传单 step。step selector priority 8/多匹配本就 execute 内不 Add（RecordingActionExecutor L318/L438，无双 step）保持后置（caller L760）。
        // detect 信号（detect selector 的 count==0/脆弱）独立于 action selector——原 result.Success 守卫"action 成功才求助 detect"是错误耦合，detect 问题不应等 action 成功才暴露（设计纠偏，非 bug）。
        var detectSelectorForHelp = detectCzLeafSelector ?? step.Detect?.Selector ?? step.Until?.Selector ?? step.Condition?.Selector;  // 待决策2 (b2)：优先用 count==0 叶子 selector（复合时非空，(h) 求助指向真实叶）
        // ND8 detect③ full picker（优先于 countZero/priority——多 frame 是"哪个 frame"问题，先定 frame 再论 count==0/脆弱）。
        if (detectMultiFrames is { Count: >= 2 })
        {
            await LogAsync($"录制提示: detect 目标在 {detectMultiFrames.Count} 个 iframe 都出现，请用户指认");
            return await RequestMultiFrameHelpAsync(_totalSteps + 1, step.Name, _currentPhaseName, action, description,
                $"detect 反推命中 {detectMultiFrames.Count} 个 frame，请用 (j) 编号指认评估该 detect 条件的目标 frame（或 (e) 跳过用默认第 1 个）", detectMultiFrames, ct);
        }
        // ND7 count==0 求助（去 result.Success 守卫——前移时未 execute，result 不存在）：detect count==0 且未 acceptAsIs → 求助给 (h)（下轮 acceptAsIs 命中跳存在性）。
        // 待决策4·D-DC15（第67轮）：(h) gate 须 per-selector（镜像 (f) gate 待决策3 per-source），否则复合多 count==0 叶子第一个选 (h)+下轮 acceptAsIs=true 时，第二个 count==0 叶 silent 落盘（czLeafSelector 经 ③段 skip 间接 self-correction 到第二个叶，但 gate 全局 acceptAsIs 阻断 (h)）。加 !IsAsIsAccepted(DetectSelector, detectSelectorForHelp) 让未接受叶仍触发 (h)。
        if (detectCountZero && (!acceptAsIs || !IsAsIsAccepted(FragileType.DetectSelector, detectSelectorForHelp)))
        {
            var czQuestion = BuildHelpQuestion(_totalSteps + 1, _currentPhaseName, step.Name, action, description,
                warning: $"detect selector 在当前页面找不到（count==0，可能未加载/动态生成/跨域/预期不可见）",
                selector: detectSelectorForHelp,
                options: HelpOption.Common5 | HelpOption.AcceptAsIs);
            var czReply = await ExecuteRequestHelpAsync(new() { ["question"] = czQuestion }, ct);
            if (!string.IsNullOrEmpty(detectSelectorForHelp) && ReplyChoseOption(czReply, 'h'))
            {
                _acceptedAsIs.Add((FragileType.DetectSelector, detectSelectorForHelp));  // W2 同款：选 (h) 填集合，下轮 acceptAsIs 命中跳存在性
                czReply = AppendSelectorAsIsGuidance(czReply, detectSelectorForHelp);  // 块B：(h) reply 追加 selector 指引
            }
            return czReply;
        }
        // C2 落点5（结论13）：detect③ iframe 链脆弱（isFragileLayer，C1 每层 AssessFragility>=8）→ 求助给 (f)（复用 RequestIframeFragileHelpAsync + 填 DetectIframe 集合，镜像 operate/save_step iframe-(f)）。isFragileLayer 优先于 selector priority 8（iframe 链更基础，先定 frame 再论 selector 脆弱）。A′ 查集合跳过已接受链（self-correction 逐轮推进）。
        if (detectIsFragileLayer && detectIsFragileLayerChain is { Length: > 0 }
            && (!acceptFragile || !IsFragileAccepted(FragileType.DetectIframe, SerializeChain(detectIsFragileLayerChain))))
        {
            var detectFragileIframeWarning = $"detect iframe 链含脆弱层「{string.Join(" > ", detectIsFragileLayerChain)}」（位置选择器层/GUID id/动态锚点，回放易失效），建议 request_help 提供 iframe 的 id/src/name 或更稳 selector 链";
            var (detectFragileIframeReply, _) = await RequestIframeFragileHelpAsync(_totalSteps + 1, step.Name, _currentPhaseName, action, description,
                detectFragileIframeWarning, detectIsFragileLayerChain, detectSelectorForHelp, FragileType.DetectIframe, ct, detectIsFragileLayerCtx);
            return detectFragileIframeReply;
        }
        // detect priority 8（原 caller detectIsFragileSource 分支前移）：detect 脆弱 selector → 求助给 (f)（下轮 acceptFragile 命中跳过 AssessFragility）。
        if (detectPriority >= 8 && !string.IsNullOrEmpty(detectSelectorForHelp) && (!acceptFragile || !IsFragileAccepted(FragileType.DetectSelector, detectSelectorForHelp)))
        {
            await LogAsync("录制提示: detect selector 较脆弱，建议提供更稳的 selector 或用 ref");
            // R14（审查选项2）：传 detect 反推链让 ExtractHtmlForHelpAsync 定位到 iframe 抓 outerHTML 显示 (l)（对齐 save_step L1559 step.Iframe / phase pc?.Iframe；原传 null 导致非脆弱 iframe 内抓不到->不显示 l 与三处不对称）。提取 ResolveDetectChainForHelp 供测试直测。
            var detectChainForHelp = ResolveDetectChainForHelp(step);
            var detectHtml = await ExtractHtmlForHelpAsync(detectSelectorForHelp, detectChainForHelp);  // R5：抓 detect selector outerHTML（用反推链定位 iframe；元素不在该链则抓不到->不显示 l 安全降级）
            var (detectFragileReply, _) = await RequestSelectorFragileHelpAsync(_totalSteps + 1, step.Name, _currentPhaseName, action, description,
                $"detect selector 较脆弱（priority={detectPriority}，纯路径/完整路径，DOM 微调即失效）", detectSelectorForHelp, detectChainForHelp, FragileType.DetectSelector, ct, detectHtml);  // 块B：收拢进 helper（含 (f) reply 追加 selector 指引 + (l)/(b) 分流），传反推链让 menu 显示 detect selector 所在 iframe
            return detectFragileReply;
        }

        // 步骤12（a-a-4 结论8/10/14/15 + DC2）：ValidateFallbackAsync 新签名（返 ModifiedFallback+Error+Priority，删 stepIframe ND6）。C′（剩余12′）透传 (totalSteps, step, action, description, ct) 供场景A fragile/提取失败求助。
        // E-8（2026-07-15）：operate fallback 校验步骤号传 _totalSteps+1，对齐 save_step(_totalSteps+1) + detect 求助(_totalSteps+1)，消除既有 off-by-one（原传 _totalSteps 比 detect 少 1）。
        var (modOperateFb, operateFallbackErr, operateFallbackPriority) = await ValidateFallbackAsync(step.Fallback, _totalSteps + 1, step, action, description, ct);
        if (operateFallbackErr != null) return operateFallbackErr;
        step = step with { Fallback = modOperateFb };  // 写回提取 Ref/IframeRef 后的链（清空 ref 字段不落盘）

        // R2-C（a-a-4 批次 E）：detect③ 脆弱链接受（集合命中下轮重传）落盘前补 onError=ai-fallback-stop（依赖 R2-D 回放触发；IsFragileLayer 客观，已接受脆弱链也兜底）。finalStep IsNullOrEmpty 守卫保留此 onError。
        if (detectIsFragileLayer && string.IsNullOrEmpty(step.OnError))
            step = step with { OnError = "ai-fallback-stop" };

        // E-2（方向E username 误伤修复，2026-07-15）：execute 前快照 CurrentSteps.Count，注册块入口据此守卫--操作失败(Success=false)或 execute 内不 Add step(priority9/multimatch/priority8未接受) 时跳过字段注册，消除"失败留脏字段->重试触发 R7 重名误伤"。
        var stepsBefore = CurrentSteps.Count;
        var result = await _actionExecutor.ExecuteOperateAsync(_page, step, _script, CurrentSteps, args, ct, CurrentPhase.LoopSource, acceptFragile: acceptFragile, isAcceptedAsIs: (t, v) => IsAsIsAccepted(t, v), isFragileAccepted: (t, v) => IsFragileAccepted(t, v));  // 批次7-loop：传当前 phase loopSource + 2c acceptFragile + ND7 acceptAsIs 回调 + 待决策1 acceptFragile 回调（阶段0 iframe 脆弱链集合命中 falls-through）
        // 形态 A：删 updatedScript 回写（无 iframeConfig 注册表，ExtractFrameChainAsync 纯函数当场算当场用）。
        // 缺陷3：无论 operate 成败，有反推 iframeChain 就更新 _lastIframeChain（供 get_snapshot 聚焦当前 iframe 上下文，9.2）。
        if (result.IframeChain is { Length: > 0 })
            _lastIframeChain = result.IframeChain;

        // switchTab 后更新当前页（附录 AG：AI 主动切换标签页；同自动 switchTab G.3.9 重置 iframe 上下文）
        if (result.NewPage != null)
        {
            _page = result.NewPage;
            _lastPageCount = _page.Context.Pages.Count;
            _lastIframeChain = null;
        }

        // A2: fieldLabel 触发字段注册（自动嵌套到 loopSource 路径下）
        var fieldName = args.GetValueOrDefault("fieldName")?.ToString();
        var fieldLabel = args.GetValueOrDefault("fieldLabel")?.ToString();
        // E-2：守卫扩展--操作成功(Success)且 step 实际落盘(CurrentSteps.Count > stepsBefore)才注册。
        // 覆盖 operate 失败(Success=false) + priority9/multimatch/priority8未接受(Success=true 但 execute 内不 Add) 三条脏字段路径，消除 R7 重名误伤。
        // acceptAsIs count==0(Add asIsStep)/forceAiAction(Add aiStep) 仍注册(Count 增加，字段定义有效)。
        if (result.Success && CurrentSteps.Count > stepsBefore && fieldName != null && fieldLabel != null)
        {
            // ④-8 🔴-3 录制层：字段名撞系统保留字直接 fail-fast（保留字无协商，区别于字段重名/撞 storeAs 走 RequestHelp）。
            // 字段名进 fillData（scopeChain 末层）会永久遮蔽系统变量（rowIndex/_lastUrl/_lastRowCount/lastError），静默取错值。
            if (SmartFilling.Engine.Engine.ScriptLoader.ReservedVarNames.Contains(fieldName))
                return $"❌ 字段名 '{fieldName}' 与系统保留变量冲突（{string.Join("/", SmartFilling.Engine.Engine.ScriptLoader.ReservedVarNames)}），请换字段名";

            // DEC-7 Y（2026-07-13）：字段名∈系统凭据（username/password）时必须 fieldSource=system（与 ScriptLoader.ValidateSystemCredentialFields 两层一致；Worker fillData 硬编码这两个 key 存系统凭据）。
            // ISSUE-D：此处 fieldDef 尚未构造（L658 构造、Source L668 才从 args["fieldSource"] 设），须提前读 args.GetValueOrDefault("fieldSource") 判断；fieldSource≠system → 反馈 AI 改 source=system 重试。
            if (SmartFilling.Engine.Engine.ScriptLoader.SystemCredentialKeys.Contains(fieldName))
            {
                var earlyFieldSource = args.GetValueOrDefault("fieldSource")?.ToString();
                if (earlyFieldSource != "system")
                    return $"❌ 字段名 '{fieldName}' 是系统凭据字段名（Worker fillData 硬编码该 key 存 payload 用户名/解密密码），其 fieldSource 必须为 system（当前: {earlyFieldSource ?? "null"}）。请设 fieldSource=system 重新调用";
            }

            var path = GetLoopFieldPath();
            var targetFields = EnsureFieldPath(path);

            // R7（2026-06-23 改）：字段重名 → 问用户（对齐 phase 重名 R12 + 准则 3 结构化求助），不再静默跳过
            bool register = true;       // 是否注册本次提取的定义
            bool overwrite = false;     // 注册时覆盖已有定义(true) 还是追加(false)
            string? renameHint = null;  // 非 null 表示让 AI 换名重试（直接 return）

            if (FieldExists(_script.Fields, fieldName))
            {
                var existingField = FindField(_script.Fields, fieldName);
                var reply = await ExecuteRequestHelpAsync(new()
                {
                    ["question"] = "字段「" + fieldName + "」已存在（标签：" + existingField?.Label + "，类型：" + existingField?.Type + "）。这属于哪种情况？请回复 a / b / c：\n" +
                                   "(a) 同一字段，复用已有定义\n" +
                                   "(b) 不同字段——回复新字段名重试；只回 b 则由我起名\n" +
                                   "(c) 覆盖——用本次提取的定义替换已有定义"
                }, ct);

                // 用户取消（录制被取消）→ 原样返回
                if (reply.Contains("取消")) return reply;

                // 解析用户选择；兜底默认 (a) 复用（安全，不破坏已有定义）
                const string prefix = "用户回答:";
                var answerText = reply.StartsWith(prefix) ? reply[prefix.Length..].Trim() : reply;
                bool reuse = answerText.Equals("a", StringComparison.OrdinalIgnoreCase) || answerText.Equals("1")
                    || answerText.Contains("复用") || answerText.Contains("继续") || answerText.Contains("原") || answerText.Contains("同一")
                    || string.IsNullOrWhiteSpace(answerText);  // 兜底默认 a 复用
                bool over = answerText.Equals("c", StringComparison.OrdinalIgnoreCase) || answerText.Equals("3")
                    || answerText.Contains("覆盖") || answerText.Contains("替换") || answerText.Contains("重写");

                // 先判 over（覆盖/替换/重写是明确破坏性意图，优先识别），再判 reuse——
                // 避免「覆盖原有的」「替换原来的」等含「原」字的覆盖措辞，被 reuse 的「原」关键词误判为复用（审查发现 1）
                if (over)
                {
                    overwrite = true;  // (c) 覆盖：进入注册分支但替换已有定义
                }
                else if (reuse)
                {
                    register = false;  // (a) 复用：不注册，step.Field 已引用原 fieldName
                }
                else
                {
                    // (b) 换名（a-cached-hopper）：不解析用户原话（自然语言措辞多样：「选 b」「用 billNo」「新名字用billNo,选b」等，
                    // 位置式正则无法可靠枚举）。透传原话给 AI，用两条规则教 AI 判断新字段名（AI 读 renameHint 后据规则分类）。
                    renameHint = BuildRenameHint(fieldName, answerText);
                }
            }

            // (b) 换名 → 直接返回让 AI 用新名重试（跳过本次注册与失败分流）
            if (renameHint != null) return renameHint;

            if (register)
            {
                // 第一层：代码从 DOM 自动提取
                var lastElement = result.Element;
                var fieldDef = await RecordingActionExecutor.ExtractFieldFromElementAsync(fieldName, fieldLabel, lastElement)
                    ?? new FieldDefinition { Name = fieldName, Label = fieldLabel };

                // 第二层：AI 传入覆盖（全部 fieldXxx 参数映射）
                var aiUiComponent = args.GetValueOrDefault("fieldUiComponent")?.ToString();
                if (!string.IsNullOrEmpty(aiUiComponent)) fieldDef = fieldDef with { UiComponent = aiUiComponent };
                var aiType = args.GetValueOrDefault("fieldType")?.ToString();
                if (!string.IsNullOrEmpty(aiType)) fieldDef = fieldDef with { Type = aiType };
                var aiRequired = GetBool(args, "fieldRequired") == true;  // #40：原 is bool 对 JsonElement 恒 false
                if (aiRequired) fieldDef = fieldDef with { Required = true };
                var aiSource = args.GetValueOrDefault("fieldSource")?.ToString();
                if (!string.IsNullOrEmpty(aiSource)) fieldDef = fieldDef with { Source = aiSource };
                // source=system 的字段（username/password 等系统变量）自动设为必填
                if (fieldDef.Source == "system" && fieldDef.Required != true)
                    fieldDef = fieldDef with { Required = true };
                var aiDesc = args.GetValueOrDefault("fieldDescription")?.ToString();
                if (!string.IsNullOrEmpty(aiDesc)) fieldDef = fieldDef with { Description = aiDesc };
                var aiFormat = args.GetValueOrDefault("fieldFormat")?.ToString();
                if (!string.IsNullOrEmpty(aiFormat)) fieldDef = fieldDef with { Format = aiFormat };
                var aiTransform = args.GetValueOrDefault("fieldTransform")?.ToString();
                if (!string.IsNullOrEmpty(aiTransform)) fieldDef = fieldDef with { Transform = aiTransform };
                var aiPattern = args.GetValueOrDefault("fieldPattern")?.ToString();
                if (!string.IsNullOrEmpty(aiPattern)) fieldDef = fieldDef with { Pattern = aiPattern };
                var aiPlaceholder = args.GetValueOrDefault("fieldPlaceholder")?.ToString();
                if (!string.IsNullOrEmpty(aiPlaceholder)) fieldDef = fieldDef with { Placeholder = aiPlaceholder };
                if (args.GetValueOrDefault("fieldOptions") is JsonElement optsEl && optsEl.ValueKind == JsonValueKind.Array)
                {
                    var opts = optsEl.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
                    if (opts.Count > 0) fieldDef = fieldDef with { Options = opts };
                }
                if (GetBool(args, "fieldMultiple") == true) fieldDef = fieldDef with { Multiple = true };  // #40：原 is bool 对 JsonElement 恒 false

                // AI 传入嵌套子字段（table 类型手动预定义所有子列）
                if (args.TryGetValue("fieldFields", out var ff) && ff is JsonElement ffEl && ffEl.ValueKind == JsonValueKind.Array)
                {
                    var existingSubFields = fieldDef.Fields ?? [];
                    foreach (var item in ffEl.EnumerateArray())
                    {
                        var sf = new FieldDefinition();
                        if (item.TryGetProperty("name", out var fn)) sf = sf with { Name = fn.GetString() ?? "" };
                        if (item.TryGetProperty("label", out var fl)) sf = sf with { Label = fl.GetString() ?? "" };
                        if (item.TryGetProperty("type", out var ft)) sf = sf with { Type = ft.GetString() ?? "string" };
                        if (item.TryGetProperty("uiComponent", out var fu)) sf = sf with { UiComponent = fu.GetString() };
                        if (item.TryGetProperty("required", out var fr) && fr.ValueKind == JsonValueKind.True) sf = sf with { Required = true };
                        if (item.TryGetProperty("source", out var fs)) sf = sf with { Source = fs.GetString() };
                        if (item.TryGetProperty("description", out var fd)) sf = sf with { Description = fd.GetString() };
                        if (item.TryGetProperty("format", out var ffmt)) sf = sf with { Format = ffmt.GetString() };
                        if (item.TryGetProperty("options", out var fopt) && fopt.ValueKind == JsonValueKind.Array)
                            sf = sf with { Options = fopt.EnumerateArray().Select(e => e.GetString() ?? "").ToList() };
                        if (item.TryGetProperty("placeholder", out var fph)) sf = sf with { Placeholder = fph.GetString() };
                        if (item.TryGetProperty("pattern", out var fpt)) sf = sf with { Pattern = fpt.GetString() };
                        if (sf.Name != "")
                        {
                            // 合并：已存在则覆盖（AI 提供的更完整），不存在则追加
                            var existIdx = existingSubFields.FindIndex(f => f.Name == sf.Name);
                            if (existIdx >= 0)
                                existingSubFields[existIdx] = sf;
                            else
                                existingSubFields.Add(sf);
                        }
                    }
                    if (existingSubFields.Count > 0)
                        fieldDef = fieldDef with { Fields = existingSubFields };
                }

                // AI 传入简单数组元素类型（Items）— 仅顶层 array 字段
                if (args.TryGetValue("fieldItems", out var fi) && fi is JsonElement fiEl && fiEl.ValueKind == JsonValueKind.Object)
                {
                    var items = new FieldItems();
                    if (fiEl.TryGetProperty("type", out var it)) items = items with { Type = it.GetString() ?? "string" };
                    if (fiEl.TryGetProperty("format", out var ifmt)) items = items with { Format = ifmt.GetString() };
                    if (fiEl.TryGetProperty("options", out var iopt) && iopt.ValueKind == JsonValueKind.Array)
                        items = items with { Options = iopt.EnumerateArray().Select(e => e.GetString() ?? "").ToList() };
                    if (fiEl.TryGetProperty("min", out var imin)) items = items with { Min = imin.GetDouble() };
                    if (fiEl.TryGetProperty("max", out var imax)) items = items with { Max = imax.GetDouble() };
                    fieldDef = fieldDef with { Items = items };
                }

                if (overwrite)
                    ReplaceField(_script.Fields, fieldName, fieldDef);  // F5(c)：递归替换已有定义（可能在嵌套子字段中）
                else
                    targetFields.Add(fieldDef);

                // 代码未检测到 required 时，提醒 AI 补充判断
                if (fieldDef.Required != true && fieldName != null)
                    result = result with { Message = result.Message + $"\n⚠️字段 {fieldName} 的必填状态未检测到，如该字段必填请传 fieldRequired:true" };
                // 代码未检测到 options 时（仅 select 类型字段），提醒 AI 补充
                if (fieldDef.UiComponent == "select" && (fieldDef.Options == null || fieldDef.Options.Count == 0) && fieldName != null)
                    result = result with { Message = result.Message + $"\n⚠️字段 {fieldName} 是选择类型但未检测到选项列表，请传 fieldOptions" };
            }
        }

        // phase 级 + operate step.Condition 反推 warnings 拼进 message（AI 下一轮可见，不阻断）；Distinct 去重
        var operateWarnings = phaseWarnings.Concat(operateDetectWarnings).Distinct().ToList();
        if (operateWarnings.Count > 0)
            result = result with { Message = result.Message + "\n" + string.Join("\n", operateWarnings) };

        // 步骤13/15（a-a-4 P1.1）：合并 step/fallback priority（caller 单次求助，对策三件套 priority 信号通道贯通）。
        // 待决策3（a-a-4）：MaxPriorityWithSource 共用聚合三源 priority + 归源（step/detect/fallback），平局取未接受源。detect 源用 detectSelectorForHelp（已含 czLeafSelector，待决策2）。
        var (combinedPriority, fragileSourceSelector, fragileSourceType) = MaxPriorityWithSource(
            (result.Priority ?? 0, result.Selector, FragileType.StepSelector),
            (detectPriority, detectSelectorForHelp, FragileType.DetectSelector),
            (operateFallbackPriority, modOperateFb?.Selector, FragileType.FallbackSelector));

        // DC6（a-a-4）onError scope：combinedPriority>=7 → 自动设宿主 step.OnError=ai-fallback-stop（过滤 then/action 避免 check no-op dead weight；wait throw 时生效）。
        if (combinedPriority >= 7 && string.IsNullOrEmpty(step.OnError) &&
            (step.Then is null or "step_error" or "phase_error" or "script_fail" || step.Action == "wait"))
            step = step with { OnError = "ai-fallback-stop" };

        // ⑤（2026-07-09）：detect③ picker + countZero 求助已前移到 execute 前（上方 ⑤ 块），此处删除（原后置致双 step）。
        // 多匹配双 step 本就无（execute 内 L318 不 Add）；countZero/detect priority 8 双 step 由前移消除。

        // V1→2c（G3.4）代码风险门禁：step selector priority 8（纯路径高脆弱）+ 多匹配 → 代码主动触发 request_help，不让脆弱/多匹配 selector 静默落盘。
        // ⑤（2026-07-09）：detect priority 8 已前移到 execute 前（上方 ⑤ 块，用 detect selector）；此处只处理 step selector priority 8 + 多匹配（两者 execute 内已不 Add，无双 step，保持后置）。
        // D9②：每个 priority 8 都求助（无周期标志），防同元素循环纯靠 acceptFragile（用户选 (f) 后下轮 acceptFragile=true+同 selector 跳过门禁）。
        // R3（第1轮核查#1）：step selector 路径两层精控——acceptFragile 总开关 + IsFragileAccepted(StepSelector) 集合精控。
        // 防用户对 selector A 选(f)后，下轮 AI 换 selector B（也 priority 8）+ acceptFragile=true → B silent 落盘（集合精控补）。
        bool sourceAccepted = !string.IsNullOrEmpty(fragileSourceSelector) && IsFragileAccepted(fragileSourceType, fragileSourceSelector);  // 待决策3：按源查集合（替代恒查 StepSelector），防 fallback/detect 源 loop 不消失
        // 内层已对 priority 8 && !acceptFragile 提前 return（不 Add 不执行），此处据信号求助；acceptFragile=true 跳过（接受脆弱落盘+onError）。
        // B2：result.NeedsAiExecution（priority 8 + forceAiAction 存 aiStep）→ 录制期执行 ai action 推进页面。
        if (result.NeedsAiExecution)
        {
            await ExecuteAiActionForRecordingAsync(description ?? $"执行 {action} 操作", result.IframeChain, args, ct);
        }
        else if (result.Success && (result.IsMultiMatch || (combinedPriority >= 8 && (!acceptFragile || !sourceAccepted))))
        {
            // priority 9（T11/DC5）：7阶段全失败无法提取 selector（非"脆弱"）→ warning 分档 + htmlContext 让用户选(b)粘 selector。
            bool isPriority9 = result.Priority == 9 && !result.IsMultiMatch;
            // step selector 脆弱/多匹配/priority 9（detect 脆弱已前移用 detect selector，此处用 result.Selector）。待决策3：fragileSelector 源优先（求助显示与集合填入同源）。
            var fragileSelector = fragileSourceSelector ?? result.Selector ?? args.GetValueOrDefault("selector")?.ToString();
            await LogAsync(result.IsMultiMatch
                ? "录制提示: selector 多匹配，需用户指认或换 selector"
                : isPriority9 ? "录制提示: 7阶段 selector 提取全失败，需用户提供 selector 或 HTML"
                : $"录制提示: selector 较脆弱（priority={result.Priority}），建议用户提供更稳的 selector 或 HTML");
            var prioWarning = result.IsMultiMatch ? "selector 匹配多个元素（多匹配，回放 strict mode 会抛错）"
                : isPriority9 ? $"无法提取稳定 selector（7 阶段候选全部失败）{(result.HtmlContext != null ? $"\n元素 HTML:\n{TruncateForUser(result.HtmlContext)}" : "")}"  // R4 #2/R3：给用户截前 50 字（给 AI 全量由 AppendHelpHtmlIfNeeded 处理）
                : $"selector 较脆弱（priority={result.Priority}，纯路径/完整路径，DOM 微调即失效）";
            // R5/R4 #3：先算 htmlForAi（决定是否显示 (l)）。提取 ComputeHtmlForAiAsync 供测试直测（T-F2 路径2，纯提取行为不变）。
            // priority9 用 result.HtmlContext；priority8 StepSelector 源用 result.Element（step 元素，匹配）；FallbackSelector/DetectSelector 源用 ExtractHtmlForHelpAsync(fragileSelector, 对应 iframe 链) 抓求助 selector 自己的元素
            // （审查第二轮 F2：result.Element 是 operate step 元素≠fallback/detect selector 元素，直用会喂错 HTML 与求助 selector 不匹配 silent；镜像 save_step L1559 ssFragileSelector 抓）。多匹配不抓（R13 由 b 必填消解=不喂，此为不抓优化）。
            var htmlForAi = await ComputeHtmlForAiAsync(result, isPriority9, fragileSourceType, fragileSelector, modOperateFb, step);
            // R4 #1：prioOptions 三分支（(l) 仅 htmlForAi!=null 时加，R5 降级守卫防选(l) 无 HTML 喂 silent-success）。priority9->Common5|UseExtractedHtml；priority8 step !多匹配->Common5|AcceptFragile|UseExtractedHtml；多匹配->Common5（无 l 无 f）。
            var prioOptions = isPriority9
                ? (htmlForAi != null ? HelpOption.Common5 | HelpOption.UseExtractedHtml : HelpOption.Common5)
                : (combinedPriority == 8 && !result.IsMultiMatch)
                    ? (htmlForAi != null ? HelpOption.Common5 | HelpOption.AcceptFragile | HelpOption.UseExtractedHtml : HelpOption.Common5 | HelpOption.AcceptFragile)
                    : HelpOption.Common5;
            var helpQuestion = BuildHelpQuestion(_totalSteps + 1, _currentPhaseName, step.Name, action, description,
                warning: prioWarning,
                selector: fragileSelector,
                options: prioOptions);
            // W2（a-a-4）：(f) 填集合工作流——求助后解析回答，用户选 (f)→写 _acceptedFragile（否则集合永空→fragileAccepted 恒 false→死循环）。
            var reply = await ExecuteRequestHelpAsync(new() { ["question"] = helpQuestion }, ct);
            // OBS-1（审查方案1）：(f) 守卫提取 TryAcceptFragileReply 供测试直测（验 priority9 menu 无 f 时回 "f" 不填集合）。
            reply = TryAcceptFragileReply(reply, result.IsMultiMatch, fragileSelector, prioOptions, fragileSourceType);
            // R4 #3：选(l) 喂全量代码 HTML / 选(b) 粘了用户为主不喂 / 选(b) 没粘必填提示不喂 / 其他原样（AppendHelpHtmlIfNeeded 统一分流）
            reply = AppendHelpHtmlIfNeeded(reply, htmlForAi, fragileSelector, result.IframeChain);
            return reply;
        }

        // 统一失败分流
        if (!result.Success)
        {
            // ref 失效：检查 message 是否包含 aria-ref（AI 自助）
            if (result.Message.Contains("aria-ref"))
            {
                await LogAsync($"录制提示: {result.Message}");
                return result.Message;
            }

            // ND8（a-a-4，选 A+B）：多 frame 命中 → (j) 指认 picker（优先于 fragile——多 frame 是"哪个 frame"问题，先定 frame 再论脆弱）。
            if (result.MultiFrames is { Count: >= 2 })
            {
                await LogAsync($"录制提示: 目标在 {result.MultiFrames.Count} 个 iframe 都出现，请用户指认");
                return await RequestMultiFrameHelpAsync(_totalSteps + 1, step.Name, _currentPhaseName, action, description,
                    result.IframeWarning ?? result.Message, result.MultiFrames, ct);
            }

            // DC3（a-a-4，P2.5 #2）+ C′（剩余12′）：IsFragileIframe（脆弱层结构化，结论13 C1 每层 AssessFragility>=8）→ RequestIframeFragileHelpAsync（helper 复用 step operate/save_step/fallback 三处脆弱 iframe 求助，含 (f)/(i)）。
            if (result.IsFragileIframe)
            {
                var (reply, _) = await RequestIframeFragileHelpAsync(_totalSteps + 1, step.Name, _currentPhaseName, action, description,
                    result.IframeWarning ?? result.Message, result.IframeChain,
                    result.Selector ?? args.GetValueOrDefault("selector")?.ToString(),
                    FragileType.StepIframe, ct, result.IframeCtx);  // R2-细化：传 ctx（operate 反推脆弱层 ctx 全量，选 (l) 喂 AI；aiChain 路径 result.IframeCtx=null 不显示 l）
                return reply;
            }

            // ND7（a-a-4）：count==0（selector 未找到，元素动态加载未到）→ 求助给 (h) acceptAsIs（下轮命中→record-without-execute+onError 兜底）。
            if (result.IsCountZero && !string.IsNullOrEmpty(result.Selector))
            {
                await LogAsync($"录制提示: selector「{result.Selector}」未找到（可能动态加载），请用户确认是否接受");
                var czQuestion = BuildHelpQuestion(_totalSteps + 1, _currentPhaseName, step.Name, action, description,
                    warning: result.Message, selector: result.Selector, iframeChain: result.IframeChain,
                    options: HelpOption.Common5 | HelpOption.AcceptAsIs);
                var czReply = await ExecuteRequestHelpAsync(new() { ["question"] = czQuestion }, ct);
                if (ReplyChoseOption(czReply, 'h'))
                {
                    _acceptedAsIs.Add((FragileType.StepSelector, result.Selector));  // W2 同款：选 (h) 填集合，下轮 acceptAsIs 命中 record-without-execute
                    czReply = AppendSelectorAsIsGuidance(czReply, result.Selector);  // 块B：(h) reply 追加 selector 指引
                }
                return czReply;
            }

            // 其他所有失败：自动问用户
            await LogAsync($"录制失败: {result.Message}");
            return await ExecuteRequestHelpAsync(new() { ["question"] = result.Message }, ct);
        }

        // E-3（#16 跳号修复，2026-07-15）：_totalSteps++ 从 execute 前移到此处（失败分流 return 后、成功日志前）。
        // 此处必为 result.Success=true 且非 caller-priority-help(return)/失败分流(return)= step 已 Add 的成功路径。
        // 消除 priority9/multimatch/priority8未接受(execute 内不 Add) 路径每轮 execute 前 _totalSteps 涨致求助文本"📍步骤 N"逐轮涨 + auto 命名跳号。
        // 与 save_step(ExecuteSaveStep 前) 同语义：失败/求助不递增，成功落盘才递增。auto-switch-tab/auto-newrow-guard 各自递增并存无冲突。
        _totalSteps++;
        await LogAsync($"录制步骤: [{action}] {description}");

        // 检测新标签页 → 自动插入 switchTab 步骤
        var pages = _page.Context.Pages;
        if (pages.Count > _lastPageCount)
        {
            _lastPageCount = pages.Count;
            var newPage = pages[^1];
            _totalSteps++;  // #8：自动 switchTab 递增计数（原未递增→下一手动步骤 step-{N} 同号冲突，ScriptLoader 同名校验报错）
            CurrentSteps.Add(new StepNode
            {
                Kind = "step",
                Name = $"auto-switch-tab-{StepCount}",  // #8：语义化名（协调 #1 强制 name；避免与 AI 命名空间冲突）
                Action = "switchTab",
                Index = -1,
                Description = "自动切换到新标签页"
            });
            _page = newPage;
            await newPage.BringToFrontAsync();
            // G.3.9：新标签页 DOM 结构不同，旧 tab 的 iframe 引用失效，重置 iframe 上下文
            _lastIframeChain = null;
        }

        // D-A 集合清空时机（结论3）：operate step 落盘后清空两集合（保留下轮换值触发重新求助；求助/失败未落盘不清空）。
        ClearAcceptedSets();
        return result.Message;
    }

    // S4 改动3：operate/save_step 读取映射辅助方法（int/bool/object 类型的 JSON 值统一解析）
    private static int? GetInt(Dictionary<string, object> args, string key)
    {
        if (!args.TryGetValue(key, out var v) || v == null) return null;
        if (v is int i) return i;
        if (v is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Number) return je.GetInt32();
        return null;
    }

    internal static bool? GetBool(Dictionary<string, object> args, string key)  // #40/🟢-2：提 internal 供 RecordingActionExecutor 共享（原 private 导致跨类重复内联 bool 解析）
    {
        if (!args.TryGetValue(key, out var v) || v == null) return null;
        if (v is bool b) return b;
        if (v is System.Text.Json.JsonElement je)
        {
            if (je.ValueKind == System.Text.Json.JsonValueKind.True) return true;
            if (je.ValueKind == System.Text.Json.JsonValueKind.False) return false;
        }
        return null;
    }

    // ===== D-A 集合机制辅助方法（a-a-4 P1.2，结论1/2/3）=====
    // acceptFragile/_acceptedAsIs 两集合：用户选 (f)/(h) 后写集合，检测点查集合命中→跳对应校验；step 落盘后清空。
    /// <summary>iframe 链序列化（string[] → " > " 连接），作 HashSet 值（防与 selector 原值碰撞）。空链/null → null。</summary>
    private static string? SerializeChain(string[]? c) => c is { Length: > 0 } ? string.Join(" > ", c) : null;
    /// <summary>用户是否对该脆弱 selector/iframe 选过 (f)（acceptFragile）。命中→跳 AssessFragility 脆弱性校验（不跳唯一性/存在性，D6）。</summary>
    private bool IsFragileAccepted(FragileType t, string? v) => !string.IsNullOrEmpty(v) && _acceptedFragile.Contains((t, v));

    // 待决策3（a-a-4）：共用聚合三源 priority，返回 (max, 源selector, 源type)——priority 来自哪个源就指向哪个 selector/集合类型。
    // 平局优先取未接受源（让用户有机会处理尚未接受的脆弱源）；b.Sel!=null 守卫避免 null 入集合查。
    private (int Max, string? Selector, FragileType Type) MaxPriorityWithSource(
        (int Prio, string? Sel, FragileType Type) a,
        (int Prio, string? Sel, FragileType Type) b,
        (int Prio, string? Sel, FragileType Type) c)
    {
        var max = a;
        if (b.Prio > max.Prio || (b.Prio == max.Prio && b.Sel != null
            && !IsFragileAccepted(b.Type, b.Sel) && IsFragileAccepted(max.Type, max.Sel))) max = b;
        if (c.Prio > max.Prio || (c.Prio == max.Prio && c.Sel != null
            && !IsFragileAccepted(c.Type, c.Sel) && IsFragileAccepted(max.Type, max.Sel))) max = c;
        return max;
    }
    /// <summary>用户是否对该 selector/iframe 选过 (h)（acceptAsIs）。命中→跳存在性校验（count==0 类 SelectorExistsAsync / strict 类 ValidateSaveStepSelectorAsync 存在性部分）。</summary>
    private bool IsAsIsAccepted(FragileType t, string? v) => !string.IsNullOrEmpty(v) && _acceptedAsIs.Contains((t, v));
    /// <summary>step 落盘后清空两集合（结论3：落盘清空；求助返回未落盘不清空，保留下轮重试命中换值触发重新求助）。</summary>
    private void ClearAcceptedSets() { _acceptedFragile.Clear(); _acceptedAsIs.Clear(); }

    /// <summary>
    /// W2/T5（a-a-4）：解析 ExecuteRequestHelpAsync 回答是否选了某选项字母 (a)-(k)。
    /// 回复格式 "用户回答: &lt;answer&gt;"。answer 以 "(X)"/"X)"/"X."/"X "/单字 "X" 开头（大小写不敏感）→ 选了 X；
    /// 回复不含字母 → 作 (k) 其它（返 false，不填集合，AI 自由分析）。防 W2 死循环：用户选(f)/(h) 须据此填集合。
    /// </summary>
    private static bool ReplyChoseOption(string reply, char option)
    {
        // R6（放法 X 块A）：单一解析源--改调 ParseReplyWithRest，防 (b) 分流（ParseReplyWithRest）与 (f)/(h) 判断（本方法）对同一回复解析不一致 -> silent 走错分支。签名不变。
        var (opt, _) = ParseReplyWithRest(reply);
        return opt.HasValue && opt.Value == char.ToLowerInvariant(option);
    }

    /// <summary>
    /// R6/R8（放法 X 块A，2026-07-14）：剥离"用户回答:"前缀 + Trim -> 识别 a-l 字母选项 + 剩余 rest。
    /// 大小写不敏感（对齐原 ReplyChoseOption ToLowerInvariant）。返 (字母?, rest)；未选字母返 (null, 整个 s)。
    /// 规则（计划 #13）：(a) 单字 s[0]∈a-l -> (该字母,"")；(b) s[0]∈a-l + s[1]∈分隔符集合 -> (该字母, s[2..].Trim())；
    /// (c) s 含 "(X)"(X∈a-l) -> (X, 去"(X)"后剩余)；(d) 首字母非 a-l 且无 (X) -> (null, 整个 s)（作 (k) 其它）。
    /// a-l 范围含新增 l=UseExtractedHtml（原 a-k 11 字母漏 l，已修--选(l) 回复"l"会被误判(k)其它）。
    /// </summary>
    internal static (char? Option, string Remaining) ParseReplyWithRest(string reply)
    {
        const string prefix = "用户回答:";
        var s = reply.StartsWith(prefix) ? reply[prefix.Length..].Trim() : reply.Trim();
        if (string.IsNullOrEmpty(s)) return (null, "");

        var first = char.ToLowerInvariant(s[0]);
        if (first >= 'a' && first <= 'l')
        {
            if (s.Length == 1) return (first, "");  // (a) 单字
            var next = s[1];
            if (next == ')' || next == '.' || next == ' ' || next == '、' || next == '：' || next == ':' || next == '，')
                return (first, s[2..].Trim());  // (b) 首字母+分隔符
        }
        // (c) (X) 形态
        var m = System.Text.RegularExpressions.Regex.Match(s, @"\(([a-lA-L])\)");
        if (m.Success)
        {
            var opt = char.ToLowerInvariant(m.Groups[1].Value[0]);
            return (opt, s.Replace(m.Value, "").Trim());
        }
        // (d) 未选字母
        return (null, s);
    }

    /// <summary>
    /// R8（放法 X 块A）：判 rest 是否用户粘了 HTML（与前端 app.js userPastedHtml 两份一致，前端 UX 拦截 + 后端权威防绕过）。
    /// ① XPath 守卫：rest 以 // 或 / 开头且不含 < -> XPath 非 HTML，不算粘（防选(b) 误粘 XPath）；
    /// ② HTML 标签正则命中 -> 粘了；③ 含 &lt; 或 &gt; 残缺 -> 粘了（保守）；④ 纯文字无 &lt;&gt; -> 没粘。
    /// </summary>
    internal static bool UserPastedHtml(string? rest)
    {
        if (string.IsNullOrEmpty(rest)) return false;
        if ((rest.StartsWith("//") || rest.StartsWith("/")) && !rest.Contains('<')) return false;  // XPath 守卫
        if (System.Text.RegularExpressions.Regex.IsMatch(rest, @"</?[a-zA-Z][a-zA-Z0-9]*\b[^>]*>")) return true;  // HTML 标签
        if (rest.Contains('<') || rest.Contains('>')) return true;  // 残缺 < >
        return false;  // 纯文字
    }

    /// <summary>R3（放法 X 块A）：给用户看的 HTML 截前 50 字符 + "..."（给 AI 全量不截断，由 caller 分别处理）。</summary>
    internal static string TruncateForUser(string? s) => string.IsNullOrEmpty(s) ? "" : (s.Length > 50 ? s.Substring(0, 50) + "..." : s);

    /// <summary>
    /// R4/第七节（放法 X 块A）：选(l) 追加全量代码 HTML 给 AI（不截断）。html=outerHTML(selector) 或 ctx(iframe)。
    /// </summary>
    internal static string AppendExtractedHtml(string reply, string? html, string? selector, string[]? iframeChain)
    {
        if (string.IsNullOrEmpty(html)) return reply;  // 无代码 HTML 可喂（不应发生--options 不含 l 时不显示；防 silent 原样返回）
        var chainDesc = iframeChain is { Length: > 0 } ? string.Join(" > ", iframeChain) : "(无)";
        var selDesc = !string.IsNullOrEmpty(selector) ? selector : "(无)";
        return $"{reply}\n[代码已提取] 该求助点元素 HTML（锚定=ref/selector/frame，全量未截断）:\n{html}\n已生成 selector:{selDesc}/iframe 链:{chainDesc}\n👉 基于此 HTML 手写稳定 selector（class+文本），下轮 operate 传 selector（不传 ref）重试";
    }

    /// <summary>
    /// R4 #3（放法 X 块A）：(l)/(b) 分流入口。htmlForAi=代码提取的 HTML（selector outerHTML / iframe ctx），null=无代码 HTML。
    /// 选 l -> AppendExtractedHtml 喂全量；选 b 粘了 -> 用户为主不喂+提示；选 b 没粘 -> 必填提示不喂代码 HTML（b 必填不兜底，用户 2026-07-14 拍板）；其他 -> 原样。
    /// </summary>
    internal static string AppendHelpHtmlIfNeeded(string reply, string? htmlForAi, string? selector, string[]? iframeChain)
    {
        var (opt, rest) = ParseReplyWithRest(reply);
        if (opt == 'l')
            return AppendExtractedHtml(reply, htmlForAi, selector, iframeChain);  // 选(l)：喂全量代码 HTML
        if (opt == 'b')
        {
            if (UserPastedHtml(rest))
            {
                var chainDesc = iframeChain is { Length: > 0 } ? string.Join(" > ", iframeChain) : "(无)";
                var selDesc = !string.IsNullOrEmpty(selector) ? selector : "(无)";
                return $"{reply}\n[代码补充] 你已粘 HTML（以你为主）。代码已抓 selector/链供参考:\n已生成 selector:{selDesc}/iframe 链:{chainDesc}\n👉 基于你粘的 HTML 手写 selector，下轮传 selector 重试（代码不重复喂 HTML 避免冲突）";
            }
            // 没粘：必填提示，不喂代码 HTML（后端防绕过：API 直调 /api/record/respond 绕过前端 confirm 时由此兜住）
            return $"{reply}\n❌ 选(b)必须填 HTML，请重新 request_help 粘贴元素 HTML（或选 (l) 让代码自动提取）";
        }
        return reply;  // 其他选项原样
    }

    /// <summary>
    /// a-cached-hopper（2026-07-16）：字段重名 (b) 换名 hint 构造——透传用户原话 + 教 AI 两条规则判断新字段名。
    /// ① 用户原话提到具体名字（「billNo」「用 X」「叫 X」「新名字用X」等）→ 用该名；
    /// ② 用户只选 b 没给名（「b」「选 b」）→ 由 AI 起一个未被占用的名。
    /// 不解析用户原话（自然语言措辞多样，位置式正则无法可靠枚举），靠 AI 读 hint 后据规则分类。
    /// answerText 为已去 "用户回答:" 前缀的纯用户回答（ExecuteRequestHelpAsync L2360 返前缀，L676 提取去前缀）。
    /// 提 internal static 供测试直测（对齐 BuildHelpQuestion 项目惯例，纯函数 string 拼接无副作用）。
    /// </summary>
    internal static string BuildRenameHint(string fieldName, string answerText)
    {
        return "用户确认「" + fieldName + "」是不同字段，需要换名重试。请按用户原话判断新字段名："
            + "若用户原话里提到了具体字段名（如「billNo」「用 billNo」「叫 billNo」「新名字用billNo」等），"
            + "就使用用户给的那个名字；若用户只是选了 b 没有给名字，由你起一个未被占用的字段名。"
            + "用户原话：\"" + answerText + "\"。请用确定的新字段名重新调用。";
    }

    /// <summary>块B（放法 X，2026-07-14）：(h) count==0 求助选"用当前值"后 reply 追加 selector+acceptAsIs 指引（镜像 selector (f) 追加 + iframe (f) 追加链）。</summary>
    private static string AppendSelectorAsIsGuidance(string reply, string? selector) =>
        $"{reply}\n👉 已接受当前 selector：{selector}（录制时找不到，回放再验证）。请用 acceptAsIs=true 重新 operate/save_step 传同 selector，代码跳过存在性校验落盘 + onError=ai-fallback-stop 兜底";

    /// <summary>
    /// R5（放法 X 块A）：从 selector 抓元素 outerHTML 供 (l)/(b) 喂 AI。唯一可抓（count==1）才返非 null；
    /// 多匹配/count==0/_page==null/带{{}} 返 null（caller 不加 UseExtractedHtml = 不显示 l，防 silent-success）。
    /// 复用 RecordingActionExecutor.ResolveFrame（提 internal static）按 iframeChain 定位 frame。
    /// </summary>
    private async Task<string?> ExtractHtmlForHelpAsync(string? selector, string[]? iframeChain)
    {
        if (_page == null || string.IsNullOrEmpty(selector) || selector.Contains("{{")) return null;
        try
        {
            var loc = RecordingActionExecutor.ResolveFrame(_page, iframeChain).Locator(selector);
            if (await loc.CountAsync() != 1) return null;  // 唯一才抓（多匹配/count==0 降级不显示 l）
            return await _selectorExtractor.CaptureElementContextAsync(loc);
        }
        catch { return null; }
    }

    /// <summary>
    /// C′（剩余12′，2026-07-08）：iframe 脆弱层结构化求助 helper——step operate / step save_step / fallback 场景A 三处共用，消除内联重复。
    /// BuildHelpQuestion(Common5|AcceptFragile|IframeRef) 含 (f) 接受脆弱链 / (i) 指 ref 重新提取；解析回复选 (f)→填 _acceptedFragile（按 fragileType 区分 StepIframe/FallbackIframe）。
    /// 复用语义边界（reuse-method-semantics-boundary）：补 selector 参数（plan 伪代码漏——两 step caller 都传元素 selector 供 (b)/(c) 重定位，helper 不可丢）；
    /// 求助动作复用，落盘目标（step.Iframe vs fallback.Iframe）由 caller 据 AcceptedFragile 自写（helper 不碰）；非 fragile（提取失败 chain=null）不走本 helper（caller 走通用 ExecuteRequestHelpAsync）。
    /// </summary>
    private async Task<(string Reply, bool AcceptedFragile)> RequestIframeFragileHelpAsync(
        int totalSteps, string? stepName, string phaseName, string action, string? description,
        string warning, string[]? iframeChain, string? selector, FragileType fragileType, CancellationToken ct,
        string? ctx = null)  // R2-细化 A：iframe 脆弱层 ctx 全量（CaptureFrameContextAsync 产），选 (l) 喂 AI；aiChain 路径 ctx=null 不显示 (l)（防 silent）
    {
        // F4（a-a-4 结论13 C2 落点5 前置）：签名 StepNode step -> stepName+phaseName（对齐 RequestMultiFrameHelpAsync，EnsurePhaseAsync ⑥ 无 StepNode 可传）。
        // R5 降级守卫：有 ctx 才显示 (l)（aiChain 路径/提取失败 ctx=null 时不显示，防选(l) 无 HTML 可喂 silent-success）。
        var opts = HelpOption.Common5 | HelpOption.AcceptFragile | HelpOption.IframeRef;
        if (ctx != null) opts |= HelpOption.UseExtractedHtml;
        var q = BuildHelpQuestion(totalSteps, phaseName, stepName, action, description,
            warning: warning, selector: selector, iframeChain: iframeChain,
            options: opts);
        var reply = await ExecuteRequestHelpAsync(new() { ["question"] = q }, ct);
        bool accepted = iframeChain is { Length: > 0 } && ReplyChoseOption(reply, 'f');
        if (accepted)
        {
            _acceptedFragile.Add((fragileType, SerializeChain(iframeChain)!));
            // R2-B F3①（a-a-4 批次 E）：reply 引导 AI 走 (a) 反推路径（acceptFragile=true 重新 execute 让代码反推用已接受的链），非"传 iframe 参数"走 (b) AI 链路径。(a) 自带 onError 兜底（R2-B finalStep 统一设）+ 集合精控；反推确定性（同 target+selector->同链）保证链一致。
            reply = $"{reply}\n👉 已接受脆弱 iframe 链：{SerializeChain(iframeChain)}。请用 acceptFragile=true 重新 execute（operate/save_step），让代码反推并用已接受的链";
        }
        // R2-细化/R4：选 (l) 喂全量 ctx / 选 (b) 分流（ctx 作 htmlForAi；没粘必填提示不喂）
        reply = AppendHelpHtmlIfNeeded(reply, ctx, selector, iframeChain);
        return (reply, accepted);
    }

    /// <summary>
    /// 块B（放法 X，2026-07-14）：selector priority 8 脆弱求助 helper，镜像 RequestIframeFragileHelpAsync（iframe 版），收拢 detect/save_step/phase 散落内联。
    /// BuildHelpQuestion(Common5|AcceptFragile|UseExtractedHtml?) + 解析选(f)->填 _acceptedFragile + reply 追加 selector+acceptFragile 指引 + AppendHelpHtmlIfNeeded((l)/(b) 分流)。
    /// selector 版 (f) reply 措辞不照搬 iframe 版反推链：selector 是 AI 传的（非反推），acceptFragile 跳过 AssessFragility 落盘+onError 兜底。
    /// </summary>
    private async Task<(string Reply, bool AcceptedFragile)> RequestSelectorFragileHelpAsync(
        int totalSteps, string? stepName, string phaseName, string action, string? description,
        string warning, string? selector, string[]? iframeChain, FragileType fragileType, CancellationToken ct,
        string? htmlForAi = null)
    {
        var opts = HelpOption.Common5 | HelpOption.AcceptFragile;
        if (htmlForAi != null) opts |= HelpOption.UseExtractedHtml;  // R5 降级守卫：有 html 才显示 (l)
        var q = BuildHelpQuestion(totalSteps, phaseName, stepName, action, description,
            warning: warning, selector: selector, iframeChain: iframeChain, options: opts);
        var reply = await ExecuteRequestHelpAsync(new() { ["question"] = q }, ct);
        bool accepted = !string.IsNullOrEmpty(selector) && ReplyChoseOption(reply, 'f');
        if (accepted)
        {
            _acceptedFragile.Add((fragileType, selector!));
            reply = $"{reply}\n👉 已接受脆弱 selector：{selector}。请用 acceptFragile=true 重新 operate/save_step 传同 selector，代码跳过脆弱检测（仍查唯一性/存在性）落盘 + 自动 onError=ai-fallback-stop 兜底";
        }
        reply = AppendHelpHtmlIfNeeded(reply, htmlForAi, selector, iframeChain);  // (l)/(b) 分流
        return (reply, accepted);
    }

    /// <summary>
    /// T-F2 路径2（审查）：operate L807 块 htmlForAi 计算提取成 internal 方法供测试直测（避免 mock 完整 ExecuteOperateAsync 的 ExtractAsync 7阶段，测试可直调验 F2 源分流）。
    /// 纯提取（行为不变）：多匹配不抓；priority9 用 result.HtmlContext；priority8 StepSelector 源用 result.Element（step 元素）；FallbackSelector/DetectSelector 源用 ExtractHtmlForHelpAsync(fragileSelector, 对应 iframe 链) 抓求助 selector 自己的元素（F2 修复防喂错 step element HTML silent）。
    /// </summary>
    internal async Task<string?> ComputeHtmlForAiAsync(RecordingActionExecutor.OperateResult result, bool isPriority9, FragileType fragileSourceType, string? fragileSelector, StepFallback? modOperateFb, StepNode step)
    {
        if (result.IsMultiMatch) return null;
        if (isPriority9) return result.HtmlContext;
        if (fragileSourceType == FragileType.StepSelector && result.Element != null)
            return await _selectorExtractor.CaptureElementContextAsync(result.Element);
        // R14-Save 对齐（独立审查第1轮，2026-07-15）：按源分流定位链--FallbackSelector 源用 modOperateFb?.Iframe，
        // DetectSelector 源用 ResolveDetectChainForHelp(step)（detect 反推链，对齐 operate⑤ R14 L570 + save_step L1594 修复），
        // 其他（StepSelector 无 Element 兜底）用 step.Iframe。DetectSelector 源在 operate 不可达（detect priority8 已⑤前移 L565-575 return），此为防御性对齐 + 与 save_step 一致。
        var opChainForHelp = fragileSourceType == FragileType.FallbackSelector ? modOperateFb?.Iframe
            : fragileSourceType == FragileType.DetectSelector ? ResolveDetectChainForHelp(step)
            : step.Iframe;
        return await ExtractHtmlForHelpAsync(fragileSelector, opChainForHelp);
    }

    /// <summary>
    /// OBS-1（审查方案1）：operate (f) 守卫提取成 internal 供测试直测（验 priority9 menu 无 (f) 时回 "f" 不填集合/不追加指引）。
    /// 守卫须查 prioOptions 含 AcceptFragile——priority9（无 f）/多匹配（无 f）时用户异常回 "f" 不应接受，否则误填 _acceptedFragile->下轮 acceptFragile silent 落盘 priority9 极不稳定 selector。
    /// 纯提取（行为不变）：守卫成立→填 _acceptedFragile + reply 追加 acceptFragile 指引。
    /// </summary>
    internal string TryAcceptFragileReply(string reply, bool isMultiMatch, string? fragileSelector, HelpOption prioOptions, FragileType fragileSourceType)
    {
        if (!isMultiMatch && !string.IsNullOrEmpty(fragileSelector) && (prioOptions & HelpOption.AcceptFragile) != 0 && ReplyChoseOption(reply, 'f'))
        {
            _acceptedFragile.Add((fragileSourceType, fragileSelector!));
            return $"{reply}\n👉 已接受脆弱 selector：{fragileSelector}。请用 acceptFragile=true 重新 operate/save_step 传同 selector，代码跳过脆弱检测（仍查唯一性/存在性）落盘 + 自动 onError=ai-fallback-stop 兜底";  // 块B：(f) reply 追加 selector 指引（镜像 iframe (f) 追加链）
        }
        return reply;
    }

    /// <summary>
    /// R14（审查选项2）：detect priority8 selector 所在 iframe 链（供 ExtractHtmlForHelpAsync 定位抓 outerHTML 显示 (l)）。
    /// 链与 detectSelectorForHelp 同源取（按 Detect?.Selector ?? Until?.Selector ?? Condition?.Selector 顺序，取第一个 Selector 非空源的 Iframe）。
    /// ProcessDetectNodeAsync L2066 已把完整反推链写回 step.Detect/Until/Condition.Iframe（无论脆弱与否），此处现成取用。多源错配时 caller ExtractHtmlForHelpAsync CountAsync!=1->null 安全降级（不喂错 HTML）。
    /// </summary>
    internal static string[]? ResolveDetectChainForHelp(StepNode step)
    {
        if (!string.IsNullOrEmpty(step.Detect?.Selector)) return step.Detect.Iframe;
        if (!string.IsNullOrEmpty(step.Until?.Selector)) return step.Until.Iframe;
        if (!string.IsNullOrEmpty(step.Condition?.Selector)) return step.Condition.Iframe;
        return null;
    }

    /// <summary>
    /// ND8（a-a-4，选 A+B·2026-07-08）：多 frame 命中求助 helper——operate/save_step/fallback/ensure_phase 共用。
    /// BuildHelpQuestion(Common5|IframeRef|FramePicker) 含 (j) frame 列表（编号选）/ (i) ref / (e) 跳过。
    /// (j) N（或纯数字 N）→ 选定 multiFrames[N-1]，reply 追加链提示让 AI 下轮传 iframe=[chain]（避免反推再 multi-frame 死循环）；
    /// (e) 跳过 → 默认第 1 个（A 兜底，同追加链提示）；(i)/(a-d)/(k) → reply 原样（AI/用户自理，(i) 下轮传 iframeRef）。
    /// ⑥（2026-07-09）：签名 step→stepName（EnsurePhaseAsync 无 StepNode，只有 phase 名）+ 加 phaseName 参数（EnsurePhaseAsync 求助时 _currentPhaseName 仍是旧值——
    /// _currentPhaseName=phaseName 在 phase 创建 L2218 后才赋值；caller 显式传 phaseName（step 级传 _currentPhaseName，phase 级传新 phaseName）避免求助显示旧 phase 名）。
    /// </summary>
    private async Task<string> RequestMultiFrameHelpAsync(
        int totalSteps, string? stepName, string phaseName, string action, string? description,
        string warning, IReadOnlyList<MultiFrameHit> multiFrames, CancellationToken ct)
    {
        var q = BuildHelpQuestion(totalSteps, phaseName, stepName, action, description,
            warning: warning, iframeChain: multiFrames[0].Chain,
            options: HelpOption.Common5 | HelpOption.IframeRef | HelpOption.FramePicker,
            frames: multiFrames.Select(m => m.Info).ToList());
        var reply = await ExecuteRequestHelpAsync(new() { ["question"] = q }, ct);
        var idx = ParseChosenFrameIndex(reply, multiFrames.Count);
        if (idx.HasValue)
        {
            var chosen = multiFrames[idx.Value - 1];
            var name = string.IsNullOrEmpty(chosen.Info.Name) ? "(无 name)" : chosen.Info.Name;
            return $"{reply}\n👉 已选定 frame {idx.Value}（{name}），iframe 链：{SerializeChain(chosen.Chain)}。请用此 iframe 链重新执行（detect③ 指认时传 detect/until/condition.iframe；operate/save_step/fallback 指认时传 iframe 参数）";
        }
        if (ReplyChoseOption(reply, 'e'))
        {
            var first = multiFrames[0];
            var name = string.IsNullOrEmpty(first.Info.Name) ? "(无 name)" : first.Info.Name;
            return $"{reply}\n👉 跳过=用默认第 1 个 frame（{name}），iframe 链：{SerializeChain(first.Chain)}。请用此 iframe 重新执行";
        }
        return reply;
    }

    /// <summary>ND8：解析求助回复中的 frame 编号（1-based，&lt;=max）。"用户回答: j 2"/"2"/"(j)2" → 2。无数字/越界 → null（(i)/(a-d)/(k)/(e) 等）。</summary>
    private static int? ParseChosenFrameIndex(string reply, int max)
    {
        const string prefix = "用户回答:";
        var answer = reply.StartsWith(prefix) ? reply[prefix.Length..].Trim() : reply.Trim();
        var match = System.Text.RegularExpressions.Regex.Match(answer, @"\d+");
        if (match.Success && int.TryParse(match.Value, out var n) && n >= 1 && n <= max) return n;
        return null;
    }

    /// <summary>形态 A：读取 iframe selector 链参数（AI 经 JSON 传 string[]，OpenAiProvider 归一化为 JsonElement Array 或 List）。</summary>
    internal static string[]? GetStringArray(Dictionary<string, object> args, string key)
    {
        if (!args.TryGetValue(key, out var v) || v == null) return null;
        switch (v)
        {
            case string[] arr when arr.Length > 0:
                return arr;
            case System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Array:
                var list = new List<string>();
                foreach (var item in je.EnumerateArray())
                {
                    var s = item.ValueKind == System.Text.Json.JsonValueKind.String ? item.GetString() : item.ToString();
                    if (!string.IsNullOrEmpty(s)) list.Add(s);
                }
                return list.Count > 0 ? list.ToArray() : null;
            case System.Collections.IList il:
            {
                var list2 = new List<string>();
                foreach (var item in il)
                {
                    var s = item?.ToString();
                    if (!string.IsNullOrEmpty(s)) list2.Add(s);
                }
                return list2.Count > 0 ? list2.ToArray() : null;
            }
            default:
                return null;
        }
    }

    /// <summary>读取并反序列化 object 类型参数（condition/preSetup/retry/fallback/detect/until 等）</summary>
    private static T? GetObject<T>(Dictionary<string, object> args, string key) where T : class
    {
        if (!args.TryGetValue(key, out var v) || v == null) return null;
        var json = v is System.Text.Json.JsonElement je ? je.GetRawText() : v.ToString();
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<T>(json, _jsonOptions); }
        catch { return null; }
    }

    internal async Task<string> ExecuteSaveStep(Dictionary<string, object> args, CancellationToken ct)
    {
        var (phaseError, phaseWarnings) = await EnsurePhaseAsync(args, ct);
        if (phaseError != null) return phaseError;

        var stepType = args.GetValueOrDefault("stepType")?.ToString() ?? "";
        var description = args.GetValueOrDefault("description")?.ToString() ?? "";

        var step = new StepNode
        {
            Kind = "step",
            Name = args.GetValueOrDefault("name")?.ToString() ?? $"step-{StepCount + 1}",
            Action = stepType,
            Description = description,
        };

        // 通用字段映射
        if (args.TryGetValue("selector", out var sel)) step = step with { Selector = sel?.ToString() };
        if (args.TryGetValue("value", out var val)) step = step with { Value = val?.ToString() };
        if (args.TryGetValue("storeAs", out var sa)) step = step with { StoreAs = sa };
        // ④-8 录制层：storeAs 撞名检查（字段名/系统保留字）；校验层 ScriptLoader 落盘兜底
        var saveStepStoreAsCollision = CheckStoreAsCollision(step.StoreAs);
        if (saveStepStoreAsCollision != null) return saveStepStoreAsCollision;
        if (args.TryGetValue("onError", out var oe)) step = step with { OnError = oe?.ToString() };
        if (args.TryGetValue("field", out var fld)) step = step with { Field = fld?.ToString() };
        // 形态 A：iframe selector 链（覆盖语义：AI 仅 warning 时传）。L1 step.Iframe 反推在下面补。
        var saveStepAiChain = GetStringArray(args, "iframe");
        if (saveStepAiChain != null)
        {
            // 形态 A 对齐 operate 阶段0（RecordingActionExecutor.ValidateIframeChainAsync L236）：AI 传的 iframe 链
            // 须经每层父文档唯一定位校验；校验失败→失败反馈，避免非法链落盘导致回放 screenshot/extract/evaluate/ai
            // 在 iframe 内定位失败（silent 落盘→回放崩溃）。
            // （2026-07-03 实施前审查·三轮核查发现：原仅 operate 路径校验，save_step 直接赋值，同语义两路径不一致）
            if (_page != null)
            {
                var (valid, validationWarning) = await RecordingActionExecutor.ValidateIframeChainAsync(_page, saveStepAiChain);
                if (!valid)
                    return validationWarning ?? "AI 传的 iframe 链校验失败（某层多匹配/找不到），请检查或留空让代码自动提取";
            }
            step = step with { Iframe = saveStepAiChain };
        }
        // 结论14 step级（第5轮核查#15 修复）：save_step iframeRef 非空且未传 iframe → ResolveIframeFromRefAsync 提取链（(i)选项落地，防 dead field）。
        var saveStepIframeRef = args.GetValueOrDefault("iframeRef")?.ToString();
        if (!string.IsNullOrEmpty(saveStepIframeRef) && (step.Iframe == null || step.Iframe.Length == 0) && _page != null)
        {
            var (ssIframeChain, ssIframeErr) = await _actionExecutor.ResolveIframeFromRefAsync(_page, saveStepIframeRef);
            if (ssIframeErr != null) return $"❌ iframeRef={saveStepIframeRef} 提取失败: {ssIframeErr}";
            step = step with { Iframe = ssIframeChain };  // 落盘 Iframe（StepNode.IframeRef 未在 step 构造设值，回放不消费）
        }
        // S4：maxAiTurns（ai action 轮次）
        if (GetInt(args, "maxAiTurns") is int matv) step = step with { MaxAiTurns = matv };
        // #15：save_step 补读 skipIf* 修饰符（原 operate 读、save_step 漏；回放 ScriptEngine 消费但录制端丢弃）
        if (GetBool(args, "skipIfDataEmpty") is bool side) step = step with { SkipIfDataEmpty = side };
        if (GetBool(args, "skipIfElementMissing") is bool siem) step = step with { SkipIfElementMissing = siem };
        // 通用修饰符（对齐 operate：condition/preSetup/retry/fallback，原 save_step 漏读，2026-06-26 附录 AC 补）
        if (GetObject<Engine.Models.DetectCondition>(args, "condition") is { } condVal) step = step with { Condition = condVal };
        if (GetObject<Engine.Models.PreSetup>(args, "preSetup") is { } preSetupVal) step = step with { PreSetup = preSetupVal };
        if (GetObject<Engine.Models.StepRetry>(args, "retry") is { } retryVal) step = step with { Retry = retryVal };
        if (GetObject<Engine.Models.StepFallback>(args, "fallback") is { } fallbackVal) step = step with { Fallback = fallbackVal };
        // timeout：通用步骤超时（附录 AD 从 wait case 提到通用区，对齐 operate L384；所有 save_step 动作的单次操作超时）
        if (GetInt(args, "timeout") is int stepTimeout) step = step with { Timeout = stepTimeout };

        // stepType 特定字段
        switch (stepType)
        {
            case "check":
                if (args.TryGetValue("detect", out var detect))
                {
                    var json = detect is JsonElement je ? je.GetRawText() : detect?.ToString() ?? "{}";
                    step = step with { Detect = System.Text.Json.JsonSerializer.Deserialize<Engine.Models.DetectCondition>(json, _jsonOptions) };
                }
                if (args.TryGetValue("then", out var then)) step = step with { Then = then?.ToString() };
                if (args.TryGetValue("message", out var msg)) step = step with { Message = msg?.ToString() };
                if (args.TryGetValue("toPhase", out var tpCheck)) step = step with { ToPhase = tpCheck?.ToString() };
                if (args.TryGetValue("toStep", out var tsCheck)) step = step with { ToStep = tsCheck?.ToString() };
                if (args.TryGetValue("cleanupSteps", out var cs) && cs is JsonElement csEl && csEl.ValueKind == JsonValueKind.Array)
                {
                    var cleanupSteps = new List<StepNode>();
                    foreach (var item in csEl.EnumerateArray())
                    {
                        // PhaseItemConverter 要求 "kind" 字段，AI 可能不传。cleanup steps 必定是 step，注入缺省值
                        var raw = item.GetRawText();
                        if (!item.TryGetProperty("kind", out _) && !item.TryGetProperty("Kind", out _))
                            raw = raw.Insert(1, "\"kind\":\"step\",");
                        var s = System.Text.Json.JsonSerializer.Deserialize<StepNode>(raw, _jsonOptions);
                        if (s != null) cleanupSteps.Add(s);
                    }
                    if (cleanupSteps.Count > 0) step = step with { CleanupSteps = cleanupSteps };
                }
                // 校验：detect 必须有 type，then 不能为空
                if (step.Detect == null || string.IsNullOrEmpty(step.Detect.Type))
                    return "❌ check 的 detect 条件缺少 type 字段。必须指定检测类型（如 url_changed、selector_visible、page_contains 等）";
                if (string.IsNullOrEmpty(step.Then))
                    return "❌ check 必须提供 then 值（continue/phase_success/phase_fail 等）";
                break;
            case "goto":
                if (args.TryGetValue("toPhase", out var tp)) step = step with { ToPhase = tp?.ToString() };
                if (args.TryGetValue("toStep", out var ts)) step = step with { ToStep = ts?.ToString() };
                break;
            case "handleDialog":
                if (GetBool(args, "accept") is bool accVal) step = step with { Accept = accVal };  // 附录 AN：改用 GetBool（原 is bool 对 JsonElement 恒 false，#40 遗留）
                if (args.TryGetValue("dialogPromptText", out var dpt)) step = step with { DialogPromptText = dpt?.ToString() };
                break;
            case "screenshot":
                // S4 N.3.1：folder/filename 允许 AI 传入（任务描述明确指定时），不传则执行时自动生成
                if (args.TryGetValue("folder", out var fld2)) step = step with { Folder = fld2?.ToString() };
                if (args.TryGetValue("filename", out var fn)) step = step with { Filename = fn?.ToString() };
                if (args.TryGetValue("screenshotType", out var st)) step = step with { ScreenshotType = st?.ToString() };
                if (GetBool(args, "saveToFile") is bool stfVal && !stfVal) step = step with { SaveToFile = false };  // 附录 AN：改用 GetBool（#40 遗留）
                if (args.TryGetValue("storeContent", out var sc)) step = step with { StoreContent = sc?.ToString() };
                break;
            case "captcha":
                if (args.TryGetValue("captchaType", out var ctVal)) step = step with { CaptchaType = ctVal?.ToString() };
                if (args.TryGetValue("imageSelector", out var isel)) step = step with { ImageSelector = isel?.ToString() };
                if (args.TryGetValue("inputSelector", out var isel2)) step = step with { InputSelector = isel2?.ToString() };
                if (args.TryGetValue("sliderSelector", out var ss)) step = step with { SliderSelector = ss?.ToString() };
                if (args.TryGetValue("targetSelector", out var ts2)) step = step with { TargetSelector = ts2?.ToString() };
                if (args.TryGetValue("backgroundSelector", out var bs)) step = step with { BackgroundSelector = bs?.ToString() };
                break;
            case "wait":
                if (GetInt(args, "ms") is int msVal) step = step with { Ms = msVal };  // 附录 AM：统一用 GetInt 兜底（原内联 GetInt32 非数字会抛，和 timeout/pollInterval/maxAiTurns 不一致）
                if (args.TryGetValue("until", out var until))
                {
                    var json = until is JsonElement je2 ? je2.GetRawText() : until?.ToString() ?? "{}";
                    step = step with { Until = System.Text.Json.JsonSerializer.Deserialize<Engine.Models.DetectCondition>(json, _jsonOptions) };
                }
                // pollInterval：wait until 的轮询间隔(ms)，任务描述明确指定才传，否则用默认（附录 AD 补，原 save_step 漏；timeout 已提到通用区）
                if (GetInt(args, "pollInterval") is int pollVal) step = step with { PollInterval = pollVal };
                // 校验：ms 和 until 不能都为空
                if (step.Ms == null && (step.Until == null || string.IsNullOrEmpty(step.Until.Type)))
                    return "❌ wait 必须提供 ms（固定时间）或 until（条件等待含 type 字段），不能都为空。示例：ms=2000 或 until={\"type\":\"selector_visible\",\"selector\":\"//*[contains(@class,'desktop')]\"}";
                break;
            case "evaluate":
                if (args.TryGetValue("code", out var code)) step = step with { Code = code?.ToString() };
                if (args.TryGetValue("args", out var evArgs) && evArgs is System.Text.Json.JsonElement evArr && evArr.ValueKind == System.Text.Json.JsonValueKind.Array)
                    step = step with { Args = System.Text.Json.JsonSerializer.Deserialize<List<object>>(evArr.GetRawText(), _jsonOptions) };
                break;
            case "extract":
                if (args.TryGetValue("property", out var prop)) step = step with { Property = prop?.ToString() };
                if (args.TryGetValue("regex", out var rgx)) step = step with { Regex = rgx?.ToString() };
                if (args.TryGetValue("extractType", out var et)) step = step with { ExtractType = et?.ToString() };
                break;
            case "ai":
                // #31：录制期 fail-fast——ai action 的 storeAs 必为 object 格式（运行期 ScriptLoader.ValidateStoreAs 也校验）
                if (step.StoreAs is string || (step.StoreAs is JsonElement jeAi && jeAi.ValueKind == JsonValueKind.String))
                    return "❌ ai action 的 storeAs 必须是 object 格式 {\"变量名\":\"描述\"}，不能是字符串";
                break;
        }

        // W1（a-a-4 P1.1）：acceptFragile/acceptAsIs 前移到 ProcessDetectAsync 前（对齐 operate）。
        var acceptFragile = GetBool(args, "acceptFragile") == true;
        var acceptAsIs = GetBool(args, "acceptAsIs") == true;  // ND7（a-a-4）

        // 附录 R：detect selector/iframe 与 operate 同源处理（check.detect/wait.until/step.Condition 递归反推）
        // 步骤13（a-a-4）：ProcessDetectAsync 5 元（+MaxPriority+CountZero）+ acceptFragile 形参。
        var (processedStep, detectError, detectWarnings, saveDetectPriority, saveDetectCountZero, saveDetectMultiFrames, saveDetectCzLeafSelector, saveDetectIsFragileLayer, saveDetectIsFragileLayerChain, saveDetectFragileLeafSelector, saveDetectIsFragileLayerCtx) = await ProcessDetectAsync(step, acceptFragile, ct);
        if (detectError != null) return detectError;
        step = processedStep;

        // 2c（阶段2）：step.Selector ref 提取（screenshot element/extract 等 step 级 selector，与 operate/detect 同源）。
        // check.detect/wait.until 的 ref 已由 ProcessDetectAsync 处理；此处补 step.Selector 本身（无 detect 包装的 action）。
        var saveStepRef = args.GetValueOrDefault("ref")?.ToString();
        int? saveStepPriority = null;
        if (!string.IsNullOrEmpty(saveStepRef) && string.IsNullOrEmpty(step.Selector) && _page != null)
        {
            var (refSel, prio, refErr) = await _actionExecutor.ExtractSelectorFromRefAsync(_page, saveStepRef, step.Iframe ?? _lastIframeChain);
            if (refErr != null)
                return $"❌ ref={saveStepRef} 提取失败: {refErr}";
            step = step with { Selector = refSel };
            saveStepPriority = prio;
        }
        else if (!string.IsNullOrEmpty(step.Selector) && !step.Selector.Contains("{{") && _page != null)
        {
            // M2（2c，B6）+ 核查第3轮 F1 修复：save_step AI 直接传 selector（无 ref）→ AssessFragility 算脆弱度 + ValidateSaveStepSelectorAsync 校验唯一性（D10 全路径对齐）。
            // ND7（a-a-4）：acceptAsIs 命中（用户选过 (h)）→ 跳存在性校验（count==0 动态加载未到，落盘+onError 兜底）。
            if (!IsAsIsAccepted(FragileType.StepSelector, step.Selector))
            {
                var (ssSelValid, ssSelWarn) = await RecordingActionExecutor.ValidateSaveStepSelectorAsync(_page, step.Iframe, step.Selector);
                if (!ssSelValid)
                {
                    // count==0（找不到，动态加载）且未接受 → 求助给 (h)；多匹配（count>1）不给 (h)（回放必失败）。
                    if (ssSelWarn != null && ssSelWarn.Contains("找不到"))
                    {
                        var ssCzQ = BuildHelpQuestion(_totalSteps + 1, _currentPhaseName, step.Name, stepType, description,
                            warning: ssSelWarn, selector: step.Selector, iframeChain: step.Iframe,
                            options: HelpOption.Common5 | HelpOption.AcceptAsIs);
                        var ssCzReply = await ExecuteRequestHelpAsync(new() { ["question"] = ssCzQ }, ct);
                        if (ReplyChoseOption(ssCzReply, 'h')) { _acceptedAsIs.Add((FragileType.StepSelector, step.Selector)); ssCzReply = AppendSelectorAsIsGuidance(ssCzReply, step.Selector); }  // W2：选 (h) 填集合 + 块B：reply 追加 selector 指引
                        return ssCzReply;
                    }
                    return ssSelWarn ?? $"save_step selector「{step.Selector}」校验失败";
                }
            }
            saveStepPriority = SelectorExtractor.AssessFragility(step.Selector);
        }
        else if (!string.IsNullOrEmpty(step.Selector) && !step.Selector.Contains("{{"))
        {
            // _page==null（测试 mock / 浏览器未就绪）：信任 AI，仅算脆弱度（不校验唯一性，回放兜底）
            saveStepPriority = SelectorExtractor.AssessFragility(step.Selector);
        }

        // 形态 A L1（第九轮回归修复）：save_step 的 screenshot/extract/evaluate/ai 需 iframe 定位，但无 operate 阶段0 反推。
        // 补 step.Iframe 反推（复用既有信号）：优先级0 AI 传链（上方已设）→ 优先级1 按 selector 反推 → 优先级2 _lastIframeChain。
        // 提取失败（warning 非空）→ R1 强求助（不静默降级 _lastIframeChain，否则 screenshot/extract iframe 内场景回放主文档失败）。
        var needsIframe = stepType is "screenshot" or "extract" or "evaluate" or "ai";
        if (needsIframe && _page != null && saveStepAiChain == null)
        {
            string[]? inferredChain = null;
            string? iframeWarning = null;
            bool iframeFragile = false;
            IReadOnlyList<MultiFrameHit>? iframeMultiFrames = null;  // ND8（a-a-4）：多 frame 命中列表
            string? saveStepCtx = null;  // R2-细化：iframe 脆弱层 ctx 全量（提升到外层块作用域，供下方 iframeFragile 求助点传 ctx）
            if (!string.IsNullOrEmpty(step.Selector) && !step.Selector.Contains("{{"))
            {
                var (chain, inferred, warning, isFragileLayer, multiFrames, ctx) = await _actionExecutor.FindFrameForTargetAsync(_page, new FrameTarget { Selector = step.Selector });
                // DC3（a-a-4，T12）：isFragileLayer 结构化（脆弱层，结论13 C1）→ BuildHelpQuestion 含 (f)/(i)；ND8：multiFrames 多 frame → (j) picker。
                // 待决策1 (a)：脆弱分支补 inferredChain=chain（原只设 iframeWarning/iframeFragile/iframeMultiFrames，inferredChain 恒 null → 下方 gate 恒 false → RequestIframeFragileHelpAsync 不可达 silent 退通用求助）。脆弱时 chain 非空，恢复 helper 可达 + 供集合检查。
                if (warning != null) { iframeWarning = warning; iframeFragile = isFragileLayer; iframeMultiFrames = multiFrames; inferredChain = chain; saveStepCtx = ctx; }
                else if (inferred) inferredChain = chain;
            }
            if (inferredChain == null && iframeWarning == null)
                inferredChain = _lastIframeChain;  // 优先级2：无 selector / 真主文档 → 最近 operate 上下文
            if (iframeWarning != null)
            {
                // ND8（a-a-4，选 A+B）：多 frame 命中 → (j) 指认 picker（优先于 fragile——先定 frame 再论脆弱）。
                if (iframeMultiFrames is { Count: >= 2 })
                {
                    return await RequestMultiFrameHelpAsync(_totalSteps + 1, step.Name, _currentPhaseName, stepType, description,
                        iframeWarning, iframeMultiFrames, ct);
                }
                // T12 + C′（剩余12′）+ 待决策1 (a)：save_step iframe 脆弱求助走 RequestIframeFragileHelpAsync（复用 helper，对齐 operate）；非脆弱提取失败仍 Common5|i（无 f）。
                if (iframeFragile && inferredChain is { Length: > 0 })
                {
                    // 待决策1 (a)：集合命中（用户已接受此脆弱链）→ 落盘继续，不求助（镜像 operate 阶段0 falls-through + selector L434 两层跳过）。onError 由 R2-B L1305 gate 统一设（不在此设）。
                    if (!(acceptFragile && IsFragileAccepted(FragileType.StepIframe, SerializeChain(inferredChain))))
                    {
                        var (ssReply, _) = await RequestIframeFragileHelpAsync(_totalSteps + 1, step.Name, _currentPhaseName, stepType, description,
                            iframeWarning, inferredChain, step.Selector, FragileType.StepIframe, ct, saveStepCtx);
                        return ssReply;   // 未命中→求助 return
                    }
                    // 集合命中 → fall-through（不 return）出 if(iframeWarning!=null) 块到下方 step.Iframe 赋值落盘
                }
                else
                {
                    var q = BuildHelpQuestion(_totalSteps + 1, _currentPhaseName, step.Name, stepType, description,
                        warning: iframeWarning, selector: step.Selector, iframeChain: inferredChain,
                        options: HelpOption.Common5 | HelpOption.IframeRef);
                    return await ExecuteRequestHelpAsync(new() { ["question"] = q }, ct);   // 非脆弱提取失败通用求助 return
                }
            }
            step = step with { Iframe = inferredChain ?? Array.Empty<string>() };  // T9（a-a-4 L4544）：inferredChain==null（无反推链+_lastIframeChain，主文档）→ 显式 []（D3 约定）；待决策1 集合命中 fall-through 也到此落盘脆弱链
        }

        // B9（2c，D10/D13）：captcha 5 selector 录制期唯一性校验（同款关卡，对齐 step selector）。
        // 回放 StepExecutor captcha 全 Locator(sel).Screenshot/Fill/BoundingBox strict mode 多匹配抛错；5 selector 逐一校验 + AssessFragility 纳入门禁取 max。
        if (stepType == "captcha" && _page != null)
        {
            var captchaSelectors = new[] { step.ImageSelector, step.InputSelector, step.SliderSelector, step.TargetSelector, step.BackgroundSelector }
                .OfType<string>()
                .Where(s => !string.IsNullOrWhiteSpace(s) && !s.Contains("{{"));
            foreach (var cs in captchaSelectors)
            {
                var (csValid, csWarn) = await RecordingActionExecutor.ValidateSaveStepSelectorAsync(_page, step.Iframe, cs);
                if (!csValid) return csWarn ?? $"captcha selector「{cs}」校验失败";
                var csPrio = SelectorExtractor.AssessFragility(cs);
                if (csPrio > (saveStepPriority ?? 0)) saveStepPriority = csPrio;  // 脆弱度纳入门禁（任一 priority>=8 求助）
            }
        }

        // 步骤12（a-a-4 结论8/10/14/15 + DC2）：ValidateFallbackAsync 新签名（返 ModifiedFallback+Error+Priority）。C′（剩余12′）透传 (totalSteps=_totalSteps+1, step, stepType, description, ct) 供场景A fragile/提取失败求助。
        var (modSaveFb, fallbackErr, saveFallbackPriority) = await ValidateFallbackAsync(step.Fallback, _totalSteps + 1, step, stepType, description, ct);
        if (fallbackErr != null) return fallbackErr;
        step = step with { Fallback = modSaveFb };

        // 步骤13/15（a-a-4）：合并 step/detect/fallback priority（caller 单次求助）。
        var saveDetectSelectorForHelp = saveDetectCzLeafSelector ?? step.Detect?.Selector ?? step.Until?.Selector ?? step.Condition?.Selector;  // 待决策2 (b2)：优先用 count==0 叶子 selector
        // 待决策3（a-a-4）：MaxPriorityWithSource 共用聚合三源 + 归源（step/detect/fallback）；删 saveDetectIsFragileSource（三源统一按源查集合）。
        var (saveCombinedPriority, saveFragileSourceSelector, saveFragileSourceType) = MaxPriorityWithSource(
            (saveStepPriority ?? 0, step.Selector, FragileType.StepSelector),
            (saveDetectPriority, saveDetectSelectorForHelp, FragileType.DetectSelector),
            (saveFallbackPriority, modSaveFb?.Selector, FragileType.FallbackSelector));

        // onError 小点（2c + DC6 a-a-4 + R2-B 批次 E）：selector/detect/fallback priority>=7 或 iframe 链脆弱（step.Iframe>=8）→ 自动设 onError=ai-fallback-stop（过滤 then/action 避免 check no-op dead weight；step.Iframe 已含 inferredChain 赋值）。
        bool saveIframeFragile = step.Iframe is { Length: > 0 } && step.Iframe.Any(s => SelectorExtractor.AssessFragility(s) >= 8);
        if ((saveCombinedPriority >= 7 || saveIframeFragile) && string.IsNullOrEmpty(step.OnError) &&
            (step.Then is null or "step_error" or "phase_error" or "script_fail" || step.Action == "wait"))
            step = step with { OnError = "ai-fallback-stop" };

        // ND8 detect③ full picker（a-a-4 B，2026-07-08）：detect 反推多 frame 命中 → 代码主动 (j) picker（对齐 operate；优先于 countZero/priority——先定 frame）。
        if (saveDetectMultiFrames is { Count: >= 2 })
        {
            await LogAsync($"录制提示: detect 目标在 {saveDetectMultiFrames.Count} 个 iframe 都出现，请用户指认");
            return await RequestMultiFrameHelpAsync(_totalSteps + 1, step.Name, _currentPhaseName, stepType, description,
                $"detect 反推命中 {saveDetectMultiFrames.Count} 个 frame，请用 (j) 编号指认评估该 detect 条件的目标 frame（或 (e) 跳过用默认第 1 个）", saveDetectMultiFrames, ct);
        }

        // ND7（a-a-4）count==0 求助：detect count==0 且未 acceptAsIs → 求助给 (h)。
        // 待决策4·D-DC15（第67轮）：(h) gate per-selector（对齐 operate⑤ L538，详见该处注释）。
        if (saveDetectCountZero && (!acceptAsIs || !IsAsIsAccepted(FragileType.DetectSelector, saveDetectSelectorForHelp)))
        {
            var ssCzQuestion = BuildHelpQuestion(_totalSteps + 1, _currentPhaseName, step.Name, stepType, description,
                warning: $"detect selector 在当前页面找不到（count==0，可能未加载/动态生成/跨域/预期不可见）",
                selector: saveDetectSelectorForHelp,
                options: HelpOption.Common5 | HelpOption.AcceptAsIs);
            var ssCzReply = await ExecuteRequestHelpAsync(new() { ["question"] = ssCzQuestion }, ct);
            if (!string.IsNullOrEmpty(saveDetectSelectorForHelp) && ReplyChoseOption(ssCzReply, 'h'))
            {
                _acceptedAsIs.Add((FragileType.DetectSelector, saveDetectSelectorForHelp));
                ssCzReply = AppendSelectorAsIsGuidance(ssCzReply, saveDetectSelectorForHelp);  // 块B：(h) reply 追加 selector 指引
            }
            return ssCzReply;
        }

        // C2 落点5（结论13）：save_step detect③ iframe 链脆弱（saveDetectIsFragileLayer）→ 求助给 (f)（复用 RequestIframeFragileHelpAsync + 填 DetectIframe 集合，对齐 operate⑤）。isFragileLayer 优先于 selector priority 8。
        if (saveDetectIsFragileLayer && saveDetectIsFragileLayerChain is { Length: > 0 }
            && (!acceptFragile || !IsFragileAccepted(FragileType.DetectIframe, SerializeChain(saveDetectIsFragileLayerChain))))
        {
            var ssFragileIframeWarning = $"detect iframe 链含脆弱层「{string.Join(" > ", saveDetectIsFragileLayerChain)}」（位置选择器层/GUID id/动态锚点，回放易失效），建议 request_help 提供 iframe 的 id/src/name 或更稳 selector 链";
            var (ssFragileIframeReply, _) = await RequestIframeFragileHelpAsync(_totalSteps + 1, step.Name, _currentPhaseName, stepType, description,
                ssFragileIframeWarning, saveDetectIsFragileLayerChain, saveDetectSelectorForHelp, FragileType.DetectIframe, ct, saveDetectIsFragileLayerCtx);
            return ssFragileIframeReply;
        }
        // V1→2c ③ + 步骤15（a-a-4）：saveCombinedPriority>=8（step/detect/fallback/captcha 任一脆弱）→ 代码触发 request_help（门禁须在 ExecuteSaveStep 前，否则 silent 落盘）。
        // 待决策3（a-a-4）：acceptance gate 按源查集合（替代恒查 StepSelector），删 saveDetectIsFragileSource（三源统一）；求助显示/警告/集合填入均按 saveFragileSourceType 归源。
        bool saveSourceAccepted = !string.IsNullOrEmpty(saveFragileSourceSelector) && IsFragileAccepted(saveFragileSourceType, saveFragileSourceSelector);
        if (saveCombinedPriority >= 8 && (!acceptFragile || !saveSourceAccepted))
        {
            var ssFragileSelector = saveFragileSourceSelector ?? step.Selector;  // 待决策3：源优先
            // R14-Save（独立审查第1轮修复，2026-07-15）：按源分流定位链--detect 源用 ResolveDetectChainForHelp(step) 取 detect 反推链
            // （Detect??Until??Condition.Iframe，对齐 operate⑤ R14 L570-571），step 源用 step.Iframe（action 链），fallback 源用 modSaveFb?.Iframe（对齐 operate ComputeHtmlForAiAsync L1151）。
            // 原统一传 step.Iframe 对 detect 源错配：detect selector 在 step.Detect/Until/Condition.Iframe 内（非 step.Iframe action 链），
            // check step（不在 needsIframe）step.Iframe=null 时主文档搜索抓不到->htmlForAi=null->不显示 (l)（与 operate⑤ R14 修复后不对称）。复合 detect 多源错配时 ExtractHtmlForHelpAsync CountAsync!=1->null 安全降级（不喂错 HTML，与现状一致）。
            var ssChainForHelp = saveFragileSourceType == FragileType.DetectSelector ? ResolveDetectChainForHelp(step)
                : saveFragileSourceType == FragileType.FallbackSelector ? modSaveFb?.Iframe
                : step.Iframe;
            var ssHtml = await ExtractHtmlForHelpAsync(ssFragileSelector, ssChainForHelp);  // R5：按源链定位抓 save_step selector outerHTML（多匹配/count==0 抓不到->不显示 l）
            var ssWarning = saveFragileSourceType == FragileType.DetectSelector
                    ? $"detect selector 较脆弱（priority={saveDetectPriority}，纯路径/完整路径，DOM 微调即失效）"
                    : saveFragileSourceType == FragileType.FallbackSelector
                        ? $"fallback selector 较脆弱（priority={saveFallbackPriority}，纯路径/完整路径，DOM 微调即失效）"
                        : $"selector 较脆弱（priority={saveCombinedPriority}，纯路径/完整路径，DOM 微调即失效）";
            var (ssReply, _) = await RequestSelectorFragileHelpAsync(_totalSteps + 1, step.Name, _currentPhaseName, stepType, description,
                ssWarning, ssFragileSelector, ssChainForHelp, saveFragileSourceType, ct, ssHtml);  // 块B：收拢进 helper（含 (f) reply 追加 selector 指引 + (l)/(b) 分流），menu 显示按源链
            return ssReply;
        }

        // R2-C（a-a-4 批次 E）：save_step detect③ 脆弱链接受落盘前补 onError=ai-fallback-stop（依赖 R2-D 回放触发）。
        if (saveDetectIsFragileLayer && string.IsNullOrEmpty(step.OnError))
            step = step with { OnError = "ai-fallback-stop" };

        _totalSteps++;
        var result = _actionExecutor.ExecuteSaveStep(step, CurrentSteps);

        // DC12（a-a-4）：顶层 new_row_appears then:continue → 自动补反 check not(new_row_appears) then:step_error（挡 detect=false silent——加行没生效时 loop 继续 fill 错位 silent Completed）。
        // T8 选a：仅顶层（step.Detect?.Type，不递归嵌套 all/any/not 内的 new_row_appears——罕见，AI/用户手动补）。
        // 落点：ExecuteSaveStep 之后（main check 已入 CurrentSteps），保证顺序=[..., main check, 反 check]（回放 main 先 → 反 check 后；若误插前面则反 check 先执行加行未录即检查→必然 step_error）。
        // Not 内层 Selector/Iframe 从 step.Detect 复制（DC11 同 selector + DC13 同源 _lastRowCount 基线 + S1 断言两端 iframe 一致），不再经 ProcessDetectNodeAsync（避免重复反推 + ②ref 提取阻断）。
        if (step.Action == "check" && step.Detect?.Type == "new_row_appears" && step.Then == "continue")
        {
            _totalSteps++;
            CurrentSteps.Add(new StepNode
            {
                Kind = "step",
                Name = $"auto-newrow-guard-{StepCount}",  // auto- 前缀唯一（照搬 auto-switch-tab-{StepCount}）
                Action = "check",
                Description = "【自动】加行失败保护：not(new_row_appears)——添加行未生效则 step_error",
                Detect = new DetectCondition { Not = new DetectCondition {
                    Type = "new_row_appears", Selector = step.Detect!.Selector, Iframe = step.Detect!.Iframe } },
                Then = "step_error",
                OnError = "ai-fallback-stop"
            });
        }

        // D-A 集合清空时机（结论3）：step 落盘后清空两集合（保留下轮换值触发重新求助；求助返回未落盘不清空）。
        ClearAcceptedSets();

        // §5 warnings 拼进 message（page_contains 反推失败/不一致告警 AI，不阻断保存；AI 下一轮可见自主决定 request_help/改 detect）；Distinct 去重（风险5：Any 多叶子都失败避免刷屏）
        if (detectWarnings.Count > 0)
            result = result with { Message = result.Message + "\n" + string.Join("\n", detectWarnings.Distinct()) };
        // phase 级 warnings（§3c phase.Condition/LoopCondition 反推告警）拼进 message，AI 下一轮可见（不阻断）；Distinct 去重
        if (phaseWarnings.Count > 0)
            result = result with { Message = result.Message + "\n" + string.Join("\n", phaseWarnings.Distinct()) };

        // save_step 成功/失败推送
        if (result.Success)
            await LogAsync($"录制步骤: [{stepType}] {description}");
        else
            await LogAsync($"录制失败: {result.Message}");
        return result.Message;
    }

    /// <summary>
    /// a-a-4（结论8/10/14/15 + DC2 + ND6）：fallback 录制期递归校验 + Ref/IframeRef 提取 + priority 出参。
    /// 删 stepIframe 参数（ND6：四场景录制期都不依赖 stepIframe，自给自足）。返 (ModifiedFallback, Error, Priority)：
    /// - ModifiedFallback：提取 Ref→Selector / IframeRef→Iframe 后的链（caller 写回 step.Fallback，清空 ref 字段不落盘）。
    /// - Error：校验失败（唯一性/链非法/反推失败）；- Priority：递归 max（DC2 caller 据此求助 + 设宿主 step.OnError）。
    /// 结论15 四场景：A 仅 selector（反推 iframe+校验）/ B 仅 iframe（校验链，selector 回放继承）/ C 都有（iframe 内校验 selector+链）/ 默认都没（跳过，回放继承 step）。
    /// D4 选a：场景A/C 复用既有 ValidateSaveStepSelectorAsync（取消新建 ValidateSelectorInIframeAsync）。
    /// C′（剩余12′，2026-07-08）：场景A 反推 iframe 脆弱（isFragileLayer）/提取失败（chain null）不再直接 return error——
    /// fragile 走 RequestIframeFragileHelpAsync（对齐 step operate/save_step，含 (f)/(i)）；提取失败走通用 ExecuteRequestHelpAsync。
    /// 故签名透传 (totalSteps, step, action, description, ct) 供 BuildHelpQuestion 上下文 + 求助调用（caller operate/save_step 作用域内均有这些变量，firsthand 验）。
    /// </summary>
    internal async Task<(StepFallback? Fallback, string? Error, int Priority)> ValidateFallbackAsync(
        StepFallback? fb, int totalSteps, StepNode step, string action, string? description, CancellationToken ct)
    {
        if (fb == null) return (null, null, 0);
        var cur = fb;
        int maxPriority = 0;

        if (_page != null)
        {
            // 结论8：fb.Ref 提取 selector（对齐 DetectCondition.Ref/StepNode，录制期临时→清空）
            if (!string.IsNullOrEmpty(cur.Ref) && string.IsNullOrEmpty(cur.Selector))
            {
                var (refSel, prio, refErr) = await _actionExecutor.ExtractSelectorFromRefAsync(_page, cur.Ref, cur.Iframe);
                if (refErr != null) return (null, $"❌ fallback ref={cur.Ref} 提取失败: {refErr}", 0);
                cur = cur with { Selector = refSel, Ref = null };
                if (prio > maxPriority) maxPriority = prio;
            }
            // 结论14：fb.IframeRef 提取 iframe 链（ResolveIframeFromRefAsync，不复用 FindFrameForTargetAsync——ND3 实证语义错位）
            if (!string.IsNullOrEmpty(cur.IframeRef) && (cur.Iframe == null || cur.Iframe.Length == 0))
            {
                var (chain, refErr) = await _actionExecutor.ResolveIframeFromRefAsync(_page, cur.IframeRef);
                if (refErr != null) return (null, $"❌ fallback iframeRef={cur.IframeRef} 提取失败: {refErr}", 0);
                cur = cur with { Iframe = chain, IframeRef = null };
            }

            bool hasSelector = !string.IsNullOrEmpty(cur.Selector) && !cur.Selector.Contains("{{");
            bool hasIframe = cur.Iframe is { Length: > 0 };
            if (hasSelector && !hasIframe)
            {
                // 场景A：仅 selector → 从 selector 反推 iframe（FindFrameForTargetAsync）+ 校验 selector 在该 iframe（D4 复用 ValidateSaveStepSelectorAsync）
                // C′（剩余12′）：fragile（isFragileLayer+fchain 非空）走 RequestIframeFragileHelpAsync（对齐 step，含 (f)/(i)）；提取失败（fchain null）走通用求助；不再直接 return error。
                var (fchain, finferred, fwarning, fisFragileLayer, fmultiFrames, fctx) = await _actionExecutor.FindFrameForTargetAsync(_page, new FrameTarget { Selector = cur.Selector });
                if (fwarning != null)
                {
                    // ND8（a-a-4，选 A+B）：多 frame 命中 → (j) picker（优先于 fragile——fallback selector 在多 iframe 出现，让用户指认目标 frame）。
                    if (fmultiFrames is { Count: >= 2 })
                    {
                        var mfReply = await RequestMultiFrameHelpAsync(totalSteps, step.Name, _currentPhaseName, action, description, fwarning, fmultiFrames, ct);
                        return (null, $"fallback selector 在 {fmultiFrames.Count} 个 iframe 出现，已求助指认（{mfReply}），请据回复用指定 iframe 重新传 fallback", maxPriority);
                    }
                    if (fisFragileLayer && fchain is { Length: > 0 })
                    {
                        var (fbReply, fbAccepted) = await RequestIframeFragileHelpAsync(totalSteps, step.Name, _currentPhaseName, action, description,
                            fwarning, fchain, cur.Selector, FragileType.FallbackIframe, ct, fctx);
                        if (fbAccepted) cur = cur with { Iframe = fchain };  // 用户接受 (f) → 落盘覆盖链，继续 selector 校验
                        else return (null, $"fallback iframe 链脆弱，用户未接受 (f)，请据回复调整（{fbReply}）", maxPriority);
                    }
                    else  // 提取失败（fchain null）→ 通用求助对齐 step（非 hard-error，AI 可据回复调整）
                    {
                        var efReply = await ExecuteRequestHelpAsync(new() { ["question"] = fwarning }, ct);
                        return (null, $"fallback iframe 提取失败，已求助（{efReply}），请据回复调整", maxPriority);
                    }
                }
                if (finferred && fchain is { Length: > 0 }) cur = cur with { Iframe = fchain };
                // ND7（a-a-4）：fallback selector acceptAsIs 命中 → 跳存在性校验（fallback 录制期不执行，count==0 接受+onError 兜底）。
                if (!IsAsIsAccepted(FragileType.FallbackSelector, cur.Selector))
                {
                    var (v, w) = await RecordingActionExecutor.ValidateSaveStepSelectorAsync(_page, cur.Iframe, cur.Selector!);
                    if (!v)
                    {
                        // count==0（找不到）→ 求助给 (h)；多匹配不给（回放必失败）。
                        if (w != null && w.Contains("找不到"))
                        {
                            var fbCzQ = BuildHelpQuestion(totalSteps, _currentPhaseName, step.Name, action, description,
                                warning: w, selector: cur.Selector, iframeChain: cur.Iframe,
                                options: HelpOption.Common5 | HelpOption.AcceptAsIs);
                            var fbCzReply = await ExecuteRequestHelpAsync(new() { ["question"] = fbCzQ }, ct);
                            if (ReplyChoseOption(fbCzReply, 'h')) { _acceptedAsIs.Add((FragileType.FallbackSelector, cur.Selector!)); fbCzReply = AppendSelectorAsIsGuidance(fbCzReply, cur.Selector); }  // 块B：(h) reply 追加 selector 指引
                            return (null, $"fallback selector「{cur.Selector}」找不到，已求助（{fbCzReply}），请据回复调整或下轮 acceptAsIs=true + 同 selector", maxPriority);
                        }
                        return (null, w ?? $"fallback selector「{cur.Selector}」校验失败", maxPriority);
                    }
                }
                var prioA = SelectorExtractor.AssessFragility(cur.Selector);
                if (prioA > maxPriority) maxPriority = prioA;
            }
            else if (hasIframe && !hasSelector)
            {
                // 场景B：仅 iframe → 校验链；selector 回放期继承 step.Selector（结论15）
                var (iv, iw) = await RecordingActionExecutor.ValidateIframeChainAsync(_page, cur.Iframe!);
                if (!iv) return (null, iw ?? "fallback iframe 链校验失败", maxPriority);
            }
            else if (hasSelector && hasIframe)
            {
                // 场景C：都有 → fb.Iframe frame 内校验 fb.Selector（D4 复用 ValidateSaveStepSelectorAsync）+ 链校验
                // ND7（a-a-4）：fallback selector acceptAsIs 命中 → 跳存在性校验（fallback 录制期不执行，count==0 接受+onError 兜底）。
                if (!IsAsIsAccepted(FragileType.FallbackSelector, cur.Selector))
                {
                    var (v, w) = await RecordingActionExecutor.ValidateSaveStepSelectorAsync(_page, cur.Iframe, cur.Selector!);
                    if (!v)
                    {
                        if (w != null && w.Contains("找不到"))
                        {
                            var fbCzQ = BuildHelpQuestion(totalSteps, _currentPhaseName, step.Name, action, description,
                                warning: w, selector: cur.Selector, iframeChain: cur.Iframe,
                                options: HelpOption.Common5 | HelpOption.AcceptAsIs);
                            var fbCzReply = await ExecuteRequestHelpAsync(new() { ["question"] = fbCzQ }, ct);
                            if (ReplyChoseOption(fbCzReply, 'h')) { _acceptedAsIs.Add((FragileType.FallbackSelector, cur.Selector!)); fbCzReply = AppendSelectorAsIsGuidance(fbCzReply, cur.Selector); }  // 块B：(h) reply 追加 selector 指引
                            return (null, $"fallback selector「{cur.Selector}」找不到，已求助（{fbCzReply}），请据回复调整或下轮 acceptAsIs=true + 同 selector", maxPriority);
                        }
                        return (null, w ?? $"fallback selector「{cur.Selector}」校验失败", maxPriority);
                    }
                }
                var (iv, iw) = await RecordingActionExecutor.ValidateIframeChainAsync(_page, cur.Iframe!);
                if (!iv) return (null, iw ?? "fallback iframe 链校验失败", maxPriority);
                var prioC = SelectorExtractor.AssessFragility(cur.Selector);
                if (prioC > maxPriority) maxPriority = prioC;
            }
            // 默认（都没）→ 跳过校验（回放期继承 step，结论15）
        }

        // 递归下一层（重建链：cur with { Fallback = 递归结果 }）
        var (nextFb, nextErr, nextPrio) = await ValidateFallbackAsync(cur.Fallback, totalSteps, step, action, description, ct);
        if (nextErr != null) return (null, nextErr, Math.Max(maxPriority, nextPrio));
        return (cur with { Fallback = nextFb }, null, Math.Max(maxPriority, nextPrio));
    }

    // ===== 附录 R：detect selector/iframe 与 operate 同源处理（check.detect / wait.until / step.Condition 递归反推）=====

    /// <summary>
    /// detect 同源 operate：①按 detect 类型反推 iframe（替换旧"第一个非主 frame"盲填）②ref→提取 selector ③selector 兜底校验。
    /// 递归 all/any/not（下传同一 warnings 实例）。处理 step.Detect / step.Until / step.Condition。返回 warnings（page_contains 反推失败/不一致告警，§5）。
    /// internal 供 App.Tests 直接驱动（§7 private 可测性：ProcessDetectAsync 是 detect 反推入口，测试需绕过 ExecuteSaveStep 直接验反推结果）。
    /// </summary>
    internal async Task<(StepNode Step, string? Error, List<string> Warnings, int MaxPriority, bool CountZero, IReadOnlyList<MultiFrameHit>? MultiFrames, string? CountZeroLeafSelector, bool IsFragileLayer, string[]? IsFragileLayerChain, string? FragileLeafSelector, string? Ctx)> ProcessDetectAsync(StepNode step, bool acceptFragile = false, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        if (_page == null) return (step, null, warnings, 0, false, null, null, false, null, null, null);

        // D-1：step 级 page_contains 反推前轮询配置（与回放 ExecuteWaitAsync 配置链一致：PollInterval ?? WaitCheckInterval / Timeout ?? DefaultTimeout）。
        // phase 级（EnsurePhaseAsync 调 ProcessDetectNodeAsync 不传 pollOptions）→ pollTimeout=null 不轮询（lenient=true 兜底双重挡）。
        var pollTimeout = step.Timeout ?? _engineOptions.DefaultTimeout;
        var pollInterval = step.PollInterval ?? _engineOptions.WaitCheckInterval;

        int maxPriority = 0;
        bool countZero = false;
        var stepMulti = new List<MultiFrameHit>();  // ND8 B（a-a-4，2026-07-08）：聚合 Detect/Until/Condition 的 multiFrames（去重）给 caller 做 detect③ picker
        string? czLeafSelector = null;  // 待决策2 (b2)：聚合 Detect/Until/Condition 第一个 count==0 叶子 selector（caller (h) 求助用）
        bool pdaIsFragileLayer = false;  // 结论13 C2 速查清单⑦：聚合 detect/until/condition 任一源脆弱（客观，不论接受->R2-C onError 兜底已接受链）
        string[]? pdaIsFragileLayerChain = null;  // 结论13 C2：第一个未接受脆弱链（加 !IsFragileAccepted 跳过已接受->caller (f) gate self-correction 逐轮推进）
        string? pdaIsFragileLayerCtx = null;  // R2-细化 A：第一个未接受脆弱链的 ctx 全量（跟随 pdaIsFragileLayerChain，选 (l) 喂 AI）
        string? pdaFragileLeafSelector = null;  // A3：第一个 priority>=8 count>0 叶（caller detect (f) priority gate 用，与 czLeafSelector count==0 叶信号分离）
        // check.detect（step 级 lenient=false：②ref 提取失败/③selector 校验失败返回 error 阻断）
        if (step.Detect != null)
        {
            var (newDetect, err, dprio, dcz, dmf, dCzLeaf, dFragile, dChain, dFragileLeaf, dCtx) = await ProcessDetectNodeAsync(step.Detect, warnings, lenient: false, pollTimeout: pollTimeout, pollInterval: pollInterval, acceptFragile: acceptFragile, ct: ct);
            if (err != null) return (step, err, warnings, 0, false, null, null, false, null, null, null);
            step = step with { Detect = newDetect };
            if (dprio > maxPriority) maxPriority = dprio;
            if (dcz) { countZero = true; if (czLeafSelector == null) czLeafSelector = dCzLeaf; }
            if (dmf != null) stepMulti.AddRange(dmf);
            // 结论13 C2 速查清单⑦：聚合 detect③ isFragileLayer/IsFragileLayerChain/FragileLeafSelector 到 PDA 出参（caller operate⑤/save_step 据 detectIsFragileLayer 弹 detect③ (f) + R2-C 设 onError）。
            if (dFragile)
            {
                pdaIsFragileLayer = true;
                if (dChain is { Length: > 0 } && pdaIsFragileLayerChain == null && !IsFragileAccepted(FragileType.DetectIframe, SerializeChain(dChain)))
                    { pdaIsFragileLayerChain = dChain; pdaIsFragileLayerCtx = dCtx; }  // R2-细化：ctx 跟随 chain 聚合（braces 防 ctx 赋值变无条件）
            }
            if (dFragileLeaf != null && pdaFragileLeafSelector == null) pdaFragileLeafSelector = dFragileLeaf;
        }
        // wait.until（step 级 lenient=false）
        if (step.Until != null)
        {
            var (newUntil, err, uprio, ucz, umf, uCzLeaf, uFragile, uChain, uFragileLeaf, uCtx) = await ProcessDetectNodeAsync(step.Until, warnings, lenient: false, pollTimeout: pollTimeout, pollInterval: pollInterval, isWaitUntil: true, acceptFragile: acceptFragile, ct: ct);
            if (err != null) return (step, err, warnings, 0, false, null, null, false, null, null, null);
            step = step with { Until = newUntil };
            if (uprio > maxPriority) maxPriority = uprio;
            if (ucz) { countZero = true; if (czLeafSelector == null) czLeafSelector = uCzLeaf; }
            if (umf != null) stepMulti.AddRange(umf);
            // 结论13 C2 速查清单⑦：聚合 until③ 脆弱信号（同 detect③）。
            if (uFragile)
            {
                pdaIsFragileLayer = true;
                if (uChain is { Length: > 0 } && pdaIsFragileLayerChain == null && !IsFragileAccepted(FragileType.DetectIframe, SerializeChain(uChain)))
                    { pdaIsFragileLayerChain = uChain; pdaIsFragileLayerCtx = uCtx; }  // R2-细化：ctx 跟随 chain 聚合（braces 防 ctx 赋值变无条件）
            }
            if (uFragileLeaf != null && pdaFragileLeafSelector == null) pdaFragileLeafSelector = uFragileLeaf;
        }
        // step.Condition（步骤执行前条件，与 step 同 frame 同时机；step 级 lenient=false）
        if (step.Condition != null)
        {
            var (newCond, err, cprio, ccz, cmf, cCzLeaf, cFragile, cChain, cFragileLeaf, cCtx) = await ProcessDetectNodeAsync(step.Condition, warnings, lenient: false, pollTimeout: pollTimeout, pollInterval: pollInterval, acceptFragile: acceptFragile, ct: ct);
            if (err != null) return (step, err, warnings, 0, false, null, null, false, null, null, null);
            step = step with { Condition = newCond };
            if (cprio > maxPriority) maxPriority = cprio;
            if (ccz) { countZero = true; if (czLeafSelector == null) czLeafSelector = cCzLeaf; }
            if (cmf != null) stepMulti.AddRange(cmf);
            // 结论13 C2 速查清单⑦：聚合 condition③ 脆弱信号（同 detect③）。
            if (cFragile)
            {
                pdaIsFragileLayer = true;
                if (cChain is { Length: > 0 } && pdaIsFragileLayerChain == null && !IsFragileAccepted(FragileType.DetectIframe, SerializeChain(cChain)))
                    { pdaIsFragileLayerChain = cChain; pdaIsFragileLayerCtx = cCtx; }  // R2-细化：ctx 跟随 chain 聚合（braces 防 ctx 赋值变无条件）
            }
            if (cFragileLeaf != null && pdaFragileLeafSelector == null) pdaFragileLeafSelector = cFragileLeaf;
        }
        return (step, null, warnings, maxPriority, countZero, DedupMultiFrames(stepMulti), czLeafSelector, pdaIsFragileLayer, pdaIsFragileLayerChain, pdaFragileLeafSelector, pdaIsFragileLayerCtx);
    }

    /// <summary>ND8 B（a-a-4，2026-07-08）：聚合 multiFrames 去重（按 frame name+url，避免组合条件/多 detect 跨节点重复 frame 入 picker 菜单）。空列表→null（caller `is {Count:>=2}` 判定）。</summary>
    private static IReadOnlyList<MultiFrameHit>? DedupMultiFrames(List<MultiFrameHit> src)
    {
        if (src.Count == 0) return null;
        return src.DistinctBy(m => (m.Info.Name ?? "", m.Info.Url ?? "")).ToList();
    }

    /// <summary>
    /// 递归处理单个 DetectCondition（组合递归下传同一 warnings + 同 lenient；叶子三段处理）。
    /// lenient：false→②③失败返 error 阻断 + detect③ 多 frame 填 nodeMultiFrames 给 caller 做 (j) picker；true→降级 warning 不阻断（软告警兼容层）。
    /// ⚠️ D-I+⑥（2026-07-09）后 EnsurePhaseAsync 的 phase.Condition/LoopCondition 也用 lenient=false（创建期校验 + ①②③④ 求助通道对齐 step detect），
    /// 故 production caller 全 false；true 仅遗留兼容层/单测（§7 测 lenient 差异）用。①反推+加固②告警两端一致（不分两套，避免逻辑漂移）。
    /// internal 供 App.Tests 直接驱动（§7：测 lenient 差异 step 阻断 vs phase 告警）。
    /// </summary>
    internal async Task<(SmartFilling.Engine.Models.DetectCondition? Node, string? Error, int Priority, bool CountZero, IReadOnlyList<MultiFrameHit>? MultiFrames, string? CountZeroLeafSelector, bool IsFragileLayer, string[]? IsFragileLayerChain, string? FragileLeafSelector, string? Ctx)> ProcessDetectNodeAsync(
        SmartFilling.Engine.Models.DetectCondition node, List<string> warnings, bool lenient,
        int? pollTimeout = null, int? pollInterval = null, bool isWaitUntil = false, string path = "",
        bool acceptFragile = false, CancellationToken ct = default)
    {
        // 组合条件递归（下传同一 warnings 实例 + 同 lenient + 同 pollOptions/ct/isWaitUntil + acceptFragile）。
        // P1.1（a-a-4）：递归聚合 priority（all/any 取 max，ND1选C not 内层直传不反转）+ countZero（all/any 取 OR，not 直传）。
        // ND8 B（a-a-4，2026-07-08）：递归聚合 multiFrames（all/any 取并集去重，not 直传内层）——detect③ 多 frame 经出参给 caller 做 (j) picker。
        if (node.All != null)
        {
            var list = new List<SmartFilling.Engine.Models.DetectCondition>();
            int maxPrio = 0; bool cz = false;
            var allMulti = new List<MultiFrameHit>();
            string? allCzLeaf = null;  // 待决策2 (b2)：复合 count==0 第一个叶子 selector（caller (h) 求助用）——块级局部（避免与叶子级 czLeafSelector 嵌套范围重名 CS0136）
            bool aggFragile = false; string[]? aggChain = null; string? aggFragileLeaf = null; string? aggCtx = null;  // 结论13 C2 A′：aggFragile 客观（任一子叶脆弱不论接受→R2-C onError 兜底）+ aggChain 第一个未接受脆弱链（查集合跳过→caller gate self-correction）+ aggFragileLeaf 第一个 priority>=8 count>0 叶
            foreach (var c in node.All)
            {
                var (u, e, p, czero, cmf, cCzLeaf, cFragile, cChain, cFragileLeaf, cCtx) = await ProcessDetectNodeAsync(c, warnings, lenient, pollTimeout, pollInterval, isWaitUntil, path + "all › ", acceptFragile, ct);
                if (e != null) return (null, e, 0, false, null, null, false, null, null, null);
                if (u != null) list.Add(u);
                if (p > maxPrio) maxPrio = p;
                if (czero) cz = true;
                if (cmf != null) allMulti.AddRange(cmf);
                if (czero && allCzLeaf == null) allCzLeaf = cCzLeaf;
                if (cFragile)
                {
                    aggFragile = true;
                    if (cChain is { Length: > 0 } && aggChain == null && !IsFragileAccepted(FragileType.DetectIframe, SerializeChain(cChain))) { aggChain = cChain; aggCtx = cCtx; }  // R2-细化：ctx 跟随 chain 聚合
                }
                if (cFragileLeaf != null && aggFragileLeaf == null) aggFragileLeaf = cFragileLeaf;
            }
            return (node with { All = list }, null, maxPrio, cz, DedupMultiFrames(allMulti), allCzLeaf, aggFragile, aggChain, aggFragileLeaf, aggCtx);
        }
        if (node.Any != null)
        {
            var list = new List<SmartFilling.Engine.Models.DetectCondition>();
            int maxPrio = 0; bool cz = false;
            var anyMulti = new List<MultiFrameHit>();
            string? anyCzLeaf = null;  // 待决策2 (b2)——块级局部（避免嵌套范围重名 CS0136）
            bool aggFragile = false; string[]? aggChain = null; string? aggFragileLeaf = null; string? aggCtx = null;  // 结论13 C2 A′
            foreach (var c in node.Any)
            {
                var (u, e, p, czero, cmf, cCzLeaf, cFragile, cChain, cFragileLeaf, cCtx) = await ProcessDetectNodeAsync(c, warnings, lenient, pollTimeout, pollInterval, isWaitUntil, path + "any › ", acceptFragile, ct);
                if (e != null) return (null, e, 0, false, null, null, false, null, null, null);
                if (u != null) list.Add(u);
                if (p > maxPrio) maxPrio = p;
                if (czero) cz = true;
                if (cmf != null) anyMulti.AddRange(cmf);
                if (czero && anyCzLeaf == null) anyCzLeaf = cCzLeaf;
                if (cFragile)
                {
                    aggFragile = true;
                    if (cChain is { Length: > 0 } && aggChain == null && !IsFragileAccepted(FragileType.DetectIframe, SerializeChain(cChain))) { aggChain = cChain; aggCtx = cCtx; }  // R2-细化：ctx 跟随 chain 聚合
                }
                if (cFragileLeaf != null && aggFragileLeaf == null) aggFragileLeaf = cFragileLeaf;
            }
            return (node with { Any = list }, null, maxPrio, cz, DedupMultiFrames(anyMulti), anyCzLeaf, aggFragile, aggChain, aggFragileLeaf, aggCtx);
        }
        if (node.Not != null)
        {
            var (u, e, p, czero, cmf, nCzLeaf, nFragile, nChain, nFragileLeaf, nCtx) = await ProcessDetectNodeAsync(node.Not, warnings, lenient, pollTimeout, pollInterval, isWaitUntil, path + "not › ", acceptFragile, ct);
            if (e != null) return (null, e, 0, false, null, null, false, null, null, null);
            return (node with { Not = u }, null, p, czero, cmf, nCzLeaf, nFragile, nChain, nFragileLeaf, nCtx);  // ND1选C：not 内层 priority/countZero/multiFrames/czLeafSelector/IsFragileLayer/IsFragileLayerChain/FragileLeafSelector/Ctx 直传（不反转）
        }

        // 叶子处理
        var result = node;
        // ND8 B（a-a-4）：detect③ 多 frame 命中经出参给 caller 做 (j) picker（lenient=false 时填——D-I+⑥ 后 step 与 phase.Condition/LoopCondition 均 false，都填 nodeMultiFrames；true 兼容层不填走软告警）。
        IReadOnlyList<MultiFrameHit>? nodeMultiFrames = null;
        // 待决策2 (b2)：czLeafSelector=count==0 叶子 selector（caller (h) 求助用）；结论13 C2/A3 占位变量（B 不填，C 填真实逻辑）。
        string? czLeafSelector = null;
        bool nodeIsFragileLayer = false;
        string[]? nodeIsFragileLayerChain = null;
        string? nodeIsFragileLayerCtx = null;  // R2-细化 A：脆弱层 ctx 全量（选 (l) 喂 AI），跟随 IsFragileLayerChain 聚合（all/any 取第一个脆弱叶 ctx，not 直传，aiChain 路径无 ctx=null）
        string? fragileLeafSelector = null;
        // ND1 附带1：组合条件路径前缀（让 not/all/any 内层 selector 求助文案清晰，如"[在 not › 条件里] detect selector_visible 脆弱"）
        var ctx = string.IsNullOrEmpty(path) ? "" : $"[在 {path}条件里] ";

        // ① iframe：B9（2c，G3.9-B9 第6项）AI 传链先校验（对齐 operate 阶段0 / save_step G2 ValidateIframeChainAsync）；未传才反推。
        // 结论14（a-a-4）：node.IframeRef 非空 → ResolveIframeFromRefAsync 提取链（aria-ref→iframe DOM→IFrame→ExtractFrameChainAsync）。
        // 落盘三分支（对齐 DetectCondition.Ref L1291 模式，W5 防 silent 死链）：成功清空 IframeRef 落盘 Iframe / lenient 失败告警+保留 / 非 lenient 失败返 error。
        if (!string.IsNullOrEmpty(node.IframeRef) && (result.Iframe == null || result.Iframe.Length == 0))
        {
            var (refChain, refIFrameErr) = await _actionExecutor.ResolveIframeFromRefAsync(_page!, node.IframeRef);
            if (refIFrameErr != null)
            {
                if (lenient) { warnings.Add($"{ctx}⚠️ detect iframeRef={node.IframeRef} 提取失败（{refIFrameErr}），已清空 iframeRef"); result = result with { IframeRef = null }; }
                else return (null, $"{ctx}❌ detect iframeRef={node.IframeRef} 提取失败: {refIFrameErr}", 0, false, null, null, false, null, null, null);
            }
            else
            {
                result = result with { Iframe = refChain, IframeRef = null };  // 成功：落盘 Iframe，清空 IframeRef
            }
        }
        if (result.Iframe is { Length: > 0 } aiDetectChain)
        {
            var (divValid, divWarn) = await RecordingActionExecutor.ValidateIframeChainAsync(_page!, aiDetectChain);
            if (!divValid)
            {
                if (lenient) warnings.Add(divWarn ?? "detect iframe 链校验失败");
                else return (null, divWarn ?? "detect iframe 链校验失败", 0, false, null, null, false, null, null, null);
            }
            // C1-A2（结论13）：detect 的 AI 链脆弱 → 设 nodeIsFragileLayer=true + nodeIsFragileLayerChain=aiDetectChain（客观脆弱 + 求助链，③段不 reset A′），caller 据 IsFragileLayer 出参弹 detect③ (f)
            if (aiDetectChain.Any(s => SelectorExtractor.AssessFragility(s) >= 8))
            {
                nodeIsFragileLayer = true; nodeIsFragileLayerChain = aiDetectChain;
            }
            // 校验通过：result.Iframe 保留 AI 链，下面反推 if 不进（Length>0）
        }
        // ① 反推（形态 A：selector 链）。AI 没传 result.Iframe → 按 detect 类型反推（与回放 DetectEvaluator 对 frame 的依赖一致）
        if (result.Iframe == null || result.Iframe.Length == 0)
        {
            var target = InferIframeForDetect(node);
            if (target != null)
            {
                string[]? inferredChain = null;
                // D-1：page_contains 反推前轮询等 keywords 出现（堵 Bug 2：页面慢→即时检测落空→误报诱导 AI 重录同名 wait）。
                // 仅 step 级（lenient=false，check.Detect/wait.Until/step.Condition）；phase 级（lenient=true）不轮询（页面≠执行时，阻塞 phase 创建无意义）。
                // 配置链与回放 ExecuteWaitAsync 对齐：PollInterval ?? WaitCheckInterval / Timeout ?? DefaultTimeout。
                var deadlineMs = pollTimeout ?? _engineOptions.DefaultTimeout;
                var deadlineSec = deadlineMs / 1000;
                bool pollTimedOut = false;
                if (!lenient && pollTimeout.HasValue && node.Type == "page_contains" && target.Keywords != null)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var interval = pollInterval ?? _engineOptions.WaitCheckInterval;
                    // keywords 一出现即中止（while 条件 !KeywordsVisibleAnywhere 一返 true 即退出），不空等
                    while (!await KeywordsVisibleAnywhereAsync(_page!, target.Keywords!))
                    {
                        if (sw.ElapsedMilliseconds > deadlineMs) { pollTimedOut = true; break; }
                        await Task.Delay(interval, ct);
                    }
                }
                // ⑦（2c，方案 C，L58 三要点）：wait.Until selector_visible/selector_exists 反推时机轮询
                // ——元素异步加载未到 DOM 时，①反推+③校验都依赖元素在 DOM，未到则反推失败诱导 AI 重录（与 D-1 page_contains 同类）。
                // 三要点：①时序插 FindFrameForTargetAsync 前；②手段 SelectorExistsAnywhereAsync 遍历所有 frame（含 iframe）；
                // ③判据 isWaitUntil && 类型∈{selector_visible,selector_exists}，仅 wait.Until（check.Detect/step.Condition 一次性语义不轮询）。
                if (!lenient && isWaitUntil && node.Type is "selector_visible" or "selector_exists" && !string.IsNullOrEmpty(target.Selector))
                {
                    var sw7 = System.Diagnostics.Stopwatch.StartNew();
                    var interval7 = pollInterval ?? _engineOptions.WaitCheckInterval;
                    while (!await SelectorExistsAnywhereAsync(_page!, target.Selector!))
                    {
                        if (sw7.ElapsedMilliseconds > deadlineMs) break;  // 超时放弃轮询，继续 FindFrameForTargetAsync（可能 warning，不阻塞）
                        await Task.Delay(interval7, ct);
                    }
                    // 元素进 DOM 后继续反推；超时则继续（FindFrameForTargetAsync 可能失败走 warning，wait step 已保留，回放按 timeout 轮询）
                }
                // N2'：不静默"按主文档处理"。薄封装透传 warning（N3），异常也填 warning 走求助。
                try
                {
                    if (pollTimedOut)
                    {
                        // D-1 轮询超时（D-2 警告触发点①）：keywords 在 deadline 内未出现。跳过 FindFrameForTargetAsync（result.Iframe 保持 null）+ 警告。
                        // 不 return error（保留 wait step，回放端会按 timeout 轮询等到 keywords 出现）。
                        warnings.Add($"⚠️ page_contains keywords 在 {deadlineSec}s 内未出现（页面仍在加载或目标不存在）。wait 回放会继续按 timeout 等待，通常无需处理。**不要据此重录同名 wait**（会导致 step name 重复）；若确信目标该出现，可 request_help 让用户确认。");
                    }
                    else
                    {
                        var (chain, inferred, warning, isFragileLayer, detectMultiFrames, detectCtx) = await _actionExecutor.FindFrameForTargetAsync(_page!, target);  // 结论13 C2：恢复第4元 isFragileLayer（B 占位 _，C 填真实）+ R2-细化 detectCtx（脆弱层 ctx 全量）
                        // ND8 B（a-a-4，2026-07-08）+ ⑤⑥（2026-07-09）：detect③ 多 frame 命中 → full (j) picker（multiFrames 经出参给 caller 做 RequestMultiFrameHelpAsync）。
                        // step 级 caller（operate ⑤ 前移到 execute 前 / save_step）+ phase 级 caller（EnsurePhaseAsync ⑥①）都消费 multiFrames 出参 → picker return reply（不落盘第一 chain）。
                        // 核查轮3 #2 旧兜底（warnings.Add(warning)）已 ⑤⑥ 回退删除：picker reply 是 signal（显示 frame 列表让用户指认），两边都走 reply 不 silent，兜底冗余。
                        // detect①反推：isFragileLayer（脆弱层，结论13 C1，第 4 元已恢复 C2）语义本由 warning 文本承载（DC3 CaptureFrameContext 拼）——picker 路径走 reply 不读 warning（用户指认 frame 后 AI 下轮传 iframe 链覆盖脆弱）；异常 catch 路径（下方 catch）仍 Add warning 供 caller 自理。
                        // 结论13 iframe 每层 AssessFragility 是独立增强（落点速查 L3407），warning 通道仅异常路径承载脆弱信息供 caller request_help。
                        if (!lenient && detectMultiFrames is { Count: >= 2 })
                        {
                            inferredChain = chain;  // first.Chain（默认兜底；(e) 跳过时落盘，(j) 指认后 AI 下轮重传 single frame）
                            nodeMultiFrames = detectMultiFrames;
                        }
                        else if (warning != null)
                        {
                            // C2 落点1（结论13）：isFragileLayer（脆弱链，chain 非空）→ inferredChain=chain + nodeIsFragileLayer=true + nodeIsFragileLayerChain=chain（不 error 短路，落盘 Node.Iframe=chain + 经出参传 isFragileLayer 让 caller 弹 (f)）。multiFrames 守卫防多 frame+脆弱误走 (f)。
                            if (isFragileLayer && chain is { Length: > 0 } && detectMultiFrames is not { Count: >= 2 })
                            {
                                inferredChain = chain;
                                nodeIsFragileLayer = true;
                                nodeIsFragileLayerChain = chain;
                                nodeIsFragileLayerCtx = detectCtx;  // R2-细化：脆弱层 ctx 全量跟随 chain（选 (l) 喂 AI）
                            }
                            else
                            {
                                // N1'：提取失败 warning（chain==null）→ 加入 warnings（AI 可见）。step 级（lenient=false）额外返 error 触发失败分流；phase 级（lenient=true）软告警不阻断（含多 frame warning）。
                                if (lenient) warnings.Add(warning);
                                else return (null, warning, 0, false, null, null, false, null, null, null);
                            }
                        }
                        else if (inferred)
                        {
                            inferredChain = chain;  // 形态 A：无 updatedScript 回写（纯函数当场算当场用）

                            // §4 加固①：page_contains 反推后二次确认（软校验，不阻断不改正）。语义与轮询不同（轮询查任何 frame 出现；二次确认查反推 chain 内出现，复核 chain 准确性），保留。
                            if (node.Type == "page_contains" && target.Keywords != null)
                            {
                                var keywordsVisibleNow = await KeywordsVisibleAnywhereAsync(_page!, target.Keywords);
                                if (!keywordsVisibleNow)
                                    warnings.Add($"⚠️ detect page_contains 反推命中 iframe={string.Join(" > ", chain!)} 但 keywords 复核未在任何 frame 找到（可能页面时序变化），iframe 可能不准");
                            }
                        }
                        else
                        {
                            // §5 加固②（D-2 警告触发点②）：page_contains 反推失败 + 有非主 frame → D-2 警告（措辞与 D-1 超时分支一致，强调"不要重录同名 wait"）。
                            // L1080 的"反推命中但复核未找到"是另一语义警告，不改（保留）。
                            if (node.Type == "page_contains" && target.Keywords != null)
                            {
                                var nonMainCount = 0;
                                foreach (var f in _page!.Frames) if (f != _page!.MainFrame) nonMainCount++;
                                if (nonMainCount > 0)
                                    warnings.Add($"⚠️ page_contains keywords 未在任何 iframe 找到（在主文档可见或 iframe 未加载），通常正常（wait step 已保留，回放按 timeout 轮询）。**不要据此重录同名 wait**（会导致 step name 重复）；若确信目标应在 iframe 内，可 request_help 让用户指认。");
                            }
                            // selector/ref 类反推失败（真主文档）→ 不额外告警（③ selector 存在性校验兜底）
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "detect iframe 反推异常");
                    var warnMsg = $"⚠️ detect iframe 反推异常（{ex.Message}），建议 request_help";
                    if (lenient) warnings.Add(warnMsg);
                    else return (null, warnMsg, 0, false, null, null, false, null, null, null);
                }

                if (inferredChain != null)
                    result = result with { Iframe = inferredChain };
                else
                    result = result with { Iframe = Array.Empty<string>() };  // T9（a-a-4 L4544）：反推类型反推到主文档 → 显式 []（D3：[]=主文档不继承）；target==null 不反推类型保持 null=不适用（W2 限定，L1705）
            }
            // target==null（不反推类型：url_changed/url_contains/dialog_contains/page_exists/always/data_exists）→ result.Iframe 保持空（主文档），
            // 正确（回放这些类型与 frame 无关：读 activePage.Url/vars/Context.Pages，填错反而是 bug）
        }

        // ② ref 提取（复用 operate aria-ref + SelectorExtractor），用 ① 反推后的 iframe chain
        // DC8 拆9'（a-a-4）：new_row_appears 禁 ref——ref 提取是单元素 selector（count=1），new_row_appears 是计数基线（数所有行），语义错位。
        // AI 须走 request_help 粘表格 HTML / 手写基线 selector / 选 (g) evaluate+check(js)。
        if (node.Type == "new_row_appears" && !string.IsNullOrEmpty(node.Ref))
            return (null, $"{ctx}❌ detect「new_row_appears」不能用 ref（ref 提取的是单元素 selector，count 恒 1 失效）。请走 request_help 粘贴整个表格 HTML / 手写基线 selector（如 .jfgrid-row）/ 改用 evaluate+check(js)", 0, false, null, null, false, null, null, null);
        if (!string.IsNullOrEmpty(node.Ref))
        {
            var (selector, priority, refErr) = await _actionExecutor.ExtractSelectorFromRefAsync(_page!, node.Ref, result.Iframe);
            if (refErr != null)
            {
                if (lenient)
                {
                    warnings.Add($"{ctx}⚠️ detect ref={node.Ref} 提取失败（{refErr}），已清空 ref");
                    result = result with { Ref = null };
                }
                else
                {
                    return (null, $"{ctx}❌ detect ref={node.Ref} 提取失败: {refErr}", 0, false, null, null, false, null, null, null);
                }
            }
            else
            {
                result = result with { Selector = selector, Ref = null };  // 清空 ref，只存 selector（回放不感知 ref）
            }
        }

        // ③ selector 兜底校验（a-a-4 DC1/P1.1：detect selector 与 step selector 同款校验关卡 + priority 出参 + countZero 出参 + ND7 acceptAsIs 跳存在性）。
        // 分类（DetectEvaluator 20 类型）：
        //  - count>0 类（含 new_row_appears，DC8选C）：多匹配合法，count==0 设 CountZero 出参（caller 求助给 h，T3选1 所有 isCountBased 都给）+ 脆弱 priority
        //  - strict 抛错类：多匹配回放抛错，校验唯一（失败返 error）+ 脆弱 priority
        //  - 不定位类（url_changed 等）：不校验（IsSelectorBasedDetect 已过滤）
        int leafPriority = 0;
        bool leafCountZero = false;
        if (!string.IsNullOrEmpty(result.Selector) && IsSelectorBasedDetect(result.Type) && !result.Selector.Contains("{{"))
        {
            var iframeDesc = result.Iframe is { Length: > 0 } icd ? string.Join(" > ", icd) : "主页面";
            bool isCountBased = result.Type is "selector_exists" or "selector_gone" or "selector_count" or "iframe_exists" or "new_row_appears";
            // ND7（a-a-4）：acceptAsIs 命中（用户选过 (h)）→ 跳存在性校验（count==0 类 SelectorExistsAsync / strict 类 ValidateSaveStepSelectorAsync 存在性部分）
            bool asIsAccepted = IsAsIsAccepted(FragileType.DetectSelector, result.Selector);
            if (isCountBased)
            {
                if (!asIsAccepted)
                {
                    var exists = await _actionExecutor.SelectorExistsAsync(_page!, result.Selector, result.Iframe);
                    if (!exists)
                    {
                        leafCountZero = true;  // T2选a：count==0 设 CountZero 出参（不返 error 短路，caller 求助给 h；T3选1 所有 isCountBased 都给）
                        czLeafSelector = result.Selector;  // 待决策2 (b2)：count==0 叶子 selector 上传出参（复合聚合取第一个）
                    }
                }
                // AssessFragility 两层（R3）：总开关 acceptFragile + 集合 IsFragileAccepted 精控（防 detect selector B 被总开关 silent 跳过）
                if (!acceptFragile || !IsFragileAccepted(FragileType.DetectSelector, result.Selector))
                    leafPriority = Math.Max(leafPriority, SelectorExtractor.AssessFragility(result.Selector));
            }
            else
            {
                // strict 抛错类：唯一性校验（多匹配/找不到）；多匹配不走 (h)（回放必失败）。
                if (!asIsAccepted)
                {
                    var (vValid, vWarn) = await RecordingActionExecutor.ValidateSaveStepSelectorAsync(_page!, result.Iframe, result.Selector);
                    if (!vValid)
                    {
                        var msg = vWarn != null ? $"{ctx}{vWarn}" : $"{ctx}❌ detect「{result.Type}」selector 校验失败";
                        if (lenient) warnings.Add(msg.Replace("❌", "⚠️"));
                        else return (null, msg, 0, false, null, null, false, null, null, null);
                    }
                }
                if (!acceptFragile || !IsFragileAccepted(FragileType.DetectSelector, result.Selector))
                    leafPriority = Math.Max(leafPriority, SelectorExtractor.AssessFragility(result.Selector));
            }
        }
        // A3（结论13，第63轮）：脆弱 selector 且命中存在元素（leafCountZero=false）→ 赋 FragileLeafSelector（caller detect (f) gate 用，解决复合 priority>=8 count>0 叶 silent；与 czLeafSelector count==0 叶信号分离）。
        if (leafPriority >= 8 && !leafCountZero)
            fragileLeafSelector = result.Selector;
        // DC1 lenient 分档：lenient=false（step 级）→ priority/countZero 走出参 caller 求助（不 Add warning）；lenient=true（phase 级）→ 软告警 Add warning。
        if (lenient)
        {
            if (leafPriority >= 8)
                warnings.Add($"{ctx}⚠️ detect「{result.Type}」selector「{result.Selector}」较脆弱（priority={leafPriority}，DOM 微调易失效），建议提供更稳 selector 或用 ref");
            if (leafCountZero)
                warnings.Add($"{ctx}⚠️ detect「{result.Type}」selector「{result.Selector}」在当前页面找不到（count==0）");
        }

        // T9（a-a-4 B，2026-07-08）：document_ready（不反推类型，无 selector/keywords 锚点）主文档归一产 []（对齐 operate/detect反推类型/save_step L4544 宽版——D3：[]=主文档不继承 / null=继承 phase）。
        // 仅成功叶子出口；error early return 返 null node 不涉及；组合递归的内层 document_ready 叶子在其递归调用的此处归一化（组合 return 带已归一化内层）。
        // document_ready 已有 AI iframeRef/Iframe 链时不归一化（result.Iframe 非空，保留 AI 显式指定）。
        if (result.Type == "document_ready" && (result.Iframe == null || result.Iframe.Length == 0))
            result = result with { Iframe = Array.Empty<string>() };
        return (result, null, leafPriority, leafCountZero, nodeMultiFrames, czLeafSelector, nodeIsFragileLayer, nodeIsFragileLayerChain, fragileLeafSelector, nodeIsFragileLayerCtx);
    }

    /// <summary>
    /// detect 类型 → 反推 target 策略（与回放 DetectEvaluator 对 frame 的依赖一致）。
    /// 返回 null=不反推（主文档）：回放读 activePage.Url/vars/Context.Pages/与 frame 无关的类型，填错反而是 bug。
    /// selector 类（含 selector_text/iframe_exists）：ref 优先，否则 selector（排除 {{}}）；page_contains：keywords。
    /// </summary>
    private static FrameTarget? InferIframeForDetect(SmartFilling.Engine.Models.DetectCondition node)
    {
        var type = node.Type;
        // 不反推：与 frame 无关的类型（回放读 url/vars/pages/dialog）+ document_ready（无 selector/keywords 锚点，#1：目标 frame 由 AI 显式 until.iframe 指定）
        if (type is "url_changed" or "url_contains" or "dialog_contains" or "page_exists" or "always" or "data_exists" or "document_ready")
            return null;
        if (type == "page_contains")
        {
            if (node.Keywords == null || node.Keywords.Length == 0) return null;
            return new FrameTarget { Keywords = node.Keywords };
        }
        // selector 类（含 selector_text/iframe_exists）：ref 优先，否则 selector
        if (!string.IsNullOrEmpty(node.Ref))
            return new FrameTarget { Ref = node.Ref };
        if (!string.IsNullOrEmpty(node.Selector) && !node.Selector.Contains("{{"))
            return new FrameTarget { Selector = node.Selector };
        return null;  // 无 ref/selector 或 selector 含变量 → 无法反推
    }

    /// <summary>
    /// §4 加固①：检查 keywords 是否在当前页面任何 frame（含主文档）的 body.innerText 出现。
    /// 用于 page_contains 反推的二次确认——确认 keywords 录制时已出现，才复核反推 frame。
    /// </summary>
    private async Task<bool> KeywordsVisibleAnywhereAsync(IPage page, string[] keywords)
    {
        try
        {
            foreach (var frame in page.Frames)  // 含 MainFrame
            {
                try
                {
                    var text = await frame.EvaluateAsync<string>("document.body.innerText") ?? "";
                    if (keywords.Any(k => text.Contains(k))) return true;
                }
                catch { /* 单 frame 异常继续下一个 */ }
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// ⑦（2c，方案 C，L58 三要点②手段）：检查 selector 是否在页面任何 frame（含主文档）存在（遍历 page.Frames）。
    /// 用于 wait.Until selector_visible/selector_exists 反推时机轮询——元素在 iframe 内未加载时 SelectorExistsAsync（chain=null 只扫主文档）会误报。
    /// 类比 KeywordsVisibleAnywhereAsync（遍历 frame），但查 selector count 而非 innerText。
    /// </summary>
    private static async Task<bool> SelectorExistsAnywhereAsync(IPage page, string selector)
    {
        try
        {
            foreach (var frame in page.Frames)  // 含 MainFrame
            {
                try
                {
                    if (await frame.Locator(selector).CountAsync() > 0) return true;
                }
                catch { /* 单 frame 异常继续下一个 */ }
            }
        }
        catch { }
        return false;
    }

    /// <summary>需要元素存在的 detect 类型（校验 selector 存在性）。DC8 选C（a-a-4）：含 new_row_appears（count-based 基线，进③ isCountBased 分支校验存在性 count==0 求助(h) + AssessFragility 脆弱(f)，不校验唯一性）。</summary>
    private static readonly HashSet<string> SelectorBasedDetects = new HashSet<string>
    {
        "selector_visible", "selector_exists", "selector_gone", "selector_enabled",
        "selector_checked", "selector_value", "selector_text", "selector_selected",
        "selector_count", "iframe_exists", "new_row_appears"
    };
    private static bool IsSelectorBasedDetect(string? type) => !string.IsNullOrEmpty(type) && SelectorBasedDetects.Contains(type);

    private async Task<string> ExecuteScreenshotAsync(Dictionary<string, object> args)
    {
        if (_page == null) return "浏览器未启动";
        var selector = args.GetValueOrDefault("selector")?.ToString();
        var quality = args.GetValueOrDefault("quality") is JsonElement qe && qe.ValueKind == JsonValueKind.Number ? qe.GetInt32() : 70;
        var (screenshot, message) = await _actionExecutor.ExecuteScreenshotAsync(_page, selector, quality);
        var base64 = Convert.ToBase64String(screenshot);
        var mimeType = screenshot.Length > 1 && screenshot[0] == 0xFF ? "image/jpeg" : "image/png";
        await OnScreenshot?.Invoke("", base64);
        // 截图通过 _messages 注入图片消息给 AI（使用 image_url 格式兼容 DashScope）
        _messages?.Add(new UserChatMessage(
            ChatMessageContentPart.CreateTextPart("[截图]"),
            ChatMessageContentPart.CreateImagePart(new Uri($"data:{mimeType};base64,{base64}"))));
        return message;
    }

    private async Task<string> ExecuteGetSnapshotAsync()
    {
        if (_page == null) return "浏览器未启动";
        // 传最近检测到的 iframe（S2-3），让快照聚焦当前 iframe 上下文
        // 形态 A：传最近反推的 iframe selector 链（9.2 成败都更新），让快照聚焦当前 iframe 上下文
        return await _actionExecutor.ExecuteGetSnapshotAsync(_page, _lastIframeChain);
    }

    private async Task<string> ExecuteRequestHelpAsync(Dictionary<string, object> args, CancellationToken ct)
    {
        var question = args.GetValueOrDefault("question")?.ToString() ?? "需要帮助";

        // E-1（方向E 日志）：求助 question 落 Serilog（Information 级）。question 已经 OnRequestHelp 推 SignalR RequestHelp 通道，
        // 日志直接调 _logger 不走 OnLog（避免重复推 ReceiveLog 通道）。BuildHelpQuestion 是 internal static 无实例 logger，须在调用方此处记。
        _logger.LogInformation($"[Help] {question}");

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingHelpTcs = tcs;

        if (OnRequestHelp != null)
        {
            byte[]? screenshot = null;
            try { if (_page != null) screenshot = await CompressScreenshotAsync(_page); } catch { }
            await OnRequestHelp("", question, screenshot != null ? Convert.ToBase64String(screenshot) : "");
        }

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        try
        {
            var answer = await tcs.Task;
            await LogAsync($"用户回答: {answer}");
            return $"用户回答: {answer}";
        }
        catch (OperationCanceledException)
        {
            return "等待用户回复时录制被取消";
        }
        finally
        {
            _pendingHelpTcs = null;
        }
    }

    /// <summary>
    /// P2.5 通用求助菜单（a-a-4）：参数化 BuildHelpQuestion，按场景 options 动态显示适用选项 (a)-(k)。
    /// (k) 其它（T5）始终含（方法体 options|=Other；回复不含字母→作(k)让 AI 自由分析不填集合）。
    /// 拼 📍 步骤序号/phase/step.name 前缀 + 警告 + selector/iframe 链 + (j) frame 列表 + 选项矩阵。
    /// 前端 #interactive-help 只显示 question 文本（app.js），故 question 是纯 string。
    /// </summary>
    internal static string BuildHelpQuestion(int totalSteps, string phaseName, string? stepName, string action, string? description,
        string? warning, string? selector = null, string[]? iframeChain = null,
        HelpOption options = HelpOption.Common5, IReadOnlyList<FrameInfo>? frames = null)
    {
        options |= HelpOption.Other;  // T5：(k) 其它始终含（兜底，回复可不带字母）
        var sb = new StringBuilder();
        sb.Append($"📍 步骤 {totalSteps} · phase「{phaseName}」· step「{stepName ?? action}」");
        if (!string.IsNullOrEmpty(action) || !string.IsNullOrEmpty(description))
        {
            var detail = $"{action} {description ?? ""}".TrimEnd();
            if (detail.Length > 0) sb.Append($"（{detail}）");
        }
        if (!string.IsNullOrEmpty(warning)) sb.Append($"\n\n⚠️ {warning}");
        if (!string.IsNullOrEmpty(selector)) sb.Append($"\n\n已生成 selector：{selector}");
        if (iframeChain is { Length: > 0 }) sb.Append($"\niframe 链：{string.Join(" > ", iframeChain)}");
        sb.Append("\n\n请选择处理方式：");
        if (options.HasFlag(HelpOption.Relocate))
            sb.Append("\n(a) 重新定位（描述元素特征，我用 ref 重新定位）");
        if (options.HasFlag(HelpOption.AnalyzeHtml))
            sb.Append("\n(b) AI 分析 HTML（粘贴元素/父级/兄弟 HTML，我生成更稳 selector）");
        if (options.HasFlag(HelpOption.ManualSelector))
            sb.Append("\n(c) 手写 selector（直接输入 XPath/CSS）");
        if (options.HasFlag(HelpOption.AiAction))
            sb.Append("\n(d) ai 节点（改用 ai action，录制期执行推进页面 + 回放由 AI 处理）");
        if (options.HasFlag(HelpOption.Skip))
            sb.Append("\n(e) 跳过此步骤");
        if (options.HasFlag(HelpOption.AcceptFragile))
            sb.Append("\n(f) 使用脆弱的（接受当前 selector + 自动 onError=ai-fallback-stop 兜底；下轮传 acceptFragile=true + 同 selector 落盘）");
        if (options.HasFlag(HelpOption.EvaluateJs))
            sb.Append("\n(g) 改用 evaluate+check(js)（落盘 2 evaluate 数行数 + 1 check(js) 比对）");
        if (options.HasFlag(HelpOption.AcceptAsIs))
            sb.Append("\n(h) 用当前 selector/iframe — 录制时找不到，回放时再验证（+onError=ai-fallback-stop 兜底；下轮传 acceptAsIs=true + 同值）");
        if (options.HasFlag(HelpOption.IframeRef))
            sb.Append("\n(i) 用 ref 指认 iframe — 传 iframeRef，我用 aria-ref 定位 iframe 元素提取链");
        if (options.HasFlag(HelpOption.FramePicker) && frames is { Count: > 0 })
        {
            sb.Append("\n(j) 指认目标 frame（输入编号）:");
            for (int i = 0; i < frames.Count; i++)
            {
                var fr = frames[i];
                var namePart = !string.IsNullOrEmpty(fr.Name) ? $"[name={fr.Name}]" : "[name=(无)]";
                var urlPart = !string.IsNullOrEmpty(fr.Url) ? $" url={fr.Url}" : "";
                var txtPart = !string.IsNullOrEmpty(fr.InnerTextSnippet) ? $" 含\"{fr.InnerTextSnippet}\"" : "";
                sb.Append($"\n     {i + 1}. {namePart}{urlPart}{txtPart}");
            }
        }
        if (options.HasFlag(HelpOption.UseExtractedHtml))
            sb.Append("\n(l) 用代码已提取的 HTML（代码已抓元素 outerHTML/iframe ctx 全量，据此手写稳定 selector；不想手粘 HTML 时选此项）");
        sb.Append("\n(k) 其它（自由描述，我分析后处理；回复也可不带字母）");
        return sb.ToString();
    }

    /// <summary>
    /// B2（2c，块2点3）：ai action 录制期执行——调 AiActionExecutor.ExecuteAsync 推进页面（否则录制断链，后续步骤依赖当前页面状态）。
    /// 用于求助选项 (d) ai 节点 / priority 8 + forceAiAction。临时 ExecutionContext（录制期 vars/scopeChain 独立，不污染回放期 ctx）。
    /// 录制期执行失败不阻断录制（aiStep 已落盘，回放期执行）；记日志让 AI 感知。scene=AiAction（非兜底场景，#3 D）。
    /// </summary>
    private async Task ExecuteAiActionForRecordingAsync(string instruction, string[]? iframeChain, Dictionary<string, object> args, CancellationToken ct)
    {
        if (_page == null) return;
        var maxTurns = GetInt(args, "maxAiTurns") ?? _engineOptions.MaxAiTurns;
        var totalTimeout = _engineOptions.DefaultAiTimeout;
        // Vars/ScopeChain 是 get-only（默认 new()），录制期 ai 执行用 ctx 自带空集合（独立，不污染回放期 ctx）
        var tempCtx = new SmartFilling.Engine.Engine.ExecutionContext
        {
            Page = _page,
            ActivePage = _page,
            Ct = ct,
            TaskId = _script.ScriptId,
            ScriptId = _script.ScriptId,
            PhaseName = _currentPhaseName,
        };
        try
        {
            await _aiActionExecutor.ExecuteAsync(_page, tempCtx, instruction, iframeChain, _script, maxTurns, totalTimeout, tempCtx.Vars, tempCtx.ScopeChain, AiScene.AiAction, ct);
        }
        catch (Exception ex) when (!IsBrowserCrashException(ex))
        {
            // 录制期 ai 执行失败不阻断录制（aiStep 已存，回放期执行）；记日志让 AI 下一轮感知
            await LogAsync($"录制提示: ai action 录制期执行未完成（{ex.Message}），已保存为 ai action，回放期将执行");
        }
    }

    private async Task<string> ExecuteDoneAsync(Dictionary<string, object> args)
    {
        var summary = args.GetValueOrDefault("summary")?.ToString();
        if (!string.IsNullOrEmpty(summary))
        {
            _script = _script with { Description = summary };
            await LogAsync(summary);
        }

        await LogAsync($"录制完成，共 {StepCount} 个步骤");
        _isDone = true;
        return "录制完成";
    }

    #endregion

    #region Phase 管理

    /// <summary>
    /// 处理 AI 传入的 phase 参数，首次新 phase 名时创建 PhaseNode
    /// 支持 parentPhase 嵌套
    /// </summary>
    internal async Task<(string? Error, List<string> Warnings)> EnsurePhaseAsync(Dictionary<string, object> args, CancellationToken ct)
    {
        var warnings = new List<string>();
        var phaseName = args.GetValueOrDefault("phase")?.ToString();

        // A4: AI 未传 phase 且 _phases 为空时，自动创建默认 phase，防止步骤丢失
        if (string.IsNullOrEmpty(phaseName))
        {
            if (_phases.Count == 0)
            {
                _phases.Add(new PhaseNode { Kind = "phase", Name = "main", Type = "sequential", Steps = [] });
                _currentPhaseName = "main";
            }
            return (null, warnings);
        }

        if (phaseName == _currentPhaseName) return (null, warnings);

        // F.4.2/R12：全局已存在同名 phase（但非当前）→ 这是业务歧义（两个同名 phase 可能是同一阶段续录，
        // 也可能是不同页面/iframe 的不同业务），代码无法判断 → 问用户（准则 3 结构化求助）。
        // 不自动合并：若属不同业务，合并会破坏执行顺序与上下文。用户选「新阶段」则让 AI 起不重名的名重试。
        if (FindPhase(_phases, phaseName) != null)
        {
            var reply = await ExecuteRequestHelpAsync(new()
            {
                ["question"] = "phase「" + phaseName + "」已存在。这属于哪种情况？请回复 a 或 b：\n" +
                               "(a) 继续往原 phase 录步骤（与之前是同一阶段）\n" +
                               "(b) 新的业务阶段（如另一页面/iframe 的不同操作）"
            }, ct);

            // 用户取消（录制被取消）→ 原样返回
            if (reply.Contains("取消")) return (reply, warnings);

            // 解析用户选择；默认偏向「新阶段」（不合并，安全），仅用户明确表示继续/复用才复用原 phase
            const string prefix = "用户回答:";
            var answerText = reply.StartsWith(prefix) ? reply[prefix.Length..].Trim() : reply;
            bool reuse = answerText.Equals("a", StringComparison.OrdinalIgnoreCase)
                || answerText.Equals("1")
                || answerText.Contains("继续") || answerText.Contains("复用") || answerText.Contains("原");
            if (reuse)
            {
                _currentPhaseName = phaseName;
                return (null, warnings);
            }
            // 用户选 (b) 新阶段 → 让 AI 用不重名的名重试（AI 根据当前页面/iframe 特征起名）
            return ("用户确认「" + phaseName + "」是新阶段（非原 phase 续录）。请用一个未被占用的 phase 名重新调用（可参考当前页面/iframe 特征起名，如「" + phaseName + "-2」或描述性名字）。", warnings);
        }

        var phaseType = args.GetValueOrDefault("phaseType")?.ToString() ?? "sequential";
        var loopSource = args.GetValueOrDefault("phaseLoopSource")?.ToString();
        // 修订4：loopSource 写裸字段名（不写 {{}}），且须是已注册的 type=array 字段（否则 GetLoopRows 静默返回空 → loop 0 迭代，明细行不填）
        if (!string.IsNullOrEmpty(loopSource))
        {
            if (loopSource.Contains("{{"))
                return ("❌ phaseLoopSource 不能含 {{}}，写裸字段名（如 items，而非 {{items}}）", warnings);
            if (!IsRegisteredArrayField(_script.Fields, loopSource))
                return ($"❌ phaseLoopSource '{loopSource}' 不是已注册的 type=array 字段（先注册该明细表字段，loopSource 写它的裸字段名）", warnings);
        }
        var parentPhase = args.GetValueOrDefault("parentPhase")?.ToString();

        // §3c phase.Condition/LoopCondition 经 ProcessDetectNodeAsync 反推。D-I（a-a-4，2026-07-06 拍板）：lenient=false
        //（Condition/LoopCondition 都是前置判断——Condition 执行前一次、LoopCondition while 风格循环前含首次，须引用"执行前已存在的稳定元素"，录制期同时机可校验）。
        // ⑥（a-a-4，2026-07-09）求助通道对齐 step（operate/save_step detect 求助）：完整消费 ProcessDetectNodeAsync 全 5 出参（Node/Error/Priority/CountZero/MultiFrames），
        // 复用 step 级同一套 helper（RequestMultiFrameHelpAsync/BuildHelpQuestion）+ 同一套集合（FragileType.DetectSelector + acceptAsIs/acceptFragile 总开关）。
        // 修待决策 #1（(h) 不可达：count-based count==0 返 pce=null 不进旧 if(pce!=null)）/ #2（priority silent：旧弃用 priority 出参 `_`）/ #3（系统性：旧只部分消费出参）。
        // 集合策略最终决策：acceptFragile/acceptAsIs 共用 step 总开关（不隔离 phase、不去总开关——软确认文字回复唯一通道靠总开关）。
        var acceptFragile = GetBool(args, "acceptFragile") == true;
        var acceptAsIs = GetBool(args, "acceptAsIs") == true;
        var phaseCondition = GetObject<DetectCondition>(args, "phaseCondition");
        var phaseLoopCondition = GetObject<DetectCondition>(args, "phaseLoopCondition");
        if (phaseCondition != null)
        {
            var (pc, pce, pcprio, pcz, pcmf, pcCzLeafSelector, pcIsFragileLayer, pcIsFragileLayerChain, pcFragileLeafSelector, pcIsFragileLayerCtx) = await ProcessDetectNodeAsync(phaseCondition, warnings, lenient: false, ct: ct);
            var pcSel = pcCzLeafSelector ?? pc?.Selector ?? phaseCondition.Selector;  // 待决策2 (b2)：优先用 count==0 叶子 selector（复合时非空）
            // ① multi frame picker（ND8 B）：phase.Condition 多 frame 命中 → (j) picker（对齐 step detect③，先定 frame 再论 count==0/脆弱）。求助在 _phases.Add 前重传 ensure_phase 时 FindPhase 找不到→重新创建，无双 phase。
            if (pcmf is { Count: >= 2 })
            {
                await LogAsync($"录制提示: phase.Condition 目标在 {pcmf.Count} 个 iframe 都出现，请用户指认");
                var mfReply = await RequestMultiFrameHelpAsync(0, phaseName, phaseName, "ensure_phase", $"condition: {phaseCondition.Type}",
                    $"phase.Condition 反推命中 {pcmf.Count} 个 frame，请用 (j) 编号指认评估该条件的目标 frame（或 (e) 跳过用默认第 1 个）", pcmf, ct);
                return ($"phase.Condition「{pcSel}」多 frame，已求助指认（{mfReply}），请据回复用指定 iframe 重新传 phaseCondition", warnings);
            }
            // ② count==0（count-based 类，不依赖 pce——修 #1 不可达）：phase.Condition selector 找不到 → 求助给 (h)（下轮 acceptAsIs 命中 ProcessDetectNodeAsync ③ 跳存在性）。
            // 待决策4·D-DC15（第67轮）：(h) gate per-selector（对齐 operate⑤ L538 / save_step L1318，复合多 count==0 叶子第二个 silent 修复，详见 operate⑤ 注释）。
            if (pcz && (!acceptAsIs || !IsAsIsAccepted(FragileType.DetectSelector, pcSel)) && !string.IsNullOrEmpty(pcSel))
            {
                var pcQ = BuildHelpQuestion(0, phaseName, phaseName, "ensure_phase", $"condition: {phaseCondition.Type}",
                    warning: $"phase.Condition selector 在当前页面找不到（count==0，可能未加载/动态生成/跨域/预期不可见/phase 执行后才出现）",
                    selector: pcSel, options: HelpOption.Common5 | HelpOption.AcceptAsIs);
                var pcReply = await ExecuteRequestHelpAsync(new() { ["question"] = pcQ }, ct);
                if (ReplyChoseOption(pcReply, 'h')) { _acceptedAsIs.Add((FragileType.DetectSelector, pcSel)); pcReply = AppendSelectorAsIsGuidance(pcReply, pcSel); }  // 块B：(h) reply 追加 selector 指引
                return ($"phase.Condition「{pcSel}」找不到（可能 phase 执行后才出现），已求助（{pcReply}），请据回复调整或下轮 acceptAsIs=true + 同 selector", warnings);
            }
            // ③ error（strict 类校验失败：多匹配/找不到）→ 阻断（strict 找不到回放必失败，不走 (h)；count-based count==0 已由 ② 处理，pce 此处恒 strict 源）。
            if (pce != null) return (pce, warnings);
            // C2 落点5（结论13）：phase.Condition detect③ iframe 链脆弱（pcIsFragileLayer）→ 求助给 (f)（复用 RequestIframeFragileHelpAsync + 填 DetectIframe 集合）。isFragileLayer 优先于 selector priority 8。
            if (pcIsFragileLayer && pcIsFragileLayerChain is { Length: > 0 }
                && (!acceptFragile || !IsFragileAccepted(FragileType.DetectIframe, SerializeChain(pcIsFragileLayerChain))))
            {
                var pcFragileIframeWarning = $"phase.Condition iframe 链含脆弱层「{string.Join(" > ", pcIsFragileLayerChain)}」（位置选择器层/GUID id/动态锚点，回放易失效），建议 request_help 提供 iframe 的 id/src/name 或更稳 selector 链";
                var (pcFragileIframeReply, _) = await RequestIframeFragileHelpAsync(0, phaseName, phaseName, "ensure_phase", $"condition: {phaseCondition.Type}",
                    pcFragileIframeWarning, pcIsFragileLayerChain, pcSel, FragileType.DetectIframe, ct, pcIsFragileLayerCtx);
                return ($"phase.Condition iframe 链脆弱，已求助（{pcFragileIframeReply}），请据回复调整或下轮 acceptFragile=true + 同 selector/iframe", warnings);
            }
            // ④ priority>=8（detect 脆弱，消费 priority 出参——修 #2 silent）：phase.Condition selector 脆弱 → 求助给 (f)（下轮 acceptFragile 命中 ProcessDetectNodeAsync ③ 跳 AssessFragility）。
            if (pcprio >= 8 && !string.IsNullOrEmpty(pcSel) && (!acceptFragile || !IsFragileAccepted(FragileType.DetectSelector, pcSel)))
            {
                await LogAsync("录制提示: phase.Condition selector 较脆弱，建议提供更稳的 selector 或用 ref");
                var pcHtml = await ExtractHtmlForHelpAsync(pcSel, pc?.Iframe);  // R5：抓 phase.Condition selector outerHTML（pc?.Iframe 反推链；抓不到->不显示 l）
                var (pcFragileReply, _) = await RequestSelectorFragileHelpAsync(0, phaseName, phaseName, "ensure_phase", $"condition: {phaseCondition.Type}",
                    $"phase.Condition selector 较脆弱（priority={pcprio}，纯路径/完整路径，DOM 微调即失效）", pcSel, pc?.Iframe, FragileType.DetectSelector, ct, pcHtml);  // 块B：收拢进 helper
                return ($"phase.Condition「{pcSel}」较脆弱，已求助（{pcFragileReply}），请据回复调整或下轮 acceptFragile=true + 同 selector", warnings);
            }
            phaseCondition = pc;
        }
        if (phaseLoopCondition != null)
        {
            var (plc, ple, plcprio, plcz, plcmf, plcCzLeafSelector, plcIsFragileLayer, plcIsFragileLayerChain, plcFragileLeafSelector, plcIsFragileLayerCtx) = await ProcessDetectNodeAsync(phaseLoopCondition, warnings, lenient: false, ct: ct);
            var plcSel = plcCzLeafSelector ?? plc?.Selector ?? phaseLoopCondition.Selector;  // 待决策2 (b2)
            // ① multi frame picker（同 phase.Condition）。
            if (plcmf is { Count: >= 2 })
            {
                await LogAsync($"录制提示: phase.LoopCondition 目标在 {plcmf.Count} 个 iframe 都出现，请用户指认");
                var lmfReply = await RequestMultiFrameHelpAsync(0, phaseName, phaseName, "ensure_phase", $"loopCondition: {phaseLoopCondition.Type}",
                    $"phase.LoopCondition 反推命中 {plcmf.Count} 个 frame，请用 (j) 编号指认评估该条件的目标 frame（或 (e) 跳过用默认第 1 个）", plcmf, ct);
                return ($"phase.LoopCondition「{plcSel}」多 frame，已求助指认（{lmfReply}），请据回复用指定 iframe 重新传 phaseLoopCondition", warnings);
            }
            // ② count==0（修 #1）。
            // 待决策4·D-DC15（第67轮）：(h) gate per-selector（对齐 phase.Condition L2300 / operate⑤ / save_step，复合多 count==0 叶子第二个 silent 修复）。phase.Condition 已修，loopCondition 同款补。
            if (plcz && (!acceptAsIs || !IsAsIsAccepted(FragileType.DetectSelector, plcSel)) && !string.IsNullOrEmpty(plcSel))
            {
                var plcQ = BuildHelpQuestion(0, phaseName, phaseName, "ensure_phase", $"loopCondition: {phaseLoopCondition.Type}",
                    warning: $"phase.LoopCondition selector 在当前页面找不到（count==0，while 前置判断，循环开始前须存在）",
                    selector: plcSel, options: HelpOption.Common5 | HelpOption.AcceptAsIs);
                var plcReply = await ExecuteRequestHelpAsync(new() { ["question"] = plcQ }, ct);
                if (ReplyChoseOption(plcReply, 'h')) { _acceptedAsIs.Add((FragileType.DetectSelector, plcSel)); plcReply = AppendSelectorAsIsGuidance(plcReply, plcSel); }  // 块B：(h) reply 追加 selector 指引
                return ($"phase.LoopCondition「{plcSel}」找不到（while 前置判断，循环开始前须存在），已求助（{plcReply}），请据回复调整或下轮 acceptAsIs=true + 同 selector", warnings);
            }
            // ③ error（strict 校验失败）→ 阻断。
            if (ple != null) return (ple, warnings);
            // C2 落点5（结论13）：phase.LoopCondition detect③ iframe 链脆弱（plcIsFragileLayer）→ 求助给 (f)（复用 RequestIframeFragileHelpAsync + 填 DetectIframe 集合）。
            if (plcIsFragileLayer && plcIsFragileLayerChain is { Length: > 0 }
                && (!acceptFragile || !IsFragileAccepted(FragileType.DetectIframe, SerializeChain(plcIsFragileLayerChain))))
            {
                var plcFragileIframeWarning = $"phase.LoopCondition iframe 链含脆弱层「{string.Join(" > ", plcIsFragileLayerChain)}」（位置选择器层/GUID id/动态锚点，回放易失效），建议 request_help 提供 iframe 的 id/src/name 或更稳 selector 链";
                var (plcFragileIframeReply, _) = await RequestIframeFragileHelpAsync(0, phaseName, phaseName, "ensure_phase", $"loopCondition: {phaseLoopCondition.Type}",
                    plcFragileIframeWarning, plcIsFragileLayerChain, plcSel, FragileType.DetectIframe, ct, plcIsFragileLayerCtx);
                return ($"phase.LoopCondition iframe 链脆弱，已求助（{plcFragileIframeReply}），请据回复调整或下轮 acceptFragile=true + 同 selector/iframe", warnings);
            }
            // ④ priority>=8（修 #2 silent）。
            if (plcprio >= 8 && !string.IsNullOrEmpty(plcSel) && (!acceptFragile || !IsFragileAccepted(FragileType.DetectSelector, plcSel)))
            {
                await LogAsync("录制提示: phase.LoopCondition selector 较脆弱，建议提供更稳的 selector 或用 ref");
                var plcHtml = await ExtractHtmlForHelpAsync(plcSel, plc?.Iframe);  // R5：抓 phase.LoopCondition selector outerHTML（plc?.Iframe 反推链；抓不到->不显示 l）
                var (plcFragileReply, _) = await RequestSelectorFragileHelpAsync(0, phaseName, phaseName, "ensure_phase", $"loopCondition: {phaseLoopCondition.Type}",
                    $"phase.LoopCondition selector 较脆弱（priority={plcprio}，纯路径/完整路径，DOM 微调即失效）", plcSel, plc?.Iframe, FragileType.DetectSelector, ct, plcHtml);  // 块B：收拢进 helper
                return ($"phase.LoopCondition「{plcSel}」较脆弱，已求助（{plcFragileReply}），请据回复调整或下轮 acceptFragile=true + 同 selector", warnings);
            }
            phaseLoopCondition = plc;
        }

        var newPhase = new PhaseNode
        {
            Kind = "phase",
            Name = phaseName,
            Type = phaseType,
            LoopSource = loopSource,
            // S4 N.3.2：补读 7 个 phase 配置字段（仅首次创建时；切回同名 phase 不再读）；Condition/LoopCondition 已上方反推
            Condition = phaseCondition,
            LoopCondition = phaseLoopCondition,
            OnError = args.GetValueOrDefault("phaseOnError")?.ToString(),
            MaxLoopCount = GetInt(args, "phaseMaxLoopCount"),
            RowIndexOffset = GetInt(args, "phaseRowIndexOffset"),
            MaxAiTurns = GetInt(args, "phaseMaxAiTurns"),
            Timeout = GetInt(args, "phaseTimeout"),
            AiGoal = args.GetValueOrDefault("phaseAiGoal")?.ToString(),  // R3-2：phase 业务目标（所有 phase 必填，首次创建时读 AI 传入的 phaseAiGoal）
            Steps = []
        };

        if (!string.IsNullOrEmpty(parentPhase))
        {
            var parent = FindPhase(_phases, parentPhase);
            if (parent != null)
                parent.Steps!.Add(newPhase);
            else
                _phases.Add(newPhase); // 找不到父 phase，降级为顶层
        }
        else
        {
            _phases.Add(newPhase);
        }

        _currentPhaseName = phaseName;

        // Phase 创建推送（日志推送 22d）
        await LogAsync($"Phase: {phaseName} ({phaseType})" +
            (!string.IsNullOrEmpty(parentPhase) ? $" [嵌套在 {parentPhase} 内]" : ""));
        return (null, warnings);
    }

    /// <summary>递归查找 phase</summary>
    private static PhaseNode? FindPhase(List<PhaseNode> phases, string name)
    {
        foreach (var p in phases)
        {
            if (p.Name == name) return p;
            if (p.Steps != null)
            {
                foreach (var child in p.Steps.OfType<PhaseNode>())
                {
                    var found = FindPhase([child], name);
                    if (found != null) return found;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// 从顶层遍历 phase 树，返回根到当前 phase 的 loopSource 路径。
    /// 例如 fill_attachments → ["items", "attachments"]
    /// </summary>
    private List<string> GetLoopFieldPath()
    {
        var path = new List<string>();
        WalkForPath(_phases, _currentPhaseName, path);
        return path;
    }

    private bool WalkForPath(List<PhaseNode> phases, string target, List<string> path)
    {
        foreach (var p in phases)
        {
            // 进入此 phase 前记录 loopSource
            if (!string.IsNullOrEmpty(p.LoopSource))
                path.Add(p.LoopSource);

            if (p.Name == target)
                return true; // 找到目标

            // 递归搜索子 phase
            if (p.Steps != null)
            {
                if (WalkForPath(p.Steps.OfType<PhaseNode>().ToList(), target, path))
                    return true;
            }

            // 此子树未找到，回溯移除 loopSource
            if (!string.IsNullOrEmpty(p.LoopSource))
                path.RemoveAt(path.Count - 1);
        }
        return false;
    }

    /// <summary>
    /// 根据 loopSource 路径，导航到目标字段集合（自动创建缺失的父字段）。
    /// 返回最终应注册子字段的 List&lt;FieldDefinition&gt;。
    /// </summary>
    private List<FieldDefinition> EnsureFieldPath(List<string> path)
    {
        var targetFields = _script.Fields;

        foreach (var loopSource in path)
        {
            var parent = targetFields.FirstOrDefault(f => f.Name == loopSource);
            if (parent == null)
            {
                // 自动创建父字段：type=array, uiComponent=table
                parent = new FieldDefinition
                {
                    Name = loopSource,
                    Label = loopSource, // 后续由 AI 通过 fieldFields 或 fieldLabel 覆盖
                    Type = "array",
                    UiComponent = "table",
                    Fields = []
                };
                targetFields.Add(parent);
            }
            else if (parent.Fields == null)
            {
                // 父字段存在但 Fields 未初始化（AI 先通过 fieldFields 创建的或其他途径）
                var idx = targetFields.IndexOf(parent);
                parent = parent with { Fields = [] };
                targetFields[idx] = parent;
            }
            targetFields = parent.Fields!;
        }

        return targetFields;
    }

    /// <summary>递归搜索所有层级的字段（含嵌套），检查 name 是否已存在</summary>
    private static bool FieldExists(List<FieldDefinition> fields, string name)
    {
        foreach (var f in fields)
        {
            if (f.Name == name) return true;
            if (f.Fields != null && FieldExists(f.Fields, name)) return true;
        }
        return false;
    }

    /// <summary>修订4：递归查找已注册的 type=array 字段（loopSource 须指向明细表字段）。</summary>
    private static bool IsRegisteredArrayField(List<FieldDefinition> fields, string name)
    {
        foreach (var f in fields)
        {
            if (f.Name == name && f.Type == "array") return true;
            if (f.Fields is { Count: > 0 } && IsRegisteredArrayField(f.Fields, name)) return true;
        }
        return false;
    }

    /// <summary>
    /// ④-8 录制层：检查 storeAs 名是否与字段名/系统保留字冲突，返回错误提示（让 AI 换名重试）。
    /// storeAs-vs-storeAs 同名允许（设计决策④：不去重，允许故意覆盖），故不查；校验层 ScriptLoader.ValidateNameCollisions 是落盘兜底底线；字段名撞保留字无协商直接 fail-fast。
    /// </summary>
    private string? CheckStoreAsCollision(object? storeAs)
    {
        var names = SmartFilling.Engine.Engine.ScriptLoader.ExtractStepStoreAsNames(new StepNode { StoreAs = storeAs });
        var fieldNames = _script.Fields.SelectMany(CollectFieldNamesRecursive).ToHashSet();
        foreach (var n in names)
        {
            if (ScriptLoader.SystemCredentialKeys.Contains(n))
                return $"❌ storeAs 变量名 '{n}' 是系统凭据字段名（会被 fillData 的 username/password 系统凭据永久遮蔽，静默取错值），请换名";
            if (ScriptLoader.ReservedVarNames.Contains(n))
                return $"❌ storeAs 变量名 '{n}' 与系统保留变量冲突（{string.Join("/", ScriptLoader.ReservedVarNames)}），请换名";
            if (fieldNames.Contains(n))
                return $"❌ storeAs 变量名 '{n}' 与字段名同名——运行时 fillData 会永久遮蔽该 storeAs，静默取错值。请换名（或字段名）";
        }
        return null;
    }

    private static IEnumerable<string> CollectFieldNamesRecursive(FieldDefinition f)
    {
        yield return f.Name;
        if (f.Fields is { Count: > 0 })
            foreach (var sub in f.Fields.SelectMany(CollectFieldNamesRecursive))
                yield return sub;
    }

    /// <summary>递归查找字段定义（F5 字段重名问用户时取已有字段元数据：label/type 等）；找不到返回 null</summary>
    private static FieldDefinition? FindField(List<FieldDefinition> fields, string name)
    {
        foreach (var f in fields)
        {
            if (f.Name == name) return f;
            if (f.Fields != null)
            {
                var nested = FindField(f.Fields, name);
                if (nested != null) return nested;
            }
        }
        return null;
    }

    /// <summary>递归查找并替换同名字段定义（F5 覆盖分支 c）；返回是否替换成功。父记录的 Fields 列表是可变引用，原地替换无需重建父记录</summary>
    private static bool ReplaceField(List<FieldDefinition> fields, string name, FieldDefinition newDef)
    {
        var idx = fields.FindIndex(f => f.Name == name);
        if (idx >= 0)
        {
            fields[idx] = newDef;
            return true;
        }
        foreach (var f in fields)
        {
            if (f.Fields != null && ReplaceField(f.Fields, name, newDef))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 构建录制进度状态 JSON（phase 树形结构 + 字段 + 计数）
    /// </summary>
    private string BuildPhaseStatus()
    {
        var phaseTree = JsonSerializer.Deserialize<JsonElement>(SerializePhaseTree(_phases));
        var fields = _script.Fields?.Select(f => f.Name).ToList() ?? [];
        var status = new
        {
            currentPhase = _currentPhaseName,
            phaseTree,
            totalSteps = _totalSteps,
            registeredFields = fields
        };
        return JsonSerializer.Serialize(status);
    }

    /// <summary>递归序列化 phase 树</summary>
    private static string SerializePhaseTree(List<PhaseNode> phases)
    {
        var items = phases.Select(p =>
        {
            var children = p.Steps?.OfType<PhaseNode>().ToList() ?? [];
            var obj = new Dictionary<string, object>
            {
                ["name"] = p.Name ?? "",
                ["type"] = p.Type ?? "sequential",
                ["steps"] = p.Steps?.Count ?? 0
            };
            if (children.Count > 0)
                obj["children"] = JsonSerializer.Deserialize<JsonElement>(SerializePhaseTree(children));
            return obj;
        });
        return JsonSerializer.Serialize(items);
    }

    #endregion

    #region 录制完成后 AI 总结

    private const string SummarizeSystemPrompt = """
        你是一个脚本分析助手。根据任务描述和脚本内容，为每个 phase 生成简洁的中文业务目标描述。

        ## 脚本结构
        脚本由 phases 数组组成，每个 phase 包含：
        - name：phase 英文名称
        - type：sequential（顺序执行）/ loop（循环执行）/ ai（AI自主执行）
        - aiGoal：业务目标描述（需要你生成）
        - steps：步骤列表，每个步骤包含 action、description、field、value 等
        phase 可以嵌套：loop phase 的 steps 中可以包含子 phase。

        ## AiGoal 的用途
        AiGoal 在脚本执行时用于：
        - AI fallback 的任务描述（步骤失败时 AI 接管，需要知道这个 phase 要完成什么）
        - ai phase 的任务指令
        - 执行日志

        ## 生成规则
        - 基于任务描述理解业务上下文
        - 结合步骤的 action、description、field、value 理解实际操作
        - 生成一句简洁的中文业务目标，如"填写合同头部基本信息"、"添加合同明细行"
        - loop phase 应体现循环语义，如"逐行填写合同明细"
        - 嵌套子 phase 的 aiGoal 应体现其在父 phase 中的角色
        - 如果 phase 已有 aiGoal 且合理，保留原值
        - **AiGoal 措辞贴近任务描述**：保留任务描述的关键要素（对象/动作/约束），用接近用户原话的措辞，聚焦本 phase 职责同时呼应整体任务。执行时整体意图靠 AiGoal 质量 + 目标链传递，不单独把原始任务描述塞给 AI
          - ❌ 差：「处理报销」（过于笼统，丢失对象与目的）
          - ✅ 好：「填写差旅报销单的行程信息（为提交审批准备）」
        - **脚本 description 同样贴近任务描述**：用一句话概括脚本整体用途，保留任务描述的核心目标

        ## returnData 生成（可选，改动5）
        - 仅当任务描述明确要求返回数据（如"提取单号""返回申请编号""保存后返回单号"）时才生成 returnData
        - returnData 是脚本顶层的数据返回映射：key=对外返回字段名（如 billCode），value 引用步骤中 storeAs 存储的变量（格式 "{{变量名}}"）
        - 先看各 phase steps 的 storeAs 字段，把要返回的数据映射到对应 storeAs 变量
        - 任务描述没提返回数据 → 不输出 returnData 字段（省略）
        - 示例（#45 决策10 多键）：任务"提交并返回单号和金额"，步骤有 extract storeAs=billNo + extract storeAs=totalAmount → "returnData": { "billCode": "{{billNo}}", "amount": "{{totalAmount}}" }
        - 任务描述要求返回多个数据时，各自起 key 名（贴近业务语义），分别引用对应 storeAs 变量；单键也支持（如 { "billCode": "{{billNo}}" }）

        ## 输出格式
        返回以下 JSON 格式（不要包含其他文字）：
        ```json
        {
          "description": "脚本整体用途描述（一句话）",
          "phases": [
            { "name": "phase英文名", "aiGoal": "中文业务目标描述" },
            ...
          ],
          "returnData": { "返回字段名": "{{storeAs变量名}}" }
        }
        ```
        对于嵌套子 phase，在 phases 数组中保持平铺，通过 name 对应即可。
        returnData 可选：仅当任务描述明确要求返回数据时输出（见上方 returnData 生成规则），否则省略。
        """;

    /// <summary>
    /// 录制完成后单独 AI 调用，总结每个 phase 的 AiGoal 和 Script Description
    /// </summary>
    async Task<ScriptV2> SummarizePhaseGoalsAsync(ScriptV2 script, string taskDescription, CancellationToken ct)
    {
        if (_aiProvider == null || script.Phases.Count == 0) return script;

        try
        {
            // 构建输入：任务描述 + 字段定义 + phase 结构 + step 详情
            var input = new
            {
                taskDescription,
                fields = script.Fields?.Select(f => new { f.Name, f.Label, f.Type }),
                phases = script.Phases.Select(p => SummarizePhaseForGoal(p)).Where(x => x != null)
            };

            var prompt = JsonSerializer.Serialize(input);
            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(SummarizeSystemPrompt),
                new UserChatMessage(prompt)
            };

            var response = await _aiProvider.SendMessageAsync(messages, [], ct);
            var json = response.Text?.Trim();
            if (!string.IsNullOrEmpty(json))
            {
                // 提取 JSON 内容（AI 可能在 markdown 代码块中返回）
                var start = json.IndexOf('{');
                var end = json.LastIndexOf('}');
                if (start >= 0 && end > start)
                {
                    var result = JsonSerializer.Deserialize<JsonElement>(json[start..(end + 1)]);
                    // 更新 script.Description
                    if (result.TryGetProperty("description", out var desc) && desc.GetString() is { } d)
                        script = script with { Description = d };
                    // 更新每个 phase 的 AiGoal（递归处理嵌套）
                    if (result.TryGetProperty("phases", out var phasesArr))
                    {
                        foreach (var p in phasesArr.EnumerateArray())
                        {
                            var name = p.GetProperty("name").GetString();
                            var goal = p.GetProperty("aiGoal").GetString();
                            if (name != null && goal != null)
                                UpdatePhaseAiGoal(script.Phases, name, goal);
                        }
                    }
                    // 改动5：更新 script.ReturnData（AI 据任务描述 + storeAs 变量生成数据返回映射；任务描述没提返回则省略）
                    if (result.TryGetProperty("returnData", out var rd) && rd.ValueKind == JsonValueKind.Object)
                    {
                        var dict = new Dictionary<string, object>();
                        foreach (var prop in rd.EnumerateObject())
                            dict[prop.Name] = prop.Value.ValueKind == JsonValueKind.String ? (object)(prop.Value.GetString() ?? "") : prop.Value.GetRawText();
                        if (dict.Count > 0)
                            script = script with { ReturnData = dict };
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // AI 总结失败不影响录制结果
            await LogAsync($"AI 总结阶段目标失败（不影响录制结果）: {ex.Message}");
        }
        return script;
    }

    static object? SummarizePhaseForGoal(PhaseItem item)
    {
        if (item is PhaseNode phase)
            return new
            {
                phase.Name, phase.Type, phase.AiGoal,
                Steps = phase.Steps?.Select(s => s is StepNode st
                    ? (object)new { st.Action, st.Description, Field = st.Field, Value = st.Value, StoreAs = st.StoreAs }
                    : SummarizePhaseForGoal(s))
            };
        return null;
    }

    /// <summary>
    /// 递归更新 phase（含嵌套子 phase）的 AiGoal
    /// </summary>
    static void UpdatePhaseAiGoal(List<PhaseItem> items, string phaseName, string aiGoal)
    {
        foreach (var item in items)
        {
            if (item is PhaseNode phase)
            {
                if (phase.Name == phaseName)
                {
                    phase.AiGoal = aiGoal;
                    return;
                }
                if (phase.Steps != null)
                    UpdatePhaseAiGoal(phase.Steps, phaseName, aiGoal);
            }
        }
    }

    #endregion

    private async Task<byte[]> CompressScreenshotAsync(IPage page)
    {
        var quality = _engineOptions.Screenshot?.CompressQuality ?? 70;
        if (quality >= 100)
            return await page.ScreenshotAsync();
        return await page.ScreenshotAsync(new() { Type = ScreenshotType.Jpeg, Quality = quality });
    }
}
