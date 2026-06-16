namespace MovieAgent.Core.Interfaces;

public interface IConversationMemoryService
{
    void AddMessage(string userId, string userMessage, string agentResponse);

    List<ChatMessage> GetHistory(string userId, int maxMessages = 10);

    string BuildContextPrompt(string userId);

    void ClearHistory(string userId);

    void RemoveMessage(string userId, DateTime timestamp);
}