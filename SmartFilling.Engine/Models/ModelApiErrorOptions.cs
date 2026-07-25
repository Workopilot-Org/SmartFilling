namespace SmartFilling.Engine.Models;

/// <summary>
/// 模型 API 调用错误处理选项（重试 + 断路器）。
/// 对齐项目 [Context]Options 模式（WorkerBusyRetryOptions、HealthCheckOptions、RerunOptions）——
/// 重试与断路器同属"模型 API 错误处理"范畴，归入统一子对象。
/// 通过 AiOptions.ModelApiErrorOptions 注入，OpenAiProvider 全局生效（覆盖录制 + 回放 + 总结所有 AI 调用）。
/// </summary>
/// <remarks>
/// ⚠️ 默认值变更说明：CircuitBreakerThreshold 默认 5，与 Worker 端既有默认值对齐（TaskRunnerFactory 默认 5）。
/// App 路径无 ModelApiErrorOptions 段时断路器阈值由原 AiOptions.CircuitBreakerThreshold 默认 3 变为 5。
/// 已决策（2026-07-25）：接受此变更，与 Worker 统一。
/// </remarks>
public record ModelApiErrorOptions
{
    /// <summary>单次调用最大重试次数（对齐 StepRetry.Count 表示"重试次数"语义，不含初始调用）。</summary>
    public int MaxRetryCount { get; init; } = 2;

    /// <summary>指数退避基准 ms（RetryBaseInterval * 2^r，r=0,1,…）。Base 前缀明示"基准非固定"，与现有固定 Interval 区分。</summary>
    public int RetryBaseInterval { get; init; } = 1000;

    /// <summary>断路器熔断阈值（连续失败次数达到此值后 SendMessageAsync 入口直接抛 InvalidOperationException）。</summary>
    public int CircuitBreakerThreshold { get; init; } = 5;
}
