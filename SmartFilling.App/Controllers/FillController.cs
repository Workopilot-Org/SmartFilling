using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using SmartFilling.App.Models;
using SmartFilling.App.Prompts;
using SmartFilling.App.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using SmartFilling.App.Configuration;
using SmartFilling.App.Hubs;
using SmartFilling.Engine.Models;

namespace SmartFilling.App.Controllers;

[ApiController]
[Route("api/[controller]")]
public class FillController : ControllerBase
{
    private readonly ScriptService _scriptService;
    private readonly WorkerApiClient _workerClient;
    private readonly AttachmentClassificationService _classificationService;
    private readonly AttachmentService _attachmentService;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<FillController> _logger;
    private readonly IHubContext<AgentHub> _hub;                  // C7：接收 Worker push 回调后推 AgentHub
    private readonly ActiveTaskRegistry _activeTaskRegistry;      // P2：活跃任务登记/移除（健康检测用）
    private readonly WorkerOptions _workerOptions;                // HealthCheck.Enabled 判断（模式 C）

    private static readonly ConcurrentDictionary<string, List<ChatMessage>> _chatSessions = new();
    private static readonly ConcurrentDictionary<string, DateTime> _sessionLastAccess = new();
    private static readonly Timer _cleanupTimer = new(_ =>
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-_expirationMinutes);
        foreach (var kv in _sessionLastAccess)
        {
            if (kv.Value < cutoff)
            {
                _chatSessions.TryRemove(kv.Key, out var _);
                _sessionLastAccess.TryRemove(kv.Key, out var _);
            }
        }
    }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    private static int _expirationMinutes = 60;
    private static bool _cleanupInitialized;

    private void EnsureCleanupTimer()
    {
        if (_cleanupInitialized) return;
        _cleanupInitialized = true;
        _expirationMinutes = _config.GetValue<int>("AppOptions:ChatSessionExpirationMinutes", 60);
        _cleanupTimer.Change(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));
    }

    public FillController(
        ScriptService scriptService,
        WorkerApiClient workerClient,
        AttachmentClassificationService classificationService,
        AttachmentService attachmentService,
        IConfiguration config,
        IWebHostEnvironment env,
        ILogger<FillController> logger,
        IHubContext<AgentHub> hub,
        ActiveTaskRegistry activeTaskRegistry,
        IOptions<WorkerOptions> workerOptions)
    {
        _scriptService = scriptService;
        _workerClient = workerClient;
        _classificationService = classificationService;
        _attachmentService = attachmentService;
        _config = config;
        _env = env;
        _logger = logger;
        _hub = hub;
        _activeTaskRegistry = activeTaskRegistry;
        _workerOptions = workerOptions.Value;
        EnsureCleanupTimer();
    }

    [HttpGet("doctypes")]
    public ActionResult<List<DocumentType>> GetDocumentTypes() => Ok(_scriptService.GetAllDocuments());

    [HttpGet("scripts/{documentTypeId}")]
    public ActionResult<List<ScriptV2>> GetScripts(string documentTypeId)
        => Ok(_scriptService.GetScriptsByDocument(documentTypeId));

    [HttpPost("chat")]
    public async Task<ActionResult<ChatResponse>> Chat([FromBody] ChatRequest request)
    {
        var sessionId = request.SessionId ?? Guid.NewGuid().ToString("N")[..8];

        // 1. 获取或创建会话
        if (!_chatSessions.ContainsKey(sessionId))
        {
            _chatSessions[sessionId] = [];
            _sessionLastAccess[sessionId] = DateTime.UtcNow;

            // v2：从 ScriptV2.fields[] 获取字段定义
            var fieldsDefinition = "无特定字段定义，请根据通用常识收集必要信息。";
            var scriptName = "未知单据";
            var dataProcessingPromptsList = new List<string>();
            var initialVarsDefinition = "无";  // 组5 (5.1)：脚本 initialVars 声明的变量名清单，供 AI 收集参考

            if (!string.IsNullOrEmpty(request.DocumentTypeId))
            {
                var docType = request.DocumentTypeId;
                var scripts = _scriptService.GetScriptsByDocument(docType);

                // 脚本加载失败（坏脚本/未关联）→ 返回 400 让前端可见，不再静默降级成"未知单据"
                if (scripts.Count == 0)
                {
                    _chatSessions.TryRemove(sessionId, out _);
                    _sessionLastAccess.TryRemove(sessionId, out _);
                    return BadRequest(new { error = "未找到关联的脚本，请重新录制" });
                }

                // 合并所有脚本字段，按 source 白名单过滤（仅空或 user 交 AI 收集，排除 system 凭据 + computed 计算值）
                var allFields = scripts
                    .Where(s => s.Fields != null)
                    .SelectMany(s => s.Fields!)
                    .Where(f => string.IsNullOrEmpty(f.Source) || "user".Equals(f.Source, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                // 检测字段名重复（仅判断 Name 是否重复，不比较定义内容；用户决策 3）
                var duplicateNames = allFields
                    .GroupBy(f => f.Name)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key)
                    .ToList();

                if (duplicateNames.Count > 0 && string.IsNullOrEmpty(request.MergeStrategy))
                {
                    // 首次请求发现字段名重复 → 不创建会话，返回冲突信息让用户选「后项覆盖前项」或「取消」
                    _chatSessions.TryRemove(sessionId, out _);
                    _sessionLastAccess.TryRemove(sessionId, out _);
                    return Ok(new { conflict = true, conflictedFields = duplicateNames });
                }

                if (allFields.Count == 0)
                {
                    // 全部字段 source=system（如登录凭据），由前端输入框（HTTP）/WS 任务提供，无需 chat 收集 → 直接开始填报
                    _chatSessions.TryRemove(sessionId, out _);
                    _sessionLastAccess.TryRemove(sessionId, out _);
                    return Ok(new ChatResponse { SessionId = sessionId, IsComplete = true, Reply = "本单据数据均由系统提供（登录凭据等），无需额外信息，可直接开始填报。", CollectedData = new Dictionary<string, object>() });
                }

                // 按合并策略去重：last-wins=末定义覆盖前定义（Reverse 后 DistinctBy 保留最后一个出现的定义）；默认 first-wins
                IEnumerable<FieldDefinition> mergedFields = "last-wins".Equals(request.MergeStrategy, StringComparison.OrdinalIgnoreCase)
                    ? allFields.Reverse<FieldDefinition>().DistinctBy(f => f.Name)
                    : allFields.DistinctBy(f => f.Name);
                var fieldsList = mergedFields.ToList();

                fieldsDefinition = JsonSerializer.Serialize(fieldsList, new JsonSerializerOptions { WriteIndented = true });

                // 组5 (5.1)：收集所有脚本 initialVars 声明的变量名清单（不作差集，收集触发由数据收集提示词三档决定）。
                var allInitialVars = new HashSet<string>();
                foreach (var s in scripts)
                    if (s.InitialVars != null)
                        foreach (var v in s.InitialVars)
                            allInitialVars.Add(v.Name);
                initialVarsDefinition = allInitialVars.Count > 0
                    ? JsonSerializer.Serialize(allInitialVars.Select(n => new { name = n }), new JsonSerializerOptions { WriteIndented = true })
                    : "无";

                // scriptName 用文档名称（多脚本合并后取文档名而非单个脚本名）
                var document = _scriptService.GetDocument(docType);
                if (document != null && !string.IsNullOrEmpty(document.Name))
                    scriptName = document.Name;

                // v2：使用 DocumentType.DataProcessingPrompts
                if (document != null && !string.IsNullOrEmpty(document.DataProcessingPrompts))
                    dataProcessingPromptsList.Add(document.DataProcessingPrompts);
            }

            if (!string.IsNullOrEmpty(request.DataCollectionPrompt))
                dataProcessingPromptsList.Add(request.DataCollectionPrompt);

            var dataProcessingPrompts = dataProcessingPromptsList.Count > 0
                ? string.Join("\n", dataProcessingPromptsList)
                : "无特殊数据处理要求";

            var systemPrompt = ChatCollectorPrompt.ChatCollectorForFill
                .Replace("{current_time}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                .Replace("{fields_definition}", fieldsDefinition)
                .Replace("{data_processing_prompts}", dataProcessingPrompts)
                .Replace("{initialVars_definition}", initialVarsDefinition)  // 组5 (5.1)：传 initialVars 声明供 AI 参考
                .Replace("{script_name}", scriptName);

            _chatSessions[sessionId].Add(new SystemChatMessage(systemPrompt));
        }

        var history = _chatSessions[sessionId];
        _sessionLastAccess[sessionId] = DateTime.UtcNow;
        var userMessage = request.Message;

        // 附件随消息发送：带附件即注入附件信息（下方工具注册分支同步触发）；不再依赖已废弃的 RequestType=attachment_notification
        if (request.Attachments is { Count: > 0 })
        {
            userMessage = BuildAttachmentUserMessage(userMessage, request.Attachments);
        }

        if (!string.IsNullOrEmpty(userMessage))
            history.Add(new UserChatMessage(userMessage));

        // 2. 调用 AI
        try
        {
            var apiKey = _config["AiProvider:ApiKey"];
            if (string.IsNullOrEmpty(apiKey))
                return StatusCode(500, new { error = "未配置 AiProvider:ApiKey" });

            var modelId = _config["AiProvider:ModelId"] ?? "deepseek-v3.2";
            var endpoint = _config["AiProvider:Endpoint"] ?? "https://dashscope.aliyuncs.com/compatible-mode/v1";

            var client = new ChatClient(modelId, new ApiKeyCredential(apiKey),
                new OpenAIClientOptions { Endpoint = new Uri(endpoint) });

            // 转换为 OpenAI 消息
            var messages = history.Select<ChatMessage, OpenAI.Chat.ChatMessage>(m => m).ToList();

            // 带附件即注册 3 个分类工具（决策：可解析——AI 既见附件，亦可按需调外部 Platform:NetApiUrl 取数填字段）
            var options = new ChatCompletionOptions();
            if (request.Attachments is { Count: > 0 })
            {
                options.Tools.Add(ChatTool.CreateFunctionTool("classify_attachment",
                    "对附件进行分类识别，不提取数据。仅当数据收集说明、数据收集提示词或用户消息明确要求对附件分类时才调用；若仅要求把附件放入 file 字段或未要求分类/提取，不要调用本工具。",
                    BinaryData.FromString("""{"type":"object","properties":{"attachment_path":{"type":"string","description":"附件 URL（取自附件清单的 URL 字段）"},"category_codes":{"type":"array","items":{"type":"string"},"description":"可选：指定分类代码集合缩小范围"},"category_ids":{"type":"array","items":{"type":"integer"},"description":"可选：指定分类ID集合缩小范围"}},"required":["attachment_path"]}""")));
                options.Tools.Add(ChatTool.CreateFunctionTool("extract_attachment_data",
                    "对已分类的附件提取结构化数据。仅当数据收集说明、数据收集提示词或用户消息明确要求提取附件数据时才调用；否则不要调用。",
                    BinaryData.FromString("""{"type":"object","properties":{"attachment_path":{"type":"string","description":"附件 URL（取自附件清单的 URL 字段）"},"category_code":{"type":"string","description":"分类代码"},"extract_mode":{"type":"string","enum":["PAGE","DOCUMENT"],"description":"取数模式，默认DOCUMENT"}},"required":["attachment_path","category_code"]}""")));
                options.Tools.Add(ChatTool.CreateFunctionTool("classify_and_extract_attachment",
                    "对附件进行分类并提取数据，一步到位。仅当数据收集说明、数据收集提示词或用户消息明确要求同时分类并提取附件数据时才调用；否则不要调用。",
                    BinaryData.FromString("""{"type":"object","properties":{"attachment_path":{"type":"string","description":"附件 URL（取自附件清单的 URL 字段）"},"category_codes":{"type":"array","items":{"type":"string"},"description":"可选：指定分类代码集合缩小范围"},"category_ids":{"type":"array","items":{"type":"integer"},"description":"可选：指定分类ID集合缩小范围"}},"required":["attachment_path"]}""")));
            }

            var completion = await client.CompleteChatAsync(messages, options);

            // 处理工具调用（支持多轮）
            while (completion.Value.ToolCalls?.Any() == true)
            {
                var toolResults = new List<ToolChatMessage>();
                messages.Add(new AssistantChatMessage(completion.Value));

                foreach (var toolCall in completion.Value.ToolCalls)
                {
                    var argsStr = toolCall.FunctionArguments?.ToString();
                    if (string.IsNullOrEmpty(argsStr))
                    {
                        toolResults.Add(new ToolChatMessage(toolCall.Id, "{\"error\":\"参数为空\"}"));
                        continue;
                    }

                    var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsStr);
                    if (args == null || !args.TryGetValue("attachment_path", out var pathEl))
                    {
                        toolResults.Add(new ToolChatMessage(toolCall.Id, "{\"error\":\"缺少attachment_path\"}"));
                        continue;
                    }

                    var attachmentPath = pathEl.GetString() ?? "";
                    string fullPath;
                    try
                    {
                        // 复用 AttachmentService.ResolveLocalPath：与上传落盘同根 + traversal 校验，
                        // 防 AI 返回的 attachment_path 路径遍历读 uploads/ 外文件（激活死代码后成真实攻击面）
                        fullPath = _attachmentService.ResolveLocalPath(attachmentPath);
                    }
                    catch (Exception ex)
                    {
                        toolResults.Add(new ToolChatMessage(toolCall.Id, JsonSerializer.Serialize(new { error = "附件路径非法: " + ex.Message })));
                        continue;
                    }

                    try
                    {
                        ClassificationPageResult result;
                        string resultJson;

                        switch (toolCall.FunctionName)
                        {
                            case "classify_attachment":
                                {
                                    var categoryCodes = ParseOptionalArray<string>(args, "category_codes");
                                    var categoryIds = ParseOptionalArray<int>(args, "category_ids");
                                    result = await _classificationService.ClassifyOnlyAsync(fullPath, attachmentPath, categoryCodes, categoryIds);
                                    resultJson = JsonSerializer.Serialize(new { result.CategoryCode, result.CategoryName, result.OcrText });
                                    break;
                                }
                            case "extract_attachment_data":
                                {
                                    var categoryCode = args.TryGetValue("category_code", out var cc) ? cc.GetString() ?? "" : "";
                                    var extractMode = args.TryGetValue("extract_mode", out var em) ? em.GetString() ?? "DOCUMENT" : "DOCUMENT";
                                    result = await _classificationService.ExtractOnlyAsync(fullPath, attachmentPath, categoryCode, extractMode);
                                    resultJson = JsonSerializer.Serialize(new { result.CategoryCode, result.CategoryName, extractedData = result.ExtractDataJson, result.OcrText });
                                    break;
                                }
                            case "classify_and_extract_attachment":
                                {
                                    var categoryCodes = ParseOptionalArray<string>(args, "category_codes");
                                    var categoryIds = ParseOptionalArray<int>(args, "category_ids");
                                    result = await _classificationService.ClassifyAndExtractAsync(fullPath, attachmentPath, categoryCodes, categoryIds);
                                    resultJson = JsonSerializer.Serialize(new { result.CategoryCode, result.CategoryName, extractedData = result.ExtractDataJson, result.OcrText });
                                    break;
                                }
                            default:
                                resultJson = "{\"error\":\"未知工具\"}";
                                break;
                        }

                        toolResults.Add(new ToolChatMessage(toolCall.Id, resultJson));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "附件工具 {ToolName} 调用失败，path={Path}", toolCall.FunctionName, attachmentPath);
                        toolResults.Add(new ToolChatMessage(toolCall.Id, JsonSerializer.Serialize(new { error = ex.Message })));
                    }
                }

                messages.AddRange(toolResults);
                completion = await client.CompleteChatAsync(messages, options);
            }

            var reply = completion.Value.Content[0].Text;
            history.Add(new AssistantChatMessage(reply));

            // 3. 解析结果
            var response = new ChatResponse { SessionId = sessionId, Reply = reply };

            // 尝试提取 JSON
            string? json = null;
            if (reply.Contains("```json"))
            {
                var start = reply.IndexOf("```json") + 7;
                var end = reply.LastIndexOf("```");
                if (end > start) json = reply[start..end];
            }
            else if (reply.Contains("```"))
            {
                var start = reply.IndexOf("```") + 3;
                var end = reply.LastIndexOf("```");
                if (end > start) json = reply[start..end];
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                var start = reply.IndexOf('{');
                var end = reply.LastIndexOf('}');
                if (start >= 0 && end > start) json = reply[start..(end + 1)];
            }

            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    bool isComplete = false;
                    JsonElement? dataEl = null;
                    JsonElement? initialVarsEl = null;  // 组5 (5.3)：AI 返回的 initialVars 对象
                    string? summaryStr = null;

                    foreach (var prop in root.EnumerateObject())
                    {
                        if (prop.Name.Equals("isComplete", StringComparison.OrdinalIgnoreCase))
                            isComplete = prop.Value.ValueKind == JsonValueKind.True;
                        else if (prop.Name.Equals("summary", StringComparison.OrdinalIgnoreCase))
                            summaryStr = prop.Value.GetString();
                        else if (prop.Name.Equals("data", StringComparison.OrdinalIgnoreCase))
                            dataEl = prop.Value;
                        else if (prop.Name.Equals("initialVars", StringComparison.OrdinalIgnoreCase))  // 组5 (5.3)
                            initialVarsEl = prop.Value;
                    }

                    if (isComplete)
                    {
                        response.IsComplete = true;
                        response.Reply = summaryStr ?? reply;
                        _chatSessions.TryRemove(sessionId, out _);
                        _sessionLastAccess.TryRemove(sessionId, out _);

                        // 决策 D1/方案2：提取为 ParseCollectedData（纯重构，对话行为零改变；逻辑等价，便于集成层单测锁 D1.5 不拍平）。
                        response.CollectedData = ParseCollectedData(dataEl);
                        // 组5 (5.3)：复用 ParseCollectedData 解析 initialVars（归一+不拍平，D1.5 原则适用）。
                        response.InitialVars = ParseCollectedData(initialVarsEl);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "JSON 解析失败");
                }
            }

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI 对话调用失败");
            return StatusCode(500, new { error = $"AI 调用失败: {ex.Message}" });
        }
    }

    /// <summary>
    /// 决策 D1/方案2：解析对话收集的 CollectedData（AI 返回的 data 对象）。
    /// 批次7-D1.5（P15/P26）：非 string 值用 NormalizeJsonElement 保持结构，勿 GetRawText 拍平成字符串。
    /// 影响面横切：附件对象数组（ConvertAttachmentUrls is List<object>）、明细表 items 数组（GetLoopRows）、
    /// 任意 object/array——拍平后下游 is 判断全 miss → URL 不转/loop 0 迭代（silent-success，任务仍 Completed）。
    /// 提取自 Chat 内联解析段（纯重构，逻辑等价；internal 供 App.Tests 集成层单测锁 D1.5 不拍平）。
    /// </summary>
    internal static Dictionary<string, object>? ParseCollectedData(JsonElement? dataEl)
    {
        if (!dataEl.HasValue || dataEl.Value.ValueKind != JsonValueKind.Object) return null;
        var dict = new Dictionary<string, object>();
        foreach (var item in dataEl.Value.EnumerateObject())
            dict[item.Name] = SmartFilling.Engine.Engine.VariableHelper.NormalizeJsonElement(item.Value);
        return dict;
    }

    /// <summary>
    /// 构造"附件随消息发送"的用户消息：附件信息块 + 用户文字（消息优先决策；空消息只附件块）。
    /// 提取为 internal 纯函数供 App.Tests 单测（仿 ParseCollectedData 模式）。
    /// 附件信息（文件名/路径/URL）注入用户消息让 AI 感知附件；用户文字是填报意图、附件是数据来源。
    /// 附件处理指令来源三级优先级（用户消息 > 数据处理提示词 > 字段定义）在 ChatCollectorPrompt 落地。
    /// </summary>
    internal static string BuildAttachmentUserMessage(string? userMessage, List<AttachmentInfo>? attachments)
    {
        if (attachments is null || attachments.Count == 0)
            return userMessage ?? "";

        var sb = new StringBuilder();
        sb.AppendLine("用户上传了以下附件：");
        foreach (var attachment in attachments)
            sb.AppendLine($"- 文件名：{attachment.Name}，URL：{attachment.Url}");
        sb.AppendLine("请根据用户本轮消息、数据收集说明、数据收集提示词判断是否需要对附件进行分类或提取数据，如果需要请按系统提示词的工具使用规则自动处理；若脚本 fields 含 file 字段（type=file/uiComponent=upload），把对应附件关联到 file 字段，最终 data[fileField] 设为附件对象数组 [{\"name\":文件名,\"url\":URL}]（回放期 Worker 下载并上传；对象数组结构须保持）。");

        if (string.IsNullOrEmpty(userMessage))
            return sb.ToString();

        sb.AppendLine();
        sb.Append("用户说：").AppendLine(userMessage);
        return sb.ToString();
    }

    [HttpPost("start-dynamic")]
    public async Task<ActionResult> StartDynamic([FromBody] StartFillRequest request)
    {
        try
        {
            // ④-4：传脚本原始 JSON 文本（不经 ScriptLoader），让 Worker 执行边界做 schema+业务校验（对象传输会丢未知字段，补不了 schema 残留字段）。
            var scripts = _scriptService.GetScriptRawTextByDocument(request.DocumentTypeId);
            if (scripts.Count == 0)
                return BadRequest(new { error = "该单据类型没有关联的脚本" });

            // O.3.1：保持嵌套对象/数组结构（勿 GetRawText 拍平成字符串），否则 WorkerApiClient.ConvertAttachmentUrls
            //       与 AttachmentService 遍历不到嵌套附件对象 → 相对 path 不转绝对 URL → 不下载（对象形式附件双重失效）
            // 批次7-D2：附件走 CollectedData[fileField]（对话收集 D1 产附件对象数组），fillData=CollectedData 已含 fileField；
            //          不单独注入 attachments（StartFillRequest 无 Attachments 字段；前端传的 attachments 被忽略冗余）
            // 组3 (3.2)：FillData 入口过滤 ReservedVarNames（既有问题一并修——原未过滤，调用方传 {_lastError:"xxx"} 会遮蔽系统变量）+ 归一。
            var fillData = FilterReservedVars(request.FillData) ?? new Dictionary<string, object>();
            // 组3 (3.2)：InitialVars 入口过滤 ReservedVarNames + 归一。
            var initialVars = FilterReservedVars(request.InitialVars);

            // C5：App 生成 taskId（Worker 不自生成，决策11-A），对齐录制 RecordController 生成模式
            var taskId = Guid.NewGuid().ToString("N")[..8];

            var attachmentBaseUrl = $"{Request.Scheme}://{Request.Host}";
            // C7/F1-2：构造 callbackUrl 传 WorkerApiClient（push 模式需 callbackUrl 非空 + Worker 配置 push=true）
            var progressCallbackUrl = $"{Request.Scheme}://{Request.Host}/api/fill/progress/{taskId}";
            var completeCallbackUrl = $"{Request.Scheme}://{Request.Host}/api/fill/complete/{taskId}";

            // 组5 (5.6)：Serilog 记录 fillData + initialVars（fillData 既有未记录，一并补；明文记录，日志文件受服务器访问控制保护——观察-2 选 D 不脱敏）
            _logger.LogInformation("填报任务启动 fillData: {FillData} initialVars: {InitialVars}", fillData, initialVars);

            var result = await _workerClient.StartFillAsync(
                taskId,
                request.DocumentTypeId,
                request.DocumentTypeName ?? "",
                scripts, fillData,
                progressCallbackUrl,
                completeCallbackUrl,
                attachmentBaseUrl,
                initialVars);

            if (result == null)
                return StatusCode(502, new { error = "Worker 不可用" });

            // P2：提交后登记活跃任务（进度推送回调验证用，与 HealthCheck 分离——HealthCheck 仅控制是否开启健康检测扫描）
            _activeTaskRegistry.Register(taskId);

            // F1-3/G2-4/L4：返 {taskId}（用本地生成的 taskId，非 result.TaskId；去 workerHubUrl）
            return Ok(new { taskId });
        }
        // ④-4：Worker 校验失败（400）透传到前端
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            _logger.LogWarning("Worker 拒绝脚本（校验失败）: {Message}", ex.Message);
            return BadRequest(new { error = ex.Message });
        }
        // B11/C12：Worker 503 透传（退避重试耗尽后，须在 catch(Exception) 之前匹配）
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
        {
            _logger.LogWarning("Worker 503 透传: {Message}", ex.Message);
            return StatusCode(503, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动填报任务失败");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("stop/{taskId}")]
    public async Task<ActionResult> StopFill(string taskId)
    {
        await _workerClient.StopFillAsync(taskId);
        return Ok(new { message = "已发送停止请求" });
    }

    // C7/P2：接收 Worker 进度 push 回调 -> 推 AgentHub（type=fill）-> 前端按 type 分发
    [HttpPost("progress/{taskId}")]
    public async Task<IActionResult> ReceiveProgress(string taskId, [FromBody] ProgressCallbackPayload payload)
    {
        if (payload == null) return BadRequest(new { error = "空 payload" });
        if (!_activeTaskRegistry.Exists(taskId))
            return NotFound(new { error = "任务不存在" });

        if (!string.IsNullOrEmpty(payload.Message))
            await _hub.SendLog(taskId, payload.Message, payload.StepName, type: "fill");
        if (!string.IsNullOrEmpty(payload.Screenshot))
            await _hub.SendScreenshot(taskId, payload.Screenshot, type: "fill");
        return Ok();
    }

    // C7/P2：接收 Worker 终态 push 回调 -> 先推 AgentHub（L2 先推后清防丢失）-> Unregister
    [HttpPost("complete/{taskId}")]
    public async Task<IActionResult> ReceiveComplete(string taskId, [FromBody] CompleteCallbackPayload payload)
    {
        if (payload == null) return BadRequest(new { error = "空 payload" });

        // L2：先推前端，成功后再 Unregister（防 push 失败时任务已移除致前端收不到通知）
        await _hub.TaskCompleted(taskId, payload.Result, errorMessage: payload.ErrorMessage,
            resultData: payload.ResultData, stats: payload.Stats, failureType: payload.FailureType,
            screenshot: payload.Screenshot, type: "fill");
        _activeTaskRegistry.Unregister(taskId);
        return Ok();
    }

    private static T[]? ParseOptionalArray<T>(Dictionary<string, JsonElement> args, string key)
    {
        if (!args.TryGetValue(key, out var el) || el.ValueKind != JsonValueKind.Array) return null;
        return el.EnumerateArray().Select(e => (T)Convert.ChangeType(e.GetString() ?? "", typeof(T))).ToArray();
    }

    /// <summary>组3 (3.2)：入口过滤 ReservedVarNames（调用方不能传系统保留名，否则污染/遮蔽系统变量）+ 归一 JsonElement→CLR。
    /// FillData 和 InitialVars 共用此方法（FillData 是既有问题一并修：原未过滤，调用方传 {_rowIndex:999} 会进 effectiveFillData 遮蔽系统变量）。</summary>
    private static Dictionary<string, object>? FilterReservedVars(Dictionary<string, JsonElement>? input)
    {
        if (input == null) return null;
        var result = new Dictionary<string, object>();
        foreach (var kv in input)
            if (!SmartFilling.Engine.Engine.ScriptLoader.ReservedVarNames.Contains(kv.Key))
                result[kv.Key] = SmartFilling.Engine.Engine.VariableHelper.NormalizeJsonElement(kv.Value);
        return result.Count > 0 ? result : null;
    }
}

public class StartFillRequest
{
    public string DocumentTypeId { get; set; } = "";
    public string? DocumentTypeName { get; set; }
    public Dictionary<string, JsonElement>? FillData { get; set; }
    /// <summary>组3：调用方传入的可变控制变量（可被脚本 storeAs 覆盖，与只读高优先级 fillData 分离）。入口过滤 ReservedVarNames + 归一。</summary>
    public Dictionary<string, JsonElement>? InitialVars { get; set; }
}

/// <summary>Worker 进度 push 回调 payload（HttpProgressReporter.PostProgressAsync 发出，字段名 camelCase 经 JSON 序列化）。</summary>
public class ProgressCallbackPayload
{
    public string? Message { get; set; }
    public string? StepName { get; set; }
    public string? Screenshot { get; set; }  // base64
}

/// <summary>Worker 终态 push 回调 payload（HttpTaskCompletionHandler.PostTerminalAsync 发出，对齐 ScriptResult + 截图）。</summary>
public class CompleteCallbackPayload
{
    public string Result { get; set; } = "";  // "Success" | "Stopped" | "Failed"
    public Dictionary<string, object>? ResultData { get; set; }
    public object? Stats { get; set; }
    public string? ErrorMessage { get; set; }
    public string? FailureType { get; set; }
    public string? Screenshot { get; set; }  // base64 终态截图
}
