using OpenAI.Chat;
using SmartFilling.Engine.Models;

namespace SmartFilling.Engine.Ai;

/// <summary>
/// 纯 AI 客户端接口：只管单次 API 调用，不碰浏览器，不管理循环。
/// </summary>
public interface IAiProvider
{
    /// <summary>
    /// 发送消息并获取 AI 响应（含可选工具定义）
    /// </summary>
    Task<AiResponse> SendMessageAsync(
        List<ChatMessage> messages,
        List<ChatTool>? tools = null,
        CancellationToken ct = default);
}
