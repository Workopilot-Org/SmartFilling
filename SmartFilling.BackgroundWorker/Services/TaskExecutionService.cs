using SmartFilling.BackgroundWorker.Models;
using TaskStatus = SmartFilling.BackgroundWorker.Models.TaskStatus;

namespace SmartFilling.BackgroundWorker.Services;

public class TaskExecutionService
{
    private readonly TaskRunnerFactory _runnerFactory;
    private readonly TaskConcurrencyManager _concurrencyManager;
    private readonly ITaskCompletionHandler _completionHandler;
    private readonly TaskOrchestrationService _orchestrationService;
    private readonly ILogger<TaskExecutionService> _logger;

    public event Action? TaskCompleted;

    public TaskExecutionService(
        TaskRunnerFactory runnerFactory,
        TaskConcurrencyManager concurrencyManager,
        ITaskCompletionHandler completionHandler,
        TaskOrchestrationService orchestrationService,
        ILogger<TaskExecutionService> logger)
    {
        _runnerFactory = runnerFactory;
        _concurrencyManager = concurrencyManager;
        _completionHandler = completionHandler;
        _orchestrationService = orchestrationService;
        _logger = logger;
    }

    public async Task<bool> ExecuteAsync(FillTask task)
    {
        var acquired = await _concurrencyManager.TryAcquireAsync();
        if (!acquired)
        {
            _logger.LogWarning("任务启动失败，并发槽位已满: {TaskId}", task.TaskId);
            return false;
        }

        _logger.LogInformation("任务开始执行: {TaskId}, 当前并发: {Current}/{Max}",
            task.TaskId, _concurrencyManager.RunningTaskCount, _concurrencyManager.MaxConcurrentTasks);

        // 注册可取消的 CTS
        var taskCt = _orchestrationService.RegisterCancellationToken(task.TaskId);

        _ = Task.Run(async () =>
        {
            try
            {
                var runner = _runnerFactory.Create();
                await runner.RunAsync(task, taskCt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "任务执行异常: {TaskId}", task.TaskId);
                task.Status = TaskStatus.Failed;
                task.ErrorMessage = ex.Message;
                // P3-11：补 FailureType=Deterministic（Runner 创建失败等引擎外异常，否则 task.FailureType=null → completionHandler 发 None → 前端"填报失败[None]"）
                task.FailureType = SmartFilling.Engine.Models.FailureType.Deterministic;
            }
            finally
            {
                _orchestrationService.UnregisterCancellationToken(task.TaskId);

                _logger.LogInformation("任务执行结束: {TaskId}, 状态: {Status}",
                    task.TaskId, task.Status);

                try { await _completionHandler.OnTaskCompleted(task); }
                catch (Exception ex) { _logger.LogError(ex, "任务完成上报失败: {TaskId}", task.TaskId); }

                _concurrencyManager.Release();
                TaskCompleted?.Invoke();
            }
        }, taskCt);

        return true;
    }
}
