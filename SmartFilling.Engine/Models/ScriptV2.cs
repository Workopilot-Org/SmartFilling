namespace SmartFilling.Engine.Models;

public record ScriptV2
{
    public int Version { get; init; } = 2;
    public string ScriptId { get; init; } = "";
    public string? DocumentTypeId { get; init; }
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public ScriptSettings? Settings { get; init; }
    public List<PhaseItem> Phases { get; init; } = [];
    public List<FieldDefinition> Fields { get; init; } = [];

    /// <summary>脚本顶层可选：数据返回映射。key=返回字段名，value=占位符 {{var}}（取 vars 原始对象）或任意 JSON 字面值。由录制 AI 据任务描述生成；未声明则不返回。</summary>
    public Dictionary<string, object>? ReturnData { get; init; }
}
