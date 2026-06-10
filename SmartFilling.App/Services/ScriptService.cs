using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SmartFilling.App.Configuration;
using SmartFilling.App.Models;
using SmartFilling.Engine.Engine;
using SmartFilling.Engine.Models;

namespace SmartFilling.App.Services;

/// <summary>
/// v2 脚本服务 — 管理 DocumentType 和 ScriptV2 的 JSON 文件存储
/// </summary>
public class ScriptService
{
    private readonly string _dataDir;
    private readonly string _documentsFile;
    private readonly string _scriptsDir;
    private List<DocumentType> _documents = [];
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly IOptions<AppOptions> _appOptions;
    private readonly ILogger<ScriptService> _logger;

    public ScriptService(IWebHostEnvironment env, IOptions<AppOptions> appOptions, ILogger<ScriptService> logger)
    {
        _appOptions = appOptions;
        _logger = logger;
        _dataDir = Path.Combine(env.ContentRootPath, "data");
        _documentsFile = Path.Combine(_dataDir, "documents.json");
        _scriptsDir = Path.Combine(_dataDir, "scripts");
        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(_scriptsDir);

        var outputAll = appOptions.Value.RecordOutputAllFields;
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = outputAll ? JsonIgnoreCondition.Never : JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,      // 输出 camelCase（kind, action, selector）
            PropertyNameCaseInsensitive = true,                      // 兼容旧 PascalCase 脚本文件
            Converters = { new PhaseItemConverter() }
        };
        LoadDocuments();
    }

    private void LoadDocuments()
    {
        if (File.Exists(_documentsFile))
            _documents = JsonSerializer.Deserialize<List<DocumentType>>(File.ReadAllText(_documentsFile), _jsonOptions) ?? [];
    }

    private void SaveDocuments()
    {
        File.WriteAllText(_documentsFile, JsonSerializer.Serialize(_documents, _jsonOptions));
    }

    // ===== DocumentType CRUD =====

    public List<DocumentType> GetAllDocuments() => _documents;
    public DocumentType? GetDocument(string id) => _documents.FirstOrDefault(d => d.Id == id);

    public DocumentType CreateDocument(string name, string description, string icon)
    {
        var doc = new DocumentType { Name = name, Description = description, Icon = icon };
        _documents.Add(doc);
        SaveDocuments();
        return doc;
    }

    public void UpdateDocument(DocumentType doc)
    {
        var idx = _documents.FindIndex(d => d.Id == doc.Id);
        if (idx >= 0) { _documents[idx] = doc; SaveDocuments(); }
    }

    public void DeleteDocument(string id)
    {
        _documents.RemoveAll(d => d.Id == id);
        SaveDocuments();
    }

    // ===== ScriptV2 CRUD =====

    public ScriptV2? GetScript(string scriptId)
    {
        var path = Path.Combine(_scriptsDir, $"{scriptId}.json");
        if (!File.Exists(path)) return null;
        // #4：改走 ScriptLoader.LoadFromJson（F.10.7 Schema+业务校验）；坏脚本容错返回 null（Controller 走 404）
        // lenientNull 由 RecordOutputAllFields 控制：录制器输出全字段(含 null)时宽松校验，避免读不回来
        try { return ScriptLoader.LoadFromJson(File.ReadAllText(path), _appOptions.Value.RecordOutputAllFields); }
        catch (Exception ex) { _logger.LogWarning(ex, "脚本加载失败: {ScriptId}", scriptId); return null; }
    }

    public string SaveScript(ScriptV2 script, bool forceSave = false)
    {
        // #4：写前校验（源头堵残留字段/不合规脚本）。forceSave=false 校验失败→抛 ScriptValidationException（承载错误列表供 Controller 返回前端）；
        // forceSave=true 跳校验（用户确认强制保存缺 aiGoal 等不完整脚本——取消/超时/AI 未回填 aiGoal 的脚本）。
        // 校验前置于 DocumentType 关联：避免校验失败仍改 documents.json 的脏关联 bug（原校验在关联后，失败时 doc.OrderedScriptIds 已被加入）。
        if (!forceSave)
        {
            // 最严检查：业务校验（ValidateAndGetErrors）+ schema 架构校验（ValidateSchemaAndGetErrors）两层。
            // 🔴难点4 边界：SaveScript 序列化对象查 schema——抓"缺字段"有效（aiGoal/selector 缺失→序列化 null→StripNulls→schema required/if-then 抓到，
            // 这是 A 块核心场景拦缺 aiGoal）；抓"未知字段"无效（对象反序列化已丢，但录制器双层过滤 AI 按工具 schema 传参+代码强类型 StepNode 赋值保证产出无未知字段，无所谓）。
            // lenientNull 动态取 RecordOutputAllFields（难点1，不硬编码）；forceSave=true 跳过整个 if 块即跳 schema+业务两层（场景2"确认后不做检查直接存"）。
            var errors = ScriptLoader.ValidateAndGetErrors(script);
            errors.AddRange(ScriptLoader.ValidateSchemaAndGetErrors(
                JsonSerializer.Serialize(script, _jsonOptions), _appOptions.Value.RecordOutputAllFields));
            if (errors.Count > 0)
                throw new ScriptValidationException(errors);
        }

        // 关联到 DocumentType（校验通过/forceSave 后才关联，需在写入文件之前，确保 DocumentTypeId 已替换为真正的 ID）
        if (!string.IsNullOrEmpty(script.DocumentTypeId))
        {
            // 按 ID 或 Name 查找（用户输入的是名称，如"智工云-登录"）
            var doc = _documents.FirstOrDefault(d => d.Id == script.DocumentTypeId || d.Name == script.DocumentTypeId);
            if (doc == null)
            {
                // 不存在则自动创建 DocumentType
                doc = new DocumentType { Name = script.DocumentTypeId, Description = $"自动创建于 {DateTime.Now}" };
                _documents.Add(doc);
            }
            // 将用户输入的文本替换为真正的 doc.Id
            script = script with { DocumentTypeId = doc.Id };
            if (!doc.OrderedScriptIds.Contains(script.ScriptId))
            {
                doc.OrderedScriptIds.Add(script.ScriptId);
                SaveDocuments();
            }
        }

        var path = Path.Combine(_scriptsDir, $"{script.ScriptId}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(script, _jsonOptions));
        return path;
    }

    public void DeleteScript(string scriptId)
    {
        var path = Path.Combine(_scriptsDir, $"{scriptId}.json");
        if (File.Exists(path)) File.Delete(path);
        foreach (var d in _documents)
            d.OrderedScriptIds.Remove(scriptId);
        SaveDocuments();
    }

    public List<ScriptV2> GetScriptsByDocument(string documentTypeId)
    {
        var doc = _documents.FirstOrDefault(d => d.Id == documentTypeId || d.Name == documentTypeId);
        if (doc == null) return [];
        return doc.OrderedScriptIds
            .Select(id => GetScript(id))
            .Where(s => s != null)
            .Cast<ScriptV2>()
            .ToList();
    }

    /// <summary>④-4：取单据关联脚本的原始 JSON 文本（不经 ScriptLoader，保留全字段供 Worker 执行边界做 schema+业务校验）。</summary>
    public List<string> GetScriptRawTextByDocument(string documentTypeId)
    {
        var doc = _documents.FirstOrDefault(d => d.Id == documentTypeId || d.Name == documentTypeId);
        if (doc == null) return [];
        return doc.OrderedScriptIds
            .Select(id => Path.Combine(_scriptsDir, $"{id}.json"))
            .Where(File.Exists)
            .Select(p => File.ReadAllText(p))
            .ToList();
    }

    public List<ScriptListItem> GetAllScripts()
    {
        var result = new List<ScriptListItem>();
        if (!Directory.Exists(_scriptsDir)) return result;
        foreach (var file in Directory.GetFiles(_scriptsDir, "*.json"))
        {
            var fileName = Path.GetFileNameWithoutExtension(file);  // 结构坏兜底时用文件名（拿不到 script.ScriptId/Name）
            try
            {
                // 宽进读回：保留原始 json（schema 补全——未知字段只在原始 json 里），DeserializeOnly 跳 schema+校验拿到 script 对象算 name。
                var json = File.ReadAllText(file);
                var script = ScriptLoader.DeserializeOnly(json);
                var errors = ScriptLoader.ValidateAndGetErrors(script);
                // 补 schema（最严检查：业务+schema 两层，含未知字段也标 HasErrors）。lenientNull 动态取 RecordOutputAllFields（难点1）。
                errors.AddRange(ScriptLoader.ValidateSchemaAndGetErrors(json, _appOptions.Value.RecordOutputAllFields));
                result.Add(new ScriptListItem(script.ScriptId ?? "", script.Name ?? "", errors.Count > 0));
            }
            catch (Exception ex)
            {
                // 结构坏 JSON（DeserializeOnly 抛）：兜底显示（用户方案场景3"名称读不出→兜底"，覆盖原"列表无期望它存在语义"理由——不再静默跳过不显示）。
                // ScriptId/Name 用文件名兜底（结构坏拿不到 script.ScriptId/Name）+ HasErrors=true + LogWarning 可观测。
                _logger.LogWarning(ex, "脚本读回失败（结构损坏，兜底显示为异常脚本）: {File}", file);
                result.Add(new ScriptListItem(fileName, $"{fileName}（异常脚本）", true));
            }
        }
        return result;
    }

    /// <summary>
    /// 读回脚本 + 业务校验错误 + 文件存在标志（三元组），供 RecordingEngine 前置块区分四种情况：
    /// ① 文件不存在(fileExists=false)→前置可选正常跳过；② 结构坏(fileExists=true &amp;&amp; script==null)→throw PrerequisiteScriptFailedException；
    /// ③ 带病(fileExists=true &amp;&amp; script!=null &amp;&amp; errors.Count&gt;0)→throw PrerequisiteScriptFailedException；④ 合法(errors 空)→执行。
    /// 宽进 DeserializeOnly 读回（带病也返回 script 非 null，能拼 script.Name）；结构坏 JSON catch→(null,[],true)+LogWarning（区分"文件存在但损坏"≠"文件不存在"，防 silent 跳过）。
    /// </summary>
    public (ScriptV2? script, List<string> errors, bool fileExists) GetScriptWithErrors(string scriptId)
    {
        var path = Path.Combine(_scriptsDir, $"{scriptId}.json");
        if (!File.Exists(path))
            return (null, new List<string>(), false);  // 文件不存在：前置可选，正常跳过（非病态）

        try
        {
            // 保留原始 json（schema 补全：未知字段只在原始 json 里，对象反序列化后 STJ 默认丢弃查不到）
            var json = File.ReadAllText(path);
            var script = ScriptLoader.DeserializeOnly(json);
            var errors = ScriptLoader.ValidateAndGetErrors(script);
            // 补 schema（最严检查：业务+schema 两层）。lenientNull 动态取 RecordOutputAllFields（与 GetScript:92 一致，不硬编码——
            // RecordOutputAllFields=true 时 StripNulls 移除 null，避免含 null 的录制脚本被 schema required 误杀）。
            // 含未知字段（ifrmae 拼错等）的脚本经此→errors 非空→③ 分支 throw PrerequisiteScriptFailedException→Failed（场景1）。
            errors.AddRange(ScriptLoader.ValidateSchemaAndGetErrors(json, _appOptions.Value.RecordOutputAllFields));
            return (script, errors, true);
        }
        catch (Exception ex)
        {
            // 结构坏/JSON 损坏：DeserializeOnly 抛。fileExists=true 区分"文件存在但损坏"（→调用方 throw Failed）≠"文件不存在"（→跳过）。
            _logger.LogWarning(ex, "脚本加载失败（文件损坏/JSON 格式错误）: {ScriptId}", scriptId);
            return (null, new List<string>(), true);
        }
    }
}
