namespace MovieAgent.Core.Interfaces;

public interface IChatProvider
{
    string Name { get; }
    string ProviderType { get; }
    Task<string> ChatAsync(string userMessage);
    Task<bool> InitializeAsync();
    bool IsAvailable { get; }
    string? LastError { get; }
    event Action<string>? OnStreamDataReceived;
}

public enum ModelProviderType
{
    Ollama,
    DeepSeek,
    LlamaCpp
}

public class ModelConfig
{
    public string Name { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public ModelProviderType ProviderType { get; set; }
    public bool IsDefault { get; set; }
}
