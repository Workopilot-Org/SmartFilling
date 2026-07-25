using System.Collections.Concurrent;

namespace SmartFilling.BackgroundWorker.Services;

/// <summary>
/// 进度内存字典（第3轮-C7）：HttpProgressReporter 写入日志/截图/终态，FillController.GetStatus 读取，HttpTaskCompletionHandler 终态后 Cleanup 清理。
/// 不写文件不持久化（只存运行中进度，终态存 TaskResultStore）。
/// Types.Log/Screenshot/Completed 控制各类型是否采集（pull 存内存）和推送（push HTTP POST）。
/// </summary>
public class InMemoryProgressStore
{
    private readonly ConcurrentDictionary<string, ProgressEntry> _store = new();
    private const int MaxLogsPerTask = 500;       // 防长任务日志无界内存
    private const int MaxScreenshotsPerTask = 50;

    public ProgressEntry? Get(string taskId)
        => _store.TryGetValue(taskId, out var e) ? e : null;

    public void AddLog(string taskId, string message, string? stepName)
    {
        var entry = _store.GetOrAdd(taskId, _ => new ProgressEntry());
        lock (entry.Logs)
        {
            if (entry.Logs.Count >= MaxLogsPerTask)
                entry.Logs.RemoveAt(0);
            entry.Logs.Add(new LogEntry { Message = message, StepName = stepName, Timestamp = DateTime.Now });
        }
    }

    public void AddScreenshot(string taskId, string base64Image, string? stepDescription)
    {
        var entry = _store.GetOrAdd(taskId, _ => new ProgressEntry());
        lock (entry.Screenshots)
        {
            if (entry.Screenshots.Count >= MaxScreenshotsPerTask)
                entry.Screenshots.RemoveAt(0);
            entry.Screenshots.Add(new ScreenshotEntry { Image = base64Image, StepDescription = stepDescription, Timestamp = DateTime.Now });
            entry.LatestScreenshot = base64Image;
        }
    }

    public void AddCompleted(string taskId, string result, string? errorMessage, string? failureType,
        Dictionary<string, object>? returnData, object? stats)
    {
        var entry = _store.GetOrAdd(taskId, _ => new ProgressEntry());
        entry.Completed = new CompletedEntry
        {
            Result = result,
            ErrorMessage = errorMessage,
            FailureType = failureType,
            ReturnData = returnData,
            Stats = stats,
            Timestamp = DateTime.Now
        };
    }

    public void Cleanup(string taskId) => _store.TryRemove(taskId, out _);
}

public class ProgressEntry
{
    public List<LogEntry> Logs { get; } = new();
    public List<ScreenshotEntry> Screenshots { get; } = new();
    public string? LatestScreenshot { get; set; }
    public CompletedEntry? Completed { get; set; }
}

public class LogEntry
{
    public string Message { get; set; } = string.Empty;
    public string? StepName { get; set; }
    public DateTime Timestamp { get; set; }
}

public class ScreenshotEntry
{
    public string Image { get; set; } = string.Empty;  // base64
    public string? StepDescription { get; set; }
    public DateTime Timestamp { get; set; }
}

public class CompletedEntry
{
    public string Result { get; set; } = "";   // "Success" | "Stopped" | "Failed"
    public string? ErrorMessage { get; set; }
    public string? FailureType { get; set; }
    public Dictionary<string, object>? ReturnData { get; set; }
    public object? Stats { get; set; }
    public DateTime Timestamp { get; set; }
}
