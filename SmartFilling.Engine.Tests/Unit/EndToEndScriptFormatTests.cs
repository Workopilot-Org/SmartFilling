using System.Text.Json;
using SmartFilling.Engine.Engine;
using SmartFilling.Engine.Models;
using Xunit;

namespace SmartFilling.Engine.Tests.Unit;

/// <summary>
/// S10 端到端格式验证（静态）：用一个集成本次优化(S1-S9)所有新字段的「黄金样例脚本」
/// 过 ScriptLoader 完整加载流程（Schema 运行时校验 F.10.7 + 反序列化 + 业务校验），
/// 逐字段断言解析正确。验证 script-v2.json Schema 端到端允许所有新字段、模型层正确承接。
///
/// 注：此为格式层验证（不启动浏览器）。录制→回放的真实运行时验证见 docs/S10端到端验证checklist.md。
/// 黄金样例同时也是真实脚本的参考模板。
/// </summary>
public class EndToEndScriptFormatTests
{
    /// <summary>
    /// 黄金样例脚本：集成本次优化的全部新字段/修复点。
    /// 必须同时满足 script-v2.json Schema 约束（additionalProperties:false / allOf action 约束）
    /// 与 ScriptLoader 业务校验（占位符/storeAs/iframe 引用/name 唯一/ai phase 结构）。
    /// </summary>
    private const string GoldenSample = """
    {
      "version": 2,
      "scriptId": "golden-e2e-001",
      "name": "黄金样例-端到端格式验证",
      "description": "集成本次优化(S1-S9)所有新字段，验证 Schema 端到端正确",
      "settings": {
        "maxAiTurns": 30
      },
      "fields": [
        { "name": "billDate", "label": "开票日期", "type": "date", "format": "YYYY-MM-DD", "required": true },
        { "name": "amount", "label": "金额", "type": "number", "format": "#,##0.00", "transform": "trim" },
        { "name": "contractName", "label": "合同名称", "type": "string", "required": true },
        { "name": "tags", "label": "标签", "type": "array", "items": { "type": "string" } },
        { "name": "invoiceItems", "label": "明细行", "type": "array", "fields": [
          { "name": "goodsName", "label": "商品名称", "type": "string" },
          { "name": "price", "label": "单价", "type": "number" }
        ]},
        { "name": "remark", "label": "备注", "type": "string" },
        { "name": "attachFile", "label": "附件", "type": "file", "uiComponent": "upload" }
      ],
      "phases": [
        {
          "kind": "phase", "name": "fillMain", "type": "sequential", "aiGoal": "填写合同基本信息与附件", "timeout": 30000, "onError": "ai-fallback-stop",
          "steps": [
            { "kind": "step", "action": "fill", "selector": "//input[@id='billDate']", "value": "{{billDate}}", "field": "billDate", "name": "fillDate" },
            { "kind": "step", "name": "fillAmount", "action": "fill", "selector": "//input[@id='amount']", "value": "{{amount}}", "field": "amount" },
            { "kind": "step", "name": "selectContract", "action": "select", "selector": "//select[@id='contract']", "value": "{{contractName}}", "matchBy": "label" },
            { "kind": "step", "name": "fillRemark", "action": "fill", "selector": "//textarea[@id='remark']", "value": "{{remark}}", "skipIfDataEmpty": true },
            { "kind": "step", "name": "uploadAttach", "action": "upload", "selector": "//input[@type='file']", "filePath": "{{attachFile}}", "skipIfElementMissing": true },
            { "kind": "step", "name": "clickExpand", "action": "click", "selector": "//button[text()='展开详情']",
              "doubleClick": false, "pressEnter": false, "useLast": false,
              "condition": { "type": "selector_exists", "selector": "//button[text()='展开详情']" },
              "fallback": { "action": "click", "selector": "//a[text()='更多']" } }
          ]
        },
        {
          "kind": "phase", "name": "fillInPopup", "type": "sequential", "aiGoal": "填写弹窗内字段", "iframe": ["//iframe[contains(@src,'dialog')]"],
          "steps": [
            { "kind": "step", "name": "fillPopup", "action": "fill", "selector": "//input[@id='popupField']", "value": "固定值" },
            { "kind": "step", "name": "pressEnterPopup", "action": "pressKey", "key": "Enter", "selector": "//input[@id='popupField']" },
            { "kind": "step", "name": "scrollDown", "action": "scroll", "direction": "down", "amount": 300 }
          ]
        },
        {
          "kind": "phase", "name": "submit", "type": "sequential", "aiGoal": "提交并确认状态",
          "steps": [
            { "kind": "step", "name": "clickSubmit", "action": "click", "selector": "//button[@id='submit']",
              "retry": { "count": 2, "interval": 500 },
              "preSetup": { "dialogHandler": "auto_accept", "dialogText": "" } },
            { "kind": "step", "name": "waitSubmit", "action": "wait",
              "until": { "all": [
                { "type": "url_changed" },
                { "type": "page_contains", "keywords": ["保存成功", "提交成功"] }
              ]},
              "onError": "ai-fallback-stop", "timeout": 10000 },
            { "kind": "step", "name": "extractBillNo", "action": "extract", "selector": "//span[@id='billNo']", "property": "textContent",
              "extractType": "property", "storeAs": "billNo", "skipIfElementMissing": true },
            { "kind": "step", "name": "checkStatus", "action": "check",
              "detect": { "type": "selector_value", "selector": "//span[@id='status']", "value": "已提交" },
              "then": "phase_success", "message": "提交状态确认" }
          ]
        },
        {
          "kind": "phase", "name": "loopInvoiceItems", "type": "loop", "aiGoal": "循环填写明细行",
          "loopSource": "invoiceItems", "maxLoopCount": 50, "rowIndexOffset": 1,
          "loopCondition": { "type": "js", "check": "true" },
          "steps": [
            {
              "kind": "phase", "name": "fillRow", "type": "sequential", "aiGoal": "填写单行明细",
              "steps": [
                { "kind": "step", "action": "fill", "selector": "//input[@class='goods']", "value": "{{invoiceItems.goodsName}}", "name": "fillGoods" },
                { "kind": "step", "name": "fillPrice", "action": "fill", "selector": "//input[@class='price']", "value": "{{invoiceItems.price}}" },
                { "kind": "step", "name": "checkRowEnd", "action": "check", "detect": { "type": "always" }, "then": "nothing", "message": "行末标记（break 限 loop，fillRow 是 sequential 改 nothing）" }
              ]
            }
          ]
        },
        {
          "kind": "phase", "name": "aiVerify", "type": "ai", "aiGoal": "核验填报结果完整且正确",
          "maxAiTurns": 5, "timeout": 60000,
          "steps": [
            { "kind": "step", "name": "aiCheckAll", "action": "ai", "description": "检查页面所有必填项已填写且无错误提示",
              "storeAs": { "result": "核验结论", "issues": "遗留问题" } }
          ]
        }
      ],
      "returnData": {
        "billCode": "{{billNo}}",
        "verifyResult": "{{aiVerify.result}}",
        "source": "SmartFilling-golden-sample"
      }
    }
    """;

    // ---------- 辅助 ----------

    private static ScriptV2 Load() => ScriptLoader.LoadFromJson(GoldenSample);

    private static PhaseNode Phase(ScriptV2 script, string name) =>
        script.Phases.OfType<PhaseNode>().First(p => p.Name == name);

    /// <summary>object? 值（StoreAs / ReturnData 值反序列化后是 JsonElement）取字符串。</summary>
    private static string? AsString(object? v) => v is JsonElement je ? je.GetString() : v?.ToString();

    // ---------- S6a：returnData 顶层（核心，本次发现的 Schema 缺口已修） ----------

    [Fact]
    public void GoldenSample_ReturnData_TopLevel_Parsed()
    {
        // S6a：脚本顶层 returnData（替代旧 billCode）。Schema 顶层 additionalProperties:false，
        // 若未补 returnData 定义则真实脚本会被拒——本测试即验证该定义存在且解析正确。
        var script = Load();

        Assert.NotNull(script.ReturnData);
        Assert.Equal("{{billNo}}", AsString(script.ReturnData!["billCode"]));
        Assert.Equal("{{aiVerify.result}}", AsString(script.ReturnData!["verifyResult"]));
        Assert.Equal("SmartFilling-golden-sample", AsString(script.ReturnData!["source"]));
    }

    // ---------- 字段层：format / transform 收窄 / items object / 嵌套 fields ----------

    [Fact]
    public void GoldenSample_Fields_FormatTransformItemsNested()
    {
        var script = Load();
        var byName = script.Fields.ToDictionary(f => f.Name);

        Assert.Equal("YYYY-MM-DD", byName["billDate"].Format);                       // format（S6c/S7 展示格式）
        Assert.True(byName["billDate"].Required);

        Assert.Equal("trim", byName["amount"].Transform);                            // transform 收窄为 trim/upper/lower（S6c 改动7）
        Assert.NotNull(byName["amount"].Format);                                      // format 与 transform 分工

        Assert.NotNull(byName["tags"].Items);                                          // items 是 object（S4 F.6.1，非 string[]）
        Assert.Equal("string", byName["tags"].Items!.Type);

        Assert.Equal(2, byName["invoiceItems"].Fields!.Count);                        // 嵌套 fields（loopSource 数据源）
        Assert.Equal("goodsName", byName["invoiceItems"].Fields![0].Name);
    }

    // ---------- phase 层：timeout(N1) / iframe 引用 / onError ----------

    [Fact]
    public void GoldenSample_Phase_TimeoutIframeOnError()
    {
        var script = Load();

        Assert.Equal(30000, Phase(script, "fillMain").Timeout);                       // phase.timeout（N1，Schema 已补）
        Assert.Equal("ai-fallback-stop", Phase(script, "fillMain").OnError);          // onError enum（S4）

        Assert.Equal(new[] { "//iframe[contains(@src,'dialog')]" }, Phase(script, "fillInPopup").Iframe);  // 形态 A：phase iframe 是 selector 链 string[]
    }

    // ---------- step 修饰符 / action 专属（S4 改动3 全面对齐） ----------

    [Fact]
    public void GoldenSample_Step_ModifiersAndActionSpecific()
    {
        var script = Load();
        var fillMain = Phase(script, "fillMain");

        var selectStep = fillMain.Steps.OfType<StepNode>().First(s => s.Action == "select");
        Assert.Equal("label", selectStep.MatchBy);                                    // select matchBy（改动2/3）

        var remarkStep = fillMain.Steps.OfType<StepNode>().First(s => s.Field != "billDate" && s.Action == "fill" && s.Selector!.Contains("remark"));
        Assert.True(remarkStep.SkipIfDataEmpty);                                       // skipIfDataEmpty 新名（S4 改名 + S7 语义）

        var uploadStep = fillMain.Steps.OfType<StepNode>().First(s => s.Action == "upload");
        Assert.Equal("{{attachFile}}", uploadStep.FilePath);                           // upload FilePath（S2 N3 映射）
        Assert.True(uploadStep.SkipIfElementMissing);                                  // skipIfElementMissing 新名（S4）

        var clickStep = fillMain.Steps.OfType<StepNode>().First(s => s.Action == "click");
        Assert.NotNull(clickStep.Condition);                                           // step condition 直接写 detect（S4/S9，不包一层）
        Assert.Equal("selector_exists", clickStep.Condition!.Type);
        Assert.NotNull(clickStep.Fallback);                                            // fallback（S6b 继承 Action+Field）
        Assert.Equal("click", clickStep.Fallback!.Action);
    }

    // ---------- detect：keywords（文本类）/ url_changed 方案B / 组合 all ----------

    [Fact]
    public void GoldenSample_Detect_KeywordsUrlChangedCombo()
    {
        var script = Load();
        var submit = Phase(script, "submit");

        var waitStep = submit.Steps.OfType<StepNode>().First(s => s.Action == "wait");
        Assert.NotNull(waitStep.Until);
        Assert.NotNull(waitStep.Until!.All);
        Assert.Equal(2, waitStep.Until.All!.Count);

        Assert.Equal("url_changed", waitStep.Until.All[0].Type);                      // S5 url_changed 方案B（组合条件中）
        Assert.Equal("page_contains", waitStep.Until.All[1].Type);
        Assert.Contains("保存成功", waitStep.Until.All[1].Keywords!);                 // S7/S8 keywords（复数，文本类）

        var checkStep = submit.Steps.OfType<StepNode>().First(s => s.Action == "check");
        Assert.Equal("selector_value", checkStep.Detect!.Type);                       // selector_value 仍用 value（非 keywords）
        Assert.Equal("已提交", checkStep.Detect!.Value);
        Assert.Equal("phase_success", checkStep.Then);                                 // then enum（S4）
        Assert.Equal("提交状态确认", checkStep.Message);

        var extractStep = submit.Steps.OfType<StepNode>().First(s => s.Action == "extract");
        Assert.Equal("billNo", AsString(extractStep.StoreAs));                         // extract storeAs（returnData 引用源）
        Assert.Equal("property", extractStep.ExtractType);                             // extractType（D 类对齐）
    }

    // ---------- loop：嵌套 phase / 行字段路径 / loopCondition 直接写 detect ----------

    [Fact]
    public void GoldenSample_Loop_NestedPhaseAndRowFieldPath()
    {
        var script = Load();
        var loop = Phase(script, "loopInvoiceItems");

        Assert.Equal("loop", loop.Type);
        Assert.Equal("invoiceItems", loop.LoopSource);
        Assert.Equal(50, loop.MaxLoopCount);
        Assert.Equal(1, loop.RowIndexOffset);                                          // rowIndexOffset（改动3）
        Assert.Equal("js", loop.LoopCondition!.Type);                                  // loopCondition 直接写 detect（S4/S9）

        var fillRow = loop.Steps.OfType<PhaseNode>().First(p => p.Name == "fillRow"); // 嵌套 phase
        var fillGoods = fillRow.Steps.OfType<StepNode>().First(s => s.Name == "fillGoods");
        Assert.Equal("{{invoiceItems.goodsName}}", fillGoods.Value);                   // loop 行字段路径（含 . 占位符放行）

        var breakStep = fillRow.Steps.OfType<StepNode>().First(s => s.Then == "nothing");  // 决策13：原 break 改 nothing（fillRow 是 sequential）
        Assert.Equal("nothing", breakStep.Then);                                 // 决策13：break 限 loop，fillRow 是 sequential 改 nothing                                         // then:break（S5 对称 pop）
    }

    // ---------- ai phase：多变量 storeAs（S6b） ----------

    [Fact]
    public void GoldenSample_AiPhase_MultiStoreAs()
    {
        var script = Load();
        var ai = Phase(script, "aiVerify");

        Assert.Equal("ai", ai.Type);
        Assert.Equal("核验填报结果完整且正确", ai.AiGoal);
        Assert.Equal(5, ai.MaxAiTurns);
        Assert.Equal(60000, ai.Timeout);                                               // ai phase timeout

        var aiStep = ai.Steps.OfType<StepNode>().Single();
        Assert.Equal("ai", aiStep.Action);
        // storeAs 必须是 object 多变量格式（S6b ai action storeAs 拆分）
        var storeAs = Assert.IsType<JsonElement>(aiStep.StoreAs);
        Assert.Equal(JsonValueKind.Object, storeAs.ValueKind);
        Assert.True(storeAs.TryGetProperty("result", out _));
        Assert.True(storeAs.TryGetProperty("issues", out _));
    }
}
