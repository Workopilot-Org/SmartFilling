using SmartFilling.Engine.Engine;
using SmartFilling.Engine.Tests.Helpers;
using Xunit;

namespace SmartFilling.Engine.Tests.Unit;

/// <summary>④-8 命名冲突校验（字段名∩storeAs 名 / 字段名∩系统保留字 / storeAs∩保留字）+ lastError step 占位符放行 fix。</summary>
public class ScriptLoaderNameCollisionTests
{
    [Fact]
    public void FieldName_CollidesWithStoreAs_Rejected()
    {
        var script = new ScriptBuilder()
            .AddField("billNo", "单号")
            .AddPhase("main", aiGoal: "测试", steps: [ScriptBuilder.Step("extract", selector: "#x", storeAs: "billNo", property: "textContent")])
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.Validate(script));
        Assert.Contains("billNo", ex.Message);
        Assert.Contains("storeAs", ex.Message);
    }

    [Fact]
    public void FieldName_IsReservedVar_Rejected()
    {
        // 🔴-3：字段名=系统保留字（如 rowIndex——极自然的明细表字段名）
        var script = new ScriptBuilder()
            .AddField("rowIndex", "行号")
            .AddPhase("main", aiGoal: "测试", steps: [ScriptBuilder.CheckStep(ScriptBuilder.Detect("always"), "nothing")])
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.Validate(script));
        Assert.Contains("rowIndex", ex.Message);
        Assert.Contains("系统保留变量", ex.Message);
    }

    [Fact]
    public void StoreName_IsLastError_Rejected()
    {
        var script = new ScriptBuilder()
            .AddPhase("main", aiGoal: "测试", steps: [ScriptBuilder.Step("extract", selector: "#x", storeAs: "lastError", property: "textContent")])
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.Validate(script));
        Assert.Contains("lastError", ex.Message);
    }

    [Fact]
    public void NoCollision_DoesNotReport()
    {
        // BUG-B（DEC-7，2026-07-13）：原 AddField("username") 无 source → DEC-7 X 规则（username 必须 source=system）后 null≠system 报错变红。
        // 改非凭据字段名 userId（ScriptBuilder.AddField 无 source 参数，无法构造 source=system 的凭据字段，故避免用凭据名），保持「无冲突不报错」语义不变。
        var script = new ScriptBuilder()
            .AddField("userId", "用户ID")
            .AddPhase("main", aiGoal: "测试", steps: [ScriptBuilder.Step("extract", selector: "#x", storeAs: "result", property: "textContent")])
            .Build();

        var ex = Record.Exception(() => ScriptLoader.Validate(script));
        Assert.Null(ex);
    }

    [Fact]
    public void StepPlaceholder_LastError_NowAllowed()
    {
        // 第6轮 fix：specialVars 复用 ReservedVarNames（含 lastError）→ step 里 {{lastError}} 放行（原被拒"字段未定义"）
        var script = new ScriptBuilder()
            .AddPhase("main", aiGoal: "测试", steps: [ScriptBuilder.Step("fill", selector: "#x", value: "{{lastError}}")])
            .Build();

        var ex = Record.Exception(() => ScriptLoader.Validate(script));
        Assert.Null(ex);
    }

    // DEC-7（2026-07-13）：系统凭据字段 username/password 硬约束（X 加载期校验 ScriptLoader + Y 录制期校验 RecordingEngine，两层一致）。
    // Worker fillData 硬编码 fillData["username"]/["password"] 存 payload 传来的用户名/解密密码（见 AutomationWsClient.BuildFillDataAndDownloadAttachmentsAsync），
    // 故 fields 这两个名必须 source=system，storeAs 名禁用这两个（防被 fillData 系统凭据永久遮蔽取错值，silent）。
    // ①③④用原始 JSON+LoadFromJson 走生产反序列化路径（ScriptBuilder.AddField 无 source 参数，仿 AiInstructionMaskTests:31 source=system 写法），断言验值不只验状态。

    [Fact]
    public void DEC7_CredentialField_NonSystemSource_Rejected()
    {
        // ① fields name=username source=user → LoadFromJson 报错（ValidateSystemCredentialFields）
        var json = """
        {
          "version": 2, "scriptId": "dec7-1", "name": "凭据source错",
          "fields": [{ "name": "username", "label": "用户名", "type": "string", "source": "user" }],
          "phases": [{ "kind": "phase", "name": "main", "type": "sequential", "aiGoal": "登录", "steps": [
            { "kind": "step", "name": "fillUser", "action": "fill", "selector": "#u", "value": "{{username}}", "field": "username" }
          ]}]
        }
        """;
        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.LoadFromJson(json));
        Assert.Contains("username", ex.Message);
        Assert.Contains("source 必须为 system", ex.Message);
    }

    [Fact]
    public void DEC7_StoreAs_CredentialName_Rejected()
    {
        // ② storeAs=username → 报错（ValidateSingleVarName DEC-7 分支）
        var json = """
        {
          "version": 2, "scriptId": "dec7-2", "name": "storeAs凭据名",
          "phases": [{ "kind": "phase", "name": "main", "type": "sequential", "aiGoal": "g", "steps": [
            { "kind": "step", "name": "extractUser", "action": "extract", "selector": "#x", "extractType": "url", "storeAs": "username" }
          ]}]
        }
        """;
        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.LoadFromJson(json));
        Assert.Contains("username", ex.Message);
        Assert.Contains("系统凭据", ex.Message);
    }

    [Fact]
    public void DEC7_CredentialField_SystemSource_Loads()
    {
        // ③ fields name=username source=system → 通过（ValidateSystemCredentialFields 不报 + schema sourceEnum 含 system）
        var json = """
        {
          "version": 2, "scriptId": "dec7-3", "name": "系统凭据合法",
          "fields": [{ "name": "username", "label": "用户名", "type": "string", "source": "system" }],
          "phases": [{ "kind": "phase", "name": "main", "type": "sequential", "aiGoal": "登录", "steps": [
            { "kind": "step", "name": "fillUser", "action": "fill", "selector": "#u", "value": "{{username}}", "field": "username" }
          ]}]
        }
        """;
        var script = ScriptLoader.LoadFromJson(json);
        Assert.Equal("系统凭据合法", script.Name);  // 验值不只验通过
    }

    [Fact]
    public void DEC7_CredentialField_SystemSource_AndNonCredentialStoreAs_Loads()
    {
        // ④ fields name=password source=system + storeAs=authCode（非凭据名）→ 通过
        var json = """
        {
          "version": 2, "scriptId": "dec7-4", "name": "密码加storeAs合法",
          "fields": [{ "name": "password", "label": "密码", "type": "string", "source": "system" }],
          "phases": [{ "kind": "phase", "name": "main", "type": "sequential", "aiGoal": "登录", "steps": [
            { "kind": "step", "name": "fillPwd", "action": "fill", "selector": "#p", "value": "{{password}}", "field": "password" },
            { "kind": "step", "name": "extractCode", "action": "extract", "selector": "#c", "extractType": "url", "storeAs": "authCode" }
          ]}]
        }
        """;
        var script = ScriptLoader.LoadFromJson(json);
        Assert.Equal("密码加storeAs合法", script.Name);  // 验值不只验通过
    }

    [Fact]
    public void DEC7_CredentialField_NestedInArrayFields_NonSystemSource_Rejected()
    {
        // ISSUE-E 递归分支覆盖：嵌套子字段（type=array 的 field.Fields 内）name=username source≠system 也应被拒。
        // ValidateSystemCredentialFields 递归遍历 field.Fields（仿 ValidateFileUiComponent 递归），嵌套子字段同样约束。
        var json = """
        {
          "version": 2, "scriptId": "dec7-5", "name": "嵌套凭据source错",
          "fields": [{ "name": "detail", "label": "明细", "type": "array", "fields": [
            { "name": "username", "label": "用户名", "type": "string", "source": "user" }
          ]}],
          "phases": [{ "kind": "phase", "name": "main", "type": "sequential", "aiGoal": "g", "steps": [
            { "kind": "step", "name": "checkAlways", "action": "check", "description": "校验", "detect": { "type": "always" }, "then": "nothing" }
          ]}]
        }
        """;
        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.LoadFromJson(json));
        Assert.Contains("username", ex.Message);
        Assert.Contains("source 必须为 system", ex.Message);
    }
}
