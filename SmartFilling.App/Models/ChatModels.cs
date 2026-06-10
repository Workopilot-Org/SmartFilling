namespace SmartFilling.App.Models;

public class ChatRequest
{
    // 已废弃（2026-07-06 附件随消息发送改造）：附件触发改由 Attachments 非空判定，不再依赖 RequestType=attachment_notification。
    // 字段保留仅为兼容，FillController 不再按此字段分支。
    public string RequestType { get; set; } = "message";
    public string ScriptId { get; set; } = "";
    public string Message { get; set; } = "";
    public string? SessionId { get; set; }
    public List<AttachmentInfo>? Attachments { get; set; }
    public string? DataCollectionPrompt { get; set; }
}

public class ChatResponse
{
    public string SessionId { get; set; } = "";
    public string Reply { get; set; } = "";
    public bool IsComplete { get; set; }
    public Dictionary<string, object>? CollectedData { get; set; }
}
