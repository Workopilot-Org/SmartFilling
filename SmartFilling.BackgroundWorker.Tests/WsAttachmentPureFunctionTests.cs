using System.Text.Json;
using SmartFilling.Engine.Engine;
using Xunit;

namespace SmartFilling.BackgroundWorker.Tests;

/// <summary>
/// code-msg 计划第二部分 ISSUE-C 层1：WS 附件链路纯函数单测。
/// DownloadAttachmentsInFillDataAsync 内部 new HttpClient 无注入点不可 stub（靠联调），故层1 只测两个 public static 纯函数：
/// ① NormalizeJsonElement（WS payload.Params file 字段 → fillData 形态，TraverseAndDownloadAsync 可识别）
/// ② ResolveUploadValue（附件数组含 path → upload 出口取 path）
/// 覆盖 WS 特有风险点（NormalizeJsonElement 转换后形态，HTTP 经 ObjectToClrConverter 不同）。
/// </summary>
public class WsAttachmentPureFunctionTests
{
    [Fact]
    public void NormalizeJsonElement_FileArray_ProducesListObjectOfDict()
    {
        // WS payload.Params["file"] 是 JsonElement(Array)，经 NormalizeJsonElement 转 CLR：
        // Array→List<object>，元素 Object→Dictionary<string,object> 含 url 键。
        // TraverseAndDownloadAsync 命中 List<object> 分支（AttachmentService:98）回填 listItem["path"]——
        // 不落只读的 JsonElement 分支（:123，该分支只读不写，附件 path 不回填 = upload silent 跳过）。
        using var doc = JsonDocument.Parse("""[{"name":"a.pdf","url":"http://x/a.pdf"}]""");
        var normalized = VariableHelper.NormalizeJsonElement(doc.RootElement);

        var list = Assert.IsType<List<object>>(normalized);
        Assert.Single(list);
        var item = Assert.IsType<Dictionary<string, object>>(list[0]);
        Assert.True(item.ContainsKey("url"));  // TraverseAndDownloadAsync 识别附件对象的关键键
        Assert.Equal("http://x/a.pdf", item["url"]);
        Assert.Equal("a.pdf", item["name"]);
    }

    [Fact]
    public void ResolveUploadValue_AttachmentsWithPath_ReturnsLocalPaths()
    {
        // upload 出口（StepExecutor:299 → VariableHelper.ResolveUploadValue）：附件数组（Dictionary 含 path）→ 返回本地路径数组。
        // 验值：path 回填后 upload 能取到（path 空则 silent 跳过——此用例 path 非空应返回）。
        var attachments = new List<object>
        {
            new Dictionary<string, object> { ["name"] = "a.pdf", ["url"] = "http://x/a.pdf", ["path"] = "uploads/abc_a.pdf" },
            new Dictionary<string, object> { ["name"] = "b.pdf", ["url"] = "http://x/b.pdf", ["path"] = "uploads/def_b.pdf" }
        };

        var paths = VariableHelper.ResolveUploadValue(attachments, @"C:\app\root");

        Assert.Equal(2, paths.Length);
        Assert.Contains("abc_a.pdf", paths[0]);
        Assert.Contains("def_b.pdf", paths[1]);
        // ResolveFilePath 对相对路径拼 rootPath（uploads/xxx → rootPath/uploads/xxx）
        Assert.True(Path.IsPathRooted(paths[0]));  // 验值：相对 path 拼成绝对路径，upload 能直接用
    }

    [Fact]
    public void ResolveUploadValue_EmptyPathList_FiltersOut()
    {
        // path 为空（附件未下载）→ 被过滤掉（silent 跳过该附件，不崩）。验值：返回空数组非 null。
        var attachments = new List<object>
        {
            new Dictionary<string, object> { ["name"] = "a.pdf", ["url"] = "http://x/a.pdf" }  // 无 path
        };

        var paths = VariableHelper.ResolveUploadValue(attachments, @"C:\app\root");

        Assert.Empty(paths);  // 未下载附件被过滤，upload 静默跳过（不崩不报错）
    }
}
