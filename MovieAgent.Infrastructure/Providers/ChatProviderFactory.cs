using MovieAgent.Core.Interfaces;

namespace MovieAgent.Infrastructure.Providers;

public class ChatProviderFactory
{
    private static IChatProvider? _currentProvider;
    private static ModelConfig? _currentConfig;

    public static IChatProvider? CurrentProvider => _currentProvider;
    public static ModelConfig? CurrentConfig => _currentConfig;

    public static IChatProvider CreateProvider(ModelConfig config)
    {
        _currentConfig = config;

        return config.ProviderType switch
        {
            ModelProviderType.DeepSeek => new DeepSeekProvider(config.ApiKey, config.Name),
            ModelProviderType.Ollama => new OllamaProvider(config.Endpoint, config.Name),
            _ => new OllamaProvider(config.Endpoint, config.Name)
        };
    }

    public static async Task<IChatProvider> InitializeProviderAsync(ModelConfig config)
    {
        var provider = CreateProvider(config);
        await provider.InitializeAsync();
        _currentProvider = provider;
        return provider;
    }

    public static List<ModelConfig> GetDefaultModels()
    {
        return new List<ModelConfig>
        {
            new ModelConfig
            {
                Name = "deepseek-v4-flash",
                Endpoint = "https://api.deepseek.com/v1/chat/completions",
                ApiKey = "",
                ProviderType = ModelProviderType.DeepSeek,
                IsDefault = false
            },
            new ModelConfig
            {
                Name = "deepseek-v4-pro",
                Endpoint = "https://api.deepseek.com/v1/chat/completions",
                ApiKey = "",
                ProviderType = ModelProviderType.DeepSeek,
                IsDefault = false
            },
            new ModelConfig
            {
                Name = "phi3.5:3.8b-mini-instruct-q4_K_M",
                Endpoint = "http://localhost:11434",
                ApiKey = "",
                ProviderType = ModelProviderType.Ollama,
                IsDefault = true
            },
            new ModelConfig
            {
                Name = "llama3.2:3b",
                Endpoint = "http://localhost:11434",
                ApiKey = "",
                ProviderType = ModelProviderType.Ollama,
                IsDefault = false
            },
            //new ModelConfig
            //{
            //    Name = "qwen2.5:3b",
            //    Endpoint = "http://localhost:11434",
            //    ApiKey = "",
            //    ProviderType = ModelProviderType.Ollama,
            //    IsDefault = false
            //}
        };
    }
}
