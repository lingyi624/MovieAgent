namespace MovieAgent.Core.Interfaces;

public interface IVideoAnalysisService
{
    Task<List<string>> ExtractKeyFramesAsync(string videoPath, int frameCount = 10);
    
    Task<string> AnalyzeSceneMoodAsync(string videoPath);
    
    Task<VideoAnalysisResult> AnalyzeVideoAsync(string videoPath);
    
    Task<bool> HasAnalysisAsync(int movieId);
    
    Task SaveAnalysisAsync(int movieId, VideoAnalysisResult result);
    
    Task<VideoAnalysisResult?> GetAnalysisAsync(int movieId);
}

public class VideoAnalysisResult
{
    public string? SceneMood { get; set; }
    
    public List<string>? KeyFramePaths { get; set; }
    
    public int? SceneCount { get; set; }
    
    public string? DominantColor { get; set; }
    
    public string? VisualStyle { get; set; }
    
    public DateTime AnalyzedAt { get; set; } = DateTime.UtcNow;
}