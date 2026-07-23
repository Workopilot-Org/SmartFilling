namespace SmartFilling.App.Recording;

/// <summary>
/// dreamy E：bizValues 录制期业务值参数化（属性锚点/拼接/全字段搜替换）的条目。
/// AI 传 bizValues:[{value,field}]（value=实际业务值，field=已注册字段名），代码在 selector/value/url/key 等字段
/// 搜替换 value 片段为 {{field}} 占位符（保留前缀，如 dc_021→dc_{{companyCode}}），对齐回放端 ReplaceVars。
/// 与 A/B（文本锚点/整值参数化）互补：A/B 只覆盖 selector 文本锚点 + fill/type/select 整值，
/// bizValues 覆盖属性锚点（@id='dc_021'）/拼接（dc_021）/url/key/code/folder 等 含业务片段的全字段。
/// STJ 反序列化 record 主构造函数参数（.NET 8 支持）。可见性 public（CS0051 编译约束——ExecuteOperateAsync 是 public 方法，参数 List&lt;BizValue&gt;? 的元素类型必须 public；详见下方注释）。
/// 经 GetObject&lt;List&lt;BizValue&gt;&gt; 反序列化（JsonElement(Array).GetRawText → JsonSerializer.Deserialize，PropertyNameCaseInsensitive=true 匹配 camelCase {value,field}）。
/// </summary>
// 注：BizValue 需 public——ExecuteOperateAsync 是 public 方法，参数 List<BizValue>? 的元素类型必须 public（CS0051 编译约束）。
// App.Tests 经 InternalsVisibleTo 可见，但 public 类型对外也可见（BizValue 是录制工具参数，非脚本模型字段，不进 script-v2.json schema）。
public record BizValue(string Value, string Field);
