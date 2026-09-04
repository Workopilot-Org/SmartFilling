using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using NSubstitute;
using SmartFilling.App.Configuration;
using SmartFilling.App.Recording;
using SmartFilling.App.Services;
using SmartFilling.Engine.Ai;
using SmartFilling.Engine.Engine;
using SmartFilling.Engine.Models;
using System.Reflection;
using System.Text.Json;
using EngineILogger = SmartFilling.Engine.Logging.ILogger;
using OpenAI.Chat;

namespace SmartFilling.App.Tests;

/// <summary>
/// loopSource 未注册时自动注册（方案A：ensure_phase 加 phaseFieldType 四选一 table/items/file/subform）测试。
/// 替代方案B（phaseFieldFields）——AI 无 fillData 难一次性猜全子列，改 table bare array 循环内 operate 增量注册子列。
/// 测试方法论（test-covers-production-deserialize-path）：phaseFieldItems 用 JsonElement 形态构造
/// （JsonDocument.Parse + Clone，模拟 AI 工具参数经 OpenAiProvider.ParseJsonElement 归一化的生产反序列化路径），
/// 不用 CLR new{} 绕过 `is JsonElement` 类型判断分支。断言验值不只验状态（Fields.Count/子列 Name/Type/uiComponent/Items.Type/Multiple 具体值）。
/// T5（问用户兜底）：mock OnRequestHelp 回复 a/b/c/d（照 P15，TestPendingHelpTcs 读 ExecuteRequestHelpAsync 新建 tcs TrySetResult）。
/// T10（operate 跳过 loopSource）：直测 IsLoopSourceField helper（提 internal，照 ParseSubFields 模式）——loopSource 字段 true / 普通 array 字段 false（不误伤）。
/// </summary>
public class EnsurePhaseAutoRegisterTests
{
    private static ScriptService CreateScriptService()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SmartFillingTest_" + Guid.NewGuid().ToString("N")[..8]);
        var mockEnv = Substitute.For<IWebHostEnvironment>();
        mockEnv.ContentRootPath.Returns(tempDir);
        var mockLogger = Substitute.For<ILogger<ScriptService>>();
        return new ScriptService(mockEnv, Options.Create(new AppOptions()), mockLogger);
    }

    private static RecordingEngine CreateEngine(IPage page)
    {
        var mockAi = Substitute.For<IAiProvider>();
        var mockLogger = Substitute.For<EngineILogger>();
        var engine = new RecordingEngine(mockAi, CreateScriptService(), mockLogger, new EngineOptions(), new AppOptions());
        engine.TestInjectedPage = page;
        return engine;
    }

    /// <summary>主文档 mock page（loopSource 自动注册路径不访问 page，但保险设一个避免意外 NRE；T5 问用户时 CompressScreenshotAsync 会调 ScreenshotAsync，NSubstitute loose 返 default 被 catch 兜底）。</summary>
    private static IPage CreateMainDocPage()
    {
        var mainFrameMock = Substitute.For<IFrame>();
        var browserMock = Substitute.For<IBrowser>();
        browserMock.IsConnected.Returns(true);
        var ctxMock = Substitute.For<IBrowserContext>();
        ctxMock.Browser.Returns(browserMock);
        var pageMock = Substitute.For<IPage>();
        pageMock.MainFrame.Returns(mainFrameMock);
        pageMock.Context.Returns(ctxMock);
        pageMock.Frames.Returns(new List<IFrame> { mainFrameMock });
        var bodyLocMock = Substitute.For<ILocator>();
        bodyLocMock.CountAsync().Returns(1);
        pageMock.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions?>()).Returns(bodyLocMock);
        mainFrameMock.Locator(Arg.Any<string>(), Arg.Any<FrameLocatorOptions?>()).Returns(bodyLocMock);
        return pageMock;
    }

    /// <summary>构造 JsonElement（Clone 产出独立实例，不依赖 JsonDocument 生命周期），模拟生产 ParseJsonElement 归一化形态。</summary>
    private static JsonElement JsonEl(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // T1：顶层 loop + phaseFieldType=table -> items 自动注册为 bare array（Fields=[]，子列循环内 operate 增量注册）（验值：type/array/table/label/Fields 空）
    [Fact]
    public async Task T1_TopLevelLoop_PhaseFieldTypeTable_AutoRegistersBareArray()
    {
        var engine = CreateEngine(CreateMainDocPage());
        // InferLoopSourceIntent(null)→"ambiguous" 触发 loopSource 意图 help（L2645），需 OnRequestHelp mock 回复避免 TCS 永久等待
        engine.OnRequestHelp += (a, q, s) => { engine.TestPendingHelpTcs?.TrySetResult("a"); return Task.CompletedTask; };
        var args = new Dictionary<string, object>
        {
            ["phase"] = "fill_items",
            ["phaseType"] = "loop",
            ["phaseLoopSource"] = "items",
            ["phaseFieldType"] = "table",
            ["phaseFieldLabel"] = "明细表"
        };

        var (err, _) = await engine.EnsurePhaseAsync(args, CancellationToken.None);

        Assert.Null(err);  // 自动注册成功
        var items = engine.Script.Fields.FirstOrDefault(f => f.Name == "items");
        Assert.NotNull(items);
        Assert.Equal("array", items!.Type);
        Assert.Equal("table", items.UiComponent);
        Assert.Equal("明细表", items.Label);
        Assert.NotNull(items.Fields);
        Assert.Empty(items.Fields!);  // bare array：Fields=[]（子列循环内 operate 增量注册，不在此一次性传全）
    }

    // T2：嵌套 loop + phaseFieldType=table + parentPhase -> attachments 嵌套进 items.Fields（验值：层级 + bare array）
    [Fact]
    public async Task T2_NestedLoop_PhaseFieldTypeTable_RegistersInParentFields()
    {
        var engine = CreateEngine(CreateMainDocPage());
        // InferLoopSourceIntent(null)→"ambiguous" 触发 loopSource 意图 help（两次 EnsurePhaseAsync 各一次），需 OnRequestHelp mock 回复
        engine.OnRequestHelp += (a, q, s) => { engine.TestPendingHelpTcs?.TrySetResult("a"); return Task.CompletedTask; };
        var args1 = new Dictionary<string, object>
        {
            ["phase"] = "fill_items",
            ["phaseType"] = "loop",
            ["phaseLoopSource"] = "items",
            ["phaseFieldType"] = "table",
            ["phaseFieldLabel"] = "明细表"
        };
        var (err1, _) = await engine.EnsurePhaseAsync(args1, CancellationToken.None);
        Assert.Null(err1);

        var args2 = new Dictionary<string, object>
        {
            ["phase"] = "fill_attachments",
            ["phaseType"] = "loop",
            ["parentPhase"] = "fill_items",
            ["phaseLoopSource"] = "attachments",
            ["phaseFieldType"] = "table",
            ["phaseFieldLabel"] = "附件"
        };
        var (err2, _) = await engine.EnsurePhaseAsync(args2, CancellationToken.None);
        Assert.Null(err2);

        var items = engine.Script.Fields.FirstOrDefault(f => f.Name == "items");
        Assert.NotNull(items);
        var attachments = items!.Fields!.FirstOrDefault(f => f.Name == "attachments");
        Assert.NotNull(attachments);
        Assert.Equal("array", attachments!.Type);
        Assert.Equal("table", attachments.UiComponent);
        Assert.Equal("附件", attachments.Label);
        Assert.Empty(attachments.Fields!);  // bare array
        Assert.DoesNotContain(engine.Script.Fields, f => f.Name == "attachments");  // 嵌套在 items.Fields 非顶层
    }

    // T3：简单数组 + phaseFieldType=items + phaseFieldItems -> 自动注册 type=array + Items（验值：uiComponent==null + Fields=[] + Items.Type）
    [Fact]
    public async Task T3_SimpleArray_PhaseFieldTypeItems_AutoRegisters()
    {
        var engine = CreateEngine(CreateMainDocPage());
        // InferLoopSourceIntent(null)→"ambiguous" 触发 loopSource 意图 help（L2645），需 OnRequestHelp mock 回复
        engine.OnRequestHelp += (a, q, s) => { engine.TestPendingHelpTcs?.TrySetResult("a"); return Task.CompletedTask; };
        var args = new Dictionary<string, object>
        {
            ["phase"] = "fill_tags",
            ["phaseType"] = "loop",
            ["phaseLoopSource"] = "tags",
            ["phaseFieldType"] = "items",
            ["phaseFieldItems"] = JsonEl("""{"type":"string","options":["A","B"]}""")
        };

        var (err, _) = await engine.EnsurePhaseAsync(args, CancellationToken.None);

        Assert.Null(err);
        var tags = engine.Script.Fields.FirstOrDefault(f => f.Name == "tags");
        Assert.NotNull(tags);
        Assert.Equal("array", tags!.Type);
        Assert.Null(tags.UiComponent);  // 简单数组非 table
        Assert.NotNull(tags.Fields);
        Assert.Empty(tags.Fields);  // items 路径 Fields=[]
        Assert.NotNull(tags.Items);
        Assert.Equal("string", tags.Items!.Type);
        Assert.Equal(new[] { "A", "B" }, tags.Items.Options!);
    }

    // T4：纯附件 + phaseFieldType=file -> 自动注册 type=file + uiComponent=upload + multiple=true（验值：type/file/upload/multiple）
    [Fact]
    public async Task T4_FileUpload_PhaseFieldTypeFile_AutoRegisters()
    {
        var engine = CreateEngine(CreateMainDocPage());
        // InferLoopSourceIntent(null)→"ambiguous" 触发 loopSource 意图 help（L2645），需 OnRequestHelp mock 回复
        engine.OnRequestHelp += (a, q, s) => { engine.TestPendingHelpTcs?.TrySetResult("a"); return Task.CompletedTask; };
        var args = new Dictionary<string, object>
        {
            ["phase"] = "fill_attachments",
            ["phaseType"] = "loop",
            ["phaseLoopSource"] = "attachments",
            ["phaseFieldType"] = "file",
            ["phaseFieldLabel"] = "附件"
        };

        var (err, _) = await engine.EnsurePhaseAsync(args, CancellationToken.None);

        Assert.Null(err);
        var att = engine.Script.Fields.FirstOrDefault(f => f.Name == "attachments");
        Assert.NotNull(att);
        Assert.Equal("file", att!.Type);
        Assert.Equal("upload", att.UiComponent);
        Assert.True(att.Multiple);
        Assert.Null(att.Fields);  // file 无子字段
        Assert.Null(att.Items);
    }

    // T5a：不传 phaseFieldType -> 问用户 a/b/c/d -> mock 回复 a -> 兜底 table（验值：type/array/uiComponent=table/Fields 空）
    [Fact]
    public async Task T5a_NoPhaseFieldType_AskUser_ReplyA_RegistersTable()
    {
        var engine = CreateEngine(CreateMainDocPage());
        engine.OnRequestHelp += (a, q, s) => { engine.TestPendingHelpTcs?.TrySetResult("a"); return Task.CompletedTask; };
        var args = new Dictionary<string, object>
        {
            ["phase"] = "fill_items",
            ["phaseType"] = "loop",
            ["phaseLoopSource"] = "items"
            // 不传 phaseFieldType -> 问 a/b/c/d -> mock 回复 a -> table 兜底
        };

        var (err, _) = await engine.EnsurePhaseAsync(args, CancellationToken.None);

        Assert.Null(err);
        var items = engine.Script.Fields.FirstOrDefault(f => f.Name == "items");
        Assert.NotNull(items);
        Assert.Equal("array", items!.Type);
        Assert.Equal("table", items.UiComponent);  // 兜底 table
        Assert.Empty(items.Fields!);  // bare array
    }

    // T5b：不传 phaseFieldType -> mock 回复 b -> items（需配 phaseFieldItems）（验值：type/array/uiComponent=null/Items.Type）
    [Fact]
    public async Task T5b_NoPhaseFieldType_AskUser_ReplyB_RegistersItems()
    {
        var engine = CreateEngine(CreateMainDocPage());
        engine.OnRequestHelp += (a, q, s) => {
            // 第一次 help（loopSource 意图）问"新建字段 vs 新建变量"→ 回复 "a"（新建字段，shouldRegisterField=true）
            // 第二次 help（phaseFieldType）问"哪种类型"→ 回复 "b"（items）
            var reply = q.Contains("哪种类型") ? "b" : "a";
            engine.TestPendingHelpTcs?.TrySetResult(reply);
            return Task.CompletedTask;
        };
        var args = new Dictionary<string, object>
        {
            ["phase"] = "fill_tags",
            ["phaseType"] = "loop",
            ["phaseLoopSource"] = "tags",
            ["phaseFieldItems"] = JsonEl("""{"type":"string"}""")
        };

        var (err, _) = await engine.EnsurePhaseAsync(args, CancellationToken.None);

        Assert.Null(err);
        var tags = engine.Script.Fields.FirstOrDefault(f => f.Name == "tags");
        Assert.NotNull(tags);
        Assert.Equal("array", tags!.Type);
        Assert.Null(tags.UiComponent);  // items 非 table
        Assert.Equal("string", tags.Items!.Type);
    }

    // T5c：不传 phaseFieldType -> mock 回复 c -> file（验值：type/file/upload/multiple）
    [Fact]
    public async Task T5c_NoPhaseFieldType_AskUser_ReplyC_RegistersFile()
    {
        var engine = CreateEngine(CreateMainDocPage());
        engine.OnRequestHelp += (a, q, s) => {
            // 第一次 help（loopSource 意图）问"新建字段 vs 新建变量"→ 回复 "a"（新建字段，shouldRegisterField=true）
            // 第二次 help（phaseFieldType）问"哪种类型"→ 回复 "c"（file）
            var reply = q.Contains("哪种类型") ? "c" : "a";
            engine.TestPendingHelpTcs?.TrySetResult(reply);
            return Task.CompletedTask;
        };
        var args = new Dictionary<string, object>
        {
            ["phase"] = "fill_attachments",
            ["phaseType"] = "loop",
            ["phaseLoopSource"] = "attachments"
        };

        var (err, _) = await engine.EnsurePhaseAsync(args, CancellationToken.None);

        Assert.Null(err);
        var att = engine.Script.Fields.FirstOrDefault(f => f.Name == "attachments");
        Assert.NotNull(att);
        Assert.Equal("file", att!.Type);
        Assert.Equal("upload", att.UiComponent);
        Assert.True(att.Multiple);
    }

    // T6：parentPhase 不存在 -> P4 前移报错 return，不注册 loopSource（验值：消息含"父 phase"+父名 + 未新增字段无残留）
    [Fact]
    public async Task T6_ParentPhaseNotFound_Errors_NoLoopSourceRegistered()
    {
        var engine = CreateEngine(CreateMainDocPage());
        var args = new Dictionary<string, object>
        {
            ["phase"] = "fill_attachments",
            ["phaseType"] = "loop",
            ["parentPhase"] = "fill_items_notexist",  // 父 phase 不存在
            ["phaseLoopSource"] = "attachments",
            ["phaseFieldType"] = "file"
        };

        var (err, _) = await engine.EnsurePhaseAsync(args, CancellationToken.None);

        Assert.NotNull(err);
        Assert.Contains("父 phase", err!);
        Assert.Contains("fill_items_notexist", err!);
        Assert.DoesNotContain(engine.Script.Fields, f => f.Name == "attachments");  // P4 前移：不注册 loopSource（无残留字段）
    }

    // T7：子列解析一致性（phaseFieldFields 已删，但 operate fieldFields 仍用 ParseSubFields）-> ParseSubFields 同款 JsonElement.Array 产出相等（纯函数，保留）
    [Fact]
    public void T7_ParseSubFields_Consistency_SameJsonProducesEqualList()
    {
        var sameJson = """[{"name":"materialName","label":"材料名称","type":"string","uiComponent":"click-choose","required":true},{"name":"unit","label":"单位","type":"string","options":["个","吨"]}]""";
        var el1 = JsonEl(sameJson);
        var el2 = JsonEl(sameJson);  // 同款 JSON 不同 JsonElement 实例

        var list1 = RecordingEngine.ParseSubFields(el1);
        var list2 = RecordingEngine.ParseSubFields(el2);

        // 逐字段比较
        Assert.Equal(list1.Count, list2.Count);
        for (int i = 0; i < list1.Count; i++)
        {
            Assert.Equal(list1[i].Name, list2[i].Name);
            Assert.Equal(list1[i].Label, list2[i].Label);
            Assert.Equal(list1[i].Type, list2[i].Type);
            Assert.Equal(list1[i].UiComponent, list2[i].UiComponent);
            Assert.Equal(list1[i].Required, list2[i].Required);
            Assert.Equal(list1[i].Options, list2[i].Options);
        }
        // 序列化 JSON 比较（规避 List<FieldDefinition> record 引用比较默认 EqualityComparer 假失败）
        Assert.Equal(JsonSerializer.Serialize(list1), JsonSerializer.Serialize(list2));
    }

    // T8：回归 operate fieldFields -- ParseSubFields 对所有子列字段属性（name/label/type/uiComponent/required/source/description/format/options/placeholder/pattern）仍正确解析（纯函数，保留）
    [Fact]
    public void T8_ParseSubFields_OperateFieldFields_AllPropertiesParsed()
    {
        var json = """[{"name":"F1","label":"字段1","type":"number","uiComponent":"input","required":true,"source":"user","description":"描述","format":"#,##0.00","options":["A"],"placeholder":"提示","pattern":"^\\d+$"}]""";
        var list = RecordingEngine.ParseSubFields(JsonEl(json));

        Assert.Single(list);
        var sf = list[0];
        Assert.Equal("F1", sf.Name);
        Assert.Equal("字段1", sf.Label);
        Assert.Equal("number", sf.Type);
        Assert.Equal("input", sf.UiComponent);
        Assert.True(sf.Required);
        Assert.Equal("user", sf.Source);
        Assert.Equal("描述", sf.Description);
        Assert.Equal("#,##0.00", sf.Format);
        Assert.Equal(new[] { "A" }, sf.Options);
        Assert.Equal("提示", sf.Placeholder);
        Assert.Equal("^\\d+$", sf.Pattern);
    }

    // T9：BuildPhaseStatus 递归含嵌套字段名 -- 顶层 items（table bare，手动加子列模拟 operate 增量）+ 嵌套 attachments（同款），registeredFields 含全部 6 个名（验 SelectMany 递归）
    [Fact]
    public async Task T9_BuildPhaseStatus_RecursiveIncludesNestedFieldNames()
    {
        var engine = CreateEngine(CreateMainDocPage());
        // InferLoopSourceIntent(null)→"ambiguous" 触发 loopSource 意图 help（两次 EnsurePhaseAsync 各一次），需 OnRequestHelp mock 回复避免 TCS 永久等待
        engine.OnRequestHelp += (a, q, s) => { engine.TestPendingHelpTcs?.TrySetResult("a"); return Task.CompletedTask; };
        await engine.EnsurePhaseAsync(new Dictionary<string, object>
        {
            ["phase"] = "fill_items", ["phaseType"] = "loop", ["phaseLoopSource"] = "items",
            ["phaseFieldType"] = "table", ["phaseFieldLabel"] = "明细表"
        }, CancellationToken.None);
        var items = engine.Script.Fields.First(f => f.Name == "items");
        // 模拟循环内 operate 增量注册子列（operate 既有 GetLoopFieldPath/EnsureFieldPath 导航 items.Fields Add；此处手动加验 BuildPhaseStatus 递归收集）
        items.Fields!.Add(new FieldDefinition { Name = "materialName", Label = "材料名称", Type = "string" });
        items.Fields.Add(new FieldDefinition { Name = "unit", Label = "单位", Type = "string" });

        await engine.EnsurePhaseAsync(new Dictionary<string, object>
        {
            ["phase"] = "fill_attachments", ["phaseType"] = "loop", ["parentPhase"] = "fill_items",
            ["phaseLoopSource"] = "attachments", ["phaseFieldType"] = "table", ["phaseFieldLabel"] = "附件"
        }, CancellationToken.None);
        var attachments = items.Fields.First(f => f.Name == "attachments");
        attachments.Fields!.Add(new FieldDefinition { Name = "name", Label = "文件名", Type = "string" });
        attachments.Fields.Add(new FieldDefinition { Name = "url", Label = "链接", Type = "string" });

        var statusJson = engine.BuildPhaseStatus();
        var root = JsonDocument.Parse(statusJson).RootElement;
        var registeredFields = root.GetProperty("registeredFields").EnumerateArray().Select(e => e.GetString()!).ToList();

        // 验值不只验存在：含全部 6 个字段名 + 总数不多不少（改动前 Select 只产 ["items"]，改动后 SelectMany 产全 6 个）
        Assert.Contains("items", registeredFields);
        Assert.Contains("materialName", registeredFields);
        Assert.Contains("unit", registeredFields);
        Assert.Contains("attachments", registeredFields);
        Assert.Contains("name", registeredFields);
        Assert.Contains("url", registeredFields);
        Assert.Equal(6, registeredFields.Count);
    }

    // T10：IsLoopSourceField 精确判断（P13）-- loopSource 字段 true（顶层+嵌套），普通字段名 false（不误伤，operate 仍问 a/b/c/d）。直测 helper（提 internal，照 ParseSubFields 模式）。
    [Fact]
    public void T10_IsLoopSourceField_LoopSourceTrue_NonLoopSourceFalse()
    {
        var phases = new List<PhaseNode>
        {
            new() { Kind = "phase", Name = "fill_items", Type = "loop", LoopSource = "items",
                    Steps = new List<PhaseItem> { new PhaseNode { Kind = "phase", Name = "fill_attachments", Type = "loop", LoopSource = "attachments" } } }
        };
        Assert.True(RecordingEngine.IsLoopSourceField(phases, "items"));        // 顶层 loopSource
        Assert.True(RecordingEngine.IsLoopSourceField(phases, "attachments")); // 嵌套 loopSource
        Assert.False(RecordingEngine.IsLoopSourceField(phases, "details"));   // 非 loopSource（普通 array 字段）-> false（不误伤，operate 仍问 a/b/c/d 保留覆盖/换名）
        Assert.False(RecordingEngine.IsLoopSourceField(phases, "nonExist"));  // 不存在
    }

    // T11：table bare array 初始 Fields=[] + 可增量加子列 + 已注册忽略不覆盖（验：bare 空 -> 手动增量 -> 再 ensure_phase 已注册忽略 Fields 不被覆盖 + 告警）
    [Fact]
    public async Task T11_TableBareArray_FieldsEmpty_ThenIncrementalAdd_AllowedAndAlreadyRegisteredIgnores()
    {
        var engine = CreateEngine(CreateMainDocPage());
        // 第一次 EnsurePhaseAsync 的 items 未注册 → InferLoopSourceIntent(null)→"ambiguous" 触发 loopSource 意图 help，需 OnRequestHelp mock 回复（第二次 items 已注册走"已注册忽略"不触发 help）
        engine.OnRequestHelp += (a, q, s) => { engine.TestPendingHelpTcs?.TrySetResult("a"); return Task.CompletedTask; };
        await engine.EnsurePhaseAsync(new Dictionary<string, object>
        {
            ["phase"] = "fill_items", ["phaseType"] = "loop", ["phaseLoopSource"] = "items",
            ["phaseFieldType"] = "table", ["phaseFieldLabel"] = "明细表"
        }, CancellationToken.None);
        var items = engine.Script.Fields.First(f => f.Name == "items");
        Assert.Equal("array", items.Type);
        Assert.Equal("table", items.UiComponent);
        Assert.Empty(items.Fields!);  // bare array 初始 Fields=[]

        // 模拟循环内 operate 增量注册子列（operate 既有逻辑：GetLoopFieldPath/EnsureFieldPath 导航 items.Fields Add）
        items.Fields!.Add(new FieldDefinition { Name = "materialName", Label = "材料名称", Type = "string" });
        Assert.Single(items.Fields);
        Assert.Equal("materialName", items.Fields[0].Name);

        // 已注册 + 再 ensure_phase 同 loopSource 传 phaseFieldType -> 忽略 + 告警（用既有定义，Fields 不被覆盖）
        var logged = new List<string>();
        engine.OnLog += (a, msg) => { logged.Add(msg); return Task.CompletedTask; };
        await engine.EnsurePhaseAsync(new Dictionary<string, object>
        {
            ["phase"] = "fill_items2", ["phaseType"] = "loop", ["phaseLoopSource"] = "items",
            ["phaseFieldType"] = "table", ["phaseFieldLabel"] = "新标签"
        }, CancellationToken.None);
        Assert.Single(items.Fields);  // 未被覆盖（已注册忽略）
        Assert.Equal("materialName", items.Fields[0].Name);
        Assert.Contains(logged, m => m.Contains("已注册") && m.Contains("忽略"));
    }

    // T12：phaseFieldType=items 缺 phaseFieldItems -> 报错（验值：消息含 phaseFieldItems + 未新增字段）
    [Fact]
    public async Task T12_ItemsType_MissingPhaseFieldItems_Errors()
    {
        var engine = CreateEngine(CreateMainDocPage());
        // phaseLoopSource="tags" 未注册 → InferLoopSourceIntent(null)→"ambiguous" 触发 loopSource 意图 help，需 OnRequestHelp mock 回复"a"（新建字段）后才走到 phaseFieldType=items 缺 phaseFieldItems 报错分支
        engine.OnRequestHelp += (a, q, s) => { engine.TestPendingHelpTcs?.TrySetResult("a"); return Task.CompletedTask; };
        var args = new Dictionary<string, object>
        {
            ["phase"] = "fill_tags",
            ["phaseType"] = "loop",
            ["phaseLoopSource"] = "tags",
            ["phaseFieldType"] = "items"
            // 不传 phaseFieldItems -> items 分支报错（P3 items 分支 if(!TryGetValue("phaseFieldItems"...)) return error）
        };

        var (err, _) = await engine.EnsurePhaseAsync(args, CancellationToken.None);

        Assert.NotNull(err);
        Assert.Contains("phaseFieldItems", err!);
        Assert.DoesNotContain(engine.Script.Fields, f => f.Name == "tags");  // 录制期即拒，不落字段
    }

    // T13：phaseFieldType=subform -> 自动注册为 bare array + UiComponent=subform（Form 型明细表，数据结构同 table）（验值：type/array/subform/label/Fields 空）
    [Fact]
    public async Task T13_TopLevelLoop_PhaseFieldTypeSubform_AutoRegistersBareArrayWithSubformUi()
    {
        var engine = CreateEngine(CreateMainDocPage());
        // InferLoopSourceIntent(null)→"ambiguous" 触发 loopSource 意图 help（L2645），需 OnRequestHelp mock 回复
        engine.OnRequestHelp += (a, q, s) => { engine.TestPendingHelpTcs?.TrySetResult("a"); return Task.CompletedTask; };
        var args = new Dictionary<string, object>
        {
            ["phase"] = "fill_rows",
            ["phaseType"] = "loop",
            ["phaseLoopSource"] = "rows",
            ["phaseFieldType"] = "subform",
            ["phaseFieldLabel"] = "明细行"
        };

        var (err, _) = await engine.EnsurePhaseAsync(args, CancellationToken.None);

        Assert.Null(err);  // 自动注册成功
        var rows = engine.Script.Fields.FirstOrDefault(f => f.Name == "rows");
        Assert.NotNull(rows);
        Assert.Equal("array", rows!.Type);
        Assert.Equal("subform", rows.UiComponent);  // subform（Form 型），非 table
        Assert.Equal("明细行", rows.Label);
        Assert.NotNull(rows.Fields);
        Assert.Empty(rows.Fields!);  // bare array：Fields=[]（子列循环内 operate 增量注册，同 table）
    }

    // T14：operate + save_step 工具 phaseFieldType enum 含 subform（反射验证工具 schema，防 enum 漏加 subform 的 silent 不可达回归）
    [Theory]
    [InlineData("operate")]
    [InlineData("save_step")]
    public void ToolSchema_PhaseFieldTypeEnum_ContainsSubform(string toolName)
    {
        var engine = CreateEngine(CreateMainDocPage());
        var method = typeof(RecordingEngine).GetMethod("GetRecordingTools",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var tools = (List<ChatTool>)method!.Invoke(engine, null)!;
        var tool = tools.First(t => t.FunctionName == toolName);
        Assert.NotNull(tool);

        // ChatTool 把 schema 存为 BinaryData — 反射取 BinaryData 类型的属性（照 OperateSchema_ContainsValueFromField 先例）
        var bdProp = typeof(ChatTool).GetProperties()
            .First(p => p.PropertyType == typeof(BinaryData));
        var schemaBd = (BinaryData)bdProp.GetValue(tool)!;
        var schemaJson = schemaBd.ToString();
        var schema = JsonDocument.Parse(schemaJson);
        Assert.True(schema.RootElement.GetProperty("properties").TryGetProperty("phaseFieldType", out var phaseFieldType),
            $"{toolName} 工具 schema 应含 phaseFieldType 字段");
        var enumValues = phaseFieldType.GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Contains("subform", enumValues);  // 验值：enum 数组含 subform（防漏加 silent 不可达）
    }
}
