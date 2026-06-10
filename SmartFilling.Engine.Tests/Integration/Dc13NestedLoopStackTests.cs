using Microsoft.Playwright;
using NSubstitute;
using SmartFilling.Engine.Engine;
using SmartFilling.Engine.Models;
using SmartFilling.Engine.Tests.Helpers;
using Xunit;
using ExecutionContext = SmartFilling.Engine.Engine.ExecutionContext;

namespace SmartFilling.Engine.Tests.Integration;

/// <summary>
/// 24 DC13（a-a-4 B，2026-07-08）：new_row_appears _lastRowCountStack 栈协同嵌套 loop 隔离测试。
/// 验证嵌套 loop 各自独立栈层（每行迭代 Push、出口 Pop 对称；Peek 取当前 loop 基线）--防回归：原单 key _lastRowCount 共享，
/// 内层覆盖外层 -> 外层 new_row_appears 比对错 silent（DC13 评级 🔴）。冒烟：嵌套 loop + new_row_appears 执行不崩 = 栈 Push/Pop 对称不 leak。
/// </summary>
public class Dc13NestedLoopStackTests
{
    private readonly NullLogger _logger = new();

    private static EngineOptions DefaultOptions() => new()
    {
        MaxScriptDuration = 600000,
        MaxLoopCount = 100,
        Rerun = new RerunOptions { MaxRowRerunCount = 2, MaxPhaseRerunCount = 2, Interval = 1 },
        StepRetry = new StepRetry { Count = 0 }
    };

    /// <summary>创建 mock page：MainFrame.Locator(any).CountAsync()=count（供 new_row_appears 基线 + 当前比对，ResolveChain 主文档->MainFrame）。</summary>
    private static IPage CreatePageWithRowCount(int count)
    {
        var mainFrameMock = Substitute.For<IFrame>();
        var locMock = Substitute.For<ILocator>();
        locMock.CountAsync().Returns(count);
        mainFrameMock.Locator(Arg.Any<string>(), Arg.Any<FrameLocatorOptions?>()).Returns(locMock);
        var browserMock = Substitute.For<IBrowser>();
        browserMock.IsConnected.Returns(true);
        var ctxMock = Substitute.For<IBrowserContext>();
        ctxMock.Browser.Returns(browserMock);
        var pageMock = Substitute.For<IPage>();
        pageMock.MainFrame.Returns(mainFrameMock);
        pageMock.Context.Returns(ctxMock);
        pageMock.Url.Returns("about:blank");
        pageMock.Frames.Returns(new List<IFrame> { mainFrameMock });
        return pageMock;
    }

    // 嵌套 loop（外层 Orders + 内层 Details）+ 内层 check new_row_appears：栈 Push/Pop 对称不崩（leak/不对称会 Pop 空或栈错->执行异常）。
    [Fact]
    public async Task NestedLoop_InnerNewRowAppears_StackSymmetricNoCrash()
    {
        var fillData = new Dictionary<string, object>
        {
            { "orders", new List<Dictionary<string, object>>
            {
                new() { { "details", new List<Dictionary<string, object>> { new() { { "item", "i1" } } } } }
            }
            }
        };
        var script = new ScriptBuilder()
            .AddPhase("Orders", type: "loop", loopSource: "orders",
                steps:
                [
                    new PhaseNode
                    {
                        Kind = "phase", Name = "Details", Type = "loop", LoopSource = "details",
                        Steps = [ScriptBuilder.CheckStep(new DetectCondition { Type = "new_row_appears", Selector = "//tr" }, "nothing")]
                    }
                ])
            .Build();
        var page = CreatePageWithRowCount(count: 1);  // 基线=1，当前=1 -> 行数不变 -> detect=false -> then=nothing（软检查不阻断）
        var engine = new ScriptEngine(_logger, DefaultOptions());

        var result = await engine.ExecuteAsync(script, fillData, page);

        Assert.True(result.Success, result.ErrorMessage ?? "");  // 嵌套 loop + new_row_appears 栈 Push/Pop 对称不崩
    }

    // 单层 loop + new_row_appears（基线=当前 -> detect=false -> nothing）：DC13 栈基本 Push/Pop 工作（基线栈 Peek 取值正确）。
    [Fact]
    public async Task SingleLoop_NewRowAppears_StackPushPopWorks()
    {
        var fillData = new Dictionary<string, object>
        {
            { "items", new List<Dictionary<string, object>> { new() { { "name", "r1" } } } }
        };
        var script = new ScriptBuilder()
            .AddPhase("Loop", type: "loop", loopSource: "items",
                steps: [ScriptBuilder.CheckStep(new DetectCondition { Type = "new_row_appears", Selector = "//tr" }, "nothing")])
            .Build();
        var page = CreatePageWithRowCount(count: 1);
        var engine = new ScriptEngine(_logger, DefaultOptions());

        var result = await engine.ExecuteAsync(script, fillData, page);

        Assert.True(result.Success, result.ErrorMessage ?? "");  // 单层 loop + new_row_appears 栈 Push/Pop + Peek 比对正常
    }

    // A3（a-a-4 测试增强）：DC13 栈隔离语义--嵌套 loop 各自独立栈层（内层 Push/Pop 不污染外层 Peek）。
    // 直测 PushRowCountStack/PopRowCountStack（提 internal）：验证 DC13 修复核心保证--原单 key _lastRowCount 共享，内层覆盖外层 -> 外层 new_row_appears 比对错 silent；栈修复后隔离。
    [Fact]
    public void RowCountStack_NestedPushPop_InnerDoesNotPolluteOuter()
    {
        var ctx = new ExecutionContext();
        ScriptEngine.PushRowCountStack(ctx, 5);  // 外层 loop 基线
        Assert.Equal(5, StackPeek(ctx));  // 外层 Peek=5

        ScriptEngine.PushRowCountStack(ctx, 3);  // 内层 loop 基线（嵌套）
        Assert.Equal(3, StackPeek(ctx));  // 内层 Peek=3（内层覆盖外层 Peek，但只在作用域内）

        ScriptEngine.PopRowCountStack(ctx);  // 内层出口 Pop
        Assert.Equal(5, StackPeek(ctx));  // ★ 外层 Peek 恢复 5（不被内层 3 污染--DC13 隔离核心，断言验值）

        ScriptEngine.PopRowCountStack(ctx);  // 外层出口 Pop
        Assert.Empty(GetStack(ctx));  // 栈空（Push/Pop 对称不 leak）
    }

    // A3 守卫：栈不存在 / 空时 Pop 不抛（防 leak 的对称 Pop，嵌套 loop break/异常出口守卫）。
    [Fact]
    public void RowCountStack_PopOnEmptyOrCreate_GuardedNoThrow()
    {
        var ctx = new ExecutionContext();
        var ex = Record.Exception(() =>
        {
            ScriptEngine.PopRowCountStack(ctx);  // 栈不存在（key 未建）
            ScriptEngine.PushRowCountStack(ctx, 1);
            ScriptEngine.PopRowCountStack(ctx);
            ScriptEngine.PopRowCountStack(ctx);  // 栈空
        });
        Assert.Null(ex);  // 守卫：不抛（任意出口对称 Pop 安全）
    }

    private static System.Collections.Generic.Stack<int> GetStack(ExecutionContext ctx)
        => (System.Collections.Generic.Stack<int>)ctx.Vars["_lastRowCountStack"];
    private static int StackPeek(ExecutionContext ctx) => GetStack(ctx).Peek();
}
