using SmartFilling.Engine.Engine;
using SmartFilling.Engine.Models;
using SmartFilling.Engine.Tests.Helpers;
using Xunit;

namespace SmartFilling.Engine.Tests.Unit;

/// <summary>loop-variable-support 功能2 校验测试（ScriptLoader.Validate code 校验路径）。
/// 覆盖 ValidateLoopElementUsage（array 值来源唯一 + storeAs 变量名合法性）+ parent 保留字。
/// schema 层校验（互斥/type=loop/selector 必填/dependentRequired）由 LoadFromJson 测试覆盖（需完整 JSON 含 version/scriptId）。</summary>
public class LoopElementValidationTests
{
    static LoopElementConfig Le(string selector = "#tr", string? storeAs = "data", string? ss = "table") =>
        new() { Selector = selector, StoreAs = storeAs, StoreStructure = ss };

    static StepNode ExtractStep(string name, string storeAs) =>
        ScriptBuilder.Step("extract", selector: "td", storeAs: storeAs, property: "textContent", name: name);

    [Fact]
    public void LoopElement_TableMode_Valid()
    {
        var script = new ScriptBuilder()
            .AddPhase("P1", type: "loop", aiGoal: "测试", loopElement: Le(ss: "table"),
                steps: [ExtractStep("s1", "code")])
            .Build();
        var ex = Record.Exception(() => ScriptLoader.Validate(script));
        Assert.Null(ex);
    }

    [Fact]
    public void LoopElement_StoreAs_IsParent_Rejected()
    {
        // R8-3：parent 是保留字，loopElement.storeAs 不能用（ValidateLoopElementUsage → ValidateSingleVarName 路径）
        var script = new ScriptBuilder()
            .AddPhase("P1", type: "loop", aiGoal: "测试", loopElement: Le(storeAs: "parent"),
                steps: [ExtractStep("s1", "code")])
            .Build();
        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.Validate(script));
        Assert.Contains("parent", ex.Message);
    }

    [Fact]
    public void LoopElement_StoreAs_IsReservedVar_Rejected()
    {
        var script = new ScriptBuilder()
            .AddPhase("P1", type: "loop", aiGoal: "测试", loopElement: Le(storeAs: "_rowIndex"),
                steps: [ExtractStep("s1", "code")])
            .Build();
        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.Validate(script));
        Assert.Contains("_rowIndex", ex.Message);
    }

    [Fact]
    public void StepStoreAs_IsParent_Rejected()
    {
        // step.storeAs 用 parent 保留字被拒（ValidateStoreAs → ValidateSingleVarName，R8-3 一处覆盖三校验方法）
        var script = new ScriptBuilder()
            .AddPhase("P1", type: "loop", aiGoal: "测试", loopElement: Le(),
                steps: [ExtractStep("s1", "parent")])
            .Build();
        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.Validate(script));
        Assert.Contains("parent", ex.Message);
    }

    [Fact]
    public void LoopElement_ArrayMode_SingleStepStoreAs_Valid()
    {
        // array 模式唯一 step.storeAs 标量来源 → 通过
        var script = new ScriptBuilder()
            .AddPhase("P1", type: "loop", aiGoal: "测试", loopElement: Le(ss: "array"),
                steps: [ExtractStep("s1", "scalar")])
            .Build();
        var ex = Record.Exception(() => ScriptLoader.Validate(script));
        Assert.Null(ex);
    }

    [Fact]
    public void LoopElement_ArrayMode_NoSource_Rejected()
    {
        // array 模式无 step.storeAs 无嵌套 loopElement → 0 来源 ≠ 1 被拒
        var script = new ScriptBuilder()
            .AddPhase("P1", type: "loop", aiGoal: "测试", loopElement: Le(ss: "array"),
                steps: [ScriptBuilder.Step("click", selector: "#btn", name: "s1")])
            .Build();
        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.Validate(script));
        Assert.Contains("array", ex.Message);
        Assert.Contains("值来源必须唯一", ex.Message);
    }

    [Fact]
    public void LoopElement_ArrayMode_BothSources_Rejected()
    {
        // array 模式同时有 step.storeAs + 嵌套 loopElement（storeAs 非空）→ 2 来源 ≠ 1 被拒
        var nestedPhase = new PhaseNode
        {
            Kind = "phase", Name = "nested", Type = "loop", AiGoal = "嵌套",
            LoopElement = new LoopElementConfig { Selector = "#inner tr", StoreAs = "innerData", StoreStructure = "array" },
            Steps = [ExtractStep("ns1", "val")]
        };
        var script = new ScriptBuilder()
            .AddPhase("P1", type: "loop", aiGoal: "测试", loopElement: Le(selector: "#outer tr", storeAs: "outerData", ss: "array"),
                steps: [ExtractStep("s1", "scalar"), nestedPhase])
            .Build();
        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.Validate(script));
        Assert.Contains("值来源必须唯一", ex.Message);
    }

    [Fact]
    public void LoopElement_TableMode_MultipleStepStoreAs_Valid()
    {
        // table 模式无约束：多 step.storeAs 字段并存合法
        var script = new ScriptBuilder()
            .AddPhase("P1", type: "loop", aiGoal: "测试", loopElement: Le(ss: "table"),
                steps: [ExtractStep("s1", "code"), ExtractStep("s2", "amount")])
            .Build();
        var ex = Record.Exception(() => ScriptLoader.Validate(script));
        Assert.Null(ex);
    }

    [Fact]
    public void LoopElement_NoStoreAs_PureOperation_Valid()
    {
        // 无 storeAs 纯操作模式：不累积，step.storeAs 走 ctx.Vars 兼容分支
        var script = new ScriptBuilder()
            .AddPhase("P1", type: "loop", aiGoal: "测试",
                loopElement: new LoopElementConfig { Selector = ".item-row" },
                rowIndexOffset: 1,
                steps: [ScriptBuilder.Step("click", selector: ".btn-edit", name: "s1")])
            .Build();
        var ex = Record.Exception(() => ScriptLoader.Validate(script));
        Assert.Null(ex);
    }

    [Fact]
    public void LoopElement_StoreAs_CollectedBy_CollectAllStoreAsNames()
    {
        // B2：CollectAllStoreAsNames 收集 loopElement.storeAs——后续 step {{tableData}} 引用放行
        var script = new ScriptBuilder()
            .AddPhase("P1", type: "loop", aiGoal: "测试", loopElement: Le(storeAs: "tableData"),
                steps: [ExtractStep("s1", "code")])
            .AddPhase("P2", type: "loop", aiGoal: "处理", loopSource: "tableData",
                steps: [ScriptBuilder.Step("fill", selector: "#x", value: "{{tableData}}", name: "f1")])
            .Build();
        var ex = Record.Exception(() => ScriptLoader.Validate(script));
        // P2 的 {{tableData}} 应被放行（CollectAllStoreAsNames 收集了 P1 的 loopElement.storeAs=tableData）
        Assert.Null(ex);
    }

    [Fact]
    public void LoopElement_StoreAs_CollidesWithFieldName_Rejected()
    {
        // B2：loopElement.storeAs 与 field name 同名 → CollectAllStoreAsNames 扩展后 ValidateNameCollisions 拦截
        var script = new ScriptBuilder()
            .AddField("tableData", "表格数据", type: "array", fields: [])
            .AddPhase("P1", type: "loop", aiGoal: "测试", loopElement: Le(storeAs: "tableData"),
                steps: [ExtractStep("s1", "code")])
            .Build();
        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.Validate(script));
        Assert.Contains("tableData", ex.Message);
        Assert.Contains("storeAs", ex.Message);
    }
}
