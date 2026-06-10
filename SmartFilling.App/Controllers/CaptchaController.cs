using Microsoft.AspNetCore.Mvc;
using SmartFilling.Engine.Services;

namespace SmartFilling.App.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CaptchaController : ControllerBase
{
    private readonly CaptchaService _captchaService;

    public CaptchaController(CaptchaService captchaService)
    {
        _captchaService = captchaService;
    }

    [HttpGet("health")]
    public IActionResult Health() => Ok(new { status = "healthy" });

    [HttpPost("recognize")]
    public async Task<IActionResult> Recognize([FromBody] CaptchaRecognizeRequest request)
    {
        return request.Type switch
        {
            "text" when request.Image != null => await RecognizeText(new CaptchaImageRequest(request.Image)),
            "slide" when request.TargetImage != null && request.BackgroundImage != null => await SlideDetect(new SlideCaptchaRequest(request.TargetImage, request.BackgroundImage)),
            _ => BadRequest(new { error = $"不支持的验证码类型: {request.Type}" })
        };
    }

    [HttpPost("recognize/text")]
    public async Task<IActionResult> RecognizeText([FromBody] CaptchaImageRequest request)
    {
        var bytes = Convert.FromBase64String(request.Image);
        var result = await _captchaService.ClassifyAsync(bytes);
        return Ok(new { result });
    }

    [HttpPost("slide/detect")]
    public async Task<IActionResult> SlideDetect([FromBody] SlideCaptchaRequest request)
    {
        var target = Convert.FromBase64String(request.TargetImage);
        var background = Convert.FromBase64String(request.BackgroundImage);
        var offset = await _captchaService.SlideMatchAsync(target, background);
        return Ok(new { offset });
    }

    [HttpPost("click/detect")]
    public async Task<IActionResult> ClickDetect([FromBody] CaptchaImageRequest request)
    {
        var bytes = Convert.FromBase64String(request.Image);
        var result = await _captchaService.DetectClickAsync(bytes);
        return Ok(result);
    }
}

public record CaptchaRecognizeRequest(string Type, string? Image, string? TargetImage, string? BackgroundImage);
public record CaptchaImageRequest(string Image);
public record SlideCaptchaRequest(string TargetImage, string BackgroundImage);
