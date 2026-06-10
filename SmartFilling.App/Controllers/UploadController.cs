using Microsoft.AspNetCore.Mvc;
using SmartFilling.App.Services;

namespace SmartFilling.App.Controllers;

[ApiController]
[Route("api/[controller]")]
public class UploadController : ControllerBase
{
    private readonly AttachmentService _attachmentService;

    public UploadController(AttachmentService attachmentService) => _attachmentService = attachmentService;

    [HttpPost]
    public async Task<ActionResult> Upload(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "未选择文件" });
        var info = await _attachmentService.UploadAsync(file);
        return Ok(info);
    }

    [HttpPost("batch")]
    public async Task<ActionResult> UploadBatch(List<IFormFile> files)
    {
        if (files == null || files.Count == 0)
            return BadRequest(new { error = "未选择文件" });
        var infos = await _attachmentService.UploadBatchAsync(files);
        return Ok(infos);
    }

    [HttpDelete]
    public ActionResult Delete([FromBody] DeleteFileRequest request)
    {
        var deleted = _attachmentService.Delete(request.FileName);
        return deleted ? Ok() : NotFound();
    }
}

public class DeleteFileRequest
{
    public string FileName { get; set; } = "";
}
