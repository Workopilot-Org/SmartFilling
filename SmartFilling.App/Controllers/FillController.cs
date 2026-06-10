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
        _expirationMinutes = _config.GetValue<int>("ChatSession:ExpirationMinutes", 60);
        _cleanupTimer.Change(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));
    }

    public FillController(
        ScriptService scriptService,
        WorkerApiClient workerClient,
        AttachmentClassificationService classificationService,
        AttachmentService attachmentService,
        IConfiguration config,
        IWebHostEnvironment env,
        ILogger<FillController> logger)
    {
        _scriptService = scriptService;
        _workerClient = workerClient;
        _classificationService = classificationService;
        _attachmentService = attachmentService;
        _config = config;
        _env = env;
        _logger = logger;
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

            if (!string.IsNullOrEmpty(request.ScriptId))
            {
                var docType = request.ScriptId;
                var scripts = _scriptService.GetScriptsByDocument(docType);
                var script = scripts.FirstOrDefault();

                // 脚本加载失败（坏脚本/未关联）→ 返回 400 让前端可见，不再静默降级成"未知单据"
                if (script == null)
                {
                    _chatSessions.TryRemove(sessionId, out _);
                    _sessionLastAccess.TryRemove(sessionId, out _);
                    return BadRequest(new { error = "脚本加载或校验失败，请检查脚本或重新录制" });
                }

                // v2：使用 ScriptV2.Fields 代替 v1 ExtractedFields
                // #50 决策T.8：收集层正向白名单——仅 source 为空或 user 的字段交 AI 收集（排除 system 凭据 + computed 计算值；system 由调用方提供，computed 由脚本 storeAs 计算，AI 询问会覆盖正确值）
                if (script.Fields is { Count: > 0 })
                {
                    var collectableFields = script.Fields
                        .Where(f => string.IsNullOrEmpty(f.Source) || string.Equals(f.Source, "user", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (collectableFields.Count == 0)
                    {
                        // 全部字段 source=system（如登录凭据），由前端输入框（HTTP）/WS 任务提供，无需 chat 收集 → 直接开始填报
                        _chatSessions.TryRemove(sessionId, out _);
                        _sessionLastAccess.TryRemove(sessionId, out _);
                        return Ok(new ChatResponse { SessionId = sessionId, IsComplete = true, Reply = "本单据数据均由系统提供（登录凭据等），无需额外信息，可直接开始填报。", CollectedData = new Dictionary<string, object>() });
                    }
                    fieldsDefinition = JsonSerializer.Serialize(collectableFields, new JsonSerializerOptions { WriteIndented = true });
                }
                if (!string.IsNullOrEmpty(script.Name))
                    scriptName = script.Name;

                // v2：使用 DocumentType.DataProcessingPrompts
                var document = _scriptService.GetDocument(docType);
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
                    string? summaryStr = null;

                    foreach (var prop in root.EnumerateObject())
                    {
                        if (prop.Name.Equals("isComplete", StringComparison.OrdinalIgnoreCase))
                            isComplete = prop.Value.ValueKind == JsonValueKind.True;
                        else if (prop.Name.Equals("summary", StringComparison.OrdinalIgnoreCase))
                            summaryStr = prop.Value.GetString();
                        else if (prop.Name.Equals("data", StringComparison.OrdinalIgnoreCase))
                            dataEl = prop.Value;
                    }

                    if (isComplete)
                    {
                        response.IsComplete = true;
                        response.Reply = summaryStr ?? reply;
                        _chatSessions.TryRemove(sessionId, out _);
                        _sessionLastAccess.TryRemove(sessionId, out _);

                        // 决策 D1/方案2：提取为 ParseCollectedData（纯重构，对话行为零改变；逻辑等价，便于集成层单测锁 D1.5 不拍平）。
                        response.CollectedData = ParseCollectedData(dataEl);
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
            var fillData = request.FillData?.ToDictionary(
                kv => kv.Key,
                kv => (object)SmartFilling.Engine.Engine.VariableHelper.NormalizeJsonElement(kv.Value)) ?? new Dictionary<string, object>();

            var attachmentBaseUrl = $"{Request.Scheme}://{Request.Host}";
            var result = await _workerClient.StartFillAsync(
                request.DocumentTypeId,
                request.DocumentTypeName ?? "",
                scripts, fillData, attachmentBaseUrl);

            if (result == null)
                return StatusCode(502, new { error = "Worker 不可用" });

            return Ok(new { taskId = result.TaskId, workerHubUrl = result.WorkerHubUrl });
        }
        // ④-4：Worker 校验失败（400）透传到前端（前端 app.js 已能读 errData.error），须在 catch(Exception) 之前匹配
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            _logger.LogWarning("Worker 拒绝脚本（校验失败）: {Message}", ex.Message);
            return BadRequest(new { error = ex.Message });
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

    private static T[]? ParseOptionalArray<T>(Dictionary<string, JsonElement> args, string key)
    {
        if (!args.TryGetValue(key, out var el) || el.ValueKind != JsonValueKind.Array) return null;
        return el.EnumerateArray().Select(e => (T)Convert.ChangeType(e.GetString() ?? "", typeof(T))).ToArray();
    }
}

public class StartFillRequest
{
    public string DocumentTypeId { get; set; } = "";
    public string? DocumentTypeName { get; set; }
    public Dictionary<string, JsonElement>? FillData { get; set; }
}
