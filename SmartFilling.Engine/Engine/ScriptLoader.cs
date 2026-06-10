using System.Text;
using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Json.Schema;
using SmartFilling.Engine.Models;

namespace SmartFilling.Engine.Engine;

public static class ScriptLoader
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,  // F1：防御性，与团队约定对齐（其他 JSON options 站点均设此项；#20「对任何 casing 都安全」原则对称覆盖脚本加载路径）
        // 支持脚本 JSON 注释（// 行注释、/* 块注释 */）：手写/手编脚本可加注解。
        // 三处 JSON 解析点（此处 JsonSerializer + _documentOptions 的 JsonDocument/JsonNode）须统一 Skip 注释——
        // System.Text.Json 默认均 Disallow 注释，仅改此处不够（见 _documentOptions）。
        // ⚠️注释只读入有效：脚本经 Serialize(script) 对象→JSON 重新写文件时注释会丢（对象无注释字段）；
        // 前端无脚本编辑器，注释丢失现实路径=重新录制同名脚本覆盖 / 经 API 重保存；直接编辑 .json 文件所加注释保留至该脚本被重新序列化保存。
        // 不开 AllowTrailingCommas（用户决策 2026-07-05：只要注释，不要尾随逗号）。
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new PhaseItemConverter() }
    };

    // 所有 JsonDocument/JsonNode 解析点统一 Skip 注释（与 _jsonOptions.ReadCommentHandling 对齐）。
    // 🔴关键：JsonDocument.Parse 默认 Disallow 注释→遇注释抛 JsonException→ValidateAgainstSchema 的 catch(JsonException) 返回 null→
    // schema 校验降级跳过，带注释脚本里的未知字段/类型错将静默漏检（silent-success，由 JsonComment_DoesNotBypassSchemaValidation 守护）。
    // StripNulls 的 JsonNode.Parse 同理（lenientNull=true 时带注释脚本直接崩）。
    private static readonly JsonDocumentOptions _documentOptions = new() { CommentHandling = JsonCommentHandling.Skip };

    public static ScriptV2 LoadFromJson(string json, bool lenientNull = false)
    {
        // F.10.7 运行时 Schema 校验：防字段残留（keyword/skipIfEmpty 等破坏性改名后，旧字段名会被默认反序列化忽略→静默失效）。
        // schema 未加载（资源缺失/解析失败）时降级跳过；校验失败抛错让调用方感知。
        // lenientNull=true：宽松模式，Schema 校验前先 StripNulls 移除 null 字段（录制器 RecordOutputAllFields=true 输出全 null 时用）。
        // 反序列化仍用原始 json（null 与字段缺失都映射 null，结果一致）。
        var schemaErrors = ValidateAgainstSchema(json, lenientNull);
        if (schemaErrors != null)
            throw new InvalidOperationException($"脚本 Schema 校验失败:\n{schemaErrors}");

        var script = JsonSerializer.Deserialize<ScriptV2>(json, _jsonOptions)
            ?? throw new JsonException("脚本反序列化失败");

        Validate(script);
        return script;
    }

    /// <summary>
    /// 宽进反序列化：跳过 Schema 校验 + 跳过 Validate，仅 JsonSerializer.Deserialize&lt;ScriptV2&gt;（复用类内 _jsonOptions，
    /// 含 PhaseItemConverter + CamelCase，否则 Phases: List&lt;PhaseItem&gt; 按 kind 分发反序列化失败）。
    /// 供 GetAllScripts/GetScriptWithErrors 读回带病脚本（缺 aiGoal 等业务校验失败的强制保存脚本也能拿到 name/结构，不再 catch 静默跳过）。
    /// 结构坏 JSON（反序列化抛 JsonException）由调用方 catch 处理。不带 lenientNull：lenientNull 的 StripNulls 只为 schema 校验服务，
    /// 跳 schema 后无作用；且 null 与字段缺失 STJ 都映射 null、结果一致。
    /// </summary>
    public static ScriptV2 DeserializeOnly(string json)
        => JsonSerializer.Deserialize<ScriptV2>(json, _jsonOptions)
           ?? throw new JsonException("脚本反序列化失败");

    /// <summary>
    /// schema 校验 public 入口（补全：DeserializeOnly 跳了 schema，此方法让 GetScriptWithErrors/GetAllScripts/SaveScript 补回 schema 校验）。
    /// 包装 private <see cref="ValidateAgainstSchema"/>（行为不变，LoadFromJson 不受影响），将其多行 string? 返回值按行 split 为 List&lt;string&gt;（空=通过/降级）。
    /// 🔴**必须接收原始 json 字符串**（不是 ScriptV2 对象）：未知字段（如 S4 改名前的旧名 skipIfEmpty、拼错的 ifrmae）只存在于原始 json；
    /// 反序列化成对象后 STJ 默认 Skip 丢弃、对象里不存在，基于对象的校验查不到（反序列化是单向损耗）。
    /// lenientNull=true 先 StripNulls（与 LoadFromJson 一致：RecordOutputAllFields=true 时移除 null，避免含 null 的录制脚本被 schema required 误杀）。
    /// _scriptSchema=null（嵌入资源缺失）时 ValidateAgainstSchema 返回 null → 返回空列表（降级，与 LoadFromJson 一致）。
    /// 🔴try/catch 兜底不抛：JsonSchema.Evaluate 意外异常 catch 返回空列表，否则被 GetScriptWithErrors/GetAllScripts 外层 catch 吞成"结构坏"语义（script=null 兜底/列表兜底）错位。
    /// </summary>
    public static List<string> ValidateSchemaAndGetErrors(string json, bool lenientNull)
    {
        try
        {
            var schemaErrors = ValidateAgainstSchema(json, lenientNull);
            if (schemaErrors == null) return new List<string>();
            // ValidateAgainstSchema 用 AppendLine 构造多行（每行一个错误，Windows \r\n）；split + 去空行 + 去首尾空白
            var result = new List<string>();
            foreach (var line in schemaErrors.Split('\n'))
            {
                var trimmed = line.Trim('\r', ' ', '\t');
                if (trimmed.Length > 0) result.Add(trimmed);
            }
            return result;
        }
        catch
        {
            return new List<string>();  // 兜底不抛：意外异常降级返回空，避免被外层 catch 吞成"结构坏"语义错位
        }
    }

    /// <summary>
    /// F.10.7：加载脚本数组（平台 List&lt;ScriptV2&gt; JSON 响应），逐元素 Schema+业务校验。
    /// 单元素失败直接抛错（让 Worker 任务失败，不静默吞）。
    /// </summary>
    public static List<ScriptV2> LoadManyFromJson(string jsonArray, bool lenientNull = false)
    {
        using var doc = JsonDocument.Parse(jsonArray, _documentOptions);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("平台返回的脚本 JSON 不是数组");
        var scripts = new List<ScriptV2>();
        foreach (var item in doc.RootElement.EnumerateArray())
            scripts.Add(LoadFromJson(item.GetRawText(), lenientNull));
        return scripts;
    }

    /// <summary>
    /// schema 加载是否失败（嵌入资源缺失/解析异常）。true 时 ValidateAgainstSchema 返 null（降级，schema 校验整体跳过——含未知字段的脚本将不被校验）。
    /// 🔴声明须在 _scriptSchema 之前：_scriptSchema 的 inline 初始化调 LoadEmbeddedSchema 设此标志，若声明在后会被默认 false 初始化覆盖。
    /// Engine 层无 Serilog 依赖（用 ILogger 抽象解耦），LoadEmbeddedSchema 内用 Console.Error 即时输出 stderr；宿主（App/Worker）启动时检查此标志用 Serilog 持久化到文件日志（运维可查）。
    /// </summary>
    public static bool SchemaLoadFailed;

    /// <summary>运行时加载嵌入的 script-v2.json Schema（F.10.7）。加载失败返回 null（降级跳过校验）+ 设 SchemaLoadFailed=true。
    /// 非 readonly 仅为支持测试注入降级场景（ScriptLoaderSchemaTests 反射设 null 验证 ValidateSchemaAndGetErrors 降级返空——.NET 8 反射改 initonly static 会抛 FieldAccessException）；生产代码仅 LoadEmbeddedSchema inline 初始化一次，不应运行时改。</summary>
    private static JsonSchema? _scriptSchema = LoadEmbeddedSchema();

    private static JsonSchema? LoadEmbeddedSchema()
    {
        try
        {
            // JsonSchema.Net 9.x 默认 dialect 是即将发布的 V1（文档建议显式设 Draft202012）；脚本 schema 声明 draft 2020-12，显式设定确保按 2020-12 规则求值（$ref/additionalProperties 等）
            Dialect.Default = Dialect.Draft202012;
            var asm = typeof(ScriptLoader).Assembly;
            var resName = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("script-v2.json", StringComparison.Ordinal));
            if (resName == null)
            {
                SchemaLoadFailed = true;
                Console.Error.WriteLine("[SmartFilling] 脚本 Schema 嵌入资源 (script-v2.json) 未找到——schema 校验已降级禁用，含未知字段的脚本将不会被校验。请检查 Engine.csproj EmbeddedResource 配置。");
                return null;
            }
            using var stream = asm.GetManifestResourceStream(resName);
            using var reader = new StreamReader(stream);
            return JsonSchema.FromText(reader.ReadToEnd());
        }
        catch (Exception ex)
        {
            SchemaLoadFailed = true;
            Console.Error.WriteLine($"[SmartFilling] 脚本 Schema (script-v2.json) 加载失败——schema 校验已降级禁用，含未知字段的脚本将不会被校验。异常: {ex.GetType().Name}: {ex.Message}");
            return null;  // schema 加载失败不阻断启动（降级到业务校验），但宿主启动时应据 SchemaLoadFailed 标志记 Serilog 日志告警
        }
    }

    /// <summary>用 script-v2.json 校验脚本 JSON。返回错误描述（多行）；null 表示通过或 schema 未加载。</summary>
    private static string? ValidateAgainstSchema(string json, bool lenientNull)
    {
        if (_scriptSchema == null) return null;
        if (lenientNull) json = StripNulls(json);  // 宽松模式：校验前移除 null 字段（null 与字段缺失语义等价）
        JsonElement root;
        try { using var doc = JsonDocument.Parse(json, _documentOptions); root = doc.RootElement.Clone(); }
        catch (JsonException) { return null; }  // JSON 语法错交给后续 Deserialize 报

        var results = _scriptSchema.Evaluate(root, new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical });
        if (results.IsValid) return null;

        var sb = new StringBuilder();
        foreach (var r in Flatten(results))
        {
            if (r.Errors == null) continue;
            var loc = r.InstanceLocation.ToString();
            foreach (var e in r.Errors)
                sb.AppendLine(string.IsNullOrEmpty(loc) ? $"  - {e.Value}" : $"  - {loc}: {e.Value}");
        }
        return sb.Length == 0 ? "脚本不符合 Schema（未知字段或类型错误）" : sb.ToString();

        static IEnumerable<EvaluationResults> Flatten(EvaluationResults root)
        {
            yield return root;
            if (root.Details != null)
                foreach (var d in root.Details)
                    foreach (var x in Flatten(d)) yield return x;
        }
    }

    /// <summary>
    /// 宽松校验预处理：递归移除值为 null 的对象属性与数组元素。
    /// null 与"字段缺失"在 v2 语义等价，移除不改变脚本行为；仅用于 lenientNull=true 模式的 Schema 校验。
    /// </summary>
    private static string StripNulls(string json)
    {
        var node = JsonNode.Parse(json, documentOptions: _documentOptions);
        var cleaned = StripNullsNode(node);
        return cleaned?.ToJsonString() ?? "{}";
    }

    private static JsonNode? StripNullsNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var result = new JsonObject();
                foreach (var kv in obj)              // 不修改原 obj，新建 result 避免遍历中改集合
                    if (kv.Value is not null)
                    {
                        var c = StripNullsNode(kv.Value);
                        if (c is not null) result[kv.Key] = c;
                    }
                return result;
            }
            case JsonArray arr:
            {
                var result = new JsonArray();
                foreach (var item in arr)
                    if (item is not null)
                    {
                        var c = StripNullsNode(item);
                        if (c is not null) result.Add(c);
                    }
                return result;
            }
            default:
                return node?.DeepClone();            // 标量深拷贝（JsonNode 单父约束：复用原节点会抛 "already has a parent"）
        }
    }

    public static ScriptV2 LoadFromFile(string path, bool lenientNull = false)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"脚本文件不存在: {path}");

        var json = File.ReadAllText(path);
        return LoadFromJson(json, lenientNull);
    }

    public static void Validate(ScriptV2 script)
    {
        var errors = ValidateAndGetErrors(script);
        if (errors.Count > 0)
            throw new InvalidOperationException($"脚本校验失败:\n{string.Join("\n", errors)}");
    }

    /// <summary>
    /// 收集脚本业务校验错误（不抛异常），返回错误列表（空=通过）。
    /// 拆自 Validate（行为不变）：Validate 现调它→非空 throw；SaveScript(forceSave=false) 据此决定拒绝/强制保存；
    /// GetAllScripts/GetScriptWithErrors 读回宽进用它算 HasErrors。回归网：Engine.Tests 15 处直达 Validate 的测试（NameCollision 5 / ScrollExclusive 7 / Schema 3）兜底。
    /// 形态 A（2026-07-02）：已删 ValidateIframeReferences 调用 + 三方法定义，改为 ValidateIframeChains（selector 链校验）+ ValidateDetectUntilUsage（R4 增强）。
    /// </summary>
    public static List<string> ValidateAndGetErrors(ScriptV2 script)
    {
        var errors = new List<string>();

        // 基础校验
        if (string.IsNullOrEmpty(script.Name))
            errors.Add("脚本缺少 name 字段");

        if (script.Phases.Count == 0)
            errors.Add("脚本至少需要一个 phase");

        // 顶层 phases 类型校验：每个元素必须是 PhaseNode
        foreach (var item in script.Phases)
        {
            if (item is not PhaseNode)
                errors.Add($"顶层 phases 中存在非 PhaseNode 元素 (kind: {item.Kind})");
        }

        // Phase name 全局唯一（方法体局部变量：L175 声明 + L176 CollectPhaseNames 收集 → L204 ValidateGotoTargets 消费，搬迁须保留顺序）
        var phaseNames = new HashSet<string>();
        CollectPhaseNames(script.Phases, phaseNames, errors);

        // R3-2：所有 phase aiGoal 严格必填（空→拒绝加载；取消/异常保存的不完整脚本缺 aiGoal 由人工介入，不兜底）
        ValidatePhaseAiGoal(script.Phases, errors);

        // Field name 全局唯一（递归收集嵌套字段；方法体局部变量：L182 收集 → L211 ValidateNameCollisions 消费，搬迁须保留顺序）
        var fieldNames = new HashSet<string>();
        CollectFieldNames(script.Fields, fieldNames, errors);

        // Step name 同一父 phase 内唯一
        ValidateStepNames(script.Phases, errors);

        // {{X}} 占位符引用校验
        ValidatePlaceholders(script, errors);

        // storeAs 变量名校验
        ValidateStoreAs(script.Phases, errors);

        // 形态 A：iframe selector 链校验（每项非空合法 selector；自包含无引用）
        ValidateIframeChains(script, errors);

        // ai phase 结构校验（P3-2: 不允许嵌套子 phase + action 只能为 ai 或省略）
        ValidateAiPhaseStructure(script.Phases, errors);

        // 决策13：then 用法校验（then 必须 action==check；break/continue/row_rerun 限 loop phase）
        ValidateThenUsage(script.Phases, null, errors);

        // R4 增强1：detect 专属 check、until 专属 wait 绑定校验（仿 ValidateThenUsage）
        ValidateDetectUntilUsage(script.Phases, errors);

        // DC11 硬校验（a-a-4 第54轮升级，2026-07-10）：loop phase 内多个 new_row_appears（check.detect 或 step.Condition）须同 selector+iframe，
        // 否则 _lastRowCount 基线语义混乱（TryFindNewRowAppearsNode 取第一个，其他不一致的 silent 误判）。schema 表达不了跨节点同-selector 约束，用 ScriptLoader 硬校验。
        ValidateNewRowAppearsBaselineConsistency(script.Phases, errors);

        // A 组校验加固（R2-6/R3-3/R3-4/R4-1/R4-2）
        ValidateGotoTargets(script.Phases, phaseNames, errors);   // R2-6：goto toPhase/toStep 目标存在性
        ValidateArrayFields(script.Fields, errors);               // R3-3：type=array 须 items 或 fields
        ValidateExtractProperty(script.Phases, errors);           // R3-4：extractType=property 时 property 必填
        ValidateScrollExclusive(script.Phases, errors);          // 批次9④：scroll selector/direction 二选一
        ValidateDetectParamsAll(script, errors);                  // R4-1：detect 各 type 必填参数
        ValidateFileUiComponent(script.Fields, errors);           // R4-2：type=file uiComponent 限 upload/hidden
        ValidateSystemCredentialFields(script.Fields, errors);    // DEC-7：username/password 必须 source=system
        ValidateCaptcha(script.Phases, errors);                   // F2：captcha pixel 必填 slider/target/background selector
        ValidateNameCollisions(script, fieldNames, errors);       // ④-8：字段名∩storeAs 名 / 字段名∩系统保留字（静默遮蔽取错值）
        ValidateSelectorCharset(script, errors);                  // F：selector 非法字符（弯引号/全角空格）加载期拦截，把"回放才崩"提前到"加载就拒"

        return errors;
    }

    /// <summary>
    /// ④-8：命名冲突校验（运行时 fillData/vars 全局扁平、无词法作用域，"同作用域"检查必漏报，故全局）。
    /// ① 字段名∩storeAs 名：字段名进 fillData（scopeChain 末层），{{X}} 查找 scopeChain 优先于 vars → 字段名永久遮蔽同名 storeAs，静默取错值。
    /// ② 字段名∩系统保留字（🔴-3）：字段名遮蔽 rowIndex/_lastUrl/_lastRowCount/lastError（如 returnData error:"{{lastError}}" 取字段值非错误消息）。
    /// （storeAs 名撞保留字已由 ValidateSingleVarName 校验；storeAs-vs-storeAs 同名覆盖允许——故意覆盖是合法用法。）
    /// </summary>
    private static void ValidateNameCollisions(ScriptV2 script, HashSet<string> fieldNames, List<string> errors)
    {
        var storeAsNames = CollectAllStoreAsNames(script.Phases);
        foreach (var fn in fieldNames)
        {
            if (storeAsNames.Contains(fn))
                errors.Add($"字段名 '{fn}' 与某 step 的 storeAs 变量名同名——运行时 fillData 会永久遮蔽 vars 同名变量，静默取错值");
            if (ReservedVarNames.Contains(fn))
                errors.Add($"字段名 '{fn}' 与系统保留变量同名（{string.Join("/", ReservedVarNames)}）——会遮蔽系统变量，静默取错值（如 {{lastError}}/{{rowIndex}} 取字段值）");
        }
    }

    /// <summary>
    /// 决策13：then 用法校验。① then 只能在 action=check 的步骤上使用（运行时 check 专属，防手写误配）；
    /// ② break/continue/row_rerun 仅限 loop phase（防非 loop 误用静默失效——非 loop 走 default=no-op 脚本编写者以为生效）。
    /// </summary>
    private static void ValidateThenUsage(List<PhaseItem> items, string? parentPhaseType, List<string> errors)
    {
        foreach (var item in items)
        {
            if (item is StepNode step && !string.IsNullOrEmpty(step.Then))
            {
                if (step.Action != "check")
                    errors.Add($"then 只能在 action=check 的步骤上使用（当前 action={step.Action}, step={step.Name ?? step.Action}）");
                if (step.Then is "break" or "continue" or "row_rerun" && parentPhaseType != "loop")
                    errors.Add($"then={step.Then} 仅可在 loop phase 内使用（当前 phase 类型={parentPhaseType ?? "sequential"}, step={step.Name ?? step.Action}）");
            }
            if (item is PhaseNode phase && phase.Steps.Count > 0)
                ValidateThenUsage(phase.Steps, phase.Type, errors);
        }
    }

    private static void CollectPhaseNames(List<PhaseItem> items, HashSet<string> names, List<string> errors)
    {
        foreach (var item in items)
        {
            if (item is PhaseNode phase)
            {
                if (!names.Add(phase.Name))
                    errors.Add($"Phase name 重复: {phase.Name}");
                if (phase.Steps.Count > 0)
                    CollectPhaseNames(phase.Steps, names, errors);
            }
        }
    }

    private static void ValidateStepNames(List<PhaseItem> items, List<string> errors)
    {
        // 只检查直接子步骤的 name 唯一性（goto toStep 只查直接 steps）
        var stepNames = new HashSet<string>();
        foreach (var item in items)
        {
            if (item is StepNode step && step.Name != null)
            {
                if (!stepNames.Add(step.Name))
                    errors.Add($"同一 phase 内 step name 重复: {step.Name}");
            }
            if (item is PhaseNode nested && nested.Steps.Count > 0)
                ValidateStepNames(nested.Steps, errors);
        }
    }

    private static void ValidatePlaceholders(ScriptV2 script, List<string> errors)
    {
        // 递归收集所有层级字段名（含嵌套 field.Fields）
        var definedFields = new HashSet<string>();
        CollectFieldDefinitions(script.Fields, definedFields);
        var specialVars = ReservedVarNames;  // ④-8：复用 ReservedVarNames（含 lastError）——step 里 {{lastError}} 引用放行（R2-12 范围内的系统变量）
        // F4(R1-3)：storeAs 变量在运行时存入 vars，可被后续 step {{var}} 引用（设计十五核心特性），并入合法占位符集合，
        // 否则 step A storeAs:"total" → step B value:"{{total}}" 会误报「total 未在 fields[] 定义」阻断加载。
        // 注意：returnData 顶层占位符本就不经本方法校验（仅 WalkItems(Phases)），不受影响。
        var storeAsNames = CollectAllStoreAsNames(script.Phases);

        void CheckStep(StepNode step)
        {
            var placeholders = ExtractPlaceholders(step);
            foreach (var ph in placeholders)
            {
                if (specialVars.Contains(ph) || definedFields.Contains(ph) || storeAsNames.Contains(ph)) continue;
                // 不在 fields 中定义，可能是嵌套对象的字段路径（如 items.name），跳过
                if (ph.Contains('.')) continue;
                errors.Add($"占位符 {{{{{ph}}}}} 引用的字段未在 fields[] 中定义 (step: {step.Action} {step.Selector})");
            }
        }

        void CheckIframeChainPlaceholders(string[]? chain, string context)
        {
            if (chain == null) return;
            foreach (var layer in chain)
            {
                if (string.IsNullOrEmpty(layer)) continue;
                foreach (Match m in Regex.Matches(layer, @"\{\{(\w+(?:\.\w+)*)\}\}"))
                {
                    var ph = m.Groups[1].Value;
                    if (specialVars.Contains(ph) || definedFields.Contains(ph) || storeAsNames.Contains(ph)) continue;
                    if (ph.Contains('.')) continue;
                    errors.Add($"占位符 {{{{{ph}}}}} 引用的字段未在 fields[] 中定义 ({context}, iframe 链)");
                }
            }
        }

        void CheckDetectPlaceholders(DetectCondition? detect, string context)
        {
            if (detect == null) return;
            CheckIframeChainPlaceholders(detect.Iframe, context);
            if (detect.All != null) foreach (var c in detect.All) CheckDetectPlaceholders(c, context);
            if (detect.Any != null) foreach (var c in detect.Any) CheckDetectPlaceholders(c, context);
            CheckDetectPlaceholders(detect.Not, context);
        }

        void WalkFallback(StepFallback? fb, string context)
        {
            if (fb == null) return;
            CheckIframeChainPlaceholders(fb.Iframe, context);
            WalkFallback(fb.Fallback, context);
        }

        void WalkItems(List<PhaseItem> items)
        {
            foreach (var item in items)
            {
                if (item is StepNode step)
                {
                    CheckStep(step);
                    CheckDetectPlaceholders(step.Detect, $"step detect: {step.Action}");
                    CheckDetectPlaceholders(step.Until, $"step until: {step.Action}");
                    CheckDetectPlaceholders(step.Condition, $"step condition: {step.Action}");
                    WalkFallback(step.Fallback, $"step fallback: {step.Action}");
                }
                if (item is PhaseNode phase)
                {
                    CheckIframeChainPlaceholders(phase.Iframe, $"phase: {phase.Name}");
                    CheckDetectPlaceholders(phase.Condition, $"phase condition: {phase.Name}");
                    CheckDetectPlaceholders(phase.LoopCondition, $"phase loopCondition: {phase.Name}");
                    if (phase.Steps.Count > 0) WalkItems(phase.Steps);
                }
            }
        }

        WalkItems(script.Phases);
    }

    private static List<string> ExtractPlaceholders(StepNode step)
    {
        var result = new List<string>();
        var sources = new[] { step.Description, step.Selector, step.Value, step.Url, step.Code, step.FilePath };
        foreach (var src in sources)
        {
            if (src == null) continue;
            foreach (Match m in Regex.Matches(src, @"\{\{(\w+(?:\.\w+)*)\}\}"))
                result.Add(m.Groups[1].Value);
        }
        // 形态 A：iframe selector 链每层支持 {{}}（人工写的 rowIndex/字段名/变量）
        if (step.Iframe != null)
        {
            foreach (var layer in step.Iframe)
            {
                if (string.IsNullOrEmpty(layer)) continue;
                foreach (Match m in Regex.Matches(layer, @"\{\{(\w+(?:\.\w+)*)\}\}"))
                    result.Add(m.Groups[1].Value);
            }
        }
        return result;
    }

    /// <summary>
    /// F4(R1-3)：递归收集全脚本所有 step 的 storeAs 目标变量名，供 ValidatePlaceholders 把 storeAs 变量并入合法占位符集合 + ④-8 命名冲突校验。
    /// ④-8：改 public 供录制层（App RecordingEngine）撞名检查复用。
    /// </summary>
    public static HashSet<string> CollectAllStoreAsNames(List<PhaseItem> items)
    {
        var names = new HashSet<string>();
        void Walk(List<PhaseItem> list)
        {
            foreach (var item in list)
            {
                if (item is StepNode step && step.StoreAs != null)
                    foreach (var n in ExtractStepStoreAsNames(step))
                        names.Add(n);
                if (item is PhaseNode phase && phase.Steps.Count > 0)
                    Walk(phase.Steps);
            }
        }
        Walk(items);
        return names;
    }

    /// <summary>④-8：提取单步 storeAs 目标变量名（string / JsonElement string / JsonElement object 的 key），抽自 CollectAllStoreAsNames 供录制层复用。</summary>
    public static List<string> ExtractStepStoreAsNames(StepNode step)
    {
        var names = new List<string>();
        if (step.StoreAs == null) return names;
        if (step.StoreAs is string s)
            names.Add(s);
        else if (step.StoreAs is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.String)
                names.Add(je.GetString() ?? "");
            else if (je.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in je.EnumerateObject())
                    names.Add(prop.Name);
            }
        }
        return names;
    }

    /// <summary>系统保留变量名（④-8：补 lastError；改 public 供录制层复用；FrozenSet 不可变防误改）。
    /// 字段名/storeAs 名禁用这些——字段名进 fillData（scopeChain 末层）会永久遮蔽系统变量；storeAs 名撞这些会遮蔽引擎写入的系统变量。</summary>
    public static readonly FrozenSet<string> ReservedVarNames = new[] { "rowIndex", "_lastUrl", "_lastRowCount", "lastError" }.ToFrozenSet();

    /// <summary>DEC-7（2026-07-13）：系统凭据字段名（username/password）——Worker fillData 硬编码这两个 key 存 payload 传来的用户名/解密密码
    /// （见 AutomationWsClient.BuildFillDataAndDownloadAttachmentsAsync：fillData["username"]=payload.Username / fillData["password"]=decryptedPassword）。
    /// 规则：① fields 用这两个名时必须 source=system（否则收集层/录制行为与 Worker 硬编码 fillData 不一致，{{username}}/{{password}} 取错值，silent）；
    /// ② storeAs 名禁用这两个（storeAs 写 vars，但 fillData 系统凭据在 scopeChain 末层优先于 vars → storeAs 被永久遮蔽取错值，silent）。
    /// **不并入 ReservedVarNames**——那会禁止 fields 用这俩名，破坏 {{username}}/{{password}} 正常用法（fields name=username source=system 是合法的、是登录场景标准用法）。
    /// 改 public 供录制层（App RecordingEngine）实时反馈复用（两层规则一致：Y 修正→X 复查通过，不双报错）。</summary>
    public static readonly FrozenSet<string> SystemCredentialKeys = new[] { "username", "password" }.ToFrozenSet();
    private static readonly Regex ValidVarNameRegex = new(@"^\w+$", RegexOptions.Compiled);
    private static readonly HashSet<string> NoStoreAsActions = new()
        { "hover", "pressKey", "scroll", "upload", "goBack", "reload", "closeTab", "wait", "check", "captcha", "goto" };

    private static void ValidateStoreAs(List<PhaseItem> items, List<string> errors)
    {
        foreach (var item in items)
        {
            if (item is StepNode step)
            {
                // 1. screenshot 特殊校验（必须在 StoreAs 判断之前）
                if (step.Action == "screenshot")
                {
                    // saveToFile=false 但没有 storeAs → 无意义
                    if (step.SaveToFile == false && step.StoreAs == null)
                        errors.Add($"screenshot saveToFile=false 时必须指定 storeAs (step: {step.Name ?? step.Action})");
                    // 有 storeContent 但没有 storeAs → storeContent 无意义
                    if (step.StoreAs == null && step.StoreContent != null)
                        errors.Add($"screenshot storeContent 需要 storeAs (step: {step.Name ?? step.Action})");
                }

                // 以下校验只在 StoreAs != null 时执行
                if (step.StoreAs != null)
                {
                    // 2. 不支持 storeAs 的 action
                    if (NoStoreAsActions.Contains(step.Action))
                        errors.Add($"action '{step.Action}' 不支持 storeAs (step: {step.Action} {step.Selector})");

                    // 3. ai action 必须是 object 格式
                    if (step.Action == "ai")
                    {
                        if (step.StoreAs is string || (step.StoreAs is JsonElement je2 && je2.ValueKind == JsonValueKind.String))
                            errors.Add($"ai action 的 storeAs 必须是 object 格式 {{ \"变量名\": \"描述\" }}，不能是字符串 (step: {step.Action})");
                    }

                    // 4. screenshot storeContent 与 saveToFile 兼容性校验
                    if (step.Action == "screenshot")
                    {
                        if (step.SaveToFile == false && step.StoreContent is null or "path" or "both")
                            errors.Add($"screenshot saveToFile=false 时 storeContent 只能为 dataUrl (step: {step.Name ?? step.Action})");
                    }

                    // 5. 原有变量名合法性校验
                    if (step.StoreAs is string s)
                        ValidateSingleVarName(s, step, errors);
                    else if (step.StoreAs is JsonElement je)
                    {
                        if (je.ValueKind == JsonValueKind.String)
                            ValidateSingleVarName(je.GetString() ?? "", step, errors);
                        else if (je.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var prop in je.EnumerateObject())
                                ValidateSingleVarName(prop.Name, step, errors);
                        }
                    }
                }
            }
            if (item is PhaseNode phase && phase.Steps.Count > 0)
                ValidateStoreAs(phase.Steps, errors);
        }
    }

    private static void ValidateSingleVarName(string name, StepNode step, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(name))
            errors.Add($"storeAs 变量名不能为空 (step: {step.Action} {step.Selector})");
        else if (!ValidVarNameRegex.IsMatch(name))
            errors.Add($"storeAs 变量名 '{name}' 不合法，只允许字母/数字/下划线 (step: {step.Action} {step.Selector})");
        else if (ReservedVarNames.Contains(name))
            errors.Add($"storeAs 变量名 '{name}' 与系统保留变量冲突 (step: {step.Action} {step.Selector})");
        else if (SystemCredentialKeys.Contains(name))  // DEC-7：storeAs 名禁用 username/password（fillData 系统凭据在 scopeChain 末层优先于 vars → storeAs 被永久遮蔽取错值）
            errors.Add($"storeAs 变量名 '{name}' 是系统凭据字段名（会被 fillData 的 username/password 系统凭据永久遮蔽，静默取错值），请换名 (step: {step.Action} {step.Selector})");
    }

    /// <summary>
    /// 递归收集字段 name，用于唯一性校验（含嵌套 field.Fields）。
    /// </summary>
    private static void CollectFieldNames(List<FieldDefinition> fields, HashSet<string> names, List<string> errors)
    {
        foreach (var field in fields)
        {
            if (!names.Add(field.Name))
                errors.Add($"字段 name 重复: {field.Name}");
            if (field.Fields is { Count: > 0 })
                CollectFieldNames(field.Fields, names, errors);
        }
    }

    /// <summary>
    /// 递归收集所有层级字段名（仅名称，用于占位符引用校验）。
    /// </summary>
    private static void CollectFieldDefinitions(List<FieldDefinition> fields, HashSet<string> names)
    {
        foreach (var field in fields)
        {
            names.Add(field.Name);
            // 批次7-loop（28 golden 暴露的 latent bug 修复）：loopSource=type=file 字段（如 attachments）的 loop rowData
            // 是附件对象 {name,path,url}（ConvertAttachmentUrls 产出），{{path}}/{{name}}/{{url}} 取当前行附件属性
            // （回放期 scopeChain 内层 rowData 命中）。原 ValidatePlaceholders 只放行顶层 field 名，拒绝 {{path}} →
            // 录制产出的 loopSource=attachments upload 脚本（filePath={{path}}）回放期 LoadFromJson 加载失败 400。
            // 对 type=file 字段补附件子字段 name/path/url 为合法占位符（加载放行；回放期 scopeChain 解析）。
            if (field.Type == "file")
            {
                names.Add("path");
                names.Add("name");
                names.Add("url");
            }
            if (field.Fields is { Count: > 0 })
                CollectFieldDefinitions(field.Fields, names);
        }
    }

    /// <summary>
    /// 形态 A：iframe selector 链校验（自包含，无 Id 引用）。
    /// step.Iframe/phase.Iframe/detect.Iframe/fallback.Iframe 若非空，每项须为非空合法 selector 字符串。
    /// </summary>
    private static void ValidateIframeChains(ScriptV2 script, List<string> errors)
    {
        void CheckChain(string[]? chain, string context)
        {
            if (chain == null) return;
            for (int i = 0; i < chain.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(chain[i]))
                    errors.Add($"iframe selector 链第 {i + 1} 层为空字符串（{context}）——每层须为非空 XPath 或 CSS selector");
            }
        }

        void CheckDetect(DetectCondition? detect, string context)
        {
            if (detect == null) return;
            CheckChain(detect.Iframe, context);
            if (detect.All != null) foreach (var c in detect.All) CheckDetect(c, context);
            if (detect.Any != null) foreach (var c in detect.Any) CheckDetect(c, context);
            CheckDetect(detect.Not, context);
        }

        void WalkFallback(StepFallback? fb, string context)
        {
            if (fb == null) return;
            CheckChain(fb.Iframe, context);
            if (fb.Fallback != null) WalkFallback(fb.Fallback, context);
        }

        void WalkItems(List<PhaseItem> items)
        {
            foreach (var item in items)
            {
                if (item is StepNode step)
                {
                    CheckChain(step.Iframe, $"step: {step.Action} {step.Selector}");
                    CheckDetect(step.Detect, $"step detect: {step.Action} {step.Selector}");
                    CheckDetect(step.Until, $"step until: {step.Action} {step.Selector}");
                    CheckDetect(step.Condition, $"step condition: {step.Action} {step.Selector}");
                    WalkFallback(step.Fallback, $"step fallback: {step.Action} {step.Selector}");
                }
                if (item is PhaseNode phase)
                {
                    CheckChain(phase.Iframe, $"phase: {phase.Name}");
                    CheckDetect(phase.Condition, $"phase condition: {phase.Name}");
                    CheckDetect(phase.LoopCondition, $"phase loopCondition: {phase.Name}");
                    if (phase.Steps.Count > 0) WalkItems(phase.Steps);
                }
            }
        }

        WalkItems(script.Phases);
    }

    /// <summary>
    /// F：selector 非法字符校验（加载期）。扫描所有"会被 Playwright 当 selector 解析"的字符串，
    /// 检测中文弯引号（U+2018/2019/201C/201D）+ 全角空格（U+3000）——合法 selector 绝不会用，命中即报错。
    /// 把"回放才崩"（Playwright document.evaluate 抛 not a valid XPath expression）提前到"加载就拒"。
    /// 在变量替换前扫原始字符串（{{vars}} 占位不影响——弯引号错在字面量部分，不在占位符内）。
    /// 字符集保持最小：不校验中文逗号/括号/冒号（可能合法出现在 :has-text('中文文本') 参数里，查了误报）。
    /// 校验点：StepNode.Selector/Iframe/captcha5；DetectCondition.Selector/Iframe（递归 all/any/not）；StepFallback.Selector/Iframe（递归 fallback）；PhaseNode.Iframe。
    /// 与录制期 a-a-4 ValidateSelectorAsync 互补：F 兜底防"保存后手动编辑"引入弯引号（录制期校验管不到保存后编辑）。
    /// </summary>
    private static void ValidateSelectorCharset(ScriptV2 script, List<string> errors)
    {
        void CheckSelector(string? sel, string context)
        {
            if (string.IsNullOrEmpty(sel)) return;
            if (TryFindIllegalCharsetChar(sel, out var ch, out var codePoint))
                errors.Add($"selector 含非法字符 {ch}（U+{codePoint:X4}）——{context}，片段 \"{TruncateForMsg(sel)}\"——请改用 ASCII 直引号/半角空格（XPath/CSS 只认 ASCII）");
        }

        void CheckChain(string[]? chain, string context)
        {
            if (chain == null) return;
            for (int i = 0; i < chain.Length; i++)
                CheckSelector(chain[i], $"{context} iframe 链第 {i + 1} 层");
        }

        void CheckDetect(DetectCondition? detect, string context)
        {
            if (detect == null) return;
            CheckSelector(detect.Selector, context);
            CheckChain(detect.Iframe, context);
            if (detect.All != null) foreach (var c in detect.All) CheckDetect(c, context);
            if (detect.Any != null) foreach (var c in detect.Any) CheckDetect(c, context);
            CheckDetect(detect.Not, context);
        }

        void WalkFallback(StepFallback? fb, string context)
        {
            if (fb == null) return;
            CheckSelector(fb.Selector, context);
            CheckChain(fb.Iframe, context);
            if (fb.Fallback != null) WalkFallback(fb.Fallback, context);
        }

        void WalkItems(List<PhaseItem> items)
        {
            foreach (var item in items)
            {
                if (item is StepNode step)
                {
                    var stepCtx = $"step={step.Name ?? step.Action}";
                    CheckSelector(step.Selector, stepCtx);
                    CheckChain(step.Iframe, stepCtx);
                    // captcha 5 选择器（名字不带 Selector 后缀但本质是 selector，易漏）
                    CheckSelector(step.ImageSelector, $"{stepCtx} captcha imageSelector");
                    CheckSelector(step.InputSelector, $"{stepCtx} captcha inputSelector");
                    CheckSelector(step.SliderSelector, $"{stepCtx} captcha sliderSelector");
                    CheckSelector(step.TargetSelector, $"{stepCtx} captcha targetSelector");
                    CheckSelector(step.BackgroundSelector, $"{stepCtx} captcha backgroundSelector");
                    CheckDetect(step.Detect, $"{stepCtx} detect");
                    CheckDetect(step.Until, $"{stepCtx} until");
                    CheckDetect(step.Condition, $"{stepCtx} condition");
                    WalkFallback(step.Fallback, $"{stepCtx} fallback");
                }
                if (item is PhaseNode phase)
                {
                    CheckChain(phase.Iframe, $"phase={phase.Name}");
                    CheckDetect(phase.Condition, $"phase={phase.Name} condition");
                    CheckDetect(phase.LoopCondition, $"phase={phase.Name} loopCondition");
                    if (phase.Steps.Count > 0) WalkItems(phase.Steps);
                }
            }
        }

        WalkItems(script.Phases);
    }

    /// <summary>F：检测 selector 中的非法字符（弯引号 U+2018/2019/201C/201D + 全角空格 U+3000）。返回首个命中字符。</summary>
    private static bool TryFindIllegalCharsetChar(string s, out char ch, out int codePoint)
    {
        foreach (var c in s)
        {
            if (c == '‘' || c == '’' || c == '“' || c == '”' || c == '　')
            {
                ch = c;
                codePoint = c;
                return true;
            }
        }
        ch = '\0';
        codePoint = 0;
        return false;
    }

    /// <summary>F：截断 selector 片段用于错误消息（避免超长 selector 刷屏）。</summary>
    private static string TruncateForMsg(string s) => s.Length <= 80 ? s : s[..80] + "...";

    /// <summary>
    /// R4 增强1：detect 专属 check、until 专属 wait 绑定校验（仿 ValidateThenUsage）。
    /// 当前 fill step 带 detect/until schema 合法但 silent 无效（fill 不读 detect/until），加载期拦截。
    /// </summary>
    private static void ValidateDetectUntilUsage(List<PhaseItem> items, List<string> errors)
    {
        foreach (var item in items)
        {
            if (item is StepNode step)
            {
                if (step.Detect != null && step.Action != "check")
                    errors.Add($"detect 只能在 action=check 的步骤上使用（当前 action={step.Action}, step={step.Name ?? step.Action}）");
                if (step.Until != null && step.Action != "wait")
                    errors.Add($"until 只能在 action=wait 的步骤上使用（当前 action={step.Action}, step={step.Name ?? step.Action}）");
                // R4 增强2：new_row_appears 位置约束（依赖 tableFrame 基线机制，仅 check step detect / step.condition 合法）
                if (step.Until != null && ContainsNewRowAppears(step.Until))
                    errors.Add($"new_row_appears 不能用于 step.until（wait 无 tableFrame 基线机制，step={step.Name ?? step.Action}）");
                if (step.Condition != null && ContainsNewRowAppears(step.Condition))
                {
                    // step.condition 允许 new_row_appears（位置3，罕见但合法），不报错
                }
            }
            if (item is PhaseNode phase)
            {
                if (phase.Condition != null && ContainsNewRowAppears(phase.Condition))
                    errors.Add($"new_row_appears 不能用于 phase.condition「{phase.Name}」（无 tableFrame 基线机制，第一轮 _lastRowCount 未设）");
                if (phase.LoopCondition != null && ContainsNewRowAppears(phase.LoopCondition))
                    errors.Add($"new_row_appears 不能用于 phase.loopCondition「{phase.Name}」（loop 每轮评估，_lastRowCount 语义崩）");
                if (phase.Steps.Count > 0) ValidateDetectUntilUsage(phase.Steps, errors);
            }
        }
    }

    /// <summary>R4 增强2：递归检测 detect 条件树中是否含 new_row_appears（all/any/not 嵌套）。</summary>
    private static bool ContainsNewRowAppears(DetectCondition? detect)
    {
        if (detect == null) return false;
        if (detect.Type == "new_row_appears") return true;
        if (detect.All != null) { foreach (var c in detect.All) if (ContainsNewRowAppears(c)) return true; }
        if (detect.Any != null) { foreach (var c in detect.Any) if (ContainsNewRowAppears(c)) return true; }
        if (detect.Not != null && ContainsNewRowAppears(detect.Not)) return true;
        return false;
    }

    /// <summary>DC11 硬校验辅助：递归收集 detect 条件树中所有 new_row_appears 节点（all/any/not 嵌套）。</summary>
    private static void CollectNewRowAppearsInTree(DetectCondition? d, List<(DetectCondition Node, string Location)> nodes, string loc)
    {
        if (d == null) return;
        if (d.Type == "new_row_appears") nodes.Add((d, loc));
        if (d.All != null) foreach (var c in d.All) CollectNewRowAppearsInTree(c, nodes, loc);
        if (d.Any != null) foreach (var c in d.Any) CollectNewRowAppearsInTree(c, nodes, loc);
        if (d.Not != null) CollectNewRowAppearsInTree(d.Not, nodes, loc);
    }

    /// <summary>DC11 硬校验：loop phase 内多个 new_row_appears 须同 selector+iframe（基线语义须一致，否则 TryFind 取第一个 + 其他不一致 silent 误判）。</summary>
    private static void ValidateNewRowAppearsBaselineConsistency(List<PhaseItem> items, List<string> errors)
    {
        foreach (var item in items)
        {
            if (item is PhaseNode phase)
            {
                if (phase.Type == "loop" && phase.Steps.Count > 0)
                {
                    var nodes = new List<(DetectCondition Node, string Location)>();
                    foreach (var s in phase.Steps.OfType<StepNode>())
                    {
                        if (s.Action == "check" && s.Detect != null)
                            CollectNewRowAppearsInTree(s.Detect, nodes, $"step「{s.Name ?? s.Action}」check.Detect");
                        if (s.Condition != null)
                            CollectNewRowAppearsInTree(s.Condition, nodes, $"step「{s.Name ?? s.Action}」Condition");
                    }
                    if (nodes.Count >= 2)
                    {
                        var first = nodes[0].Node;
                        var firstSel = first.Selector ?? "";
                        var firstIframe = first.Iframe != null ? string.Join(">", first.Iframe) : "";
                        for (int i = 1; i < nodes.Count; i++)
                        {
                            var n = nodes[i].Node;
                            var nSel = n.Selector ?? "";
                            var nIframe = n.Iframe != null ? string.Join(">", n.Iframe) : "";
                            if (nSel != firstSel || nIframe != firstIframe)
                                errors.Add($"loop phase「{phase.Name}」内多个 new_row_appears 须同 selector+iframe（DC11 硬校验）：{nodes[0].Location} selector=「{firstSel}」iframe=「{firstIframe}」 vs {nodes[i].Location} selector=「{nSel}」iframe=「{nIframe}」（基线语义须一致，否则 silent 误判）");
                        }
                    }
                }
                if (phase.Steps.Count > 0) ValidateNewRowAppearsBaselineConsistency(phase.Steps, errors);
            }
        }
    }

    /// <summary>
    /// ai phase 结构校验（🟢-6：schema 已 required action，此处为业务层双保险）：不允许嵌套子 phase + action 必须为 ai
    /// </summary>
    private static void ValidateAiPhaseStructure(List<PhaseItem> items, List<string> errors)
    {
        foreach (var item in items)
        {
            if (item is PhaseNode phase)
            {
                if (phase.Type == "ai")
                {
                    foreach (var child in phase.Steps)
                    {
                        if (child is PhaseNode nestedPhase)
                            errors.Add($"ai phase '{phase.Name}' 下不允许嵌套子 phase '{nestedPhase.Name}'");
                        if (child is StepNode step && step.Action != "ai")
                            errors.Add($"ai phase '{phase.Name}' 下步骤 action 必须为 'ai'，当前为 '{step.Action ?? "null"}'");
                    }
                }
                // 递归检查非 ai phase 的子 phase
                if (phase.Type != "ai" && phase.Steps.Count > 0)
                    ValidateAiPhaseStructure(phase.Steps, errors);
            }
        }
    }

    /// <summary>R2-6：goto toPhase/toStep 目标存在性 load 时校验（原仅 run-time 抛 ScriptEngine L714，提早到加载期发现）。覆盖 action==goto 与 check+then==goto 两类跳转。</summary>
    private static void ValidateGotoTargets(List<PhaseItem> phases, HashSet<string> phaseNames, List<string> errors)
    {
        void CheckPhase(PhaseNode phase)
        {
            // 收集当前 phase 直接子 step 名（goto toStep 只查直接 steps）
            var stepNames = new HashSet<string>();
            foreach (var item in phase.Steps)
                if (item is StepNode s && s.Name != null) stepNames.Add(s.Name);

            foreach (var item in phase.Steps)
            {
                if (item is StepNode step && (step.Action == "goto" || (step.Action == "check" && step.Then == "goto")))
                {
                    if (!string.IsNullOrEmpty(step.ToPhase) && !phaseNames.Contains(step.ToPhase))
                        errors.Add($"goto toPhase '{step.ToPhase}' 不存在 (phase: {phase.Name}, step: {step.Name ?? step.Action})");
                    if (!string.IsNullOrEmpty(step.ToStep) && !stepNames.Contains(step.ToStep))
                        errors.Add($"goto toStep '{step.ToStep}' 在当前 phase 内不存在 (phase: {phase.Name}, step: {step.Name ?? step.Action})");
                }
                if (item is PhaseNode nested && nested.Steps.Count > 0)
                    CheckPhase(nested);
            }
        }

        foreach (var item in phases)
            if (item is PhaseNode p && p.Steps.Count > 0) CheckPhase(p);
    }

    /// <summary>R3-3：type=array 字段须有 items（简单值列表）或 fields（结构化行）之一，否则数组元素结构不明。</summary>
    private static void ValidateArrayFields(List<FieldDefinition> fields, List<string> errors)
    {
        foreach (var field in fields)
        {
            if (field.Type == "array" && field.Items == null && (field.Fields == null || field.Fields.Count == 0))
                errors.Add($"type=array 字段 '{field.Name}' 须配置 items（简单值列表）或 fields（结构化行）之一");
            if (field.Fields is { Count: > 0 })
                ValidateArrayFields(field.Fields, errors);
        }
    }

    /// <summary>R3-4：extractType=property（默认/空）时 property 必填（运行时 GetAttributeAsync(step.Property) 空串取不到属性）。</summary>
    private static void ValidateExtractProperty(List<PhaseItem> items, List<string> errors)
    {
        foreach (var item in items)
        {
            if (item is StepNode step && step.Action == "extract")
            {
                var et = string.IsNullOrEmpty(step.ExtractType) ? "property" : step.ExtractType;
                if (et == "property" && string.IsNullOrEmpty(step.Property))
                    errors.Add($"extract extractType=property 时必须指定 property 属性名 (step: {step.Name ?? step.Action})");
            }
            if (item is PhaseNode phase && phase.Steps.Count > 0)
                ValidateExtractProperty(phase.Steps, errors);
        }
    }

    /// <summary>批次9④：scroll selector 与 direction 严格二选一（同传/都不传都报错，对齐回放③ + schema⑤）。</summary>
    private static void ValidateScrollExclusive(List<PhaseItem> items, List<string> errors)
    {
        foreach (var item in items)
        {
            if (item is StepNode step && step.Action == "scroll")
            {
                var hasSelector = !string.IsNullOrEmpty(step.Selector);
                var hasDirection = !string.IsNullOrEmpty(step.Direction);
                if (hasSelector && hasDirection)
                    errors.Add($"scroll selector 与 direction 互斥，不能同时指定 (step: {step.Name ?? step.Action})");
                else if (!hasSelector && !hasDirection)
                    errors.Add($"scroll 需指定 selector（滚到元素）或 direction（页面滚动）之一 (step: {step.Name ?? step.Action})");
            }
            if (item is PhaseNode phase && phase.Steps.Count > 0)
                ValidateScrollExclusive(phase.Steps, errors);
        }
    }

    /// <summary>R4-1：遍历脚本所有 detect 点（step detect/until/condition + phase condition/loopCondition）校验必填参数。</summary>
    private static void ValidateDetectParamsAll(ScriptV2 script, List<string> errors)
    {
        void Walk(List<PhaseItem> items)
        {
            foreach (var item in items)
            {
                if (item is StepNode step)
                {
                    ValidateDetectParams(step.Detect, $"step detect: {step.Action} {step.Selector}", errors);
                    ValidateDetectParams(step.Until, $"step until: {step.Action} {step.Selector}", errors);
                    ValidateDetectParams(step.Condition, $"step condition: {step.Action} {step.Selector}", errors);
                }
                if (item is PhaseNode phase)
                {
                    ValidateDetectParams(phase.Condition, $"phase condition: {phase.Name}", errors);
                    ValidateDetectParams(phase.LoopCondition, $"phase loopCondition: {phase.Name}", errors);
                    if (phase.Steps.Count > 0) Walk(phase.Steps);
                }
            }
        }
        Walk(script.Phases);
    }

    /// <summary>
    /// R4-1：detect 各 type 必填参数校验。原仅运行时 silent false（selector_count 例外告警），load 时拦可提早发现配置错误。
    /// DC9（a-a-4）：new_row_appears selector 必填（数行基线，不能用默认 table tbody tr——grid 场景 silent 数 0）。
    /// document_ready iframe 可省略（决策2-A：null=继承 phase.iframe 合法）；url_changed/page_exists/always 无必填参数。
    /// </summary>
    private static void ValidateDetectParams(DetectCondition? detect, string context, List<string> errors)
    {
        if (detect == null) return;
        if (detect.All != null) { foreach (var c in detect.All) ValidateDetectParams(c, context, errors); return; }
        if (detect.Any != null) { foreach (var c in detect.Any) ValidateDetectParams(c, context, errors); return; }
        if (detect.Not != null) { ValidateDetectParams(detect.Not, context, errors); return; }

        var type = detect.Type;
        if (string.IsNullOrEmpty(type)) return;

        bool Missing(string? v) => string.IsNullOrEmpty(v);
        switch (type)
        {
            case "selector_visible":
            case "selector_exists":
            case "selector_gone":
            case "selector_enabled":
            case "selector_checked":
                if (Missing(detect.Selector)) errors.Add($"detect '{type}' 须指定 selector ({context})");
                break;
            case "selector_value":
            case "selector_selected":
                if (Missing(detect.Selector)) errors.Add($"detect '{type}' 须指定 selector ({context})");
                if (Missing(detect.Value)) errors.Add($"detect '{type}' 须指定 value ({context})");
                break;
            case "selector_text":
                if (Missing(detect.Selector)) errors.Add($"detect 'selector_text' 须指定 selector ({context})");
                if (detect.Keywords == null || detect.Keywords.Length == 0) errors.Add($"detect 'selector_text' 须指定 keywords ({context})");
                break;
            case "selector_count":
                if (Missing(detect.Selector)) errors.Add($"detect 'selector_count' 须指定 selector ({context})");
                if (!detect.Count.HasValue) errors.Add($"detect 'selector_count' 须指定 count ({context})");
                break;
            case "page_contains":
            case "dialog_contains":
                if (detect.Keywords == null || detect.Keywords.Length == 0) errors.Add($"detect '{type}' 须指定 keywords ({context})");
                break;
            case "js":
                if (Missing(detect.Check)) errors.Add($"detect 'js' 须指定 check 表达式 ({context})");
                break;
            case "data_exists":
                if (Missing(detect.Field)) errors.Add($"detect 'data_exists' 须指定 field ({context})");
                break;
            case "url_contains":
                if (Missing(detect.Value)) errors.Add($"detect 'url_contains' 须指定 value ({context})");
                break;
            case "iframe_exists":
                if (Missing(detect.Selector)) errors.Add($"detect 'iframe_exists' 须指定 selector（要检查的 iframe 元素，{context}）");
                break;
            case "new_row_appears":  // DC9：数行基线 selector 必填（默认 table tbody tr 在 grid 场景 silent 数 0；schema detectConditionFull allOf 同步强制）
                if (Missing(detect.Selector)) errors.Add($"detect 'new_row_appears' 须显式指定 selector（数行的基线，不能用默认 table tbody tr——grid 场景会 silent 数 0）({context})");
                break;
            case "document_ready":  // 决策2-A（a-a-4）：iframe 可省略——null=继承 phase.iframe（现状恒 null→顶层；缺陷修复后=phase iframe，两种都合理）；[链]=指定 iframe 走 detect 路径校验
                break;
        }
    }

    /// <summary>R4-2：type=file 时 uiComponent 限 {upload,hidden}（强制显式配，消除 file+null→input 兜底不搭；hidden=系统附件）。</summary>
    private static void ValidateFileUiComponent(List<FieldDefinition> fields, List<string> errors)
    {
        foreach (var field in fields)
        {
            if (field.Type == "file")
            {
                var ui = field.UiComponent;
                if (ui != "upload" && ui != "hidden")
                    errors.Add($"type=file 字段 '{field.Name}' 的 uiComponent 必须为 upload 或 hidden（当前: {ui ?? "null"}）");
            }
            if (field.Fields is { Count: > 0 })
                ValidateFileUiComponent(field.Fields, errors);
        }
    }

    /// <summary>
    /// DEC-7 X（2026-07-13）：系统凭据字段（name∈SystemCredentialKeys，即 username/password）的 source 必须为 system。
    /// Worker fillData 硬编码 fillData["username"]=payload.Username / fillData["password"]=decryptedPassword（见 AutomationWsClient.BuildFillDataAndDownloadAttachmentsAsync），
    /// 故 fields 用这两个名时必须是 source=system（系统注入，非用户收集）——否则 source=user/computed 的同名字段会被 Worker 硬编码值覆盖（或反向：收集层/录制行为与 Worker 不一致），
    /// {{username}}/{{password}} 取错值（silent）。递归遍历 script.Fields 含 field.Fields 嵌套（仿 ValidateFileUiComponent 递归模式；嵌套子字段同样约束）。
    /// 触发路径：App SaveScript 保存 + Worker FetchScripts 加载 + 手工 LoadFromJson（全路径，X 加在 ValidateAndGetErrors 覆盖所有）。
    /// </summary>
    private static void ValidateSystemCredentialFields(List<FieldDefinition> fields, List<string> errors)
    {
        foreach (var field in fields)
        {
            if (SystemCredentialKeys.Contains(field.Name) && field.Source != "system")
                errors.Add($"系统凭据字段 '{field.Name}' 的 source 必须为 system（当前: {field.Source ?? "null"}）——Worker fillData 硬编码该 key 存系统凭据，source≠system 会致 {{{{username}}}}/{{{{password}}}} 取错值");
            if (field.Fields is { Count: > 0 })
                ValidateSystemCredentialFields(field.Fields, errors);
        }
    }

    /// <summary>
    /// R3-2：所有 phase（含嵌套）aiGoal 严格必填。aiGoal 是 phase 业务目标，用于 AI fallback/ai phase；
    /// 缺失→加载失败（用户决策：不兜底、不掩盖不完整脚本，人工介入）。
    /// </summary>
    private static void ValidatePhaseAiGoal(List<PhaseItem> items, List<string> errors)
    {
        foreach (var item in items)
        {
            if (item is PhaseNode phase)
            {
                if (string.IsNullOrEmpty(phase.AiGoal))
                    errors.Add($"phase '{phase.Name}' 缺少 aiGoal（所有 phase 必填：phase 业务目标，AI fallback/ai phase 使用）");
                if (phase.Steps.Count > 0)
                    ValidatePhaseAiGoal(phase.Steps, errors);
            }
        }
    }

    /// <summary>
    /// F2 + §7.3：captcha selector 完整性校验。
    /// pixel 必填 sliderSelector+targetSelector+backgroundSelector（slide_comparison 契约需 target 不带缺口完整图 + background 带缺口图）；
    /// click 必填 imageSelector（验证码大图），targetSelector 可选（提供走路径 A 读提示，不提供走路径 B 图内顶部行兜底）。
    /// </summary>
    private static void ValidateCaptcha(List<PhaseItem> items, List<string> errors)
    {
        foreach (var item in items)
        {
            if (item is StepNode step && step.Action == "captcha")
            {
                switch (step.CaptchaType)
                {
                    case "pixel":
                        if (string.IsNullOrEmpty(step.SliderSelector)) errors.Add($"captcha pixel 需要 sliderSelector (step: {step.Name ?? step.Action})");
                        if (string.IsNullOrEmpty(step.TargetSelector)) errors.Add($"captcha pixel 需要 targetSelector（不带缺口的完整背景图）(step: {step.Name ?? step.Action})");
                        if (string.IsNullOrEmpty(step.BackgroundSelector)) errors.Add($"captcha pixel 需要 backgroundSelector（带缺口的背景图）(step: {step.Name ?? step.Action})");
                        break;
                    case "click":
                        if (string.IsNullOrEmpty(step.ImageSelector)) errors.Add($"captcha click 需要 imageSelector（验证码大图）(step: {step.Name ?? step.Action})");
                        break;
                }
            }
            if (item is PhaseNode phase && phase.Steps.Count > 0)
                ValidateCaptcha(phase.Steps, errors);
        }
    }
}
