using SmartFilling.App.Services;
using SmartFilling.Engine.Engine;
using System.Text.Json;

namespace SmartFilling.App.Tests;

/// <summary>
/// 决策3 App.Tests：WorkerApiClient.ConvertAttachmentUrls 三分支（验批次7 附件链路 url 转换 + P15/P26 silent-success 防御）。
/// 生产链路：对话收集 D1 产附件对象数组 → start-dynamic NormalizeJsonElement 保持结构 → ConvertAttachmentUrls 转 url 绝对 → Worker 下载。
/// 若对象数组被 GetRawText 拍平成 string（D1.5 修复前），string 不命中 List<object> 分支 → url 不转 → Worker 不下载（silent-success）。
/// </summary>
public class WorkerApiClientTests
{
    [Fact]
    public void ConvertAttachmentUrls_ObjectArray_RelativeToAbsolute()
    {
        var data = new Dictionary<string, object>
        {
            ["fileField"] = new List<object>
            {
                new Dictionary<string, object> { ["name"] = "a.pdf", ["url"] = "uploads/a.pdf" },
                new Dictionary<string, object> { ["name"] = "b.pdf", ["url"] = "uploads/b.pdf" }
            }
        };

        WorkerApiClient.ConvertAttachmentUrls(data, "http://app.example.com");

        var arr = (List<object>)data["fileField"];
        Assert.Equal("http://app.example.com/uploads/a.pdf", ((Dictionary<string, object>)arr[0])["url"]);
        Assert.Equal("http://app.example.com/uploads/b.pdf", ((Dictionary<string, object>)arr[1])["url"]);
    }

    [Fact]
    public void ConvertAttachmentUrls_AlreadyAbsolute_KeepsAsIs()
    {
        var data = new Dictionary<string, object>
        {
            ["fileField"] = new List<object>
            {
                new Dictionary<string, object> { ["name"] = "a.pdf", ["url"] = "http://cdn.example.com/a.pdf" }
            }
        };

        WorkerApiClient.ConvertAttachmentUrls(data, "http://app.example.com");

        var arr = (List<object>)data["fileField"];
        Assert.Equal("http://cdn.example.com/a.pdf", ((Dictionary<string, object>)arr[0])["url"]);  // 已绝对不重复转
    }

    [Fact]
    public void ConvertAttachmentUrls_StringValue_SilentMiss()
    {
        // P15/P26 silent-success 防御：对象数组若被 GetRawText 拍平成 string，string 不命中 List<object> 分支 → url 不转 → Worker 不下载。
        // 这正是 D1.5（NormalizeJsonElement 替 GetRawText）要避免的——本测试锁定"拍平后果"，证明结构保持的必要性。
        var data = new Dictionary<string, object>
        {
            ["fileField"] = "[{\"name\":\"a.pdf\",\"url\":\"uploads/a.pdf\"}]"  // 拍平成 string
        };

        WorkerApiClient.ConvertAttachmentUrls(data, "http://app.example.com");

        Assert.Equal("[{\"name\":\"a.pdf\",\"url\":\"uploads/a.pdf\"}]", data["fileField"]);  // string 原样未转
    }

    [Fact]
    public void ConvertAttachmentUrls_EmptyBaseUrl_NoOp()
    {
        var data = new Dictionary<string, object>
        {
            ["fileField"] = new List<object>
            {
                new Dictionary<string, object> { ["name"] = "a.pdf", ["url"] = "uploads/a.pdf" }
            }
        };

        WorkerApiClient.ConvertAttachmentUrls(data, "");

        var arr = (List<object>)data["fileField"];
        Assert.Equal("uploads/a.pdf", ((Dictionary<string, object>)arr[0])["url"]);  // baseUrl 空不转
    }

    [Fact]
    public void ConvertAttachmentUrls_NestedObject_Recursive()
    {
        var data = new Dictionary<string, object>
        {
            ["outer"] = new Dictionary<string, object>
            {
                ["fileField"] = new List<object>
                {
                    new Dictionary<string, object> { ["name"] = "a.pdf", ["url"] = "uploads/a.pdf" }
                }
            }
        };

        WorkerApiClient.ConvertAttachmentUrls(data, "http://app.example.com");

        var outer = (Dictionary<string, object>)data["outer"];
        var arr = (List<object>)outer["fileField"];
        Assert.Equal("http://app.example.com/uploads/a.pdf", ((Dictionary<string, object>)arr[0])["url"]);
    }

    [Fact]
    public void ConvertAttachmentUrls_ListOfStrings_NotAttachment_NoChange()
    {
        // 字符串数组（非附件对象数组）：元素非 Dictionary → 不处理（如 fillData 的字符串数组 filePath）
        var data = new Dictionary<string, object>
        {
            ["paths"] = new List<object> { "uploads/a.pdf", "uploads/b.pdf" }
        };

        WorkerApiClient.ConvertAttachmentUrls(data, "http://app.example.com");

        var arr = (List<object>)data["paths"];
        Assert.Equal("uploads/a.pdf", arr[0]);  // 字符串数组元素不转（非附件对象）
        Assert.Equal("uploads/b.pdf", arr[1]);
    }

    [Fact]
    public void ConvertAttachmentUrls_ProductionPath_JsonNormalizeThenConvert()
    {
        // C⑧（test-covers-production-deserialize-path）：走生产反序列化形态。
        // 生产链路 FillController.StartDynamic L368-370：前端 JSON → FillData(JsonElement 值) → NormalizeJsonElement 保持结构
        // （Object→Dictionary/Array→List<object>/String→CLR string）→ ConvertAttachmentUrls 转 url 绝对 → Worker 下载。
        // 本测试用 JsonDocument.Parse（生产反序列化）+ NormalizeJsonElement（生产归一化）构造 fillData，
        // 不直接 new List<object>/Dictionary（理想化已归一化形态），验对象数组经真实变换链后 ConvertAttachmentUrls 仍正确转 url。
        using var doc = JsonDocument.Parse("""{"fileField":[{"name":"a.pdf","url":"uploads/a.pdf"},{"name":"b.pdf","url":"uploads/b.pdf"}]}""");
        var fillData = new Dictionary<string, object>();
        foreach (var p in doc.RootElement.EnumerateObject())
            fillData[p.Name] = VariableHelper.NormalizeJsonElement(p.Value);  // 复现 FillController L370 生产归一化

        WorkerApiClient.ConvertAttachmentUrls(fillData, "http://app.example.com");

        var arr = (List<object>)fillData["fileField"];  // NormalizeJsonElement(Array) 产 List<object>（生产形态）
        Assert.Equal("http://app.example.com/uploads/a.pdf", ((Dictionary<string, object>)arr[0])["url"]);
        Assert.Equal("http://app.example.com/uploads/b.pdf", ((Dictionary<string, object>)arr[1])["url"]);
    }
}
