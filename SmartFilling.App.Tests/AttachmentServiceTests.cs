using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using SmartFilling.App.Services;
using SmartFilling.Engine.Models;

namespace SmartFilling.App.Tests;

/// <summary>
/// AttachmentService.ResolveLocalPath 单测（2026-07-06 附件随消息发送改造新增）。
/// 此方法承载 AI 工具调用传入的任意 attachment_path 的路径遍历防御（激活死代码后成真实攻击面），
/// 是安全敏感 public 方法，必须有回归守护（test-covers-prod-path + silent-success：防御块写错会 silent 放行）。
/// 验合法 uploads/x 通过；../、绝对路径、裸文件名（无 uploads/ 前缀）均抛 InvalidOperationException。
/// </summary>
public class AttachmentServiceTests
{
    // 用唯一临时根避免并发/残留；uploadRoot=null 模拟"UploadRootPath 为空 → 用 ContentRootPath"生产默认。
    private static (AttachmentService svc, string root) Create()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "sf_att_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempRoot);
        var envMock = Substitute.For<IWebHostEnvironment>();
        envMock.ContentRootPath.Returns(tempRoot);
        var opts = Options.Create(new EngineOptions { UploadRootPath = "" });
        var svc = new AttachmentService(envMock, opts);
        return (svc, tempRoot);  // _root = tempRoot（UploadRootPath 空）
    }

    [Fact]
    public void ResolveLocalPath_ValidUploadsPath_ResolvesAbsolute()
    {
        var (svc, root) = Create();
        var expected = Path.GetFullPath(Path.Combine(root, "uploads", "a.pdf"));

        var actual = svc.ResolveLocalPath("uploads/a.pdf");

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ResolveLocalPath_ParentTraversal_Throws()
    {
        // uploads/../evil.pdf → _root/evil.pdf 逃出 uploads/
        var (svc, _) = Create();

        Assert.Throws<InvalidOperationException>(() => svc.ResolveLocalPath("uploads/../evil.pdf"));
    }

    [Fact]
    public void ResolveLocalPath_RelativeOutsideUploads_Throws()
    {
        var (svc, _) = Create();

        Assert.Throws<InvalidOperationException>(() => svc.ResolveLocalPath("../etc/passwd"));
    }

    [Fact]
    public void ResolveLocalPath_BareFileName_NoUploadsPrefix_Throws()
    {
        // 裸文件名（无 uploads/ 前缀）→ _root/x.pdf 不在 _root/uploads/ 下 → 拒
        var (svc, _) = Create();

        Assert.Throws<InvalidOperationException>(() => svc.ResolveLocalPath("x.pdf"));
    }

    [Fact]
    public void ResolveLocalPath_AbsolutePath_Throws()
    {
        // 绝对路径：Path.Combine(_root, absolute) 忽略 _root → 直接绝对路径，不在 uploads/ 下 → 拒
        var (svc, _) = Create();
        var abs = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "evil.pdf"));

        Assert.Throws<InvalidOperationException>(() => svc.ResolveLocalPath(abs));
    }

    [Fact]
    public void ResolveLocalPath_LeadingSlash_Normalized()
    {
        // 前导 / 被 TrimStart('/') 处理后仍须在 uploads/ 下（/uploads/x.pdf → uploads/x.pdf）
        var (svc, root) = Create();
        var expected = Path.GetFullPath(Path.Combine(root, "uploads", "x.pdf"));

        var actual = svc.ResolveLocalPath("/uploads/x.pdf");

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ResolveLocalPath_EmptyString_Throws()
    {
        // 空串 → _root 本身，不在 _root/uploads/ 下 → 拒（平台无关）
        var (svc, _) = Create();

        Assert.Throws<InvalidOperationException>(() => svc.ResolveLocalPath(""));
    }

    [Fact]
    public void ResolveLocalPath_UrlEncodedSlash_Throws()
    {
        // URL 编码 %2F 不解码为分隔符，视为字面字符 → "uploads%2F..." 不在 uploads/ 子目录下 → 拒（平台无关）
        var (svc, _) = Create();

        Assert.Throws<InvalidOperationException>(() => svc.ResolveLocalPath("uploads%2F..%2Fevil.pdf"));
    }

    [Fact]
    public void ResolveLocalPath_BackslashTraversal_NormalizedAndThrows()
    {
        // 项5：Windows 风格反斜杠混用（uploads\..\x.pdf）经 Replace('\\','/') 归一化为 uploads/../x.pdf → 逃逸被拦（跨平台稳定，Linux 也识别）
        var (svc, _) = Create();

        Assert.Throws<InvalidOperationException>(() => svc.ResolveLocalPath("uploads\\..\\x.pdf"));
    }
}
