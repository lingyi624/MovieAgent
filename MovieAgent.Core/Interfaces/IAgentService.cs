namespace MovieAgent.Core.Interfaces;

/// <summary>
/// AI代理服务接口 - 提供自然语言对话和电影推荐功能
/// 使用 Ollama 运行本地大语言模型
/// </summary>
public interface IAgentService
{
    /// <summary>
    /// 发送聊天消息并获取AI回复
    /// </summary>
    /// <param name="userMessage">用户消息</param>
    /// <returns>AI回复文本</returns>
    Task<string> ChatAsync(string userMessage);

    /// <summary>
    /// 初始化AI服务（连接Ollama，验证模型可用性）
    /// </summary>
    Task InitializeAsync();

    /// <summary>
    /// 重新连接AI服务
    /// </summary>
    /// <returns>是否连接成功</returns>
    Task<bool> ReconnectAsync();

    /// <summary>
    /// AI服务是否可用
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// 最后一次错误信息
    /// </summary>
    string? LastError { get; }
  
}

/// <summary>
/// AI代理响应 - 包含文本回复、意图识别和结构化数据
/// </summary>
public class AgentResponse
{
    /// <summary>AI回复文本</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>识别的用户意图（如 play_movie, search, recommend）</summary>
    public string? Intent { get; set; }

    /// <summary>提取的结构化数据</summary>
    public Dictionary<string, object?>? Data { get; set; }
}
