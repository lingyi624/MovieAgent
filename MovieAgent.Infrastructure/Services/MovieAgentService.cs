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
    private readonly IConversationMemoryService _memoryService;
    private readonly IHybridSearchService _hybridSearch;
    private string _modelName = "llama3.2";
    private const string DefaultUserId = "default";

    public bool IsAvailable { get; private set; }
    public string? LastError { get; private set; }

    public MovieAgentService(IMovieRepository movieRepo, IPlayerService player, 
        IConversationMemoryService memoryService, IHybridSearchService hybridSearch,
        string modelUrl, string modelName)
    {
        _movieRepo = movieRepo;
        _player = player;
        _memoryService = memoryService;
        _hybridSearch = hybridSearch;
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
        var localResult = await HandleLocalCommand(userMessage);
        if (localResult != null)
        {
            _memoryService.AddMessage(DefaultUserId, userMessage, localResult);
            return localResult;
        }

        if (!IsAvailable)
        {
            var errorMsg = LastError != null 
                ? $"AI 服务暂不可用: {LastError}" 
                : "AI 服务暂不可用，请检查 Ollama 是否已启动。";
            _memoryService.AddMessage(DefaultUserId, userMessage, errorMsg);
            return errorMsg;
        }

        try
        {
            Debug.WriteLine($"[Agent] Sending to Ollama: {userMessage}");
            
            var context = _memoryService.BuildContextPrompt(DefaultUserId);
            var prompt = $"{GetSystemPrompt()}\n\n{context}\n\n用户: {userMessage}\n助手:";
            
            var requestBody = new
            {
                model = _modelName,
                prompt = prompt,
                stream = false
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync("/api/generate", content);
            var responseJson = await response.Content.ReadAsStringAsync();
            
            using var doc = JsonDocument.Parse(responseJson);
            var result = doc.RootElement.GetProperty("response").GetString() ?? "";
            
            Debug.WriteLine($"[Agent] Received: {result}");
            
            var finalResult = string.IsNullOrWhiteSpace(result) 
                ? "抱歉，我无法理解你的请求。" 
                : result;
            
            _memoryService.AddMessage(DefaultUserId, userMessage, finalResult);
            
            return finalResult;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Agent] Chat error: {ex.Message}");
            var errorMsg = $"AI 响应出错: {ex.Message}";
            _memoryService.AddMessage(DefaultUserId, userMessage, errorMsg);
            return errorMsg;
        }
    }

    private async Task<string?> HandleLocalCommand(string userMessage)
    {
        var msg = userMessage.Trim().ToLower();
        
        if (msg.StartsWith("播放") || msg.StartsWith("/play"))
        {
            var title = msg.StartsWith("/play") 
                ? msg[5..].Trim().Trim('《', '》', '"')
                : msg[2..].Trim().Trim('《', '》', '"');
            
            var movies = await _hybridSearch.SearchAsync(title);
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

        if (msg.StartsWith("/搜索") || msg.StartsWith("/search"))
        {
            var query = msg.StartsWith("/search") ? msg[7..].Trim() : msg[3..].Trim();
            var movies = await _hybridSearch.SearchAsync(query, topK: 5);
            if (movies.Count > 0)
            {
                var result = string.Join("\n", movies.Select((m, i) => $"{i + 1}. 《{m.Title}》 - 评分: {m.Rating}"));
                return $"找到以下电影：\n{result}";
            }
            return $"未找到相关电影: {query}";
        }

        if (msg.StartsWith("/推荐") || msg.StartsWith("/recommend"))
        {
            var query = msg.StartsWith("/recommend") ? msg[10..].Trim() : msg[3..].Trim();
            var history = _memoryService.GetHistory(DefaultUserId);
            var movies = await _hybridSearch.SearchWithMemoryAsync(query, history, topK: 5);
            if (movies.Count > 0)
            {
                var result = string.Join("\n", movies.Select((m, i) => $"{i + 1}. 《{m.Title}》 ({m.ReleaseYear})"));
                return $"为你推荐：\n{result}";
            }
            return "暂无推荐电影";
        }

        if (msg.StartsWith("/评分") || msg.StartsWith("/rate"))
        {
            var parts = msg.StartsWith("/rate") ? msg[6..].Trim().Split(' ') : msg[3..].Trim().Split(' ');
            if (parts.Length >= 2 && int.TryParse(parts[^1], out int rating) && rating >= 1 && rating <= 5)
            {
                var title = string.Join(" ", parts.Take(parts.Length - 1));
                var movies = await _movieRepo.SearchAsync(title);
                if (movies.Count > 0)
                {
                    movies[0].UserRating = rating;
                    await _movieRepo.UpdateAsync(movies[0]);
                    return $"已为《{movies[0].Title}》打 {rating} 星";
                }
            }
            return "评分格式错误，请使用：/评分 电影名 1-5";
        }

        if (msg.StartsWith("/清空") || msg.StartsWith("/clear"))
        {
            _memoryService.ClearHistory(DefaultUserId);
            return "对话历史已清空";
        }

        if (msg.StartsWith("/帮助") || msg.StartsWith("/help"))
        {
            return """
                可用命令：
                /播放 电影名 - 播放指定电影
                /搜索 关键词 - 搜索电影
                /推荐 [关键词] - 智能推荐电影
                /评分 电影名 1-5 - 为电影打分
                /清空 - 清空对话历史
                /帮助 - 显示此帮助
                """;
        }

        return null;
    }

    private static string GetSystemPrompt() => """
        你是一个专业的电影管家，名叫"小影"。你的职责是帮助用户管理和发现电影。
        
        可用命令：
        - /play 电影名 - 播放电影
        - /search 关键词 - 搜索电影
        - /recommend - 推荐电影
        - /rate 电影名 评分 - 为电影打分
        
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
        - 参考对话历史理解用户上下文
        """;
}