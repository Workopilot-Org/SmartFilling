namespace SmartFilling.App.Models;

/// <summary>
/// 脚本列表项（扁平 DTO，不嵌套 {script:{...}}）——前端 app.js 直接 s.scriptId/s.name 不变，加 s.hasErrors 标记
/// 校验失败的脚本（前端拼「⚠️」名提醒用户）。HasErrors 反映 schema 或业务任一层校验失败
/// （schema 补全后含未知字段/类型错——GetAllScripts 合并 ValidateAndGetErrors + ValidateSchemaAndGetErrors，未知字段经原始 json 被抓到）。
/// </summary>
public record ScriptListItem(string ScriptId, string Name, bool HasErrors);
