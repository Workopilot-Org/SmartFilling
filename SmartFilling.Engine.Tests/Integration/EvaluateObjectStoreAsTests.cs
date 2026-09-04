using Microsoft.Playwright;
using SmartFilling.Engine.Engine;
using SmartFilling.Engine.Models;
using SmartFilling.Engine.Tests.Helpers;
using Xunit;

namespace SmartFilling.Engine.Tests.Integration;

/// <summary>
/// evaluate 返回 JS 对象 + storeAs 回归测试（计划 evaluate-humble-badger，真实 Chromium）。
/// Playwright EvaluateAsync&lt;object?&gt; 对 JS 对象返回 ExpandoObject（v1.59.0 源码 ParseEvaluateResultToExpando：
/// 嵌套对象递归产 ExpandoObject、数组产 object[]、数字产 double），StoreVars 经 NormalizeToClr 归一为 Dictionary 后
/// 下游 {{name.key}}（ReplaceVars/PluckPath 只认 Dictionary）才可达——修复前 ExpandoObject 原样存入 vars，{{obj.key}}
/// 原样字面输出（silent，生产 4d1e9111.json 日期步骤超时的根因）。
/// evaluate 步骤在任意 page 上下文可执行（不需本地 HTTP 服务），参考 HttpRequestShareBrowserSessionTests 的
/// IAsyncLifetime 结构（Playwright/Chromium 生命周期）。
/// </summary>
public class EvaluateObjectStoreAsTests : IAsyncLifetime
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });
    }

    public async Task DisposeAsync()
    {
        if (_browser != null) await _browser.CloseAsync();
        _playwright?.Dispose();
    }

    private static EngineOptions DefaultOptions() => new()
    {
        MaxScriptDuration = 600000,
        DefaultTimeout = 60000,
        StepRetry = new StepRetry { Count = 0 }
    };

    [Fact]
    public async Task EvaluateObject_SingleStoreAs_DotKeyReplaces()
    {
        // 用例1 顶层对象：evaluate 返回 {y:'2025', d:'12'} + storeAs:"dateParts"
        // → vars["dateParts"] 是 Dictionary（修复前存 ExpandoObject 原对象）+ {{dateParts.y}} 替换为 2025（验值）
        var logger = new NullLogger();
        var engine = new ScriptEngine(logger, DefaultOptions());
        var page = await _browser!.NewPageAsync();
        try
        {
            await page.GotoAsync("about:blank");
            var script = new ScriptBuilder()
                .AddPhase("Main", steps:
                [
                    ScriptBuilder.Step("evaluate", code: "(() => ({y:'2025', d:'12'}))()", storeAs: "dateParts")
                ])
                .Build();

            var result = await engine.ExecuteAsync(script, new(), page);

            Assert.True(result.Success, result.ErrorMessage ?? "");
            Assert.True(result.Vars!.ContainsKey("dateParts"), "dateParts 应已存入 vars");
            var stored = Assert.IsType<Dictionary<string, object>>(result.Vars!["dateParts"]);
            Assert.Equal("2025", stored["y"]);
            Assert.Equal("12", stored["d"]);
            // 下游消费（生产同款函数）：{{dateParts.y}} 替换成功（修复前原样字面输出 {{dateParts.y}}）
            var replaced = VariableHelper.ReplaceVars("year='{{dateParts.y}}'", [], result.Vars!);
            Assert.Equal("year='2025'", replaced);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Fact]
    public async Task EvaluateObject_NestedThreeLevels_DotKeyReplaces()
    {
        // 用例2 嵌套对象三层：evaluate 返回 {a:{b:{c:'x'}}} → {{x.a.b.c}} 替换为 x（覆盖 NormalizeToClr 嵌套递归）
        var logger = new NullLogger();
        var engine = new ScriptEngine(logger, DefaultOptions());
        var page = await _browser!.NewPageAsync();
        try
        {
            await page.GotoAsync("about:blank");
            var script = new ScriptBuilder()
                .AddPhase("Main", steps:
                [
                    ScriptBuilder.Step("evaluate", code: "(() => ({a:{b:{c:'x'}}}))()", storeAs: "x")
                ])
                .Build();

            var result = await engine.ExecuteAsync(script, new(), page);

            Assert.True(result.Success, result.ErrorMessage ?? "");
            var stored = Assert.IsType<Dictionary<string, object>>(result.Vars!["x"]);
            var a = Assert.IsType<Dictionary<string, object>>(stored["a"]);
            var b = Assert.IsType<Dictionary<string, object>>(a["b"]);
            Assert.Equal("x", b["c"]);
            var replaced = VariableHelper.ReplaceVars("deep='{{x.a.b.c}}'", [], result.Vars!);
            Assert.Equal("deep='x'", replaced);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Fact]
    public async Task EvaluateObject_MultiStoreAs_SplitsByVarName()
    {
        // 用例3 多变量 storeAs：evaluate 返回 {y:'2025', d:'12'} + storeAs:{y:年, d:日}
        // → vars["y"]/vars["d"] 分别正确（修复前多变量分支 `when value is Dictionary` 对 ExpandoObject 不匹配 → 静默不存）
        var logger = new NullLogger();
        var engine = new ScriptEngine(logger, DefaultOptions());
        var page = await _browser!.NewPageAsync();
        try
        {
            await page.GotoAsync("about:blank");
            var script = new ScriptBuilder()
                .AddPhase("Main", steps:
                [
                    ScriptBuilder.Step("evaluate", code: "(() => ({y:'2025', d:'12'}))()",
                        storeAs: new Dictionary<string, object> { ["y"] = "年", ["d"] = "日" })
                ])
                .Build();

            var result = await engine.ExecuteAsync(script, new(), page);

            Assert.True(result.Success, result.ErrorMessage ?? "");
            Assert.Equal("2025", result.Vars!["y"]);
            Assert.Equal("12", result.Vars!["d"]);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Fact]
    public async Task EvaluateObject_LoadFromJsonProductionPath_SingleAndMultiStoreAs()
    {
        // 用例4（用户决策 A4）生产反序列化路径：ScriptLoader.LoadFromJson 加载（storeAs 经 STJ 反序列化为 JsonElement：
        // 单变量 JsonElement(String) / 多变量 JsonElement(Object)）+ 真实浏览器执行 → 两种 storeAs 的 vars 均正确落盘、
        // 下游 {{name.key}} 替换成功。value=Playwright 真实 ExpandoObject + storeAs=JsonElement 的组合不依赖
        // ScriptBuilder 的 C# 强类型构造（用例1-3），storeAs 侧也覆盖生产反序列化路径。
        var logger = new NullLogger();
        var engine = new ScriptEngine(logger, DefaultOptions());
        var page = await _browser!.NewPageAsync();
        try
        {
            await page.GotoAsync("about:blank");
            var json = """
            {
              "version": 2,
              "scriptId": "evaluate-obj-prod-path",
              "name": "evaluate对象storeAs生产路径测试",
              "phases": [
                {
                  "kind": "phase", "name": "Main", "aiGoal": "测试", "steps": [
                    { "kind": "step", "action": "evaluate", "name": "splitDate",
                      "code": "(() => ({y:'2025', d:'12'}))()", "storeAs": "dateParts" },
                    { "kind": "step", "action": "evaluate", "name": "splitDateMulti",
                      "code": "(() => ({y:'2025', d:'12'}))()",
                      "storeAs": { "y": "年", "d": "日" } }
                  ]
                }
              ]
            }
            """;
            var script = ScriptLoader.LoadFromJson(json);
            Assert.Equal(2, ((PhaseNode)script.Phases[0]).Steps.Count);

            var result = await engine.ExecuteAsync(script, new(), page);

            Assert.True(result.Success, result.ErrorMessage ?? "");
            // 单变量（storeAs=JsonElement(String)，生产 App→Worker 链路形态）
            var stored = Assert.IsType<Dictionary<string, object>>(result.Vars!["dateParts"]);
            Assert.Equal("2025", stored["y"]);
            Assert.Equal("12", stored["d"]);
            var replaced = VariableHelper.ReplaceVars("year='{{dateParts.y}}'", [], result.Vars!);
            Assert.Equal("year='2025'", replaced);
            // 多变量（storeAs=JsonElement(Object)）
            Assert.Equal("2025", result.Vars!["y"]);
            Assert.Equal("12", result.Vars!["d"]);
        }
        finally
        {
            await page.CloseAsync();
        }
    }
}
