namespace SmartFilling.Engine.Models;

public record FieldDefinition
{
    public string Name { get; init; } = "";
    public string Label { get; init; } = "";
    public string Type { get; init; } = "string"; // string/number/date/boolean/file/array
    public string? UiComponent { get; init; } // input/textarea/select/radio/checkbox/upload/click-choose/datepicker/table/hidden
    public string? Source { get; init; } // user/system/computed
    public bool? Required { get; init; }
    public object? DefaultValue { get; init; }
    public List<string>? Options { get; init; }
    public string? Format { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public int? MaxLength { get; init; }
    public string? Pattern { get; init; }
    public string? Description { get; init; }
    public string? Placeholder { get; init; }
    public bool? Multiple { get; init; }
    public FieldItems? Items { get; init; }
    public List<FieldDefinition>? Fields { get; init; } // 嵌套子字段（type: array 时）
    public string? Transform { get; init; } // trim/upper/lower（改动7：date/number 转换移到 Format 字段）
}

public record FieldItems
{
    public string Type { get; init; } = "string";
    public List<string>? Options { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public string? Format { get; init; }
}
