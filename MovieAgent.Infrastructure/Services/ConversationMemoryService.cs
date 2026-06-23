using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MovieAgent.Core.Entities;
using MovieAgent.Core.Interfaces;
using MovieAgent.Infrastructure.Data;

namespace MovieAgent.Infrastructure.Services;

public class ConversationMemoryService : IConversationMemoryService
{
    private readonly ConcurrentDictionary<string, List<ChatMessage>> _userConversations = new();
    private readonly IServiceProvider _serviceProvider;
    private const int DefaultMaxMessages = 20;
    private const int CompressThreshold = 15;

    public ConversationMemoryService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public void AddMessage(string userId, string userMessage, string agentResponse)
    {
        if (!_userConversations.TryGetValue(userId, out var history))
        {
            history = new List<ChatMessage>();
            _userConversations[userId] = history;
        }

        history.Add(new ChatMessage
        {
            User = userMessage,
            Agent = agentResponse,
            Timestamp = DateTime.UtcNow
        });

        while (history.Count > DefaultMaxMessages)
        {
            history.RemoveAt(0);
        }
    }

    public List<ChatMessage> GetHistory(string userId, int maxMessages = 10)
    {
        if (!_userConversations.TryGetValue(userId, out var history))
        {
            return new List<ChatMessage>();
        }

        return history.TakeLast(maxMessages).ToList();
    }

    public string BuildContextPrompt(string userId)
    {
        var history = GetHistory(userId);
        if (history.Count == 0)
            return string.Empty;

        var contextBuilder = new StringBuilder();
        contextBuilder.AppendLine("以下是之前的对话历史，供参考：");
        contextBuilder.AppendLine("---");

        foreach (var msg in history)
        {
            contextBuilder.AppendLine($"用户: {msg.User}");
            contextBuilder.AppendLine($"助手: {msg.Agent}");
            contextBuilder.AppendLine();
        }

        contextBuilder.AppendLine("---");
        contextBuilder.AppendLine("请根据以上历史对话，理解用户当前请求的上下文。");

        return contextBuilder.ToString();
    }

    public void ClearHistory(string userId)
    {
        _userConversations.TryRemove(userId, out _);

        // 异步清除数据库记录
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var records = await db.ConversationRecords
                    .Where(c => c.UserId == userId)
                    .ToListAsync();
                db.ConversationRecords.RemoveRange(records);
                await db.SaveChangesAsync();
            }
            catch { }
        });
    }

    public void RemoveMessage(string userId, DateTime timestamp)
    {
        if (_userConversations.TryGetValue(userId, out var history))
        {
            history.RemoveAll(m => m.Timestamp == timestamp);
        }
    }

    public async Task LoadHistoryAsync(string userId)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var records = await db.ConversationRecords
                .Where(c => c.UserId == userId)
                .OrderByDescending(c => c.Timestamp)
                .Take(DefaultMaxMessages)
                .ToListAsync();

            if (records.Count == 0) return;

            var history = new List<ChatMessage>();
            // 反转顺序以保持时间顺序
            records.Reverse();
            foreach (var r in records)
            {
                if (r.IsSummary)
                {
                    // 摘要消息作为系统消息
                    history.Add(new ChatMessage
                    {
                        User = "[摘要]",
                        Agent = r.AgentResponse,
                        Timestamp = r.Timestamp
                    });
                }
                else
                {
                    history.Add(new ChatMessage
                    {
                        User = r.UserMessage,
                        Agent = r.AgentResponse,
                        Timestamp = r.Timestamp
                    });
                }
            }

            _userConversations[userId] = history;
        }
        catch { }
    }

    public async Task SaveHistoryAsync(string userId)
    {
        if (!_userConversations.TryGetValue(userId, out var history) || history.Count == 0)
            return;

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // 清除旧记录
            var oldRecords = await db.ConversationRecords
                .Where(c => c.UserId == userId)
                .ToListAsync();
            db.ConversationRecords.RemoveRange(oldRecords);

            // 保存新记录
            foreach (var msg in history)
            {
                db.ConversationRecords.Add(new ConversationRecord
                {
                    UserId = userId,
                    UserMessage = msg.User,
                    AgentResponse = msg.Agent,
                    Timestamp = msg.Timestamp,
                    IsSummary = msg.User == "[摘要]"
                });
            }

            await db.SaveChangesAsync();
        }
        catch { }
    }

    public async Task CompressAndSaveAsync(string userId, string ollamaUrl, string modelName)
    {
        if (!_userConversations.TryGetValue(userId, out var history) || history.Count < CompressThreshold)
        {
            await SaveHistoryAsync(userId);
            return;
        }

        try
        {
            // 将旧消息压缩为摘要
            var oldMessages = history.Take(history.Count - 5).ToList();
            var recentMessages = history.TakeLast(5).ToList();

            if (oldMessages.Count == 0)
            {
                await SaveHistoryAsync(userId);
                return;
            }

            var summary = await GenerateSummaryAsync(oldMessages, ollamaUrl, modelName);

            // 保存摘要 + 最近消息
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var oldRecords = await db.ConversationRecords
                .Where(c => c.UserId == userId)
                .ToListAsync();
            db.ConversationRecords.RemoveRange(oldRecords);

            if (!string.IsNullOrEmpty(summary))
            {
                db.ConversationRecords.Add(new ConversationRecord
                {
                    UserId = userId,
                    UserMessage = "[摘要]",
                    AgentResponse = summary,
                    Timestamp = DateTime.UtcNow,
                    IsSummary = true
                });
            }

            foreach (var msg in recentMessages)
            {
                db.ConversationRecords.Add(new ConversationRecord
                {
                    UserId = userId,
                    UserMessage = msg.User,
                    AgentResponse = msg.Agent,
                    Timestamp = msg.Timestamp,
                    IsSummary = false
                });
            }

            await db.SaveChangesAsync();
        }
        catch
        {
            await SaveHistoryAsync(userId);
        }
    }

    private async Task<string> GenerateSummaryAsync(List<ChatMessage> messages, string ollamaUrl, string modelName)
    {
        try
        {
            var client = new HttpClient { BaseAddress = new Uri(ollamaUrl) };
            var sb = new StringBuilder();
            sb.AppendLine("请将以下对话历史压缩为一段简洁的摘要（不超过200字），保留关键信息：");
            sb.AppendLine("---");
            foreach (var msg in messages)
            {
                sb.AppendLine($"用户: {msg.User}");
                sb.AppendLine($"助手: {msg.Agent}");
            }
            sb.AppendLine("---");
            sb.AppendLine("摘要：");

            var request = new 
            { 
                model = modelName, 
                messages = new[]
                {
                    new { role = "system", content = "请将对话历史压缩为一段简洁的摘要，保留关键信息。" },
                    new { role = "user", content = sb.ToString() }
                },
                stream = false,
                options = new { temperature = 0.3, num_predict = 200 }
            };
            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await client.PostAsync("/api/chat", content);

            if (response.IsSuccessStatusCode)
            {
                var responseJson = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseJson);
                var result = doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";
                return result.Trim();
            }
        }
        catch { }
        return string.Empty;
    }
}