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
/// ⑤（a-a-4，2026-07-09）operate 双 step 彻底方案测试：前移 detect③/countZero/detect priority 8 三条 detect 信号求助到 ExecuteOperateAsync 落盘前。
/// 核心：求助在 execute 前 return -> 不落盘 step -> AI 重传单 step -> 回放 action 不执行 2 遍（断言 TestStepCount==0 无双 step）。
/// ExecuteOperateAsync 提 internal 供直调（TestStepCount 断言 CurrentSteps.Count）；求助经 OnRequestHelp + ReplyHelp 驱动。
/// 🟡 mock 边界：mock 验"前移求助 return 前 CurrentSteps.Count==0"（无双 step 核心），验不出 Playwright 真执行--真实浏览器 golden E2E 另测。
/// </summary>
public class OperateDoubleStepTests
{
    private static ScriptService CreateScriptService()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SmartFillingTest_" + Guid.NewGuid().ToString("N")[..8]);
        var mockEnv = Substitute.For<IWebHostEnvironment>();
        mockEnv.ContentRootPath.Returns(tempDir);
        var mockLogger = Substitute.For<ILogger<ScriptService>>();
        return new ScriptService(mockEnv, Options.Create(new AppOptions()), mockLogger);
    }

    private static RecordingEngine CreateEngine(IPage page)
    {
        var mockAi = Substitute.For<IAiProvider>();
        var mockLogger = Substitute.For<EngineILogger>();
        var engine = new RecordingEngine(mockAi, CreateScriptService(), mockLogger, new EngineOptions(), new AppOptions());
        engine.TestInjectedPage = page;
        return engine;
    }

    private static IPage CreateMainDocPage(int count)
    {
        var mainFrameMock = Substitute.For<IFrame>();
        var browserMock = Substitute.For<IBrowser>();
        browserMock.IsConnected.Returns(true);
        var ctxMock = Substitute.For<IBrowserContext>();
        ctxMock.Browser.Returns(browserMock);
        var pageMock = Substitute.For<IPage>();
        pageMock.MainFrame.Returns(mainFrameMock);
        pageMock.Context.Returns(ctxMock);
        pageMock.Frames.Returns(new List<IFrame> { mainFrameMock });
        var bodyLocMock = Substitute.For<ILocator>();
        bodyLocMock.CountAsync().Returns(count);
        bodyLocMock.Locator(Arg.Any<string>(), Arg.Any<LocatorLocatorOptions?>()).Returns(bodyLocMock);
        pageMock.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions?>()).Returns(bodyLocMock);
        mainFrameMock.Locator(Arg.Any<string>(), Arg.Any<FrameLocatorOptions?>()).Returns(bodyLocMock);
        return pageMock;
    }

    private static IPage CreatePageWithFrames(params (string name, string url, int locatorCount, string? bodyText)[] childSpecs)
    {
        var mainFrame = Substitute.For<IFrame>();
        var browserMock = Substitute.For<IBrowser>();
        browserMock.IsConnected.Returns(true);
        var ctxMock = Substitute.For<IBrowserContext>();
        ctxMock.Browser.Returns(browserMock);
        var pageMock = Substitute.For<IPage>();
        pageMock.MainFrame.Returns(mainFrame);
        pageMock.Context.Returns(ctxMock);
        var frames = new List<IFrame> { mainFrame };
        foreach (var (name, url, count, body) in childSpecs)
        {
            var fm = Substitute.For<IFrame>();
            fm.Name.Returns(name);
            fm.Url.Returns(url);
            fm.ParentFrame.Returns(mainFrame);
            fm.FrameElementAsync().Returns(Substitute.For<IElementHandle>());
            var locMock = Substitute.For<ILocator>();
            locMock.CountAsync().Returns(count);
            fm.Locator(Arg.Any<string>(), Arg.Any<FrameLocatorOptions?>()).Returns(locMock);
            if (body != null)
                fm.EvaluateAsync<string>(Arg.Any<string>(), Arg.Any<object?>()).Returns(body);
            frames.Add(fm);
        }
        pageMock.Frames.Returns(frames);
        return pageMock;
    }

    private static Dictionary<string, object> OperateArgs(string action, string? selector = null, DetectCondition? condition = null)
    {
        var args = new Dictionary<string, object> { ["action"] = action, ["name"] = "s", ["description"] = "d" };
        if (selector != null) args["selector"] = selector;
        if (condition != null) args["condition"] = System.Text.Json.JsonSerializer.SerializeToElement(condition);
        return args;
    }

    private static void WireHelp(RecordingEngine engine, string reply)
    {
        engine.OnRequestHelp += (a, q, s) => { engine.ReplyHelp(reply); return Task.CompletedTask; };
        engine.OnLog += (a, msg) => Task.CompletedTask;
    }

    // ⑤ 核心 1：detect③ 多 frame（step.Condition page_contains 在 2 个 iframe 都命中）-> 前移 picker（execute 前 return）-> 不落盘 step（无双 step）。
    [Fact]
    public async Task Operate_DetectMultiFrame_PickerBeforeExecute_NoDoubleStep()
    {
        var page = CreatePageWithFrames(("frame-a", "http://a", 0, "提交成功"), ("frame-b", "http://b", 0, "操作成功"));
        var engine = CreateEngine(page);
        WireHelp(engine, "j 1");
        var args = OperateArgs("click", "//btn", new DetectCondition { Type = "page_contains", Keywords = new[] { "成功" } });

        var ret = await engine.ExecuteOperateAsync(args, CancellationToken.None);

        Assert.Equal(0, engine.TestStepCount);  // 前移 picker：execute 前 return，不落盘 step（无双 step 核心）
        Assert.Contains("frame", ret);  // picker reply（含 frame 链提示）
    }

    // ⑤ 核心 2：detect count==0（step.Condition selector_exists 找不到）-> 前移 (h) 求助（execute 前 return）-> 不落盘 step。
    [Fact]
    public async Task Operate_DetectCountZero_HelpBeforeExecute_NoDoubleStep()
    {
        var page = CreateMainDocPage(count: 0);
        var engine = CreateEngine(page);
        WireHelp(engine, "h");
        var args = OperateArgs("click", "//btn", new DetectCondition { Type = "selector_exists", Selector = "//missing" });

        var ret = await engine.ExecuteOperateAsync(args, CancellationToken.None);

        Assert.Equal(0, engine.TestStepCount);  // 前移 (h)：execute 前 return，不落盘 step
        Assert.Contains("用户回答", ret);  // 求助 reply
    }

    // ⑤ 核心 3：detect priority 8（step.Condition selector_visible GUID 脆弱）-> 前移 (f) 求助（execute 前 return）-> 不落盘 step。
    [Fact]
    public async Task Operate_DetectPriority8_HelpBeforeExecute_NoDoubleStep()
    {
        var page = CreateMainDocPage(count: 1);  // 唯一（strict 校验通过）+ GUID 脆弱
        var engine = CreateEngine(page);
        WireHelp(engine, "f");
        var args = OperateArgs("click", "//btn", new DetectCondition { Type = "selector_visible", Selector = "//*[@id='37b9c6a8-1234-5678-abcd-ef0123456789']" });

        var ret = await engine.ExecuteOperateAsync(args, CancellationToken.None);

        Assert.Equal(0, engine.TestStepCount);  // 前移 (f)：execute 前 return，不落盘 step
        Assert.Contains("用户回答", ret);
    }

    // ⑤ 对照 4：step selector priority 8（action selector GUID 脆弱）-> execute 内 L438 不 Add（无双 step）-> caller 后置求助（保持后置）。
    // 验 ⑤ 简化 caller（删 detectIsFragileSource）后 step selector priority 8 路径仍求助 + 仍无双 step（executor 不 Add）。
    [Fact]
    public async Task Operate_StepSelectorPriority8_HelpAfterExecute_NoDoubleStep()
    {
        var page = CreateMainDocPage(count: 1);  // action selector GUID 唯一（count=1）
        var engine = CreateEngine(page);
        WireHelp(engine, "f");
        var args = OperateArgs("click", "//*[@id='37b9c6a8-1234-5678-abcd-ef0123456789']");  // GUID -> AssessFragility 8

        var ret = await engine.ExecuteOperateAsync(args, CancellationToken.None);

        Assert.Equal(0, engine.TestStepCount);  // execute 内 priority 8 不 Add（L438 return）-> 无双 step
        Assert.Contains("用户回答", ret);  // caller 后置求助（L760 简化后仍触发）
    }
}
