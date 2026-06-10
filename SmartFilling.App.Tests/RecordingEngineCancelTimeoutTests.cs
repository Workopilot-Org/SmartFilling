using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
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
/// 录制取消/超时/maxTurns 终态测试：验证 RecordingEngine.RecordAsync 三种非正常结束都外抛 RecordingCancelledException（Cancelled），
/// 正常 done 返回脚本（Success）。修复前取消/超时被吞伪装 Success（silent-success）。
///
/// 可测性：App.Tests 经 InternalsVisibleTo 访问 internal TestInjectedPage 注入 mock IPage 绕过真实浏览器；用 NSubstitute mock IAiProvider。
/// 取消/超时测试用 request_help 阻塞锚点（await tcs.Task）确保停在 await 点等 cts 触发，规避循环空转跑满 maxTurns 走 MaxTurnsExhausted；
/// MaxTurnsExhausted 测试反过来用纯文本空转 + maxTurns=3 主动触发轮次耗尽。
/// </summary>
public class RecordingEngineCancelTimeoutTests
{
    /// <summary>构造测试用 RecordingEngine：mock IAiProvider + 真实 ScriptService（mock 依赖，不调其方法）+ mock IPage（internal 注入绕过浏览器）。</summary>
    private static RecordingEngine CreateEngine(IAiProvider mockAi, EngineOptions engineOptions, AppOptions appOptions)
    {
        var mockLogger = Substitute.For<EngineILogger>();
        var scriptService = CreateScriptService(appOptions);
        var engine = new RecordingEngine(mockAi, scriptService, mockLogger, engineOptions, appOptions);
        engine.TestInjectedPage = CreateMockPage();
        // 🔴 必须订阅 OnLog：RecordAsync 多处 `await OnLog?.Invoke(...)`，OnLog=null 时 Invoke 返 null -> await null 抛 NRE -> catch(Exception) 吞 -> 返 Success（伪装成功）。
        engine.OnLog += (_, _) => Task.CompletedTask;
        // OnScreenshot/OnRequestHelp 不订阅：request_help 内 `if(OnRequestHelp != null)` 为 false 跳过截图段，直接 await tcs.Task 阻塞。
        return engine;
    }

    private static ScriptService CreateScriptService(AppOptions appOptions)
    {
        // ContentRootPath 指临时目录（构造会 CreateDirectory + LoadDocuments，测试不调其方法）
        var tempDir = Path.Combine(Path.GetTempPath(), "SmartFillingTest_" + Guid.NewGuid().ToString("N")[..8]);
        var mockEnv = Substitute.For<IWebHostEnvironment>();
        mockEnv.ContentRootPath.Returns(tempDir);
        var mockLogger = Substitute.For<ILogger<ScriptService>>();
        return new ScriptService(mockEnv, Options.Create(appOptions), mockLogger);
    }

    /// <summary>mock IPage 通过 EnsureBrowserHealthy（Context.Browser.IsConnected + !IsClosed）。loose mock IsClosed 默认 false。</summary>
    private static IPage CreateMockPage()
    {
        var mockBrowser = Substitute.For<IBrowser>();
        mockBrowser.IsConnected.Returns(true);
        var mockContext = Substitute.For<IBrowserContext>();
        mockContext.Browser.Returns(mockBrowser);
        var mockPage = Substitute.For<IPage>();
        mockPage.Context.Returns(mockContext);
        return mockPage;
    }

    /// <summary>手动取消：request_help 阻塞 -> MarkManualCancel + Cancel -> ManualStop + "用户取消录制"。</summary>
    [Fact]
    public async Task ManualCancel_ViaRequestHelp_ThrowsManualStop()
    {
        var mockAi = Substitute.For<IAiProvider>();
        mockAi.SendMessageAsync(Arg.Any<List<ChatMessage>>(), Arg.Any<List<ChatTool>?>(), Arg.Any<CancellationToken>())
            .Returns(new AiResponse
            {
                ToolCalls = [new AiToolCall { Id = "1", Name = "request_help", Arguments = new() { ["question"] = "测试问题" } }]
            });

        var engine = CreateEngine(mockAi, new EngineOptions { MaxScriptDuration = 600000 }, new AppOptions { MaxRecordTurns = 100 });
        using var cts = new CancellationTokenSource();

        // 启动录制（turn 0 mock 返回 request_help -> ExecuteRequestHelpAsync 阻塞 await tcs.Task）
        var recordTask = engine.RecordAsync("docType", "任务", null, headless: true, userResponse: null, ct: cts.Token);

        // 等 RecordAsync 阻塞在 request_help 的 await 点（mock 同步返回，几 ms 即到，200ms 余量防 CI 慢机器）
        await Task.Delay(200);
        engine.MarkManualCancel("用户取消录制");  // 标记手动（必须在 Cancel 前）
        cts.Cancel();  // 触发：tcs.TrySetCanceled -> ExecuteRequestHelpAsync catch OCE 返回字符串 -> 下一轮 ThrowIfCancellationRequested 抛 OCE -> RecordAsync catch(OCE)

        var ex = await Assert.ThrowsAsync<RecordingCancelledException>(() => recordTask);
        Assert.Equal(RecordingCancelReason.ManualStop, ex.Reason);
        Assert.Equal("用户取消录制", ex.Message);  // 验值：区分原因的 errorMessage
    }

    /// <summary>总超时：request_help 阻塞 -> cts 自动到期（不调 MarkManualCancel，_cancelReason 空）-> Timeout + 文案含"超时"。</summary>
    [Fact]
    public async Task TotalTimeout_ViaRequestHelp_ThrowsTimeout()
    {
        var mockAi = Substitute.For<IAiProvider>();
        mockAi.SendMessageAsync(Arg.Any<List<ChatMessage>>(), Arg.Any<List<ChatTool>?>(), Arg.Any<CancellationToken>())
            .Returns(new AiResponse
            {
                ToolCalls = [new AiToolCall { Id = "1", Name = "request_help", Arguments = new() { ["question"] = "测试问题" } }]
            });

        // MaxScriptDuration=600000（minutes 计算用）；cts 500ms 自动到期（不调 MarkManualCancel -> _cancelReason 空 = 超时）
        var engine = CreateEngine(mockAi, new EngineOptions { MaxScriptDuration = 600000 }, new AppOptions { MaxRecordTurns = 100 });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var ex = await Assert.ThrowsAsync<RecordingCancelledException>(() =>
            engine.RecordAsync("docType", "任务", null, headless: true, userResponse: null, ct: cts.Token));
        Assert.Equal(RecordingCancelReason.Timeout, ex.Reason);
        Assert.Contains("超时", ex.Message);  // minutes=10（600000/60000），文案"录制超时(10分钟)"
    }

    /// <summary>maxTurns 耗尽：纯文本空转 + maxTurns=3 -> MaxTurnsExhausted（原走 Success 的 silent-success）。</summary>
    [Fact]
    public async Task MaxTurnsExhausted_ThrowsMaxTurns()
    {
        var mockAi = Substitute.For<IAiProvider>();
        mockAi.SendMessageAsync(Arg.Any<List<ChatMessage>>(), Arg.Any<List<ChatTool>?>(), Arg.Any<CancellationToken>())
            .Returns(new AiResponse { Text = "继续录制中", ToolCalls = null });  // 纯文本不 done -> 循环空转

        var engine = CreateEngine(mockAi, new EngineOptions { MaxScriptDuration = 600000 }, new AppOptions { MaxRecordTurns = 3 });
        using var cts = new CancellationTokenSource();

        var ex = await Assert.ThrowsAsync<RecordingCancelledException>(() =>
            engine.RecordAsync("docType", "任务", null, headless: true, userResponse: null, ct: cts.Token));
        Assert.Equal(RecordingCancelReason.MaxTurnsExhausted, ex.Reason);
        Assert.Contains("轮次限制", ex.Message);  // "录制达到最大轮次限制(3轮)"
    }

    /// <summary>正常完成：done 工具调用 -> 返回脚本（不抛 RecordingCancelledException，走 Success）。</summary>
    [Fact]
    public async Task NormalDone_ReturnsScript()
    {
        var mockAi = Substitute.For<IAiProvider>();
        // 第1次（RecordAsync 循环）返回 done；第2次（SummarizePhaseGoalsAsync 总结）返回空文本（解析失败->catch->返回原 script）
        mockAi.SendMessageAsync(Arg.Any<List<ChatMessage>>(), Arg.Any<List<ChatTool>?>(), Arg.Any<CancellationToken>())
            .Returns(
                new AiResponse { ToolCalls = [new AiToolCall { Id = "1", Name = "done", Arguments = new() { ["summary"] = "完成" } }] },
                new AiResponse { Text = "" });

        var engine = CreateEngine(mockAi, new EngineOptions { MaxScriptDuration = 600000 }, new AppOptions { MaxRecordTurns = 10 });
        using var cts = new CancellationTokenSource();

        var script = await engine.RecordAsync("docType", "任务", null, headless: true, userResponse: null, ct: cts.Token);
        Assert.NotNull(script);
        Assert.IsType<ScriptV2>(script);  // 正常返回脚本，未抛 RecordingCancelledException
    }

    /// <summary>边界（2026-06-29 核查发现）：request_help 在最后一轮（turn=maxTurns-1）被取消时，
    /// ExecuteRequestHelpAsync catch(OCE) 吞掉返回字符串但循环已结束（无下一轮 ThrowIfCancellationRequested 传播取消），
    /// 修复前会误报 MaxTurnsExhausted；修复后（227 块首 ct.ThrowIfCancellationRequested）应正确走 ManualStop。</summary>
    [Fact]
    public async Task ManualCancel_OnLastTurnRequestHelp_ThrowsManualStop_NotMaxTurns()
    {
        var mockAi = Substitute.For<IAiProvider>();
        // turn 0,1 纯文本空转；turn 2（最后一轮，maxTurns=3）返回 request_help 阻塞
        mockAi.SendMessageAsync(Arg.Any<List<ChatMessage>>(), Arg.Any<List<ChatTool>?>(), Arg.Any<CancellationToken>())
            .Returns(
                new AiResponse { Text = "继续录制中", ToolCalls = null },
                new AiResponse { Text = "继续录制中", ToolCalls = null },
                new AiResponse { ToolCalls = [new AiToolCall { Id = "1", Name = "request_help", Arguments = new() { ["question"] = "最后一轮求助" } }] });

        var engine = CreateEngine(mockAi, new EngineOptions { MaxScriptDuration = 600000 }, new AppOptions { MaxRecordTurns = 3 });
        using var cts = new CancellationTokenSource();

        // 启动录制：turn 0,1 纯文本快速空转，turn 2 request_help 阻塞 await tcs.Task
        var recordTask = engine.RecordAsync("docType", "任务", null, headless: true, userResponse: null, ct: cts.Token);

        await Task.Delay(300);  // 等 turn 0,1,2 到 request_help 阻塞
        engine.MarkManualCancel("用户取消录制");
        cts.Cancel();  // 触发：tcs.TrySetCanceled -> ExecuteRequestHelpAsync catch OCE 返回字符串 -> 循环结束 -> 227 ct.ThrowIfCancellationRequested（修复）-> catch(OCE)

        var ex = await Assert.ThrowsAsync<RecordingCancelledException>(() => recordTask);
        Assert.Equal(RecordingCancelReason.ManualStop, ex.Reason);  // 修复后：ManualStop（修复前会是 MaxTurnsExhausted）
        Assert.Equal("用户取消录制", ex.Message);  // 验值：区分原因的 errorMessage
    }

    /// <summary>IsBrowserCrashException 应识别 EnsureBrowserHealthy 的中文消息（修复 silent-success：
    /// 原只匹配英文，中文消息->catch(Exception)->非崩溃->return GetCurrentScript() 伪装成功）。英文 Playwright 异常不回归。</summary>
    [Theory]
    [InlineData("Target closed")]        // Playwright 英文
    [InlineData("Browser closed")]       // Playwright 英文
    [InlineData("browser disconnected")] // Playwright 英文（验 OrdinalIgnoreCase）
    [InlineData("浏览器已断开连接")]      // EnsureBrowserHealthy 中文（_page==null/Context.Browser==null/!IsConnected）
    [InlineData("页面已关闭")]            // EnsureBrowserHealthy 中文（_page.IsClosed）
    public void IsBrowserCrashException_RecognizesCrash_ChineseAndEnglish(string message)
    {
        Assert.True(RecordingEngine.IsBrowserCrashException(new InvalidOperationException(message)));
    }

    [Theory]
    [InlineData("普通录制异常")]
    [InlineData("AI API 抖动")]
    [InlineData("工具执行失败(operate)")]
    public void IsBrowserCrashException_RejectsNonCrash(string message)
    {
        Assert.False(RecordingEngine.IsBrowserCrashException(new Exception(message)));
    }

    /// <summary>前置脚本期间取消（总超时）-> Cancelled（非 Failed）。
    /// 修复前：ExecuteAsync catch(OCE) 返回 Success=false -> RecordAsync 抛 PrerequisiteScriptFailedException -> Failed；
    /// 修复后：RecordAsync:130 ct.ThrowIfCancellationRequested -> OCE -> catch(OCE) -> RCE。
    /// 注：前置脚本在 RecordAsync if(_page==null) 浏览器创建块内，须用真实 headless 浏览器（不注入 TestInjectedPage）。</summary>
    [Fact]
    public async Task Cancel_DuringPrerequisiteScript_ThrowsCancelled_NotFailed()
    {
        var mockAi = Substitute.For<IAiProvider>();
        var appOptions = new AppOptions();
        var engineOptions = new EngineOptions { MaxScriptDuration = 600000, Headless = true };
        var scriptService = CreateScriptService(appOptions);
        scriptService.SaveScript(new ScriptV2
        {
            ScriptId = "prereq-cancel-test",
            Name = "前置取消测试",
            DocumentTypeId = "test-doc",
            Phases = [new PhaseNode { Kind = "phase", Name = "main", Type = "sequential", AiGoal = "前置测试目标", Steps = [] }]
        });
        var engine = new RecordingEngine(mockAi, scriptService, Substitute.For<EngineILogger>(), engineOptions, appOptions);
        engine.OnLog += (_, _) => Task.CompletedTask;

        using var cts = new CancellationTokenSource();
        cts.Cancel();  // 预先取消（模拟总超时）

        var ex = await Assert.ThrowsAsync<RecordingCancelledException>(() =>
            engine.RecordAsync("test-doc", "任务", "prereq-cancel-test", headless: true, userResponse: null, ct: cts.Token));
        Assert.Equal(RecordingCancelReason.Timeout, ex.Reason);  // _cancelReason 空=总超时
        Assert.Contains("超时", ex.Message);
        // AI 未被调用（前置脚本取消，未进录制循环）
        await mockAi.DidNotReceive().SendMessageAsync(Arg.Any<List<ChatMessage>>(), Arg.Any<List<ChatTool>?>(), Arg.Any<CancellationToken>());
    }
}
