using SmartFilling.Engine.Engine;
using SmartFilling.Engine.Models;
using System.Text.Json;
using Xunit;

namespace SmartFilling.Engine.Tests.Unit;

/// <summary>
/// 组3 (3.10/3.11/3.15)：initialVars 声明校验 + ValidatePlaceholders 放行 + ValidateSettings 值域校验 测试。
/// 全部用原始 JSON + LoadFromJson 走生产反序列化路径（test-covers-production-deserialize-path memory：
/// InitialVar.DefaultValue 经 STJ 反序列化为 JsonElement，C# 强类型构造会绕过该路径）。
/// </summary>
public class ScriptLoaderInitialVarsTests
{
    private static ScriptV2 LoadFromJson(string json)
        => ScriptLoader.LoadFromJson(json);

    // === ValidatePlaceholders 放行 initialVars 声明（3.10）===

    [Fact]
    public void InitialVars_Declared_PlaceholderPasses()
    {
        // 档②：脚本声明 initialVars mode + 脚本 navigate url 用 {{mode}} → ValidatePlaceholders 放行
        var json = """
        {
          "version": 2,
          "scriptId": "test-iv",
          "name": "IV测试",
          "phases": [{"kind":"phase","name":"main","type":"sequential","aiGoal":"测试","steps":[{"kind":"step","action":"navigate","name":"nav","url":"http://localhost:8088/{{mode}}.html"}]}],
          "initialVars": [{"name":"mode","description":"填报模式"}],
          "fields": [{"name":"field1","label":"字段1","type":"string"}]
        }
        """;
        var script = LoadFromJson(json);
        Assert.NotNull(script.InitialVars);
        Assert.Single(script.InitialVars);
        Assert.Equal("mode", script.InitialVars[0].Name);
    }

    [Fact]
    public void InitialVars_NotDeclared_PlaceholderRejected()
    {
        // {{unknownVar}} 不在 fields/storeAs/ReservedVarNames/initialVars → LoadFromJson 报错（防拼错）
        var json = """
        {
          "version": 2,
          "scriptId": "test-iv",
          "name": "IV测试",
          "phases": [{"kind":"phase","name":"main","type":"sequential","aiGoal":"测试","steps":[{"kind":"step","action":"navigate","name":"nav","url":"http://localhost:8088/{{unknownVar}}.html"}]}],
          "fields": [{"name":"field1","label":"字段1","type":"string"}]
        }
        """;
        var ex = Assert.Throws<InvalidOperationException>(() => LoadFromJson(json));
        Assert.Contains("initialVars", ex.Message);
    }

    // === ValidateInitialVarsNames 校验（3.11）===

    [Fact]
    public void InitialVars_NameConflict_ReservedVarNames_Rejected()
    {
        var json = """
        {
          "version": 2,
          "scriptId": "test-iv",
          "name": "IV测试",
          "phases": [{"kind":"phase","name":"main","type":"sequential","aiGoal":"测试","steps":[{"kind":"step","action":"navigate","name":"nav","url":"http://localhost:8088/{{_rowIndex}}.html"}]}],
          "initialVars": [{"name":"_rowIndex"}],
          "fields": [{"name":"field1","label":"字段1","type":"string"}]
        }
        """;
        var ex = Assert.Throws<InvalidOperationException>(() => LoadFromJson(json));
        Assert.Contains("_rowIndex", ex.Message);
        Assert.Contains("系统保留变量", ex.Message);
    }

    [Fact]
    public void InitialVars_NameConflict_SystemCredentialKeys_Rejected()
    {
        var json = """
        {
          "version": 2,
          "scriptId": "test-iv",
          "name": "IV测试",
          "phases": [{"kind":"phase","name":"main","type":"sequential","aiGoal":"测试","steps":[{"kind":"step","action":"navigate","name":"nav","url":"http://localhost:8088/{{username}}.html"}]}],
          "initialVars": [{"name":"username"}],
          "fields": [{"name":"field1","label":"字段1","type":"string"}]
        }
        """;
        var ex = Assert.Throws<InvalidOperationException>(() => LoadFromJson(json));
        Assert.Contains("username", ex.Message);
        Assert.Contains("系统凭据", ex.Message);
    }

    [Fact]
    public void InitialVars_NameConflict_Fields_Rejected()
    {
        var json = """
        {
          "version": 2,
          "scriptId": "test-iv",
          "name": "IV测试",
          "phases": [{"kind":"phase","name":"main","type":"sequential","aiGoal":"测试","steps":[{"kind":"step","action":"navigate","name":"nav","url":"http://localhost:8088/{{field1}}.html"}]}],
          "initialVars": [{"name":"field1"}],
          "fields": [{"name":"field1","label":"字段1","type":"string"}]
        }
        """;
        var ex = Assert.Throws<InvalidOperationException>(() => LoadFromJson(json));
        Assert.Contains("field1", ex.Message);
        Assert.Contains("fields", ex.Message);
    }

    [Fact]
    public void InitialVars_DuplicateName_Rejected()
    {
        var json = """
        {
          "version": 2,
          "scriptId": "test-iv",
          "name": "IV测试",
          "phases": [{"kind":"phase","name":"main","type":"sequential","aiGoal":"测试","steps":[{"kind":"step","action":"navigate","name":"nav","url":"http://localhost:8088/{{mode}}.html"}]}],
          "initialVars": [{"name":"mode"},{"name":"mode"}],
          "fields": [{"name":"field1","label":"字段1","type":"string"}]
        }
        """;
        var ex = Assert.Throws<InvalidOperationException>(() => LoadFromJson(json));
        Assert.Contains("mode", ex.Message);
        Assert.Contains("重复", ex.Message);
    }

    [Fact]
    public void InitialVars_IllegalName_Rejected()
    {
        // 数字开头不合法（须 ^[a-zA-Z_]\w*$）—— schema pattern 先挡（双层防御：schema pattern + code ValidateInitialVarsNames）
        var json = """
        {
          "version": 2,
          "scriptId": "test-iv",
          "name": "IV测试",
          "phases": [{"kind":"phase","name":"main","type":"sequential","aiGoal":"测试","steps":[{"kind":"step","action":"navigate","name":"nav","url":"http://localhost:8088/test.html"}]}],
          "initialVars": [{"name":"1mode"}],
          "fields": [{"name":"field1","label":"字段1","type":"string"}]
        }
        """;
        // 双层防御：schema pattern（^[a-zA-Z_]\w*$）或 code ValidateInitialVarsNames 拒绝
        Assert.Throws<InvalidOperationException>(() => LoadFromJson(json));
    }

    [Fact]
    public void InitialVars_DefaultValue_PreservedAsJsonElement()
    {
        // defaultValue 经 STJ 反序列化为 JsonElement（生产路径），engine 入口 NormalizeJsonElement 归一
        var json = """
        {
          "version": 2,
          "scriptId": "test-iv",
          "name": "IV测试",
          "phases": [{"kind":"phase","name":"main","type":"sequential","aiGoal":"测试","steps":[{"kind":"step","action":"navigate","name":"nav","url":"http://localhost:8088/{{mode}}.html"}]}],
          "initialVars": [{"name":"mode","defaultValue":"A"}],
          "fields": [{"name":"field1","label":"字段1","type":"string"}]
        }
        """;
        var script = LoadFromJson(json);
        Assert.NotNull(script.InitialVars);
        var iv = script.InitialVars[0];
        Assert.NotNull(iv.DefaultValue);
        // DefaultValue 经 STJ 反序列化是 JsonElement（生产路径验证）
        Assert.IsType<JsonElement>(iv.DefaultValue);
        var je = (JsonElement)iv.DefaultValue;
        Assert.Equal(JsonValueKind.String, je.ValueKind);
        Assert.Equal("A", je.GetString());
    }

    // === ValidateSettings 值域校验（3.15）===

    private const string SettingsBase = """
        {
          "version": 2,
          "scriptId": "test-iv",
          "name": "IV测试",
          "phases": [{"kind":"phase","name":"main","type":"sequential","aiGoal":"测试","steps":[{"kind":"step","action":"navigate","name":"nav","url":"http://localhost:8088/test.html"}]}],
          "fields": [{"name":"field1","label":"字段1","type":"string"}]
        }
        """;

    private static string WithSettings(string settingsJson)
        => SettingsBase.Replace("\"fields\":", $"\"settings\":{settingsJson},\n          \"fields\":");

    [Fact]
    public void Settings_DefaultTimeoutZero_Rejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LoadFromJson(WithSettings("""{"defaultTimeout":0}""")));
        Assert.Contains("defaultTimeout", ex.Message);
    }

    [Fact]
    public void Settings_CompressQuality_Over100_Rejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LoadFromJson(WithSettings("""{"screenshot":{"compressQuality":200}}""")));
        Assert.Contains("compressQuality", ex.Message);
    }

    [Fact]
    public void Settings_CompressQuality_Valid_Accepted()
    {
        // 3.15 改法 A：schema 已补 compressQuality，此处验值域 1-100 通过
        var script = LoadFromJson(WithSettings("""{"screenshot":{"compressQuality":100}}"""));
        Assert.NotNull(script.Settings);
        Assert.NotNull(script.Settings.Screenshot);
        Assert.Equal(100, script.Settings.Screenshot.CompressQuality);
    }

    [Fact]
    public void Settings_MaxScriptDurationNegative_Rejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LoadFromJson(WithSettings("""{"maxScriptDuration":-1}""")));
        Assert.Contains("maxScriptDuration", ex.Message);
    }

    [Fact]
    public void Settings_OnErrorInvalid_Rejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LoadFromJson(WithSettings("""{"onError":"invalid-value"}""")));
        Assert.Contains("onError", ex.Message);
    }
}
