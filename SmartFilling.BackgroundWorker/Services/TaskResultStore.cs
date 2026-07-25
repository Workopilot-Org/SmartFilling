using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SmartFilling.BackgroundWorker.Configuration;
using SmartFilling.Engine.Models;

namespace SmartFilling.BackgroundWorker.Services;

/// <summary>
/// 终态持久化（决策7：单文件 JSONL）。内存字典做查询索引 + 文件 append + 定时 compact。
/// 进度不持久化，只存终态（任务结束写一次不更新）。原子写用临时文件+rename（compact 时），append 崩只毁最后半行。
/// 供 HttpTaskCompletionHandler（终态写入）+ FillController.GetStatus（查询）/StartFill（幂等检查）使用。
/// </summary>
public class TaskResultStore
{
    private readonly ConcurrentDictionary<string, TaskResultRecord> _records = new();
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly WorkerOptions _options;
    private readonly ILogger<TaskResultStore> _logger;
    private Timer? _cleanupTimer;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public TaskResultStore(IOptions<WorkerOptions> options, ILogger<TaskResultStore> logger)
    {
        _options = options.Value;
        _logger = logger;
        Load();
        var interval = _options.TaskResultStore.CleanupIntervalHours > 0
            ? _options.TaskResultStore.CleanupIntervalHours
            : 6;
        _cleanupTimer = new Timer(async _ => await CleanupAsync(), null,
            TimeSpan.FromHours(interval), TimeSpan.FromHours(interval));
    }

    /// <summary>查询终态（null=不存在/未结束）。</summary>
    public virtual TaskResultRecord? TryGet(string taskId)
        => _records.TryGetValue(taskId, out var r) ? r : null;

    /// <summary>是否已存在终态（幂等检查用：已结束=True）。</summary>
    public virtual bool Exists(string taskId) => _records.ContainsKey(taskId);

    /// <summary>写入终态（内存索引 + JSONL append）。virtual 供测试 mock（避免文件 IO 副作用）。</summary>
    public virtual async Task AppendAsync(TaskResultRecord record)
    {
        _records[record.TaskId] = record;
        await AppendToFileAsync(record);
    }

    private async Task AppendToFileAsync(TaskResultRecord record)
    {
        await _fileLock.WaitAsync();
        try
        {
            var filePath = ResolveFilePath();
            EnsureDir(filePath);
            var line = JsonSerializer.Serialize(record, _jsonOptions) + "\n";
            await File.AppendAllTextAsync(filePath, line);
        }
        catch (Exception ex)
        {
            // 文件写入失败不阻断终态上报（内存索引已建，GET 仍可查；终态不丢，只是跨重启丢失）
            _logger.LogError(ex, "终态 JSONL 写入失败: {TaskId}", record.TaskId);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>启动时加载已有 JSONL 到内存字典（跨重启终态可查）。</summary>
    private void Load()
    {
        try
        {
            var filePath = ResolveFilePath();
            if (!File.Exists(filePath)) return;
            foreach (var line in File.ReadAllLines(filePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var record = JsonSerializer.Deserialize<TaskResultRecord>(line, _jsonOptions);
                    if (record != null && !string.IsNullOrEmpty(record.TaskId))
                        _records[record.TaskId] = record;
                }
                catch { /* 跳过损坏行（append 崩的半行） */ }
            }
            _logger.LogInformation("TaskResultStore 加载 {Count} 条终态记录", _records.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TaskResultStore 加载失败");
        }
    }

    /// <summary>定时 compact：删 RetentionDays 前终态 + 临时文件 rename 原子重写。</summary>
    private async Task CleanupAsync()
    {
        try
        {
            var retention = _options.TaskResultStore.RetentionDays > 0
                ? _options.TaskResultStore.RetentionDays
                : 7;
            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(retention);
            var expired = _records.Where(kv => kv.Value.Timestamp < cutoff).Select(kv => kv.Key).ToList();
            foreach (var id in expired)
                _records.TryRemove(id, out _);

            if (expired.Count > 0)
                _logger.LogInformation("TaskResultStore compact 清理 {Count} 条过期终态", expired.Count);

            await RewriteFileAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TaskResultStore compact 失败");
        }
    }

    /// <summary>原子重写：写临时文件 + rename 覆盖（防 append 崩残留半行 + 删过期后磁盘同步）。</summary>
    private async Task RewriteFileAsync()
    {
        await _fileLock.WaitAsync();
        try
        {
            var filePath = ResolveFilePath();
            EnsureDir(filePath);
            var tempPath = filePath + ".tmp";
            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            await using (var writer = new StreamWriter(fs))
            {
                foreach (var kv in _records)
                    await writer.WriteLineAsync(JsonSerializer.Serialize(kv.Value, _jsonOptions));
            }
            // File.Move 跨卷可能失败（临时文件与目标同目录则同卷，安全）
            if (File.Exists(filePath))
                File.Replace(tempPath, filePath, null);
            else
                File.Move(tempPath, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TaskResultStore 原子重写失败");
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private string ResolveFilePath()
    {
        var path = _options.TaskResultStore.FilePath;
        if (string.IsNullOrWhiteSpace(path))
            path = "data/task-results.jsonl";
        if (!Path.IsPathRooted(path))
            path = Path.Combine(AppContext.BaseDirectory, path);
        return path;
    }

    private static void EnsureDir(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }
}

/// <summary>终态记录（GET /tasks/{id} 响应 + completeCallbackUrl payload 同构基础）。</summary>
public class TaskResultRecord
{
    public string TaskId { get; set; } = string.Empty;
    /// <summary>"completed" | "failed" | "cancelled"（对齐 ScriptResult.Status + 停止语义）。</summary>
    public string Status { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? FailureType { get; set; }
    public Dictionary<string, object>? ReturnData { get; set; }
    public ExecutionStats? Stats { get; set; }
    public string? Screenshot { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
