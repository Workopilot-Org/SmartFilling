using SmartFilling.Engine.Models;

namespace SmartFilling.Engine.Ai;

/// <summary>
/// 敏感字段识别（F8 + #3 B 脱敏总原则统一入口）。
/// 双轨识别：① script.Fields 中 Source==system 的字段名；② key 名单兜底（password/username/token/secret/apikey/api_key，Worker 注入的凭据可能不在 fields）。
/// ScriptEngine（instruction 脱敏 BuildStepForAi / availableData BuildMaskedAllData）+ AiActionExecutor（toolResult/evaluate 脱敏 CollectSensitiveValues）共用，
/// 避免名单/判据多处各写一套致漏脱敏（TD-P2 抽取，原 ScriptEngine + AiActionExecutor 两处重复定义合并为此单一入口）。
/// </summary>
internal static class SensitiveKeys
{
    /// <summary>敏感 key 名单兜底（与 source=system 双轨，这些 key 可能不在 fields 定义）。</summary>
    public static readonly HashSet<string> KeyNames = new(StringComparer.OrdinalIgnoreCase) { "password", "username", "token", "secret", "apikey", "api_key" };

    /// <summary>判断字段是否敏感（source=system 或 key 名单）。给 AI 路径脱敏判据（日志路径不脱敏）。</summary>
    public static bool IsSensitive(string? key, ScriptV2? script)
        => !string.IsNullOrEmpty(key)
           && (script?.Fields?.Any(f => f.Name == key && f.Source == "system") == true
               || KeyNames.Contains(key!));
}
