using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using NSubstitute;
using SmartFilling.App.Configuration;
using SmartFilling.App.Recording;
using SmartFilling.App.Services;
using SmartFilling.Engine.Ai;
using SmartFilling.Engine.Models;
using EngineILogger = SmartFilling.Engine.Logging.ILogger;

namespace SmartFilling.App.Tests;

/// <summary>
/// E-1~E-8（方向E 日志 + username 误伤 + #16 跳号 + 求助步骤号 + operate fallback off-by-one）测试（2026-07-15）。
/// 仿 Decision2MockTests/OperateDoubleStepTests mock-frame 模式：经生产 args->工具执行路径 + 断言验值不只验状态。
///
/// 覆盖：
/// - E-2：operate 失败(Success=false) / priority8 no-Add(Success=true 不 Add) 均不注册字段（CurrentSteps.Count 对比守卫）
/// - E-3/E-4：priority8 no-Add 循环 _totalSteps 不逐轮涨 + caller 求助步骤号=_totalSteps+1（稳定=1）
/// - E-1：LogAsync/求助question/tool call 三类信号落 Serilog（_logger.LogInformation）+ SerializeArgs 排除 value + 处理 JsonElement
/// - E-8：ValidateFallbackAsync 场景A 求助步骤号=totalSteps 参数（L578 传 _totalSteps+1 对齐）
/// </summary>
public class ELogGuardTests
{
    private static ScriptService CreateScriptService()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SmartFillingTest_" + Guid.NewGuid().ToString("N")[..8]);
        var mockEnv = Substitute.For<IWebHostEnvironment>();
        mockEnv.ContentRootPath.Returns(tempDir);
        var mockLogger = Substitute.For<ILogger<ScriptService>>();
        return new ScriptService(mockEnv, Options.Create(new AppOptions()), mockLogger);
    }

    private static RecordingEngine CreateEngine(IPage page, EngineILogger? mockLogger = null)
    {
        mockLogger ??= Substitute.For<EngineILogger>();
        var engine = new RecordingEngine(Substitute.For<IAiProvider>(), CreateScriptService(), mockLogger, new EngineOptions(), new AppOptions());
        engine.TestInjectedPage = page;
        return engine;
    }

    private static void WireHelp(RecordingEngine engine, string reply)
    {
        engine.OnRequestHelp += (a, q, s) => { engine.ReplyHelp(reply); return Task.CompletedTask; };
        engine.OnLog += (a, msg) => Task.CompletedTask;
    }

    /// <summary>创建 mock page：仅 MainFrame + bodyLocMock 自返（CountAsync=count，Locator->自身，FrameLocator->frameLocMock）。供主文档 selector 校验。</summary>
    private static IPage CreateMainDocPage(int count)
    {
        var mainFrameMock = Substitute.For<IFrame>();
        var browserMock = Substitute.For<IBrowser>();
        browserMock.IsConnected.Returns(true);
        var ctxMock = Substitute.For<IBrowserContext>();
        ctxMock.Browser.Returns(browserMock);
        ctxMock.Pages.Returns(new List<IPage>());
        var pageMock = Substitute.For<IPage>();
        pageMock.MainFrame.Returns(mainFrameMock);
        pageMock.Context.Returns(ctxMock);
        pageMock.Frames.Returns(new List<IFrame> { mainFrameMock });
        var bodyLocMock = Substitute.For<ILocator>();
        bodyLocMock.CountAsync().Returns(count);
        bodyLocMock.ClickAsync(Arg.Any<LocatorClickOptions?>()).Returns(Task.CompletedTask);
        bodyLocMock.DblClickAsync(Arg.Any<LocatorDblClickOptions?>()).Returns(Task.CompletedTask);
        bodyLocMock.First.Returns(bodyLocMock);
        bodyLocMock.Last.Returns(bodyLocMock);
        bodyLocMock.Locator(Arg.Any<string>(), Arg.Any<LocatorLocatorOptions?>()).Returns(bodyLocMock);
        var frameLocMock = Substitute.For<IFrameLocator>();
        frameLocMock.Locator(Arg.Any<string>(), Arg.Any<FrameLocatorLocatorOptions?>()).Returns(bodyLocMock);
        bodyLocMock.FrameLocator(Arg.Any<string>()).Returns(frameLocMock);
        pageMock.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions?>()).Returns(bodyLocMock);
        mainFrameMock.Locator(Arg.Any<string>(), Arg.Any<FrameLocatorOptions?>()).Returns(bodyLocMock);
        return pageMock;
    }

    /// <summary>创建 mock page：MainFrame + 一个子 frame（无 name/url + FrameElementAsync 返 Mock handle -> FallbackSelector xpath=(//iframe)[1] 脆弱链）。供 ValidateFallbackAsync 场景A。</summary>
    private static IPage CreatePageWithFragileFrame(int locatorCount)
    {
        var mainFrame = Substitute.For<IFrame>();
        var browserMock = Substitute.For<IBrowser>();
        browserMock.IsConnected.Returns(true);
        var ctxMock = Substitute.For<IBrowserContext>();
        ctxMock.Browser.Returns(browserMock);
        ctxMock.Pages.Returns(new List<IPage>());
        var pageMock = Substitute.For<IPage>();
        pageMock.MainFrame.Returns(mainFrame);
        pageMock.Context.Returns(ctxMock);
        var fm = Substitute.For<IFrame>();
        fm.Name.Returns("");
        fm.Url.Returns("");
        fm.ParentFrame.Returns(mainFrame);
        fm.FrameElementAsync().Returns(Substitute.For<IElementHandle>());
        var locMock = Substitute.For<ILocator>();
        locMock.CountAsync().Returns(locatorCount);
        locMock.Locator(Arg.Any<string>(), Arg.Any<LocatorLocatorOptions?>()).Returns(locMock);
        fm.Locator(Arg.Any<string>(), Arg.Any<FrameLocatorOptions?>()).Returns(locMock);
        pageMock.Frames.Returns(new List<IFrame> { mainFrame, fm });
        var bodyLocMock = Substitute.For<ILocator>();
        bodyLocMock.CountAsync().Returns(locatorCount);
        bodyLocMock.Locator(Arg.Any<string>(), Arg.Any<LocatorLocatorOptions?>()).Returns(locMock);
        var frameLocMock = Substitute.For<IFrameLocator>();
        frameLocMock.Locator(Arg.Any<string>(), Arg.Any<FrameLocatorLocatorOptions?>()).Returns(locMock);
        bodyLocMock.FrameLocator(Arg.Any<string>()).Returns(frameLocMock);
        pageMock.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions?>()).Returns(bodyLocMock);
        mainFrame.Locator(Arg.Any<string>(), Arg.Any<FrameLocatorOptions?>()).Returns(locMock);
        return pageMock;
    }

    private static Dictionary<string, object> OperateArgs(string action, string? selector = null, string? fieldName = null, string? fieldLabel = null)
    {
        var args = new Dictionary<string, object> { ["action"] = action, ["name"] = "s", ["description"] = "d" };
        if (selector != null) args["selector"] = selector;
        if (fieldName != null) args["fieldName"] = fieldName;  // 生产路径：ParseJsonElement String->CLR string，测试用 CLR string 是生产形态
        if (fieldLabel != null) args["fieldLabel"] = fieldLabel;
        return args;
    }

    /// <summary>从求助 question 提取"📍 步骤 N"的 N（验步骤号值，不只验存在）。</summary>
    private static int ExtractStepNumber(string question)
    {
        var m = System.Text.RegularExpressions.Regex.Match(question, @"📍 步骤 (\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : -1;
    }

    // ===== E-2：操作失败/未落盘跳过字段注册（CurrentSteps.Count 对比守卫）=====

    // E-2 核心1：operate 失败（selector count==0 -> IsCountZero Success=false）+ 传 fieldName/fieldLabel -> 不注册脏字段（修复前 L610 注册块无 result.Success 守卫 -> 失败留脏字段 -> 重试 FieldExists=true 触发 R7 重名误伤）。
    // 验值：Fields 空（失败不注册）+ ret 不含"必填状态"（失败 message 不被字段提示污染）+ TestStepCount==0（失败不落盘）。
    [Fact]
    public async Task E2_OperateFailed_NotRegistered_NoDirtyField()
    {
        var page = CreateMainDocPage(count: 0);  // selector count==0 -> execute 返 IsCountZero(Success=false)
        var engine = CreateEngine(page);
        WireHelp(engine, "a");  // count==0 求助选 (a) 重新定位（不填集合，不落盘）
        var args = OperateArgs("click", "//btn", fieldName: "myField", fieldLabel: "我的字段");

        var ret = await engine.ExecuteOperateAsync(args, CancellationToken.None);

        Assert.Equal(0, engine.TestStepCount);  // 失败不落盘
        Assert.Empty(engine.GetCurrentScript().Fields);  // E-2：失败跳过注册，无脏字段（验值非仅状态）
        Assert.DoesNotContain("必填状态", ret);  // E-2 附加：失败 message 不被 L770 字段提示污染
        Assert.Contains("用户回答", ret);  // count==0 求助 reply
    }

    // E-2 核心2：priority8 no-Add（Success=true 但 execute 内不 Add step）+ 传 fieldName/fieldLabel -> 不注册脏字段。
    // 覆盖 E-2 决策扩展：守卫 CurrentSteps.Count > stepsBefore 拦截 priority9/multimatch/priority8未接受 三条 Success=true-no-Add 路径（result.Success 守卫不够，这些路径 Success=true）。
    // 验值：Fields 不含 myField（no-Add 跳过注册）+ TestStepCount==0（no-Add 不落盘）。
    [Fact]
    public async Task E2_Priority8NoAdd_NotRegistered_NoDirtyField()
    {
        var page = CreateMainDocPage(count: 1);  // GUID selector 唯一（count=1）+ priority 8
        var engine = CreateEngine(page);
        WireHelp(engine, "f");  // caller priority help 选 (f)
        var args = OperateArgs("click", "//input[@id='37b9c6a8-1234-5678-abcd-ef0123456789']", fieldName: "myField", fieldLabel: "我的字段");

        var ret = await engine.ExecuteOperateAsync(args, CancellationToken.None);

        Assert.Equal(0, engine.TestStepCount);  // priority8 no-Add 不落盘（execute 内 L456 return 不 Add）
        Assert.False(engine.GetCurrentScript().Fields.Any(f => f.Name == "myField"));  // E-2：no-Add 跳过注册（验值非仅状态）
        Assert.Contains("用户回答", ret);  // caller priority help reply
    }

    // ===== E-3/E-4：#16 跳号 + caller 求助步骤号 +1 =====

    // E-3/E-4：priority8 no-Add 循环两轮 -> _totalSteps 不逐轮涨（E-3：L586 _totalSteps++ 移到成功落盘后，no-Add 路径不递增）
    // + caller 求助步骤号=_totalSteps+1=1（E-4：L832 BuildHelpQuestion(_totalSteps+1)）。
    // 验值：Round1 步骤号=1 + Round2 步骤号=1（E-3 修复前 L586 execute 前递增 -> Round2=2 逐轮涨；E-4 修复前传 _totalSteps -> Round1=0）。
    [Fact]
    public async Task E3E4_Priority8Loop_StepNumberStable_NotIncrementPerRound()
    {
        var page = CreateMainDocPage(count: 1);
        var engine = CreateEngine(page);
        string lastQ = "";
        engine.OnRequestHelp += (a, q, s) => { lastQ = q; engine.ReplyHelp("a"); return Task.CompletedTask; };  // 两轮选 (a) 重新定位（不填集合，循环触发）
        engine.OnLog += (a, msg) => Task.CompletedTask;
        var args = OperateArgs("click", "//input[@id='37b9c6a8-1234-5678-abcd-ef0123456789']", fieldName: "myField", fieldLabel: "我的");

        // Round1：priority8 no-Add -> caller help -> _totalSteps 不递增（E-3）-> 步骤号=1（_totalSteps+1，E-4）
        await engine.ExecuteOperateAsync(args, CancellationToken.None);
        Assert.Equal(0, engine.TestStepCount);
        Assert.Equal(1, ExtractStepNumber(lastQ));  // E-4：caller 求助步骤号=_totalSteps(0)+1=1

        // Round2：同 selector + 同 engine -> priority8 no-Add -> caller help -> _totalSteps 仍不递增（E-3）-> 步骤号仍=1
        lastQ = "";
        await engine.ExecuteOperateAsync(args, CancellationToken.None);
        Assert.Equal(0, engine.TestStepCount);
        Assert.Equal(1, ExtractStepNumber(lastQ));  // E-3：_totalSteps 不逐轮涨（Round2==Round1==1；修复前 Round2=2）
    }

    // ===== E-1：方向E 日志落 Serilog =====

    // E-1 落点1：LogAsync 输出落 Serilog（_logger.LogInformation）。selector count==0 失败 -> L851 LogAsync("录制提示: selector...未找到") + L890 LogAsync("录制失败:...")。
    // 验值：mockLogger.LogInformation 被调用含"未找到"（修复前 LogAsync 仅 OnLog->SignalR 不经 Serilog，app-*.log 找不到）。
    [Fact]
    public async Task E1_LogAsync_WritesToSerilog()
    {
        var page = CreateMainDocPage(count: 0);
        var mockLogger = Substitute.For<EngineILogger>();
        var engine = CreateEngine(page, mockLogger);
        WireHelp(engine, "a");
        var args = OperateArgs("click", "//btn", fieldName: "myField", fieldLabel: "我的");

        await engine.ExecuteOperateAsync(args, CancellationToken.None);

        mockLogger.Received().LogInformation(Arg.Is<string>(s => s!.Contains("未找到")));  // E-1：LogAsync 落 Serilog
    }

    // E-1 落点2：求助 question 落 Serilog（[Help] 前缀，不走 OnLog 避免重复推 ReceiveLog）。
    // 验值：mockLogger.LogInformation 被调用含"[Help]"（修复前 question 仅 OnRequestHelp->SignalR，app-*.log 无发起时 question）。
    [Fact]
    public async Task E1_HelpQuestion_WritesToSerilog()
    {
        var page = CreateMainDocPage(count: 0);
        var mockLogger = Substitute.For<EngineILogger>();
        var engine = CreateEngine(page, mockLogger);
        WireHelp(engine, "a");
        var args = OperateArgs("click", "//btn", fieldName: "myField", fieldLabel: "我的");

        await engine.ExecuteOperateAsync(args, CancellationToken.None);

        mockLogger.Received().LogInformation(Arg.Is<string>(s => s!.Contains("[Help]")));  // E-1：求助 question 落 Serilog
    }

    // E-1 落点3：tool call 参数落 Serilog（[ToolCall] 前缀）+ SerializeArgs 排除 value（敏感 fillData/PII）。
    // 验值：mockLogger.LogInformation 被调用含"[ToolCall]"+工具名+fieldName，不含 value 值"sensitive-data"（N2：排除 value）。
    [Fact]
    public async Task E1_ToolCall_LogsToSerilog_ExcludesValue()
    {
        var page = CreateMainDocPage(count: 0);
        var mockLogger = Substitute.For<EngineILogger>();
        var engine = CreateEngine(page, mockLogger);
        WireHelp(engine, "a");
        var tc = new AiToolCall { Id = "t1", Name = "operate", Arguments = new Dictionary<string, object>
        {
            ["action"] = "click", ["selector"] = "//btn", ["value"] = "sensitive-data", ["fieldName"] = "myField"
        } };

        await engine.ExecuteRecordingToolAsync(tc, CancellationToken.None);

        mockLogger.Received(1).LogInformation(Arg.Is<string>(s => s!.Contains("[ToolCall]") && s.Contains("operate") && s.Contains("myField") && !s.Contains("sensitive-data")));  // E-1：[ToolCall] 落 Serilog + 排除 value
    }

    // E-1 SerializeArgs：排除 value（敏感）+ 处理 JsonElement（生产形态：ParseJsonElement 保留 Number/Object/Array 为 JsonElement）。
    // 验值：CLR string/bool 序列化 + value 键不出现 + JsonElement Number/Array 用原始 JSON（GetRawText 经 JsonSerializer.Serialize）。
    [Fact]
    public void E1_SerializeArgs_ExcludesValue_AndHandlesJsonElement()
    {
        // CLR string + bool 值 + 排除 value
        var args = new Dictionary<string, object>
        {
            ["action"] = "click",
            ["fieldName"] = "user",
            ["value"] = "sensitive-data",  // 应排除（敏感 fillData/PII）
            ["pressEnter"] = true,
        };
        var json = RecordingEngine.SerializeArgs(args);
        using (var doc = System.Text.Json.JsonDocument.Parse(json))
        {
            Assert.Equal("click", doc.RootElement.GetProperty("action").GetString());
            Assert.Equal("user", doc.RootElement.GetProperty("fieldName").GetString());
            Assert.True(doc.RootElement.GetProperty("pressEnter").GetBoolean());
            Assert.False(doc.RootElement.TryGetProperty("value", out _));  // E-1 N2：排除 value 键
        }

        // JsonElement 值（生产形态：OpenAiProvider.ParseJsonElement 保留 Number/Object/Array 为 JsonElement）
        var args2 = new Dictionary<string, object>
        {
            ["action"] = "fill",
            ["timeout"] = System.Text.Json.JsonSerializer.SerializeToElement(500),  // Number JsonElement
            ["options"] = System.Text.Json.JsonSerializer.SerializeToElement(new[] { "a", "b" }),  // Array JsonElement
            ["value"] = "secret",  // 应排除
        };
        var json2 = RecordingEngine.SerializeArgs(args2);
        using (var doc2 = System.Text.Json.JsonDocument.Parse(json2))
        {
            Assert.Equal(500, doc2.RootElement.GetProperty("timeout").GetInt32());  // JsonElement Number 序列化
            Assert.Equal(new[] { "a", "b" }, doc2.RootElement.GetProperty("options").EnumerateArray().Select(e => e.GetString()).ToArray());  // JsonElement Array 序列化
            Assert.False(doc2.RootElement.TryGetProperty("value", out _));  // 排除 value
        }
    }

    // ===== E-8：operate fallback 校验步骤号 off-by-one（L578 传 _totalSteps+1 对齐 save_step）=====

    // E-8：ValidateFallbackAsync 场景A 求助（fallback selector 在脆弱 iframe -> RequestIframeFragileHelpAsync）步骤号=totalSteps 参数。
    // L578 传 _totalSteps+1（E-8 修复）-> 场景A 求助步骤号=_totalSteps+1，对齐 detect 求助（_totalSteps+1）+ save_step fallback（_totalSteps+1）。
    // 验值：传 totalSteps=7 -> 求助 question 步骤号=7（验 totalSteps 参数链路贯通到 BuildHelpQuestion）。
    [Fact]
    public async Task E8_ValidateFallback_ScenarioA_HelpStepNumber_FromTotalStepsParam()
    {
        var page = CreatePageWithFragileFrame(locatorCount: 1);  // fallback selector 在脆弱 iframe（无 name/url -> xpath=(//iframe)[1] 脆弱链）
        var engine = CreateEngine(page);
        string lastQ = "";
        engine.OnRequestHelp += (a, q, s) => { lastQ = q; engine.ReplyHelp("f"); return Task.CompletedTask; };
        engine.OnLog += (a, msg) => Task.CompletedTask;
        var fb = new StepFallback { Selector = "//input[@id='x']" };  // 在脆弱 iframe 内
        var step = new StepNode { Name = "s", Action = "click" };

        await engine.ValidateFallbackAsync(fb, 7, step, "click", "d", CancellationToken.None);  // 传 totalSteps=7（模拟 L578 _totalSteps+1 传参）

        Assert.Contains("📍 步骤 7", lastQ);  // E-8：场景A 求助步骤号=totalSteps 参数（L578 传 _totalSteps+1 -> 对齐 detect/caller/save_step）
    }

    // E-4 失败分流求助步骤号：selector count==0 -> 失败分流 count==0 求助 L899 BuildHelpQuestion(_totalSteps+1) -> 步骤号=1（_totalSteps=0 失败不递增 +1）。
    // 与 E3E4_Priority8Loop（caller L854 步骤号=1）互补，覆盖 E-4 失败分流路径（L881/L888/L899 同语义 +1）。
    [Fact]
    public async Task E4_FailureShuntCountZero_HelpStepNumber_One()
    {
        var page = CreateMainDocPage(count: 0);
        var engine = CreateEngine(page);
        string lastQ = "";
        engine.OnRequestHelp += (a, q, s) => { lastQ = q; engine.ReplyHelp("a"); return Task.CompletedTask; };
        engine.OnLog += (a, msg) => Task.CompletedTask;
        var args = OperateArgs("click", "//btn", fieldName: "myField", fieldLabel: "我的");

        await engine.ExecuteOperateAsync(args, CancellationToken.None);

        Assert.Equal(1, ExtractStepNumber(lastQ));  // E-4：失败分流 count==0 求助步骤号=_totalSteps(0)+1=1（失败不递增 E-3 + +1 E-4）
    }
}
