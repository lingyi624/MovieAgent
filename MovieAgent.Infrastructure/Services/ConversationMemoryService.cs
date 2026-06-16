using MovieAgent.Core.Interfaces;
using System.Collections.Concurrent;

namespace MovieAgent.Infrastructure.Services;

public class ConversationMemoryService : IConversationMemoryService
{
    private readonly ConcurrentDictionary<string, List<ChatMessage>> _userConversations = new();
    private const int DefaultMaxMessages = 10;

    public void AddMessage(string userId, string userMessage, string agentResponse)
    {
        if (!_userConversations.TryGetValue(userId, out var history))
        {
            history = new List<ChatMessage>();
            _userConversations[userId] = history;
        }

        history.Add(new ChatMessage
        {
            User = userMessage,
            Agent = agentResponse,
            Timestamp = DateTime.UtcNow
        });

        while (history.Count > DefaultMaxMessages)
        {
            history.RemoveAt(0);
        }
    }

    public List<ChatMessage> GetHistory(string userId, int maxMessages = 10)
    {
        if (!_userConversations.TryGetValue(userId, out var history))
        {
            return new List<ChatMessage>();
        }

        return history.Take(maxMessages).ToList();
    }

    public string BuildContextPrompt(string userId)
    {
        var history = GetHistory(userId);
        if (history.Count == 0)
            return string.Empty;

        var contextBuilder = new System.Text.StringBuilder();
        contextBuilder.AppendLine("以下是之前的对话历史，供参考：");
        contextBuilder.AppendLine("---");

        foreach (var msg in history)
        {
            contextBuilder.AppendLine($"用户: {msg.User}");
            contextBuilder.AppendLine($"助手: {msg.Agent}");
            contextBuilder.AppendLine();
        }

        contextBuilder.AppendLine("---");
        contextBuilder.AppendLine("请根据以上历史对话，理解用户当前请求的上下文。");

        return contextBuilder.ToString();
    }

    public void ClearHistory(string userId)
    {
        _userConversations.TryRemove(userId, out _);
    }

    public void RemoveMessage(string userId, DateTime timestamp)
    {
        if (_userConversations.TryGetValue(userId, out var history))
        {
            history.RemoveAll(m => m.Timestamp == timestamp);
        }
    }
}