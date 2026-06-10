using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartFilling.BackgroundWorker.Models;

public class WsMessage
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = string.Empty;

    [JsonPropertyName("msgId")]
    public string MsgId { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [JsonPropertyName("payload")]
    public object? Payload { get; set; }
}

public class ExecuteTaskPayload
{
    [JsonPropertyName("taskId")]
    public string TaskId { get; set; } = string.Empty;

    [JsonPropertyName("scriptId")]
    public string ScriptId { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    public Dictionary<string, JsonElement>? Params { get; set; }

    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("fileList")]
    public string? FileList { get; set; }
}

public class TerminatePayload
{
    [JsonPropertyName("taskId")]
    public string TaskId { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("cleanup")]
    public bool Cleanup { get; set; }
}

public class FetchTaskPayload { }

public class HeartbeatPayload
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "IDLE";

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

public class TaskProgressPayload
{
    [JsonPropertyName("taskId")]
    public string TaskId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("currentStep")]
    public string? CurrentStep { get; set; }

    [JsonPropertyName("billCode")]
    public string? BillCode { get; set; }

    [JsonPropertyName("resultData")]
    public object? ResultData { get; set; }

    [JsonPropertyName("screenshot")]
    public string? Screenshot { get; set; }
}

public class LogReportPayload
{
    [JsonPropertyName("taskId")]
    public string TaskId { get; set; } = string.Empty;

    [JsonPropertyName("level")]
    public string Level { get; set; } = "INFO";

    [JsonPropertyName("stepName")]
    public string? StepName { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

public class ApiResult<T>
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("msg")]
    public string Msg { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public T? Data { get; set; }
}

/// <summary>
/// 平台 getScriptContent 接口 data 数组元素模型（照搬 v1 WsMessageModels，:154-176）。
/// 平台返回 {code,msg,data:[ScriptApiResponse,...]}，真正脚本内容在 scirptTotalContent 字符串里（二次反序列化）。
/// ⚠️ 字段拼写为平台原始 scirptTotalContent（"scirpt" 非笔误，v1 忠实保留，v2 必须沿用否则取不到值）。
/// v2 当前仅消费 ScriptTotalContent；保留全字段以对齐 v1 + 未来可用（scriptId/scriptName/remark/prompt/updateTime 为 document 元数据）。
/// </summary>
public class ScriptApiResponse
{
    [JsonPropertyName("scriptId")]
    public string ScriptId { get; set; } = string.Empty;

    [JsonPropertyName("scriptVersion")]
    public string? ScriptVersion { get; set; }

    [JsonPropertyName("scriptName")]
    public string? ScriptName { get; set; }

    [JsonPropertyName("remark")]
    public string? Remark { get; set; }

    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("scirptTotalContent")]
    public string? ScriptTotalContent { get; set; }  // 平台原始拼写 scirpt

    [JsonPropertyName("updateTime")]
    public string? UpdateTime { get; set; }
}
