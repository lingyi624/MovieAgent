using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MonoTorrent;
using MonoTorrent.Client;
using MovieAgent.Core.Entities;
using MovieAgent.Core.Interfaces;
using MovieAgent.Core.Models;
using MovieAgent.Infrastructure.Data;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace MovieAgent.Infrastructure.Services;

public class MovieDownloadService : IMovieDownloadService, IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerService _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConcurrentDictionary<string, DownloadTask> _tasks = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new();
    private readonly ConcurrentDictionary<string, long> _lastBytes = new();
    private SemaphoreSlim _concurrencySemaphore;
    private System.Timers.Timer? _progressTimer;
    private DownloadSettings _settings;
    private bool _disposed;
    private bool _loaded;

    public event EventHandler<DownloadProgressEventArgs>? DownloadProgressChanged;

    public MovieDownloadService(IConfiguration configuration, IHttpClientFactory httpClientFactory, ILoggerService logger, IServiceScopeFactory scopeFactory)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _settings = LoadSettings();
        _concurrencySemaphore = new SemaphoreSlim(_settings.MaxConcurrentDownloads);

        _progressTimer = new System.Timers.Timer(1000);
        _progressTimer.Elapsed += OnProgressTimerElapsed;
        _progressTimer.Start();

        _ = LoadTasksFromDbAsync();
    }

    private DownloadSettings LoadSettings()
    {
        return new DownloadSettings
        {
            DownloadDirectory = _configuration["Download:DownloadDirectory"] ?? @"D:\Movies\Downloads",
            NasDownloadDirectory = _configuration["Download:NasDownloadDirectory"] ?? string.Empty,
            MaxConcurrentDownloads = int.TryParse(_configuration["Download:MaxConcurrentDownloads"], out var mc) ? mc : 3,
            MaxRetries = int.TryParse(_configuration["Download:MaxRetries"], out var mr) ? mr : 3,
            UseNasForDownload = bool.TryParse(_configuration["Download:UseNasForDownload"], out var un) && un
        };
    }

    private async Task LoadTasksFromDbAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var entities = await db.DownloadTasks
                .OrderByDescending(t => t.CreatedAt)
                .ToListAsync();

            foreach (var entity in entities)
            {
                var task = MapToModel(entity);
                _tasks[task.Id] = task;

                if (task.Status == DownloadStatus.Downloading || task.Status == DownloadStatus.Queued)
                {
                    task.Status = DownloadStatus.Paused;
                    task.ErrorMessage = "程序重启，任务已暂停";
                }
            }

            _loaded = true;
            _logger.Information("[Download] 从数据库加载了 {Count} 个下载任务", entities.Count);
        }
        catch (Exception ex)
        {
            _logger.Information("[Download] 加载下载任务失败: {Error}", ex.Message);
            _loaded = true;
        }
    }

    private async Task SaveTaskToDbAsync(DownloadTask task)
    {
        if (!_loaded) return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var entity = MapToEntity(task);
            var existing = await db.DownloadTasks.FindAsync(task.Id);
            if (existing != null)
            {
                db.Entry(existing).CurrentValues.SetValues(entity);
            }
            else
            {
                db.DownloadTasks.Add(entity);
            }
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.Information("[Download] 保存任务状态失败: {Error}", ex.Message);
        }
    }

    private async Task DeleteTaskFromDbAsync(string taskId)
    {
        if (!_loaded) return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var entity = await db.DownloadTasks.FindAsync(taskId);
            if (entity != null)
            {
                db.DownloadTasks.Remove(entity);
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.Information("[Download] 删除任务记录失败: {Error}", ex.Message);
        }
    }

    public async Task<string> AddDownloadAsync(string sourceUrl, string? customName = null)
    {
        var sourceType = DetectSourceType(sourceUrl);
        var name = customName ?? ExtractNameFromUrl(sourceUrl);

        var task = new DownloadTask
        {
            Name = name,
            SourceUrl = sourceUrl,
            SourceType = sourceType,
            MaxRetries = _settings.MaxRetries
        };

        _tasks[task.Id] = task;
        await SaveTaskToDbAsync(task);
        _logger.Information("[Download] 添加下载任务: {Name} ({Type})", task.Name, task.SourceType);

        _ = ProcessDownloadAsync(task);
        return task.Id;
    }

    private static DownloadSourceType DetectSourceType(string url)
    {
        if (url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            return DownloadSourceType.Magnet;
        if (url.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
            return DownloadSourceType.TorrentFile;
        if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return DownloadSourceType.TorrentFile;
        return DownloadSourceType.DirectHttp;
    }

    private static string ExtractNameFromUrl(string url)
    {
        try
        {
            if (url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                var dnIndex = url.IndexOf("dn=", StringComparison.OrdinalIgnoreCase);
                if (dnIndex >= 0)
                {
                    var dnStart = dnIndex + 3;
                    var dnEnd = url.IndexOf('&', dnStart);
                    var dn = dnEnd > 0 ? url[dnStart..dnEnd] : url[dnStart..];
                    return Uri.UnescapeDataString(dn);
                }
                return "磁力链接下载";
            }

            var uri = new Uri(url);
            var fileName = Path.GetFileName(uri.LocalPath);
            if (!string.IsNullOrWhiteSpace(fileName))
                return Uri.UnescapeDataString(fileName);
            return uri.Host;
        }
        catch
        {
            return "未知下载";
        }
    }

    private async Task ProcessDownloadAsync(DownloadTask task)
    {
        try
        {
            await _concurrencySemaphore.WaitAsync();

            task.Status = DownloadStatus.Downloading;
            task.StartedAt = DateTime.Now;
            task.ErrorMessage = null;

            var saveDir = await GetDefaultDownloadDirectoryAsync();
            Directory.CreateDirectory(saveDir);

            if (task.SourceType == DownloadSourceType.Magnet || task.SourceType == DownloadSourceType.TorrentFile)
            {
                task.SavePath = saveDir;
            }
            else
            {
                task.SavePath = Path.Combine(saveDir, SanitizeFileName(task.Name));
            }

            await SaveTaskToDbAsync(task);

            var cts = new CancellationTokenSource();
            _cancellations[task.Id] = cts;
            _lastBytes[task.Id] = 0;

            if (task.SourceType == DownloadSourceType.Magnet || task.SourceType == DownloadSourceType.TorrentFile)
            {
                await DownloadViaTorrentAsync(task, cts.Token);
            }
            else
            {
                await DownloadViaHttpAsync(task, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            if (task.Status == DownloadStatus.Paused)
            {
                _logger.Information("[Download] 任务已暂停: {Name}", task.Name);
            }
            else
            {
                task.Status = DownloadStatus.Cancelled;
            }
            await SaveTaskToDbAsync(task);
        }
        catch (Exception ex)
        {
            _logger.Information("[Download] 下载失败: {Name}, 错误: {Error}", task.Name, ex.Message);
            task.ErrorMessage = ex.Message;

            if (task.RetryCount < task.MaxRetries)
            {
                task.RetryCount++;
                task.Status = DownloadStatus.Queued;
                _logger.Information("[Download] 重试 {Retry}/{Max}: {Name}", task.RetryCount, task.MaxRetries, task.Name);
                await SaveTaskToDbAsync(task);
                _ = ProcessDownloadAsync(task);
                return;
            }

            task.Status = DownloadStatus.Failed;
            await SaveTaskToDbAsync(task);
        }
        finally
        {
            _cancellations.TryRemove(task.Id, out _);
            _lastBytes.TryRemove(task.Id, out _);
            _concurrencySemaphore.Release();
        }
    }

    private async Task DownloadViaHttpAsync(DownloadTask task, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("DownloadClient");
        // 使用 CancellationToken 控制取消，设置超时很大或直接禁用
        client.Timeout = Timeout.InfiniteTimeSpan;

        var tempPath = task.SavePath + ".tmp";
        var finalPath = task.SavePath;

        try
        {
            // 确保下载目录存在
            var directory = Path.GetDirectoryName(finalPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // 获取续传偏移量
            var resumeOffset = 0L;
            if (File.Exists(tempPath))
            {
                resumeOffset = new FileInfo(tempPath).Length;
            }

            // 构建请求
            using var request = new HttpRequestMessage(HttpMethod.Get, task.SourceUrl);
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/114.0.0.0 Safari/537.36");
            request.Headers.Add("Accept", "*/*");

            if (resumeOffset > 0)
            {
                request.Headers.Range = new RangeHeaderValue(resumeOffset, null);
            }

            // 发送请求，只读取头部
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            // 确定写入模式
            long actualResumeOffset = resumeOffset;
            FileMode fileMode;
            if (response.StatusCode == HttpStatusCode.PartialContent && resumeOffset > 0)
            {
                fileMode = FileMode.Append;
            }
            else
            {
                fileMode = FileMode.Create;
                actualResumeOffset = 0;
            }

            // 更新总大小（服务器返回 Content-Length 时总是更新）
            if (response.Content.Headers.ContentLength is long contentLength)
            {
                if (response.StatusCode == HttpStatusCode.PartialContent)
                    task.TotalBytes = actualResumeOffset + contentLength;
                else
                    task.TotalBytes = contentLength;
            }

            // 打开响应流和文件流
            using var responseStream = await response.Content.ReadAsStreamAsync(ct);
            using var fileStream = new FileStream(tempPath, fileMode, FileAccess.Write, FileShare.Read, bufferSize: 262144, useAsync: true);

            task.DownloadedBytes = actualResumeOffset;

            var buffer = new byte[262144];
            int bytesRead;
            while ((bytesRead = await responseStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                task.DownloadedBytes += bytesRead;
            }

            task.Status = DownloadStatus.Completed;
            task.CompletedAt = DateTime.Now;

            // 取消检查，避免在移动文件时做无用功
            ct.ThrowIfCancellationRequested();

            // 重试移动临时文件到最终位置
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    if (File.Exists(finalPath))
                        File.Delete(finalPath);
                    responseStream.Dispose();
                    fileStream.Dispose();
                    File.Move(tempPath, finalPath);
                    break;
                }
                catch (IOException ex) when (i < 2)
                {
                    await Task.Delay(500, ct);
                }
            }

            // 自动添加 BT 任务
            //if (Path.GetExtension(finalPath).Equals(".torrent", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Information("[Download] 检测到种子文件，自动开始BT下载");
                var fileUri = "file:///" + finalPath.Replace("\\", "/");
                await AddDownloadAsync(fileUri, Path.GetFileNameWithoutExtension(finalPath));
            }

            _logger.Information("[Download] HTTP下载完成: {Name}", task.Name);
        }
        catch (OperationCanceledException)
        {
            task.Status = DownloadStatus.Cancelled;
            _logger.Information("[Download] 下载已取消: {Name}", task.Name);
            TryDeleteTempFile(tempPath);
            throw; // 继续传播取消
        }
        catch (Exception ex)
        {
            task.Status = DownloadStatus.Failed;
            task.ErrorMessage = ex.Message;
            _logger.Error(ex, "[Download] HTTP下载失败: {Name}", task.Name);
            TryDeleteTempFile(tempPath);
        }
        finally
        {
            await SaveTaskToDbAsync(task);
        }
    }

    private void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch { /* 忽略清理失败 */ }
    }
    private async Task DownloadViaTorrentAsync(DownloadTask task, CancellationToken ct)
    {
        try
        {
            var saveDir = task.SavePath ?? await GetDefaultDownloadDirectoryAsync();
            Directory.CreateDirectory(saveDir);

            await Task.Run(async () =>
            {
                var cacheDir = Path.Combine(saveDir, ".monotorrent_cache");
                Directory.CreateDirectory(cacheDir);

                var engineSettings = new EngineSettingsBuilder
                {
                    AllowPortForwarding = true,
                    AllowLocalPeerDiscovery = true,
                    AutoSaveLoadDhtCache = true,
                    AutoSaveLoadMagnetLinkMetadata = true,
                    CacheDirectory = cacheDir,
                    ConnectionTimeout = TimeSpan.FromSeconds(15),
                    //ListenPort = 0
                }.ToSettings();

                using var engine = new ClientEngine(engineSettings);

                var torrentSettings = new TorrentSettingsBuilder
                {
                    AllowDht = true,
                    AllowPeerExchange = true,
                    MaximumConnections = 60,
                    CreateContainingDirectory = true
                }.ToSettings();

                TorrentManager manager;

                // ---- 1. 添加任务（已含异常捕获） ----
                try
                {
                    if (task.SourceType == DownloadSourceType.Magnet)
                    {
                        // 添加 Tracker 列表（在添加任务前设置）
                        List<string> trickList = new List<string>
                        {
                            "udp://tracker.opentrackr.org:1337/announce",
                            "udp://tracker.openbittorrent.com:6969/announce",
                            "udp://tracker.coppersurfer.tk:6969/announce",
                            "udp://tracker.leechers-paradise.org:6969/announce",
                            "udp://tracker.internetwarriors.net:1337/announce",
                            "udp://explodie.org:6969/announce",
                            "udp://tracker.pirateparty.gr:6969/announce",
                            "udp://tracker.cyberia.is:6969/announce",
                            "udp://tracker.zer0day.to:1337/announce"
                        };
                        var magnet = MagnetLink.Parse(task.SourceUrl);
                        _logger.Information("[Download] 磁力链接已解析");
                        
                          manager = await engine.AddAsync(magnet, saveDir, torrentSettings);
                    }
                    else
                    {
                        byte[] torrentData;
                        if (task.SourceUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                        {
                            var localPath = new Uri(task.SourceUrl).LocalPath;
                            _logger.Information("[Download] 正在读取本地种子文件: {Path}", localPath);
                            torrentData = await File.ReadAllBytesAsync(localPath, ct);
                        }
                        else
                        {
                            using var client = _httpClientFactory.CreateClient("DownloadClient");
                            _logger.Information("[Download] 正在下载种子文件: {Url}", task.SourceUrl);
                            torrentData = await client.GetByteArrayAsync(task.SourceUrl, ct);
                        }

                        var torrent = Torrent.Load(torrentData);
                        _logger.Information("[Download] 种子文件已解析: {Name}, 文件数: {Count}, 大小: {Size}",
                            torrent.Name, torrent.Files?.Count ?? 0, torrent.Size);
                        manager = await engine.AddAsync(torrent, saveDir, torrentSettings);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "[Download] 添加BT任务失败");
                    throw; // 由外层 catch 标记任务失败
                }

                // 启动引擎
                try
                {
                    await manager.StartAsync();
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "[Download] 启动BT引擎失败");
                    throw;
                }

                task.Status = DownloadStatus.Downloading;
                _logger.Information("[Download] BT引擎已启动");

                // ---- 2. 等待元数据（磁力链接），内部有独立异常处理 ----
                if (task.SourceType == DownloadSourceType.Magnet)
                {
                    _logger.Information("[Download] 等待磁力链接元数据下载 (最多5分钟)...");
                    using var metadataCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, metadataCts.Token);
                    try
                    {
                        while (manager.Torrent == null)
                        {
                            linkedCts.Token.ThrowIfCancellationRequested();
                            await Task.Delay(500, linkedCts.Token);
                        }
                        task.TotalBytes = manager.Torrent.Size;
                        _logger.Information("[Download] 磁力链接元数据已获取: {Name}, 大小: {Size}",
                            manager.Torrent.Name, manager.Torrent.Size);
                    }
                    catch (OperationCanceledException) when (metadataCts.IsCancellationRequested)
                    {
                        _logger.Warning("[Download] 磁力链接元数据获取超时");
                        await TryStopManager(manager);
                        throw new Exception("磁力链接元数据获取超时，可能没有可用的种子节点");
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "[Download] 等待元数据期间出现异常");
                        await TryStopManager(manager);
                        throw;
                    }
                }
                else
                {
                    task.TotalBytes = manager.Torrent?.Size ?? -1;
                }

                // ---- 3. 下载进度循环（每次迭代都受保护） ----
                var lastDownloaded = manager.Monitor.DataBytesDownloaded;
                var noProgressTimeout = TimeSpan.FromMinutes(10);
                var noProgressTimer = Stopwatch.StartNew();

                while (manager.State != TorrentState.Stopped &&
                       manager.State != TorrentState.Seeding &&
                       manager.State != TorrentState.Error)
                {
                    ct.ThrowIfCancellationRequested();

                    // 关键：把每次进度查询和所有可能抛异常的操作都包起来
                    try
                    {
                        long downloaded = manager.Monitor.DataBytesDownloaded;
                        task.DownloadedBytes = downloaded;

                        if (task.TotalBytes <= 0 && manager.Torrent != null)
                        {
                            task.TotalBytes = manager.Torrent.Size;
                        }

                        // 无数据超时检测
                        if (downloaded > lastDownloaded)
                        {
                            lastDownloaded = downloaded;
                            noProgressTimer.Restart();
                        }
                        else if (noProgressTimer.Elapsed > noProgressTimeout)
                        {
                            _logger.Warning("[Download] 连续{Minutes}分钟无速度，主动停止", noProgressTimeout.TotalMinutes);
                            await TryStopManager(manager);
                            throw new Exception($"下载卡死：连续 {noProgressTimeout.TotalMinutes} 分钟无数据传入，可能无可用Peer");
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // 循环内发生任何意外，立即停止引擎，让任务进入失败流程
                        _logger.Error(ex, "[Download] 进度监测期间发生异常，停止下载");
                        await TryStopManager(manager);
                        throw; // 直接抛出，外层 catch 会处理
                    }

                    await Task.Delay(500, ct);
                }

                // 检查引擎是否因为错误退出
                if (manager.State == TorrentState.Error)
                {
                    throw new Exception($"BT下载引擎错误: {manager.Error?.Exception.Message ?? "未知内部错误"}");
                }

                // 下载完成
                task.DownloadedBytes = task.TotalBytes > 0 ? task.TotalBytes : manager.Monitor.DataBytesDownloaded;
                task.Status = DownloadStatus.Completed;
                task.CompletedAt = DateTime.Now;
                _logger.Information("[Download] BT下载完成: {Name}", task.Name);

            }, ct);
        }
        catch (OperationCanceledException)
        {
            //task.Status = DownloadStatus.Cancelled;
            _logger.Information("[Download] BT下载已取消: {Name}", task.Name);
            throw;
        }
        catch (Exception ex)
        {
            task.Status = DownloadStatus.Failed;
            task.ErrorMessage = ex.Message;
            _logger.Error(ex, "[Download] BT下载失败: {Name}", task.Name);
        }
        finally
        {
            await SaveTaskToDbAsync(task);
        }
    }

    // 辅助方法：安全地停止 TorrentManager
    private async Task TryStopManager(TorrentManager manager)
    {
        try
        {
            if (manager != null && manager.State != TorrentState.Stopped)
                await manager.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex.Message.ToString(), "[Download] 停止BT引擎时发生异常");
        }
    }
    public async Task PauseDownloadAsync(string taskId)
    {
        if (_tasks.TryGetValue(taskId, out var task) && task.Status == DownloadStatus.Downloading)
        {
            task.Status = DownloadStatus.Paused;
            if (_cancellations.TryGetValue(taskId, out var cts))
                cts.Cancel();
            await SaveTaskToDbAsync(task);
        }
    }

    public async Task ResumeDownloadAsync(string taskId)
    {
        if (_tasks.TryGetValue(taskId, out var task) && task.Status == DownloadStatus.Paused)
        {
            task.Status = DownloadStatus.Queued;
            await SaveTaskToDbAsync(task);
            _ = ProcessDownloadAsync(task);
        }
    }

    public async Task CancelDownloadAsync(string taskId)
    {
        if (_tasks.TryGetValue(taskId, out var task) &&
            task.Status is DownloadStatus.Downloading or DownloadStatus.Queued or DownloadStatus.Paused)
        {
            task.Status = DownloadStatus.Cancelled;
            if (_cancellations.TryGetValue(taskId, out var cts))
                cts.Cancel();
            await SaveTaskToDbAsync(task);
        }
    }

    public async Task RetryDownloadAsync(string taskId)
    {
        if (_tasks.TryGetValue(taskId, out var task) && task.Status == DownloadStatus.Failed)
        {
            task.RetryCount = 0;
            task.Status = DownloadStatus.Queued;
            task.ErrorMessage = null;
            await SaveTaskToDbAsync(task);
            _ = ProcessDownloadAsync(task);
        }
    }

    public async Task RemoveDownloadAsync(string taskId)
    {
        if (_tasks.TryRemove(taskId, out var task))
        {
            if (_cancellations.TryRemove(taskId, out var cts))
                cts.Cancel();

            if (task.Status == DownloadStatus.Completed && task.SavePath != null)
            {
                try
                {
                    if (Directory.Exists(task.SavePath))
                    {
                        var cacheDir = Path.Combine(task.SavePath, ".monotorrent_cache");
                        if (Directory.Exists(cacheDir))
                            Directory.Delete(cacheDir, true);
                    }
                    else if (File.Exists(task.SavePath))
                    {
                        File.Delete(task.SavePath);
                        var tmpPath = task.SavePath + ".tmp";
                        if (File.Exists(tmpPath))
                            File.Delete(tmpPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Information("[Download] 删除文件失败: {Error}", ex.Message);
                }
            }

            await DeleteTaskFromDbAsync(taskId);
        }
    }

    public async Task SetPriorityAsync(string taskId, DownloadPriority priority)
    {
        if (_tasks.TryGetValue(taskId, out var task))
        {
            task.Priority = priority;
            await SaveTaskToDbAsync(task);
        }
    }

    public Task<IReadOnlyList<DownloadTask>> GetAllTasksAsync()
    {
        return Task.FromResult<IReadOnlyList<DownloadTask>>(
            _tasks.Values.OrderByDescending(t => t.Priority).ThenBy(t => t.CreatedAt).ToList());
    }

    public DownloadTask? GetTask(string taskId)
    {
        _tasks.TryGetValue(taskId, out var task);
        return task;
    }

    public Task UpdateSettingsAsync(DownloadSettings settings)
    {
        _settings = settings;
        _concurrencySemaphore = new SemaphoreSlim(settings.MaxConcurrentDownloads);
        return Task.CompletedTask;
    }

    public DownloadSettings GetSettings() => _settings;

    public Task<string> GetDefaultDownloadDirectoryAsync()
    {
        var dir = _settings.UseNasForDownload && !string.IsNullOrWhiteSpace(_settings.NasDownloadDirectory)
            ? _settings.NasDownloadDirectory
            : _settings.DownloadDirectory;

        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        return Task.FromResult(dir);
    }

    private void OnProgressTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        foreach (var task in _tasks.Values.Where(t => t.Status == DownloadStatus.Downloading))
        {
            var currentBytes = task.DownloadedBytes;
            var lastBytes = _lastBytes.GetValueOrDefault(task.Id, 0);
            task.DownloadSpeedBps = currentBytes - lastBytes;
            _lastBytes[task.Id] = currentBytes;

            DownloadProgressChanged?.Invoke(this, new DownloadProgressEventArgs
            {
                TaskId = task.Id,
                Status = task.Status,
                DownloadedBytes = task.DownloadedBytes,
                TotalBytes = task.TotalBytes,
                SpeedBps = task.DownloadSpeedBps
            });
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "download" : sanitized;
    }

    private static DownloadTask MapToModel(DownloadTaskEntity entity)
    {
        return new DownloadTask
        {
            Id = entity.Id,
            Name = entity.Name,
            SourceUrl = entity.SourceUrl,
            SourceType = (DownloadSourceType)entity.SourceType,
            Status = (DownloadStatus)entity.Status,
            Priority = (DownloadPriority)entity.Priority,
            TotalBytes = entity.TotalBytes,
            DownloadedBytes = entity.DownloadedBytes,
            DownloadSpeedBps = entity.DownloadSpeedBps,
            SavePath = entity.SavePath,
            CreatedAt = entity.CreatedAt,
            StartedAt = entity.StartedAt,
            CompletedAt = entity.CompletedAt,
            ErrorMessage = entity.ErrorMessage,
            RetryCount = entity.RetryCount,
            MaxRetries = entity.MaxRetries
        };
    }

    private static DownloadTaskEntity MapToEntity(DownloadTask task)
    {
        return new DownloadTaskEntity
        {
            Id = task.Id,
            Name = task.Name,
            SourceUrl = task.SourceUrl,
            SourceType = (int)task.SourceType,
            Status = (int)task.Status,
            Priority = (int)task.Priority,
            TotalBytes = task.TotalBytes,
            DownloadedBytes = task.DownloadedBytes,
            DownloadSpeedBps = task.DownloadSpeedBps,
            SavePath = task.SavePath,
            CreatedAt = task.CreatedAt,
            StartedAt = task.StartedAt,
            CompletedAt = task.CompletedAt,
            ErrorMessage = task.ErrorMessage,
            RetryCount = task.RetryCount,
            MaxRetries = task.MaxRetries
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _progressTimer?.Stop();
        _progressTimer?.Dispose();

        foreach (var cts in _cancellations.Values)
            cts.Cancel();

        _concurrencySemaphore?.Dispose();

        foreach (var task in _tasks.Values)
        {
            if (task.Status == DownloadStatus.Downloading)
            {
                task.Status = DownloadStatus.Paused;
                task.ErrorMessage = "程序已关闭";
            }
        }
    }
}