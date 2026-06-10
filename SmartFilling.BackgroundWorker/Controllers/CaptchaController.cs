using Microsoft.AspNetCore.Mvc;
using SmartFilling.Engine.Services;
using EngineILogger = SmartFilling.Engine.Logging.ILogger;

namespace SmartFilling.BackgroundWorker.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CaptchaController : ControllerBase
{
    private readonly CaptchaService _captchaService;
    private readonly EngineILogger _logger;

    public CaptchaController(CaptchaService captchaService, EngineILogger logger)
    {
        _captchaService = captchaService;
        _logger = logger;
    }

    [HttpGet("health")]
    public IActionResult Health() => Ok(new { status = "healthy" });

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
}

public record CaptchaImageRequest(string Image);
public record SlideCaptchaRequest(string TargetImage, string BackgroundImage);
