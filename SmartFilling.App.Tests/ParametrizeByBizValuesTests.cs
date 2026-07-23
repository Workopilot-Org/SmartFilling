using System.Text.Json;
using SmartFilling.App.Recording;
using SmartFilling.Engine.Models;
using Xunit;

namespace SmartFilling.App.Tests;

/// <summary>
/// dreamy E：bizValues 纯函数单测（BuildBizList 校验/自动推导/切分检测 + ParametrizeByBizValues/OtherFields/ParametrizeString/Fallback/Args/Detect 搜替换）。
/// 含生产反序列化路径 JsonElement 形态（BizValue 经 JsonSerializer.Deserialize<List<BizValue>>，避免重蹈 loopSource P0/StoreVars 覆辙）。
/// 断言验值（落盘占位符具体内容），不只验状态。
/// </summary>
public class ParametrizeByBizValuesTests
{
    // ===== E1 BuildBizList：自动推导 + 去重 + 字段名校验 + 切分检测 =====

    [Fact]
    public void BuildBizList_AutoDerive_FillValue()  // 决策3：fill value + fieldName 自动推导
    {
        var step = new StepNode { Action = "fill", Value = "张三", Field = "username" };
        var (allBiz, _, err) = RecordingActionExecutor.BuildBizList(null, "username", "fill", step, null);
        Assert.Null(err);
        Assert.Single(allBiz);
        Assert.Equal("张三", allBiz[0].Value);
        Assert.Equal("username", allBiz[0].Field);
    }

    [Fact]
    public void BuildBizList_AutoDerive_TypeAction()  // type 也自动推导（B 修原漏 type）
    {
        var step = new StepNode { Action = "type", Value = "追加文本", Field = "suffix" };
        var (allBiz, _, err) = RecordingActionExecutor.BuildBizList(null, "suffix", "type", step, null);
        Assert.Null(err);
        Assert.Single(allBiz);
        Assert.Equal("追加文本", allBiz[0].Value);
    }

    [Fact]
    public void BuildBizList_Dedup_AutoDerive_And_AI_Biz()  // 决策5：自动推导 + AI 传同值去重
    {
        var bizValues = new List<BizValue> { new("张三", "username") };
        var step = new StepNode { Action = "fill", Value = "张三", Field = "username" };
        var (allBiz, _, err) = RecordingActionExecutor.BuildBizList(bizValues, "username", "fill", step, null);
        Assert.Null(err);
        Assert.Single(allBiz);  // 去重后 1 个
    }

    [Fact]
    public void BuildBizList_FieldNotRegistered_ReturnsError()  // 决策13：field 未注册 -> error
    {
        var bizValues = new List<BizValue> { new("021", "companyCode") };
        var step = new StepNode { Action = "click", Selector = "//div[@id='dc_021']" };
        var (allBiz, _, err) = RecordingActionExecutor.BuildBizList(bizValues, "otherField", "click", step, null);
        Assert.NotNull(err);
        Assert.Contains("companyCode", err);
        Assert.Contains("未注册", err);
    }

    [Fact]
    public void BuildBizList_FieldRegistered_ThisStep()  // field == 本步 fieldName（前瞻，阶段6 早于字段注册）
    {
        var bizValues = new List<BizValue> { new("021", "companyCode") };
        var step = new StepNode { Action = "click", Selector = "//div[@id='dc_021']" };
        var (allBiz, _, err) = RecordingActionExecutor.BuildBizList(bizValues, "companyCode", "click", step, null);
        Assert.Null(err);
    }

    [Fact]
    public void BuildBizList_FieldRegistered_PreviousStep()  // field 前步已注册（script.Fields 含）
    {
        var bizValues = new List<BizValue> { new("021", "companyCode") };
        var step = new StepNode { Action = "click", Selector = "//div[@id='dc_021']" };
        var script = new ScriptV2 { Fields = new List<FieldDefinition> { new() { Name = "companyCode" } } };
        var (allBiz, _, err) = RecordingActionExecutor.BuildBizList(bizValues, "otherField", "click", step, script);
        Assert.Null(err);
    }

    [Fact]
    public void BuildBizList_EmptyValue_ReturnsError()  // value/field 空 -> error
    {
        var bizValues = new List<BizValue> { new("", "companyCode") };
        var step = new StepNode { Action = "click", Selector = "//div[@id='dc_021']" };
        var (_, _, err) = RecordingActionExecutor.BuildBizList(bizValues, "companyCode", "click", step, null);
        Assert.NotNull(err);
        Assert.Contains("不能为空", err);
    }

    [Fact]
    public void BuildBizList_SplitWarning_AttrWholeValue()  // 决策9/10：bizValue==属性值整体 -> warning（不阻断）
    {
        var bizValues = new List<BizValue> { new("dc_021", "companyCode") };  // bizValue == 属性值整体（含前缀 dc_）
        var step = new StepNode { Action = "click", Selector = "//div[@id='dc_021']" };
        var script = new ScriptV2 { Fields = new List<FieldDefinition> { new() { Name = "companyCode" } } };
        var (_, warnings, err) = RecordingActionExecutor.BuildBizList(bizValues, "companyCode", "click", step, script);
        Assert.Null(err);  // warning 不阻断
        Assert.NotEmpty(warnings);
        Assert.Contains(warnings, w => w.Contains("切分") || w.Contains("前缀"));
    }

    // ===== E1 ParametrizeString：搜替换 + 边界（决策6/7/8）=====

    [Fact]
    public void ParametrizeString_LongValue_NoBoundary()  // >=8 无边界
    {
        var biz = new List<BizValue> { new("SQS2026071300003", "contractNo") };
        var result = RecordingActionExecutor.ParametrizeString("合同编号SQS2026071300003结束", biz);
        Assert.Equal("合同编号{{contractNo}}结束", result);
    }

    [Fact]
    public void ParametrizeString_ShortValue_WordBoundary()  // <8 词边界（避免误匹配）
    {
        var biz = new List<BizValue> { new("021", "companyCode") };
        // 词边界：021 前后非字母数字才替换
        var result = RecordingActionExecutor.ParametrizeString("@id='dc_021'", biz);
        Assert.Equal("@id='dc_{{companyCode}}'", result);
    }

    [Fact]
    public void ParametrizeString_ShortValue_NoFalseMatch_InWord()  // <8 词边界防误匹配（021 在 10213 内不替换）
    {
        var biz = new List<BizValue> { new("021", "companyCode") };
        var result = RecordingActionExecutor.ParametrizeString("10213", biz);
        Assert.Equal("10213", result);  // 021 在数字中间，词边界不匹配
    }

    [Fact]
    public void ParametrizeString_PreservePrefix()  // 决策24：value 拼接保留前缀（备注：021 -> 备注：{{companyCode}}）
    {
        var biz = new List<BizValue> { new("021", "companyCode") };
        var result = RecordingActionExecutor.ParametrizeString("备注：021", biz);
        Assert.Equal("备注：{{companyCode}}", result);
    }

    [Fact]
    public void ParametrizeString_Null_Returns_Null()
    {
        var biz = new List<BizValue> { new("021", "companyCode") };
        var result = RecordingActionExecutor.ParametrizeString(null, biz);
        Assert.Null(result);
    }

    [Fact]
    public void ParametrizeString_EmptyBiz_Unchanged()
    {
        var result = RecordingActionExecutor.ParametrizeString("dc_021", new List<BizValue>());
        Assert.Equal("dc_021", result);
    }

    // ===== E1 ParametrizeByBizValues：全字段搜替换（save_step 用）=====

    [Fact]
    public void ParametrizeByBizValues_AllFields()  // 决策27/28：全字段搜替换
    {
        var biz = new List<BizValue> { new("021", "companyCode") };
        var step = new StepNode
        {
            Selector = "//div[@id='dc_021']",
            Value = "备注：021",
            Url = "https://example.com/021/list",
            Key = "key_021",
            Code = "code_021",
            Description = "公司021",
            Iframe = new[] { "//iframe[@id='frame_021']" },
        };
        var result = RecordingActionExecutor.ParametrizeByBizValues(step, biz);
        Assert.Contains("dc_{{companyCode}}", result.Selector);
        Assert.Equal("备注：{{companyCode}}", result.Value);
        Assert.Contains("/{{companyCode}}/", result.Url);
        Assert.Equal("key_{{companyCode}}", result.Key);
        Assert.Equal("code_{{companyCode}}", result.Code);
        Assert.Contains("{{companyCode}}", result.Description);
        Assert.Contains("frame_{{companyCode}}", result.Iframe![0]);
    }

    [Fact]
    public void ParametrizeByBizValues_Captcha5Selector()  // captcha 5 selector 搜替换
    {
        var biz = new List<BizValue> { new("021", "companyCode") };
        var step = new StepNode
        {
            ImageSelector = "//img[@id='img_021']",
            InputSelector = "//input[@id='in_021']",
            SliderSelector = "//div[@id='sl_021']",
            TargetSelector = "//div[@id='tg_021']",
            BackgroundSelector = "//div[@id='bg_021']",
        };
        var result = RecordingActionExecutor.ParametrizeByBizValues(step, biz);
        Assert.Contains("img_{{companyCode}}", result.ImageSelector);
        Assert.Contains("in_{{companyCode}}", result.InputSelector);
        Assert.Contains("sl_{{companyCode}}", result.SliderSelector);
        Assert.Contains("tg_{{companyCode}}", result.TargetSelector);
        Assert.Contains("bg_{{companyCode}}", result.BackgroundSelector);
    }

    [Fact]
    public void ParametrizeByBizValues_Fallback_Recursive()  // 决策28：fallback 递归
    {
        var biz = new List<BizValue> { new("021", "companyCode") };
        var step = new StepNode
        {
            Fallback = new StepFallback
            {
                Selector = "//div[@id='fb_021']",
                Value = "v_021",
                Code = "c_021",
                Iframe = new[] { "//iframe[@id='fbframe_021']" },
                Fallback = new StepFallback { Selector = "//div[@id='nested_021']" },
            }
        };
        var result = RecordingActionExecutor.ParametrizeByBizValues(step, biz);
        Assert.Contains("fb_{{companyCode}}", result.Fallback!.Selector);
        Assert.Equal("v_{{companyCode}}", result.Fallback.Value);
        Assert.Contains("fbframe_{{companyCode}}", result.Fallback.Iframe![0]);
        Assert.Contains("nested_{{companyCode}}", result.Fallback.Fallback!.Selector);  // 递归
    }

    [Fact]
    public void ParametrizeByBizValues_Detect_Recursive()  // 决策25：detect all/any/not 递归
    {
        var biz = new List<BizValue> { new("021", "companyCode") };
        var step = new StepNode
        {
            Detect = new DetectCondition
            {
                All = new List<DetectCondition>
                {
                    new() { Selector = "//div[@id='a_021']", Value = "v_021" },
                },
                Any = new List<DetectCondition>
                {
                    new() { Keywords = new[] { "k_021" }, UrlContains = "u_021" },
                },
                Not = new DetectCondition { Selector = "//div[@id='n_021']" },
            }
        };
        var result = RecordingActionExecutor.ParametrizeByBizValues(step, biz);
        Assert.Contains("a_{{companyCode}}", result.Detect!.All![0].Selector);
        Assert.Equal("v_{{companyCode}}", result.Detect.All[0].Value);
        Assert.Contains("k_{{companyCode}}", result.Detect.Any![0].Keywords![0]);
        Assert.Contains("u_{{companyCode}}", result.Detect.Any[0].UrlContains);
        Assert.Contains("n_{{companyCode}}", result.Detect.Not!.Selector);
    }

    [Fact]
    public void ParametrizeByBizValues_Detect_Check_NotReplaced()  // detect.Check（JS）不搜替换
    {
        var biz = new List<BizValue> { new("021", "companyCode") };
        var step = new StepNode { Detect = new DetectCondition { Type = "js", Check = "fillData.code === '021'" } };
        var result = RecordingActionExecutor.ParametrizeByBizValues(step, biz);
        Assert.Equal("fillData.code === '021'", result.Detect!.Check);  // Check 不替换（JS 用 fillData/vars 直接访问）
    }

    [Fact]
    public void ParametrizeByBizValues_CleanupSteps_Recursive()  // CleanupSteps 递归
    {
        var biz = new List<BizValue> { new("021", "companyCode") };
        var step = new StepNode
        {
            CleanupSteps = new List<StepNode> { new() { Selector = "//div[@id='clean_021']" } }
        };
        var result = RecordingActionExecutor.ParametrizeByBizValues(step, biz);
        Assert.Contains("clean_{{companyCode}}", result.CleanupSteps![0].Selector);
    }

    [Fact]
    public void ParametrizeByBizValues_Args_StringElement()  // Args string 元素搜替换
    {
        var biz = new List<BizValue> { new("021", "companyCode") };
        var step = new StepNode { Args = new List<object> { "arg_021", 123, true } };
        var result = RecordingActionExecutor.ParametrizeByBizValues(step, biz);
        Assert.Equal("arg_{{companyCode}}", result.Args![0]);
        Assert.Equal(123, result.Args[1]);  // 非 string 不变
    }

    // ===== E1 ParametrizeByBizValuesOtherFields：非 Selector/Value/Iframe（operate E7 用）=====

    [Fact]
    public void ParametrizeByBizValuesOtherFields_PreservesSelectorValueIframe()  // 不搜替换 Selector/Value/Iframe（caller 对局部变量单独搜替换）
    {
        var biz = new List<BizValue> { new("021", "companyCode") };
        var step = new StepNode
        {
            Selector = "//div[@id='dc_021']",  // 不替换
            Value = "v_021",  // 不替换
            Iframe = new[] { "//iframe[@id='f_021']" },  // 不替换
            Url = "u_021",  // 替换
            Key = "k_021",  // 替换
        };
        var result = RecordingActionExecutor.ParametrizeByBizValuesOtherFields(step, biz);
        Assert.Equal("//div[@id='dc_021']", result.Selector);  // 原样
        Assert.Equal("v_021", result.Value);  // 原样
        Assert.Equal("//iframe[@id='f_021']", result.Iframe![0]);  // 原样
        Assert.Equal("u_{{companyCode}}", result.Url);  // 替换
        Assert.Equal("k_{{companyCode}}", result.Key);  // 替换
    }

    // ===== D-3 BizValue 生产反序列化路径（JsonElement 形态，避免重蹈 loopSource P0/StoreVars 覆辙）=====

    [Fact]
    public void BizValue_Deserialize_From_JsonElement_Array()  // 模拟 GetObject<List<BizValue>> 生产路径
    {
        // 生产链路：AI 工具 arg bizValues 经 OpenAiProvider.ParseJsonElement 为 JsonElement(Array)
        // -> GetObject 调 JsonElement.GetRawText -> JsonSerializer.Deserialize<List<BizValue>>
        var json = "[{\"value\":\"021\",\"field\":\"companyCode\"},{\"value\":\"SQS2026071300003\",\"field\":\"contractNo\"}]";
        var je = JsonDocument.Parse(json).RootElement.Clone();  // JsonElement(Array)
        var rawText = je.GetRawText();  // 模拟 GetObject 的 je.GetRawText()
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var list = JsonSerializer.Deserialize<List<BizValue>>(rawText, options);
        Assert.NotNull(list);
        Assert.Equal(2, list.Count);
        Assert.Equal("021", list[0].Value);
        Assert.Equal("companyCode", list[0].Field);
        Assert.Equal("SQS2026071300003", list[1].Value);
        Assert.Equal("contractNo", list[1].Field);
    }

    [Fact]
    public void BizValue_Deserialize_MissingField_Boundary()  // 缺 value/field 字段边界
    {
        var json = "[{\"value\":\"021\"}]";  // 缺 field
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var list = JsonSerializer.Deserialize<List<BizValue>>(json, options);
        Assert.NotNull(list);
        Assert.Single(list);
        Assert.Equal("021", list![0].Value);
        Assert.Null(list[0].Field);  // 缺 field -> null（BuildBizList 校验空 field -> error）
    }

    [Fact]
    public void BizValue_Deserialize_SingleObject_Boundary()  // 单对象（非数组）边界
    {
        var json = "{\"value\":\"021\",\"field\":\"companyCode\"}";  // 单对象非数组
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        // 单对象反序列化为 List<BizValue> 应抛（生产 GetObject 宽 catch 返 null）
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<List<BizValue>>(json, options));
    }
}
