using SmartFilling.Engine.Engine;
using SmartFilling.Engine.Models;
using Xunit;

namespace SmartFilling.Engine.Tests.Unit;

/// <summary>
/// F.10.7 运行时 Schema 校验测试：验证 script-v2.json 真正参与运行时校验，
/// 拒绝 S4 破坏性改名/删除后残留的旧字段（skipIfEmpty / keyword / maxPhaseRetries），
/// 避免被 System.Text.Json 默认忽略导致静默失效。
/// </summary>
public class ScriptLoaderSchemaTests
{
    private const string ValidMinimal = """
        {
          "version": 2,
          "scriptId": "test-001",
          "name": "测试脚本",
          "phases": [
            { "kind": "phase", "name": "main", "type": "sequential", "aiGoal": "测试主流程", "steps": [
              { "kind": "step", "name": "checkAlways", "action": "check", "description": "校验", "detect": { "type": "always" }, "then": "nothing" }
            ]}
          ]
        }
        """;

    [Fact]
    public void ValidScript_LoadsWithoutError()
    {
        var script = ScriptLoader.LoadFromJson(ValidMinimal);
        Assert.Equal("测试脚本", script.Name);
    }

    [Fact]
    public void LegacySkipIfEmpty_RejectedBySchema()
    {
        // S4 重命名：skipIfEmpty → skipIfDataEmpty。残留旧名应被 schema（stepNode additionalProperties:false）拒绝。
        var json = ValidMinimal.Replace("\"action\": \"check\"", "\"action\": \"check\", \"skipIfEmpty\": true");
        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.LoadFromJson(json));
        Assert.Contains("Schema", ex.Message);
    }

    [Fact]
    public void LegacySkipIfMissing_RejectedBySchema()
    {
        // S4 重命名：skipIfMissing → skipIfElementMissing。
        var json = ValidMinimal.Replace("\"action\": \"check\"", "\"action\": \"check\", \"skipIfMissing\": true");
        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.LoadFromJson(json));
        Assert.Contains("Schema", ex.Message);
    }

    [Fact]
    public void LegacyKeyword_RejectedBySchema()
    {
        // F.10.6：detect 删单数 keyword（单条件分支 additionalProperties:false）。
        var json = ValidMinimal.Replace("\"type\": \"always\"", "\"type\": \"page_contains\", \"keyword\": \"成功\"");
        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.LoadFromJson(json));
        Assert.Contains("Schema", ex.Message);
    }

    [Fact]
    public void RemovedMaxPhaseRetries_RejectedBySchema()
    {
        // H.5.1：scriptSettings 删 maxPhaseRetries（ScriptSettings 模型不读，孤儿字段）。
        var json = ValidMinimal.Replace("\"name\": \"测试脚本\"", "\"name\": \"测试脚本\", \"settings\": { \"maxPhaseRetries\": 1 }");
        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.LoadFromJson(json));
        Assert.Contains("Schema", ex.Message);
    }

    [Fact]
    public void NewFields_AcceptedBySchema()
    {
        // S4 新增字段（skipIfDataEmpty / phase.timeout / items object）应被 schema 接受。
        var json = """
        {
          "version": 2,
          "scriptId": "test-002",
          "name": "新字段测试",
          "fields": [
            { "name": "tags", "label": "标签", "type": "array", "items": { "type": "string" } }
          ],
          "phases": [
            { "kind": "phase", "name": "main", "type": "sequential", "aiGoal": "新字段测试", "timeout": 30000, "steps": [
              { "kind": "step", "name": "fillTags", "action": "fill", "selector": "#i", "value": "{{tags}}", "description": "填值", "skipIfDataEmpty": true }
            ]}
          ]
        }
        """;
        var script = ScriptLoader.LoadFromJson(json);
        Assert.Equal("新字段测试", script.Name);
    }

    [Fact]
    public void AiPhaseStep_NullAction_RejectedByBusinessValidation()
    {
        // F12：ai phase 下 step 的 action 为 null 应被业务校验拒绝。
        // 旧守卫 `step.Action != null && step.Action != "ai"` 会放行 null；F12 新守卫 `step.Action != "ai"` 不放行。
        // 直达 ScriptLoader.Validate 绕过 schema——schema required action 正常路径已先行拦截 null，
        // 本测试覆盖「schema 降级（嵌入资源缺失）时业务校验兜底」这条 F12 守卫路径（schema 层已有 required action，业务层是双保险）。
        // StepNode.Action 是非空 string（默认 ""），此处 null! 模拟「JSON 显式 "action":null 反序列化后运行时为 null」的真实降级态。
        var script = new ScriptV2
        {
            Name = "test",
            Phases =
            [
                new PhaseNode
                {
                    Kind = "phase",
                    Name = "ai1",
                    Type = "ai",
                    AiGoal = "ai phase 测试",
                    Steps =
                    [
                        new StepNode { Kind = "step", Name = "s1", Action = null! }
                    ]
                }
            ]
        };
        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.Validate(script));
        Assert.Contains("action 必须为 'ai'", ex.Message);
        Assert.Contains("当前为 'null'", ex.Message);  // step.Action ?? "null" 渲染
    }

    [Fact]
    public void F6_TypelessSequentialPhase_LoadsWithoutError()
    {
        // F6：phaseNode 的 type 非必填，省略 type 的 sequential phase 应通过加载。
        // 旧 schema 的 then:required[loopSource] 因「if.properties.type.const=loop 空真」（缺 type 时空真通过）
        // 会误伤省略 type 的 sequential phase——已删除该 if/then 约束。
        var json = """
        {
          "version": 2,
          "scriptId": "f6-001",
          "name": "无 type sequential",
          "phases": [
            { "kind": "phase", "name": "main", "aiGoal": "测试主流程", "steps": [
              { "kind": "step", "name": "checkAlways", "action": "check", "description": "校验", "detect": { "type": "always" }, "then": "nothing" }
            ]}
          ]
        }
        """;
        var script = ScriptLoader.LoadFromJson(json);
        Assert.Equal("无 type sequential", script.Name);
    }

    [Fact]
    public void F6_PureLoopConditionLoop_LoadsWithoutError()
    {
        // F6：纯 loopCondition 翻页 loop（无 loopSource）应通过加载——设计 loopSource/loopCondition 二选一或共存
        // （设计 L490 / 手册 L532），代码 ScriptEngine 已正确支持（loopSource null 时靠 loopCondition + maxLoopCount 兜底）。
        // 旧 schema then:required[loopSource] 错误拒绝此类合法翻页 loop，已删除。
        var json = """
        {
          "version": 2,
          "scriptId": "f6-002",
          "name": "纯 loopCondition 翻页",
          "phases": [
            { "kind": "phase", "name": "main", "type": "loop", "aiGoal": "翻页直到无下一页", "loopCondition": { "type": "selector_exists", "selector": ".next-page" }, "maxLoopCount": 50, "steps": [
              { "kind": "step", "name": "clickNext", "action": "click", "selector": ".next-page", "description": "下一页" }
            ]}
          ]
        }
        """;
        var script = ScriptLoader.LoadFromJson(json);
        Assert.Equal("纯 loopCondition 翻页", script.Name);
    }

    [Fact]
    public void F6_LoopSourceAndConditionCoexist_LoadsWithoutError()
    {
        // F6：loopSource + loopCondition 共存应通过加载（设计「两者可共存」）。
        var json = """
        {
          "version": 2,
          "scriptId": "f6-003",
          "name": "共存 loop",
          "fields": [
            { "name": "rows", "label": "行", "type": "array", "items": { "type": "object" } }
          ],
          "phases": [
            { "kind": "phase", "name": "main", "type": "loop", "aiGoal": "遍历行填写", "loopSource": "rows", "loopCondition": { "type": "selector_exists", "selector": ".more" }, "maxLoopCount": 100, "steps": [
              { "kind": "step", "name": "fillRow", "action": "fill", "selector": "#i", "value": "{{rows.name}}", "description": "填行" }
            ]}
          ]
        }
        """;
        var script = ScriptLoader.LoadFromJson(json);
        Assert.Equal("共存 loop", script.Name);
    }

    [Fact]
    public void F4_StoreAs_ReferenceInLaterStep_LoadsWithoutError()
    {
        // F4(R1-3)：step A extract storeAs:"total" → step B value:"{{total}}" 引用应通过加载。
        // 旧 ValidatePlaceholders 不认 storeAs 变量，会误报 "total 未在 fields[] 定义" 阻断加载（storeAs→后续引用是设计十五核心特性）。
        var json = """
        {
          "version": 2,
          "scriptId": "f4-001",
          "name": "storeAs 引用",
          "phases": [
            { "kind": "phase", "name": "main", "type": "sequential", "aiGoal": "提取并回填", "steps": [
              { "kind": "step", "name": "extractUrl", "action": "extract", "extractType": "url", "storeAs": "total", "description": "提取当前URL" },
              { "kind": "step", "name": "fillEcho", "action": "fill", "selector": "#echo", "value": "{{total}}", "description": "回填" }
            ]}
          ]
        }
        """;
        var script = ScriptLoader.LoadFromJson(json);
        Assert.Equal("storeAs 引用", script.Name);
    }

    [Fact]
    public void F4_StoreAs_ReferenceViaValidate_NotRejected()
    {
        // F4 直达 Validate（绕过 schema）：storeAs 变量被后续 step {{var}} 引用不应误报。
        // 兜底用例——即便 schema 对 extract 细节有要求，本测试证明 ValidatePlaceholders 的 storeAs 识别逻辑正确。
        var script = new ScriptV2
        {
            Name = "f4-validate",
            Phases =
            [
                new PhaseNode
                {
                    Kind = "phase", Name = "main", Type = "sequential", AiGoal = "提取并回填",
                    Steps =
                    [
                        new StepNode { Kind = "step", Name = "extractTotal", Action = "extract", ExtractType = "url", StoreAs = "total" },
                        new StepNode { Kind = "step", Name = "fillEcho", Action = "fill", Selector = "#echo", Value = "{{total}}" }
                    ]
                }
            ]
        };
        ScriptLoader.Validate(script);  // 不抛即通过
    }

    [Fact]
    public void R3_2_PhaseWithoutAiGoal_Rejected()
    {
        // R3-2：所有 phase aiGoal 严格必填，缺 aiGoal 应加载失败（不兜底、不掩盖不完整脚本，人工介入）。
        var script = new ScriptV2
        {
            Name = "r3-2-test",
            Phases =
            [
                new PhaseNode
                {
                    Kind = "phase", Name = "main", Type = "sequential",  // 故意不设 AiGoal
                    Steps = [new StepNode { Kind = "step", Name = "s1", Action = "check", Detect = new DetectCondition { Type = "always" }, Then = "nothing" }]
                }
            ]
        };
        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.Validate(script));
        Assert.Contains("aiGoal", ex.Message);
    }

    [Fact]
    public void PressKey_MissingKey_RejectedBySchema()
    {
        // C④（M1 / P5）：pressKey 的 key 字段必填（schema stepNode allOf: {if action=pressKey then required:[key]}）。
        // 三文档/代码/prompt 均假定 key 必填，schema 缺配会让"缺 key 的 pressKey"静默加载 → 回放 ExecutePressKeyAsync 读 step.Key=null 报错或 no-op（silent-success）。
        // 负向测试：缺 key 的 pressKey step 应被 schema 拒（LoadFromJson 经 JsonSchema.Net 校验）。
        var json = """
        {
          "version": 2,
          "scriptId": "pk-001",
          "name": "pressKey 缺 key",
          "phases": [
            { "kind": "phase", "name": "main", "type": "sequential", "aiGoal": "按键", "steps": [
              { "kind": "step", "name": "pressEnter", "action": "pressKey", "description": "按回车" }
            ]}
          ]
        }
        """;
        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.LoadFromJson(json));
        Assert.Contains("Schema", ex.Message);
    }

    [Fact]
    public void PressKey_WithKey_LoadsWithoutError()
    {
        // C④ 正向：pressKey 带 key（含无 selector 的全局按键，pressKey 不强制 selector）应通过加载。
        var json = """
        {
          "version": 2,
          "scriptId": "pk-002",
          "name": "pressKey 全局",
          "phases": [
            { "kind": "phase", "name": "main", "type": "sequential", "aiGoal": "按键", "steps": [
              { "kind": "step", "name": "pressEnter", "action": "pressKey", "key": "Enter", "description": "按回车" }
            ]}
          ]
        }
        """;
        var script = ScriptLoader.LoadFromJson(json);
        Assert.Equal("pressKey 全局", script.Name);
    }

    [Fact]
    public void Placeholder_LoopAttachmentPath_AcceptedByFileField()
    {
        // 28 golden 暴露的 latent bug 修复：loopSource=file 字段（attachments）的 loop rowData 是附件对象 {name,path,url}，
        // {{path}} 取当前行附件属性。原 ValidatePlaceholders 拒绝 {{path}}（不在顶层 fields），修复后 CollectFieldDefinitions
        // 对 type=file 字段补 name/path/url 为合法占位符 → 放行（否则录制产出的 loopSource=attachments upload 脚本回放加载失败 400）。
        var json = """
        {
          "version": 2,
          "scriptId": "ph-001",
          "name": "loop attachment path",
          "fields": [
            { "name": "attachments", "label": "附件", "type": "file", "uiComponent": "upload", "multiple": true }
          ],
          "phases": [
            { "kind": "phase", "name": "loop", "type": "loop", "loopSource": "attachments", "rowIndexOffset": 1, "aiGoal": "逐行上传",
              "steps": [
                { "kind": "step", "name": "upload-row", "action": "upload", "selector": "//tr[@rowindex='{{rowIndex}}']//input", "filePath": "{{path}}" }
              ]}
          ]
        }
        """;
        var script = ScriptLoader.LoadFromJson(json);  // 修复前抛"{{path}} 未定义"，修复后通过
        Assert.Equal("loop attachment path", script.Name);
    }

    [Fact]
    public void ValidateSchemaAndGetErrors_UnknownField_ReturnsErrors()
    {
        // public 契约（schema 补全）：未知字段（step 级 ifrmae 拼错）应被 schema 抓到 → 返回非空错误列表（List<string> 类型）
        var json = ValidMinimal.Replace("\"action\": \"check\"", "\"action\": \"check\", \"ifrmae\": \"f1\"");
        var errors = ScriptLoader.ValidateSchemaAndGetErrors(json, lenientNull: false);
        Assert.IsType<List<string>>(errors);  // 返回类型
        Assert.NotEmpty(errors);  // 未知字段被抓（stepNode additionalProperties:false）
    }

    [Fact]
    public void ValidateSchemaAndGetErrors_ValidScript_ReturnsEmpty()
    {
        // public 契约：合法脚本 → 空列表（通过）
        var errors = ScriptLoader.ValidateSchemaAndGetErrors(ValidMinimal, lenientNull: false);
        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateSchemaAndGetErrors_InvalidJson_DoesNotThrow_ReturnsEmpty()
    {
        // public 契约：JSON 语法错（ValidateAgainstSchema 内 JsonDocument.Parse catch 返回 null）→ 不抛 + 空列表（降级，结构坏交给 Deserialize 报）；
        // ValidateSchemaAndGetErrors 自己的 try/catch 兜底任何意外异常（🔴不抛，避免被 GetScriptWithErrors/GetAllScripts 外层 catch 吞成"结构坏"语义错位）
        var errors = ScriptLoader.ValidateSchemaAndGetErrors("{invalid json", lenientNull: false);
        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateSchemaAndGetErrors_SchemaNotLoaded_DegradesToEmpty()
    {
        // 决策2（schema 未加载降级直接单测）：模拟嵌入资源加载失败（_scriptSchema=null）→ schema 校验降级返空（ValidateAgainstSchema if(_scriptSchema==null) return null）。
        // 用含未知字段 ifrmae 的 json 区分降级：正常（schema 加载）→ errors 非空（ifrmae 被抓，见 UnknownField_ReturnsErrors）；降级（_scriptSchema=null）→ errors 空（schema 整体跳过，ifrmae 不被抓）。
        // _scriptSchema 是 private static（非 readonly——决策2 去掉 readonly 以支持 .NET 8 反射注入降级场景），反射修改；finally 恢复避免影响其他测试（static 字段进程级，xUnit 同类串行故本类内安全）。
        var field = typeof(ScriptLoader).GetField("_scriptSchema", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(field);
        var original = field!.GetValue(null);  // 保存原值（有效 JsonSchema）
        var jsonWithUnknownField = ValidMinimal.Replace("\"action\": \"check\"", "\"action\": \"check\", \"ifrmae\": \"f1\"");
        try
        {
            field.SetValue(null, null);
            var errors = ScriptLoader.ValidateSchemaAndGetErrors(jsonWithUnknownField, lenientNull: false);
            Assert.Empty(errors);  // 降级返空（schema 未加载，ifrmae 未知字段不被抓）
        }
        finally
        {
            field.SetValue(null, original);  // 恢复有效 schema，避免影响后续测试
        }
    }

    // B″：script-v2.json transform 加 enum ["trim","upper","lower"]，保存层校验 transform 值域。
    // 经 LoadFromJson 生产路径（schema enum 拦截），断言验错误信息含 transform 片段不只验抛异常。

    [Fact]
    public void B2_Transform_ValidEnum_Loads()
    {
        var json = """
        {
          "version": 2, "scriptId": "b2-001", "name": "transform 合法",
          "fields": [
            { "name": "f1", "label": "字段1", "type": "string", "transform": "trim" },
            { "name": "f2", "label": "字段2", "type": "string", "transform": "upper" },
            { "name": "f3", "label": "字段3", "type": "string", "transform": "lower" }
          ],
          "phases": [{ "kind": "phase", "name": "main", "type": "sequential", "aiGoal": "g", "steps": [
            { "kind": "step", "name": "checkAlways", "action": "check", "description": "校验", "detect": { "type": "always" }, "then": "nothing" }
          ]}]
        }
        """;
        var script = ScriptLoader.LoadFromJson(json);
        Assert.Equal("trim", script.Fields[0].Transform);
        Assert.Equal("upper", script.Fields[1].Transform);
        Assert.Equal("lower", script.Fields[2].Transform);
    }

    [Theory]
    [InlineData("trim ")]   // 尾空格（非精确匹配 enum）
    [InlineData("Trim")]    // 大小写（enum 大小写敏感）
    [InlineData("unknown")] // 非法值
    public void B2_Transform_InvalidEnum_RejectedBySchema(string badTransform)
    {
        var json = $$"""
        {
          "version": 2, "scriptId": "b2-002", "name": "transform 非法",
          "fields": [{ "name": "f", "label": "F", "type": "string", "transform": "{{badTransform}}" }],
          "phases": [{ "kind": "phase", "name": "main", "type": "sequential", "aiGoal": "g", "steps": [
            { "kind": "step", "name": "checkAlways", "action": "check", "description": "校验", "detect": { "type": "always" }, "then": "nothing" }
          ]}]
        }
        """;
        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.LoadFromJson(json));
        Assert.Contains("transform", ex.Message);  // schema 错误信息含字段路径 /fields/0/transform
    }

    [Fact]
    public void B2_Transform_Omitted_LoadsWithoutError()
    {
        var json = """
        {
          "version": 2, "scriptId": "b2-003", "name": "transform 省略",
          "fields": [{ "name": "f", "label": "F", "type": "string" }],
          "phases": [{ "kind": "phase", "name": "main", "type": "sequential", "aiGoal": "g", "steps": [
            { "kind": "step", "name": "checkAlways", "action": "check", "description": "校验", "detect": { "type": "always" }, "then": "nothing" }
          ]}]
        }
        """;
        var script = ScriptLoader.LoadFromJson(json);
        Assert.Null(script.Fields[0].Transform);  // 省略 → null
    }

    // F：selector 非法字符校验（加载期 ValidateSelectorCharset）。把"回放才崩"提前到"加载就拒"。
    // 经 LoadFromJson 生产路径（弯引号是合法 JSON 字符串内容 → schema 通过 → Deserialize 成功 → Validate F 报错 → throw）。

    [Fact]
    public void F_SelectorCurlyQuote_Rejected()
    {
        // step.Selector 含弯引号 U+2019（Bug 3 同型）→ F 拦截，错误信息含码点 + step 位置 + 片段
        var json = """
        {
          "version": 2, "scriptId": "f-001", "name": "selector 弯引号",
          "phases": [{ "kind": "phase", "name": "main", "type": "sequential", "aiGoal": "g", "steps": [
            { "kind": "step", "name": "clickAdd", "action": "click", "selector": "//a[@id=’zgy_add’]", "description": "点" }
          ]}]
        }
        """;
        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.LoadFromJson(json));
        Assert.Contains("U+2019", ex.Message);
        Assert.Contains("clickAdd", ex.Message);  // step 位置
    }

    [Fact]
    public void F_SelectorChinesePunctuation_AcceptedNotFalsePositive()
    {
        // selector 含中文逗号/括号（合法出现在 text() 参数里）——F 只校验弯引号+全角空格，不误报中文标点
        var json = """
        {
          "version": 2, "scriptId": "f-002", "name": "中文标点不误报",
          "phases": [{ "kind": "phase", "name": "main", "type": "sequential", "aiGoal": "g", "steps": [
            { "kind": "step", "name": "clickItem", "action": "click", "selector": "//div[contains(text(),'中文，文本')]", "description": "点" }
          ]}]
        }
        """;
        var script = ScriptLoader.LoadFromJson(json);  // 不抛
        Assert.Equal("中文标点不误报", script.Name);
    }

    [Fact]
    public void F_IframeChainCurlyQuote_Rejected()
    {
        // iframe 链某层含弯引号 → F 拦截
        var json = """
        {
          "version": 2, "scriptId": "f-003", "name": "iframe 链弯引号",
          "phases": [{ "kind": "phase", "name": "main", "type": "sequential", "aiGoal": "g", "steps": [
            { "kind": "step", "name": "fillName", "action": "fill", "selector": "//input", "value": "x", "iframe": ["//iframe[@id=’frm’]"], "description": "填" }
          ]}]
        }
        """;
        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.LoadFromJson(json));
        Assert.Contains("U+2019", ex.Message);
        Assert.Contains("iframe", ex.Message);
    }

    [Fact]
    public void F_DetectSelectorCurlyQuote_Rejected()
    {
        // detect.Selector 含弯引号 → F 拦截（递归 detect 树）
        var json = """
        {
          "version": 2, "scriptId": "f-004", "name": "detect 弯引号",
          "phases": [{ "kind": "phase", "name": "main", "type": "sequential", "aiGoal": "g", "steps": [
            { "kind": "step", "name": "checkVisible", "action": "check", "description": "查", "detect": { "type": "selector_visible", "selector": "//div[@id=’x’]" }, "then": "nothing" }
          ]}]
        }
        """;
        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.LoadFromJson(json));
        Assert.Contains("U+2019", ex.Message);
    }

    [Fact]
    public void F_CaptchaSelectorCurlyQuote_Rejected()
    {
        // captcha sliderSelector 含弯引号 → F 拦截（captcha 5 选择器，名字不带 Selector 后缀易漏）
        var json = """
        {
          "version": 2, "scriptId": "f-005", "name": "captcha 弯引号",
          "phases": [{ "kind": "phase", "name": "main", "type": "sequential", "aiGoal": "g", "steps": [
            { "kind": "step", "name": "solveSlide", "action": "captcha", "captchaType": "slide", "sliderSelector": "//div[@class=’slider’]", "targetSelector": "//img[1]", "backgroundSelector": "//img[2]", "description": "验证码" }
          ]}]
        }
        """;
        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.LoadFromJson(json));
        Assert.Contains("U+2019", ex.Message);
        Assert.Contains("sliderSelector", ex.Message);
    }

    [Fact]
    public void F_FallbackSelectorCurlyQuote_Rejected()
    {
        // fallback.Selector 含弯引号 → F 拦截（递归 fallback 链）
        var json = """
        {
          "version": 2, "scriptId": "f-006", "name": "fallback 弯引号",
          "phases": [{ "kind": "phase", "name": "main", "type": "sequential", "aiGoal": "g", "steps": [
            { "kind": "step", "name": "clickBtn", "action": "click", "selector": "//button", "fallback": { "selector": "//button[@id=’go’]" }, "description": "点" }
          ]}]
        }
        """;
        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.LoadFromJson(json));
        Assert.Contains("U+2019", ex.Message);
    }

    // 注释支持（2026-07-05）：ScriptLoader._jsonOptions 加 ReadCommentHandling=Skip，脚本 JSON 支持 // 行注释与 /* 块注释 */。
    // schema 阶段 JsonDocument.Parse 默认 CommentHandling=Skip 已容忍，此处反序列化对齐（默认 Disallow 抛 JsonException）。
    // 经 LoadFromJson 生产路径，断言验值不只验通过。不开 AllowTrailingCommas（用户决策：只要注释）。

    [Fact]
    public void JsonComment_LineComment_TopLevel_Loads()
    {
        // 顶层 // 行注释（独占一行）应被跳过，注释后的 scriptId/name 正确解析
        var json = ValidMinimal.Replace("\"version\": 2,", "\"version\": 2,\n  // 顶层行注释：示例说明\n");
        var script = ScriptLoader.LoadFromJson(json);
        Assert.Equal("test-001", script.ScriptId);
        Assert.Equal("测试脚本", script.Name);
    }

    [Fact]
    public void JsonComment_BlockComment_TopLevel_Loads()
    {
        // /* 块注释 */（含跨行）出现在属性名前应被跳过
        var json = ValidMinimal.Replace("\"version\": 2,", "/* 块注释：\n  跨行说明 */ \"version\": 2,");
        var script = ScriptLoader.LoadFromJson(json);
        Assert.Equal("test-001", script.ScriptId);
    }

    [Fact]
    public void JsonComment_BlockComment_InStep_Loads()
    {
        // step 内注释：单行 JSON 用 // 会吞到行尾破坏后续字段，须用 /* */（只吃到 */）。
        // 验值：注释后 description/detect/then 字段仍正确解析（防"注释 skip 误吞字段"的 silent 回归）。
        var json = ValidMinimal.Replace("\"description\": \"校验\",", "\"description\": \"校验\", /* step 内块注释 */");
        var script = ScriptLoader.LoadFromJson(json);
        var phase = Assert.IsType<PhaseNode>(script.Phases[0]);
        var step = Assert.IsType<StepNode>(phase.Steps[0]);
        Assert.Equal("checkAlways", step.Name);
        Assert.Equal("校验", step.Description);  // 注释不影响字段值
    }

    [Fact]
    public void JsonComment_DoesNotBypassSchemaValidation()
    {
        // 防回归：注释 Skip ≠ 未知字段放行。带注释 + 未知字段（skipIfEmpty 旧名）仍被 schema additionalProperties:false 拒。
        var json = ValidMinimal
            .Replace("\"version\": 2,", "\"version\": 2,\n  // 注释\n")
            .Replace("\"action\": \"check\"", "\"action\": \"check\", \"skipIfEmpty\": true");
        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.LoadFromJson(json));
        Assert.Contains("Schema", ex.Message);
    }

    // WaitLoadAfterNavigation 已于 2026-07-14 彻底移除（功能净效果三类冗余+一类有害：navigate/goBack/reload 自带 WaitUntil=Load 冗余、click 弹窗类 racy 掉弹窗有害）。
    // 新名 waitLoadAfterNavigation 经移除后被 schema additionalProperties:false 拒；旧名 waitForNavigation 仍被拒（additionalProperties:false 与字段是否存在无关）。
    // 走 LoadFromJson 完整路径（schema 校验 + 反序列化），断言验值（.Value 防 null 漏检）不只验状态。

    [Fact]
    public void WaitLoadAfterNavigation_Removed_RejectedBySchema_InSettings()
    {
        // 守护（settings 层）：移除新名后，settings 注入 waitLoadAfterNavigation 应被 schema scriptSettings additionalProperties:false 拒（防第7步漏删 settings 层 schema key）。
        var json = ValidMinimal.Replace("\"name\": \"测试脚本\"", "\"name\": \"测试脚本\", \"settings\": { \"waitLoadAfterNavigation\": true }");
        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.LoadFromJson(json));
        Assert.Contains("Schema", ex.Message);
    }

    [Fact]
    public void WaitLoadAfterNavigation_Removed_RejectedBySchema_InPreSetup()
    {
        // 守护（preSetup 层）：移除新名后，step.preSetup 注入 waitLoadAfterNavigation 应被 schema preSetup additionalProperties:false 拒（防第7步漏删 preSetup 层 schema key；两层独立各测防某层漏删 silent 回归）。
        var json = ValidMinimal.Replace("\"action\": \"check\"",
            "\"action\": \"check\", \"preSetup\": { \"waitLoadAfterNavigation\": true }");
        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.LoadFromJson(json));
        Assert.Contains("Schema", ex.Message);
    }

    [Fact]
    public void WaitForNavigation_Legacy_RejectedBySchema_InSettings()
    {
        // Legacy 守护（TD-W9① settings 层）：旧名 waitForNavigation 注入 settings 层应被 schema scriptSettings additionalProperties:false 拒（防回归成 alias）。
        var json = ValidMinimal.Replace("\"name\": \"测试脚本\"", "\"name\": \"测试脚本\", \"settings\": { \"waitForNavigation\": true }");
        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.LoadFromJson(json));
        Assert.Contains("Schema", ex.Message);
    }

    [Fact]
    public void WaitForNavigation_Legacy_RejectedBySchema_InPreSetup()
    {
        // Legacy 守护（TD-W9① preSetup 层）：旧名 waitForNavigation 注入 step.preSetup 层应被 schema preSetup additionalProperties:false 拒。
        // schema settings 层 + preSetup 层 两处独立——漏改某层不编译报错，单层单测发现不了，必须两层各测（防"某层漏改名"silent 回归成 alias）。
        var json = ValidMinimal.Replace("\"action\": \"check\"",
            "\"action\": \"check\", \"preSetup\": { \"waitForNavigation\": true }");
        var ex = Assert.Throws<InvalidOperationException>(() => ScriptLoader.LoadFromJson(json));
        Assert.Contains("Schema", ex.Message);
    }
}
