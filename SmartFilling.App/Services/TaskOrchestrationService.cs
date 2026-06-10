using SmartFilling.Engine.Engine;
using SmartFilling.Engine.Models;

namespace SmartFilling.App.Services;

/// <summary>
/// 录制模式编排服务：管理多脚本录制顺序 + 前置脚本执行。
/// 前置脚本使用 ScriptEngine 确定性执行（0 token），然后在同一浏览器上开始 AI 录制。
/// </summary>
public class TaskOrchestrationService
{
    private readonly ILogger<TaskOrchestrationService> _logger;
    private readonly ScriptService _scriptService;

    public TaskOrchestrationService(
        ILogger<TaskOrchestrationService> logger,
        ScriptService scriptService)
    {
        _logger = logger;
        _scriptService = scriptService;
    }

    /// <summary>
    /// 获取前置脚本列表
    /// </summary>
    public async Task<List<ScriptV2>> LoadPrerequisiteScriptsAsync(List<string> scriptIds)
    {
        var scripts = new List<ScriptV2>();
        foreach (var id in scriptIds)
        {
            var script = _scriptService.GetScript(id);
            if (script != null)
                scripts.Add(script);
            else
                _logger.LogWarning("前置脚本未找到: {ScriptId}", id);
        }
        return scripts;
    }

    /// <summary>
    /// 执行前置脚本（确定性执行，0 token），返回执行后的 vars 合并到录制上下文。
    /// 任一前置脚本失败则抛出异常。
    /// </summary>
    public async Task<Dictionary<string, object>> ExecutePrerequisitesAsync(
        List<ScriptV2> scripts,
        Dictionary<string, object> fillData,
        ScriptEngine engine,
        Microsoft.Playwright.IPage page,
        CancellationToken ct)
    {
        var mergedVars = new Dictionary<string, object>();

        foreach (var script in scripts)
        {
            ct.ThrowIfCancellationRequested();

            _logger.LogInformation("执行前置脚本: {Name}", script.Name);

            var effectiveFillData = new Dictionary<string, object>(fillData);
            foreach (var kv in mergedVars)
                effectiveFillData[kv.Key] = kv.Value;

            var result = await engine.ExecuteAsync(script, effectiveFillData, page, ct);

            if (!result.Success)
                throw new InvalidOperationException($"前置脚本 {script.Name} 执行失败: {result.ErrorMessage}");

            if (result.Vars != null)
                foreach (var kv in result.Vars)
                    mergedVars[kv.Key] = kv.Value;

            _logger.LogInformation("前置脚本 {Name} 执行完成", script.Name);
        }

        return mergedVars;
    }
}
