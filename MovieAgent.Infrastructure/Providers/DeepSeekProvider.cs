using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.IO;
using MovieAgent.Core.Interfaces;

namespace MovieAgent.Infrastructure.Providers;

public class DeepSeekProvider : IChatProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _modelName;
    private bool _isAvailable;
    private string? _lastError;

    public string Name => "DeepSeek";
    public string ProviderType => "DeepSeek";
    public bool IsAvailable => _isAvailable;
    public string? LastError => _lastError;

    public event Action<string>? OnStreamDataReceived;

    public DeepSeekProvider(string apiKey, string modelName)
    {
        _apiKey = apiKey;
        _modelName = modelName;
        
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
    }

    public async Task<bool> InitializeAsync()
    {
        _isAvailable = false;
        _lastError = null;

        try
        {
            var requestBody = new
            {
                model = _modelName,
                messages = new[]
                {
                    new { role = "user", content = "ping" }
                },
                stream = false,
                max_tokens = 10
            };

            var response = await SendRequestAsync(requestBody);
            _isAvailable = response.IsSuccessStatusCode;
            
            if (!_isAvailable)
            {
                var content = await response.Content.ReadAsStringAsync();
                _lastError = $"API 连接失败: {content}";
            }

            return _isAvailable;
        }
        catch (Exception ex)
        {
            _lastError = $"初始化失败: {ex.Message}";
            return false;
        }
    }

    public async Task<string> ChatAsync(string userMessage)
    {
        try
        {
            var requestBody = new
            {
                model = _modelName,
                messages = new[]
                {
                    new { role = "system", content = GetSystemPrompt() },
                    new { role = "user", content = userMessage }
                },
                stream = true,
                max_tokens = 2048,
                temperature = 0.7,
                top_p = 0.95,
                frequency_penalty = 0,
                presence_penalty = 0
            };

            var response = await SendRequestAsync(requestBody);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

            var result = new StringBuilder();
            string? line;

            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: "))
                    continue;

                var jsonData = line[6..];
                if (jsonData == "[DONE]")
                    break;

                try
                {
                    using var doc = JsonDocument.Parse(jsonData);
                    if (doc.RootElement.TryGetProperty("choices", out var choices))
                    {
                        var choice = choices.EnumerateArray().FirstOrDefault();
                        if (choice.TryGetProperty("delta", out var delta))
                        {
                            if (delta.TryGetProperty("content", out var content))
                            {
                                var chunk = content.GetString();
                                if (!string.IsNullOrEmpty(chunk))
                                {
                                    result.Append(chunk);
                                    OnStreamDataReceived?.Invoke(chunk);
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            return result.ToString().Trim();
        }
        catch (Exception ex)
        {
            _lastError = $"聊天错误: {ex.Message}";
            return $"AI 响应出错: {ex.Message}";
        }
    }

    private async Task<HttpResponseMessage> SendRequestAsync(object requestBody)
    {
        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _httpClient.PostAsync("https://api.deepseek.com/v1/chat/completions", content);
    }

    private static string GetSystemPrompt() => """
        你是电影管家"小雪雪"。回答必须基于本地电影库，禁止编造，禁止推荐外部电影。

        回答规则：
        1. 必须使用电影库中的电影标题且电影标题必须用《电影标题》括起来，不管是英文还是中文标题。
        2. 如果用户询问的信息在电影库中不存在，必须直接回复："此类电影或者这一部电影资料在电影库未收录" 严禁反问用户任何问题。
        3. 回答按模板回复。
        
        可用命令：
        /播放 电影名 - 播放指定电影
        /搜索 关键词 - 搜索电影
        /推荐 [关键词] - 智能推荐电影
        /评分 电影名 1-5 - 为电影打分
        /暂停 - 暂停播放
        /继续 - 继续播放
        /切换音轨 - 切换音频
        /加载字幕 - 加载字幕
        """;
}
