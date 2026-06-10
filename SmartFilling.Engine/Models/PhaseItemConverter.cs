using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartFilling.Engine.Models;

public class PhaseItemConverter : JsonConverter<PhaseItem>
{
    public override PhaseItem? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        // 兼容旧 PascalCase 文件：同时检查 "kind" 和 "Kind"
        if (!root.TryGetProperty("kind", out var kindProp))
        {
            if (!root.TryGetProperty("Kind", out kindProp))
                throw new JsonException("PhaseItem 缺少 'kind' 字段");
        }

        var kind = kindProp.GetString();
        var rawText = root.GetRawText();

        return kind switch
        {
            "step" => JsonSerializer.Deserialize<StepNode>(rawText, options),
            "phase" => JsonSerializer.Deserialize<PhaseNode>(rawText, options),
            _ => throw new JsonException($"未知的 kind 值: {kind}")
        };
    }

    public override void Write(Utf8JsonWriter writer, PhaseItem value, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value, value.GetType(), options));
        writer.WriteStartObject();

        // kind 作为第一个字段（设计文档规范）
        writer.WritePropertyName("kind");
        writer.WriteStringValue(value.Kind);

        // 其余属性按原序输出，跳过 Kind/kind（已写）
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.NameEquals("kind") || prop.NameEquals("Kind")) continue;
            prop.WriteTo(writer);
        }

        writer.WriteEndObject();
    }
}
