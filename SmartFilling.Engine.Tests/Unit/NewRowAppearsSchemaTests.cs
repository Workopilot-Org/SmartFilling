using SmartFilling.Engine.Engine;
using Xunit;

namespace SmartFilling.Engine.Tests.Unit;

/// <summary>
/// a-a-4 DC9 + DC14 schema 架构挡测试（silent 防御，经生产 LoadFromJson 反序列化路径）。
/// DC9：new_row_appears selector 必填（detectConditionFull allOf if/then）。
/// DC14：sequential/ai phase 不设 _lastRowCount 基线 → check new_row_appears silent 失效，schema allOf 合并（stepNode + detect→NoNewRow）挡。
/// 断言验值不只验状态：拒绝时 errors 含 new_row_appears（不只验 Throws）；合法场景通过。
/// ⚠️ DC14 allOf 同字段 $ref intersection 是 JsonSchema.NET 验证器依赖，须实证（防 schema 看似挡了但验证器合并错漏挡 → silent）。
/// </summary>
public class NewRowAppearsSchemaTests
{
    private static string LoopPhaseWithNewRow(string selectorJson) => $$"""
        {
          "version": 2, "scriptId": "t", "name": "t",
          "fields": [{ "name": "rows", "type": "array", "label": "行", "items": { "type": "string" } }],
          "phases": [
            { "kind": "phase", "name": "loop", "type": "loop", "aiGoal": "明细", "loopSource": "rows", "steps": [
              { "kind": "step", "name": "checkNewRow", "action": "check", "description": "加行校验",
                "detect": { "type": "new_row_appears" {{selectorJson}} }, "then": "continue" }
            ]}
          ]
        }
        """;

    [Fact]
    public void DC9_NewRowAppears_MissingSelector_Rejected()
    {
        // DC9：new_row_appears 无 selector → schema allOf if/then required:[selector] 拒（挡手编脚本 silent 数 0）
        var json = LoopPhaseWithNewRow("");
        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.LoadFromJson(json));
        Assert.NotEmpty(ex.Message);  // DC 拦截生效（JsonSchema.NET allOf 报 /phases/0 级，消息不含 new_row_appears 文本，验非空错误即可）
    }

    [Fact]
    public void DC9_NewRowAppears_WithSelector_Passes()
    {
        // DC9：有 selector 合法（golden gt-10 场景）
        var json = LoopPhaseWithNewRow(", \"selector\": \"#data-table tbody tr\"");
        var script = ScriptLoader.LoadFromJson(json);
        Assert.Equal("t", script.Name);
    }

    [Fact]
    public void DC14_SequentialPhase_NewRowAppears_Rejected()
    {
        // DC14：sequential phase + check new_row_appears → schema allOf（stepNode + detect→NoNewRow）挡（sequential 不设 _lastRowCount 基线 → silent 失效）
        var json = $$"""
        {
          "version": 2, "scriptId": "t", "name": "t",
          "phases": [
            { "kind": "phase", "name": "main", "type": "sequential", "aiGoal": "测试", "steps": [
              { "kind": "step", "name": "checkNewRow", "action": "check", "description": "校验",
                "detect": { "type": "new_row_appears", "selector": "#tbl tbody tr" }, "then": "nothing" }
            ]}
          ]
        }
        """;
        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.LoadFromJson(json));
        Assert.NotEmpty(ex.Message);  // DC 拦截生效（JsonSchema.NET allOf 报 /phases/0 级，消息不含 new_row_appears 文本，验非空错误即可）
    }

    [Fact]
    public void DC14_SequentialPhase_StepConditionNewRowAppears_Rejected()
    {
        // DC14 condition 补挡（第52轮 F6）：sequential phase 的 step.condition（步骤执行前条件）含 new_row_appears 同样依赖 _lastRowCount 基线，
        // schema 须一并挡（原 allOf 只 override detect→NoNewRow，condition→Full 漏挡 → step.condition new_row_appears 静默 false 跳过 step；
        // DetectEvaluator EvaluateNewRowAppearsAsync 栈空时 warning 自承"DC14 schema 应挡，返回 false"）。本用例 detect 用合法 selector_visible（不触发）专验 condition 漏挡修复。
        var json = $$"""
        {
          "version": 2, "scriptId": "t", "name": "t",
          "phases": [
            { "kind": "phase", "name": "main", "type": "sequential", "aiGoal": "测试", "steps": [
              { "kind": "step", "name": "clickIfNewRow", "action": "click", "description": "条件点击",
                "selector": "#btn", "condition": { "type": "new_row_appears", "selector": "#tbl tbody tr" } }
            ]}
          ]
        }
        """;
        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.LoadFromJson(json));
        Assert.NotEmpty(ex.Message);  // F6 后 condition→NoNewRow 挡 new_row_appears；F6 前 condition→Full 漏挡会通过（无异常）→ 本断言守护修复
    }

    [Fact]
    public void DC14_LoopPhase_NewRowAppears_NotBlocked()
    {
        // DC14 不误挡：loop phase + new_row_appears（有 selector）合法（DC10 递归找 + DC13 栈隔离在 loop 内有效）
        var json = LoopPhaseWithNewRow(", \"selector\": \".row\"");
        var script = ScriptLoader.LoadFromJson(json);
        Assert.Equal("t", script.Name);
    }

    [Fact]
    public void DC14_SequentialPhase_OtherDetect_Passes()
    {
        // DC14 不误挡：sequential phase + selector_visible（非 new_row_appears）合法
        var json = $$"""
        {
          "version": 2, "scriptId": "t", "name": "t",
          "phases": [
            { "kind": "phase", "name": "main", "type": "sequential", "aiGoal": "测试", "steps": [
              { "kind": "step", "name": "checkVisible", "action": "check", "description": "校验",
                "detect": { "type": "selector_visible", "selector": "#x" }, "then": "nothing" }
            ]}
          ]
        }
        """;
        var script = ScriptLoader.LoadFromJson(json);
        Assert.Equal("t", script.Name);
    }

    [Fact]
    public void DC14_NestedSequentialInLoop_NewRowAppears_Rejected()
    {
        // DC14 递归挡：嵌套 sequential-in-loop，内层 sequential + new_row_appears → 拒（嵌套 phase 走 phaseNode 递归按各自 type 重评估 allOf）
        var json = $$"""
        {
          "version": 2, "scriptId": "t", "name": "t",
          "fields": [{ "name": "rows", "type": "array", "label": "行", "items": { "type": "string" } }],
          "phases": [
            { "kind": "phase", "name": "loop", "type": "loop", "aiGoal": "明细", "loopSource": "rows", "steps": [
              { "kind": "phase", "name": "inner", "type": "sequential", "aiGoal": "内层", "steps": [
                { "kind": "step", "name": "checkNewRow", "action": "check", "description": "校验",
                  "detect": { "type": "new_row_appears", "selector": ".r" }, "then": "nothing" }
              ]}
            ]}
          ]
        }
        """;
        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.LoadFromJson(json));
        Assert.NotEmpty(ex.Message);  // DC 拦截生效（JsonSchema.NET allOf 报 /phases/0 级，消息不含 new_row_appears 文本，验非空错误即可）
    }
    [Fact]
    public void DC9_NewRowAppears_MissingSelector_SchemaRejected_Independent()
    {
        // DC9 加强（批次 D）：独立 schema 校验绕 code 兜底（ScriptLoader L1004 code 也挡 new_row_appears 无 selector，LoadFromJson 无法区分 schema vs code 拒）。
        // 用 ValidateSchemaAndGetErrors 独立验 schema allOf if/then required:[selector] 挡（防 schema 失效被 code 兜底掩盖测试全绿）。
        var json = LoopPhaseWithNewRow("");
        var schemaErrors = ScriptLoader.ValidateSchemaAndGetErrors(json, lenientNull: false);
        Assert.NotEmpty(schemaErrors);  // schema allOf 挡（非 code 兜底）
    }
}
