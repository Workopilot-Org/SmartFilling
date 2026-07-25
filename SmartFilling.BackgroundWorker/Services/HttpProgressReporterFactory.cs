using Microsoft.Extensions.Options;
using SmartFilling.BackgroundWorker.Configuration;
using SmartFilling.Engine.Reporting;

namespace SmartFilling.BackgroundWorker.Services;

/// <summary>
/// HTTP 进度 reporter 工厂（D2：实现 IProgressReporterFactory）。
/// 替代原 SignalRProgressReporterFactory（FillHub 砍后不再用 SignalR 推送）。
/// CreateForTask 接收 per-task progressCallbackUrl（由 ScriptEngineRunner/TaskOrchestrationService 从 FillTask.ProgressCallbackUrl 传入）。
/// </summary>
public class HttpProgressReporterFactory : IProgressReporterFactory
{
    private readonly InMemoryProgressStore _store;
    private readonly HttpClient _httpClient;
    private readonly WorkerOptions _options;
    private readonly ILoggerFactory _loggerFactory;

    public HttpProgressReporterFactory(
        InMemoryProgressStore store,
        HttpClient httpClient,
        IOptions<WorkerOptions> options,
        ILoggerFactory loggerFactory)
    {
        _store = store;
        _httpClient = httpClient;
        _options = options.Value;
        _loggerFactory = loggerFactory;
    }

    public ITaskProgressReporter CreateForTask(string taskId, string? progressCallbackUrl = null)
        => new HttpProgressReporter(taskId, progressCallbackUrl, _options.ProgressReport, _store, _httpClient, _loggerFactory.CreateLogger<HttpProgressReporter>());
}
