using Microsoft.AspNetCore.Mvc;
using SmartFilling.App.Models;
using SmartFilling.App.Services;
using SmartFilling.Engine.Models;

namespace SmartFilling.App.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ScriptController : ControllerBase
{
    private readonly ScriptService _scriptService;

    public ScriptController(ScriptService scriptService) => _scriptService = scriptService;

    [HttpGet("list")]
    public ActionResult<List<ScriptListItem>> List() => Ok(_scriptService.GetAllScripts());

    [HttpGet("{scriptId}")]
    public ActionResult<ScriptV2> Get(string scriptId)
    {
        var script = _scriptService.GetScript(scriptId);
        return script == null ? NotFound() : Ok(script);
    }

    [HttpPost("save")]
    public ActionResult Save([FromBody] ScriptV2 script)
    {
        // 用户决策 2（2026-07-01）：ScriptController.Save 不加 forceSave 入口——前端只用 /api/record/save（已支持 forceSave），
        // /api/script/save 无消费方；仅保留 catch→400 防御性（body 对齐 RecordController 保持一致）。
        try
        {
            _scriptService.SaveScript(script);
        }
        catch (ScriptValidationException ex)
        {
            return BadRequest(new { validationFailed = true, error = "脚本校验失败", errors = ex.Errors });
        }
        return Ok(new { scriptId = script.ScriptId });
    }

    [HttpDelete("{scriptId}")]
    public ActionResult Delete(string scriptId)
    {
        _scriptService.DeleteScript(scriptId);
        return Ok();
    }
}
