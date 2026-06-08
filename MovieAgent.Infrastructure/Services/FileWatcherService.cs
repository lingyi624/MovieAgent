using System.Diagnostics;
using System.IO;
using MovieAgent.Core.Interfaces;

namespace MovieAgent.Infrastructure.Services;

public class FileWatcherService : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly IMovieScannerService _scanner;
    private readonly IMovieRepository _repo;

    public event EventHandler<string>? NewFileDetected;

    public FileWatcherService(IMovieScannerService scanner, IMovieRepository repo)
    {
        _scanner = scanner;
        _repo = repo;
    }

    public void StartWatching(List<string> sharePaths)
    {
        StopWatching();
        foreach (var path in sharePaths)
        {
            if (!Directory.Exists(path)) continue;
            try
            {
                var watcher = new FileSystemWatcher(path)
                {
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
                };
                watcher.Created += OnFileCreated;
                watcher.Error += OnWatcherError;
                _watchers.Add(watcher);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FileWatcher] Error watching {path}: {ex.Message}");
            }
        }
    }

    private async void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        try
        {
            await Task.Delay(2000);
            if (File.Exists(e.FullPath))
            {
                var ext = Path.GetExtension(e.FullPath).ToLowerInvariant();
                var validExts = new[] { ".mkv", ".mp4", ".avi", ".mov", ".iso", ".m2ts", ".ts", ".wmv", ".flv", ".webm" };
                if (validExts.Contains(ext))
                {
                    NewFileDetected?.Invoke(this, e.FullPath);
                    await _scanner.ImportNewMoviesAsync(new List<string> { e.FullPath });
                }
            }
        }
        catch { }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        Debug.WriteLine($"[FileWatcher] Error: {e.GetException().Message}");
    }

    public void StopWatching()
    {
        foreach (var w in _watchers)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }
        _watchers.Clear();
    }

    public void Dispose() => StopWatching();
}