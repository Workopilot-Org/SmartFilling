using System.Text.Json;
using System.Text.Json.Serialization;
using SmartFilling.Engine.Engine;

namespace SmartFilling.BackgroundWorker;

/// <summary>
/// #13/O.3.1：把 MVC 默认反序列化为 JsonElement 的 object 字段还原为 CLR 托管对象
///（Object→Dictionary，Array→List，标量→对应类型），让 AttachmentService 的
/// case Dictionary&lt;string,object&gt;/List&lt;object&gt; 命中（原 MVC 默认绑定还原成 JsonElement，
/// AttachmentService 两个 case 全 miss → 嵌套附件下载必失败）。
/// 复用 Engine.VariableHelper.NormalizeJsonElement 逻辑（与 App FillController 原等价 ConvertJsonElement 同源，后者已废弃改调同一方法）。
/// </summary>
public class ObjectToClrConverter : JsonConverter<object>
{
    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        return VariableHelper.NormalizeJsonElement(doc.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, value?.GetType() ?? typeof(object), options);
    }
}
