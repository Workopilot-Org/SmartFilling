using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SmartFilling.BackgroundWorker.Configuration;
using SmartFilling.BackgroundWorker.Models;
using SmartFilling.BackgroundWorker.Services;
using SmartFilling.Engine.Models;
using EngineILogger = SmartFilling.Engine.Logging.ILogger;
using BWTaskStatus = SmartFilling.BackgroundWorker.Models.TaskStatus;

namespace SmartFilling.BackgroundWorker.Controllers;

[ApiController]
[Route("tasks")]  // 路由资源化：/api/fill/* -> /tasks
public class FillController : ControllerBase
{
    private readonly IProgressReporterFactory _reporterFactory;
    private readonly IWebHostEnvironment _env;
    private readonly EngineILogger _logger;
    private readonly TaskExecutionService _taskExecutionService;
    private readonly TaskConcurrencyManager _concurrencyManager;
    private readonly TaskOrchestrationService _orchestrationService;
    private readonly WorkerOptions _workerOptions;
    private readonly EngineOptions _engineOptions;
    private readonly TaskResultStore _resultStore;          // C10/H11：GetStatus 真查终态 + StartFill 幂等检查
    private readonly TaskActivityTracker _activityTracker;  // G1-11：health?taskId 查询 + StartFill 幂等检查在跑
    private readonly InMemoryProgressStore _progressStore;  // H11：GetStatus 查进度（pull 模式）

    public FillController(
        IProgressReporterFactory reporterFactory,
        IWebHostEnvironment env,
        EngineILogger logger,
        TaskExecutionService taskExecutionService,
        TaskConcurrencyManager concurrencyManager,
        TaskOrchestrationService orchestrationService,
        IOptions<WorkerOptions> workerOptions,
        IOptions<EngineOptions> engineOptions,
        TaskResultStore resultStore,
        TaskActivityTracker activityTracker,
        InMemoryProgressStore progressStore)
    {
        _reporterFactory = reporterFactory;
        _env = env;
        _logger = logger;
        _taskExecutionService = taskExecutionService;
        _concurrencyManager = concurrencyManager;
        _orchestrationService = orchestrationService;
        _workerOptions = workerOptions.Value;
        _engineOptions = engineOptions.Value;
        _resultStore = resultStore;
        _activityTracker = activityTracker;
        _progressStore = progressStore;
    }

    // health/server-status 用 ~ 前缀覆盖类级别路由 -> /health + /server-status（根级，不归 /tasks 下，避免与 {taskId} 冲突）
    [HttpGet("~/health")]
    public IActionResult Health(string? taskId = null)
    {
        if (string.IsNullOrEmpty(taskId))
            return Ok(new { status = "healthy", timestamp = DateTime.Now });

        // G1-11：有参查 TaskActivityTracker 返 {status, taskStatus, lastActivity}
        var lastActivity = _activityTracker.GetLastActivity(taskId);
        return Ok(new
        {
            status = "healthy",
            taskStatus = lastActivity.HasValue ? "running" : "not_found",
            lastActivity
        });
    }

    [HttpPost("")]
    public async Task<ActionResult> StartFill([FromBody] FillRequest request)
    {
        try
        {
            // 决策11-A：taskId 必填（上游传入稳定业务键，Worker 不自生成）
            if (string.IsNullOrEmpty(request.TaskId))
                return BadRequest(new { error = "taskId 必填" });

            // M1/M2：幂等检查在脚本验证前（避免已存在 taskId 浪费脚本验证开销）
            // 已结束（终态）→ 200 幂等返终态
            var existing = _resultStore.TryGet(request.TaskId);
            if (existing != null)
                return Ok(new { taskId = request.TaskId, status = existing.Status, result = existing });
            // 在跑 → 200 running
            if (_activityTracker.IsRegistered(request.TaskId))
                return Ok(new { taskId = request.TaskId, status = "running", message = "任务已在执行中" });

            if (request.Scripts == null || request.Scripts.Count == 0)
                return BadRequest(new { error = "未提供可执行的脚本" });

            // ④-4：校验前移到 Worker 执行边界——传原始 JSON 文本逐个 LoadFromJson 全校验（schema+业务+④-8 命名冲突）。
            var validatedScripts = new List<ScriptV2>();
            for (int i = 0; i < request.Scripts.Count; i++)
            {
                try
                {
                    validatedScripts.Add(SmartFilling.Engine.Engine.ScriptLoader.LoadFromJson(request.Scripts[i], _workerOptions.IgnoreNullFieldsOnScriptValidation));
                }
                catch (Exception ex) when (ex is InvalidOperationException or JsonException)
                {
                    return BadRequest(new { error = $"脚本 #{i + 1} 校验失败: {ex.Message}" });
                }
            }

            var task = new FillTask
            {
                TaskId = request.TaskId,  // 决策11-A：上游传入，Worker 不自生成
                DocumentTypeId = request.DocumentTypeId,
                DocumentTypeName = request.DocumentTypeName,
                Scripts = validatedScripts,
                FillData = request.FillData,
                ProgressCallbackUrl = request.ProgressCallbackUrl,  // H9：显式赋值中转（入口请求体→FillTask 存储→reporter/handler 消费，三段不缺中转）
                CompleteCallbackUrl = request.CompleteCallbackUrl,
                Status = BWTaskStatus.Running
            };

            // 下载附件：B3 删 PlatformOptions.AttachmentBaseUrl（App/PlatformAdapter 上游已转绝对 URL，传空安全）；timeout 用 AttachmentDownloadTimeoutSeconds（从 PlatformOptions.DefaultTimeoutSeconds 迁移）
            var timeoutSeconds = _workerOptions.AttachmentDownloadTimeoutSeconds > 0 ? _workerOptions.AttachmentDownloadTimeoutSeconds : 60;
            await AttachmentService.DownloadAttachmentsInFillDataAsync(task.FillData, _engineOptions.UploadRootPath ?? _env.ContentRootPath, _logger, null, timeoutSeconds);

            var reporter = _reporterFactory.CreateForTask(task.TaskId, task.ProgressCallbackUrl);
            await reporter.SendLogAsync("填报任务已创建");

            var started = await _taskExecutionService.ExecuteAsync(task);
            if (!started)
            {
                return StatusCode(503, new
                {
                    error = "服务器繁忙",
                    runningTasks = _concurrencyManager.RunningTaskCount,
                    maxConcurrentTasks = _concurrencyManager.MaxConcurrentTasks
                });
            }

            // 新任务返 202 {taskId} + Location header（REST 实践；StatusCode(202) 不自动加 Location，需显式设置）
            Response.Headers["Location"] = $"/tasks/{task.TaskId}";
            return StatusCode(202, new { taskId = task.TaskId });
        }
        catch (Exception ex)
        {
            _logger.LogError($"启动填报任务失败: {ex.Message}");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("{taskId}:cancel")]
    public async Task<IActionResult> StopTask(string taskId)
    {
        // P3-7：只 CancelByTaskId + SendLogAsync。终态由 completionHandler 按 task 实际状态单一出口发。
        _orchestrationService.CancelByTaskId(taskId);

        var reporter = _reporterFactory.CreateForTask(taskId);
        await reporter.SendLogAsync("任务已被用户停止");
        return Ok(new { message = "已发送停止请求" });
    }

    [HttpGet("{taskId}")]
    public async Task<IActionResult> GetStatus(string taskId)
    {
        // 终态（TaskResultStore JSONL，GET 地基，始终可查——B9 兑现）
        var record = _resultStore.TryGet(taskId);
        if (record != null)
        {
            return Ok(new
            {
                taskId,
                status = record.Status,
                progress = (object?)null,  // 终态后进度内存已 Cleanup，progress 为空但终态可查
                result = record,
                error = record.ErrorMessage,
                failureType = record.FailureType
            });
        }

        // 进度（InMemoryProgressStore，pull 模式运行中可查；push 模式进度已 POST 给 progressCallbackUrl，GET progress 为空但终态仍可查 TaskResultStore）
        if (_activityTracker.IsRegistered(taskId))
        {
            var progress = _progressStore.Get(taskId);
            return Ok(new
            {
                taskId,
                status = "running",
                progress = progress != null ? new { logs = progress.Logs, screenshot = progress.LatestScreenshot, completed = progress.Completed } : null,
                result = (object?)null,
                error = (string?)null,
                failureType = (string?)null
            });
        }

        return NotFound(new { taskId, status = "not_found" });
    }

    [HttpGet("~/server-status")]
    public IActionResult GetServerStatus() => Ok(new
    {
        maxConcurrentTasks = _concurrencyManager.MaxConcurrentTasks,
        runningTasks = _concurrencyManager.RunningTaskCount,
        availableSlots = _concurrencyManager.MaxConcurrentTasks - _concurrencyManager.RunningTaskCount,
        status = _concurrencyManager.IsBusy ? "busy" : "available",
        timestamp = DateTime.Now
    });
}
