using SmartFilling.Engine.Models;

namespace SmartFilling.BackgroundWorker.Models;

public class FillTask
{
    public string TaskId { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string DocumentTypeId { get; set; } = string.Empty;
    public string DocumentTypeName { get; set; } = string.Empty;
    public List<ScriptV2> Scripts { get; set; } = [];
    public Dictionary<string, object> FillData { get; set; } = new();
    public TaskStatus Status { get; set; } = TaskStatus.Pending;
    public long CallbackId { get; set; }
    public string? BillCode { get; set; }
    public Dictionary<string, object>? ReturnData { get; set; }  // J.2：脚本 returnData 聚合（成功数据字典），WS/HTTP 两模式共用；BillCode 保留供 WS 单值兼容
    public ExecutionStats? Stats { get; set; }  // P3-2：AI 统计（completionHandler 发 TaskCompleted 时带 stats；成功路径 Runner 回填 finalResult.Stats）
    public string? FinalScreenshot { get; set; }
    public string? ErrorMessage { get; set; }
    public FailureType? FailureType { get; set; }  // #16 决策4：失败类型（None/Deterministic/AiFallback/AiPhase/Timeout/Cancelled），WS resultData 组装用。AiPhase 为观察②新增（ai phase 主路径失败）
    public DateTime StartTime { get; set; } = DateTime.Now;
}

public enum TaskStatus
{
    Pending,
    Running,
    WaitingForUser,
    Completed,
    Failed
}

public class FillRequest
{
    public string DocumentTypeId { get; set; } = "";
    public string DocumentTypeName { get; set; } = "";
    /// <summary>④-4：脚本原始 JSON 文本列表（传原始文本而非 ScriptV2 对象——对象传输会被 MVC 反序列化丢弃未知字段，补不了 schema 残留字段校验）。Worker StartFill 逐个 LoadFromJson 校验。</summary>
    public List<string> Scripts { get; set; } = [];
    public Dictionary<string, object> FillData { get; set; } = new();
}
