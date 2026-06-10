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
using EngineILogger = SmartFilling.Engine.Logging.ILogger;

namespace SmartFilling.App.Tests;

/// <summary>
/// 录制前置脚本 silent-success 修复测试（强制保存计划 C 块）。
/// 验证 RecordingEngine 前置块四分支：① 文件不存在→跳过+OnLog 可观测 / ② 结构坏→throw PrerequisiteScriptFailedException+LogWarning /
/// ③ 带病（缺 aiGoal）→throw+消息含 script.Name+校验错误 / ④ 合法→执行不抛。
/// 前置块在 RecordAsync if(_page==null) 内，须用真实 headless 浏览器（不注入 TestInjectedPage，参照 Cancel_DuringPrerequisiteScript）。
/// 带病/结构坏 JSON 须直接 File.WriteAllText（不能经 SaveScript，校验/反序列化会拒绝）——天然经生产 DeserializeOnly 路径。断言验值不只验状态。
/// </summary>
public class RecordingEnginePrerequisiteTests
{
    private class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel level, string message)> Logs = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Logs.Add((logLevel, formatter(state, exception)));
        private class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
    }

    /// <summary>构造真实浏览器录制引擎 + 真实 ScriptService（临时目录）+ 可捕获日志。返回 scriptsDir 供直接写带病/结构坏 JSON。</summary>
    private static (RecordingEngine engine, ScriptService scriptService, string scriptsDir, CapturingLogger<ScriptService> svcLogger, List<string> onLogMessages) Create(IAiProvider mockAi)
    {
        // 🔴难点2：测试 lenientNull 与生产对齐——RecordOutputAllFields=true（生产 appsettings.json:60=true），
        // 否则测试在 false（严格）下绿、生产 true（StripNulls 宽松）下行为不同，掩盖生产真实 lenientNull 行为。
        var appOptions = new AppOptions { MaxRecordTurns = 10, RecordOutputAllFields = true };
        var engineOptions = new EngineOptions { MaxScriptDuration = 600000, Headless = true };
        var tempDir = Path.Combine(Path.GetTempPath(), "SmartFillingPrereqTest_" + Guid.NewGuid().ToString("N")[..8]);
        var mockEnv = Substitute.For<IWebHostEnvironment>();
        mockEnv.ContentRootPath.Returns(tempDir);
        var svcLogger = new CapturingLogger<ScriptService>();
        var scriptService = new ScriptService(mockEnv, Options.Create(appOptions), svcLogger);
        var engine = new RecordingEngine(mockAi, scriptService, Substitute.For<EngineILogger>(), engineOptions, appOptions);
        var onLogMessages = new List<string>();
        engine.OnLog += (_, msg) => { onLogMessages.Add(msg); return Task.CompletedTask; };
        return (engine, scriptService, Path.Combine(tempDir, "data", "scripts"), svcLogger, onLogMessages);
    }

    /// <summary>① 带病前置（缺 aiGoal）→ throw PrerequisiteScriptFailedException + 消息含 script.Name + 校验错误（③ 分支）。</summary>
    [Fact]
    public async Task SickPrerequisite_MissingAiGoal_ThrowsWithScriptNameAndError()
    {
        var mockAi = Substitute.For<IAiProvider>();
        var (engine, _, scriptsDir, _, _) = Create(mockAi);
        // 直接写缺 aiGoal 的带病 JSON（不能经 SaveScript，校验拒绝）——经生产 DeserializeOnly 读回
        File.WriteAllText(Path.Combine(scriptsDir, "sick-prereq.json"),
            """{"scriptId":"sick-prereq","name":"带病前置脚本","phases":[{"kind":"phase","name":"main","type":"sequential","steps":[]}]}""");

        using var cts = new CancellationTokenSource();
        var ex = await Assert.ThrowsAsync<PrerequisiteScriptFailedException>(() =>
            engine.RecordAsync("test-doc", "任务", "sick-prereq", headless: true, userResponse: null, ct: cts.Token));
        await engine.StopAsync();  // 清理浏览器
        Assert.Contains("带病前置脚本", ex.Message);  // 验值：消息含 script.Name（带病能拿到 name）
        Assert.Contains("校验失败", ex.Message);  // ③ 分支
        Assert.Contains("aiGoal", ex.Message);  // 验值：含具体校验错误
        await mockAi.DidNotReceive().SendMessageAsync(Arg.Any<List<ChatMessage>>(), Arg.Any<List<ChatTool>?>(), Arg.Any<CancellationToken>());  // 未进录制循环
    }

    /// <summary>② 结构坏前置（invalid JSON）→ throw PrerequisiteScriptFailedException + 消息含 scriptId + "加载失败" + ScriptService LogWarning（② 分支）。</summary>
    [Fact]
    public async Task BrokenJsonPrerequisite_ThrowsWithScriptIdAndLogsWarning()
    {
        var mockAi = Substitute.For<IAiProvider>();
        var (engine, _, scriptsDir, svcLogger, _) = Create(mockAi);
        File.WriteAllText(Path.Combine(scriptsDir, "broken-prereq.json"), "{invalid json structure");  // 结构坏（语法错）

        using var cts = new CancellationTokenSource();
        var ex = await Assert.ThrowsAsync<PrerequisiteScriptFailedException>(() =>
            engine.RecordAsync("test-doc", "任务", "broken-prereq", headless: true, userResponse: null, ct: cts.Token));
        await engine.StopAsync();
        Assert.Contains("broken-prereq", ex.Message);  // 验值：scriptId 兜底（结构坏拿不到 script.Name）
        Assert.Contains("加载失败", ex.Message);  // ② 分支（"加载失败（文件损坏/JSON 格式错误）"）
        // 验可观测：GetScriptWithErrors catch 内 LogWarning 被调用（silent-success 防护）
        Assert.Contains(svcLogger.Logs, l => l.level == LogLevel.Warning && l.message.Contains("broken-prereq"));
        await mockAi.DidNotReceive().SendMessageAsync(Arg.Any<List<ChatMessage>>(), Arg.Any<List<ChatTool>?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>① 文件不存在前置 → 跳过 + OnLog "不存在，跳过" 可观测 + 不抛 PrerequisiteScriptFailedException（前置可选，非病态）。</summary>
    [Fact]
    public async Task NonexistentPrerequisite_SkipsWithOnLogAndNoThrow()
    {
        var mockAi = Substitute.For<IAiProvider>();
        // 前置跳过后进录制循环：第1次 done 完成，第2次空文本（总结）
        mockAi.SendMessageAsync(Arg.Any<List<ChatMessage>>(), Arg.Any<List<ChatTool>?>(), Arg.Any<CancellationToken>())
            .Returns(
                new AiResponse { ToolCalls = [new AiToolCall { Id = "1", Name = "done", Arguments = new() { ["summary"] = "完成" } }] },
                new AiResponse { Text = "" });

        var (engine, _, _, _, onLogMessages) = Create(mockAi);
        using var cts = new CancellationTokenSource();
        var script = await engine.RecordAsync("test-doc", "任务", "nonexistent-script", headless: true, userResponse: null, ct: cts.Token);
        await engine.StopAsync();
        Assert.NotNull(script);  // 不抛 PrerequisiteScriptFailedException，正常返回（前置跳过后录制完成）
        Assert.Contains(onLogMessages, m => m.Contains("不存在") && m.Contains("跳过"));  // 验可观测：OnLog 含"不存在，跳过"
    }

    /// <summary>④ 合法前置（有 aiGoal）→ 不抛 + 正常执行（errors 空→执行分支，空 steps 快速完成）。</summary>
    [Fact]
    public async Task ValidPrerequisite_ExecutesWithoutThrow()
    {
        var mockAi = Substitute.For<IAiProvider>();
        mockAi.SendMessageAsync(Arg.Any<List<ChatMessage>>(), Arg.Any<List<ChatTool>?>(), Arg.Any<CancellationToken>())
            .Returns(
                new AiResponse { ToolCalls = [new AiToolCall { Id = "1", Name = "done", Arguments = new() { ["summary"] = "完成" } }] },
                new AiResponse { Text = "" });

        var (engine, scriptService, _, _, _) = Create(mockAi);
        // 用 SaveScript 存合法前置（有 aiGoal，校验通过）
        scriptService.SaveScript(new ScriptV2
        {
            ScriptId = "valid-prereq",
            Name = "合法前置脚本",
            DocumentTypeId = "test-doc",
            Phases = [new PhaseNode { Kind = "phase", Name = "main", Type = "sequential", AiGoal = "前置目标", Steps = [] }]
        });

        using var cts = new CancellationTokenSource();
        var script = await engine.RecordAsync("test-doc", "任务", "valid-prereq", headless: true, userResponse: null, ct: cts.Token);
        await engine.StopAsync();
        Assert.NotNull(script);  // 前置 errors 空→执行（空 steps 无操作），不抛 PrerequisiteScriptFailedException
    }

    /// <summary>⑤ 前置含未知字段（step 级 ifrmae 拼错，业务校验通过但 schema 抓到）→ errors&gt;0 → throw PrerequisiteScriptFailedException（③ 分支，schema 错亦走此路径，消息含 script.Name）。</summary>
    [Fact]
    public async Task UnknownFieldPrerequisite_ThrowsWithSchemaError()
    {
        var mockAi = Substitute.For<IAiProvider>();
        var (engine, _, scriptsDir, _, _) = Create(mockAi);
        // 直接写含未知字段的脚本（业务校验通过：name/aiGoal/selector 齐全；schema stepNode additionalProperties:false 抓 ifrmae）
        File.WriteAllText(Path.Combine(scriptsDir, "unknown-prereq.json"),
            """{"scriptId":"unknown-prereq","name":"未知字段前置","phases":[{"kind":"phase","name":"main","type":"sequential","aiGoal":"前置目标","steps":[{"kind":"step","name":"s1","action":"click","ifrmae":"f1","selector":"#x"}]}]}""");

        using var cts = new CancellationTokenSource();
        var ex = await Assert.ThrowsAsync<PrerequisiteScriptFailedException>(() =>
            engine.RecordAsync("test-doc", "任务", "unknown-prereq", headless: true, userResponse: null, ct: cts.Token));
        await engine.StopAsync();
        Assert.Contains("未知字段前置", ex.Message);  // 验值：消息含 script.Name（schema 错也走 ③ 带 script.Name，业务读回成功能拿 name）
        Assert.Contains("校验失败", ex.Message);  // ③ 分支（preErrors.Count>0→throw）
        await mockAi.DidNotReceive().SendMessageAsync(Arg.Any<List<ChatMessage>>(), Arg.Any<List<ChatTool>?>(), Arg.Any<CancellationToken>());  // 未进录制循环
    }
}
