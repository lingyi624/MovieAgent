using System.ComponentModel.DataAnnotations;

namespace MovieAgent.Core.Entities;

/// <summary>
/// AI对话记录 - 持久化存储对话上下文
/// 关闭对话时压缩保存，下次打开时自动加载
/// </summary>
public class ConversationRecord
{
    [Key]
    public int Id { get; set; }

    /// <summary>用户ID（支持多用户，默认 "default"）</summary>
    [MaxLength(100)]
    public string UserId { get; set; } = "default";

    /// <summary>用户消息</summary>
    [MaxLength(4000)]
    public string UserMessage { get; set; } = string.Empty;

    /// <summary>AI回复</summary>
    [MaxLength(8000)]
    public string AgentResponse { get; set; } = string.Empty;

    /// <summary>消息时间戳</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>是否为压缩后的摘要（当对话过长时压缩为摘要）</summary>
    public bool IsSummary { get; set; }
}