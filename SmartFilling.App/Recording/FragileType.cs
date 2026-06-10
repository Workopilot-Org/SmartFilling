namespace SmartFilling.App.Recording;

/// <summary>
/// D-A 集合机制（a-a-4 计划，结论1/2）：6 类 selector/iframe 检测点全独立。
/// acceptFragile（用户选(f)，跳 AssessFragility 脆弱性校验）与 acceptAsIs（用户选(h)，跳存在性校验）
/// 两集合共享此枚举，存 (FragileType, 值) 元组——防换值误接受 + 让换值触发重新求助。
/// selector 原值直接存；iframe 链序列化为 string.Join(" > ", chain) 防与 selector 值碰撞。
/// </summary>
public enum FragileType
{
    StepSelector,
    StepIframe,
    DetectSelector,
    DetectIframe,
    FallbackSelector,
    FallbackIframe,
}
