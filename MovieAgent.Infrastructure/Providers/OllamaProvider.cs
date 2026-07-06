using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using MovieAgent.Core.Interfaces;

namespace MovieAgent.Infrastructure.Providers;

public class OllamaProvider : IChatProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _modelName;
    private readonly string _baseUrl;
    private bool _isAvailable;
    private string? _lastError;

    public string Name => "Ollama";
    public string ProviderType => "Ollama";
    public bool IsAvailable => _isAvailable;
    public string? LastError => _lastError;

    public event Action<string>? OnStreamDataReceived;

    public OllamaProvider(string baseUrl, string modelName)
    {
        _baseUrl = baseUrl;
        _modelName = modelName;
        _httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public async Task<bool> InitializeAsync()
    {
        _isAvailable = false;
        _lastError = null;

        try
        {
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
                    _isAvailable = true;
                }
                else
                {
                    var availableModels = string.Join(", ", models.EnumerateArray()
                        .Select(m => m.GetProperty("name").GetString()));
                    _lastError = $"模型 '{_modelName}' 未找到。可用模型: {availableModels}";
                }
            }
            else
            {
                _lastError = $"连接失败: {response.StatusCode}";
            }
        }
        catch (HttpRequestException ex)
        {
            _lastError = $"无法连接到 Ollama: {ex.Message}";
        }
        catch (Exception ex)
        {
            _lastError = $"初始化失败: {ex.Message}";
        }

        return _isAvailable;
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
                options = new { temperature = 0.7, num_predict = 2048, num_ctx = 4096 }
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("/api/chat", content);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

            var result = new StringBuilder();
            string? line;

            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("message", out var message))
                    {
                        if (message.TryGetProperty("content", out var contentElement))
                        {
                            var chunk = contentElement.GetString();
                            if (!string.IsNullOrEmpty(chunk))
                            {
                                result.Append(chunk);
                                OnStreamDataReceived?.Invoke(chunk);
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

    private static string GetSystemPrompt() => """
        你是电影管家"小影"。回答必须基于本地电影库，禁止编造，禁止推荐外部电影。

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
