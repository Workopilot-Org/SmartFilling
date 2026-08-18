using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using OpenAI.Chat;
using SmartFilling.App.Configuration;
using SmartFilling.App.Recording;
using SmartFilling.App.Services;
using SmartFilling.Engine.Ai;
using SmartFilling.Engine.Models;
using System.Text.Json;
using System.Reflection;
using EngineILogger = SmartFilling.Engine.Logging.ILogger;
using Xunit;

namespace SmartFilling.App.Tests;

/// <summary>
/// valueFrom 录制期参数测试 + storeAsVariables 收集 + operate schema 验证。
/// </summary>
public class RecordingValueFromTests
{
    [Fact]
    public void ValueFrom_NonEmpty_ReturnsValueFrom_SkipsFieldNameParam()
    {
        var result = RecordingActionExecutor.ParametrizeValue(
            "fill", "actualToken", "someField", null, "{{resp.body.token}}");
        Assert.Equal("{{resp.body.token}}", result);
    }

    [Fact]
    public void ValueFrom_NonEmpty_DoesNotFillCache()
    {
        var cache = new Dictionary<string, string>();
        var result = RecordingActionExecutor.ParametrizeValue(
            "fill", "actualToken", "someField", cache, "{{resp.body.token}}");
        Assert.Equal("{{resp.body.token}}", result);
        // valueFrom 优先不填缓存（不参数化为 fieldName）
        Assert.False(cache.ContainsKey("someField"));
    }

    [Fact]
    public void ValueFrom_Empty_FallsBackToFieldNameParam()
    {
        var cache = new Dictionary<string, string>();
        var result = RecordingActionExecutor.ParametrizeValue(
            "fill", "张三", "username", cache, null);
        Assert.Equal("{{username}}", result);
        Assert.Equal("张三", cache["username"]);
    }

    [Fact]
    public void ValueFrom_EmptyString_FallsBackToFieldNameParam()
    {
        var result = RecordingActionExecutor.ParametrizeValue(
            "fill", "张三", "username", null, "");
        Assert.Equal("{{username}}", result);
    }

    // ---------- #13: 守卫 valueFrom 豁免 ----------

    [Fact]
    public void ValueFrom_ExemptsValueGuard_UnresolvedVarNotBlocked()
    {
        // 无 valueFrom：value="{{token}}" 含 {{}} → 守卫拦截（返 "value"）
        var blocked = RecordingActionExecutor.CheckUnresolvedRecordVar(
            "fill", "//input[@id='user']", "{{token}}", null);
        Assert.Equal("value", blocked);

        // 有 valueFrom：value 传 null（豁免）→ 守卫跳过 value 检查 → 无错误
        // （实际代码 L516: !string.IsNullOrEmpty(valueFrom) ? null : value）
        var exempted = RecordingActionExecutor.CheckUnresolvedRecordVar(
            "fill", "//input[@id='user']", null, null);
        Assert.Null(exempted);
    }

    // ---------- #14: BuildPhaseStatus 含 storeAsVariables ----------

    private static RecordingEngine CreateEngine()
    {
        var mockAi = Substitute.For<IAiProvider>();
        var mockLogger = Substitute.For<EngineILogger>();
        var tempDir = Path.Combine(Path.GetTempPath(), "SmartFillingTest_" + Guid.NewGuid().ToString("N")[..8]);
        var mockEnv = Substitute.For<IWebHostEnvironment>();
        mockEnv.ContentRootPath.Returns(tempDir);
        var mockScriptLogger = Substitute.For<ILogger<ScriptService>>();
        var scriptService = new ScriptService(mockEnv, Options.Create(new AppOptions()), mockScriptLogger);
        var engine = new RecordingEngine(mockAi, scriptService, mockLogger, new EngineOptions(), new AppOptions());
        engine.OnLog += (a, msg) => Task.CompletedTask;
        return engine;
    }

    [Fact]
    public async Task BuildPhaseStatus_ContainsStoreAsVariables()
    {
        var engine = CreateEngine();

        // 录一个 httpRequest step storeAs=resp → 填充 _phases
        var args = new Dictionary<string, object>
        {
            ["stepType"] = "httpRequest",
            ["name"] = "fetchToken",
            ["description"] = "get token",
            ["httpUrl"] = "https://api.test.com/login",
            ["httpMethod"] = "POST",
            ["storeAs"] = "resp",
            ["phase"] = "p1",
            ["phaseType"] = "sequential",
            ["phaseAiGoal"] = "test"
        };
        var result = await engine.ExecuteSaveStep(args, CancellationToken.None);
        Assert.True(string.IsNullOrEmpty(result) || !result.Contains("❌"),
            $"ExecuteSaveStep returned: '{result}'");

        // BuildPhaseStatus 应含 storeAsVariables 数组，其中含 "resp"
        var status = engine.BuildPhaseStatus();
        var json = JsonDocument.Parse(status);
        Assert.True(json.RootElement.TryGetProperty("storeAsVariables", out var storeAsVars));
        var names = storeAsVars.EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Contains("resp", names);
    }

    // ---------- #15: operate 工具 schema 含 valueFrom 参数 ----------

    [Fact]
    public void OperateSchema_ContainsValueFromField()
    {
        var engine = CreateEngine();
        var method = typeof(RecordingEngine).GetMethod("GetRecordingTools",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var tools = (List<ChatTool>)method!.Invoke(engine, null)!;
        var operateTool = tools.First(t => t.FunctionName == "operate");
        Assert.NotNull(operateTool);

        // ChatTool 把 schema 存为 BinaryData — 反射取 BinaryData 类型的属性
        var bdProp = typeof(ChatTool).GetProperties()
            .First(p => p.PropertyType == typeof(BinaryData));
        var schemaBd = (BinaryData)bdProp.GetValue(operateTool)!;
        var schemaJson = schemaBd.ToString();
        var schema = JsonDocument.Parse(schemaJson);
        Assert.True(schema.RootElement.GetProperty("properties").TryGetProperty("valueFrom", out var valueFromField),
            "operate 工具 schema 应含 valueFrom 字段");
        Assert.Equal("string", valueFromField.GetProperty("type").GetString());
    }
}
