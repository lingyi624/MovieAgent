using System.Text;
using MovieAgent.Core.Entities;
using MovieAgent.Core.Interfaces;

namespace MovieAgent.Infrastructure.Services;

public interface IReportService
{
    Task<string> GenerateMonthlyReportAsync(int year, int month);
    Task<string> GenerateYearlyReportAsync(int year);
    Task<string> GenerateAiInsightsAsync();
}

public class ReportService : IReportService
{
    private readonly IPlayHistoryService _historyService;
    private readonly IMovieRepository _movieRepo;
    private readonly IAgentService _agentService;

    public ReportService(IPlayHistoryService historyService, IMovieRepository movieRepo, IAgentService agentService)
    {
        _historyService = historyService;
        _movieRepo = movieRepo;
        _agentService = agentService;
    }

    public async Task<string> GenerateMonthlyReportAsync(int year, int month)
    {
        var startDate = new DateTime(year, month, 1);
        var endDate = startDate.AddMonths(1).AddDays(-1);

        var watchedCount = await _historyService.GetWatchedCountAsync(startDate, endDate);
        var totalTime = await _historyService.GetTotalWatchTimeAsync(startDate, endDate);
        var movies = await _movieRepo.GetAllAsync();
        var watchedMovies = movies.Where(m => m.WatchedAt.HasValue && 
                                              m.WatchedAt.Value >= startDate && 
                                              m.WatchedAt.Value <= endDate).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"📅 {year}年{month}月观影报告");
        sb.AppendLine("=".PadRight(40, '='));
        sb.AppendLine();
        sb.AppendLine($"🎬 本月观看电影: {watchedCount} 部");
        sb.AppendLine($"⏱️ 累计观看时长: {FormatTime(totalTime)}");
        sb.AppendLine($"📊 平均每天观看: {watchedCount / DateTime.DaysInMonth(year, month):F1} 部");
        sb.AppendLine();
        sb.AppendLine("🎞️ 本月观看影片:");
        
        foreach (var movie in watchedMovies.Take(10))
        {
            sb.AppendLine($"  • {movie.Title} ({movie.ReleaseYear}) - {movie.Rating}分");
        }
        
        if (watchedMovies.Count > 10)
        {
            sb.AppendLine($"  ... 还有 {watchedMovies.Count - 10} 部");
        }

        return sb.ToString();
    }

    public async Task<string> GenerateYearlyReportAsync(int year)
    {
        var startDate = new DateTime(year, 1, 1);
        var endDate = new DateTime(year, 12, 31);

        var watchedCount = await _historyService.GetWatchedCountAsync(startDate, endDate);
        var totalTime = await _historyService.GetTotalWatchTimeAsync(startDate, endDate);
        var movies = await _movieRepo.GetAllAsync();
        var watchedMovies = movies.Where(m => m.IsWatched).ToList();
        var favorites = movies.Where(m => m.IsFavorite).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"📅 {year}年度观影报告");
        sb.AppendLine("=".PadRight(40, '='));
        sb.AppendLine();
        sb.AppendLine($"🎬 全年观看电影: {watchedCount} 部");
        sb.AppendLine($"⏱️ 累计观看时长: {FormatTime(totalTime)}");
        sb.AppendLine($"📊 平均每月观看: {watchedCount / 12:F1} 部");
        sb.AppendLine();
        sb.AppendLine("🏆 收藏影片:");
        
        foreach (var movie in favorites.Take(5))
        {
            sb.AppendLine($"  • {movie.Title} ({movie.ReleaseYear}) - {movie.Rating}分");
        }

        var genreStats = GetGenreStatistics(movies);
        sb.AppendLine();
        sb.AppendLine("📈 类型分布:");
        foreach (var (genre, count) in genreStats.Take(5))
        {
            sb.AppendLine($"  • {genre}: {count} 部");
        }

        return sb.ToString();
    }

    public async Task<string> GenerateAiInsightsAsync()
    {
        var movies = await _movieRepo.GetAllAsync();
        var watchedCount = movies.Count(m => m.IsWatched);
        var averageRating = movies.Where(m => m.Rating.HasValue).Select(m => m.Rating.Value).DefaultIfEmpty(0).Average();

        var prompt = $"分析用户观影数据：总共{movies.Count}部电影，已观看{watchedCount}部，平均评分{averageRating:F1}分。请给出个性化的观影建议和洞察分析。";
        var response = await _agentService.ChatAsync(prompt);
        
        return $"🤖 AI观影洞察\n{response}";
    }

    private string FormatTime(TimeSpan time)
    {
        if (time.TotalHours >= 24)
        {
            return $"{time.Days} 天 {time.Hours} 小时";
        }
        return $"{time.Hours} 小时 {time.Minutes} 分钟";
    }

    private List<(string Genre, int Count)> GetGenreStatistics(List<Movie> movies)
    {
        var genreCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var movie in movies)
        {
            if (!string.IsNullOrEmpty(movie.Genres))
            {
                try
                {
                    var genres = System.Text.Json.JsonSerializer.Deserialize<List<string>>(movie.Genres);
                    if (genres != null)
                    {
                        foreach (var genre in genres)
                        {
                            genreCounts[genre] = genreCounts.GetValueOrDefault(genre, 0) + 1;
                        }
                    }
                }
                catch { }
            }
        }

        return genreCounts.Select(kv => (kv.Key, kv.Value))
                         .OrderByDescending(t => t.Item2)
                         .ToList();
    }
}
