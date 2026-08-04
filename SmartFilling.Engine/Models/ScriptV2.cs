namespace SmartFilling.Engine.Models;

public record ScriptV2
{
    public int Version { get; init; } = 2;
    public string ScriptId { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public ScriptSettings? Settings { get; init; }
    public List<PhaseItem> Phases { get; init; } = [];
    public List<FieldDefinition> Fields { get; init; } = [];

    /// <summary>脚本顶层可选：数据返回映射。key=返回字段名，value=占位符 {{var}}（取 vars 原始对象）或任意 JSON 字面值。由录制 AI 据任务描述生成；未声明则不返回。</summary>
    public Dictionary<string, object>? ReturnData { get; init; }

    /// <summary>组3：本脚本依赖的外部变量声明（调用方 InitialVars + 前序脚本 storeAs 透传）。
    /// 声明 name 供 ValidatePlaceholders 放行 {{name}}，供 TaskOrchestrationService 按 name 过滤注入，供 DefaultValue 兜底。
    /// 与 fields 的区别：fields 是表单字段（source=user/system，AI 收集/只读），initialVars 是外部变量（可变，脚本可 storeAs 覆盖）。</summary>
    public List<InitialVar>? InitialVars { get; init; }
}

/// <summary>组3：外部变量声明（name 必填，description/defaultValue 可选）。JSON camelCase 经 ScriptLoader CamelCase policy 反序列化。</summary>
public record InitialVar
{
    /// <summary>变量名（合法标识符 ^[a-zA-Z_]\w*$，必填）。不与 ReservedVarNames/SystemCredentialKeys/fields name 冲突。</summary>
    public string Name { get; init; } = "";
    /// <summary>描述（可选，文档化用途，不影响运行）。</summary>
    public string? Description { get; init; }
    /// <summary>默认值（可选，任意 JSON 字面值）。透传值为空（无人提供）时兜底；优先级：透传值 > 前序 storeAs > defaultValue。</summary>
    public object? DefaultValue { get; init; }
}
