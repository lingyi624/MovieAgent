using System.Net.Http;
using System.Text.Json;
using System.IO;
using MovieAgent.Core.Interfaces;

namespace MovieAgent.Infrastructure.Services;

public interface ISubtitleService
{
    Task<List<SubtitleResult>> SearchSubtitlesAsync(string query, string language = "zh");
    Task<byte[]?> DownloadSubtitleAsync(string downloadUrl);
    Task<string> SaveSubtitleAsync(int movieId, byte[] subtitleData, string extension = ".srt");
}

public class SubtitleResult
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public string Extension { get; set; } = ".srt";
    public int Rating { get; set; }
    public bool IsHearingImpaired { get; set; }
}

public class SubtitleService : ISubtitleService
{
    private readonly HttpClient _httpClient;
    private readonly ILoggerService _logger;

    public SubtitleService(HttpClient httpClient, ILoggerService logger)
    {
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MovieAgent/1.0");
        _logger = logger;
    }

    public async Task<List<SubtitleResult>> SearchSubtitlesAsync(string query, string language = "zh")
    {
        var results = new List<SubtitleResult>();
        
        try
        {
            var url = $"https://rest.opensubtitles.org/search/sublanguageid-{language}/query-{Uri.EscapeDataString(query)}";
            
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                _logger.Debug($"[SubtitleService] Search failed: {response.StatusCode}");
                return results;
            }

            var content = await response.Content.ReadAsStringAsync();
            results = ParseOpenSubtitlesResponse(content);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[SubtitleService] Search exception");
        }

        return results;
    }

    public async Task<byte[]?> DownloadSubtitleAsync(string downloadUrl)
    {
        try
        {
            return await _httpClient.GetByteArrayAsync(downloadUrl);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[SubtitleService] Download failed");
            return null;
        }
    }

    public async Task<string> SaveSubtitleAsync(int movieId, byte[] subtitleData, string extension = ".srt")
    {
        try
        {
            var moviesPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MovieAgent", "Subtitles");
            Directory.CreateDirectory(moviesPath);
            
            var fileName = $"movie_{movieId}{extension}";
            var filePath = Path.Combine(moviesPath, fileName);
            
            await File.WriteAllBytesAsync(filePath, subtitleData);
            _logger.Debug($"[SubtitleService] Subtitle saved: {filePath}");
            
            return filePath;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[SubtitleService] Save failed");
            return string.Empty;
        }
    }

    private List<SubtitleResult> ParseOpenSubtitlesResponse(string content)
    {
        var results = new List<SubtitleResult>();
        
        try
        {
            var jsonDoc = JsonDocument.Parse(content);
            var array = jsonDoc.RootElement.EnumerateArray();
            
            foreach (var item in array)
            {
                var result = new SubtitleResult
                {
                    Id = item.GetProperty("IDSubtitle").GetString() ?? string.Empty,
                    Title = item.GetProperty("MovieName").GetString() ?? string.Empty,
                    Language = item.GetProperty("LanguageName").GetString() ?? string.Empty,
                    DownloadUrl = item.GetProperty("ZipDownloadLink").GetString() ?? string.Empty,
                    Extension = ".srt",
                    Rating = item.TryGetProperty("SubRating", out var rating) ? rating.GetInt32() : 0,
                    IsHearingImpaired = item.TryGetProperty("HearingImpaired", out var hi) && hi.GetInt32() == 1
                };
                
                if (!string.IsNullOrEmpty(result.DownloadUrl))
                {
                    results.Add(result);
                }
            }
        }
        catch
        {
        }
        
        return results;
    }
}