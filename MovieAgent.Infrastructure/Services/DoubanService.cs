using MovieAgent.Core.Interfaces;
using System.Net.Http;
using System.Text.Json;

namespace MovieAgent.Infrastructure.Services;

public interface IDoubanService
{
    Task<DoubanMovieInfo?> SearchMovieAsync(string title, int? year = null);
    Task<DoubanMovieInfo?> GetMovieInfoAsync(string doubanId);
    Task SyncDoubanRatingAsync(int movieId, string doubanId);
}

public class DoubanMovieInfo
{
    public string? Id { get; set; }
    public string? Title { get; set; }
    public string? OriginalTitle { get; set; }
    public double? Rating { get; set; }
    public int? RatingCount { get; set; }
    public string? Year { get; set; }
    public string? Director { get; set; }
    public string? Actors { get; set; }
    public string? Genres { get; set; }
    public string? Country { get; set; }
    public string? Language { get; set; }
    public string? Poster { get; set; }
    public string? Summary { get; set; }
}

public class DoubanService : IDoubanService
{
    private readonly HttpClient _httpClient;
    private readonly IMovieRepository _movieRepo;
    private readonly IMovieUpdateService? _movieUpdateService;
    private readonly ILoggerService _logger;

    public DoubanService(HttpClient httpClient, IMovieRepository movieRepo, IMovieUpdateService? movieUpdateService, ILoggerService logger)
    {
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _movieRepo = movieRepo;
        _movieUpdateService = movieUpdateService;
        _logger = logger;
    }

    public async Task<DoubanMovieInfo?> SearchMovieAsync(string title, int? year = null)
    {
        try
        {
            var url = $"https://www.douban.com/j/search?q={Uri.EscapeDataString(title)}&cat=1002";
            
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            
            var content = await response.Content.ReadAsStringAsync();
            
            if (string.IsNullOrWhiteSpace(content))
                return null;

            return ParseSearchResult(content, year);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Douban search failed");
            return null;
        }
    }

    public async Task<DoubanMovieInfo?> GetMovieInfoAsync(string doubanId)
    {
        try
        {
            var url = $"https://api.douban.com/v2/movie/subject/{doubanId}";
            
            var response = await _httpClient.GetAsync(url);
            
            if (!response.IsSuccessStatusCode)
                return null;

            var content = await response.Content.ReadAsStringAsync();
            return ParseMovieInfo(content);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Douban get movie info failed");
            return null;
        }
    }

    public async Task SyncDoubanRatingAsync(int movieId, string doubanId)
    {
        var info = await GetMovieInfoAsync(doubanId);
        if (info == null || !info.Rating.HasValue)
            return;

        var movie = await _movieRepo.GetByIdAsync(movieId);
        if (movie != null)
        {
            movie.Rating = info.Rating.Value;
            
            // 使用统一更新服务，同步向量数据库
            if (_movieUpdateService != null)
            {
                await _movieUpdateService.UpdateMovieWithVectorAsync(movie);
            }
            else
            {
                await _movieRepo.UpdateAsync(movie);
            }
        }
    }

    private DoubanMovieInfo? ParseSearchResult(string content, int? year)
    {
        try
        {
            var index = content.IndexOf("{\"subjects\"");
            if (index == -1)
                return null;

            var jsonContent = content.Substring(index);
            var endIndex = jsonContent.IndexOf("};");
            if (endIndex != -1)
                jsonContent = jsonContent.Substring(0, endIndex + 1);

            var doc = JsonDocument.Parse(jsonContent);
            var subjects = doc.RootElement.GetProperty("subjects");
            
            foreach (var subject in subjects.EnumerateArray())
            {
                var movie = new DoubanMovieInfo
                {
                    Id = subject.GetProperty("id").GetString(),
                    Title = subject.GetProperty("title").GetString(),
                    Year = subject.GetProperty("year").GetString()
                };

                if (year.HasValue && movie.Year != year.Value.ToString())
                    continue;

                return movie;
            }
        }
        catch
        {
        }
        
        return null;
    }

    private DoubanMovieInfo? ParseMovieInfo(string content)
    {
        try
        {
            var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            return new DoubanMovieInfo
            {
                Id = root.GetProperty("id").GetString(),
                Title = root.GetProperty("title").GetString(),
                OriginalTitle = root.GetProperty("original_title").GetString(),
                Rating = root.GetProperty("rating").GetProperty("average").GetDouble(),
                RatingCount = root.GetProperty("ratings_count").GetInt32(),
                Year = root.GetProperty("year").GetString(),
                Director = string.Join(", ", root.GetProperty("directors").EnumerateArray()
                    .Select(d => d.GetProperty("name").GetString())),
                Actors = string.Join(", ", root.GetProperty("casts").EnumerateArray()
                    .Select(c => c.GetProperty("name").GetString())),
                Genres = string.Join(", ", root.GetProperty("genres").EnumerateArray()
                    .Select(g => g.GetString())),
                Country = string.Join(", ", root.GetProperty("countries").EnumerateArray()
                    .Select(c => c.GetString())),
                Language = string.Join(", ", root.GetProperty("languages").EnumerateArray()
                    .Select(l => l.GetString())),
                Poster = root.GetProperty("images").GetProperty("large").GetString(),
                Summary = root.GetProperty("summary").GetString()
            };
        }
        catch
        {
            return null;
        }
    }
}