using System.IO;
using System.Text.Json;
using MovieAgent.Core.Interfaces;

namespace MovieAgent.Infrastructure.Services;

public class VideoAnalysisService : IVideoAnalysisService
{
    private readonly string _cacheDirectory;
    private readonly IAgentService _agentService;

    public VideoAnalysisService(string cacheDirectory, IAgentService agentService)
    {
        _cacheDirectory = cacheDirectory;
        _agentService = agentService;
        Directory.CreateDirectory(cacheDirectory);
    }

    public async Task<List<string>> ExtractKeyFramesAsync(string videoPath, int frameCount = 10)
    {
        var keyFrames = new List<string>();
        
        for (int i = 0; i < Math.Min(frameCount, 5); i++)
        {
            var framePath = Path.Combine(_cacheDirectory, $"frame_{Guid.NewGuid()}.jpg");
            keyFrames.Add(framePath);
        }
        
        return await Task.FromResult(keyFrames);
    }

    public async Task<string> AnalyzeSceneMoodAsync(string videoPath)
    {
        var moods = new List<string> { "紧张", "温馨", "悬疑", "浪漫", "悲壮", "轻松", "惊悚", "感人" };
        var random = new Random();
        var mood = moods[random.Next(moods.Count)];
        
        return await Task.FromResult(mood);
    }

    public async Task<VideoAnalysisResult> AnalyzeVideoAsync(string videoPath)
    {
        var result = new VideoAnalysisResult
        {
            KeyFramePaths = await ExtractKeyFramesAsync(videoPath),
            SceneMood = await AnalyzeSceneMoodAsync(videoPath),
            SceneCount = new Random().Next(3, 10),
            DominantColor = GetRandomColor(),
            VisualStyle = GetRandomStyle(),
            AnalyzedAt = DateTime.UtcNow
        };

        return result;
    }

    public async Task<bool> HasAnalysisAsync(int movieId)
    {
        var filePath = GetAnalysisFilePath(movieId);
        return await Task.FromResult(File.Exists(filePath));
    }

    public async Task SaveAnalysisAsync(int movieId, VideoAnalysisResult result)
    {
        var filePath = GetAnalysisFilePath(movieId);
        var json = JsonSerializer.Serialize(result);
        await File.WriteAllTextAsync(filePath, json);
    }

    public async Task<VideoAnalysisResult?> GetAnalysisAsync(int movieId)
    {
        var filePath = GetAnalysisFilePath(movieId);
        if (!File.Exists(filePath))
            return null;

        var json = await File.ReadAllTextAsync(filePath);
        return JsonSerializer.Deserialize<VideoAnalysisResult>(json);
    }

    private string GetAnalysisFilePath(int movieId)
    {
        return Path.Combine(_cacheDirectory, $"analysis_{movieId}.json");
    }

    private string GetRandomColor()
    {
        var colors = new List<string> { "#E94560", "#4ECDC4", "#45B7D1", "#96CEB4", "#FFEAA7", "#DDA0DD", "#98D8C8" };
        return colors[new Random().Next(colors.Count)];
    }

    private string GetRandomStyle()
    {
        var styles = new List<string> { "写实", "唯美", "暗黑", "清新", "复古", "科幻", "文艺", "商业" };
        return styles[new Random().Next(styles.Count)];
    }
}