namespace MovieAgent.Core.Interfaces;

public interface IMovieScannerService
{
    event EventHandler<ScanProgressEventArgs>? ScanProgressChanged;
    event EventHandler<ScanCompletedEventArgs>? ScanCompleted;

    Task<List<string>> ScanVideoFilesAsync(List<string> sharePaths);
    Task<int> ImportNewMoviesAsync(List<string> filePaths, CancellationToken ct = default);
}

public class ScanProgressEventArgs : EventArgs
{
    public string? CurrentPath { get; set; }
    public int FoundCount { get; set; }
    public int TotalScanned { get; set; }
    public string? CurrentFileName { get; set; }
    public int CurrentIndex { get; set; }
    public int TotalFiles { get; set; }
}

public class ScanCompletedEventArgs : EventArgs
{
    public int TotalFiles { get; set; }
    public int NewMovies { get; set; }
    public int Skipped { get; set; }
}