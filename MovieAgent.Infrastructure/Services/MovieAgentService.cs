using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using MovieAgent.Core.Interfaces;

namespace MovieAgent.Infrastructure.Services;

public class MovieAgentService : IAgentService
{
    private readonly HttpClient _httpClient;
    private readonly IMovieRepository _movieRepo;
    private readonly IPlayerService _player;
    private string _modelName = "llama3.2";

    public bool IsAvailable { get; private set; }
    public string? LastError { get; private set; }

    public MovieAgentService(IMovieRepository movieRepo, IPlayerService player, string modelUrl, string modelName)
    {
        _movieRepo = movieRepo;
        _player = player;
        _modelName = modelName;
        _httpClient = new HttpClient { BaseAddress = new Uri(modelUrl) };
        
        Debug.WriteLine($"[Agent] Initializing with URL: {modelUrl}, Model: {modelName}");
    }

    public async Task InitializeAsync()
    {
        IsAvailable = false;
        LastError = null;

        try
        {
            Debug.WriteLine($"[Agent] Testing connection...");
            
            var response = await _httpClient.GetAsync("/api/tags");
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var models = doc.RootElement.GetProperty("models");
                
                var modelExists = models.EnumerateArray()
                    .Any(m => m.GetProperty("name").GetString()?.Contains(_modelName, StringComparison.OrdinalIgnoreCase) == true);
                
                if (modelExists)
                {
                    IsAvailable = true;
                    Debug.WriteLine($"[Agent] Connected successfully, model: {_modelName}");
                }
                else
                {
                    var availableModels = string.Join(", ", models.EnumerateArray()
                        .Select(m => m.GetProperty("name").GetString()));
                    LastError = $"模型 '{_modelName}' 未找到。可用模型: {availableModels}";
                    Debug.WriteLine($"[Agent] Model not found: {_modelName}");
                }
            }
            else
            {
                LastError = $"连接失败: {response.StatusCode}";
                Debug.WriteLine($"[Agent] Connection failed: {response.StatusCode}");
            }
        }
        catch (HttpRequestException ex)
        {
            LastError = $"无法连接到 Ollama: {ex.Message}";
            Debug.WriteLine($"[Agent] Connection error: {ex.Message}");
            IsAvailable = false;
        }
        catch (Exception ex)
        {
            LastError = $"初始化失败: {ex.Message}";
            Debug.WriteLine($"[Agent] Init error: {ex.Message}");
            IsAvailable = false;
        }
    }

    public async Task<bool> ReconnectAsync()
    {
        await InitializeAsync();
        return IsAvailable;
    }

    public async Task<string> ChatAsync(string userMessage)
    {
        if (!IsAvailable)
        {
            var localResult = await HandleLocalCommand(userMessage);
            if (localResult != null) return localResult;
            return LastError != null 
                ? $"AI 服务暂不可用: {LastError}" 
                : "AI 服务暂不可用，请检查 Ollama 是否已启动。";
        }

        try
        {
            var localResult = await HandleLocalCommand(userMessage);
            if (localResult != null) return localResult;

            Debug.WriteLine($"[Agent] Sending to Ollama: {userMessage}");
            
            var requestBody = new
            {
                model = _modelName,
                prompt = $"{GetSystemPrompt()}\n\n用户: {userMessage}\n助手:",
                stream = false
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync("/api/generate", content);
            var responseJson = await response.Content.ReadAsStringAsync();
            
            using var doc = JsonDocument.Parse(responseJson);
            var result = doc.RootElement.GetProperty("response").GetString() ?? "";
            
            Debug.WriteLine($"[Agent] Received: {result}");
            
            return string.IsNullOrWhiteSpace(result) 
                ? "抱歉，我无法理解你的请求。" 
                : result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Agent] Chat error: {ex.Message}");
            return $"AI 响应出错: {ex.Message}";
        }
    }

    private async Task<string?> HandleLocalCommand(string userMessage)
    {
        var msg = userMessage.Trim();
        if (msg.StartsWith("播放", StringComparison.OrdinalIgnoreCase))
        {
            var title = msg[2..].Trim().Trim('《', '》', '"');
            var movies = await _movieRepo.SearchAsync(title);
            if (movies.Count > 0)
            {
                try
                {
                    await _player.PlayAsync(movies[0].FilePath);
                    return $"正在播放《{movies[0].Title}》";
                }
                catch { return $"无法播放《{movies[0].Title}》，文件可能不存在。"; }
            }
            return $"未找到电影: {title}";
        }
        return null;
    }

    private static string GetSystemPrompt() => """
        你是一个专业的电影管家，名叫"小影"。你的职责是帮助用户管理和发现电影。
        你可以执行以下操作：
        1. 推荐电影：根据用户的喜好、心情、类型推荐电影
        2. 搜索电影：按标题、类型、演员、导演搜索
        3. 播放电影：启动播放器播放指定的电影
        4. 电影信息：提供电影的详细信息（时长、评分、简介等）
        5. 评分记录：记录用户对电影的评价
        6. 闲聊交流：与用户进行友好的对话

        回复要求：
        - 用中文回复，语气友好热情
        - 回复简洁明了，不超过200字
        - 如果用户想看电影，优先推荐本地库中评分高的
        """;
}