using Microsoft.Extensions.Options;
using SmartFilling.App.Models;
using SmartFilling.Engine.Models;

namespace SmartFilling.App.Services;

/// <summary>
/// 附件上传管理
/// </summary>
public class AttachmentService
{
    private readonly string _root;          // 附件根目录（UploadRootPath ?? ContentRootPath）——附件相对路径的解析基
    private readonly string _uploadDir;
    private readonly HashSet<string> _allowedExtensions = [".pdf", ".jpg", ".jpeg", ".png", ".doc", ".docx", ".xls", ".xlsx", ".zip", ".rar"];
    private const long MaxFileSize = 50 * 1024 * 1024; // 50MB

    public AttachmentService(IWebHostEnvironment env, IOptions<EngineOptions> engineOptions)
    {
        // 附件根目录统一规则：UploadRootPath 非""/null 用它，否则用 ContentRoot（与 Worker 下载保存/upload 查找一致）
        _root = string.IsNullOrEmpty(engineOptions.Value.UploadRootPath)
            ? env.ContentRootPath
            : engineOptions.Value.UploadRootPath;
        _uploadDir = Path.Combine(_root, "uploads");
        Directory.CreateDirectory(_uploadDir);
    }

    /// <summary>
    /// 把附件相对路径（如 "uploads/xxx.pdf"，含 uploads/ 前缀，与 <see cref="AttachmentInfo.Path"/> 一致）
    /// 解析为绝对路径并校验未逃逸 uploads/ 目录。供 ChatCollector 工具调用读取附件时复用，
    /// 防 AI 返回的 attachment_path 路径遍历读 uploads/ 外文件（激活死代码后成为真实攻击面）。
    /// 与上传落盘同根（_root），避免 UploadRootPath 配置后"上传处/读取处根不一致"找不到文件。
    /// </summary>
    public string ResolveLocalPath(string relativePath)
    {
        // 项5：归一化反斜杠为正斜杠（Linux 不识别 \ 为分隔符，否则 uploads\..\x.pdf 当单文件名通过校验 → 逃逸）；跨平台一致拦 traversal
        relativePath = relativePath.Replace('\\', '/');
        var fullPath = Path.GetFullPath(Path.Combine(_root, relativePath.TrimStart('/')));
        var uploadRoot = Path.GetFullPath(_uploadDir);
        if (!fullPath.StartsWith(uploadRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"非法附件路径: {relativePath}");
        return fullPath;
    }

    public async Task<AttachmentInfo> UploadAsync(IFormFile file)
    {
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!_allowedExtensions.Contains(ext))
            throw new InvalidOperationException($"不支持的文件类型: {ext}");

        if (file.Length > MaxFileSize)
            throw new InvalidOperationException("文件大小超过 50MB 限制");

        var id = Guid.NewGuid().ToString("N")[..8];
        // F7：净化文件名（剥离路径段）+ 跨平台兜底校验，防 ../../evil.pdf 逃逸 uploads/ 写任意文件
        var fileName = $"{id}_{Path.GetFileName(file.FileName)}";
        var path = EnsureWithinUploadDir(fileName);

        using var stream = new FileStream(path, FileMode.Create);
        await file.CopyToAsync(stream);

        return new AttachmentInfo
        {
            Id = id,
            Name = file.FileName,
            Size = file.Length,
            Path = $"uploads/{fileName}",
            Url = $"uploads/{fileName}"
        };
    }

    public async Task<List<AttachmentInfo>> UploadBatchAsync(List<IFormFile> files)
    {
        var results = new List<AttachmentInfo>();
        foreach (var file in files)
            results.Add(await UploadAsync(file));
        return results;
    }

    public bool Delete(string fileName)
    {
        // F7：净化 + 跨平台兜底校验，防 path traversal 任意删除 uploads/ 外文件
        var safeName = Path.GetFileName(fileName);
        var path = EnsureWithinUploadDir(safeName);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    /// <summary>
    /// F7(R5-1)：把文件名解析为 uploadDir 内的绝对路径并校验未逃逸。
    /// 跨平台必需：GetFileName 在 Linux 不识别 \ 为分隔符，单靠它拦不住 ..\..\evil.pdf，
    /// 需 GetFullPath + StartsWith 兜底防 ../.. 目录跳转逃逸。
    /// </summary>
    private string EnsureWithinUploadDir(string fileName)
    {
        var root = Path.GetFullPath(_uploadDir);
        var fullPath = Path.GetFullPath(Path.Combine(_uploadDir, fileName));
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"非法文件路径: {fileName}");
        return fullPath;
    }
}
