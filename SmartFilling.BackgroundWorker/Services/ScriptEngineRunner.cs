using Microsoft.Playwright;
using SmartFilling.Engine.Ai;
using SmartFilling.Engine.Engine;
using SmartFilling.Engine.Models;
using SmartFilling.Engine.Reporting;
using SmartFilling.BackgroundWorker.Models;
using EngineILogger = SmartFilling.Engine.Logging.ILogger;
using BWTaskStatus = SmartFilling.BackgroundWorker.Models.TaskStatus;

namespace SmartFilling.BackgroundWorker.Services;

public class ScriptEngineRunner
{
    private readonly EngineILogger _logger;
    private readonly IProgressReporterFactory _reporterFactory;
    private readonly IConfiguration _configuration;
    private readonly AiOptions? _aiOptions;
    private readonly EngineOptions _engineOptions;
    private readonly SmartFilling.Engine.Services.CaptchaService? _captchaService;
    private readonly TaskOrchestrationService? _orchestrationService;
    private readonly TaskActivityTracker _activityTracker;
    private readonly HttpClient? _httpClient;

    public ScriptEngineRunner(
        EngineILogger logger,
        IProgressReporterFactory reporterFactory,
        IConfiguration configuration,
        EngineOptions engineOptions,
        TaskActivityTracker activityTracker,
        AiOptions? aiOptions = null,
        SmartFilling.Engine.Services.CaptchaService? captchaService = null,
        TaskOrchestrationService? orchestrationService = null,
        HttpClient? httpClient = null)
    {
        _logger = logger;
        _reporterFactory = reporterFactory;
        _configuration = configuration;
        _engineOptions = engineOptions;
        _activityTracker = activityTracker;
        _aiOptions = aiOptions;
        _captchaService = captchaService;
        _orchestrationService = orchestrationService;
        _httpClient = httpClient;
    }

    public async Task RunAsync(FillTask task, CancellationToken ct)
    {
        task.Status = BWTaskStatus.Running;
        task.StartTime = DateTime.Now;

        var reporter = _reporterFactory.CreateForTask(task.TaskId, task.ProgressCallbackUrl);

        IPlaywright? playwright = null;
        IBrowser? browser = null;
        IPage? page = null;

        try
        {
            await reporter.SendLogAsync("正在启动浏览器...");

            playwright = await Playwright.CreateAsync();

            // F5/R1-9：Headless/BrowserPath/SlowMo 统一从已绑定的 _engineOptions（appsettings ScriptEngine 段）取，
            // 消除双轨——原 task.Headless（WS: WsClient:Headless 恒 true / HTTP: App 转发硬编码 true）+ BrowserPath/SlowMo 直读 IConfiguration。
            // Worker Program.cs Configure<EngineOptions>("ScriptEngine") 已绑定，改 appsettings 即对 HTTP/WS 两模式填报同时生效。
            var launchOptions = new BrowserTypeLaunchOptions { Headless = _engineOptions.Headless };
            if (!string.IsNullOrEmpty(_engineOptions.BrowserPath))
                launchOptions.ExecutablePath = _engineOptions.BrowserPath;
            if (_engineOptions.SlowMo > 0)
                launchOptions.SlowMo = _engineOptions.SlowMo;

            browser = await playwright.Chromium.LaunchAsync(launchOptions);

            var vp = _engineOptions.Viewport;
            var context = await browser.NewContextAsync(new()
            {
                ViewportSize = new ViewportSize { Width = vp?.Width ?? 1280, Height = vp?.Height ?? 720 }
            });

            page = await context.NewPageAsync();

            await reporter.SendLogAsync("浏览器已启动");

            ScriptEngine engine;
            if (_aiOptions != null && !string.IsNullOrEmpty(_aiOptions.ApiKey))
            {
                var aiProvider = new OpenAiProvider(_aiOptions, _logger);
                engine = new ScriptEngine(_logger, _engineOptions, aiProvider, reporter, _captchaService, _httpClient);
            }
            else
            {
                engine = new ScriptEngine(_logger, _engineOptions, reporter, _captchaService, _httpClient);
            }

            // P1（第8轮决策落实）：订阅 Engine StepActivity 事件——主执行循环每步触发，调 TaskActivityTracker.Touch 更新最近活动时间
            // （覆盖确定性步骤 + AI fallback + AI action + AI phase 全路径，供 /health?taskId=xxx 查询，App/PlatformAdapter 健康检测用）
            engine.StepActivity += tid => { if (!string.IsNullOrEmpty(tid)) _activityTracker.Touch(tid); };

            ScriptResult finalResult;
            if (task.Scripts.Count > 1 && _orchestrationService != null)
            {
                // 多脚本编排：委托 TaskOrchestrationService（vars 自动合并 + 组3：传 task.InitialVars）
                finalResult = await _orchestrationService.ExecuteAsync(
                    task.Scripts, task.FillData, engine, page, task.TaskId, ct, task.ProgressCallbackUrl,
                    task.InitialVars);  // 组3 (3.8)：传调用方 InitialVars（已过滤 ReservedVarNames + 归一）
            }
            else
            {
                // 组3 (3.6)：单脚本也按 script.InitialVars 声明过滤（与多脚本 3.7 方案B 同模式，避免未声明 key 进 ctx.Vars 污染 AI availableData）。
                var singleScript = task.Scripts[0];
                var declared = singleScript.InitialVars?.Select(v => v.Name).ToHashSet(System.StringComparer.Ordinal) ?? new HashSet<string>();
                Dictionary<string, object>? scriptInitialVars = null;
                if (declared.Count > 0 && task.InitialVars != null)
                {
                    scriptInitialVars = new Dictionary<string, object>();
                    foreach (var name in declared)
                        if (task.InitialVars.TryGetValue(name, out var mv))
                            scriptInitialVars[name] = mv;
                }
                var result = await engine.ExecuteAsync(singleScript, task.FillData, page, ct, task.TaskId, scriptInitialVars);
                finalResult = result;
                if (!result.Success)
                {
                    task.Status = BWTaskStatus.Failed;
                    task.ErrorMessage = result.ErrorMessage ?? "脚本执行失败";
                    task.FailureType = result.FailureType;  // #16 决策4
                    task.ReturnData = result.ReturnData;  // P3：失败也回填 returnData（completionHandler 读 task.ReturnData）
                    task.Stats = result.Stats;
                    await reporter.SendLogAsync($"脚本执行失败: {task.ErrorMessage}");
                    await reporter.SendTaskCompletedAsync(result);  // 通知 ProgressReport（Types.Completed 控制是否采集/推送）
                    return;
                }
            }

            if (!finalResult.Success)
            {
                task.Status = BWTaskStatus.Failed;
                task.ErrorMessage = finalResult.ErrorMessage ?? "脚本执行失败";
                task.FailureType = finalResult.FailureType;  // #16 决策4
                task.ReturnData = finalResult.ReturnData;  // P3：失败也回填
                task.Stats = finalResult.Stats;
                await reporter.SendLogAsync($"任务执行失败: {task.ErrorMessage}");
                await reporter.SendTaskCompletedAsync(finalResult);  // 通知 ProgressReport（Types.Completed 控制是否采集/推送）
                return;
            }

            try
            {
                var screenshot = await CompressScreenshotAsync(page);
                task.FinalScreenshot = Convert.ToBase64String(screenshot);
            }
            catch { }

            // M.2.1：回填 returnData 到 FillTask（HttpTaskCompletionHandler 读 task.ReturnData 写 TaskResultStore + push 回调到 completeCallbackUrl）。
            // returnData 已由 Engine 按脚本顶层 returnData 声明组装进 ScriptResult.ReturnData；删原读 Vars["_billCode"] 死代码（无写入点，returnData 不再走 vars）。
            task.ReturnData = finalResult.ReturnData;
            task.Stats = finalResult.Stats;  // P3-2：回填 stats（completionHandler 发 TaskCompleted 时带）

            task.Status = BWTaskStatus.Completed;
            await reporter.SendLogAsync("所有脚本执行完成");
            await reporter.SendTaskCompletedAsync(finalResult);  // 通知 ProgressReport（Types.Completed 控制是否采集/推送）
        }
        catch (OperationCanceledException)
        {
            task.Status = BWTaskStatus.Failed;
            task.ErrorMessage = "任务被取消";
            task.FailureType = Engine.Models.FailureType.Cancelled;  // #16 决策4
            await reporter.SendLogAsync("任务被取消");
            await reporter.SendTaskCompletedAsync(new ScriptResult  // 通知 ProgressReport（Types.Completed 控制是否采集/推送）
            {
                Success = false,
                Status = "failed",
                ErrorMessage = task.ErrorMessage,
                FailureType = Engine.Models.FailureType.Cancelled,
                ReturnData = task.ReturnData,
                Stats = task.Stats
            });
        }
        catch (Exception ex)
        {
            task.Status = BWTaskStatus.Failed;
            task.ErrorMessage = ex.Message;
            task.FailureType = Engine.Models.FailureType.Deterministic;  // #16 决策4
            _logger.LogError($"任务执行异常: {ex.Message}");

            try
            {
                if (page != null)
                {
                    var screenshot = await CompressScreenshotAsync(page);
                    task.FinalScreenshot = Convert.ToBase64String(screenshot);
                }
            }
            catch { }

            await reporter.SendLogAsync($"任务执行失败: {ex.Message}");
            await reporter.SendTaskCompletedAsync(new ScriptResult  // 通知 ProgressReport（Types.Completed 控制是否采集/推送）
            {
                Success = false,
                Status = "failed",
                ErrorMessage = task.ErrorMessage,
                FailureType = Engine.Models.FailureType.Deterministic,
                ReturnData = task.ReturnData,
                Stats = task.Stats
            });
        }
        finally
        {
            try { await browser!.CloseAsync(); } catch { }
            playwright?.Dispose();
        }
    }

    private async Task<byte[]> CompressScreenshotAsync(IPage page)
    {
        var quality = _engineOptions.Screenshot?.CompressQuality ?? 70;
        if (quality >= 100)
            return await page.ScreenshotAsync();
        return await page.ScreenshotAsync(new() { Type = ScreenshotType.Jpeg, Quality = quality });
    }
}
