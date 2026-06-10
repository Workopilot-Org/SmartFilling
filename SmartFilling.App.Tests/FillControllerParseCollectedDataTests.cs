using SmartFilling.App.Controllers;
using System.Text.Json;

namespace SmartFilling.App.Tests;

/// <summary>
/// 决策 D1/方案2：FillController.ParseCollectedData 集成层单测（锁 D1.5 不拍平）。
/// 生产链路：AI 经 chat 产出 data 对象（含附件对象数组/明细表 items/嵌套对象）→ ParseCollectedData 用 NormalizeJsonElement 保持结构
/// （Object→Dictionary/Array→List<object>/String→CLR string）→ 回传 start-dynamic → ConvertAttachmentUrls/GetLoopRows 消费。
/// 关键 silent-success 防御：非 string 值若被 GetRawText 拍平成字符串 → 下游 is 判断全 miss（URL 不转/loop 0 迭代），任务仍 Completed 不报错。
/// 本测试经 JsonDocument.Parse 喂 JSON（生产反序列化形态），断言值/结构（不只验状态），锁 D1.5 修复不回归。
/// 注：方案2 是方法层测试（不测 Chat 是否调 ParseCollectedData，靠"提取为唯一解析入口"+ 代码审查保证）。
/// </summary>
public class FillControllerParseCollectedDataTests
{
    private static JsonElement? Parse(string json)
    {
        // 经 JsonDocument.Parse 构造（生产反序列化形态；dataEl 是 AI 返回的 data 对象的 JsonElement）
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();  // Clone 脱离 doc 生命周期，等价生产 chat 解析的 dataEl
    }

    [Fact]
    public void ParseCollectedData_ObjectArray_NotFlattened_LocksD15()
    {
        // 🔴 核心：对象数组（附件对象/明细表 items）必须保持 List<object> 结构，**非拍平 string**（锁 D1.5）。
        // 拍平后果：ConvertAttachmentUrls `is List<object>` miss → URL 不转 → Worker 不下载；GetLoopRows miss → loop 0 迭代。
        var dataEl = Parse("""{"items":[{"name":"a","qty":1},{"name":"b","qty":2}]}""");

        var dict = FillController.ParseCollectedData(dataEl);

        Assert.NotNull(dict);
        var items = dict!["items"];
        Assert.IsType<List<object>>(items);   // 非 string（拍平 = string，silent-success）
        var arr = (List<object>)items;
        Assert.Equal(2, arr.Count);
        Assert.IsType<Dictionary<string, object>>(arr[0]);  // 元素是 Dictionary（非 string/非拍平）
        Assert.Equal("a", ((Dictionary<string, object>)arr[0])["name"]);
    }

    [Fact]
    public void ParseCollectedData_StringValue_ReturnsClrString()
    {
        var dataEl = Parse("""{"k":"v"}""");

        var dict = FillController.ParseCollectedData(dataEl);

        Assert.NotNull(dict);
        Assert.Equal("v", dict!["k"]);  // string 值返回 CLR string（无引号）
    }

    [Fact]
    public void ParseCollectedData_Null_ReturnsNull()
    {
        var dict = FillController.ParseCollectedData(null);
        Assert.Null(dict);
    }

    [Fact]
    public void ParseCollectedData_NonObjectJson_ReturnsNull()
    {
        // 非 object（如顶层是 array）→ 返回 null（data 必须是 object 才有意义）
        var dataEl = Parse("[1,2,3]");

        var dict = FillController.ParseCollectedData(dataEl);

        Assert.Null(dict);
    }

    [Fact]
    public void ParseCollectedData_NestedObject_PreservesDictionaryStructure()
    {
        // 嵌套对象 → 递归保持 Dictionary 结构（NormalizeJsonElement Object 分支）
        var dataEl = Parse("""{"o":{"k":"v"}}""");

        var dict = FillController.ParseCollectedData(dataEl);

        Assert.NotNull(dict);
        var nested = dict!["o"];
        Assert.IsType<Dictionary<string, object>>(nested);
        Assert.Equal("v", ((Dictionary<string, object>)nested)["k"]);
    }
}
