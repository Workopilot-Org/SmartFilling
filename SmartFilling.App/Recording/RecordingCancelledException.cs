namespace SmartFilling.App.Recording;

/// <summary>
/// 录制取消异常：录制非正常结束（总超时/手动停止/轮次耗尽）时抛出，外抛至 RecordController → TaskCompleted(Cancelled)。
/// 录制引擎原把取消（OperationCanceledException）吞掉 return GetCurrentScript() → 走 TaskCompleted(Success) 伪装录制成功，
/// 用户无法从日志判断是超时还是手动。改为抛此专用异常外抛，如实上报 Cancelled + 区分原因的 errorMessage（保留已录步骤可保存）。
/// 三种非正常结束统一走 Cancelled，与填报 ScriptEngine 的失败/取消语义对称（只有正常 done 才走 Success）。
/// </summary>
public class RecordingCancelledException : Exception
{
    /// <summary>取消原因：总超时 / 手动停止 / 轮次耗尽</summary>
    public RecordingCancelReason Reason { get; }

    public RecordingCancelledException(RecordingCancelReason reason, string message) : base(message)
        => Reason = reason;
}

/// <summary>录制非正常结束的三种原因（区分 errorMessage 文案，前端统一显示"已取消"态）</summary>
public enum RecordingCancelReason
{
    /// <summary>录制总超时（MaxScriptDuration 到期，cts 自动取消）。判据：_cancelReason 为空（手动取消会显式标记）。</summary>
    Timeout,

    /// <summary>用户手动停止（Stop 端点 MarkManualCancel 标记后 Cancel）。</summary>
    ManualStop,

    /// <summary>跑满 MaxRecordTurns 未 done（循环空转耗尽轮次，原走 Success 的 silent-success，现对称走 Cancelled）。</summary>
    MaxTurnsExhausted,
}
