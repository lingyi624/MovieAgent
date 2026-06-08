using System.Text.Json;
using MovieAgent.Core.Entities;
using MovieAgent.Core.Interfaces;

namespace MovieAgent.Infrastructure.Services;

public interface ITagService
{
    Task AddTagAsync(int movieId, string tag);
    Task RemoveTagAsync(int movieId, string tag);
    Task<List<string>> GetTagsAsync(int movieId);
    Task<List<(string Tag, int Count)>> GetTagStatisticsAsync();
    Task AddEmotionTagsAsync(int movieId);
}

public class TagService : ITagService
{
    private readonly IMovieRepository _movieRepo;
    private readonly IAgentService _agentService;

    public TagService(IMovieRepository movieRepo, IAgentService agentService)
    {
        _movieRepo = movieRepo;
        _agentService = agentService;
    }

    public async Task AddTagAsync(int movieId, string tag)
    {
        var movie = await _movieRepo.GetByIdAsync(movieId);
        if (movie == null) return;

        var tags = GetTagsFromMovie(movie);
        if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
        {
            tags.Add(tag);
            movie.Tags = JsonSerializer.Serialize(tags);
            await _movieRepo.UpdateAsync(movie);
        }
    }

    public async Task RemoveTagAsync(int movieId, string tag)
    {
        var movie = await _movieRepo.GetByIdAsync(movieId);
        if (movie == null) return;

        var tags = GetTagsFromMovie(movie);
        tags.RemoveAll(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase));
        movie.Tags = tags.Any() ? JsonSerializer.Serialize(tags) : null;
        await _movieRepo.UpdateAsync(movie);
    }

    public async Task<List<string>> GetTagsAsync(int movieId)
    {
        var movie = await _movieRepo.GetByIdAsync(movieId);
        return movie != null ? GetTagsFromMovie(movie) : new List<string>();
    }

    public async Task<List<(string Tag, int Count)>> GetTagStatisticsAsync()
    {
        var movies = await _movieRepo.GetAllAsync();
        var tagCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var movie in movies)
        {
            var tags = GetTagsFromMovie(movie);
            foreach (var tag in tags)
            {
                tagCounts[tag] = tagCounts.GetValueOrDefault(tag, 0) + 1;
            }
        }

        return tagCounts.Select(kv => (kv.Key, kv.Value))
                        .OrderByDescending(t => t.Item2)
                        .ToList();
    }

    public async Task AddEmotionTagsAsync(int movieId)
    {
        var movie = await _movieRepo.GetByIdAsync(movieId);
        if (movie == null || string.IsNullOrEmpty(movie.Overview)) return;

        try
        {
            var prompt = $"根据电影简介生成情感标签，只返回标签列表，用中文逗号分隔：{movie.Overview}";
            var response = await _agentService.ChatAsync(prompt);
            
            var tags = response.Split('，', '，', ',', '、')
                              .Select(t => t.Trim())
                              .Where(t => !string.IsNullOrWhiteSpace(t))
                              .ToList();

            foreach (var tag in tags)
            {
                await AddTagAsync(movieId, tag);
            }
        }
        catch
        {
            // AI服务不可用时跳过
        }
    }

    private List<string> GetTagsFromMovie(Movie movie)
    {
        if (string.IsNullOrEmpty(movie.Tags))
            return new List<string>();

        try
        {
            return JsonSerializer.Deserialize<List<string>>(movie.Tags) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }
}
