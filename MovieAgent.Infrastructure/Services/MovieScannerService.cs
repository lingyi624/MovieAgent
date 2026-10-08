using MovieAgent.Core.Entities;
using MovieAgent.Core.Interfaces;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text.RegularExpressions;
using System.Windows.Shapes;

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

    void GetFilesSafe(string currentPath, HashSet<string> extensions, List<string> result, DateTime lastTime)
    {
        try
        {
            DirectoryInfo rootDir = new DirectoryInfo(currentPath);
            var filteredDirs = rootDir.GetDirectories()
                          .Where(d => d.CreationTime > lastTime)
                          .ToList();
            var filteredFiles = rootDir.GetFiles()
                      .Where(f => f.LastWriteTime > lastTime)
                      .ToArray();
            foreach (var file in filteredFiles)
            {
                if (extensions.Contains(System.IO.Path.GetExtension(file.FullName)))
                {
                    result.Add(file.FullName);
                }
            }
           
            foreach (var dir in filteredDirs)
            {
                GetFilesSafe(dir.FullName, extensions, result, lastTime);
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
        return await Task.Run(async () =>
        {
            var files = new List<string>();
            // 扫描时间在循环外读取一次，避免每个目录都去读一次配置
            var lastScanTime = await GetLastScanTimeAsync();
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
                    GetFilesSafe(path, VideoExtensions, found, lastScanTime);
                    files.AddRange(found);
                    ScanProgressChanged?.Invoke(this, new ScanProgressEventArgs
                    {
                        CurrentPath = path,
                        FoundCount = found.Count,
                        TotalScanned = files.Count
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Scanner] Error scanning {path}: {ex.Message}");
                }
            }
            return files;
        });
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
                GetFilesSafe(path, VideoExtensions, found, lastScanTime);
                
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

    // 扫描并行度：同时处理的文件数。每个文件包含一次 NAS 读取（FFmpeg 探测）
    // 和最多两次 TMDB 网络请求，6 是吞吐与 TMDB 限流/NAS 磁盘竞争之间的折中值。
    private const int ScanConcurrency = 6;

    public async Task<int> ImportNewMoviesAsync(List<string> filePaths, CancellationToken ct = default)
    {
        int total = filePaths.Count;
        var stopwatch = Stopwatch.StartNew();

        ReportProgress(new ScanProgressEventArgs
        {
            Stage = "Scanning",
            TotalFiles = total,
            TotalScanned = 0
        });

        // 一次性载入已有电影建立索引：既省掉每文件一次数据库查询，
        // 也避免在并行区访问共享 DbContext（EF Core 不支持并发使用同一上下文）。
        var existingByPath = new Dictionary<string, Movie>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var existingMovies = await _repo.GetAllAsync();
            foreach (var m in existingMovies)
            {
                if (!string.IsNullOrEmpty(m.FilePath))
                    existingByPath[m.FilePath] = m;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Scanner] 预加载已有电影失败: {ex.Message}");
        }

        // 按下标写入结果数组，无锁且保持原始顺序
        var results = new (string FilePath, Movie Movie, MediaInfoResult? MediaInfo, bool IsNew)?[total];
        int slot = -1;
        int finished = 0;

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = ScanConcurrency
        };

        await Parallel.ForEachAsync(filePaths, parallelOptions, async (fp, token) =>
        {
            var index = Interlocked.Increment(ref slot);
            try
            {
                existingByPath.TryGetValue(fp, out var existingMovie);
                bool isNew = existingMovie == null;

                Movie movie;
                if (existingMovie != null)
                {
                    movie = existingMovie;
                }
                else
                {
                    var parsed = ParseFileName(fp);
                    if (parsed == null) return;
                    movie = parsed;
                    movie.FilePath = fp;
                    try
                    {
                        movie.FileSize = new FileInfo(fp).Length;
                    }
                    catch { }
                }

                // MediaInfo 是同步 FFmpeg 探测（要读 NAS 文件块），放到线程池执行；
                // 已有完整媒体信息的电影跳过，避免重扫时重复读取网络盘。
                MediaInfoResult? mediaInfo = null;
                if (isNew || string.IsNullOrEmpty(movie.VideoCodec) || string.IsNullOrEmpty(movie.Resolution))
                {
                    try
                    {
                        mediaInfo = await Task.Run(() => _mediaInfo.GetMediaInfo(fp), token);
                        if (mediaInfo.Success)
                        {
                            movie.VideoCodec = mediaInfo.VideoCodec;
                            movie.AudioCodec = mediaInfo.AudioCodec;
                            movie.Resolution = mediaInfo.Resolution;
                            movie.HdrType = mediaInfo.HdrType;
                            movie.Width = mediaInfo.Width;
                            movie.Height = mediaInfo.Height;
                            movie.FrameRate = mediaInfo.FrameRate;
                            movie.VideoBitrate = (long?)(mediaInfo.VideoBitrate * 1000000);
                            movie.AudioBitrate = (long?)(mediaInfo.AudioBitrate * 1000);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Scanner] MediaInfo error for {fp}: {ex.Message}");
                    }
                }

                // TMDB 每个文件最多两次网络请求，是最主要的耗时来源。
                // 已取得元数据的电影不再重复请求，否则每次重扫都要全量拉取 3000 部。
                if (isNew || string.IsNullOrEmpty(movie.TmdbId) || string.IsNullOrEmpty(movie.Overview))
                {
                    try
                    {
                        await _tmdb.FillMovieMetadataAsync(movie);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Scanner] TMDB metadata error for {fp}: {ex.Message}");
                    }
                }

                results[index] = (fp, movie, mediaInfo, isNew);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Scanner] Error processing {fp}: {ex.Message}");
            }
            finally
            {
                var done = Interlocked.Increment(ref finished);
                // 每 20 个更新一次进度，避免频繁 UI 刷新
                if (done % 20 == 0 || done == total)
                {
                    ReportProgress(new ScanProgressEventArgs
                    {
                        Stage = "Scanning",
                        CurrentFileName = System.IO.Path.GetFileName(fp),
                        CurrentIndex = done,
                        TotalFiles = total,
                        TotalScanned = done
                    });
                }
            }
        });

        var movieDataList = new List<(string FilePath, Movie Movie, MediaInfoResult? MediaInfo, bool IsNew)>(total);
        foreach (var r in results)
        {
            if (r.HasValue) movieDataList.Add(r.Value);
        }

        if (movieDataList.Count == 0)
        {
            await UpdateLastScanTimeAsync();
            return 0;
        }

        int dbCount = 0, vectorCount = 0;
        var lockObj = new object();
        
        Action<int, int> reportProgress = (db, vector) =>
        {
            lock (lockObj)
            {
                dbCount = db;
                vectorCount = vector;
            }
            
            ReportProgress(new ScanProgressEventArgs
            {
                Stage = "Processing",
                TotalFiles = movieDataList.Count,
                DbImported = db,
                VectorUpdated = vector
            });
        };

        // 先写库（本地 SQLite，速度快），并取得新增电影的自增 Id 供向量生成使用
        var dbTask = Task.Run(async () =>
        {
            int count = 0;
            foreach (var (fp, movie, mediaInfo, isNew) in movieDataList)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    if (movie.Id > 0)
                    {
                        await _repo.UpdateAsync(movie);
                    }
                    else
                    {
                        await _repo.AddAsync(movie);
                    }
                    count++;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Scanner] DB Error for {fp}: {ex.Message}");
                }
                
                reportProgress(count, vectorCount);
            }
            return count;
        }, ct);

        var vectorTask = Task.Run(async () =>
        {
            var count = 0;

            // 等待写库完成，新增电影此时才拥有数据库 Id
            var dbDone = await dbTask;

            // 只为本次新导入的电影生成向量。已有电影的向量早已写入库中，
            // 每次重扫都全量重算 3000+ 次本地推理是重复扫描的主要耗时来源。
            var moviesToVector = movieDataList
                .Where(x => x.IsNew && x.Movie.Id > 0)
                .Select(x => x.Movie)
                .ToList();

            if (_vectorDb != null && moviesToVector.Count > 0)
            {
                try
                {
                    var vectorData = moviesToVector
                        .Select(m => (m.Id, BuildEmbeddingText(m), m.Title ?? "", m.Overview))
                        .ToList();
                    
                    count = await _vectorDb.BatchGenerateAndAddAsync(vectorData, new Progress<(int Current, int Total, string Stage)>(p =>
                    {
                        reportProgress(dbDone, p.Current);
                    }));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Scanner] Vector Error: {ex.Message}");
                }
            }
            return count;
        }, ct);

        await Task.WhenAll(dbTask, vectorTask);
        
        int dbResult = dbTask.Result;
        int vectorResult = vectorTask.Result;

        await UpdateLastScanTimeAsync();

        stopwatch.Stop();
        Debug.WriteLine($"[Scanner] 导入完成: {movieDataList.Count} 个文件, 写库 {dbResult} 条, 向量 {vectorResult} 条, 耗时 {stopwatch.Elapsed.TotalSeconds:F1}s");

        ReportProgress(new ScanProgressEventArgs
        {
            Stage = "Completed",
            TotalFiles = movieDataList.Count,
            DbImported = dbResult,
            VectorUpdated = vectorResult
        });

        return dbResult;
    }
    
    private void ReportProgress(ScanProgressEventArgs args)
    {
        ScanProgressChanged?.Invoke(this, args);
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
            await _configStorage.SetConfigAsync(LastScanTimeKey, DateTime.Now.ToString("o"));
        }
    }

  

    public static Movie? ParseFileName(string filePath)
    { 
        return UltimateMovieParser.ParseFileName(filePath);
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

            // 使用 search_document 前缀生成文档向量
            var vector = await _vectorDb.GenerateDocumentEmbeddingAsync(text);
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

    /// <summary>
    /// 批量更新向量数据库（推荐使用，性能更高）
    /// </summary>
    /// <param name="movies">电影列表</param>
    /// <param name="progress">进度回调</param>
    /// <returns>成功更新的数量</returns>
    public async Task<int> BatchUpdateVectorDatabaseAsync(List<Movie> movies, IProgress<(int Current, int Total)>? progress = null)
    {
        if (_vectorDb == null || movies.Count == 0) return 0;

        try
        {
            Debug.WriteLine($"[Scanner] 开始批量更新向量数据库，共 {movies.Count} 部电影");
            
            // 准备批量数据
            var movieData = new List<(int MovieId, string Text, string Title, string? Overview)>();
            
            foreach (var movie in movies)
            {
                if (movie.Id == 0) continue;
                
                var text = BuildEmbeddingText(movie);
                if (string.IsNullOrWhiteSpace(text))
                {
                    Debug.WriteLine($"[Scanner] 跳过空嵌入文本: {movie.Title}");
                    continue;
                }
                
                // 输出嵌入文本的前100个字符（用于调试）
                Debug.WriteLine($"[Scanner] 生成嵌入文本: {movie.Title} - {text[..Math.Min(text.Length, 100)]}...");
                
                movieData.Add((movie.Id, text, movie.Title, movie.Overview));
            }

            if (movieData.Count == 0) 
            {
                Debug.WriteLine("[Scanner] 没有有效的嵌入数据");
                return 0;
            }

            Debug.WriteLine($"[Scanner] 准备批量生成 {movieData.Count} 个向量...");

            // 使用批量生成并添加方法
            var progressWrapper = new Progress<(int Current, int Total, string Stage)>(p =>
            {
                Debug.WriteLine($"[Scanner] 批量更新进度: {p.Current}/{p.Total} - {p.Stage}");
                progress?.Report((p.Current, p.Total));
            });

            var addedCount = await _vectorDb.BatchGenerateAndAddAsync(movieData, progressWrapper);
            
            Debug.WriteLine($"[Scanner] 批量向量更新完成: {addedCount} 部电影");
            return addedCount;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Scanner] BatchUpdateVectorDatabaseAsync 失败: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 更新所有电影的向量（重新生成全部）
    /// </summary>
    public async Task<int> RegenerateAllVectorsAsync(IProgress<(int Current, int Total)>? progress = null)
    {
        if (_vectorDb == null) return 0;

        try
        {
            Debug.WriteLine("[Scanner] Regenerating all vectors...");
            
            // 获取所有电影
            var allMovies = await _repo.GetAllAsync();
            if (allMovies.Count == 0) return 0;

            Debug.WriteLine($"[Scanner] Found {allMovies.Count} movies to regenerate vectors");

            return await BatchUpdateVectorDatabaseAsync(allMovies, progress);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Scanner] RegenerateAllVectorsAsync failed: {ex.Message}");
            throw;
        }
    }

    private string BuildEmbeddingText(Movie movie)
    {
        var parts = new List<string>();
        
        // 基本信息 - 使用中文标签
        if (!string.IsNullOrWhiteSpace(movie.Title))
            parts.Add($"电影标题：{movie.Title}");
        
        if (!string.IsNullOrWhiteSpace(movie.OriginalTitle))
            parts.Add($"原名：{movie.OriginalTitle}");
        
        if (!string.IsNullOrWhiteSpace(movie.Tagline))
            parts.Add($"标语：{movie.Tagline}");
        
        // 上映信息
        if (movie.ReleaseDate.HasValue)
            parts.Add($"上映日期：{movie.ReleaseDate.Value:yyyy年MM月dd日}");
        
        if (movie.ReleaseYear.HasValue)
            parts.Add($"上映年份：{movie.ReleaseYear.Value}年");
        
        if (!string.IsNullOrWhiteSpace(movie.Status))
            parts.Add($"状态：{movie.Status}");
        
        // 制片信息
        if (!string.IsNullOrWhiteSpace(movie.ProductionCompanies))
            parts.Add($"制片公司：{ParseJsonList(movie.ProductionCompanies)}");
        
        if (!string.IsNullOrWhiteSpace(movie.ProductionCountries))
            parts.Add($"制片国家：{ParseJsonList(movie.ProductionCountries)}");
        
        if (!string.IsNullOrWhiteSpace(movie.OriginCountry))
            parts.Add($"原产国：{movie.OriginCountry}");
        
        if (!string.IsNullOrWhiteSpace(movie.Country))
            parts.Add($"国家：{movie.Country}");
        
        // 财务信息
        if (movie.Budget.HasValue && movie.Budget > 0)
            parts.Add($"预算：{movie.Budget.Value:N0}美元");
        
        if (movie.Revenue.HasValue && movie.Revenue > 0)
            parts.Add($"票房：{movie.Revenue.Value:N0}美元");
        
        // 语言信息
        if (!string.IsNullOrWhiteSpace(movie.OriginalLanguage))
            parts.Add($"原始语言：{GetLanguageName(movie.OriginalLanguage)}");
        
        if (!string.IsNullOrWhiteSpace(movie.Language))
            parts.Add($"语言：{movie.Language}");
        
        if (!string.IsNullOrWhiteSpace(movie.AudioLanguages))
            parts.Add($"音频语言：{movie.AudioLanguages}");
        
        // 类型和关键词
        if (!string.IsNullOrWhiteSpace(movie.Genres))
            parts.Add($"类型：{ParseJsonList(movie.Genres)}");
        
        if (!string.IsNullOrWhiteSpace(movie.Keywords))
            parts.Add($"关键词：{ParseJsonList(movie.Keywords)}");
        
        if (!string.IsNullOrWhiteSpace(movie.AlternativeTitles))
            parts.Add($"别名：{movie.AlternativeTitles}");
        
        // 演职人员
        if (!string.IsNullOrWhiteSpace(movie.Director))
            parts.Add($"导演：{movie.Director}");
        
        if (!string.IsNullOrWhiteSpace(movie.Writer))
            parts.Add($"编剧：{movie.Writer}");
        
        if (!string.IsNullOrWhiteSpace(movie.Cast))
            parts.Add($"演员：{movie.Cast}");
        
        // 视频技术信息
        if (!string.IsNullOrWhiteSpace(movie.VideoCodec))
            parts.Add($"视频编码：{movie.VideoCodec}");
        
        if (!string.IsNullOrWhiteSpace(movie.Resolution))
            parts.Add($"分辨率：{movie.Resolution}");
        
        if (movie.Width.HasValue && movie.Height.HasValue)
            parts.Add($"尺寸：{movie.Width}x{movie.Height}");
        
        if (movie.FrameRate.HasValue)
            parts.Add($"帧率：{movie.FrameRate}");
        
        if (!string.IsNullOrWhiteSpace(movie.AspectRatio))
            parts.Add($"画幅比例：{movie.AspectRatio}");
        
        if (!string.IsNullOrWhiteSpace(movie.HdrType))
            parts.Add($"HDR类型：{movie.HdrType}");
        
        if (!string.IsNullOrWhiteSpace(movie.ColorSpace))
            parts.Add($"色彩空间：{movie.ColorSpace}");
        
        if (!string.IsNullOrWhiteSpace(movie.BitDepth))
            parts.Add($"位深：{movie.BitDepth}");
        
        // 音频技术信息
        if (!string.IsNullOrWhiteSpace(movie.AudioCodec))
            parts.Add($"音频编码：{movie.AudioCodec}");
        
        if (!string.IsNullOrWhiteSpace(movie.AudioChannels))
            parts.Add($"音频声道：{movie.AudioChannels}");
        
        if (!string.IsNullOrWhiteSpace(movie.SubtitleFormats))
            parts.Add($"字幕格式：{movie.SubtitleFormats}");
        
        // 评分和收藏
        if (movie.Rating.HasValue)
            parts.Add($"评分：{movie.Rating:F1}分");
        
        if (movie.VoteCount.HasValue)
            parts.Add($"投票数：{movie.VoteCount.Value:N0}");
        
        if (movie.Popularity.HasValue)
            parts.Add($"人气：{movie.Popularity:F1}");
        
        if (movie.Runtime.HasValue)
        {
            var hours = movie.Runtime.Value / 60;
            var minutes = movie.Runtime.Value % 60;
            var runtimeStr = hours > 0 ? $"{hours}小时{minutes}分钟" : $"{minutes}分钟";
            parts.Add($"时长：{runtimeStr}");
        }
        
        // 简介
        if (!string.IsNullOrWhiteSpace(movie.Overview))
            parts.Add($"简介：{movie.Overview}");

        return string.Join("。", parts);
    }

    private string ParseJsonList(string? json)
    {
        if (string.IsNullOrEmpty(json)) return "";
        
        try
        {
            var list = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
            return list != null && list.Any() ? string.Join("、", list) : json;
        }
        catch
        {
            return json;
        }
    }

    private string GetLanguageName(string code)
    {
        var languageMap = new Dictionary<string, string>
        {
            { "en", "英语" }, { "zh", "中文" }, { "ja", "日语" },
            { "ko", "韩语" }, { "fr", "法语" }, { "de", "德语" },
            { "it", "意大利语" }, { "es", "西班牙语" }, { "ru", "俄语" },
            { "hi", "印地语" }, { "th", "泰语" }, { "pt", "葡萄牙语" }
        };
        
        return languageMap.TryGetValue(code.ToLower(), out var name) ? name : code.ToUpper();
    }
}