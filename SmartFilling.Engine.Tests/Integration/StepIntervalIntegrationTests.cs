using SmartFilling.Engine.Engine;
using SmartFilling.Engine.Models;
using SmartFilling.Engine.Tests.Helpers;
using System.Diagnostics;
using Xunit;

namespace SmartFilling.Engine.Tests.Integration;

/// <summary>
/// 步骤间固定等待编排层集成测试。MockPageFactory normal mock 下所有 step 耗时 ≈ 0ms，
/// 总耗时 = 步骤间间隔主导，用 Stopwatch 稳定测量（断言验值不只验状态）。
/// 适用范围（决策 2026-07-30 B 选项）：fill/click/navigate/ai 等改变状态类 action 后等待；
/// wait/check/extract/screenshot 跳过（不改变页面状态）；evaluate 参与。
/// </summary>
public class StepIntervalIntegrationTests
{
    private readonly NullLogger _logger = new();

    private static EngineOptions Options(int interval) => new()
    {
        MaxScriptDuration = 600000,
        StepRetry = new StepRetry { Count = 0 },
        StepIntervalMs = interval
    };

    private static ScriptV2 TwoFillScript(ScriptSettings? settings = null)
    {
        var b = new ScriptBuilder().AddPhase("Main", steps:
        [
            ScriptBuilder.Step("fill", selector: "//x", value: "a", name: "s1"),
            ScriptBuilder.Step("fill", selector: "//x", value: "b", name: "s2")
        ]);
        if (settings != null) b.AddSettings(settings);
        return b.Build();
    }

    [Fact]
    public async Task Sequential_IntervalApplied()
    {
        var script = TwoFillScript();
        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, Options(500));

        var sw = Stopwatch.StartNew();
        var result = await engine.ExecuteAsync(script, new(), page);
        sw.Stop();

        Assert.True(result.Success, result.ErrorMessage ?? "");
        Assert.True(sw.ElapsedMilliseconds >= 1000, $"期望 ≥1000ms（2次：step0后+step1后尾部）实际 {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Sequential_IntervalZero_Skipped()
    {
        var script = TwoFillScript();
        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, Options(0));

        var sw = Stopwatch.StartNew();
        var result = await engine.ExecuteAsync(script, new(), page);
        sw.Stop();

        Assert.True(result.Success, result.ErrorMessage ?? "");
        Assert.True(sw.ElapsedMilliseconds < 300, $"期望 <300ms 实际 {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Sequential_LastStepAlsoWaits()
    {
        // 去守卫后每个 step 后都等（含尾部）：2-step → 2 次间隔（step0后 + step1后尾部）
        var script = TwoFillScript();
        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, Options(500));

        var sw = Stopwatch.StartNew();
        var result = await engine.ExecuteAsync(script, new(), page);
        sw.Stop();

        Assert.True(result.Success, result.ErrorMessage ?? "");
        Assert.True(sw.ElapsedMilliseconds >= 1000, $"期望 ≥1000ms（2次含尾部）实际 {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task CrossPhase_IntervalApplied()
    {
        // 跨 phase 等待：2 phase × 1 step each，phase A 的 fill 后等 → phase B 的 fill 前有等待 + phase B fill 后尾部等 = 2 次
        var script = new ScriptBuilder()
            .AddPhase("P1", steps: [ScriptBuilder.Step("fill", selector: "//x", value: "a", name: "s1")])
            .AddPhase("P2", steps: [ScriptBuilder.Step("fill", selector: "//x", value: "b", name: "s2")])
            .Build();
        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, Options(500));

        var sw = Stopwatch.StartNew();
        var result = await engine.ExecuteAsync(script, new(), page);
        sw.Stop();

        Assert.True(result.Success, result.ErrorMessage ?? "");
        Assert.True(sw.ElapsedMilliseconds >= 1000, $"期望 ≥1000ms（phase间+尾部）实际 {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Sequential_AiActionIncluded()
    {
        // ai action 与 fill 一致参与等待；须注入 MockAiProvider，否则抛 InvalidOperationException 走失败路径
        var script = new ScriptBuilder().AddPhase("Main", steps:
        [
            ScriptBuilder.Step("fill", selector: "//x", value: "a", name: "s1"),
            ScriptBuilder.Step("ai", description: "do something", name: "ai1")
        ]).Build();
        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, Options(500), new MockAiProvider());

        var sw = Stopwatch.StartNew();
        var result = await engine.ExecuteAsync(script, new(), page);
        sw.Stop();

        Assert.True(result.Success, result.ErrorMessage ?? "");
        Assert.True(sw.ElapsedMilliseconds >= 1000, $"期望 ≥1000ms（fill后+ai后尾部，2次）实际 {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Loop_IntervalApplied()
    {
        // loop 2 行 × 2 step/行 → 去守卫后每 step 后都等（含行间 + 尾部）：行1 step0后+step1后(行间) + 行2 step0后+step1后(尾部) = 4 次 → ≥2000ms
        var script = new ScriptBuilder().AddPhase("L", type: "loop", loopSource: "rows", steps:
        [
            ScriptBuilder.Step("fill", selector: "//x", value: "a", name: "s1"),
            ScriptBuilder.Step("fill", selector: "//x", value: "b", name: "s2")
        ]).Build();
        var fillData = new Dictionary<string, object>
        {
            ["rows"] = new List<object>
            {
                new Dictionary<string, object> { ["v"] = "a" },
                new Dictionary<string, object> { ["v"] = "b" }
            }
        };
        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, Options(500));

        var sw = Stopwatch.StartNew();
        var result = await engine.ExecuteAsync(script, fillData, page);
        sw.Stop();

        Assert.True(result.Success, result.ErrorMessage ?? "");
        Assert.True(sw.ElapsedMilliseconds >= 2000, $"期望 ≥2000ms（4次含行间+尾部）实际 {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Negative_IntervalsSkipped()
    {
        // 负数被 ApplyStepIntervalAsync 的 interval<=0 守卫跳过，验证守卫集成路径（解析层透传见 StepIntervalConfigTests.Negative_PassesThrough）
        var script = TwoFillScript();
        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, Options(-100));

        var sw = Stopwatch.StartNew();
        var result = await engine.ExecuteAsync(script, new(), page);
        sw.Stop();

        Assert.True(result.Success, result.ErrorMessage ?? "");
        Assert.True(sw.ElapsedMilliseconds < 300, $"期望 <300ms（负数被守卫跳过）实际 {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task ReadActions_Skipped()
    {
        // fill 改变状态→等待 1 次；check/extract/screenshot 跳过（不改变页面状态）→非 3 次间隔
        // 顺序：fill(0)→check(1)→extract(2)→screenshot(3)；仅 fill 后等 500ms（0→1），其余跳过，screenshot 阶段末不等待
        var script = new ScriptBuilder().AddPhase("Main", steps:
        [
            ScriptBuilder.Step("fill", selector: "//x", value: "a", name: "s1"),
            ScriptBuilder.Step("check", detect: ScriptBuilder.Detect("always"), then: "nothing", name: "c1"),
            ScriptBuilder.Step("extract", extractType: "url", storeAs: "url", name: "e1"),
            new StepNode { Kind = "step", Action = "screenshot", Name = "sh1", SaveToFile = false }
        ]).Build();
        MockPageFactory.Create(out var page);
        var engine = new ScriptEngine(_logger, Options(500));

        var sw = Stopwatch.StartNew();
        var result = await engine.ExecuteAsync(script, new(), page);
        sw.Stop();

        Assert.True(result.Success, result.ErrorMessage ?? "");
        Assert.True(sw.ElapsedMilliseconds >= 500, $"期望 ≥500ms（fill 后等1次）实际 {sw.ElapsedMilliseconds}ms");
        Assert.True(sw.ElapsedMilliseconds < 1000, $"期望 <1000ms（check/extract/screenshot 跳过，非3次间隔）实际 {sw.ElapsedMilliseconds}ms");
    }
}
