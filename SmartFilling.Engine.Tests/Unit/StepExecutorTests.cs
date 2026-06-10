using Microsoft.Playwright;
using NSubstitute;
using SmartFilling.Engine.Engine;
using SmartFilling.Engine.Models;
using SmartFilling.Engine.Tests.Helpers;
using ExecutionContext = SmartFilling.Engine.Engine.ExecutionContext;
using Xunit;

namespace SmartFilling.Engine.Tests.Unit;

/// <summary>
/// C⑨：StepExecutor 运行时越界/二选一校验单测（复用 MockPageFactory + ExecutionContext，不改 StepExecutor 生产代码）。
/// 锁批次8（switchTab/closeTab 越界 throw，撤销 Y14 静默）+ 批次9③（scroll 严格二选一 + 非法 direction throw，撤销静默 no-op）。
/// 这些是 silent-success 高发点：原行为是 LogWarning + 静默（保持当前页/跳过/忽略 selector），任务仍 Completed 不报错。
/// 测试断言「抛异常类型 + 消息含关键词」（验值不只验抛），覆盖越界/同传/都不传/非法 direction 四态。
/// throw 全部发生在 DOM 操作前，MockPageFactory 基础 mock（Url/Context.Pages/主体 Locator）即可，无需真实浏览器。
/// </summary>
public class StepExecutorTests
{
    private static StepExecutor CreateExecutor() =>
        new(new DetectEvaluator(new NullLogger()), new EngineOptions(), new NullLogger());

    private static ExecutionContext CreateCtx(IPage page) =>
        new() { Page = page, ActivePage = page, Ct = default };

    private static ScriptV2 MinimalScript() => new()
    {
        Name = "test",
        Phases = [new PhaseNode { Kind = "phase", Name = "main", Type = "sequential", AiGoal = "test" }]
    };

    // ===== switchTab / closeTab 越界（批次8：撤销 Y14 静默，越界抛 InvalidOperationException 触发 onError）=====

    [Fact]
    public async Task SwitchTab_OutOfBounds_ThrowsInvalidOperationException()
    {
        MockPageFactory.Create(out var page);
        var executor = CreateExecutor();
        var ctx = CreateCtx(page);
        var step = ScriptBuilder.Step("switchTab", index: 99, name: "oob");  // 仅 1 个标签页，99 越界

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.ExecuteAsync(step, ctx, MinimalScript(), null));
        Assert.Contains("switchTab", ex.Message);
        Assert.Contains("越界", ex.Message);
    }

    [Fact]
    public async Task CloseTab_OutOfBounds_ThrowsInvalidOperationException()
    {
        MockPageFactory.Create(out var page);
        var executor = CreateExecutor();
        var ctx = CreateCtx(page);
        var step = ScriptBuilder.Step("closeTab", index: 99, name: "oob");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.ExecuteAsync(step, ctx, MinimalScript(), null));
        Assert.Contains("closeTab", ex.Message);
        Assert.Contains("越界", ex.Message);
    }

    [Fact]
    public async Task SwitchTab_InRange_NoThrow()
    {
        // 正向对照：idx=0 在范围内不抛（ctx.ActivePage 切到 pages[0]，BringToFrontAsync 在 loose mock 上返 default ValueTask）
        MockPageFactory.Create(out var page);
        var executor = CreateExecutor();
        var ctx = CreateCtx(page);
        var step = ScriptBuilder.Step("switchTab", index: 0, name: "first");

        var result = await executor.ExecuteAsync(step, ctx, MinimalScript(), null);
        // 不抛即 PASS（switchTab 返回 StepResult(url)）
        Assert.NotNull(result);
    }

    [Fact]
    public async Task SwitchTab_IndexMinusOne_TakesLastPage_NoThrow()
    {
        // 正向对照：idx=-1 取最后一个标签页（switchTab 设计支持 -1；MockPageFactory 1 个标签页 → idx 解析为 0）
        MockPageFactory.Create(out var page);
        var executor = CreateExecutor();
        var ctx = CreateCtx(page);
        var step = new StepNode { Kind = "step", Name = "last", Action = "switchTab" };  // 不传 index → 默认 -1

        var result = await executor.ExecuteAsync(step, ctx, MinimalScript(), null);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task CloseTab_NoIndex_ClosesCurrentPage_NoThrow()
    {
        // 正向对照：closeTab 无 index 关当前页（CloseAsync 在 loose mock 上返 default ValueTask）
        MockPageFactory.Create(out var page);
        var executor = CreateExecutor();
        var ctx = CreateCtx(page);
        var step = new StepNode { Kind = "step", Name = "cur", Action = "closeTab" };

        var result = await executor.ExecuteAsync(step, ctx, MinimalScript(), null);
        Assert.Null(result);  // closeTab 返 null StepResult
    }

    // ===== scroll 严格二选一 + 非法 direction（批次9③：撤销 direction 优先软兜底 + Y10 仅都不传 throw）=====

    [Fact]
    public async Task Scroll_BothSelectorAndDirection_ThrowsArgumentException()
    {
        MockPageFactory.Create(out var page);
        var executor = CreateExecutor();
        var ctx = CreateCtx(page);
        var step = ScriptBuilder.Step("scroll", selector: "//*[@id='x']", direction: "down", name: "both");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            executor.ExecuteAsync(step, ctx, MinimalScript(), null));
        Assert.Contains("互斥", ex.Message);
    }

    [Fact]
    public async Task Scroll_NeitherSelectorNorDirection_ThrowsArgumentException()
    {
        MockPageFactory.Create(out var page);
        var executor = CreateExecutor();
        var ctx = CreateCtx(page);
        var step = ScriptBuilder.Step("scroll", name: "neither");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            executor.ExecuteAsync(step, ctx, MinimalScript(), null));
        Assert.Contains("需指定", ex.Message);
    }

    [Fact]
    public async Task Scroll_IllegalDirection_ThrowsArgumentException()
    {
        MockPageFactory.Create(out var page);
        var executor = CreateExecutor();
        var ctx = CreateCtx(page);
        var step = ScriptBuilder.Step("scroll", direction: "diagonal", name: "illegal");  // 非法 direction（不在 up/down/left/right/bottom/top）

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            executor.ExecuteAsync(step, ctx, MinimalScript(), null));
        Assert.Contains("非法 direction", ex.Message);
        Assert.Contains("diagonal", ex.Message);
    }

    [Fact]
    public async Task Scroll_DirectionOnly_NoThrow()
    {
        // 正向对照：direction-only（selector=null）合法不抛。需 mock Mouse.WheelAsync（direction 模式调 ctx.ActivePage.Mouse）。
        MockPageFactory.Create(out var page);
        var mouseMock = Substitute.For<IMouse>();
        mouseMock.WheelAsync(Arg.Any<float>(), Arg.Any<float>()).Returns(Task.CompletedTask);
        page.Mouse.Returns(mouseMock);

        var executor = CreateExecutor();
        var ctx = CreateCtx(page);
        var step = ScriptBuilder.Step("scroll", direction: "down", name: "down");

        var result = await executor.ExecuteAsync(step, ctx, MinimalScript(), null);
        Assert.Null(result);  // scroll 返 null StepResult（不抛即 PASS）
        await mouseMock.Received(1).WheelAsync(0, 300);  // 验 down 方向按默认 amount=300 滚动（验值不只验不抛）
    }
}
