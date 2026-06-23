namespace MovieAgent.Core.Interfaces;

public interface IConversationMemoryService
{
    void AddMessage(string userId, string userMessage, string agentResponse);

    List<ChatMessage> GetHistory(string userId, int maxMessages = 10);

    string BuildContextPrompt(string userId);

    void ClearHistory(string userId);

    void RemoveMessage(string userId, DateTime timestamp);

    /// <summary>从数据库加载对话历史到内存</summary>
    Task LoadHistoryAsync(string userId);

    /// <summary>将内存中的对话历史持久化到数据库（关闭对话时调用）</summary>
    Task SaveHistoryAsync(string userId);

    /// <summary>压缩旧对话为摘要（调用Ollama生成摘要）</summary>
    Task CompressAndSaveAsync(string userId, string ollamaUrl, string modelName);
}