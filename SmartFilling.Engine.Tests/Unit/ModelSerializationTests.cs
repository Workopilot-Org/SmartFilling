using System.Text.Json;
using SmartFilling.Engine.Models;

namespace SmartFilling.Engine.Tests.Unit;

public class ModelSerializationTests
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions _roundtripOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public void Deserialize_MinimalScript()
    {
        var json = """
        {
            "version": 2,
            "scriptId": "test-001",
            "name": "Minimal Script"
        }
        """;

        var script = JsonSerializer.Deserialize<ScriptV2>(json, _options);

        Assert.NotNull(script);
        Assert.Equal(2, script.Version);
        Assert.Equal("test-001", script.ScriptId);
        Assert.Equal("Minimal Script", script.Name);
        Assert.Empty(script.Phases);
        Assert.Empty(script.Fields);
        Assert.Null(script.Settings);
    }

    [Fact]
    public void Deserialize_PhaseItemConverter_Step()
    {
        var json = """
        {
            "version": 2,
            "scriptId": "s1",
            "name": "Step Test",
            "phases": [
                {
                    "kind": "phase",
                    "name": "main",
                    "steps": [
                        {
                            "kind": "step",
                            "action": "click",
                            "selector": "#btn"
                        }
                    ]
                }
            ]
        }
        """;

        var script = JsonSerializer.Deserialize<ScriptV2>(json, _options);

        Assert.Single(script!.Phases);
        var phase = Assert.IsType<PhaseNode>(script.Phases[0]);
        Assert.Equal("main", phase.Name);
        Assert.Single(phase.Steps);
        var step = Assert.IsType<StepNode>(phase.Steps[0]);
        Assert.Equal("click", step.Action);
        Assert.Equal("#btn", step.Selector);
    }

    [Fact]
    public void Deserialize_PhaseItemConverter_Phase()
    {
        var json = """
        {
            "version": 2,
            "scriptId": "s2",
            "name": "Phase Test",
            "phases": [
                {
                    "kind": "phase",
                    "name": "login",
                    "type": "sequential",
                    "steps": []
                }
            ]
        }
        """;

        var script = JsonSerializer.Deserialize<ScriptV2>(json, _options);

        Assert.Single(script!.Phases);
        var phase = Assert.IsType<PhaseNode>(script.Phases[0]);
        Assert.Equal("login", phase.Name);
        Assert.Equal("sequential", phase.Type);
    }

    [Fact]
    public void Deserialize_MixedSteps()
    {
        var json = """
        {
            "version": 2,
            "scriptId": "s3",
            "name": "Mixed",
            "phases": [
                {
                    "kind": "phase",
                    "name": "outer",
                    "steps": [
                        {
                            "kind": "step",
                            "action": "navigate",
                            "url": "https://example.com"
                        },
                        {
                            "kind": "phase",
                            "name": "inner",
                            "steps": [
                                {
                                    "kind": "step",
                                    "action": "fill",
                                    "selector": "#input",
                                    "value": "hello"
                                }
                            ]
                        },
                        {
                            "kind": "step",
                            "action": "click",
                            "selector": "#submit"
                        }
                    ]
                }
            ]
        }
        """;

        var script = JsonSerializer.Deserialize<ScriptV2>(json, _options);

        var outer = Assert.IsType<PhaseNode>(script!.Phases[0]);
        Assert.Equal(3, outer.Steps.Count);

        var navStep = Assert.IsType<StepNode>(outer.Steps[0]);
        Assert.Equal("navigate", navStep.Action);

        var innerPhase = Assert.IsType<PhaseNode>(outer.Steps[1]);
        Assert.Equal("inner", innerPhase.Name);
        Assert.Single(innerPhase.Steps);

        var fillStep = Assert.IsType<StepNode>(innerPhase.Steps[0]);
        Assert.Equal("fill", fillStep.Action);
        Assert.Equal("hello", fillStep.Value);

        var clickStep = Assert.IsType<StepNode>(outer.Steps[2]);
        Assert.Equal("click", clickStep.Action);
    }

    [Fact]
    public void Deserialize_MissingKind_Throws()
    {
        var json = """
        {
            "version": 2,
            "scriptId": "s4",
            "name": "Missing Kind",
            "phases": [
                {
                    "action": "click",
                    "selector": "#btn"
                }
            ]
        }
        """;

        var ex = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ScriptV2>(json, _options));

        Assert.Contains("kind", ex.Message);
    }

    [Fact]
    public void Deserialize_UnknownKind_Throws()
    {
        var json = """
        {
            "version": 2,
            "scriptId": "s5",
            "name": "Unknown Kind",
            "phases": [
                {
                    "kind": "unknown_type",
                    "action": "click"
                }
            ]
        }
        """;

        var ex = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ScriptV2>(json, _options));

        Assert.Contains("unknown_type", ex.Message);
    }

    [Fact]
    public void Deserialize_NestedPhases()
    {
        var json = """
        {
            "version": 2,
            "scriptId": "s6",
            "name": "Nested",
            "phases": [
                {
                    "kind": "phase",
                    "name": "L1",
                    "steps": [
                        {
                            "kind": "phase",
                            "name": "L2",
                            "steps": [
                                {
                                    "kind": "phase",
                                    "name": "L3",
                                    "steps": [
                                        {
                                            "kind": "step",
                                            "action": "click",
                                            "selector": "#deep"
                                        }
                                    ]
                                }
                            ]
                        }
                    ]
                }
            ]
        }
        """;

        var script = JsonSerializer.Deserialize<ScriptV2>(json, _options);

        var l1 = Assert.IsType<PhaseNode>(script!.Phases[0]);
        Assert.Equal("L1", l1.Name);

        var l2 = Assert.IsType<PhaseNode>(l1.Steps[0]);
        Assert.Equal("L2", l2.Name);

        var l3 = Assert.IsType<PhaseNode>(l2.Steps[0]);
        Assert.Equal("L3", l3.Name);

        var deepStep = Assert.IsType<StepNode>(l3.Steps[0]);
        Assert.Equal("click", deepStep.Action);
        Assert.Equal("#deep", deepStep.Selector);
    }

    [Fact]
    public void Deserialize_Step_AllActionTypes()
    {
        var json = """
        {
            "version": 2,
            "scriptId": "s7",
            "name": "All Actions",
            "phases": [
                {
                    "kind": "phase",
                    "name": "actions",
                    "steps": [
                        { "kind": "step", "action": "navigate", "url": "https://example.com" },
                        { "kind": "step", "action": "click", "selector": "#btn" },
                        { "kind": "step", "action": "fill", "selector": "#input", "value": "text" },
                        { "kind": "step", "action": "check", "selector": "#chk", "accept": true },
                        { "kind": "step", "action": "wait", "ms": 2000 },
                        { "kind": "step", "action": "extract", "selector": "#result", "field": "F_NAME", "storeAs": "nameVar" },
                        { "kind": "step", "action": "ai", "aiGoal": "Fill the form", "maxAiTurns": 5 },
                        { "kind": "step", "action": "captcha", "captchaType": "slider", "imageSelector": "#img", "inputSelector": "#input", "sliderSelector": "#slider", "targetSelector": "#target", "backgroundSelector": "#bg" },
                        { "kind": "step", "action": "goto", "url": "https://other.com" },
                        { "kind": "step", "action": "press", "key": "Enter" },
                        { "kind": "step", "action": "scroll", "selector": "#section", "direction": "down", "amount": 300 },
                        { "kind": "step", "action": "evaluate", "code": "document.title" }
                    ]
                }
            ]
        }
        """;

        var script = JsonSerializer.Deserialize<ScriptV2>(json, _options);

        var phase = Assert.IsType<PhaseNode>(script!.Phases[0]);
        Assert.Equal(12, phase.Steps.Count);

        var nav = Assert.IsType<StepNode>(phase.Steps[0]);
        Assert.Equal("navigate", nav.Action);
        Assert.Equal("https://example.com", nav.Url);

        var click = Assert.IsType<StepNode>(phase.Steps[1]);
        Assert.Equal("click", click.Action);

        var fill = Assert.IsType<StepNode>(phase.Steps[2]);
        Assert.Equal("fill", fill.Action);
        Assert.Equal("text", fill.Value);

        var check = Assert.IsType<StepNode>(phase.Steps[3]);
        Assert.Equal("check", check.Action);
        Assert.True(check.Accept);

        var wait = Assert.IsType<StepNode>(phase.Steps[4]);
        Assert.Equal("wait", wait.Action);
        Assert.Equal(2000, wait.Ms);

        var extract = Assert.IsType<StepNode>(phase.Steps[5]);
        Assert.Equal("extract", extract.Action);
        Assert.Equal("F_NAME", extract.Field);

        var ai = Assert.IsType<StepNode>(phase.Steps[6]);
        Assert.Equal("ai", ai.Action);
        Assert.Equal(5, ai.MaxAiTurns);

        var scroll = Assert.IsType<StepNode>(phase.Steps[10]);
        Assert.Equal("scroll", scroll.Action);
        Assert.Equal("down", scroll.Direction);
        Assert.Equal(300, scroll.Amount);

        var eval = Assert.IsType<StepNode>(phase.Steps[11]);
        Assert.Equal("evaluate", eval.Action);
        Assert.Equal("document.title", eval.Code);
    }

    [Fact]
    public void Deserialize_AllDetectTypes()
    {
        var json = """
        {
            "version": 2,
            "scriptId": "s8",
            "name": "Detect Test",
            "phases": [
                {
                    "kind": "phase",
                    "name": "detect",
                    "condition": {
                        "type": "url_changed",
                        "urlContains": "/login"
                    },
                    "steps": [
                        {
                            "kind": "step",
                            "action": "check",
                            "detect": { "type": "selector_visible", "selector": "#loaded" }
                        },
                        {
                            "kind": "step",
                            "action": "check",
                            "detect": { "type": "js", "check": "document.readyState === 'complete'" }
                        },
                        {
                            "kind": "step",
                            "action": "check",
                            "detect": { "type": "data_exists", "field": "F_CODE" }
                        },
                        {
                            "kind": "step",
                            "action": "check",
                            "detect": { "type": "always" }
                        },
                        {
                            "kind": "step",
                            "action": "check",
                            "detect": {
                                "type": "all",
                                "all": [
                                    { "type": "selector_visible", "selector": "#a" },
                                    { "type": "selector_visible", "selector": "#b" }
                                ]
                            }
                        },
                        {
                            "kind": "step",
                            "action": "check",
                            "detect": {
                                "type": "any",
                                "any": [
                                    { "type": "selector_visible", "selector": "#x" },
                                    { "type": "selector_visible", "selector": "#y" }
                                ]
                            }
                        },
                        {
                            "kind": "step",
                            "action": "check",
                            "detect": {
                                "type": "not",
                                "not": { "type": "selector_visible", "selector": "#loading" }
                            }
                        }
                    ]
                }
            ]
        }
        """;

        var script = JsonSerializer.Deserialize<ScriptV2>(json, _options);

        var phase = Assert.IsType<PhaseNode>(script!.Phases[0]);

        // Phase condition: url_changed
        Assert.NotNull(phase.Condition);
        Assert.Equal("url_changed", phase.Condition!.Type);
        Assert.Equal("/login", phase.Condition.UrlContains);

        // Step detects
        var s0 = Assert.IsType<StepNode>(phase.Steps[0]);
        Assert.Equal("selector_visible", s0.Detect!.Type);

        var s1 = Assert.IsType<StepNode>(phase.Steps[1]);
        Assert.Equal("js", s1.Detect!.Type);

        var s2 = Assert.IsType<StepNode>(phase.Steps[2]);
        Assert.Equal("data_exists", s2.Detect!.Type);

        var s3 = Assert.IsType<StepNode>(phase.Steps[3]);
        Assert.Equal("always", s3.Detect!.Type);

        var s4 = Assert.IsType<StepNode>(phase.Steps[4]);
        Assert.Equal("all", s4.Detect!.Type);
        Assert.Equal(2, s4.Detect!.All!.Count);

        var s5 = Assert.IsType<StepNode>(phase.Steps[5]);
        Assert.Equal("any", s5.Detect!.Type);
        Assert.Equal(2, s5.Detect!.Any!.Count);

        var s6 = Assert.IsType<StepNode>(phase.Steps[6]);
        Assert.Equal("not", s6.Detect!.Type);
        Assert.NotNull(s6.Detect!.Not);
        Assert.Equal("selector_visible", s6.Detect!.Not!.Type);
    }

    [Fact]
    public void Deserialize_FieldDefinition_Nested()
    {
        var json = """
        {
            "version": 2,
            "scriptId": "s9",
            "name": "Fields Nested",
            "fields": [
                {
                    "name": "invoiceItems",
                    "label": "Invoice Items",
                    "type": "array",
                    "multiple": true,
                    "fields": [
                        {
                            "name": "itemName",
                            "label": "Item Name",
                            "type": "string",
                            "required": true
                        },
                        {
                            "name": "quantity",
                            "label": "Quantity",
                            "type": "number",
                            "min": 1,
                            "max": 9999
                        },
                        {
                            "name": "unitPrice",
                            "label": "Unit Price",
                            "type": "number",
                            "format": "#,##0.00"
                        }
                    ]
                }
            ]
        }
        """;

        var script = JsonSerializer.Deserialize<ScriptV2>(json, _options);

        Assert.Single(script!.Fields);
        var field = script.Fields[0];
        Assert.Equal("invoiceItems", field.Name);
        Assert.Equal("array", field.Type);
        Assert.True(field.Multiple);
        Assert.Equal(3, field.Fields!.Count);

        Assert.Equal("itemName", field.Fields[0].Name);
        Assert.True(field.Fields[0].Required);

        Assert.Equal("quantity", field.Fields[1].Name);
        Assert.Equal(1, field.Fields[1].Min);
        Assert.Equal(9999, field.Fields[1].Max);

        Assert.Equal("unitPrice", field.Fields[2].Name);
        Assert.Equal("#,##0.00", field.Fields[2].Format);
    }

    [Fact]
    public void Deserialize_FieldDefinition_Items()
    {
        var json = """
        {
            "version": 2,
            "scriptId": "s10",
            "name": "Field Items",
            "fields": [
                {
                    "name": "category",
                    "label": "Category",
                    "type": "string",
                    "uiComponent": "select",
                    "items": {
                        "type": "string",
                        "options": ["A", "B", "C"]
                    }
                }
            ]
        }
        """;

        var script = JsonSerializer.Deserialize<ScriptV2>(json, _options);

        Assert.Single(script!.Fields);
        var field = script.Fields[0];
        Assert.Equal("select", field.UiComponent);
        Assert.NotNull(field.Items);
        Assert.Equal("string", field.Items!.Type);
        Assert.Equal(3, field.Items!.Options!.Count);
        Assert.Equal(["A", "B", "C"], field.Items!.Options);
    }

    [Fact]
    public void Deserialize_FieldDefinition_Transform()
    {
        var json = """
        {
            "version": 2,
            "scriptId": "s11",
            "name": "Transform Test",
            "fields": [
                {
                    "name": "amount",
                    "label": "Amount",
                    "type": "number",
                    "transform": "number:#,##0.00"
                },
                {
                    "name": "date",
                    "label": "Date",
                    "type": "date",
                    "transform": "date:dd/MM/yyyy"
                },
                {
                    "name": "code",
                    "label": "Code",
                    "type": "string",
                    "transform": "upper"
                }
            ]
        }
        """;

        var script = JsonSerializer.Deserialize<ScriptV2>(json, _options);

        Assert.Equal(3, script!.Fields.Count);
        Assert.Equal("number:#,##0.00", script.Fields[0].Transform);
        Assert.Equal("date:dd/MM/yyyy", script.Fields[1].Transform);
        Assert.Equal("upper", script.Fields[2].Transform);
    }

    [Fact]
    public void Deserialize_IframeChain_StringArray()
    {
        // 形态 A：step.iframe / phase.iframe 是 selector 链 string[]（根→叶），经 JSON 反序列化产 string[]。
        var json = """
        {
            "version": 2,
            "scriptId": "s12",
            "name": "Iframe Chain Test",
            "phases": [
                {
                    "kind": "phase",
                    "name": "p1",
                    "aiGoal": "g",
                    "iframe": ["//iframe[@id='outer']", "//iframe[@name='nested']"],
                    "steps": [
                        { "kind": "step", "name": "s1", "action": "fill", "selector": "#i", "value": "x", "iframe": ["//iframe[@id='outer']"] }
                    ]
                }
            ]
        }
        """;

        var script = JsonSerializer.Deserialize<ScriptV2>(json, _options);

        var phase = (PhaseNode)script!.Phases[0];
        Assert.NotNull(phase.Iframe);
        Assert.Equal(new[] { "//iframe[@id='outer']", "//iframe[@name='nested']" }, phase.Iframe);  // 断言验值（数组内容）

        var step = (StepNode)phase.Steps[0];
        Assert.Equal(new[] { "//iframe[@id='outer']" }, step.Iframe);  // 单层链
    }

    [Fact]
    public void Deserialize_ScriptSettings_AllFields()
    {
        var json = """
        {
            "version": 2,
            "scriptId": "s13",
            "name": "Settings Test",
            "settings": {
                "defaultTimeout": 30000,
                "stepRetry": {
                    "count": 3,
                    "interval": 2000
                },
                "onError": "continue",
                "viewport": {
                    "width": 1920,
                    "height": 1080
                },
                "maxAiTurns": 10,
                "screenshot": {
                    "onPhaseProgress": true,
                    "onStepFailure": true,
                    "onTaskComplete": false,
                    "onTaskFailure": true
                },
                "maxScriptDuration": 600,
                "maxLoopCount": 100
            }
        }
        """;

        var script = JsonSerializer.Deserialize<ScriptV2>(json, _options);

        var s = script!.Settings!;
        Assert.NotNull(s);
        Assert.Equal(30000, s.DefaultTimeout);
        Assert.Equal("continue", s.OnError);
        Assert.Equal(10, s.MaxAiTurns);
        Assert.Equal(600, s.MaxScriptDuration);
        Assert.Equal(100, s.MaxLoopCount);

        Assert.NotNull(s.StepRetry);
        Assert.Equal(3, s.StepRetry!.Count);
        Assert.Equal(2000, s.StepRetry!.Interval);

        Assert.NotNull(s.Viewport);
        Assert.Equal(1920, s.Viewport!.Width);
        Assert.Equal(1080, s.Viewport!.Height);

        Assert.NotNull(s.Screenshot);
        Assert.True(s.Screenshot!.OnPhaseProgress);
        Assert.True(s.Screenshot!.OnStepFailure);
        Assert.False(s.Screenshot!.OnTaskComplete);
        Assert.True(s.Screenshot!.OnTaskFailure);
    }

    [Fact]
    public void Deserialize_StepFallback_Nested()
    {
        var json = """
        {
            "version": 2,
            "scriptId": "s14",
            "name": "Fallback Test",
            "phases": [
                {
                    "kind": "phase",
                    "name": "main",
                    "steps": [
                        {
                            "kind": "step",
                            "action": "click",
                            "selector": "#btn1",
                            "fallback": {
                                "selector": "#btn2",
                                "action": "click",
                                "fallback": {
                                    "selector": "#btn3",
                                    "action": "click",
                                    "fallback": {
                                        "code": "console.log('all failed')"
                                    }
                                }
                            }
                        }
                    ]
                }
            ]
        }
        """;

        var script = JsonSerializer.Deserialize<ScriptV2>(json, _options);

        var step = Assert.IsType<StepNode>(Assert.IsType<PhaseNode>(script!.Phases[0]).Steps[0]);
        Assert.Equal("#btn1", step.Selector);

        var fb1 = step.Fallback!;
        Assert.Equal("#btn2", fb1.Selector);
        Assert.Equal("click", fb1.Action);

        var fb2 = fb1.Fallback!;
        Assert.Equal("#btn3", fb2.Selector);

        var fb3 = fb2.Fallback!;
        Assert.Equal("console.log('all failed')", fb3.Code);
        Assert.Null(fb3.Fallback);
    }

    [Fact]
    public void Deserialize_HkSampleScript()
    {
        var json = """
        {
            "version": 2,
            "scriptId": "hk-declaration",
            "name": "HK Declaration Filing",
            "documentTypeId": "DT_HK",
            "description": "HK tax declaration script",
            "settings": {
                "defaultTimeout": 30000,
                "onError": "stop"
            },
            "fields": [
                { "name": "F_BRN", "label": "BRN", "type": "string", "required": true },
                { "name": "F_YEAR", "label": "Year of Assessment", "type": "string", "required": true },
                { "name": "F_TOTAL_INCOME", "label": "Total Income", "type": "number" }
            ],
            "phases": [
                {
                    "kind": "phase",
                    "name": "login",
                    "type": "sequential",
                    "steps": [
                        { "kind": "step", "action": "navigate", "url": "https://hk-tax.example.com/login" },
                        { "kind": "step", "action": "fill", "selector": "#username", "value": "{{F_BRN}}" },
                        { "kind": "step", "action": "fill", "selector": "#password", "value": "{{password}}" },
                        { "kind": "step", "action": "click", "selector": "#loginBtn" },
                        {
                            "kind": "step",
                            "action": "wait",
                            "until": { "type": "url_changed", "urlContains": "/dashboard" }
                        }
                    ]
                },
                {
                    "kind": "phase",
                    "name": "fill_rows",
                    "type": "loop",
                    "loopSource": "incomeRows",
                    "maxLoopCount": 50,
                    "rowIndexOffset": 0,
                    "loopCondition": {
                        "type": "selector_visible",
                        "selector": "#addRowBtn"
                    },
                    "steps": [
                        { "kind": "step", "action": "click", "selector": "#addRowBtn" },
                        { "kind": "step", "action": "fill", "selector": "#rowIncome", "value": "{{income}}" },
                        { "kind": "step", "action": "fill", "selector": "#rowDeduction", "value": "{{deduction}}" }
                    ]
                },
                {
                    "kind": "phase",
                    "name": "save",
                    "type": "sequential",
                    "steps": [
                        { "kind": "step", "action": "click", "selector": "#saveBtn" },
                        {
                            "kind": "step",
                            "action": "check",
                            "detect": { "type": "selector_visible", "selector": "#successMsg" },
                            "message": "Save successful"
                        }
                    ]
                }
            ]
        }
        """;

        var script = JsonSerializer.Deserialize<ScriptV2>(json, _options);

        Assert.NotNull(script);
        Assert.Equal("hk-declaration", script.ScriptId);
        Assert.Equal("DT_HK", script.DocumentTypeId);
        Assert.Equal(3, script.Fields.Count);
        Assert.Equal(3, script.Phases.Count);

        // Login phase
        var login = Assert.IsType<PhaseNode>(script.Phases[0]);
        Assert.Equal("login", login.Name);
        Assert.Equal("sequential", login.Type);
        Assert.Equal(5, login.Steps.Count);

        // Loop phase
        var loop = Assert.IsType<PhaseNode>(script.Phases[1]);
        Assert.Equal("fill_rows", loop.Name);
        Assert.Equal("loop", loop.Type);
        Assert.Equal("incomeRows", loop.LoopSource);
        Assert.Equal(50, loop.MaxLoopCount);
        Assert.Equal(0, loop.RowIndexOffset);
        Assert.NotNull(loop.LoopCondition);
        Assert.Equal("selector_visible", loop.LoopCondition!.Type);
        Assert.Equal(3, loop.Steps.Count);

        // Save phase
        var save = Assert.IsType<PhaseNode>(script.Phases[2]);
        Assert.Equal("save", save.Name);
        Assert.Equal(2, save.Steps.Count);

        var checkStep = Assert.IsType<StepNode>(save.Steps[1]);
        Assert.Equal("check", checkStep.Action);
        Assert.Equal("Save successful", checkStep.Message);
    }

    [Fact]
    public void Serialize_Roundtrip()
    {
        var json = """
        {
            "version": 2,
            "scriptId": "roundtrip-001",
            "name": "Roundtrip Test",
            "documentTypeId": "DT_TEST",
            "phases": [
                {
                    "kind": "phase",
                    "name": "main",
                    "type": "sequential",
                    "steps": [
                        {
                            "kind": "step",
                            "action": "navigate",
                            "url": "https://example.com"
                        },
                        {
                            "kind": "step",
                            "action": "fill",
                            "selector": "#input",
                            "value": "test value"
                        }
                    ]
                }
            ],
            "fields": [
                {
                    "name": "F_NAME",
                    "label": "Name",
                    "type": "string",
                    "required": true
                }
            ],
            "settings": {
                "defaultTimeout": 15000
            }
        }
        """;

        var script = JsonSerializer.Deserialize<ScriptV2>(json, _options);
        var roundtrippedJson = JsonSerializer.Serialize(script!, _roundtripOptions);
        var script2 = JsonSerializer.Deserialize<ScriptV2>(roundtrippedJson, _options);

        Assert.NotNull(script2);
        Assert.Equal(script!.ScriptId, script2.ScriptId);
        Assert.Equal(script.Name, script2.Name);
        Assert.Equal(script.DocumentTypeId, script2.DocumentTypeId);
        Assert.Equal(script.Fields.Count, script2.Fields.Count);
        Assert.Equal(script.Fields[0].Name, script2.Fields[0].Name);
        Assert.Equal(script.Settings!.DefaultTimeout, script2.Settings!.DefaultTimeout);

        var phase1 = Assert.IsType<PhaseNode>(script.Phases[0]);
        var phase2 = Assert.IsType<PhaseNode>(script2.Phases[0]);
        Assert.Equal(phase1.Name, phase2.Name);
        Assert.Equal(phase1.Steps.Count, phase2.Steps.Count);
    }

    [Fact]
    public void Deserialize_Step_CaptchaFields()
    {
        var json = """
        {
            "version": 2,
            "scriptId": "s15",
            "name": "Captcha Test",
            "phases": [
                {
                    "kind": "phase",
                    "name": "captcha",
                    "steps": [
                        {
                            "kind": "step",
                            "action": "captcha",
                            "captchaType": "slider",
                            "imageSelector": "#captcha-img",
                            "inputSelector": "#captcha-input",
                            "sliderSelector": "#captcha-slider",
                            "targetSelector": "#captcha-target",
                            "backgroundSelector": "#captcha-bg"
                        }
                    ]
                }
            ]
        }
        """;

        var script = JsonSerializer.Deserialize<ScriptV2>(json, _options);

        var step = Assert.IsType<StepNode>(Assert.IsType<PhaseNode>(script!.Phases[0]).Steps[0]);
        Assert.Equal("captcha", step.Action);
        Assert.Equal("slider", step.CaptchaType);
        Assert.Equal("#captcha-img", step.ImageSelector);
        Assert.Equal("#captcha-input", step.InputSelector);
        Assert.Equal("#captcha-slider", step.SliderSelector);
        Assert.Equal("#captcha-target", step.TargetSelector);
        Assert.Equal("#captcha-bg", step.BackgroundSelector);
    }

    [Fact]
    public void Deserialize_LoopPhase_AllFields()
    {
        var json = """
        {
            "version": 2,
            "scriptId": "s16",
            "name": "Loop Phase Test",
            "phases": [
                {
                    "kind": "phase",
                    "name": "loopRows",
                    "type": "loop",
                    "loopSource": "dataRows",
                    "maxLoopCount": 100,
                    "rowIndexOffset": 1,
                    "loopCondition": {
                        "type": "selector_visible",
                        "selector": "#hasMoreRows"
                    },
                    "onError": "continue",
                    "iframe": ["//iframe[@id='content-frame']"],
                    "timeout": 5000,
                    "steps": [
                        { "kind": "step", "action": "click", "selector": "#addItem" },
                        { "kind": "step", "action": "fill", "selector": "#desc", "value": "{{description}}" }
                    ]
                }
            ]
        }
        """;

        var script = JsonSerializer.Deserialize<ScriptV2>(json, _options);

        var phase = Assert.IsType<PhaseNode>(script!.Phases[0]);
        Assert.Equal("loopRows", phase.Name);
        Assert.Equal("loop", phase.Type);
        Assert.Equal("dataRows", phase.LoopSource);
        Assert.Equal(100, phase.MaxLoopCount);
        Assert.Equal(1, phase.RowIndexOffset);
        Assert.NotNull(phase.LoopCondition);
        Assert.Equal("selector_visible", phase.LoopCondition!.Type);
        Assert.Equal("#hasMoreRows", phase.LoopCondition!.Selector);
        Assert.Equal("continue", phase.OnError);
        Assert.Equal(new[] { "//iframe[@id='content-frame']" }, phase.Iframe);
        Assert.Equal(5000, phase.Timeout);
        Assert.Equal(2, phase.Steps.Count);
    }

    [Fact]
    public void Serialize_RefAndIframeRef_Null_NotWritten_EvenWhenGlobalNever()
    {
        // 生产 RecordOutputAllFields=true -> DefaultIgnoreCondition=Never（全输出含 null）。
        // _roundtripOptions 未设 DefaultIgnoreCondition（STJ 默认 Never），模拟生产全局行为。
        // 字段级 [JsonIgnore(WhenWritingNull)] 应优先于全局 Never：ref/iframeRef 的 null 不写出，其他 null 字段照常写出。
        var step = JsonSerializer.Deserialize<StepNode>("""{"kind":"step","action":"click","selector":"#btn"}""", _options)!;
        var json = JsonSerializer.Serialize(step, _roundtripOptions);

        // 录制期临时字段 null 不落盘（字段级 WhenWritingNull 优先于全局 Never）
        Assert.DoesNotContain("\"iframeRef\":", json);
        // 其他 null 字段仍输出（证明只对 ref/iframeRef 生效，非全局 WhenWritingNull）
        Assert.Contains("\"value\":null", json);
        Assert.Contains("\"selector\":\"#btn\"", json);

        // detect 同款（Ref + IframeRef 都不写出）
        var detect = JsonSerializer.Deserialize<DetectCondition>("""{"type":"selector_exists","selector":"#x"}""", _options)!;
        var detectJson = JsonSerializer.Serialize(detect, _roundtripOptions);
        Assert.DoesNotContain("\"ref\":", detectJson);
        Assert.DoesNotContain("\"iframeRef\":", detectJson);
        Assert.Contains("\"selector\":\"#x\"", detectJson);

        // fallback 同款
        var fb = JsonSerializer.Deserialize<StepFallback>("""{"selector":"#y"}""", _options)!;
        var fbJson = JsonSerializer.Serialize(fb, _roundtripOptions);
        Assert.DoesNotContain("\"ref\":", fbJson);
        Assert.DoesNotContain("\"iframeRef\":", fbJson);
        Assert.Contains("\"selector\":\"#y\"", fbJson);
    }
}
