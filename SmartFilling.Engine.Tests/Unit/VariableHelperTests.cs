using SmartFilling.Engine.Engine;
using System.Text.Json;

namespace SmartFilling.Engine.Tests.Unit;

public class VariableHelperTests
{
    #region NormalizeJsonElement

    [Fact]
    public void NormalizeJsonElement_AllValueKinds()
    {
        // loopSource Q4 + 改动① 依赖：NormalizeJsonElement 7 分支逐类型正确（生产反序列化路径）。
        using var doc = JsonDocument.Parse("""{"s":"text","i":42,"d":3.5,"t":true,"f":false,"n":null,"o":{"k":"v"},"a":[1,2]}""");
        var root = (Dictionary<string, object>)VariableHelper.NormalizeJsonElement(doc.RootElement);

        Assert.Equal("text", root["s"]);
        Assert.Equal(42L, root["i"]);              // 整数 → Int64
        Assert.Equal(3.5, root["d"]);              // 小数 → double
        Assert.Equal(true, root["t"]);
        Assert.Equal(false, root["f"]);
        Assert.Equal("", root["n"]);               // null → ""（NormalizeJsonElement 约定）
        var obj = Assert.IsType<Dictionary<string, object>>(root["o"]);
        Assert.Equal("v", obj["k"]);
        var arr = Assert.IsType<List<object>>(root["a"]);
        Assert.Equal(new object[] { 1L, 2L }, arr);
    }

    #endregion

    #region ReplaceVars

    [Fact]
    public void ReplaceVars_SingleVar_InScopeChain()
    {
        var scopeChain = new List<Dictionary<string, object>>
        {
            new() { { "name", "World" } }
        };
        var vars = new Dictionary<string, object>();

        var result = VariableHelper.ReplaceVars("Hello {{name}}", scopeChain, vars);

        Assert.Equal("Hello World", result);
    }

    [Fact]
    public void ReplaceVars_MultipleVars_InDifferentScopes()
    {
        var scopeChain = new List<Dictionary<string, object>>
        {
            new() { { "a", "1" }, { "b", "2" } },
            new() { { "c", "3" } }
        };
        var vars = new Dictionary<string, object>();

        var result = VariableHelper.ReplaceVars("{{a}}-{{b}}-{{c}}", scopeChain, vars);

        Assert.Equal("1-2-3", result);
    }

    [Fact]
    public void ReplaceVars_InnerScopeOverridesOuter()
    {
        var scopeChain = new List<Dictionary<string, object>>
        {
            new() { { "x", "inner" } },
            new() { { "x", "outer" } }
        };
        var vars = new Dictionary<string, object>();

        var result = VariableHelper.ReplaceVars("{{x}}", scopeChain, vars);

        Assert.Equal("inner", result);
    }

    [Fact]
    public void ReplaceVars_FallsThroughToVars()
    {
        var scopeChain = new List<Dictionary<string, object>>();
        var vars = new Dictionary<string, object> { { "x", "from_vars" } };

        var result = VariableHelper.ReplaceVars("{{x}}", scopeChain, vars);

        Assert.Equal("from_vars", result);
    }

    [Fact]
    public void ReplaceVars_UnresolvedKeepsOriginal()
    {
        var scopeChain = new List<Dictionary<string, object>>();
        var vars = new Dictionary<string, object>();

        var result = VariableHelper.ReplaceVars("{{unknown}}", scopeChain, vars);

        Assert.Equal("{{unknown}}", result);
    }

    [Fact]
    public void ReplaceVars_NullValue_ReplacesWithEmpty()
    {
        var scopeChain = new List<Dictionary<string, object>>
        {
            new() { { "x", null! } }
        };
        var vars = new Dictionary<string, object>();

        var result = VariableHelper.ReplaceVars("val={{x}}", scopeChain, vars);

        Assert.Equal("val=", result);
    }

    [Fact]
    public void ReplaceVars_EmptyTemplate_ReturnsEmpty()
    {
        var result = VariableHelper.ReplaceVars("", [], new());

        Assert.Equal("", result);
    }

    [Fact]
    public void ReplaceVars_NullTemplate_ReturnsNull()
    {
        var result = VariableHelper.ReplaceVars(null!, [], new());

        Assert.Null(result);
    }

    [Fact]
    public void ReplaceVars_NoPlaceholders_ReturnsOriginal()
    {
        var result = VariableHelper.ReplaceVars("plain text", [], new());

        Assert.Equal("plain text", result);
    }

    [Fact]
    public void ReplaceVars_ScopeChainPrecedenceOverVars()
    {
        var scopeChain = new List<Dictionary<string, object>>
        {
            new() { { "x", "scope" } }
        };
        var vars = new Dictionary<string, object> { { "x", "var" } };

        var result = VariableHelper.ReplaceVars("{{x}}", scopeChain, vars);

        Assert.Equal("scope", result);
    }

    #endregion

    #region GetLoopRows

    [Fact]
    public void GetLoopRows_FindsInInnerScope()
    {
        var rows = new List<Dictionary<string, object>>
        {
            new() { { "name", "row1" } }
        };
        var scopeChain = new List<Dictionary<string, object>>
        {
            new() { { "items", rows } }
        };

        var result = VariableHelper.GetLoopRows("items", scopeChain);

        Assert.Single(result);
        Assert.Equal("row1", result[0]["name"]);
    }

    [Fact]
    public void GetLoopRows_FindsInOuterScope()
    {
        var rows = new List<Dictionary<string, object>>
        {
            new() { { "name", "row1" } }
        };
        var scopeChain = new List<Dictionary<string, object>>
        {
            new(),
            new() { { "items", rows } }
        };

        var result = VariableHelper.GetLoopRows("items", scopeChain);

        Assert.Single(result);
    }

    [Fact]
    public void GetLoopRows_NotFound_ReturnsEmpty()
    {
        var scopeChain = new List<Dictionary<string, object>>
        {
            new() { { "other", "value" } }
        };

        var result = VariableHelper.GetLoopRows("items", scopeChain);

        Assert.Empty(result);
    }

    [Fact]
    public void GetLoopRows_NullSource_ReturnsEmpty()
    {
        var result = VariableHelper.GetLoopRows(null, []);

        Assert.Empty(result);
    }

    [Fact]
    public void GetLoopRows_NonListValue_ReturnsEmpty()
    {
        var scopeChain = new List<Dictionary<string, object>>
        {
            new() { { "items", "not a list" } }
        };

        var result = VariableHelper.GetLoopRows("items", scopeChain);

        Assert.Empty(result);
    }

    [Fact]
    public void GetLoopRows_HandlesListObject_ProductionPath()
    {
        // 改动①：生产链路 NormalizeJsonElement 产出 List<object>（元素运行时是 Dictionary），原 `is List<Dictionary>` 对其恒 false（P0）。
        var rows = new List<object>
        {
            new Dictionary<string, object> { { "name", "row1" } },
            new Dictionary<string, object> { { "name", "row2" } }
        };
        var scopeChain = new List<Dictionary<string, object>> { new() { { "items", rows } } };

        var result = VariableHelper.GetLoopRows("items", scopeChain);

        Assert.Equal(2, result.Count);
        Assert.Equal("row1", result[0]["name"]);
        Assert.Equal("row2", result[1]["name"]);
    }

    [Fact]
    public void GetLoopRows_HandlesJsonElementArray()
    {
        // 改动①：未经入口归一的 JsonElement(Array) 兜底分支。
        using var doc = JsonDocument.Parse("""{"items": [{"name": "row1"}, {"name": "row2"}]}""");
        var scopeChain = new List<Dictionary<string, object>> { new() { { "items", doc.RootElement.GetProperty("items") } } };

        var result = VariableHelper.GetLoopRows("items", scopeChain);

        Assert.Equal(2, result.Count);
        Assert.Equal("row1", result[0]["name"]);
    }

    [Fact]
    public void GetLoopRows_WrapsScalarItems_ForItemReference()
    {
        // 改动⑤：简单值数组（array+items）元素是标量 → 包装 {"item": 标量} 供 loop 内 {{item}} 引用。
        var rows = new List<object> { "apple", "book" };
        var scopeChain = new List<Dictionary<string, object>> { new() { { "items", rows } } };

        var result = VariableHelper.GetLoopRows("items", scopeChain);

        Assert.Equal(2, result.Count);
        Assert.Equal("apple", result[0]["item"]);
        Assert.Equal("book", result[1]["item"]);
    }

    #endregion

    #region ApplyTransform

    [Fact]
    public void Transform_Trim()
    {
        Assert.Equal("hello", VariableHelper.ApplyTransform(" hello ", "trim"));
    }

    [Fact]
    public void Transform_Upper()
    {
        Assert.Equal("ABC", VariableHelper.ApplyTransform("abc", "upper"));
    }

    [Fact]
    public void Transform_Lower()
    {
        Assert.Equal("abc", VariableHelper.ApplyTransform("ABC", "lower"));
    }

    [Fact]
    public void Transform_Date_Format()
    {
        // 改动7：date/number 转换从 transform 移到 format（ApplyFormat）
        var result = VariableHelper.ApplyFormat("2026-05-19", "dd/MM/yyyy", "date");
        Assert.Equal("19/05/2026", result);
    }

    [Fact]
    public void Transform_Date_InTransform_NoLongerConverts()
    {
        // 改动7：ApplyTransform 的 date:/number: 分支已删，返回原值（转换走 ApplyFormat）
        Assert.Equal("2026-05-19", VariableHelper.ApplyTransform("2026-05-19", "date:dd/MM/yyyy"));
    }

    [Fact]
    public void Transform_Number_Format()
    {
        // 改动7：number 转换走 ApplyFormat
        var result = VariableHelper.ApplyFormat("1234567", "#,##0.00", "number");
        Assert.Equal("1,234,567.00", result);
    }

    [Fact]
    public void Transform_NullValue_ReturnsNull()
    {
        Assert.Null(VariableHelper.ApplyTransform(null, "trim"));
    }

    [Fact]
    public void Transform_NullTransform_ReturnsValue()
    {
        Assert.Equal("hello", VariableHelper.ApplyTransform("hello", null));
    }

    [Fact]
    public void Transform_UnknownTransform_ReturnsValue()
    {
        Assert.Equal("hello", VariableHelper.ApplyTransform("hello", "unknown"));
    }

    [Fact]
    public void Transform_Date_InvalidDate_ReturnsOriginal()
    {
        // 改动7：ApplyFormat TryParse 失败返回原值
        Assert.Equal("not-a-date", VariableHelper.ApplyFormat("not-a-date", "dd/MM/yyyy", "date"));
    }

    [Fact]
    public void Transform_Number_InvalidNumber_ReturnsOriginal()
    {
        Assert.Equal("not-a-number", VariableHelper.ApplyFormat("not-a-number", "#,##0.00", "number"));
    }

    // 项6 补充：ApplyFormat 早返回分支（null value / 空 format / 非 date-number type）
    [Fact]
    public void ApplyFormat_NullValue_ReturnsNull()
    {
        Assert.Null(VariableHelper.ApplyFormat(null, "dd/MM/yyyy", "date"));
    }

    [Fact]
    public void ApplyFormat_EmptyFormat_ReturnsOriginal()
    {
        Assert.Equal("2026-06-23", VariableHelper.ApplyFormat("2026-06-23", "", "date"));
    }

    [Fact]
    public void ApplyFormat_NullFormat_ReturnsOriginal()
    {
        Assert.Equal("2026-06-23", VariableHelper.ApplyFormat("2026-06-23", null, "date"));
    }

    [Fact]
    public void ApplyFormat_StringType_IgnoresFormat()
    {
        // string/boolean/file/array 的 format 是纯前端展示，不触发转换（ApplyFormat 的 _ 分支原样返回）
        Assert.Equal("2026-06-23", VariableHelper.ApplyFormat("2026-06-23", "dd/MM/yyyy", "string"));
    }

    [Fact]
    public void ApplyFormat_BooleanType_IgnoresFormat()
    {
        Assert.Equal("true", VariableHelper.ApplyFormat("true", "dd/MM/yyyy", "boolean"));
    }

    // 项6 补充：TransformNumber 未覆盖数值范围（负数 / 整数千分位 / 固定小数 / 大数）
    [Fact]
    public void TransformNumber_Negative()
    {
        Assert.Equal("-1,234.50", VariableHelper.ApplyFormat("-1234.5", "#,##0.00", "number"));
    }

    [Fact]
    public void TransformNumber_IntegerThousands()
    {
        Assert.Equal("1,234,567", VariableHelper.ApplyFormat("1234567", "#,##0", "number"));
    }

    [Fact]
    public void TransformNumber_FixedDecimalF2()
    {
        Assert.Equal("1234.50", VariableHelper.ApplyFormat("1234.5", "F2", "number"));
    }

    [Fact]
    public void TransformNumber_LargeNumber()
    {
        Assert.Equal("1,234,567,890.00", VariableHelper.ApplyFormat("1234567890", "#,##0.00", "number"));
    }

    #endregion

    #region F3 date format: moment.js ↔ C# token 规范化

    [Fact]
    public void F3_Date_MomentStyle_YYYYMMDD()
    {
        // moment.js 风格 YYYY-MM-DD 应正确输出（YYYY→yyyy 转换，按长度降序避免被 YY 误二次替换）
        Assert.Equal("2026-06-23", VariableHelper.ApplyFormat("2026-06-23", "YYYY-MM-DD", "date"));
    }

    [Fact]
    public void F3_Date_MomentStyle_DDMMYYYY()
    {
        // moment.js 风格 DD/MM/YYYY（手册字段示例样例）应正确输出（DD→dd 转换）
        Assert.Equal("23/06/2026", VariableHelper.ApplyFormat("2026-06-23", "DD/MM/YYYY", "date"));
    }

    [Fact]
    public void F3_Date_CSharpStyle_StillWorks()
    {
        // C# 风格 yyyy-MM-dd 不受转换器影响（小写 y/d 碰不到「转大写 D/Y」规则），原样正确
        Assert.Equal("2026-06-23", VariableHelper.ApplyFormat("2026-06-23", "yyyy-MM-dd", "date"));
    }

    [Fact]
    public void F3_Date_MixedMomentCSharpTokens()
    {
        // 混写：YYYY（moment，转）+ MM/dd（两套一致，不动）→ 仅 YYYY 转换
        Assert.Equal("2026/06/23", VariableHelper.ApplyFormat("2026-06-23", "YYYY/MM/dd", "date"));
    }

    [Fact]
    public void F3_Date_WeekdayShort_ddd()
    {
        // 星期缩写 ddd（moment/C# 一致，不转换）：2024-01-01 是 Monday → Mon
        Assert.Equal("Mon", VariableHelper.ApplyFormat("2024-01-01", "ddd", "date"));
    }

    [Fact]
    public void F3_Date_WeekdayFull_dddd()
    {
        // 星期全名 dddd（小写，不被 D→d 误伤；大小写敏感）：2024-01-01 → Monday
        Assert.Equal("Monday", VariableHelper.ApplyFormat("2024-01-01", "dddd", "date"));
    }

    [Fact]
    public void F3_NumberFormat_NotAffectedByDateNormalizer()
    {
        // number 的 #,##0.00 不经 NormalizeDateFormat（TransformNumber 独立路径），两套通用
        Assert.Equal("1,234.50", VariableHelper.ApplyFormat("1234.5", "#,##0.00", "number"));
    }

    // 项6 补充：TransformDate 未覆盖 token / 输入格式
    [Fact]
    public void F3_Date_TwoDigitYear_YY()
    {
        // moment YY（2 位年）→ C# yy：2026 → "26"
        Assert.Equal("26", VariableHelper.ApplyFormat("2026-06-23", "YY", "date"));
    }

    [Fact]
    public void F3_Date_ChineseLiteralFormat()
    {
        // 中文字面量在 format 中原样保留；无大写 Y/D → 不经 NormalizeDateFormat
        Assert.Equal("2026年06月23日", VariableHelper.ApplyFormat("2026-06-23", "yyyy年MM月dd日", "date"));
    }

    [Fact]
    public void TransformDate_InputWithTime_TryParses()
    {
        // DateTime.TryParse 接受含时间的 ISO 输入；HH/mm/ss 两套一致不经 NormalizeDateFormat
        Assert.Equal("2026-06-23 14:30:45", VariableHelper.ApplyFormat("2026-06-23T14:30:45", "yyyy-MM-dd HH:mm:ss", "date"));
    }

    [Fact]
    public void TransformDate_EmptyValue_ReturnsEmpty()
    {
        // value="" + 非空 format：TryParse("")=false → 返回原值 ""
        Assert.Equal("", VariableHelper.ApplyFormat("", "yyyy-MM-dd", "date"));
    }

    #endregion

    #region InferFieldFromValue

    [Fact]
    public void InferField_SinglePlaceholder()
    {
        Assert.Equal("F_HTMC", VariableHelper.InferFieldFromValue("{{F_HTMC}}"));
    }

    [Fact]
    public void InferField_InLargerText()
    {
        Assert.Equal("F_HTMC", VariableHelper.InferFieldFromValue("合同名称是 {{F_HTMC}} 填写"));
    }

    [Fact]
    public void InferField_NoMatch_ReturnsNull()
    {
        Assert.Null(VariableHelper.InferFieldFromValue("no placeholder"));
    }

    [Fact]
    public void InferField_Null_ReturnsNull()
    {
        Assert.Null(VariableHelper.InferFieldFromValue(null));
    }

    #endregion

    #region ResolveUploadValue

    [Fact]
    public void ResolveUpload_SinglePath_Relative()
    {
        var result = VariableHelper.ResolveUploadValue("/uploads/test.pdf", "C:\\data");
        Assert.Single(result);
        Assert.Contains("test.pdf", result[0]);
    }

    [Fact]
    public void ResolveUpload_SinglePath_Absolute()
    {
        var absPath = "C:\\absolute\\test.pdf";
        var result = VariableHelper.ResolveUploadValue(absPath, "C:\\data");
        Assert.Single(result);
        Assert.Equal(absPath, result[0]);
    }

    [Fact]
    public void ResolveUpload_ObjectArray_ExtractsPath()
    {
        var list = new List<object>
        {
            new Dictionary<string, object> { { "name", "a.pdf" }, { "path", "/uploads/a.pdf" }, { "size", 100 } }
        };
        var result = VariableHelper.ResolveUploadValue(list, "C:\\data");
        Assert.Single(result);
        Assert.Contains("a.pdf", result[0]);
    }

    [Fact]
    public void ResolveUpload_StringArray()
    {
        var list = new List<object> { "/uploads/a.pdf", "/uploads/b.pdf" };
        var result = VariableHelper.ResolveUploadValue(list, "C:\\data");
        Assert.Equal(2, result.Length);
    }

    [Fact]
    public void ResolveUpload_Null_ReturnsEmpty()
    {
        var result = VariableHelper.ResolveUploadValue(null, "C:\\data");
        Assert.Empty(result);
    }

    [Fact]
    public void ResolveUpload_JsonElement_String()
    {
        var json = JsonSerializer.Deserialize<JsonElement>(@"""/uploads/test.pdf""");
        var result = VariableHelper.ResolveUploadValue(json, "C:\\data");
        Assert.Single(result);
    }

    [Fact]
    public void ResolveUpload_JsonElement_Array()
    {
        var json = JsonSerializer.Deserialize<JsonElement>("[{\"name\":\"a.pdf\",\"path\":\"/uploads/a.pdf\"}]");
        var result = VariableHelper.ResolveUploadValue(json, "C:\\data");
        Assert.Single(result);
    }

    [Fact]
    public void ResolveUpload_JsonElement_ObjectArray()
    {
        var json = JsonSerializer.Deserialize<JsonElement>(
            "[{\"name\":\"a.pdf\",\"path\":\"/uploads/a.pdf\"},{\"name\":\"b.pdf\",\"path\":\"/uploads/b.pdf\"}]");
        var result = VariableHelper.ResolveUploadValue(json, "C:\\data");
        Assert.Equal(2, result.Length);
    }

    #endregion

    #region StoreVars

    [Fact]
    public void StoreVars_StringKey()
    {
        var vars = new Dictionary<string, object>();
        VariableHelper.StoreVars(vars, "myVar", "myValue");
        Assert.Equal("myValue", vars["myVar"]);
    }

    [Fact]
    public void StoreVars_NullStoreAs_NoOp()
    {
        var vars = new Dictionary<string, object>();
        VariableHelper.StoreVars(vars, null, "value");
        Assert.Empty(vars);
    }

    [Fact]
    public void StoreVars_NullValue_NoOp()
    {
        var vars = new Dictionary<string, object>();
        VariableHelper.StoreVars(vars, "myVar", null);
        Assert.Empty(vars);
    }

    [Fact]
    public void StoreVars_ObjectMap()
    {
        var vars = new Dictionary<string, object>();
        var storeAsJson = JsonSerializer.Deserialize<JsonElement>(@"{""billCode"":""单据编号"",""amount"":""金额""}");
        var value = new Dictionary<string, object>
        {
            { "billCode", "INV001" },
            { "amount", 1000 }
        };

        VariableHelper.StoreVars(vars, storeAsJson, value);

        Assert.Equal("INV001", vars["billCode"]);
        Assert.Equal(1000, vars["amount"]);
    }

    // 项7 发现的 bug 回归：JSON 反序列化使 storeAs 进 object? 字段成 JsonElement(String)，
    // 原 StoreVars switch 仅 case string + case JsonElement(Object)，漏 JsonElement(String) → 静默不存。
    // 经 JSON 加载的脚本（生产 App→Worker + golden 套件）extract/evaluate storeAs 全丢失、returnData 引用全空。
    [Fact]
    public void StoreVars_JsonElementString_StoreName()
    {
        var vars = new Dictionary<string, object>();
        var storeAs = JsonDocument.Parse("\"token\"").RootElement;  // JsonElement(String)，等价 STJ 反序列化 object? 字段
        VariableHelper.StoreVars(vars, storeAs, "ABC123");
        Assert.Equal("ABC123", vars["token"]);
    }

    [Fact]
    public void StoreVars_JsonElementObject_MultiMap()
    {
        // JsonElement(Object) 多 storeAs（归一化后走 Dictionary 分支，与 StoreVars_ObjectMap 等价的 JsonDocument 构造）
        var vars = new Dictionary<string, object>();
        var storeAs = JsonDocument.Parse("{\"billCode\":\"单据号\",\"amount\":\"金额\"}").RootElement;
        var value = new Dictionary<string, object> { ["billCode"] = "INV001", ["amount"] = 1000 };
        VariableHelper.StoreVars(vars, storeAs, value);
        Assert.Equal("INV001", vars["billCode"]);
        Assert.Equal(1000, vars["amount"]);
    }

    [Fact]
    public void StoreVars_ObjectMap_JsonValueIsJsonObjectElement_SplitsByVarName()
    {
        // 调研#9 根因验证(2026-06-29)：生产 ai action 的 resultValue 来自 done 工具 result 参数，
        // 经 OpenAiProvider.ParseJsonElement，Object 值保持 JsonElement(Object)（非 CLR Dictionary，见 ParseJsonElement L137 Object→element）。
        // StoreVars 多变量分支 `case Dictionary when value is Dictionary` 要求 value 是 CLR Dictionary，
        // JsonElement(Object) 不匹配 → 多变量 storeAs 不 populate（真实生产 bug，非测试设计错/非纯 AI 非确定性）。
        // 既有 StoreVars_ObjectMap / StoreVars_JsonElementObject_MultiMap 的 value 均用 CLR Dictionary（理想化构造）绕过此形态。
        // 本测试用 JsonElement(Object) 构造 value（走生产 ai action resultValue 真实形态），断言应按变量名拆分（修后通过）。
        var vars = new Dictionary<string, object>();
        var storeAs = JsonDocument.Parse("{\"token\":\"token值\",\"itemCount\":\"数量\"}").RootElement;
        var value = JsonDocument.Parse("{\"token\":\"ABC123\",\"itemCount\":4}").RootElement;  // 生产形态：JsonElement(Object)

        VariableHelper.StoreVars(vars, storeAs, value);

        Assert.Equal("ABC123", vars["token"]?.ToString());
        Assert.Equal("4", vars["itemCount"]?.ToString());
    }


    #endregion

    #region Route B: {{obj.key}} dot notation

    [Fact]
    public void ReplaceVars_DotNotation_BasicObject()
    {
        var vars = new Dictionary<string, object>
        {
            { "shot", new Dictionary<string, object> { { "path", "E:\\screenshots\\test.png" }, { "dataUrl", "data:image/png;base64,abc" } } }
        };

        var result = VariableHelper.ReplaceVars("{{shot.path}}", [], vars);
        Assert.Equal("E:\\screenshots\\test.png", result);
    }

    [Fact]
    public void ReplaceVars_DotNotation_DataUrl()
    {
        var vars = new Dictionary<string, object>
        {
            { "shot", new Dictionary<string, object> { { "path", "/tmp/test.png" }, { "dataUrl", "data:image/png;base64,abc" } } }
        };

        var result = VariableHelper.ReplaceVars("{{shot.dataUrl}}", [], vars);
        Assert.Equal("data:image/png;base64,abc", result);
    }

    [Fact]
    public void ReplaceVars_DotNotation_NestedObject()
    {
        var inner = new Dictionary<string, object> { { "c", "deep_value" } };
        var outer = new Dictionary<string, object> { { "b", inner } };
        var vars = new Dictionary<string, object> { { "a", outer } };

        var result = VariableHelper.ReplaceVars("{{a.b.c}}", [], vars);
        Assert.Equal("deep_value", result);
    }

    [Fact]
    public void ReplaceVars_DotNotation_UnknownRoot_PreservesPlaceholder()
    {
        var vars = new Dictionary<string, object>();

        var result = VariableHelper.ReplaceVars("{{unknown.key}}", [], vars);
        Assert.Equal("{{unknown.key}}", result);
    }

    [Fact]
    public void ReplaceVars_DotNotation_MissingProperty_PreservesPlaceholder()
    {
        var vars = new Dictionary<string, object>
        {
            { "shot", new Dictionary<string, object> { { "path", "/tmp/test.png" } } }
        };

        var result = VariableHelper.ReplaceVars("{{shot.missing}}", [], vars);
        Assert.Equal("{{shot.missing}}", result);
    }

    [Fact]
    public void ReplaceVars_DotNotation_MixedWithPlainVars()
    {
        var scopeChain = new List<Dictionary<string, object>>
        {
            new() { { "name", "test" } }
        };
        var vars = new Dictionary<string, object>
        {
            { "stats", new Dictionary<string, object> { { "total", "100" }, { "count", "3" } } }
        };

        var result = VariableHelper.ReplaceVars("{{name}}: total={{stats.total}}, count={{stats.count}}", scopeChain, vars);
        Assert.Equal("test: total=100, count=3", result);
    }

    [Fact]
    public void ResolveRawValue_DotNotation_ReturnsRawObject()
    {
        var dict = new Dictionary<string, object> { { "path", "/tmp/test.png" } };
        var vars = new Dictionary<string, object> { { "shot", dict } };

        var result = VariableHelper.ResolveRawValue("{{shot}}", [], vars);
        Assert.Same(dict, result);
    }

    [Fact]
    public void ResolveRawValue_DotNotation_ReturnsNestedValue()
    {
        var dict = new Dictionary<string, object> { { "path", "/tmp/test.png" } };
        var vars = new Dictionary<string, object> { { "shot", dict } };

        var result = VariableHelper.ResolveRawValue("{{shot.path}}", [], vars);
        Assert.Equal("/tmp/test.png", result);
    }

    [Fact]
    public void ResolveRawValue_LoopRowPath_TakesCurrentRowNotRootArray()
    {
        // 批次7-loop 修复（核查第11轮 P1）：loopSource=attachments 回放每行 rowData={name,url,path}，filePath={{path}} 取当前行单附件 path
        // （原 {{fieldName}} 在 rowData 无键→落根作用域取完整数组→每轮传全部；修复后 {{path}} 命中当前行）
        var rowData = new Dictionary<string, object> { { "path", "/uploads/a.pdf" }, { "name", "a.pdf" }, { "url", "/uploads/a.pdf" } };
        var rootAttachments = new List<object> { rowData, new Dictionary<string, object> { { "path", "/uploads/b.pdf" } } };
        var scopeChain = new List<Dictionary<string, object>> { rowData, new() { { "attachments", rootAttachments } } };

        var result = VariableHelper.ResolveRawValue("{{path}}", scopeChain, new());

        Assert.Equal("/uploads/a.pdf", result);  // 取当前行 path（非根作用域完整数组）
    }

    [Fact]
    public void ResolveRawValue_PluckArrayField_SingleLevel()
    {
        // 改动④：{{arr.field}} 数组逐项取字段 → flatMap 一维（{{detailtable.id}} → id 列数组）。
        var rows = new List<object>
        {
            new Dictionary<string, object> { { "id", "A001" } },
            new Dictionary<string, object> { { "id", "A002" } }
        };
        var vars = new Dictionary<string, object> { { "detailtable", rows } };

        var result = VariableHelper.ResolveRawValue("{{detailtable.id}}", [], vars);

        var arr = Assert.IsType<List<object>>(result);
        Assert.Equal(new[] { "A001", "A002" }, arr);
    }

    [Fact]
    public void ResolveRawValue_PluckNestedArray_FlatMap()
    {
        // 改动④：多层 {{a.b.c}} → 递归 flatMap 摊平一维（明细→消费→金额）。
        var rows = new List<object>
        {
            new Dictionary<string, object>
            {
                { "consumption", new List<object>
                    {
                        new Dictionary<string, object> { { "amount", 10 } },
                        new Dictionary<string, object> { { "amount", 20 } }
                    }
                }
            },
            new Dictionary<string, object>
            {
                { "consumption", new List<object>
                    {
                        new Dictionary<string, object> { { "amount", 30 } }
                    }
                }
            }
        };
        var vars = new Dictionary<string, object> { { "detailtable", rows } };

        var result = VariableHelper.ResolveRawValue("{{detailtable.consumption.amount}}", [], vars);

        var arr = Assert.IsType<List<object>>(result);
        Assert.Equal(new object[] { 10, 20, 30 }, arr);  // 手动构造的 int 字面量原样返回（pluck 不改类型）
    }

    [Fact]
    public void ResolveRawValue_PluckArray_MissingField_Skipped()
    {
        // 改动④：数组中部分元素缺字段 → 该项跳过（不报错、不中断），结果只含命中的。
        var rows = new List<object>
        {
            new Dictionary<string, object> { { "id", "A001" } },
            new Dictionary<string, object> { { "name", "no-id" } },
            new Dictionary<string, object> { { "id", "A003" } }
        };
        var vars = new Dictionary<string, object> { { "detailtable", rows } };

        var result = VariableHelper.ResolveRawValue("{{detailtable.id}}", [], vars);

        var arr = Assert.IsType<List<object>>(result);
        Assert.Equal(new[] { "A001", "A003" }, arr);
    }

    [Fact]
    public void ReplaceArgsVars_PassesObjectArray()
    {
        // 改动③：args 纯变量引用（如 "{{detailtable}}"）透传原始对象数组，非字符串。
        var rows = new List<object>
        {
            new Dictionary<string, object> { { "id", "A001" } }
        };
        var scopeChain = new List<Dictionary<string, object>> { new() { { "detailtable", rows } } };

        var result = VariableHelper.ReplaceArgsVars(["{{detailtable}}"], scopeChain, new());

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Same(rows, result![0]);  // 透传原始 List<object>，非 ToString
    }

    [Fact]
    public void ReplaceArgsVars_NonPureVar_FallsBackToTextReplace()
    {
        // 改动③：非纯变量引用（混合文本）回退 ReplaceVars 文本替换。
        var scopeChain = new List<Dictionary<string, object>> { new() { { "name", "World" } } };

        var result = VariableHelper.ReplaceArgsVars(["Hello {{name}}"], scopeChain, new());

        Assert.Equal("Hello World", result![0]);
    }

    [Fact]
    public void InferFieldFromValue_DotNotation_ReturnsRootField()
    {
        Assert.Equal("shot", VariableHelper.InferFieldFromValue("{{shot.path}}"));
    }

    [Fact]
    public void InferFieldFromValue_DotNotation_DeepNesting()
    {
        Assert.Equal("stats", VariableHelper.InferFieldFromValue("{{stats.total.count}}"));
    }

    #endregion

    #region StoreVars Dictionary compatibility

    [Fact]
    public void StoreVars_StringKey_DictionaryValue_ExtractsMatchingKey()
    {
        var vars = new Dictionary<string, object>();
        var value = new Dictionary<string, object>
        {
            { "billCode", "BILL001" },
            { "amount", 1000 }
        };
        VariableHelper.StoreVars(vars, "billCode", value);
        Assert.Equal("BILL001", vars["billCode"]);
    }

    [Fact]
    public void StoreVars_StringKey_ScalarValue_StoresDirectly()
    {
        var vars = new Dictionary<string, object>();
        VariableHelper.StoreVars(vars, "name", "hello");
        Assert.Equal("hello", vars["name"]);
    }

    #endregion
}
