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

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".mov", ".iso", ".m2ts", ".ts", ".wmv", ".flv", ".webm", ".rmvb", ".m4v", ".mpg", ".mpeg"
    };

    public event EventHandler<ScanProgressEventArgs>? ScanProgressChanged;
    public event EventHandler<ScanCompletedEventArgs>? ScanCompleted;

    public MovieScannerService(IMovieRepository repo, ITmdbService tmdb, IMediaInfoService mediaInfo, IVectorDatabaseService? vectorDb = null)
    {
        _repo = repo;
        _tmdb = tmdb;
        _mediaInfo = mediaInfo;
        _vectorDb = vectorDb;
    }

    void GetFilesSafe(string currentPath, HashSet<string> extensions, List<string> result)
    {
        try
        {

            // 处理当前目录的文件
            foreach (var file in Directory.GetFiles(currentPath))
            {
                if (extensions.Contains(Path.GetExtension(file)))
                {
                    result.Add(file);
                  
                }
            }

            // 递归处理子目录
            foreach (var dir in Directory.GetDirectories(currentPath))
            {
                GetFilesSafe(dir, extensions, result);  // 递归调用，每层都有 try-catch
            }
        }
        catch (UnauthorizedAccessException)
        {
            // 跳过无法访问的目录，继续其他目录
            return;
        }
        catch (PathTooLongException)
        {
            // 跳过路径过长的文件
            return;
        }
    }

    public async Task<List<string>> ScanVideoFilesAsync(List<string> sharePaths)
    {
        var files = new List<string>();
        foreach (var path in sharePaths)
        {
            if (!Directory.Exists(path)&&!Directory.GetFiles(path).Any())
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

                // 解析本地媒体信息
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

                // 更新向量数据库
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

        ScanCompleted?.Invoke(this, new ScanCompletedEventArgs
        {
            TotalFiles = filePaths.Count,
            NewMovies = newCount,
            Skipped = skipped
        });
        return newCount;
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

        // ---------- 新增：处理 [中文]英文.年份... 格式 ----------
        string chineseTitle = null;
        string englishTitle = null;
        int? year = null;

        // 1. 尝试匹配方括号内的中文标题（如 "[功夫]"）
        var bracketMatch = Regex.Match(fileName, @"\[(.*?)\]");
        if (bracketMatch.Success)
        {
            chineseTitle = bracketMatch.Groups[1].Value.Trim();
            // 剩余部分：方括号之后的内容，例如 "Kung.Fu.Hustle.2004.BluRay.1080p.x265..."
            string remainingAfterBracket = fileName.Substring(bracketMatch.Index + bracketMatch.Length);

            // 2. 从剩余部分提取年份
            var yearMatch = YearRegex.Match(remainingAfterBracket);
            if (yearMatch.Success)
            {
                year = int.Parse(yearMatch.Value);
                // 截取年份之前的部分作为英文标题候选
                string englishCandidate = remainingAfterBracket.Substring(0, yearMatch.Index);
                // 清理英文标题：将点、下划线、短横替换为空格，并移除常见技术后缀
                englishCandidate = Regex.Replace(englishCandidate, @"[\.\-_]", " ");
                englishCandidate = Regex.Replace(englishCandidate, @"\b(BluRay|WEB-DL|WEBRip|REMUX|PROPER|REPACK|DSNP|NF|AMZN|HMAX|ATVP|DDP?5\.1|Atmos|TrueHD|DTS-HD|DTS|MA|AAC|AC3|MP3|FLAC|HDRip|BDRip|XviD|DivX|10bit|8bit|x265|x264|HEVC|AVC|AV1|2Audio|MultiAudio|BOBO)\b", "", RegexOptions.IgnoreCase);
                englishCandidate = Regex.Replace(englishCandidate, @"\s+", " ").Trim();
                // 转换为首字母大写（保留常见大写缩写，如 "III" 会被转换为 "Iii"，这里简单处理）
                if (!string.IsNullOrEmpty(englishCandidate))
                {
                    var textInfo = CultureInfo.InvariantCulture.TextInfo;
                    englishTitle = textInfo.ToTitleCase(englishCandidate.ToLower());
                }
            }
        }

        // 如果成功提取到中文标题，则直接构建 Movie 对象（技术参数仍需从文件名中解析）
        if (!string.IsNullOrEmpty(chineseTitle))
        {
            // 解析技术参数（分辨率、音视频编码）—— 可以使用原方法中已有的正则，直接从整个文件名中提取
            var resolution = ResolutionRegex.Match(fileName).Value?.ToUpper();
            var videoCodec = VideoCodecRegex.Match(fileName).Value?.ToUpper();
            var audioCodec = AudioCodecRegex.Match(fileName).Value?.ToUpper();

            var fileInfo = new FileInfo(filePath);
            return new Movie
            {
                Title = chineseTitle,
                OriginalTitle = englishTitle,          // 可能为 null 或空
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

        // ---------- 原有逻辑：处理普通格式（无方括号中文） ----------
        string workingTitle = fileName;
        year = null;

        // 1. 提取年份
        var yearMatchGeneral = YearRegex.Match(workingTitle);
        if (yearMatchGeneral.Success)
        {
            year = int.Parse(yearMatchGeneral.Value);
            workingTitle = workingTitle.Replace(yearMatchGeneral.Value, "");
        }

        // 2. 移除 [xxx] 或 (xxx) 形式的内容
        workingTitle = Regex.Replace(workingTitle, @"[\[\(].*?[\]\)]", "");

        // 3. 解析技术细节
        var resolutionGen = ResolutionRegex.Match(workingTitle).Value?.ToUpper();
        var videoCodecGen = VideoCodecRegex.Match(workingTitle).Value?.ToUpper();
        var audioCodecGen = AudioCodecRegex.Match(workingTitle).Value?.ToUpper();

        // 4. 清洗标题
        workingTitle = ReleaseGroupRegex.Replace(workingTitle, "");
        foreach (var pattern in CleanupPatterns)
            workingTitle = Regex.Replace(workingTitle, pattern, " ", RegexOptions.IgnoreCase);

        workingTitle = Regex.Replace(workingTitle, @"\b(?![A-Z][a-z]+\b)[A-Z0-9]+\b", "");
        workingTitle = Regex.Replace(workingTitle, @"\s+", " ").Trim();

        // 5. 格式化标题
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
            // 构建用于生成向量的文本
            var text = BuildEmbeddingText(movie);
            if (string.IsNullOrWhiteSpace(text))
            {
                Debug.WriteLine($"[Scanner] No text for embedding: {movie.Title}");
                return;
            }

            // 生成向量
            var vector = await _vectorDb.GenerateEmbeddingAsync(text);
            if (vector == null || vector.Length == 0)
            {
                Debug.WriteLine($"[Scanner] Empty embedding for: {movie.Title}");
                return;
            }

            // 添加/更新到向量数据库
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
