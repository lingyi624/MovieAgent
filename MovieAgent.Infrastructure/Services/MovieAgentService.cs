using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using MovieAgent.Core.Interfaces;
using MovieAgent.Infrastructure.Providers;

namespace MovieAgent.Infrastructure.Services;

public class MovieAgentService : IAgentService
{
    private readonly IMovieRepository _movieRepo;
    private readonly IMovieUpdateService? _movieUpdateService;
    private readonly IPlayerService _player;
    private readonly IConversationMemoryService _memoryService;
    private readonly IHybridSearchService _hybridSearch;
    private readonly IVectorDatabaseService _vectorDb;
    private IChatProvider? _chatProvider;
    private readonly ModelConfig _modelConfig;
    private const string DefaultUserId = "default";

    public bool IsAvailable { get; private set; }
    public string? LastError { get; private set; }

    public MovieAgentService(IMovieRepository movieRepo, IMovieUpdateService? movieUpdateService, IPlayerService player, 
        IConversationMemoryService memoryService, IHybridSearchService hybridSearch,
        IVectorDatabaseService vectorDb, IConfiguration config)
    {
        _movieRepo = movieRepo;
        _movieUpdateService = movieUpdateService;
        _player = player;
        _memoryService = memoryService;
        _hybridSearch = hybridSearch;
        _vectorDb = vectorDb;
        
        var providerType = Enum.TryParse<ModelProviderType>(config["AI:Provider"] ?? "Ollama", out var type) 
            ? type 
            : ModelProviderType.Ollama;
        
        _modelConfig = new ModelConfig
        {
            Name = config["AI:ModelName"] ?? "phi3.5:3.8b-mini-instruct-q4_K_M",
            Endpoint = providerType switch
            {
                ModelProviderType.DeepSeek => config["AI:DeepSeekUrl"] ?? "https://api.deepseek.com/v1",
                _ => config["AI:ModelUrl"] ?? "http://localhost:11434"
            },
            ApiKey = config["AI:ApiKey"] ?? "",
            ProviderType = providerType
        };
        
        Debug.WriteLine($"[Agent] Initializing with Provider: {_modelConfig.ProviderType}, Model: {_modelConfig.Name}, Endpoint: {_modelConfig.Endpoint}");
    }
    public string? ModelName => _modelConfig.Name;

    public async Task InitializeAsync()
    {
        IsAvailable = false;
        LastError = null;

        try
        {
            Debug.WriteLine($"[Agent] Testing connection...");
            
            _chatProvider = ChatProviderFactory.CreateProvider(_modelConfig);
            IsAvailable = await _chatProvider.InitializeAsync();  
            LastError = _chatProvider.LastError;
            
            if (IsAvailable)
            {
                _chatProvider.OnStreamDataReceived += OnStreamDataReceived;
                Debug.WriteLine($"[Agent] Connected successfully, provider: {_chatProvider.Name}, model: {_modelConfig.Name}");
            }
            else
            {
                Debug.WriteLine($"[Agent] Connection failed: {LastError}");
            }
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

        if (!IsAvailable || _chatProvider == null)
        {
            var errorMsg = LastError != null 
                ? $"AI 服务暂不可用: {LastError}" 
                : "AI 服务暂不可用，请检查配置是否正确。";
            _memoryService.AddMessage(DefaultUserId, userMessage, errorMsg);
            return errorMsg;
        }

        try
        {
            Debug.WriteLine($"[Agent] Sending to {_chatProvider.Name}: {userMessage}");
           
            var movieContext = await BuildMovieContextAsync(userMessage);
            var systemPrompt = GetSystemPrompt();
            var fullMessage = $"{systemPrompt}\n\n{movieContext}\n\n用户查询: {userMessage}";
            
            var response = await _chatProvider.ChatAsync(fullMessage);
            
            Debug.WriteLine($"[Agent] Received: {response.Substring(0, Math.Min(50, response.Length))}...");
            _memoryService.AddMessage(DefaultUserId, userMessage, response);
            
            return response;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Agent] Chat error: {ex.Message}");
            var errorMsg = $"AI 响应出错: {ex.Message}";
            _memoryService.AddMessage(DefaultUserId, userMessage, errorMsg);
            return errorMsg;
        }
    }

    public event Action<string>? OnStreamDataReceived;

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
                    
                    // 使用统一更新服务，同步向量数据库
                    if (_movieUpdateService != null)
                    {
                        await _movieUpdateService.UpdateMovieWithVectorAsync(movies[0]);
                    }
                    else
                    {
                        await _movieRepo.UpdateAsync(movies[0]);
                    }
                    
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
        你是电影管家“小影”。回答必须基于本地电影库，禁止编造，禁止推荐外部电影。

        回答规则：
        1. 必须使用电影库中的电影标题且电影标题必须用《电影标题》括起来，不管是英文还是中文标题。
        2. 如果用户询问的信息在电影库中不存在（如具体上映日期），必须直接回复：“此类电影或者这一部电影资料在电影库未收录” 严禁反问用户任何问题。
        3. 回答按模板回复。
         

        示例：
        用户: "电影标题"
        助手: "《电影标题》
               年份: XX | 评分:XX | 时长: XX 
               类型: XX
               导演: XX
               主演: XX
               简介: XX"

        用户: "播放指环王"
        助手: "正在播放《指环王1：护戒使者》。"

        用户: "有哪些4K电影推荐"
        助手: "1. 《电影标题1》 - 年份 XX，类型 XX，导演 XX，评分XX分
               2. 《电影标题2》 - 年份 XX，类型 XX，导演 XX，评分XX分"

        用户: "推荐一部科幻片"
        助手: "《电影标题》
               年份: XX | 评分:XX| 时长: XX 
               类型: XX
               导演: XX
               主演: XX
               简介: XX"
        
        用户: "给盗梦空间打5星"
        助手: "已经给《盗梦空间》打5星。"
        
        """;
    // 查询重写提示词
    private const string QueryRewritePrompt = """
你是一个电影搜索引擎的查询优化助手。你的任务是将用户的自然语言查询重写为关键词列表，用于向量搜索。

规则：
1. 只输出用空格分隔的关键词，不要包含任何解释、序号、前缀或后缀。
2. 保留原查询的核心实体（电影名、人名、类型）。
3. 不要添加无关词（如“电影”、“片”、“推荐”等）。 
4. 输出不超过10个关键词。

示例： 

用户: "我想看XX导演的电影"
重写结果: XX  

用户: "功夫"
重写结果: 功夫

用户: "推荐一部科幻片"
重写结果: 科幻片

用户查询: {query}
重写结果:
""";

    private async Task<string> BuildMovieContextAsync(string userMessage)
    {
        try
        {
            // 1. 查询重写：使用AI将用户查询优化为更适合向量搜索的文本
            //var rewrittenQuery = await RewriteQueryAsync(userMessage);
            //Debug.WriteLine($"[Agent] Original query: {userMessage}");
            //Debug.WriteLine($"[Agent] Rewritten query: {rewrittenQuery}");

            // 2. 使用重写后的查询进行搜索
            var movies = await _hybridSearch.SearchAsync(userMessage, topK: 5);
            if (movies.Count == 0) 
                return "【电影库检索结果】\n未找到相关电影，请尝试其他关键词。\n---\n";

            var sb = new StringBuilder();
            sb.AppendLine("【电影库检索结果】");
            sb.AppendLine("以下是从本地电影库中检索到的相关电影信息，回答时请参考：");
            sb.AppendLine("---");
            
            foreach (var m in movies)
            {
                var genres = ParseGenres(m.Genres);
                var cast = m.Cast != null && m.Cast.Length > 50 ? m.Cast[..50] + "..." : m.Cast;
                
                sb.AppendLine($"  电影标题《{m.Title}》");
                sb.AppendLine($"  年份: {m.ReleaseYear} | 评分: {m.Rating} | 时长: {FormatRuntime(m.Runtime)}");
                sb.AppendLine($"  类型: {genres}");
                sb.AppendLine($"  导演: {m.Director ?? "未知"}");
                if (!string.IsNullOrEmpty(cast))
                    sb.AppendLine($"  主演: {cast}");
                if (!string.IsNullOrWhiteSpace(m.Overview))
                {
                    var overview = m.Overview.Length > 150 ? m.Overview[..150] + "..." : m.Overview;
                    sb.AppendLine($"  简介: {overview}");
                }
                sb.AppendLine();
            }
            sb.AppendLine("---");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Agent] BuildMovieContext error: {ex.Message}");
            return "【电影库检索失败】\n无法获取电影信息，请稍后重试。\n---\n";
        }
    }

    /// <summary>
    /// 使用AI重写查询，提升向量搜索效果
    /// </summary>
    private async Task<string> RewriteQueryAsync(string query)
    {
        try
        {
            var request = new
            {
                model = _modelConfig.Name,
                messages = new[]
                {
                    new { role = "system", content = QueryRewritePrompt },
                    new { role = "user", content = query }
                },
                stream = false,
                options = new { temperature = 0.1, num_predict = 80, num_ctx = 256 }
            };

            using var httpClient = new HttpClient { BaseAddress = new Uri(_modelConfig.Endpoint) };
            var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync("/api/chat", content);
            var responseString = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"原始响应: {responseString}");
            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine($"[Agent] Query rewrite failed: {response.StatusCode}");
                return query; // 降级：返回原始查询
            }

            using var doc = JsonDocument.Parse(responseString);
            if (doc.RootElement.TryGetProperty("message", out var messageObj) && 
                messageObj.TryGetProperty("content", out var responseText))
            {
                var rewritten = responseText.GetString()?.Trim() ?? query;
                // 清理可能的引号和多余空白
                rewritten = rewritten.Trim('"', ' ', '\n', '\r');
                return string.IsNullOrEmpty(rewritten) ? query : rewritten;
            }

            return query;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Agent] Query rewrite error: {ex.Message}");
            return query; // 降级：返回原始查询
        }
    }

    private string ParseGenres(string? genresJson)
    {
        if (string.IsNullOrEmpty(genresJson)) return "未知";
        
        try
        {
            var genres = JsonSerializer.Deserialize<List<string>>(genresJson);
            return genres != null && genres.Any() ? string.Join("、", genres) : "未知";
        }
        catch
        {
            return genresJson;
        }
    }

    private string FormatRuntime(int? runtime)
    {
        if (!runtime.HasValue) return "未知";
        var hours = runtime.Value / 60;
        var minutes = runtime.Value % 60;
        return hours > 0 ? $"{hours}小时{minutes}分钟" : $"{minutes}分钟";
    }
}