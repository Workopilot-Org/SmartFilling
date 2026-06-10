using SmartFilling.BackgroundWorker.Configuration;
using SmartFilling.BackgroundWorker.Models;

namespace SmartFilling.BackgroundWorker.Services;

public class TaskConcurrencyManager
{
    private readonly SemaphoreSlim _semaphore;
    private readonly int _maxConcurrentTasks;
    private int _runningTaskCount;
    private readonly ILogger<TaskConcurrencyManager> _logger;

    public TaskConcurrencyManager(WorkerOptions workerOptions, ILogger<TaskConcurrencyManager> logger)
    {
        _maxConcurrentTasks = workerOptions.MaxConcurrentTasks;
        _semaphore = new SemaphoreSlim(_maxConcurrentTasks, _maxConcurrentTasks);
        _logger = logger;
    }

    public async Task<bool> TryAcquireAsync(TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(5);
        var acquired = await _semaphore.WaitAsync(timeout.Value);
        if (acquired)
            Interlocked.Increment(ref _runningTaskCount);
        return acquired;
    }

    public void Release()
    {
        Interlocked.Decrement(ref _runningTaskCount);
        _semaphore.Release();
        _logger.LogInformation("任务已完成，释放并发槽位，当前运行任务数: {Current}/{Max}",
            _runningTaskCount, _maxConcurrentTasks);
    }

    public bool IsBusy => Volatile.Read(ref _runningTaskCount) >= _maxConcurrentTasks;
    public string Status => IsBusy ? "BUSY" : "IDLE";
    public int RunningTaskCount => Volatile.Read(ref _runningTaskCount);
    public int MaxConcurrentTasks => _maxConcurrentTasks;
}
