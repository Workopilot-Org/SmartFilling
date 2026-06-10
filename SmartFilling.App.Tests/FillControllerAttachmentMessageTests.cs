using SmartFilling.App.Controllers;
using SmartFilling.App.Models;
using System.Text.Json;

namespace SmartFilling.App.Tests;

/// <summary>
/// 附件随消息发送改造（2026-07-06）：FillController.BuildAttachmentUserMessage 纯函数单测。
/// 生产链路：前端 sendChat → /api/fill/chat（ChatRequest.Attachments）→ 带附件即 BuildAttachmentUserMessage
/// 组合"附件信息块 + 用户文字" → 注入 UserChatMessage → AI 感知附件（文件名/URL）+ 用户填报意图。
/// 决策：消息优先（用户消息 > 数据处理提示词 > 字段定义），空消息允许发送（仅附件块）。
/// 断言验值不只验状态（test-covers-prod-path）：验附件名/URL、"用户说："前缀有无、三级优先级措辞落地。
/// 项4（2026-07-07）：去 path 只留 url（AttachmentInfo.Path==Url 同值），全部 8 测试补 Assert.DoesNotContain("路径：")
/// 守护 path 标签彻底移除（防 Contains("uploads/...") 因 path==url 同值 silent 通过，测不出 path 回归）。
/// 含经 JsonSerializer.Deserialize&lt;ChatRequest&gt; 的生产反序列化路径用例（锁大小写不敏感映射 + 字段值注入）。
/// </summary>
public class FillControllerAttachmentMessageTests
{
    private static List<AttachmentInfo> Attachments(params (string name, string path, string url)[] items)
        => items.Select(i => new AttachmentInfo { Name = i.name, Path = i.path, Url = i.url }).ToList();

    [Fact]
    public void WithAttachments_AndMessage_CombinesAttachmentBlockAndUserText()
    {
        var atts = Attachments(("发票.pdf", "uploads/a1_发票.pdf", "uploads/a1_发票.pdf"));

        var result = FillController.BuildAttachmentUserMessage("帮我提取金额", atts);

        Assert.Contains("用户上传了以下附件：", result);
        Assert.Contains("发票.pdf", result);
        Assert.Contains("uploads/a1_发票.pdf", result);
        Assert.Contains("用户说：帮我提取金额", result);
        Assert.DoesNotContain("路径：", result);  // 项4 守护：path 标签彻底移除（防 Contains 同值 silent 通过）
    }

    [Fact]
    public void WithAttachments_NullMessage_OnlyAttachmentBlock_NoUserSays()
    {
        // 决策：空消息允许发送（仅附件）—— 不附加"用户说："行
        var atts = Attachments(("a.pdf", "uploads/a.pdf", "uploads/a.pdf"));

        var result = FillController.BuildAttachmentUserMessage(null, atts);

        Assert.Contains("用户上传了以下附件：", result);
        Assert.Contains("a.pdf", result);
        Assert.DoesNotContain("用户说：", result);
        Assert.DoesNotContain("路径：", result);  // 项4 守护
    }

    [Fact]
    public void WithAttachments_EmptyMessage_OnlyAttachmentBlock()
    {
        var atts = Attachments(("a.pdf", "uploads/a.pdf", "uploads/a.pdf"));

        var result = FillController.BuildAttachmentUserMessage("", atts);

        Assert.Contains("用户上传了以下附件：", result);
        Assert.DoesNotContain("用户说：", result);
        Assert.DoesNotContain("路径：", result);  // 项4 守护
    }

    [Fact]
    public void NullAttachments_ReturnsOriginalMessage()
    {
        var result = FillController.BuildAttachmentUserMessage("你好", null);
        Assert.Equal("你好", result);
        Assert.DoesNotContain("路径：", result);  // 项4 守护：无附件返回原消息（原消息不含"路径："）
    }

    [Fact]
    public void EmptyAttachments_ReturnsOriginalMessage()
    {
        var result = FillController.BuildAttachmentUserMessage("你好", new List<AttachmentInfo>());
        Assert.Equal("你好", result);
        Assert.DoesNotContain("路径：", result);  // 项4 守护
    }

    [Fact]
    public void MultipleAttachments_AllListed()
    {
        var atts = Attachments(
            ("a.pdf", "uploads/a.pdf", "uploads/a.pdf"),
            ("b.xlsx", "uploads/b.xlsx", "uploads/b.xlsx"));

        var result = FillController.BuildAttachmentUserMessage("处理这两个", atts);

        Assert.Contains("a.pdf", result);
        Assert.Contains("b.xlsx", result);
        Assert.Contains("用户说：处理这两个", result);
        Assert.DoesNotContain("路径：", result);  // 项4 守护
    }

    [Fact]
    public void Attachments_FromProductionJsonDeserialize_AllFieldsInjected()
    {
        // 生产链路：前端 JSON → ChatRequest.Attachments（ASP.NET 反序列化，大小写不敏感）→ BuildAttachmentUserMessage。
        // 走生产反序列化形态（非纯 C# 强类型构造），锁小写 json key → 大写 AttachmentInfo 属性映射 + 字段值注入。
        var json = """{"attachments":[{"name":"合同.pdf","path":"uploads/c1_合同.pdf","url":"uploads/c1_合同.pdf"}]}""";
        var req = JsonSerializer.Deserialize<ChatRequest>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var result = FillController.BuildAttachmentUserMessage("分类这张合同", req.Attachments);

        Assert.NotNull(req.Attachments);
        Assert.Single(req.Attachments);
        Assert.Contains("合同.pdf", result);
        Assert.Contains("uploads/c1_合同.pdf", result);
        Assert.Contains("用户说：分类这张合同", result);
        Assert.DoesNotContain("路径：", result);  // 项4 守护
    }

    [Fact]
    public void Result_Includes_MessagePriorityGuidance()
    {
        // v1 风格中性措辞落地：注入文本含"用户本轮消息" + "判断是否需要分类或提取"（让 AI 按提示词判断是否调工具，不硬编码工具名）
        var atts = Attachments(("a.pdf", "uploads/a.pdf", "uploads/a.pdf"));

        var result = FillController.BuildAttachmentUserMessage("提取", atts);

        Assert.Contains("用户本轮消息", result);
        Assert.Contains("判断是否需要对附件进行分类或提取数据", result);
        Assert.DoesNotContain("用 classify/extract 工具", result);  // v1 风格：注入文字不提工具名（防诱导 AI 擅自调工具）
        Assert.DoesNotContain("路径：", result);  // 项4 守护
    }
}
