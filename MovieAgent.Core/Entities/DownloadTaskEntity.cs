namespace MovieAgent.Core.Entities;

public class DownloadTaskEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public int SourceType { get; set; }
    public int Status { get; set; }
    public int Priority { get; set; }
    public long TotalBytes { get; set; } = -1;
    public long DownloadedBytes { get; set; }
    public double DownloadSpeedBps { get; set; }
    public string? SavePath { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public int RetryCount { get; set; }
    public int MaxRetries { get; set; } = 3;
}