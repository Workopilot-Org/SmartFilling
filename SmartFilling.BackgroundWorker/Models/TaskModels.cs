using SmartFilling.Engine.Models;

namespace SmartFilling.BackgroundWorker.Models;

public class FillTask
{
    /// <summary>决策11-A：taskId 必填，上游传入稳定业务键，Worker 不自生成（默认空字符串，防止漏改 StartFill 赋值时静默自生成违反约束）。</summary>
    public string TaskId { get; set; } = string.Empty;
    public string DocumentTypeId { get; set; } = string.Empty;
    public string DocumentTypeName { get; set; } = string.Empty;
    public List<ScriptV2> Scripts { get; set; } = [];
    public Dictionary<string, object> FillData { get; set; } = new();
    /// <summary>组3：调用方传入的可变控制变量（可被脚本 storeAs 覆盖）。与 fillData（只读高优先级）分离，进 ctx.Vars 层。入口已过滤 ReservedVarNames + 归一。</summary>
    public Dictionary<string, object>? InitialVars { get; set; }
    public TaskStatus Status { get; set; } = TaskStatus.Pending;
    /// <summary>进度 push 回调地址（per-task，请求体传入；ProgressReport.push=true 且非空才 HTTP POST，第3轮-C2）。</summary>
    public string? ProgressCallbackUrl { get; set; }
    /// <summary>终态 push 回调地址（per-task，请求体传入；CompleteReport.push=true 且非空才 HTTP POST，第3轮-C2）。</summary>
    public string? CompleteCallbackUrl { get; set; }
    public Dictionary<string, object>? ReturnData { get; set; }  // J.2：脚本 returnData 聚合（成功数据字典），HTTP 模式 completionHandler 读后写 TaskResultStore；B4 删 BillCode（WS 平台私有，随 ws 砍）
    public ExecutionStats? Stats { get; set; }  // P3-2：AI 统计（completionHandler 发 TaskCompleted 时带 stats；成功路径 Runner 回填 finalResult.Stats）
    public string? FinalScreenshot { get; set; }
    public string? ErrorMessage { get; set; }
    public FailureType? FailureType { get; set; }  // #16 决策4：失败类型（None/Deterministic/AiFallback/AiPhase/Timeout/Cancelled）
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
    /// <summary>决策11-A：taskId 必填（上游传入稳定业务键），Worker 不自生成。FillController 非空校验返 400。</summary>
    public string TaskId { get; set; } = "";
    public string DocumentTypeId { get; set; } = "";
    public string DocumentTypeName { get; set; } = "";
    /// <summary>④-4：脚本原始 JSON 文本列表（传原始文本而非 ScriptV2 对象——对象传输会被 MVC 反序列化丢弃未知字段，补不了 schema 残留字段校验）。Worker StartFill 逐个 LoadFromJson 校验。</summary>
    public List<string> Scripts { get; set; } = [];
    public Dictionary<string, object> FillData { get; set; } = new();
    /// <summary>组3：调用方传入的可变控制变量（可被脚本 storeAs 覆盖）。Worker FillController 入口过滤 ReservedVarNames + 归一后赋 FillTask.InitialVars。</summary>
    public Dictionary<string, object>? InitialVars { get; set; }
    /// <summary>进度 push 回调地址（可选，请求体传入；ProgressReport.push=true 时 Worker POST 到此）。</summary>
    public string? ProgressCallbackUrl { get; set; }
    /// <summary>终态 push 回调地址（可选，请求体传入；CompleteReport.push=true 时 Worker POST 到此）。</summary>
    public string? CompleteCallbackUrl { get; set; }
}
