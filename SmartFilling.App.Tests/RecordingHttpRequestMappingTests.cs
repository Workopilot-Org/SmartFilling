using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using SmartFilling.App.Configuration;
using SmartFilling.App.Recording;
using SmartFilling.App.Services;
using SmartFilling.Engine.Ai;
using SmartFilling.Engine.Models;
using System.Text.Json;
using EngineILogger = SmartFilling.Engine.Logging.ILogger;
using Xunit;

namespace SmartFilling.App.Tests;

/// <summary>
/// httpRequest save_step 字段映射测试（R2-3 选项 B）。
/// 验 ExecuteSaveStep 传 stepType=httpRequest + 全字段 → 落盘 StepNode 各字段值正确（验值不只验状态）。
/// + URL 格式校验正负向。
/// </summary>
public class RecordingHttpRequestMappingTests
{
    private static RecordingEngine CreateEngine()
    {
        var mockAi = Substitute.For<IAiProvider>();
        var mockLogger = Substitute.For<EngineILogger>();
        var engine = new RecordingEngine(mockAi, CreateScriptService(), mockLogger, new EngineOptions(), new AppOptions());
        return engine;
    }

    private static ScriptService CreateScriptService()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SmartFillingTest_" + Guid.NewGuid().ToString("N")[..8]);
        var mockEnv = Substitute.For<IWebHostEnvironment>();
        mockEnv.ContentRootPath.Returns(tempDir);
        var mockLogger = Substitute.For<ILogger<ScriptService>>();
        return new ScriptService(mockEnv, Options.Create(new AppOptions()), mockLogger);
    }

    private static Dictionary<string, object> HttpStepArgs(string url, string method = "POST",
        object? headers = null, object? body = null, object? @params = null,
        bool? failOnHttpError = null, bool? shareBrowserSession = null, List<object>? files = null)
    {
        var args = new Dictionary<string, object>
        {
            ["stepType"] = "httpRequest",
            ["name"] = "httpStep",
            ["description"] = "test http",
            ["httpUrl"] = url,
            ["httpMethod"] = method,
            ["storeAs"] = "resp"
        };
        if (headers != null) args["headers"] = headers;
        if (body != null) args["httpBody"] = body;
        if (@params != null) args["params"] = @params;
        if (failOnHttpError != null) args["failOnHttpError"] = failOnHttpError;
        if (shareBrowserSession != null) args["shareBrowserSession"] = shareBrowserSession;
        if (files != null) args["files"] = files;
        return args;
    }

    [Fact]
    public async Task ExecuteSaveStep_HttpRequest_AllFieldsMapped()
    {
        var engine = CreateEngine();
        engine.OnLog += (a, msg) => Task.CompletedTask;

        // 一次性传 phase + httpRequest 全字段（phase 在 EnsurePhaseAsync 中自动创建）
        var headersJson = JsonSerializer.SerializeToElement(new { Authorization = "Bearer {{token}}" });
        var bodyJson = JsonSerializer.SerializeToElement(new { formId = "{{formId}}" });
        var paramsJson = JsonSerializer.SerializeToElement(new { page = 1 });
        var filesJson = JsonSerializer.SerializeToElement(new object[]
        {
            new { paramName = "file", path = "{{filePath}}" }
        });

        var args = HttpStepArgs(
            url: "https://api.example.com/submit",
            method: "POST",
            headers: headersJson,
            body: bodyJson,
            @params: paramsJson,
            failOnHttpError: true,
            shareBrowserSession: true,
            files: null  // 单独设置 below
        );
        args["files"] = filesJson;  // 传 JsonElement(Array) 形态——生产路径 AI tool call 经 STJ 反序列化
        args["phase"] = "p1";
        args["phaseType"] = "sequential";
        args["phaseAiGoal"] = "test";

        var result = await engine.ExecuteSaveStep(args, CancellationToken.None);

        // 不应报错
        Assert.True(string.IsNullOrEmpty(result) || !result.Contains("❌"),
            $"ExecuteSaveStep returned: '{result}'. LastStep.Action={engine.TestLastStep?.Action} StepCount={engine.TestStepCount}");

        // 验落盘 step 各字段值（验值不只验状态）
        var step = engine.TestLastStep;
        Assert.NotNull(step);
        Assert.Equal("httpRequest", step!.Action);
        Assert.Equal("POST", step.Method);
        Assert.Equal("https://api.example.com/submit", step.Url);
        // 验 Headers 内容（防透传 object 被 ToString 成 string 的 silent 回归——StoreVars JsonElement bug 前科）
        Assert.True(step.Headers is JsonElement, "Headers 应为 JsonElement 非 string");
        var headersElement = (JsonElement)step.Headers;
        Assert.True(headersElement.TryGetProperty("Authorization", out var authVal));
        Assert.Equal("Bearer {{token}}", authVal.GetString());

        // 验 Body 内容
        Assert.True(step.Body is JsonElement, "Body 应为 JsonElement 非 string");
        var bodyElement = (JsonElement)step.Body;
        Assert.True(bodyElement.TryGetProperty("formId", out var formIdVal));
        Assert.Equal("{{formId}}", formIdVal.GetString());

        // 验 Params 内容
        Assert.True(step.Params is JsonElement, "Params 应为 JsonElement 非 string");
        var paramsElement = (JsonElement)step.Params;
        Assert.True(paramsElement.TryGetProperty("page", out var pageVal));
        Assert.Equal(1, pageVal.GetInt32());
        Assert.True(step.FailOnHttpError == true, $"FailOnHttpError should be true, got {step.FailOnHttpError}");
        Assert.True(step.ShareBrowserSession == true, $"ShareBrowserSession should be true, got {step.ShareBrowserSession}");
        Assert.NotNull(step.Files);
        Assert.Single(step.Files!);
        Assert.Equal("resp", step.StoreAs);
    }

    [Fact]
    public async Task ExecuteSaveStep_HttpRequest_InvalidUrl_ReturnsError()
    {
        var engine = CreateEngine();
        engine.OnLog += (a, msg) => Task.CompletedTask;

        var args = HttpStepArgs(url: "not-a-url");
        args["phase"] = "p2";
        args["phaseType"] = "sequential";
        args["phaseAiGoal"] = "test";

        var result = await engine.ExecuteSaveStep(args, CancellationToken.None);

        Assert.Contains("http://", result ?? "");
        // 不应落盘 httpRequest step
        var lastStep = engine.TestLastStep;
        Assert.True(lastStep?.Action != "httpRequest", "httpRequest with invalid URL should not be saved");
    }

    [Fact]
    public async Task ExecuteSaveStep_HttpRequest_VarUrl_NoError()
    {
        var engine = CreateEngine();
        engine.OnLog += (a, msg) => Task.CompletedTask;

        var args = HttpStepArgs(url: "{{apiUrl}}");
        args["phase"] = "p3";
        args["phaseType"] = "sequential";
        args["phaseAiGoal"] = "test";

        var result = await engine.ExecuteSaveStep(args, CancellationToken.None);

        // 含 {{}} 变量占位豁免，不报错
        Assert.DoesNotContain("❌", result ?? "");
        var step = engine.TestLastStep;
        Assert.NotNull(step);
        Assert.Equal("{{apiUrl}}", step!.Url);
    }
}
