using Microsoft.Playwright;
using SmartFilling.Engine.Logging;
using SmartFilling.Engine.Models;

namespace SmartFilling.Engine.Engine;

public class ExecutionContext
{
    public IPage Page { get; set; } = null!;
    public IPage ActivePage { get; set; } = null!; // switchTab 后更新
    public string? TaskId { get; init; }            // Worker 层传入
    public string? ScriptId { get; set; }           // ScriptEngine 设置
    public string? PhaseName { get; set; }          // 每进入一个 phase 时更新
    public Dictionary<string, object> Vars { get; } = new();
    public List<StepLog> Log { get; } = [];
    public List<string> CompletedPhases { get; } = [];
    public Dictionary<string, int> GotoCounter { get; } = new();
    public int PhaseJumpCount { get; set; }  // 脚本级 goto toPhase 计数器
    public CancellationToken Ct { get; init; }
    public List<Dictionary<string, object>> ScopeChain { get; } = new(); // 作用域栈
    /// <summary>目标链栈（改动6.4）：栈底=script.Description，每个 phase 入口 push AiGoal/出口 pop。AI fallback instruction 展开整体→外层→当前目标。</summary>
    public Stack<string> GoalStack { get; } = new();

    // AI 统计累积
    public int AiSteps { get; set; }
    public int AiApiCallCount { get; set; }
    public int AiInputTokens { get; set; }
    public int AiOutputTokens { get; set; }

    // 截图统计
    public int SnapshotCount { get; set; }
    public int SnapshotTokens { get; set; }
    public int AiScreenshotCount { get; set; }
    public int AiCallsWithScreenshot { get; set; }
    public int ScreenshotTokens { get; set; }

    // 失败类型追踪：任务级最终结果用
    public FailureType FailureType { get; set; } = FailureType.None;
    /// <summary>标记是否触发过 AI fallback 失败（用于 FailureType 推断）</summary>
    public bool AiFallbackFailed { get; set; }
    /// <summary>标记是否触发过 ai phase 失败（主路径 AI 自主执行失败，非兜底；用于 FailureType 推断为 AiPhase）</summary>
    public bool AiPhaseFailed { get; set; }
}
