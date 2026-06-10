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
[Route("api/[controller]")]
public class FillController : ControllerBase
{
    private readonly IProgressReporterFactory _reporterFactory;
    private readonly IWebHostEnvironment _env;
    private readonly EngineILogger _logger;
    private readonly TaskExecutionService _taskExecutionService;
    private readonly TaskConcurrencyManager _concurrencyManager;
    private readonly TaskOrchestrationService _orchestrationService;
    private readonly PlatformOptions _platformOptions;
    private readonly WorkerOptions _workerOptions;
    private readonly EngineOptions _engineOptions;

    public FillController(
        IProgressReporterFactory reporterFactory,
        IWebHostEnvironment env,
        EngineILogger logger,
        TaskExecutionService taskExecutionService,
        TaskConcurrencyManager concurrencyManager,
        TaskOrchestrationService orchestrationService,
        IOptions<PlatformOptions> platformOptions,
        IOptions<WorkerOptions> workerOptions,
        IOptions<EngineOptions> engineOptions)
    {
        _reporterFactory = reporterFactory;
        _env = env;
        _logger = logger;
        _taskExecutionService = taskExecutionService;
        _concurrencyManager = concurrencyManager;
        _orchestrationService = orchestrationService;
        _platformOptions = platformOptions.Value;
        _workerOptions = workerOptions.Value;
        _engineOptions = engineOptions.Value;
    }

    [HttpGet("health")]
    public IActionResult Health() => Ok(new { status = "healthy", timestamp = DateTime.Now });

    [HttpPost("start")]
    public async Task<ActionResult> StartFill([FromBody] FillRequest request)
    {
        try
        {
            if (request.Scripts == null || request.Scripts.Count == 0)
                return BadRequest(new { error = "未提供可执行的脚本" });

            // ④-4：校验前移到 Worker 执行边界——传原始 JSON 文本逐个 LoadFromJson 全校验（schema+业务+④-8 命名冲突）。
            // 局部 try/catch 拦截校验异常→400（含明细透传前端），不被外层 catch(Exception)→500 吞掉（第4轮 🔴 try/catch 嵌套陷阱）。
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
                TaskId = Guid.NewGuid().ToString("N")[..8],
                DocumentTypeId = request.DocumentTypeId,
                DocumentTypeName = request.DocumentTypeName,
                Scripts = validatedScripts,
                FillData = request.FillData,
                Status = BWTaskStatus.Running
            };

            // 下载附件：从配置取 baseUrl（App 地址），不从 Request 取
            var baseUrl = _platformOptions.AttachmentBaseUrl;
            var timeoutSeconds = _platformOptions.DefaultTimeoutSeconds > 0 ? _platformOptions.DefaultTimeoutSeconds : 60;
            await AttachmentService.DownloadAttachmentsInFillDataAsync(task.FillData, _engineOptions.UploadRootPath ?? _env.ContentRootPath, _logger, baseUrl, timeoutSeconds);

            var reporter = _reporterFactory.CreateForTask(task.TaskId);
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

            var workerHubUrl = !string.IsNullOrEmpty(_workerOptions.BaseUrl)
                ? $"{_workerOptions.BaseUrl.TrimEnd('/')}/hubs/fill"
                : $"{Request.Scheme}://{Request.Host}/hubs/fill";

            return Ok(new { taskId = task.TaskId, workerHubUrl });
        }
        catch (Exception ex)
        {
            _logger.LogError($"启动填报任务失败: {ex.Message}");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("stop/{taskId}")]
    public async Task<IActionResult> StopTask(string taskId)
    {
        // P3-7：只 CancelByTaskId + SendLogAsync。终态由 completionHandler 按 task 实际状态单一出口发（根治停止/完成竞争——
        // 原 reporter.SendTaskStoppedAsync 越权发终态，与 engine/取消赛跑致"显示已停止但实际成功"）。
        _orchestrationService.CancelByTaskId(taskId);

        var reporter = _reporterFactory.CreateForTask(taskId);
        await reporter.SendLogAsync("任务已被用户停止");
        return Ok(new { message = "已发送停止请求" });
    }

    [HttpGet("status/{taskId}")]
    public IActionResult GetStatus(string taskId) => Ok(new { taskId, status = "running" });

    [HttpGet("server-status")]
    public IActionResult GetServerStatus() => Ok(new
    {
        maxConcurrentTasks = _concurrencyManager.MaxConcurrentTasks,
        runningTasks = _concurrencyManager.RunningTaskCount,
        availableSlots = _concurrencyManager.MaxConcurrentTasks - _concurrencyManager.RunningTaskCount,
        status = _concurrencyManager.IsBusy ? "busy" : "available",
        timestamp = DateTime.Now
    });
}
