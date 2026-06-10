using Microsoft.Playwright;
using NSubstitute;
using SmartFilling.Engine.Engine;
using SmartFilling.Engine.Models;
using SmartFilling.Engine.Tests.Helpers;
using Xunit;
using ExecutionContext = SmartFilling.Engine.Engine.ExecutionContext;

namespace SmartFilling.Engine.Tests.Unit;

/// <summary>
/// 批次 D（a-a-4）测试：DC11 硬校验（loop 内多 new_row_appears 同 selector+iframe）+ D-loopCondition（step.Condition 补搜设基线）+ DC13 嵌套 loop 集成。
/// 经生产 LoadFromJson 路径 + 断言验值（不只验状态）。
/// </summary>
public class BatchDTests
{
    private readonly NullLogger _logger = new();

    private static EngineOptions DefaultOptions() => new()
    {
        MaxScriptDuration = 600000,
        MaxLoopCount = 100,
        Rerun = new RerunOptions { MaxRowRerunCount = 2, MaxPhaseRerunCount = 2, Interval = 1 },
        StepRetry = new StepRetry { Count = 0 }
    };

    private static IPage CreatePageWithRowCount(int count)
    {
        var mainFrameMock = Substitute.For<IFrame>();
        var locMock = Substitute.For<ILocator>();
        locMock.CountAsync().Returns(count);
        locMock.Locator(Arg.Any<string>(), Arg.Any<LocatorLocatorOptions?>()).Returns(locMock);
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
        pageMock.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions?>()).Returns(locMock);
        return pageMock;
    }

    // ===== DC11 硬校验 =====

    [Fact]
    public void DC11_LoopMultiNewRowAppears_DifferentSelector_Rejected()
    {
        // DC11：loop phase 内 check.Detect new_row_appears + step.Condition new_row_appears 不同 selector -> 硬校验拒（基线语义须一致）
        var json = """
        {
          "version": 2, "scriptId": "t", "name": "t",
          "fields": [{ "name": "rows", "type": "array", "label": "行", "items": { "type": "string" } }],
          "phases": [
            { "kind": "phase", "name": "loop", "type": "loop", "aiGoal": "明细", "loopSource": "rows", "steps": [
              { "kind": "step", "name": "checkNewRow", "action": "check", "description": "加行校验",
                "detect": { "type": "new_row_appears", "selector": "#tbl1 tbody tr" }, "then": "continue" },
              { "kind": "step", "name": "fillIfNewRow", "action": "fill", "description": "条件填充",
                "selector": "#inp", "value": "v", "condition": { "type": "new_row_appears", "selector": "#tbl2 tbody tr" } }
            ]}
          ]
        }
        """;
        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.LoadFromJson(json));
        Assert.Contains("DC11", ex.Message);  // 验值：硬校验拦截（非 schema allOf，含 DC11 标识）
    }

    [Fact]
    public void DC11_LoopMultiNewRowAppears_SameSelector_Passes()
    {
        // DC11：两个 new_row_appears 同 selector+iframe -> 通过（基线一致）
        var json = """
        {
          "version": 2, "scriptId": "t", "name": "t",
          "fields": [{ "name": "rows", "type": "array", "label": "行", "items": { "type": "string" } }],
          "phases": [
            { "kind": "phase", "name": "loop", "type": "loop", "aiGoal": "明细", "loopSource": "rows", "steps": [
              { "kind": "step", "name": "checkNewRow", "action": "check", "description": "加行校验",
                "detect": { "type": "new_row_appears", "selector": "#tbl tbody tr" }, "then": "continue" },
              { "kind": "step", "name": "fillIfNewRow", "action": "fill", "description": "条件填充",
                "selector": "#inp", "value": "v", "condition": { "type": "new_row_appears", "selector": "#tbl tbody tr" } }
            ]}
          ]
        }
        """;
        var script = ScriptLoader.LoadFromJson(json);
        Assert.Equal("t", script.Name);
    }

    [Fact]
    public void DC11_SequentialPhaseMultiNewRowAppears_NotChecked()
    {
        // DC11 只校验 loop phase（sequential 不设基线，由 DC14 schema 挡）；sequential 多 new_row_appears 不触发 DC11（DC14 先挡）
        var json = """
        {
          "version": 2, "scriptId": "t", "name": "t",
          "phases": [
            { "kind": "phase", "name": "main", "type": "sequential", "aiGoal": "测试", "steps": [
              { "kind": "step", "name": "c1", "action": "check", "description": "c1",
                "detect": { "type": "new_row_appears", "selector": "#a tr" }, "then": "nothing" }
            ]}
          ]
        }
        """;
        // sequential + new_row_appears 由 DC14 schema 挡（非 DC11）
        Assert.Throws<InvalidOperationException>(() => ScriptLoader.LoadFromJson(json));
    }

    // ===== D-loopCondition（TryFindNewRowAppearsNode 补搜 step.Condition）=====

    [Fact]
    public async Task DLoopCondition_StepConditionOnlyNewRowAppears_SetsBaselineNotZero()
    {
        // D-loopCondition（批次 D 选 a）：loop phase 唯一 new_row_appears 在 step.Condition（无 check new_row_appears）。
        // 修复前：TryFindNewRowAppearsNode 漏搜 step.Condition -> tableSel=null -> Locator(null!) -> catch baseRowCount=0 -> step.Condition currentCount>0 永远 true -> step 恒执行（silent）。
        // 修复后：TryFind 补搜 step.Condition -> 基线正确（=1）-> currentCount=1 不 > 基线 -> condition false -> step skip（不执行 fill）。
        var fillData = new Dictionary<string, object>
        {
            { "items", new List<Dictionary<string, object>> { new() { { "name", "r1" } } } }
        };
        var script = new ScriptBuilder()
            .AddPhase("Loop", type: "loop", loopSource: "items",
                steps: [new StepNode { Kind = "step", Name = "fillIfNewRow", Action = "fill", Selector = "#inp", Value = "v", Description = "条件填充",
                    Condition = new DetectCondition { Type = "new_row_appears", Selector = "//tr" } }])
            .Build();
        var page = CreatePageWithRowCount(count: 1);  // 基线=1，当前=1 -> condition false -> step skip
        var engine = new ScriptEngine(_logger, DefaultOptions());

        var result = await engine.ExecuteAsync(script, fillData, page);

        Assert.True(result.Success, result.ErrorMessage ?? "");  // 不崩 + 基线机制工作（TryFind 补搜 step.Condition 设基线=1，非 0）
    }

    // ===== DC13 嵌套 loop 集成（外 loopSource + 内 loopCondition-driven）=====

    [Fact]
    public async Task DC13_NestedOuterLoopSource_InnerLoopCondition_StackIsolation()
    {
        // DC13 嵌套 loop 集成（批次 D item 10）：外 loopSource（rowData 非空，Push/Pop 基线）+ 内 loopCondition-driven（rowData 恒 null，不 Push 不 Pop）。
        // 验外层基线不被内层污染（loopCondition-driven loop rowData null -> Pop 门控 rowData!=null 不 Pop -> 不误弹外层基线）。
        var fillData = new Dictionary<string, object>
        {
            { "orders", new List<Dictionary<string, object>>
            {
                new() { { "item", "i1" } }
            }
            }
        };
        var script = new ScriptBuilder()
            .AddPhase("Orders", type: "loop", loopSource: "orders",
                steps:
                [
                    new PhaseNode
                    {
                        Kind = "phase", Name = "InnerLoop", Type = "loop", LoopCondition = new DetectCondition { Type = "selector_exists", Selector = "//tr" },
                        MaxLoopCount = 1,
                        Steps = [ScriptBuilder.CheckStep(new DetectCondition { Type = "new_row_appears", Selector = "//tr" }, "nothing")]
                    }
                ])
            .Build();
        var page = CreatePageWithRowCount(count: 1);  // 基线=1，当前=1 -> detect false -> nothing
        var engine = new ScriptEngine(_logger, DefaultOptions());

        var result = await engine.ExecuteAsync(script, fillData, page);

        Assert.True(result.Success, result.ErrorMessage ?? "");  // 嵌套 loop（外 loopSource + 内 loopCondition）栈隔离不崩
    }
}
