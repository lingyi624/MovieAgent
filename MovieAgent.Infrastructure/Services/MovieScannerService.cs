using MovieAgent.Core.Entities;
using MovieAgent.Core.Interfaces;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace MovieAgent.Infrastructure.Services;

public class MovieScannerService : IMovieScannerService
{
    private readonly IMovieRepository _repo;
    private readonly ITmdbService _tmdb;
    private readonly IMediaInfoService _mediaInfo;
    private readonly IVectorDatabaseService? _vectorDb;
    private readonly IConfigStorageService? _configStorage;

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".mov", ".iso", ".m2ts", ".ts", ".wmv", ".flv", ".webm", ".rmvb", ".m4v", ".mpg", ".mpeg"
    };

    private const string LastScanTimeKey = "LastScanTime";

    public event EventHandler<ScanProgressEventArgs>? ScanProgressChanged;
    public event EventHandler<ScanCompletedEventArgs>? ScanCompleted;

    public MovieScannerService(IMovieRepository repo, ITmdbService tmdb, IMediaInfoService mediaInfo, 
        IVectorDatabaseService? vectorDb = null, IConfigStorageService? configStorage = null)
    {
        _repo = repo;
        _tmdb = tmdb;
        _mediaInfo = mediaInfo;
        _vectorDb = vectorDb;
        _configStorage = configStorage;
    }

    void GetFilesSafe(string currentPath, HashSet<string> extensions, List<string> result)
    {
        try
        {
            foreach (var file in Directory.GetFiles(currentPath))
            {
                if (extensions.Contains(Path.GetExtension(file)))
                {
                    result.Add(file);
                }
            }

            foreach (var dir in Directory.GetDirectories(currentPath))
            {
                GetFilesSafe(dir, extensions, result);
            }
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (PathTooLongException)
        {
            return;
        }
    }

    public async Task<List<string>> ScanVideoFilesAsync(List<string> sharePaths)
    {
        var files = new List<string>();
        foreach (var path in sharePaths)
        {
            if (!Directory.Exists(path) && !Directory.GetFiles(path).Any())
            {
                Debug.WriteLine($"[Scanner] Path not found: {path}");
                continue;
            }

            try
            { 
                var found = new List<string>(); 
                GetFilesSafe(path, VideoExtensions, found);
                files.AddRange(found);
                ScanProgressChanged?.Invoke(this, new ScanProgressEventArgs
                {
                    CurrentPath = path,
                    FoundCount = found.Count(),
                    TotalScanned = files.Count
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Scanner] Error scanning {path}: {ex.Message}");
            }
        }
        return files;
    }

    public async Task<List<string>> ScanNewVideoFilesAsync(List<string> sharePaths)
    {
        var lastScanTime = await GetLastScanTimeAsync();
        Debug.WriteLine($"[Scanner] Last scan time: {lastScanTime}");

        var newFiles = new List<string>();
        foreach (var path in sharePaths)
        {
            if (!Directory.Exists(path))
            {
                Debug.WriteLine($"[Scanner] Path not found: {path}");
                continue;
            }

            try
            {
                var found = new List<string>();
                GetFilesSafe(path, VideoExtensions, found);
                
                foreach (var file in found)
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        if (fileInfo.LastWriteTimeUtc > lastScanTime)
                        {
                            newFiles.Add(file);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Scanner] Error checking file {file}: {ex.Message}");
                    }
                }

                ScanProgressChanged?.Invoke(this, new ScanProgressEventArgs
                {
                    CurrentPath = path,
                    FoundCount = newFiles.Count,
                    TotalScanned = newFiles.Count
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Scanner] Error scanning {path}: {ex.Message}");
            }
        }

        await UpdateLastScanTimeAsync();
        return newFiles;
    }

    public async Task<int> ImportNewMoviesAsync(List<string> filePaths, CancellationToken ct = default)
    {
        int newCount = 0, skipped = 0;
        int total = filePaths.Count;
        
        for (int i = 0; i < total; i++)
        {
            if (ct.IsCancellationRequested) break;
            var fp = filePaths[i];
            
            ScanProgressChanged?.Invoke(this, new ScanProgressEventArgs
            {
                CurrentFileName = Path.GetFileName(fp),
                CurrentIndex = i + 1,
                TotalFiles = total,
                TotalScanned = newCount + skipped
            });

            try
            {
                if (await _repo.ExistsByFilePathAsync(fp)) { skipped++; continue; }
                var movie = ParseFileName(fp);
                if (movie == null) { skipped++; continue; }

                try
                {
                    var mediaInfo = _mediaInfo.GetMediaInfo(fp);
                    if (mediaInfo.Success)
                    {
                        movie.VideoCodec = mediaInfo.VideoCodec;
                        movie.AudioCodec = mediaInfo.AudioCodec;
                        movie.Resolution = mediaInfo.Resolution;
                        movie.HdrType = mediaInfo.HdrType;
                        Debug.WriteLine($"[Scanner] Media parsed: {movie.VideoCodec}, {movie.AudioCodec}, {movie.Resolution}, HDR: {movie.HdrType}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Scanner] MediaInfo error: {ex.Message}");
                }

                try
                {
                    await _tmdb.FillMovieMetadataAsync(movie);
                }
                catch { /* metadata optional */ }

                await _repo.AddAsync(movie);
                newCount++;

                if (_vectorDb != null)
                {
                    try
                    {
                        await UpdateVectorDatabaseAsync(movie);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Scanner] Vector DB update failed for {movie.Title}: {ex.Message}");
                    }
                }
            }
            catch { skipped++; }
        }

        await UpdateLastScanTimeAsync();

        ScanCompleted?.Invoke(this, new ScanCompletedEventArgs
        {
            TotalFiles = filePaths.Count,
            NewMovies = newCount,
            Skipped = skipped
        });
        return newCount;
    }

    public async Task<int> ImportIncrementalMoviesAsync(List<string> sharePaths, CancellationToken ct = default)
    {
        var newFiles = await ScanNewVideoFilesAsync(sharePaths);
        return await ImportNewMoviesAsync(newFiles, ct);
    }

    private async Task<DateTime> GetLastScanTimeAsync()
    {
        if (_configStorage == null)
            return DateTime.MinValue;

        var timeString = await _configStorage.GetConfigAsync<string>(LastScanTimeKey);
        if (DateTime.TryParse(timeString, out DateTime time))
            return time;

        return DateTime.MinValue;
    }

    private async Task UpdateLastScanTimeAsync()
    {
        if (_configStorage != null)
        {
            await _configStorage.SetConfigAsync(LastScanTimeKey, DateTime.UtcNow.ToString("o"));
        }
    }

    private static readonly Regex YearRegex = new Regex(@"\b(19|20)\d{2}\b", RegexOptions.Compiled);
    private static readonly Regex ResolutionRegex = new Regex(@"\b(4K|2160p|1080p|720p)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex VideoCodecRegex = new Regex(@"\b(x265|HEVC|x264|AVC|AV1)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AudioCodecRegex = new Regex(@"\b(DTS-HD|TrueHD|DTS|AC3|AAC|Atmos)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ReleaseGroupRegex = new Regex(@"[-_\s]+(SeeHD|CtrlHD|NTb|DIMENSION|Felony|SPARKS|BOBO)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly string[] CleanupPatterns = {
        @"[\.\-_]",
        @"(4K|2160p|1080p|720p|HDR|HDR10|DV|HEVC|H\.?264|H\.?265|AVC|AV1|BluRay|WEB-DL|WEBRip|REMUX|PROPER|REPACK|DSNP|NF|AMZN|HMAX|ATVP|DDP?5\.1|Atmos|TrueHD|DTS-HD|DTS|MA|AAC|AC3|MP3|FLAC|HDRip|BDRip|XviD|DivX|S\d{2}E\d{2})"
    };

    public static Movie? ParseFileName(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        string fileName;
        try { fileName = Path.GetFileNameWithoutExtension(filePath); }
        catch (ArgumentException) { return null; }
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        string chineseTitle = null;
        string englishTitle = null;
        int? year = null;

        var bracketMatch = Regex.Match(fileName, @"\[(.*?)\]");
        if (bracketMatch.Success)
        {
            chineseTitle = bracketMatch.Groups[1].Value.Trim();
            string remainingAfterBracket = fileName.Substring(bracketMatch.Index + bracketMatch.Length);

            var yearMatch = YearRegex.Match(remainingAfterBracket);
            if (yearMatch.Success)
            {
                year = int.Parse(yearMatch.Value);
                string englishCandidate = remainingAfterBracket.Substring(0, yearMatch.Index);
                englishCandidate = Regex.Replace(englishCandidate, @"[\.\-_]", " ");
                englishCandidate = Regex.Replace(englishCandidate, @"\b(BluRay|WEB-DL|WEBRip|REMUX|PROPER|REPACK|DSNP|NF|AMZN|HMAX|ATVP|DDP?5\.1|Atmos|TrueHD|DTS-HD|DTS|MA|AAC|AC3|MP3|FLAC|HDRip|BDRip|XviD|DivX|10bit|8bit|x265|x264|HEVC|AVC|AV1|2Audio|MultiAudio|BOBO)\b", "", RegexOptions.IgnoreCase);
                englishCandidate = Regex.Replace(englishCandidate, @"\s+", " ").Trim();
                if (!string.IsNullOrEmpty(englishCandidate))
                {
                    var textInfo = CultureInfo.InvariantCulture.TextInfo;
                    englishTitle = textInfo.ToTitleCase(englishCandidate.ToLower());
                }
            }
        }

        if (!string.IsNullOrEmpty(chineseTitle))
        {
            var resolution = ResolutionRegex.Match(fileName).Value?.ToUpper();
            var videoCodec = VideoCodecRegex.Match(fileName).Value?.ToUpper();
            var audioCodec = AudioCodecRegex.Match(fileName).Value?.ToUpper();

            var fileInfo = new FileInfo(filePath);
            return new Movie
            {
                Title = chineseTitle,
                OriginalTitle = englishTitle,
                ReleaseYear = year,
                Resolution = resolution,
                VideoCodec = videoCodec,
                AudioCodec = audioCodec,
                FilePath = filePath,
                FileSize = fileInfo.Exists ? fileInfo.Length : 0,
                IsWatched = false,
                IsFavorite = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
        }

        string workingTitle = fileName;
        year = null;

        var yearMatchGeneral = YearRegex.Match(workingTitle);
        if (yearMatchGeneral.Success)
        {
            year = int.Parse(yearMatchGeneral.Value);
            workingTitle = workingTitle.Replace(yearMatchGeneral.Value, "");
        }

        workingTitle = Regex.Replace(workingTitle, @"[\[\(].*?[\]\)]", "");

        var resolutionGen = ResolutionRegex.Match(workingTitle).Value?.ToUpper();
        var videoCodecGen = VideoCodecRegex.Match(workingTitle).Value?.ToUpper();
        var audioCodecGen = AudioCodecRegex.Match(workingTitle).Value?.ToUpper();

        workingTitle = ReleaseGroupRegex.Replace(workingTitle, "");
        foreach (var pattern in CleanupPatterns)
            workingTitle = Regex.Replace(workingTitle, pattern, " ", RegexOptions.IgnoreCase);

        workingTitle = Regex.Replace(workingTitle, @"\b(?![A-Z][a-z]+\b)[A-Z0-9]+\b", "");
        workingTitle = Regex.Replace(workingTitle, @"\s+", " ").Trim();

        if (!string.IsNullOrWhiteSpace(workingTitle))
        {
            var titleInfo = new CultureInfo("en-US", false).TextInfo;
            workingTitle = titleInfo.ToTitleCase(workingTitle.ToLower());
        }
        else
        {
            return null;
        }

        var fileInfoGen = new FileInfo(filePath);
        return new Movie
        {
            Title = workingTitle,
            OriginalTitle = null,
            ReleaseYear = year,
            Resolution = resolutionGen,
            VideoCodec = videoCodecGen,
            AudioCodec = audioCodecGen,
            FilePath = filePath,
            FileSize = fileInfoGen.Exists ? fileInfoGen.Length : 0,
            IsWatched = false,
            IsFavorite = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    private async Task UpdateVectorDatabaseAsync(Movie movie)
    {
        if (_vectorDb == null || movie.Id == 0) return;

        try
        {
            var text = BuildEmbeddingText(movie);
            if (string.IsNullOrWhiteSpace(text))
            {
                Debug.WriteLine($"[Scanner] No text for embedding: {movie.Title}");
                return;
            }

            var vector = await _vectorDb.GenerateEmbeddingAsync(text);
            if (vector == null || vector.Length == 0)
            {
                Debug.WriteLine($"[Scanner] Empty embedding for: {movie.Title}");
                return;
            }

            await _vectorDb.AddMovieAsync(movie.Id, vector, movie.Title, movie.Overview);
            Debug.WriteLine($"[Scanner] Added to vector DB: {movie.Title} (ID: {movie.Id})");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Scanner] UpdateVectorDatabaseAsync failed: {ex.Message}");
            throw;
        }
    }

    private string BuildEmbeddingText(Movie movie)
    {
        var parts = new List<string>();
        
        if (!string.IsNullOrWhiteSpace(movie.Title))
            parts.Add(movie.Title);
        
        if (movie.ReleaseYear.HasValue)
            parts.Add($"Year: {movie.ReleaseYear.Value}");
        
        if (!string.IsNullOrWhiteSpace(movie.Overview))
            parts.Add(movie.Overview);
        
        if (!string.IsNullOrWhiteSpace(movie.Genres))
            parts.Add($"Genres: {movie.Genres}");
        
        if (!string.IsNullOrWhiteSpace(movie.Resolution))
            parts.Add($"Resolution: {movie.Resolution}");
        
        if (!string.IsNullOrWhiteSpace(movie.VideoCodec))
            parts.Add($"Video: {movie.VideoCodec}");
        
        if (!string.IsNullOrWhiteSpace(movie.AudioCodec))
            parts.Add($"Audio: {movie.AudioCodec}");

        return string.Join(" ", parts);
    }
}