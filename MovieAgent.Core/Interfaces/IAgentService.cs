namespace MovieAgent.Core.Interfaces;

public interface IAgentService
{
    Task<string> ChatAsync(string userMessage);
    Task InitializeAsync();
    Task<bool> ReconnectAsync();
    bool IsAvailable { get; }
    string? LastError { get; }
}

public class AgentResponse
{
    public string Text { get; set; } = string.Empty;
    public string? Intent { get; set; }
    public Dictionary<string, object?>? Data { get; set; }
}