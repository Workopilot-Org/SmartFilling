namespace SmartFilling.App.Models;

public class ChatRequest
{
    // 已废弃（2026-07-06 附件随消息发送改造）：附件触发改由 Attachments 非空判定，不再依赖 RequestType=attachment_notification。
    // 字段保留仅为兼容，FillController 不再按此字段分支。
    public string RequestType { get; set; } = "message";
    public string DocumentTypeId { get; set; } = "";  // 原 ScriptId，改名后语义清晰（实际传单据类型 ID）
    public string Message { get; set; } = "";
    public string? SessionId { get; set; }
    public List<AttachmentInfo>? Attachments { get; set; }
    public string? DataCollectionPrompt { get; set; }
    /// <summary>多脚本字段合并策略：空=首次请求检测冲突返回 / "first-wins"=保留首定义 / "last-wins"=保留末定义。用户决策 3。</summary>
    public string? MergeStrategy { get; set; }
}

public class ChatResponse
{
    public string SessionId { get; set; } = "";
    public string Reply { get; set; } = "";
    public bool IsComplete { get; set; }
    public Dictionary<string, object>? CollectedData { get; set; }
    /// <summary>组5 (5.4)：AI 收集的初始变量（可变控制变量，可被脚本 storeAs 覆盖）。数据收集提示词驱动三档收集。</summary>
    public Dictionary<string, object>? InitialVars { get; set; }
}
