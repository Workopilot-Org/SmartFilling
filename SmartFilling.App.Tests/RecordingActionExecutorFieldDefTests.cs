using SmartFilling.App.Recording;
using SmartFilling.Engine.Models;
using System.Text.Json;

namespace SmartFilling.App.Tests;

/// <summary>
/// 决策3 App.Tests：批次3 ResolveRecordingFieldDef 双源合成（args + _script.Fields）。
/// 验首次录制 baseDef=null 时 args 合成 + 重复录制 script.Fields 兜底 + 双源 args 覆盖 + 首次录制 Type 缺失（重大1，批次4 ResolveFieldTypeAsync 现场补）。
/// args 用 CLR string 构造（生产经 OpenAiProvider.ParseJsonElement 归一化为 CLR string，此处等价；P32 注释）。
/// </summary>
public class RecordingActionExecutorFieldDefTests
{
    private static ScriptV2 BuildScriptWithField(string name, string? format = null, string? transform = null, string? type = null) =>
        new()
        {
            ScriptId = "test",
            Name = "Test",
            Phases = new List<PhaseItem>(),
            Fields = new List<FieldDefinition>
            {
                new() { Name = name, Label = "字段", Type = type ?? "string", Format = format, Transform = transform }
            }
        };

    private static Dictionary<string, object> Args(params (string k, string v)[] kv) =>
        kv.ToDictionary(x => x.k, x => (object)x.v);

    [Fact]
    public void ResolveRecordingFieldDef_AllNull_ReturnsNull()
    {
        var def = RecordingActionExecutor.ResolveRecordingFieldDef(null, null, null);
        Assert.Null(def);
    }

    [Fact]
    public void ResolveRecordingFieldDef_EmptyArgs_NoScript_ReturnsNull()
    {
        var def = RecordingActionExecutor.ResolveRecordingFieldDef("x", null, new Dictionary<string, object>());
        Assert.Null(def);
    }

    [Fact]
    public void ResolveRecordingFieldDef_ArgsOnly_Synthesizes()
    {
        // 首次录制：baseDef=null，args 是唯一来源
        var args = Args(("fieldFormat", "0.00"), ("fieldType", "number"), ("fieldTransform", "trim"));

        var def = RecordingActionExecutor.ResolveRecordingFieldDef("amount", null, args);

        Assert.NotNull(def);
        Assert.Equal("amount", def!.Name);
        Assert.Equal("0.00", def.Format);
        Assert.Equal("number", def.Type);
        Assert.Equal("trim", def.Transform);
    }

    [Fact]
    public void ResolveRecordingFieldDef_ScriptField_Fallback()
    {
        // 重复录制：_script.Fields 已注册，args 不传 → 用 script.Fields
        var script = BuildScriptWithField("amount", format: "0.00", transform: "upper", type: "number");

        var def = RecordingActionExecutor.ResolveRecordingFieldDef("amount", script, null);

        Assert.NotNull(def);
        Assert.Equal("amount", def!.Name);
        Assert.Equal("0.00", def.Format);
        Assert.Equal("number", def.Type);
        Assert.Equal("upper", def.Transform);
    }

    [Fact]
    public void ResolveRecordingFieldDef_ArgsOverrideScriptField()
    {
        // 双源：args 覆盖 script.Fields（args 优先）
        var script = BuildScriptWithField("amount", format: "old", type: "string");
        var args = Args(("fieldFormat", "new"), ("fieldType", "number"));

        var def = RecordingActionExecutor.ResolveRecordingFieldDef("amount", script, args);

        Assert.NotNull(def);
        Assert.Equal("new", def!.Format);   // args 覆盖
        Assert.Equal("number", def.Type);    // args 覆盖
    }

    [Fact]
    public void ResolveRecordingFieldDef_FirstRecord_TypeMissing()
    {
        // 重大1：首次录制 baseDef=null + AI 只传 fieldFormat 不传 fieldType → Type=null（format 依赖 Type，由批次4 ResolveFieldTypeAsync 现场从 DOM 补）
        var args = Args(("fieldFormat", "0.00"));

        var def = RecordingActionExecutor.ResolveRecordingFieldDef("amount", null, args);

        Assert.NotNull(def);
        Assert.Equal("0.00", def!.Format);
        Assert.Null(def.Type);  // 首次录制 Type 缺失，证明需 ResolveFieldTypeAsync 现场补
    }

    [Fact]
    public void ResolveRecordingFieldDef_ScriptFieldNotFound_UsesArgs()
    {
        // script.Fields 无该字段 + args 有 → args 合成（Name=fieldName）
        var script = BuildScriptWithField("other", type: "string");
        var args = Args(("fieldType", "date"));

        var def = RecordingActionExecutor.ResolveRecordingFieldDef("missing", script, args);

        Assert.NotNull(def);
        Assert.Equal("missing", def!.Name);  // baseDef=null（script 无），Name=fieldName
        Assert.Equal("date", def.Type);
    }

    [Fact]
    public void ResolveRecordingFieldDef_ProductionNormalizedStringArgs_SynthesizesWithoutQuotes()
    {
        // C⑦（P20/M7）：锁"生产 ParseJsonElement 后 ToString 无引号"。
        // 生产路径 OpenAiProvider.ParseJsonElement（L134）把 JSON string 归一化为 CLR string（GetString，无引号），
        // 故 args["fieldFormat"] 运行时是 CLR string，ResolveRecordingFieldDef 读 GetValueOrDefault(...).ToString() 返回纯 "0.00"（无引号）。
        // 本测试用 JsonDocument.Parse（生产反序列化形态）构造 raw JsonElement，再复现 ParseJsonElement 的 String→GetString 归一化，
        // 走生产链路真实数据变换（非直接 new CLR string 理想化构造，印证 test-covers-production-deserialize-path）。
        using var doc = JsonDocument.Parse("""{"fieldFormat":"0.00","fieldType":"number"}""");
        var args = new Dictionary<string, object>();
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            // 复现 OpenAiProvider.ParseJsonElement 归一化：String→GetString()（CLR string 无引号）；其他 ValueKind 保持 JsonElement
            args[p.Name] = p.Value.ValueKind == JsonValueKind.String ? (object)(p.Value.GetString() ?? "") : p.Value;
        }

        var def = RecordingActionExecutor.ResolveRecordingFieldDef("amount", null, args);

        Assert.NotNull(def);
        Assert.Equal("0.00", def!.Format);   // 无引号（生产归一化结果）
        Assert.Equal("number", def.Type);
    }

    [Fact]
    public void ResolveRecordingFieldDef_RawJsonElementString_AlsoSynthesizesClean()
    {
        // C⑦（firsthand 实证纠正计划 L69 注释「JsonElement.ToString() 对 string 带引号」的二手假设）：
        // 实测 .NET 8 中 JsonElement(String).ToString() 返回**无引号**的字符串值（与 GetString() 等价），
        // 故即便 args 直传 raw JsonElement（未经 OpenAiProvider.ParseJsonElement 归一化），ResolveRecordingFieldDef 的
        // GetValueOrDefault(...).ToString() 仍返回纯 "0.00"（无引号），合成 fieldDef.Format 干净。
        // 即 ResolveRecordingFieldDef 对「CLR string（生产归一化形态）」与「raw JsonElement(String)」两种形态均鲁棒——
        // 计划/P20/M7 担心的"非 OpenAiProvider 实现直传 JsonElement 致带引号 format 失效"在 .NET 8 不成立。
        // 生产仍走 GetString()（OpenAiProvider.ParseJsonElement L134）——语义最清晰且正确处理 null token；本测试锁定合成鲁棒性契约。
        using var doc = JsonDocument.Parse("""{"fieldFormat":"0.00"}""");
        var raw = doc.RootElement.GetProperty("fieldFormat");
        Assert.Equal("0.00", raw.ToString());  // firsthand 实证：JsonElement(string).ToString() 无引号（纠正计划 L69 旧注释）

        var args = new Dictionary<string, object> { ["fieldFormat"] = raw };
        var def = RecordingActionExecutor.ResolveRecordingFieldDef("amount", null, args);
        Assert.Equal("0.00", def!.Format);  // 合成干净（无引号），与生产归一化形态结果一致
    }
}
