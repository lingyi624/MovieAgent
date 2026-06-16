using MovieAgent.Core.Entities;
using MovieAgent.Core.Models;

namespace MovieAgent.Core.Interfaces;

public interface IHybridSearchService
{
    Task<List<Movie>> SearchAsync(string query, MovieFilter? filter = null, int topK = 10);

    Task<List<Movie>> SearchWithMemoryAsync(string query, List<ChatMessage> history, MovieFilter? filter = null, int topK = 10);
}

public class ChatMessage
{
    public string User { get; set; } = string.Empty;
    public string Agent { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}