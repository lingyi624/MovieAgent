using MovieAgent.Core.Models;

namespace MovieAgent.Core.Interfaces;

public interface IMovieDownloadService
{
    event EventHandler<DownloadProgressEventArgs>? DownloadProgressChanged;

    Task<string> AddDownloadAsync(string sourceUrl, string? customName = null, string? customSaveDir = null, List<string>? selectedFiles = null);
    Task<TorrentPreviewInfo?> GetTorrentPreviewAsync(string sourceUrl);
    Task<TorrentPreviewInfo?> GetTorrentPreviewAsync(string sourceUrl, CancellationToken cancellationToken);
    Task PauseDownloadAsync(string taskId);
    Task ResumeDownloadAsync(string taskId);
    Task CancelDownloadAsync(string taskId);
    Task RetryDownloadAsync(string taskId);
    Task RemoveDownloadAsync(string taskId);

    Task SetPriorityAsync(string taskId, DownloadPriority priority);

    Task<IReadOnlyList<DownloadTask>> GetAllTasksAsync();
    DownloadTask? GetTask(string taskId);

    Task UpdateSettingsAsync(DownloadSettings settings);
    DownloadSettings GetSettings();
    Task<string> GetDefaultDownloadDirectoryAsync();
}