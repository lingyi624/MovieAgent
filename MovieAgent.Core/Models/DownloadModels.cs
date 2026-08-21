namespace MovieAgent.Core.Models;

public enum DownloadSourceType
{
    Magnet,
    TorrentFile,
    DirectHttp
}

public enum DownloadStatus
{
    Queued,
    Downloading,
    Paused,
    Completed,
    Failed,
    Cancelled
}

public enum DownloadPriority
{
    Low,
    Normal,
    High
}

public class DownloadTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public DownloadSourceType SourceType { get; set; }
    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public DownloadPriority Priority { get; set; } = DownloadPriority.Normal;
    public long TotalBytes { get; set; } = -1;
    public long DownloadedBytes { get; set; }
    public double ProgressPercent => TotalBytes > 0 ? (double)DownloadedBytes / TotalBytes * 100 : 0;
    public double DownloadSpeedBps { get; set; }
    public string? SavePath { get; set; }
    public string? TargetDirectory { get; set; }
    public List<string>? SelectedFiles { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public int RetryCount { get; set; }
    public int MaxRetries { get; set; } = 3;
}

public class DownloadProgressEventArgs : EventArgs
{
    public string TaskId { get; set; } = string.Empty;
    public DownloadStatus Status { get; set; }
    public long DownloadedBytes { get; set; }
    public long TotalBytes { get; set; }
    public double SpeedBps { get; set; }
    public string? ErrorMessage { get; set; }
}

public class DownloadSettings
{
    public string DownloadDirectory { get; set; } = @"D:\Movies\Downloads";
    public string NasDownloadDirectory { get; set; } = string.Empty;
    public int MaxConcurrentDownloads { get; set; } = 3;
    public int MaxRetries { get; set; } = 3;
    public bool UseNasForDownload { get; set; }
}

public class TorrentFileInfo
{
    public string Path { get; set; } = string.Empty;
    public string FileName => System.IO.Path.GetFileName(Path);
    public long Size { get; set; }
    public bool Selected { get; set; } = true;
}

public class TorrentPreviewInfo
{
    public string SuggestedName { get; set; } = string.Empty;
    public string SuggestedDirectory { get; set; } = string.Empty;
    public long TotalSize { get; set; }
    public List<TorrentFileInfo> Files { get; set; } = new();
    public string? ErrorMessage { get; set; }
}