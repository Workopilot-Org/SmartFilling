using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using SmartFilling.App.Configuration;
using SmartFilling.App.Hubs;
using SmartFilling.App.Models;
using SmartFilling.App.Recording;
using SmartFilling.App.Services;
using SmartFilling.Engine.Ai;
using SmartFilling.Engine.Models;
using EngineILogger = SmartFilling.Engine.Logging.ILogger;

namespace SmartFilling.App.Controllers;

[ApiController]
[Route("api/[controller]")]
public class RecordController : ControllerBase
{
    // F1(R3-1)：跨 HTTP 请求线程并发读写（start/stop/respond/save 多端点），普通 Dictionary 非线程安全，
    // 多用户并发录制可致内部数组损坏。改 ConcurrentDictionary，与下方 _taskCancellations 一致。
    private static readonly ConcurrentDictionary<string, RecordingEngine> _activeRecordings = new();
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> _taskCancellations = new();
    private readonly IServiceProvider _sp;
    private readonly ILogger<RecordController> _logger;
    private readonly EngineOptions _engineOptions;
    private readonly AppOptions _appOptions;

    public RecordController(IServiceProvider sp, ILogger<RecordController> logger, IOptions<EngineOptions> engineOptions, IOptions<AppOptions> appOptions)
    {
        _sp = sp;
        _logger = logger;
        _engineOptions = engineOptions.Value;
        _appOptions = appOptions.Value;
    }

    [HttpPost("start")]
    public async Task<ActionResult> Start([FromBody] StartRecordRequest request)
    {
        var taskId = Guid.NewGuid().ToString("N")[..8];

        var engineLogger = _sp.GetRequiredService<EngineILogger>();
        var scriptService = _sp.GetRequiredService<ScriptService>();
        var aiProvider = _sp.GetRequiredService<IAiProvider>();

        var engine = new RecordingEngine(aiProvider, scriptService, engineLogger, _engineOptions, _appOptions, request.Attachments);  // 批次7-C2：透传 attachments 给录制引擎

        // D1（2026-06-30 用户决策）：删除"启动录制时读取已有关联脚本 fields 喂给 AI"逻辑——无设计背书（fields 是录制产物，非输入；
        // 三文档 firsthand 查证：接口表未提读 fields，设计理念相反）。首次录制 fields=null 正常证明非必需，疑 v1→v2 迁移遗留。

        // 事件订阅
        var hubContext = _sp.GetRequiredService<IHubContext<AgentHub>>();
        engine.OnLog += async (tid, msg) => await hubContext.SendLog(taskId, msg);
        engine.OnScreenshot += async (tid, img) => await hubContext.SendScreenshot(taskId, img);
        engine.OnRequestHelp += async (tid, q, img) => await hubContext.RequestHelp(taskId, q, img);

        _activeRecordings[taskId] = engine;

        // 后台启动录制
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(_engineOptions.MaxScriptDuration));
        _taskCancellations[taskId] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                var script = await engine.RecordAsync(
                    request.DocumentTypeId,
                    request.TaskDescription,
                    request.PrerequisiteScriptId,
                    headless: _engineOptions.Headless,
                    userResponse: null,
                    ct: cts.Token,
                    username: request.Username,
                    password: request.Password);

                // 录制正常完成：单信号模型对齐填报（result=Success/script/无 errorMessage）
                await hubContext.TaskCompleted(taskId, "Success", script);
            }
            catch (RecordingCancelledException ex)
            {
                // 录制取消（总超时/手动停止/maxTurns 耗尽）：发 TaskCompleted + Cancelled + 区分原因的 errorMessage（保留已录步骤可保存）。
                // 原 catch(OperationCanceledException) 是死代码（RecordAsync 不抛 OCE，改抛 RecordingCancelledException），现已替换。
                await hubContext.TaskCompleted(taskId, "Cancelled", engine.GetCurrentScript(), ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "录制异常: {TaskId}", taskId);
                // 异常（含浏览器崩溃）：发 TaskCompleted + Failed + errorMessage
                await hubContext.TaskCompleted(taskId, "Failed", engine.GetCurrentScript(), ex.Message);
            }
            finally
            {
                try { await engine.StopAsync(); } catch { }
                _activeRecordings.TryRemove(taskId, out _);
                _taskCancellations.TryRemove(taskId, out _);
            }
        });

        return Ok(new { taskId });
    }

    [HttpPost("respond")]
    public ActionResult Respond([FromBody] UserResponseRequest request)
    {
        if (!_activeRecordings.TryGetValue(request.TaskId, out var engine))
            return NotFound(new { error = "录制任务不存在" });

        var accepted = engine.ReplyHelp(request.Response);
        if (!accepted)
            return Conflict(new { error = "AI 当前未请求帮助，或该请求已结束" });

        return Ok(new { message = "已发送用户回复" });
    }

    [HttpPost("stop")]
    public ActionResult Stop([FromBody] StopRequest request)
    {
        if (!_activeRecordings.TryGetValue(request.TaskId, out var engine))
            return NotFound(new { error = "录制任务不存在" });

        // 先取消录制循环（MarkManualCancel 必须在 Cancel 前，确保 RecordAsync catch(OCE) 能读到 _cancelReason 判据区分手动取消 vs 总超时）
        if (_taskCancellations.TryRemove(request.TaskId, out var cts))
        {
            engine.MarkManualCancel("用户取消录制");
            cts.Cancel();
        }

        var script = engine.GetCurrentScript();
        // 不在此 StopAsync（2026-06-29 竞态 A 修复）：cts.Cancel 后立即关浏览器会与 RecordAsync 正在执行的 operate Playwright 操作（不传 ct）竞态——
        // operate 抛 "Target closed"→IsBrowserCrashException→Failed（而非 Cancelled）。浏览器改由 Task.Run finally（RecordAsync 取消处理后，:101）关闭。
        // Stop 端点返回的 script 来自 GetCurrentScript（读内存 _phases），不依赖浏览器。
        return Ok(new { scriptId = script.ScriptId, stepCount = CountAllSteps(script.Phases) });  // #36：递归计数所有 step（含嵌套 phase；原仅取首个 phase 非递归）
    }

    /// <summary>#36：递归统计脚本所有 step（含嵌套 phase 内的 step）</summary>
    private static int CountAllSteps(List<Engine.Models.PhaseItem> items)
    {
        var count = 0;
        foreach (var item in items)
        {
            if (item is Engine.Models.StepNode) count++;
            else if (item is Engine.Models.PhaseNode p && p.Steps.Count > 0) count += CountAllSteps(p.Steps);
        }
        return count;
    }

    [HttpPost("save")]
    public ActionResult Save([FromBody] SaveScriptRequest request)
    {
        ScriptV2? script = null;

        if (request.Script != null)
        {
            script = request.Script;
        }
        else if (_activeRecordings.TryGetValue(request.TaskId, out var engine))
        {
            script = engine.GetCurrentScript();
        }

        if (script == null)
            return NotFound(new { error = "录制任务不存在或已完成" });

        if (!string.IsNullOrEmpty(request.Name)) script = script with { Name = request.Name };
        if (!string.IsNullOrEmpty(request.DocumentTypeId)) script = script with { DocumentTypeId = request.DocumentTypeId };

        var scriptService = _sp.GetRequiredService<ScriptService>();
        try
        {
            scriptService.SaveScript(script, request.ForceSave);
        }
        catch (ScriptValidationException ex)
        {
            // 校验失败（forceSave=false 缺 aiGoal 等不完整脚本）：返 400 + validationFailed=true + errors，
            // 前端 app.js confirm 是否强制保存 → forceSave=true 重发跳校验。不返 500（原无 catch 走 500，前端只在日志记「保存失败」用户不知为何）。
            return BadRequest(new { validationFailed = true, error = "脚本校验失败", errors = ex.Errors });
        }

        return Ok(new { scriptId = script.ScriptId, saved = true });
    }
}

public class StartRecordRequest
{
    public string DocumentTypeId { get; set; } = "";
    public string TaskDescription { get; set; } = "";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? PrerequisiteScriptId { get; set; }
    public List<AttachmentInfo>? Attachments { get; set; }  // 批次7-C1：录制端接收附件（upload 执行层用 _attachments）
}

public class UserResponseRequest
{
    public string TaskId { get; set; } = "";
    public string Response { get; set; } = "";
}

public class StopRequest
{
    public string TaskId { get; set; } = "";
}

public class SaveScriptRequest
{
    public string TaskId { get; set; } = "";
    public string? Name { get; set; }
    public string? DocumentTypeId { get; set; }
    public ScriptV2? Script { get; set; }
    public bool ForceSave { get; set; }  // 强制保存：跳过 schema+业务两层校验（用户在 app.js confirm 后重发时设 true）
}
