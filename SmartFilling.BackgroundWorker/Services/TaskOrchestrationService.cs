using System.Collections.Concurrent;
using Microsoft.Playwright;
using SmartFilling.Engine.Engine;
using SmartFilling.Engine.Logging;
using SmartFilling.Engine.Models;
using SmartFilling.BackgroundWorker.Models;
using EngineILogger = SmartFilling.Engine.Logging.ILogger;
using BWTaskStatus = SmartFilling.BackgroundWorker.Models.TaskStatus;

namespace SmartFilling.BackgroundWorker.Services;

/// <summary>
/// 多脚本编排服务：按顺序执行多个脚本，脚本间传递 vars。
/// 编排中任一脚本失败立即终止整个任务，后续脚本不执行。
/// 同时管理任务取消（通过 ConcurrentDictionary 存储每个任务的 CTS）。
/// </summary>
public class TaskOrchestrationService
{
    private readonly EngineILogger _logger;
    private readonly IProgressReporterFactory _reporterFactory;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellationTokens = new();

    public TaskOrchestrationService(EngineILogger logger, IProgressReporterFactory reporterFactory)
    {
        _logger = logger;
        _reporterFactory = reporterFactory;
    }

    /// <summary>
    /// 注册任务的取消令牌
    /// </summary>
    public CancellationToken RegisterCancellationToken(string taskId)
    {
        var cts = new CancellationTokenSource();
        _cancellationTokens[taskId] = cts;
        return cts.Token;
    }

    /// <summary>
    /// 取消指定任务
    /// </summary>
    public bool CancelByTaskId(string taskId)
    {
        if (_cancellationTokens.TryRemove(taskId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
            return true;
        }
        return false;
    }

    /// <summary>
    /// 清理已完成任务的 CTS
    /// </summary>
    public void UnregisterCancellationToken(string taskId)
    {
        if (_cancellationTokens.TryRemove(taskId, out var cts))
            cts.Dispose();
    }

    /// <summary>
    /// 编排执行多个脚本。返回最终合并的 ScriptResult。
    /// </summary>
    public async Task<ScriptResult> ExecuteAsync(
        List<ScriptV2> scripts,
        Dictionary<string, object> fillData,
        ScriptEngine engine,
        IPage page,
        string taskId,
        CancellationToken ct,
        string? progressCallbackUrl = null,  // R2-1：传 callbackUrl 给 CreateForTask（HttpProgressReporter push 用）
        Dictionary<string, object>? initialVars = null)  // 组3：调用方传入的可变控制变量（可被脚本 storeAs 覆盖）
    {
        var reporter = _reporterFactory.CreateForTask(taskId, progressCallbackUrl);
        // 组3 方案B：mergedVars 初始化 = 调用方 initialVars 作底（过滤 ReservedVarNames，防系统变量被污染/遮蔽）。
        // 前序脚本 storeAs 后写覆盖 mergedVars；每脚本按 script.InitialVars 声明从 mergedVars 取（一源）。
        var mergedVars = new Dictionary<string, object>();
        if (initialVars != null)
            foreach (var kv in initialVars)
                if (!ScriptLoader.ReservedVarNames.Contains(kv.Key))
                    mergedVars[kv.Key] = kv.Value;
        var allLog = new List<Engine.Logging.StepLog>();
        Dictionary<string, object>? mergedReturnData = null;
        int totalDurationMs = 0;

        for (int i = 0; i < scripts.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var script = scripts[i];
            var scriptName = script.Name ?? $"脚本 {i + 1}";

            await reporter.SendLogAsync($"开始执行 [{scriptName}] ({i + 1}/{scripts.Count})");

            // 组1：effectiveFillData 只用 fillData（前序 storeAs/调用方 initialVars 改走 initialVars 通道进 ctx.Vars，不塞 effectiveFillData/scopeChain——
            // 原 mergedVars 塞 effectiveFillData 致后脚本 storeAs 同名被前序透传值遮蔽（scopeChain 优先 > vars），且系统 _ 变量串台）。
            var effectiveFillData = new Dictionary<string, object>(fillData);

            // 组3 方案B：每脚本按 script.InitialVars 声明从 mergedVars 取（含调用方 initialVars 作底 + 前序 storeAs 覆盖），未声明的不进 ctx.Vars（避免污染 AI availableData）。
            var declared = script.InitialVars?.Select(v => v.Name).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();
            Dictionary<string, object>? scriptInitialVars = null;
            if (declared.Count > 0)
            {
                scriptInitialVars = new Dictionary<string, object>();
                foreach (var name in declared)
                    if (mergedVars.TryGetValue(name, out var mv))
                        scriptInitialVars[name] = mv;
            }

            var result = await engine.ExecuteAsync(script, effectiveFillData, page, ct, taskId, scriptInitialVars);

            totalDurationMs += result.TotalDurationMs;
            if (result.ExecutionLog != null)
                allLog.AddRange(result.ExecutionLog);
            // 改动5：聚合各脚本 returnData（同 key 后者覆盖），多脚本编排合并返回
            if (result.ReturnData != null)
            {
                mergedReturnData ??= new Dictionary<string, object>();
                foreach (var kv in result.ReturnData)
                    mergedReturnData[kv.Key] = kv.Value;
            }

            if (!result.Success)
            {
                // 编排失败：保留已完成的 vars，报告失败
                await reporter.SendLogAsync($"脚本 [{scriptName}] 执行失败: {result.ErrorMessage}");
                return new ScriptResult
                {
                    Success = false,
                    ExecutionLog = allLog,
                    ErrorMessage = $"脚本 [{scriptName}] 执行失败: {result.ErrorMessage}",
                    Status = "failed",
                    FailureType = result.FailureType,
                    TotalDurationMs = totalDurationMs,
                    Vars = mergedVars,
                    ReturnData = mergedReturnData
                };
            }

            // 组1：合并本脚本的 vars 到 mergedVars（过滤 ReservedVarNames——系统 _ 变量不跨脚本透传）。
            // 用 ReservedVarNames 精确过滤（非 _ 前缀），不误伤用户自定义 _ 开头 storeAs。
            if (result.Vars != null)
            {
                foreach (var kv in result.Vars)
                    if (!ScriptLoader.ReservedVarNames.Contains(kv.Key))
                        mergedVars[kv.Key] = kv.Value;
            }

            await reporter.SendLogAsync($"脚本 [{scriptName}] 执行完成");
        }

        return new ScriptResult
        {
            Success = true,
            ExecutionLog = allLog,
            Status = "completed",
            TotalDurationMs = totalDurationMs,
            Vars = mergedVars,
            ReturnData = mergedReturnData
        };
    }
}
