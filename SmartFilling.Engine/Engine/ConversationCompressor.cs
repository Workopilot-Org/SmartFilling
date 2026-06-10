using OpenAI.Chat;
using SmartFilling.Engine.Models;

namespace SmartFilling.Engine.Engine;

/// <summary>
/// AI 对话压缩（从 v1 TokenManager 迁移改造，归属 AiActionExecutor）。
/// Turn 分组算法直接复用，受保护消息 ID 保留，旧快照压缩保留。
/// </summary>
public class ConversationCompressor
{
    private readonly HashSet<string> _protectedMessageIds = [];
    private readonly List<string> _snapshotToolCallIds = [];

    public void AddProtectMessageId(string messageId) => _protectedMessageIds.Add(messageId);
    public void RemoveProtectMessageId(string messageId) => _protectedMessageIds.Remove(messageId);
    public void RegisterSnapshotId(string toolCallId) => _snapshotToolCallIds.Add(toolCallId);

    /// <summary>
    /// 压缩旧快照，只保留最新的一个
    /// </summary>
    public void CompressOldSnapshots(List<ChatMessage> messages)
    {
        if (_snapshotToolCallIds.Count <= 1) return;

        var oldSnapshotIds = _snapshotToolCallIds.SkipLast(1).ToHashSet();
        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i] is ToolChatMessage toolMsg && oldSnapshotIds.Contains(toolMsg.ToolCallId))
                messages[i] = new ToolChatMessage(toolMsg.ToolCallId, "[snapshot]");
        }
        _snapshotToolCallIds.RemoveRange(0, _snapshotToolCallIds.Count - 1);
    }

    /// <summary>
    /// 压缩对话历史
    /// </summary>
    public List<ChatMessage> CompressHistory(List<ChatMessage> history, CompressionOptions options)
    {
        if (history.Count <= options.Threshold) return history;

        var preservedMessages = new List<ChatMessage>();

        // 1. 保留系统消息
        preservedMessages.AddRange(history.OfType<SystemChatMessage>());

        // 2. 保留初始用户消息（第一个 Assistant 之前的 User 消息）
        if (options.PreserveInitialUserMessages)
        {
            var firstAssistantIndex = history.FindIndex(m => m is AssistantChatMessage);
            if (firstAssistantIndex > 0)
                preservedMessages.AddRange(history.Take(firstAssistantIndex).OfType<UserChatMessage>());
        }

        // 3. 获取剩余消息
        var remainingMessages = history.Where(m => !preservedMessages.Contains(m)).ToList();

        // 4. 分组为 Turn
        var turns = GroupIntoTurns(remainingMessages);

        // 5. 保留受保护的 Turn + 最近的 Turn
        var protectedTurns = turns.Where(t => IsTurnProtected(t)).ToList();
        var recentTurns = turns.TakeLast(options.MinimumPreserved).ToList();
        var selectedTurns = protectedTurns
            .Concat(recentTurns)
            .Distinct()
            .OrderBy(t => turns.IndexOf(t))
            .ToList();

        // 6. 合并
        preservedMessages.AddRange(selectedTurns.SelectMany(t => t));
        return preservedMessages;
    }

    private bool IsTurnProtected(List<ChatMessage> turn)
    {
        if (_protectedMessageIds.Count == 0) return false;
        return turn.Any(msg =>
            (msg is AssistantChatMessage a && a.ToolCalls.Any(tc => _protectedMessageIds.Contains(tc.Id))) ||
            (msg is ToolChatMessage t && _protectedMessageIds.Contains(t.ToolCallId)));
    }

    private static List<List<ChatMessage>> GroupIntoTurns(List<ChatMessage> messages)
    {
        var turns = new List<List<ChatMessage>>();
        var currentTurn = new List<ChatMessage>();
        var currentToolCallIds = new HashSet<string>();

        foreach (var message in messages)
        {
            currentTurn.Add(message);

            if (message is AssistantChatMessage assistant)
            {
                if (assistant.ToolCalls.Any())
                    currentToolCallIds = new HashSet<string>(assistant.ToolCalls.Select(tc => tc.Id));
                else
                    EndCurrentTurn();
            }
            else if (message is ToolChatMessage tool)
            {
                currentToolCallIds.Remove(tool.ToolCallId);
                if (currentToolCallIds.Count == 0) EndCurrentTurn();
            }
            else if (message is UserChatMessage)
            {
                if (currentTurn.Count > 1)
                {
                    EndCurrentTurn();
                    currentTurn = [message];
                }
            }
        }

        if (currentTurn.Count > 0) turns.Add(currentTurn);
        return turns;

        void EndCurrentTurn()
        {
            if (currentTurn.Count > 0)
            {
                turns.Add(currentTurn);
                currentTurn = [];
                currentToolCallIds.Clear();
            }
        }
    }
}
