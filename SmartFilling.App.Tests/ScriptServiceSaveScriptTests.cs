using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using SmartFilling.App.Configuration;
using SmartFilling.App.Controllers;
using SmartFilling.App.Models;
using SmartFilling.App.Services;
using SmartFilling.Engine.Engine;
using SmartFilling.Engine.Models;

namespace SmartFilling.App.Tests;

/// <summary>
/// ScriptService.SaveScript forceSave + GetAllScripts 读回兼容测试（强制保存计划 A/B 块）。
/// 验证：forceSave=false 校验失败抛 ScriptValidationException（承载错误列表）/ forceSave=true 跳校验写文件 /
/// 脏关联修复（校验失败不mutate documents.json）/ GetAllScripts 带病读回 HasErrors + 结构坏跳过 LogWarning。
/// 经生产反序列化路径构造（DeserializeOnly）覆盖 PhaseItemConverter；断言验值不只验状态。
/// </summary>
public class ScriptServiceSaveScriptTests
{
    /// <summary>可捕获日志的 ILogger 实现（替代 Moq 验证 ILogger.Log 泛型 matcher 的脆弱性，直接读 Logs 列表验可观测）。</summary>
    private class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel level, string message)> Logs = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Logs.Add((logLevel, formatter(state, exception)));
        private class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
    }

    private static (ScriptService service, string scriptsDir, string documentsFile, CapturingLogger<ScriptService> logger) Create()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SmartFillingSaveTest_" + Guid.NewGuid().ToString("N")[..8]);
        var mockEnv = Substitute.For<IWebHostEnvironment>();
        mockEnv.ContentRootPath.Returns(tempDir);
        var logger = new CapturingLogger<ScriptService>();
        // 🔴难点2：测试 lenientNull 与生产对齐——RecordOutputAllFields=true（生产 appsettings.json:60=true），
        // 否则测试在 false（严格）下绿、生产 true（StripNulls 宽松）下行为不同，掩盖生产真实 lenientNull 行为。
        var service = new ScriptService(mockEnv, Options.Create(new AppOptions { RecordOutputAllFields = true }), logger);
        return (service, Path.Combine(tempDir, "data", "scripts"), Path.Combine(tempDir, "data", "documents.json"), logger);
    }

    /// <summary>forceSave=false + 缺 aiGoal → ScriptValidationException，Errors 含 aiGoal 项（验值），文件不写入。</summary>
    [Fact]
    public void SaveScript_ForceSaveFalse_MissingAiGoal_ThrowsWithAiGoalError()
    {
        var (service, scriptsDir, _, _) = Create();
        var script = new ScriptV2
        {
            ScriptId = "test-missing-aigoal",
            Name = "缺aiGoal测试",
            Phases = [new PhaseNode { Kind = "phase", Name = "main", Type = "sequential", AiGoal = null, Steps = [] }]  // 缺 aiGoal
        };

        var ex = Assert.Throws<ScriptValidationException>(() => service.SaveScript(script));
        Assert.NotEmpty(ex.Errors);
        Assert.Contains(ex.Errors, e => e.Contains("aiGoal"));  // 验值：错误含 aiGoal 项
        Assert.False(File.Exists(Path.Combine(scriptsDir, $"{script.ScriptId}.json")));  // 校验失败不写文件
    }

    /// <summary>forceSave=true + 缺 aiGoal → 不抛 + 文件写入（强制保存跳校验）。</summary>
    [Fact]
    public void SaveScript_ForceSaveTrue_MissingAiGoal_WritesFile()
    {
        var (service, scriptsDir, _, _) = Create();
        var script = new ScriptV2
        {
            ScriptId = "test-force-save",
            Name = "强制保存测试",
            Phases = [new PhaseNode { Kind = "phase", Name = "main", Type = "sequential", AiGoal = null, Steps = [] }]  // 缺 aiGoal
        };

        var path = service.SaveScript(script, forceSave: true);
        Assert.True(File.Exists(path));  // forceSave 跳校验写文件
        var json = File.ReadAllText(path);
        Assert.Contains("强制保存测试", json);  // 验值：文件内容正确
    }

    /// <summary>脏关联修复：forceSave=false 校验失败 → documents.json 未被改写（校验前置于 DocumentType 关联）。</summary>
    [Fact]
    public void SaveScript_ForceSaveFalse_ValidationFail_DoesNotMutateDocuments()
    {
        var (service, _, documentsFile, _) = Create();
        var script = new ScriptV2
        {
            ScriptId = "test-dirty-assoc",
            Name = "脏关联测试",
            DocumentTypeId = "新单据-不应创建",  // 校验通过才会触发自动创建 DocumentType
            Phases = [new PhaseNode { Kind = "phase", Name = "main", Type = "sequential", AiGoal = null, Steps = [] }]  // 缺 aiGoal → 校验失败
        };

        Assert.Throws<ScriptValidationException>(() => service.SaveScript(script));
        // documents.json 不含该 DocumentType（校验失败未关联；文件不存在也算通过——构造时不创建 documents.json）
        var docsContent = File.Exists(documentsFile) ? File.ReadAllText(documentsFile) : "";
        Assert.DoesNotContain("新单据-不应创建", docsContent);
    }

    /// <summary>forceSave=true + DocumentTypeId → 仍正确关联 DocumentType（正向路径：校验跳过不阻断关联，顺带修脏关联未破坏正常关联）。</summary>
    [Fact]
    public void SaveScript_ForceSaveTrue_WithDocumentType_AssociatesCorrectly()
    {
        var (service, _, documentsFile, _) = Create();
        var script = new ScriptV2
        {
            ScriptId = "test-force-assoc",
            Name = "强制保存关联测试",
            DocumentTypeId = "强制单据",  // forceSave 跳校验后应正常关联（自动创建 DocumentType）
            Phases = [new PhaseNode { Kind = "phase", Name = "main", Type = "sequential", AiGoal = null, Steps = [] }]  // 缺 aiGoal（forceSave 跳过）
        };

        service.SaveScript(script, forceSave: true);
        // documents.json 被改写 + 含该 DocumentType + scriptId 加入 OrderedScriptIds（正向关联未破坏）
        Assert.True(File.Exists(documentsFile));
        var docsJson = File.ReadAllText(documentsFile);
        Assert.Contains("强制单据", docsJson);
        Assert.Contains("test-force-assoc", docsJson);
    }

    /// <summary>经生产反序列化路径构造（DeserializeOnly 覆盖 PhaseItemConverter），缺 aiGoal → ScriptValidationException。</summary>
    [Fact]
    public void SaveScript_FromJsonDeserializationPath_MissingAiGoal_Throws()
    {
        var (service, _, _, _) = Create();
        // 经 JSON 反序列化构造（走 PhaseItemConverter，弥补 C# new 强类型构造绕过反序列化的风险）
        var json = """{"scriptId":"test-json-path","name":"JSON构造测试","phases":[{"kind":"phase","name":"main","type":"sequential","steps":[]}]}""";
        var script = ScriptLoader.DeserializeOnly(json);
        Assert.NotNull(script);
        Assert.Equal("JSON构造测试", script.Name);  // 验值：PhaseItemConverter 正确分发 + name 读回

        var ex = Assert.Throws<ScriptValidationException>(() => service.SaveScript(script));
        Assert.Contains(ex.Errors, e => e.Contains("aiGoal"));
    }

    /// <summary>GetAllScripts 读回兼容：带病脚本（缺 aiGoal 强制保存）能读回 + HasErrors=true（不再静默跳过→列表不显示）。</summary>
    [Fact]
    public void GetAllScripts_SickScript_ReadBackWithHasErrors()
    {
        var (service, _, _, _) = Create();
        // 先强制保存带病脚本（forceSave=false 会拒绝）
        service.SaveScript(new ScriptV2
        {
            ScriptId = "sick-script",
            Name = "带病脚本",
            Phases = [new PhaseNode { Kind = "phase", Name = "main", Type = "sequential", AiGoal = null, Steps = [] }]
        }, forceSave: true);

        var all = service.GetAllScripts();
        var item = Assert.Single(all);  // 带病脚本也能读回（不再 catch 静默跳过）
        Assert.Equal("sick-script", item.ScriptId);
        Assert.Equal("带病脚本", item.Name);
        Assert.True(item.HasErrors);  // 验值：业务校验失败 HasErrors=true
    }

    /// <summary>GetAllScripts 结构坏脚本兜底显示（用户方案场景3"名称读不出→兜底"，不再静默跳过）+ LogWarning 被调用（silent-success 防护——让跳过可观测）。</summary>
    [Fact]
    public void GetAllScripts_BrokenJson_FallsBackWithWarning()
    {
        var (service, scriptsDir, _, logger) = Create();
        File.WriteAllText(Path.Combine(scriptsDir, "broken.json"), "{invalid json structure");  // 结构坏（不能用 SaveScript）

        var all = service.GetAllScripts();
        var item = Assert.Single(all);  // 结构坏兜底显示（场景3：不再跳过，列表非空）
        Assert.Equal("broken", item.ScriptId);  // 兜底用文件名（结构坏拿不到 script.ScriptId）
        Assert.Contains("异常脚本", item.Name);  // 兜底名："{文件名}（异常脚本）"
        Assert.True(item.HasErrors);  // 兜底项标错
        // 验可观测：LogWarning 被调用（不再空 catch 吞掉）
        Assert.Contains(logger.Logs, l => l.level == LogLevel.Warning && l.message.Contains("broken.json"));
    }

    /// <summary>GetAllScripts 含未知字段脚本（step 级 ifrmae 拼错）→ schema additionalProperties:false 抓到 → HasErrors=true（补 schema 后能抓，原 DeserializeOnly 跳 schema 抓不到）。</summary>
    [Fact]
    public void GetAllScripts_UnknownField_HasErrors()
    {
        var (service, scriptsDir, _, _) = Create();
        // 直接写含未知字段的脚本（业务校验通过：name/aiGoal/selector 齐全；schema stepNode additionalProperties:false 抓 ifrmae 未知字段）
        File.WriteAllText(Path.Combine(scriptsDir, "unknown-field.json"),
            """{"scriptId":"unknown-field","name":"未知字段测试","phases":[{"kind":"phase","name":"main","type":"sequential","aiGoal":"测试","steps":[{"kind":"step","name":"s1","action":"click","ifrmae":"f1","selector":"#x"}]}]}""");

        var all = service.GetAllScripts();
        var item = Assert.Single(all);
        Assert.Equal("unknown-field", item.ScriptId);  // 业务读回正常（name/scriptId 能拿）
        Assert.True(item.HasErrors);  // 🔴验值：schema 补全后抓到未知字段 ifrmae（补 schema 前 DeserializeOnly 跳 schema→HasErrors=false 漏）
    }

    /// <summary>ScriptController.Save catch ScriptValidationException → BadRequest 400（防御性，前端未用此端点）。</summary>
    [Fact]
    public void ScriptController_Save_ValidationFail_Returns400()
    {
        var (service, _, _, _) = Create();
        var controller = new ScriptController(service);
        var script = new ScriptV2
        {
            ScriptId = "ctrl-test",
            Name = "控制器测试",
            Phases = [new PhaseNode { Kind = "phase", Name = "main", Type = "sequential", AiGoal = null, Steps = [] }]  // 缺 aiGoal
        };

        var result = controller.Save(script);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        // 验值：body 含 validationFailed=true（对齐 RecordController）
        dynamic body = badRequest.Value!;
        Assert.True((bool)body.validationFailed);
    }
}
