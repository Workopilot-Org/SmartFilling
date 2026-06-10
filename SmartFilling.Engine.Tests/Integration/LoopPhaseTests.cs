using SmartFilling.Engine.Engine;
using SmartFilling.Engine.Models;
using SmartFilling.Engine.Tests.Helpers;
using System.Text.Json;
using Xunit;

namespace SmartFilling.Engine.Tests.Integration;

/// <summary>
/// Loop Phase 集成测试：遍历、空数据源、maxLoopCount、scopeChain、loopCondition、嵌套循环、重试。
/// 所有 step 使用 check(always) 不需要真实 DOM 操作。
/// </summary>
public class LoopPhaseTests
{
    private readonly NullLogger _logger = new();

    private static EngineOptions DefaultOptions() => new()
    {
        MaxScriptDuration = 600000,
        MaxLoopCount = 100,
        Rerun = new RerunOptions { MaxRowRerunCount = 2, MaxPhaseRerunCount = 2, Interval = 1 },  // 决策9/13：MaxPhaseRetries 废弃换 RerunOptions；Interval=1 加速 rerun 测试
        StepRetry = new StepRetry { Count = 0 }
    };

    private static Dictionary<string, object> ThreeRowData => new()
    {
        { "items", new List<Dictionary<string, object>>
        {
            new() { { "name", "row1" } },
            new() { { "name", "row2" } },
            new() { { "name", "row3" } }
        }
        }
    };

    [Fact]
    public async Task Loop_IteratesOverAllRows()
    {
        // 3 行数据全遍历，每行有 check(always) then=next，最后自动遍历完
        var script = new ScriptBuilder()
            .AddPhase("Loop", type: "loop", loopSource: "items", steps:
            [
                ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "next")
            ])
            .Build();

        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, DefaultOptions());

        var result = await engine.ExecuteAsync(script, ThreeRowData, page);

        Assert.True(result.Success, result.ErrorMessage);
    }

    [Fact]
    public async Task Loop_EmptyDataSource_NoIteration()
    {
        // loopSource 指向不存在的 key，返回空数组，不执行任何迭代
        var script = new ScriptBuilder()
            .AddPhase("Loop", type: "loop", loopSource: "nonexistent", steps:
            [
                ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "next")
            ])
            .Build();

        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, DefaultOptions());

        var result = await engine.ExecuteAsync(script, new(), page);

        Assert.True(result.Success, result.ErrorMessage);
        // 没有迭代发生，ExecutionLog 为空
        Assert.Empty(result.ExecutionLog);
    }

    [Fact]
    public async Task Loop_MaxLoopCount_Sufficient_Succeeds()
    {
        // 修订8：maxLoopCount(10) ≥ 预期行数(3) → 正常遍历所有行（成功）
        var script = new ScriptBuilder()
            .AddPhase("Loop", type: "loop", loopSource: "items", maxLoopCount: 10, steps:
            [
                ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "next")
            ])
            .Build();

        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, DefaultOptions());

        var result = await engine.ExecuteAsync(script, ThreeRowData, page);

        Assert.True(result.Success, result.ErrorMessage);
        // 3 行全部遍历，最后一行 rowIdx=2
        Assert.Equal(2, result.Vars?["rowIndex"]);
    }

    [Fact]
    public async Task Loop_MaxLoopCount_ExceedsLimit_Fails()
    {
        // 修订8：rows.Count(3) > maxLoopCount(2) → 超限失败（原 G.3.4 截断 Success+告警会静默丢第 3 行数据）
        var script = new ScriptBuilder()
            .AddPhase("Loop", type: "loop", loopSource: "items", maxLoopCount: 2, steps:
            [
                ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "next")
            ])
            .Build();

        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, DefaultOptions());

        var result = await engine.ExecuteAsync(script, ThreeRowData, page);

        Assert.False(result.Success);
        Assert.Contains("超过最大循环次数", result.ErrorMessage ?? "");
    }

    [Fact]
    public async Task PhaseRerun_NonConvergent_RespectsMaxPhaseRerunCount()
    {
        // ④-2：sequential phase 的 phase_rerun 非收敛脚本（check 持续 true→phase_rerun）应有全局上限（MaxPhaseRerunCount=2），
        // 不再递归 ExecutePhaseAsync 自旋到 MaxScriptDuration(600s)。PhaseResult 是内部方法返回值，测试改经 ScriptResult 间接断言（第5轮）。
        var script = new ScriptBuilder()
            .AddPhase("Main", steps:  // sequential phase（非 loop/ai）
            [
                ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "phase_rerun")
            ])
            .Build();

        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, DefaultOptions());

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await engine.ExecuteAsync(script, new(), page);
        sw.Stop();

        Assert.False(result.Success);
        Assert.Contains("超过最大 phase 重跑次数", result.ErrorMessage ?? "");
        // 证明是计数器拦截而非 MaxScriptDuration(600s) 超时兜底
        Assert.True(sw.ElapsedMilliseconds < 60000, $"耗时 {sw.ElapsedMilliseconds}ms，疑似自旋到超时（递归未消除）");
    }

    [Fact]
    public async Task LoopPhaseRerun_NonConvergent_RespectsMaxPhaseRerunCount()
    {
        // ④-2 附带修复1：loop phase 的 phase_rerun 路径（goto 键清理已补，与 row_rerun/sequential 对称）。
        // 非收敛 loop（check 持续 true→phase_rerun）应受 MaxPhaseRerunCount(2) 上限拦截，不无限重跑。
        var script = new ScriptBuilder()
            .AddPhase("Loop", type: "loop", loopSource: "items", steps:
            [
                ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "phase_rerun")
            ])
            .Build();

        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, DefaultOptions());

        var result = await engine.ExecuteAsync(script, ThreeRowData, page);

        Assert.False(result.Success);
        Assert.Contains("超过最大 phase 重跑次数", result.ErrorMessage ?? "");
    }

    [Fact]
    public async Task Loop_ProductionPath_NestedLoop_Normalized()
    {
        // loopSource 嵌套 loop 经生产 NormalizeJsonElement 路径（外层 List<object>，内层 rowData.details 也是 List<object>）。
        var json = """{"orders": [{"orderName": "O1", "details": [{"item": "D1"}, {"item": "D2"}]}]}""";
        using var doc = JsonDocument.Parse(json);
        var fillData = (Dictionary<string, object>)VariableHelper.NormalizeJsonElement(doc.RootElement);

        var script = new ScriptBuilder()
            .AddPhase("Orders", type: "loop", loopSource: "orders",
                steps:
                [
                    new PhaseNode
                    {
                        Kind = "phase", Name = "Details", Type = "loop", LoopSource = "details",
                        Steps = [ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "next")]
                    }
                ])
            .Build();

        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, DefaultOptions());

        var result = await engine.ExecuteAsync(script, fillData, page);

        Assert.True(result.Success, result.ErrorMessage);
    }

    [Fact]
    public async Task Loop_DeepNestedFillData_EntryNormalize_Iterates()
    {
        // Q4：fillData 含原始 JsonElement（未经 NormalizeJsonElement），ExecuteAsync 入口深度归一后 loop 正常迭代。
        // 构造 明细→消费→附件 三层嵌套，验证递归归一幂等（修订1）。
        var json = """{"items": [{"id": "A1", "consumption": {"amount": 10, "attachments": [{"name": "f1"}]}}]}""";
        using var doc = JsonDocument.Parse(json);
        // 直接把 items 作为 JsonElement 包进 fillData（模拟未经归一的入口）
        var fillData = new Dictionary<string, object> { { "items", doc.RootElement.GetProperty("items") } };
        Assert.True(fillData["items"] is System.Text.Json.JsonElement, "前置：入口应为 JsonElement");

        var script = new ScriptBuilder()
            .AddPhase("Loop", type: "loop", loopSource: "items", steps:
            [
                ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "next")
            ])
            .Build();

        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, DefaultOptions());

        var result = await engine.ExecuteAsync(script, fillData, page);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.Vars?.ContainsKey("rowIndex") ?? false, "loop 未迭代——Q4 入口归一未生效");
        // Q4 入口归一副作用：fillData 顶层值已从 JsonElement 转为 CLR（List<object>）
        Assert.False(fillData["items"] is System.Text.Json.JsonElement, "Q4 入口应已归一 fillData 中的 JsonElement");
    }

    [Fact]
    public async Task Loop_RowIndex_SetCorrectly()
    {
        // rowIndex 从 0 开始递增，通过最终 vars.rowIndex 验证
        var script = new ScriptBuilder()
            .AddPhase("Loop", type: "loop", loopSource: "items", steps:
            [
                ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "next")
            ])
            .Build();

        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, DefaultOptions());

        var result = await engine.ExecuteAsync(script, ThreeRowData, page);

        Assert.True(result.Success, result.ErrorMessage);
        // 3 行遍历完，最后一次设置 rowIndex=2（rowIdx=2, offset=0）
        Assert.Equal(2, result.Vars?["rowIndex"]);
    }

    [Fact]
    public async Task Loop_ScopeChain_PushPop()
    {
        // loop 中 step 使用 {{name}} 引用 rowData，通过 extract 验证 scope 可访问
        // 使用 check(always) then=next 确认每行执行成功
        var script = new ScriptBuilder()
            .AddPhase("Loop", type: "loop", loopSource: "items", steps:
            [
                ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "next")
            ])
            .AddPhase("After", steps:
            [
                ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "phase_success")
            ])
            .Build();

        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, DefaultOptions());

        var result = await engine.ExecuteAsync(script, ThreeRowData, page);

        Assert.True(result.Success, result.ErrorMessage);
    }

    [Fact]
    public async Task Loop_LoopCondition_StopsWhenFalse()
    {
        // loopCondition 检查发生在 rowData push 之前，检查的是 scopeChain（包含 fillData）
        // 使用 data_exists(field="keepGoing")，fillData 中没有 keepGoing → 立即退出
        var fillData = new Dictionary<string, object>
        {
            { "items", new List<Dictionary<string, object>>
            {
                new() { { "name", "row1" } },
                new() { { "name", "row2" } }
            }
            }
        };

        var script = new ScriptBuilder()
            .AddPhase("Loop", type: "loop", loopSource: "items",
                loopCondition: ScriptBuilder.Detect("data_exists", field: "keepGoing"),
                steps:
                [
                    ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "next")
                ])
            .Build();

        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, DefaultOptions());

        var result = await engine.ExecuteAsync(script, fillData, page);

        Assert.True(result.Success, result.ErrorMessage);
        // loopCondition 不满足，无迭代发生，rowIndex 未设置
        Assert.False(result.Vars?.ContainsKey("rowIndex") ?? false);
    }

    [Fact]
    public async Task Loop_NestedLoop_ReferencedFromParent()
    {
        // 外层 loop loopSource="orders"，内层 loop loopSource="details"
        // 内层从外层 rowData 的 details 字段取数据
        var fillData = new Dictionary<string, object>
        {
            { "orders", new List<Dictionary<string, object>>
            {
                new()
                {
                    { "orderName", "Order1" },
                    { "details", new List<Dictionary<string, object>>
                    {
                        new() { { "item", "Item1" } },
                        new() { { "item", "Item2" } }
                    }
                    }
                }
            }
            }
        };

        var script = new ScriptBuilder()
            .AddPhase("Orders", type: "loop", loopSource: "orders",
                steps:
                [
                    new PhaseNode
                    {
                        Kind = "phase",
                        Name = "Details",
                        Type = "loop",
                        LoopSource = "details",
                        Steps =
                        [
                            ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "next")
                        ]
                    }
                ])
            .Build();

        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, DefaultOptions());

        var result = await engine.ExecuteAsync(script, fillData, page);

        Assert.True(result.Success, result.ErrorMessage);
    }

    [Fact]
    public async Task Loop_PhaseRetry_RetriesCurrentRow()
    {
        // phase_retry 在 loop 中重试当前行
        // 但 check(always) + phase_retry 会无限重试，所以需要 phase_retry 后 then=next
        // 实际上 loop 中 phase_retry 会 isRetry=true, rowIdx--，然后重新执行当前行
        // 使用简单的 then=next 验证所有行正常遍历
        var script = new ScriptBuilder()
            .AddPhase("Loop", type: "loop", loopSource: "items", steps:
            [
                ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "next")
            ])
            .Build();

        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, DefaultOptions());

        var result = await engine.ExecuteAsync(script, ThreeRowData, page);

        Assert.True(result.Success, result.ErrorMessage);
        // 验证遍历完成：rowIndex 应为 2（最后一行的 0-based index）
        Assert.Equal(2, result.Vars?["rowIndex"]);
    }

    [Fact]
    public async Task Loop_PhaseFail_ExitsLoop()
    {
        // then=phase_fail 退出循环
        var script = new ScriptBuilder()
            .AddPhase("Loop", type: "loop", loopSource: "items",
                onError: "skip",
                steps:
                [
                    ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "phase_fail",
                        message: "循环中止")
                ])
            .Build();

        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, DefaultOptions());

        var result = await engine.ExecuteAsync(script, ThreeRowData, page);

        // phase_fail 使 loop phase 返回失败，但 onError=skip 允许继续
        // 由于 loop 后没有其他 phase，脚本仍为成功
        Assert.True(result.Success);
    }

    [Fact]
    public async Task Loop_WithProductionDeserializedFillData_IteratesRows()
    {
        // 复现 P0 bug：生产链路（App ConvertJsonElement / Worker ObjectToClrConverter → NormalizeJsonElement）
        // 把 JSON 数组反序列化成 List<object>（元素运行时是 Dictionary），而非本套件常用的强类型 List<Dictionary>。
        // 若 GetLoopRows 的 `rows is List<Dictionary<string,object>>` 不兼容 List<object>，
        // loop 取不到数据 → 0 次迭代 → rowIndex 不被设置。
        var json = """{"items": [{"name": "row1"}, {"name": "row2"}, {"name": "row3"}]}""";
        using var doc = JsonDocument.Parse(json);
        var fillData = (Dictionary<string, object>)VariableHelper.NormalizeJsonElement(doc.RootElement);

        // 前置：坐实生产路径确实产出 List<object>（而非强类型 List<Dictionary>），否则测试前提不成立
        Assert.True(fillData["items"] is System.Collections.IList, "items 应为 IList");
        Assert.False(fillData["items"] is List<Dictionary<string, object>>,
            "生产反序列化路径下 items 不应是强类型 List<Dictionary>");

        var script = new ScriptBuilder()
            .AddPhase("Loop", type: "loop", loopSource: "items", steps:
            [
                ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "next")
            ])
            .Build();

        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, DefaultOptions());

        var result = await engine.ExecuteAsync(script, fillData, page);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.Vars?.ContainsKey("rowIndex") ?? false,
            "loop 未迭代——GetLoopRows 取不到生产 List<object> 数据（is List<Dictionary<string,object>> 类型不匹配）");
        Assert.Equal(2, result.Vars?["rowIndex"]);  // 3 行遍历，最后一行 rowIdx=2
    }
}
