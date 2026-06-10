using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SmartFilling.Engine.Logging;
using SmartFilling.Engine.Models;

namespace SmartFilling.Engine.Engine;

public static class VariableHelper
{
    /// <summary>
    /// 递归把 JsonElement 转为 CLR 托管对象：Object→Dictionary，Array→List，标量→对应类型。
    /// 避免把 JsonElement 原样塞进 Dictionary 导致下游二次序列化/类型丢失（#47；App FillController 原 ConvertJsonElement 已废弃改调本方法，消除复制品）。
    /// </summary>
    public static object NormalizeJsonElement(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.String => (object)(el.GetString() ?? ""),
            JsonValueKind.Number => el.TryGetInt64(out var l) ? (object)l : el.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => "",
            JsonValueKind.Object => el.EnumerateObject().ToDictionary(p => p.Name, p => NormalizeJsonElement(p.Value)),
            JsonValueKind.Array => el.EnumerateArray().Select(NormalizeJsonElement).ToList(),
            _ => el.GetRawText(),
        };
    }

    /// <summary>
    /// 从作用域栈逐层查找并替换 {{varName}} 占位符。
    /// scopeChain: [内层 rowData, 外层 rowData, ..., fillData]
    /// </summary>
    public static string ReplaceVars(string template, List<Dictionary<string, object>> scopeChain, Dictionary<string, object> vars)
    {
        if (string.IsNullOrEmpty(template)) return template;

        return Regex.Replace(template, @"\{\{(\w+(?:\.\w+)*)\}\}", m =>
        {
            var key = m.Groups[1].Value;
            var parts = key.Split('.');

            // 查找根变量
            bool found = false;
            object? val = null;
            foreach (var scope in scopeChain)
            {
                if (scope.TryGetValue(parts[0], out var v)) { val = v; found = true; break; }
            }
            if (!found && vars.TryGetValue(parts[0], out var v2)) { val = v2; found = true; }
            if (!found) return m.Value;

            // 逐层取值
            for (int i = 1; i < parts.Length && val != null; i++)
            {
                if (val is Dictionary<string, object> dict && dict.TryGetValue(parts[i], out var dv))
                    val = dv;
                else
                    return m.Value;
            }
            return val?.ToString() ?? "";
        });
    }

    private static readonly Regex PureVarRegex = new(@"^\{\{(\w+(?:\.\w+)*)\}\}$", RegexOptions.Compiled);

    /// <summary>
    /// 解析模板中的变量引用，返回原始对象值。
    /// 当模板是纯变量引用（如 "{{consumeAttachments}}"）时，直接从作用域链取原始值（byte[]、List、Dictionary等）。
    /// 否则返回 null，调用方应使用 ReplaceVars 处理混合文本。
    /// </summary>
    public static object? ResolveRawValue(string? template, List<Dictionary<string, object>> scopeChain, Dictionary<string, object> vars, ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(template)) return template;
        var m = PureVarRegex.Match(template);
        if (!m.Success) return null;

        var key = m.Groups[1].Value;
        var parts = key.Split('.');

        // 查找根变量
        bool found = false;
        object? val = null;
        foreach (var scope in scopeChain)
        {
            if (scope.TryGetValue(parts[0], out var v)) { val = v; found = true; break; }
        }
        if (!found && vars.TryGetValue(parts[0], out var v2)) { val = v2; found = true; }
        if (!found) return null;

        // 逐层取值（返回原始对象，不 ToString）；数组逐项 pluck 并 flatMap 摊平一维（改动④）
        return PluckPath(val, parts, 1, logger, key);
    }

    /// <summary>
    /// 递归逐层取值（改动④）：
    /// - Dictionary：取字段，缺失→null（记 R2-12 日志，调用方回退文本替换）；
    /// - 数组（IList）：逐项递归取剩余路径，结果 flatMap 摊平一维（{{detailtable.id}}→id 列数组；{{a.b.c}} 多层→一维）；
    /// - 标量但仍有剩余路径→null（记日志）。
    /// logger/fullKey 仅用于诊断日志（R2-12），数组分支内逐项缺失静默跳过（flatMap 语义，避免日志爆炸）。
    /// </summary>
    private static object? PluckPath(object? val, string[] parts, int i, ILogger? logger = null, string? fullKey = null)
    {
        while (i < parts.Length && val != null)
        {
            if (val is Dictionary<string, object> dict)
            {
                if (!dict.TryGetValue(parts[i], out var dv))
                {
                    logger?.LogWarning($"嵌套变量 {{{{{fullKey}}}}} 解析在 '.{parts[i]}' 处中断（字段不存在或值为 null），回退到文本替换");
                    return null;
                }
                val = dv; i++;
            }
            else if (val is System.Collections.IList list)
            {
                // 数组：逐项递归取剩余路径，结果 flatMap 摊平一维
                var plucked = new List<object>();
                foreach (var item in list)
                {
                    var sub = PluckPath(item, parts, i, logger, fullKey);
                    if (sub is System.Collections.IList sl) foreach (var x in sl) plucked.Add(x);
                    else if (sub != null) plucked.Add(sub);                     // 缺失/null 跳过
                }
                return plucked;                                                 // 固定 flatMap（一维）
            }
            else
            {
                logger?.LogWarning($"嵌套变量 {{{{{fullKey}}}}} 解析在 '.{parts[i]}' 处中断（值为标量或 null，非对象），回退到文本替换");
                return null;                                                    // 标量但还有路径 → null
            }
        }
        return val;
    }

    /// <summary>
    /// 从作用域栈获取 loop 数据源（改动①：放宽类型判断，兼容生产链路多形态）。
    /// 生产链路（App/Worker）经 NormalizeJsonElement 反序列化后产出 List<object>（元素运行时是 Dictionary），
    /// 而非强类型 List<Dictionary>——原 `is List<Dictionary>` 因 C# 泛型不变对 List<object> 恒 false，明细表 loop 0 迭代（P0）。
    /// </summary>
    public static List<Dictionary<string, object>> GetLoopRows(string? loopSource, List<Dictionary<string, object>> scopeChain)
    {
        if (loopSource == null) return [];

        foreach (var scope in scopeChain)
        {
            if (!scope.TryGetValue(loopSource, out var rows) || rows == null) continue;
            var normalized = rows switch
            {
                List<Dictionary<string, object>> list => list,                                    // 测试/强类型：行为完全不变
                System.Collections.IList list => NormalizeItems(list),                            // 生产 List<object>（元素运行时是 Dictionary）
                JsonElement je when je.ValueKind == JsonValueKind.Array                            // JsonElement（未经入口归一的残留兜底）
                    => NormalizeItems(je.EnumerateArray().Select(NormalizeJsonElement).ToList()),
                _ => null
            };
            if (normalized != null) return normalized;
        }
        return [];
    }

    /// <summary>
    /// 把 loop 数据源元素归一为 Dictionary 行（改动⑤）：
    /// 结构化行（array+fields，元素是 Dictionary）原样；简单值数组（array+items，元素是标量）包装成 {"item": 标量} 供 loop 内 {{item}} 引用。
    /// （结构化行恰含 item 字段时走原样分支，{{item}} 取该字段，与简单值语义一致，无冲突。）
    /// </summary>
    private static List<Dictionary<string, object>> NormalizeItems(System.Collections.IList list)
        => list.Cast<object>().Select(o => o switch
        {
            Dictionary<string, object> d => d,                                  // 结构化行（array+fields），原样
            _ => new Dictionary<string, object> { { "item", o } }               // 简单值（array+items），包装供 {{item}} 引用
        }).ToList();

    /// <summary>
    /// 根据 field 定义的 transform 规则变换值
    /// </summary>
    public static string? ApplyTransform(string? value, string? transform)
    {
        if (value == null || transform == null) return value;

        return transform switch
        {
            "trim" => value.Trim(),
            "upper" => value.ToUpperInvariant(),
            "lower" => value.ToLowerInvariant(),
            _ => value
        };
    }

    /// <summary>
    /// 按 field 的 format 转换值（改动7：date/number 转换从 transform 移到 format）。
    /// 仅 type=date/number 且 format 非空才转；string/boolean/file/array 的 format 是纯前端展示不触发；TryParse 失败返回原值。
    /// </summary>
    public static string? ApplyFormat(string? value, string? format, string? type)
    {
        if (value == null || string.IsNullOrEmpty(format)) return value;
        return type switch
        {
            "date" => TransformDate(value, format),
            "number" => TransformNumber(value, format),
            _ => value
        };
    }

    /// <summary>
    /// 递归查找 field 定义（含嵌套 field.Fields，修 N5）；未找到返回 null。
    /// 批次1：从 StepExecutor 搬迁为 public，供录制端复用 fieldDef 递归查找（首层查 script.Fields，递归 field.Fields）。
    /// </summary>
    public static FieldDefinition? GetFieldDefinition(string? fieldName, ScriptV2 script)
    {
        if (script == null || fieldName == null) return null;
        return FindField(script.Fields, fieldName);

        static FieldDefinition? FindField(List<FieldDefinition>? fields, string name)
        {
            if (fields == null) return null;
            foreach (var f in fields)
            {
                if (f.Name == name) return f;
                var nested = FindField(f.Fields, name);
                if (nested != null) return nested;
            }
            return null;
        }
    }

    /// <summary>S7 空值判定：null/""/空集合→空，0/false/其他→非空（skipIfDataEmpty 与 data_exists 统一语义）</summary>
    public static bool IsEmptyValue(object? v) => v switch
    {
        null => true,
        string s => string.IsNullOrEmpty(s),
        System.Collections.IList list => list.Count == 0,
        System.Text.Json.JsonElement je => je.ValueKind == System.Text.Json.JsonValueKind.Array && je.GetArrayLength() == 0,
        _ => false
    };

    /// <summary>F.10.3：字段是否存在于作用域链且非空（skipIfDataEmpty / data_exists 共用）</summary>
    public static bool FieldExists(List<Dictionary<string, object>> scopeChain, string? fieldName)
    {
        if (fieldName == null) return false;
        return scopeChain.Any(scope => scope.ContainsKey(fieldName) && !IsEmptyValue(scope[fieldName]));
    }

    /// <summary>
    /// 从 value 中的 {{X}} 推断关联的 field name
    /// </summary>
    public static string? InferFieldFromValue(string? value)
    {
        if (value == null) return null;
        var m = Regex.Match(value, @"\{\{(\w+(?:\.\w+)*)\}\}");
        if (!m.Success) return null;
        var fullKey = m.Groups[1].Value;
        var dotIdx = fullKey.IndexOf('.');
        return dotIdx > 0 ? fullKey[..dotIdx] : fullKey;
    }

    /// <summary>
    /// 解析 upload 的 filePath 值，支持三种格式：单路径、纯路径数组、附件对象数组
    /// 附件对象格式：{name, url, path}，优先用 path（本地路径），path 为空时跳过（需先下载）
    /// </summary>
    public static string[] ResolveUploadValue(object? raw, string rootPath)
    {
        switch (raw)
        {
            case string path:
                return [ResolveFilePath(path, rootPath)];
            case List<object> list when list.Count > 0:
                if (list[0] is Dictionary<string, object>)
                {
                    return list
                        .Where(o => o is Dictionary<string, object>)
                        .Select(o =>
                        {
                            var dict = (Dictionary<string, object>)o;
                            // 优先取 path（下载后的本地路径）
                            if (dict.TryGetValue("path", out var p) && !string.IsNullOrWhiteSpace(p?.ToString()))
                                return ResolveFilePath(p!.ToString()!, rootPath);
                            return null; // path 为空，附件尚未下载
                        })
                        .Where(p => p != null)
                        .ToArray()!;
                }
                return list.Select(v => v?.ToString() ?? "").Where(v => !string.IsNullOrEmpty(v)).ToArray();
            case System.Text.Json.JsonElement je:
                return ResolveUploadValueFromJson(je, rootPath);
            default:
                return [];
        }
    }

    /// <summary>
    /// storeAs 赋值（支持单变量 string 和多变量 object）
    /// </summary>
    public static void StoreVars(Dictionary<string, object> vars, object? storeAs, object? value)
    {
        if (storeAs == null || value == null) return;

        // STJ 把 object? 字段（StepNode.StoreAs）反序列化为 JsonElement，非 C# string/IDictionary。
        // 归一化为 CLR 后 switch 才能命中 string / Dictionary 分支——否则单 storeAs 字符串（JsonElement(String)）
        // 静默漏存（原 switch 仅 case string + case JsonElement(Object)），导致经 JSON 加载的脚本
        // extract/evaluate storeAs 全部不进 vars、returnData 引用全空（生产链路 App→Worker + golden 套件均受影响）。
        if (storeAs is System.Text.Json.JsonElement je)
            storeAs = NormalizeJsonElement(je);
        // 调研#9 修复（2026-06-29）：value 经生产链路也可能是 JsonElement——ai action 的 resultValue 来自 done 工具 result 参数，
        // 经 OpenAiProvider.ParseJsonElement，Object 值保持 JsonElement(Object)（非 CLR Dictionary，见 OpenAiProvider L137 Object→element）。
        // 原仅归一化 storeAs，多变量分支 `case Dictionary when value is Dictionary` 对 JsonElement(Object) 不匹配 → 多变量 storeAs 静默不存（生产 bug，golden 27 硬断言揭穿）。
        // 归一化 value（对称 storeAs）让多变量分支命中；与 ai phase CollectAllStoreAs 的 JsonElement(Object) 分支（ScriptEngine L659）行为一致。
        if (value is System.Text.Json.JsonElement ve)
            value = NormalizeJsonElement(ve);

        switch (storeAs)
        {
            case string varName:
                // 兼容：AI fallback 可能返回 JSON 对象，按变量名提取对应值
                if (value is Dictionary<string, object> resultMap && resultMap.TryGetValue(varName, out var dictVal))
                    vars[varName] = dictVal;
                else
                    vars[varName] = value;
                break;
            case Dictionary<string, object> map when value is Dictionary<string, object> resultMap2:
                // 多 storeAs（storeAs 是 {varName:描述} 对象）：AI fallback 返回 JSON 对象时按变量名逐个提取
                foreach (var kv in map)
                    if (resultMap2.TryGetValue(kv.Key, out var val))
                        vars[kv.Key] = val;
                break;
        }
    }

    /// <summary>
    /// 递归替换 Args 列表中的 {{}} 占位符。string 直接替换，
    /// JsonElement(Array/Object) 递归遍历内部 string 元素替换，
    /// number/boolean/null 不处理直接传入。
    /// </summary>
    public static List<object>? ReplaceArgsVars(List<object>? args, List<Dictionary<string, object>> scopeChain, Dictionary<string, object> vars)
    {
        if (args == null) return null;

        var result = new List<object>(args.Count);
        foreach (var arg in args)
        {
            result.Add(arg switch
            {
                // 改动③：纯变量引用（如 "{{detailtable}}"）透传原始对象（数组/number），非纯变量回退文本替换
                string s => ResolveRawValue(s, scopeChain, vars) ?? (object)ReplaceVars(s, scopeChain, vars),
                System.Text.Json.JsonElement je => ReplaceJsonElementVars(je, scopeChain, vars),
                _ => arg
            });
        }
        return result;
    }

    private static object ReplaceJsonElementVars(System.Text.Json.JsonElement je, List<Dictionary<string, object>> scopeChain, Dictionary<string, object> vars)
    {
        if (je.ValueKind == System.Text.Json.JsonValueKind.String)
            return ReplaceVars(je.GetString() ?? "", scopeChain, vars);

        if (je.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var list = new List<object>();
            foreach (var item in je.EnumerateArray())
                list.Add(ReplaceJsonElementVars(item, scopeChain, vars));
            return list;
        }

        if (je.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            var dict = new Dictionary<string, object>();
            foreach (var prop in je.EnumerateObject())
                dict[prop.Name] = ReplaceJsonElementVars(prop.Value, scopeChain, vars);
            return dict;
        }

        // number / boolean / null
        return je;
    }

    private static string ResolveFilePath(string path, string rootPath)
    {
        if (Path.IsPathRooted(path)) return path;
        return Path.Combine(rootPath, path.TrimStart('/'));
    }

    private static string[] ResolveUploadValueFromJson(System.Text.Json.JsonElement je, string rootPath)
    {
        if (je.ValueKind == System.Text.Json.JsonValueKind.String)
            return [ResolveFilePath(je.GetString()!, rootPath)];

        if (je.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var result = new List<string>();
            foreach (var item in je.EnumerateArray())
            {
                if (item.ValueKind == System.Text.Json.JsonValueKind.String)
                    result.Add(ResolveFilePath(item.GetString()!, rootPath));
                else if (item.ValueKind == System.Text.Json.JsonValueKind.Object && item.TryGetProperty("path", out var pathProp))
                    result.Add(ResolveFilePath(pathProp.GetString()!, rootPath));
            }
            return result.ToArray();
        }
        return [];
    }

    private static string TransformDate(string value, string format)
    {
        if (DateTime.TryParse(value, out var dt))
            return dt.ToString(NormalizeDateFormat(format), CultureInfo.InvariantCulture);
        return value;
    }

    /// <summary>
    /// F3(R1-2)：moment.js 风格日期 format → C# DateTime.ToString 兼容 token。
    /// moment 用大写 Y/D（YYYY/DD），C# 用小写 y/d（yyyy/dd）——大小写相反的转换；
    /// MM/M/ddd/dddd/HH/mm/ss 等两套一致的保持不变。
    /// 按 token 长度降序、大小写敏感替换：先 YYYY 再 YY（避免 YYYY 被 YY 误二次替换），
    /// 先 DD 再 D（避免 DD 被 D 误伤）。小写 d/dd/ddd/dddd 是 moment 星期 token，大小写不同，不受 D→d 影响。
    /// 效果：moment 写法（YYYY-MM-DD / DD/MM/YYYY / dddd）与 C# 写法（yyyy-MM-dd）均正确输出。
    /// 边界：moment 序数型 Do/Mo/Qo/Wo、星期几数字 d、极短缩写 dd（Mo/Tu）C# 无等价 token，不支持（改用 evaluate）。
    /// </summary>
    private static string NormalizeDateFormat(string format) =>
        format.Replace("YYYY", "yyyy").Replace("YY", "yy").Replace("DD", "dd").Replace("D", "d");

    private static string TransformNumber(string value, string format)
    {
        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var num))
            return num.ToString(format, CultureInfo.InvariantCulture);
        return value;
    }
}
