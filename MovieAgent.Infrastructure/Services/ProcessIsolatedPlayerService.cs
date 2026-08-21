using MovieAgent.Core.Interfaces;
using MovieAgent.FFmpegDecoder;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TMDbLib.Objects.Authentication;
using Vortice.Direct3D11;
using Vortice.Direct3D12;
using Vortice.Direct3D9;

namespace MovieAgent.Infrastructure.Services
{
    /// <summary>
    /// 进程隔离播放器服务
    /// 将视频解码逻辑隔离到独立进程，避免解码线程阻塞UI线程
    /// </summary>
    public class ProcessIsolatedPlayerService : IPlayerService, IDisposable
    {
        #region 字段

        /// <summary>
        /// 日志服务
        /// </summary>
        private readonly ILoggerService _logger;
        private readonly IConfiguration _configuration;

        /// <summary>
        /// 解码器进程实例
        /// </summary>
        private Process? _decoderProcess;

        /// <summary>
        /// 命名管道客户端流，用于与解码器进程通信
        /// </summary>
        private NamedPipeClientStream? _pipeClient;

        /// <summary>
        /// 管道读取器
        /// </summary>
        private StreamReader? _reader;

        /// <summary>
        /// 管道写入器
        /// </summary>
        private StreamWriter? _writer;

        /// <summary>
        /// 服务是否正在运行
        /// </summary>
        private bool _isRunning;

        /// <summary>
        /// 是否已释放资源
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// 共享内存管理器，用于高效传输视频帧
        /// </summary>
        private SharedMemoryManager? _sharedMemory;

        /// <summary>
        /// 帧读取任务
        /// </summary>
        private Task? _frameReadTask;

        /// <summary>
        /// 管道名称
        /// </summary>
        private string? _pipeName;

        /// <summary>
        /// 帧率
        /// </summary>
        private double _fps;

        #endregion
        #region 属性

        /// <summary>
        /// 是否正在播放
        /// </summary>
        public bool IsPlaying { get; private set; }

        /// <summary>
        /// 是否处于暂停状态
        /// </summary>
        public bool IsPaused { get; private set; }

        /// <summary>
        /// 视频总时长
        /// </summary>
        public TimeSpan Duration { get; private set; }

        /// <summary>
        /// 当前播放位置
        /// </summary>
        public TimeSpan Position { get; private set; }

        /// <summary>
        /// 视频时间戳
        /// </summary>
        public TimeSpan VideoTimestamp { get; private set; }

        /// <summary>
        /// 音频时间戳
        /// </summary>
        public TimeSpan AudioTimestamp { get; private set; }

        /// <summary>
        /// 音频播放位置
        /// </summary>
        public long AudioPlayPosition { get; private set; } = 0;

        /// <summary>
        /// 当前音量（0.0-1.0）
        /// </summary>
        public float Volume { get; private set; }

        /// <summary>
        /// 音频轨道数量
        /// </summary>
        public int AudioTrackCount => 0;
        /// <summary>
        /// 帧率
        /// </summary>
        public double fps => _fps;

        /// <summary>
        /// 当前音频轨道索引
        /// </summary>
        public int CurrentAudioTrack { get; private set; } = -1;

        /// <summary>
        /// 字幕轨道数量
        /// </summary>
        public int SpuTrackCount => 0;

        /// <summary>
        /// 当前字幕轨道索引
        /// </summary>
        public int CurrentSpuTrack { get; private set; } = -1;

        /// <summary>
        /// 视频宽度
        /// </summary>
        public int VideoWidth { get; private set; }

        /// <summary>
        /// 视频高度
        /// </summary>
        public int VideoHeight { get; private set; }

        /// <summary>
        /// 是否为杜比视界视频（进程隔离模式不支持DV检测，默认false）
        /// </summary>
        public bool IsDolbyVision => false;

        /// <summary>
        /// 是否为HDR传输特性（进程隔离模式不支持检测，默认false）
        /// </summary>
        public bool IsPqTransfer => false;

        /// <summary>
        /// 是否为ICtCp色彩空间输入（进程隔离模式不支持检测，默认false）
        /// </summary>
        public bool IsIctcpInput => false;

        public DoviRenderMetadata? DoviMetadata => null;

        /// <summary>
        /// 当前解码器名称
        /// </summary>
        private string _currentDecoderName = string.Empty;

        /// <summary>
        /// 当前解码模式（Auto/Hardware/Software）
        /// </summary>
        private string _currentDecodeMode = "Auto";

        /// <summary>
        /// 获取当前解码器名称
        /// </summary>
        public string CurrentDecoderName => _currentDecoderName;

        /// <summary>
        /// 获取当前解码模式
        /// </summary>
        public string CurrentDecodeMode => _currentDecodeMode;

 
        public FFmpegDecoderEngine.D3DMode CurrentD3dModel => throw new NotImplementedException();

        FFmpegDecoderEngine.D3DMode? IPlayerService.CurrentD3dModel => CurrentD3dModel;


        #endregion

        #region 事件

        /// <summary>
        /// 帧更新事件，当收到新的视频帧时触发
        /// </summary>
        public event EventHandler<FrameData>? FrameUpdated;

        /// <summary>
        /// 播放结束事件
        /// </summary>
        public event EventHandler? PlaybackEnded;

        /// <summary>
        /// Blazor请求播放事件
        /// </summary>
        public event EventHandler? PlaybackRequestedByBlazor;

        /// <summary>
        /// 播放请求标志，防止重复请求
        /// </summary>
        private bool _playbackRequestedByBlazor;

        /// <summary>
        /// 当前请求播放的文件路径
        /// </summary>
        private string? _currentRequestedFilePath;

        /// <summary>
        /// 播放失败事件
        /// </summary>
        public event EventHandler<string>? PlaybackFailed;

        /// <summary>
        /// 性能警告事件，当解码性能下降时触发
        /// </summary>
        public event EventHandler<DecodePerformanceWarningMessage>? PerformanceWarning;

        /// <summary>
        /// 音频轨道信息接收事件
        /// </summary>
        public event EventHandler<AudioTracksMessage>? AudioTracksReceived;

        /// <summary>
        /// 字幕轨道信息接收事件
        /// </summary>
        public event EventHandler<SubtitleTracksMessage>? SubtitleTracksReceived;

        /// <summary>
        /// 截图结果接收事件
        /// </summary>
        public event EventHandler<ScreenshotResultMessage>? ScreenshotResultReceived;

        /// <summary>
        /// 字幕延迟信息接收事件
        /// </summary>
        public event EventHandler<SubtitleDelayMessage>? SubtitleDelayReceived;

        /// <summary>
        /// 分辨率降级通知事件
        /// </summary>
        public event EventHandler<ResolutionDownscaleMessage>? ResolutionDownscale;

        /// <summary>
        /// 字幕解码完成事件
        /// </summary>
        public event EventHandler<SubtitleDecodedMessage>? SubtitleDecoded;

        #endregion

        #region 自动重启机制

        /// <summary>
        /// 当前播放的文件路径（用于自动重启）
        /// </summary>
        private string? _currentFilePath;

        /// <summary>
        /// 重试次数
        /// </summary>
        private int _retryCount;

        /// <summary>
        /// 最大重试次数
        /// </summary>
        private const int MaxRetries = 3;

        /// <summary>
        /// 是否正在重新连接
        /// </summary>
        private bool _isReconnecting;

        /// <summary>
        /// 是否启用自动重启
        /// </summary>
        private bool _autoRestartEnabled = true;

        #endregion

        #region 构造函数

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="logger">日志服务</param>
        public ProcessIsolatedPlayerService(ILoggerService logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
        }

        #endregion

        #region 公共方法

        /// <summary>
        /// 请求播放（从Blazor调用）
        /// 设置播放请求标志并触发PlaybackRequestedByBlazor事件
        /// </summary>
        /// <param name="filePath">视频文件路径</param>
        public void RequestPlayback(string filePath)
        {
            if (!_playbackRequestedByBlazor)
            {
                _playbackRequestedByBlazor = true;
                _currentRequestedFilePath = filePath;
                _logger.Debug($"[Player] RequestPlayback called with: {filePath} - firing PlaybackRequestedByBlazor event");
                PlaybackRequestedByBlazor?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// 获取当前请求播放的文件路径
        /// </summary>
        /// <returns>文件路径，如果没有请求则返回null</returns>
        public string? GetCurrentRequestedFilePath() => _currentRequestedFilePath;

        /// <summary>
        /// 开始播放视频
        /// </summary>
        /// <param name="filePath">视频文件路径</param>
        /// <returns>任务</returns>
        public async Task PlayAsync(string filePath)
        {
            _logger.Information($"[Player] ===== PlayAsync 开始 ===== ");
            _logger.Information($"[Player] 文件路径: {filePath}");

            _logger.Debug($"[Player] 步骤1: 停止当前播放");
            await StopInternalAsync();
            _logger.Debug($"[Player] 步骤1完成: 已停止当前播放");

            // 保存当前文件路径用于自动重启
            _currentFilePath = filePath;
            _retryCount = 0;

            // 重新设置运行标志，允许新的消息接收循环
            _logger.Debug($"[Player] 步骤2: 设置运行标志");
            _isRunning = true;

            _logger.Debug($"[Player] 步骤3: 启动解码器进程");
            await StartDecoderAsync(filePath);

            _logger.Information($"[Player] ===== PlayAsync 完成 ===== ");
        }

        #endregion

        #region 私有方法

        /// <summary>
        /// 启动解码器进程
        /// </summary>
        /// <param name="filePath">视频文件路径</param>
        /// <returns>任务</returns>
        private async Task StartDecoderAsync(string filePath)
        {
            // 生成唯一的管道名称
            _pipeName = $"movieagent_ffmpeg_{Guid.NewGuid():N}";
            _logger.Debug($"[Player] Generated pipe name: {_pipeName}");

            // 获取解码器可执行文件路径
            string decoderPath = GetDecoderPath();
            _logger.Debug($"[Player] Looking for decoder at: {decoderPath}");

            // 检查解码器是否存在
            if (!File.Exists(decoderPath))
            {
                string errorMsg = $"Decoder executable not found at: {decoderPath}";
                _logger.Error($"[Player] {errorMsg}");
                PlaybackFailed?.Invoke(this, errorMsg);
                throw new FileNotFoundException(errorMsg, decoderPath);
            }

            try
            {
                // 创建解码器进程
                _decoderProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = decoderPath,
                        Arguments = $"--pipe-name {_pipeName}",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    },
                    EnableRaisingEvents = true
                };

                // 订阅进程退出事件
                _decoderProcess.Exited += OnDecoderProcessExited;

                // 订阅输出和错误输出事件
                _decoderProcess.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        _logger.Debug($"[Decoder] {e.Data}");
                };
                _decoderProcess.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        _logger.Error($"[Decoder Error] {e.Data}");
                };

                // 启动解码器进程
                _logger.Information($"[Player] Starting decoder process...");
                _decoderProcess.Start();
                _decoderProcess.BeginOutputReadLine();
                _decoderProcess.BeginErrorReadLine();
                _logger.Information($"[Player] Decoder process started with PID: {_decoderProcess.Id}");

                // 等待解码器初始化
                await Task.Delay(500);

                // 连接到命名管道
                _logger.Information($"[Player] Connecting to pipe: {_pipeName}");
                _pipeClient = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

                try
                {
                    await _pipeClient.ConnectAsync(5000);
                    _logger.Information($"[Player] Connected to decoder pipe successfully");
                }
                catch (TimeoutException ex)
                {
                    string errorMsg = $"Failed to connect to decoder pipe within timeout: {ex.Message}";
                    _logger.Error($"[Player] {errorMsg}");
                    PlaybackFailed?.Invoke(this, errorMsg);
                    throw new TimeoutException(errorMsg, ex);
                }
                catch (Exception ex)
                {
                    string errorMsg = $"Pipe connection failed: {ex.Message}";
                    _logger.Error($"[Player] {errorMsg}");
                    PlaybackFailed?.Invoke(this, errorMsg);
                    throw new InvalidOperationException(errorMsg, ex);
                }

                // 创建管道读写器
                _reader = new StreamReader(_pipeClient);
                _writer = new StreamWriter(_pipeClient) { AutoFlush = true };

                // 设置运行标志并启动消息接收循环
                _isRunning = true;
                _ = Task.Run(ReceiveMessagesAsync);
                _logger.Information($"[Player] Message receiver task started");

                // 发送播放命令
                _logger.Information($"[Player] Sending Play command for file: {filePath}");
                var decodeMode = _configuration["Decode:DecodeMode"] ?? "Auto";
                await SendCommandAsync(new DecoderCommand { Command = "Play", FilePath = filePath, DecodeMode = decodeMode });
                _logger.Information($"[Player] Playback initiated with decode mode: {decodeMode}");
            }
            catch (Exception ex)
            {
                string errorMsg = $"Playback initialization failed: {ex.Message}";
                _logger.Error($"[Player] {errorMsg}\nStack trace: {ex.StackTrace}");
                PlaybackFailed?.Invoke(this, errorMsg);
                Cleanup();
                throw;
            }
        }

        /// <summary>
        /// 获取解码器可执行文件路径
        /// 按优先级检查多个可能的路径
        /// </summary>
        /// <returns>解码器可执行文件路径</returns>
        private string GetDecoderPath()
        {
            string baseDir = AppContext.BaseDirectory;
            _logger.Debug($"[Player] Base directory: {baseDir}");

            // 检查路径1：应用程序目录
            string decoderPath = Path.Combine(baseDir, "MovieAgent.FFmpegDecoder.exe");
            _logger.Debug($"[Player] Checking decoder path 1: {decoderPath}");
            if (File.Exists(decoderPath))
                return decoderPath;

            // 检查路径2：解决方案目录下的Debug版本
            string solutionDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
            _logger.Debug($"[Player] Solution directory: {solutionDir}");

            decoderPath = Path.Combine(solutionDir, "MovieAgent.FFmpegDecoder", "bin", "Debug", "net10.0", "MovieAgent.FFmpegDecoder.exe");
            _logger.Debug($"[Player] Checking decoder path 2: {decoderPath}");
            if (File.Exists(decoderPath))
                return decoderPath;

            // 检查路径3：解决方案目录下的Release版本
            decoderPath = Path.Combine(solutionDir, "MovieAgent.FFmpegDecoder", "bin", "Release", "net10.0", "MovieAgent.FFmpegDecoder.exe");
            _logger.Debug($"[Player] Checking decoder path 3: {decoderPath}");
            if (File.Exists(decoderPath))
                return decoderPath;

            // 检查路径4：相对路径下的Debug版本
            decoderPath = Path.Combine(baseDir, "..", "..", "MovieAgent.FFmpegDecoder", "bin", "Debug", "net10.0", "MovieAgent.FFmpegDecoder.exe");
            _logger.Debug($"[Player] Checking decoder path 4: {decoderPath}");
            if (File.Exists(decoderPath))
                return decoderPath;

            // 检查路径5：相对路径下的Release版本
            decoderPath = Path.Combine(baseDir, "..", "..", "MovieAgent.FFmpegDecoder", "bin", "Release", "net10.0", "MovieAgent.FFmpegDecoder.exe");
            _logger.Debug($"[Player] Checking decoder path 5: {decoderPath}");

            return decoderPath;
        }

        /// <summary>
        /// 消息接收循环
        /// 持续监听解码器进程发送的消息
        /// </summary>
        /// <returns>任务</returns>
        private async Task ReceiveMessagesAsync()
        {
            _logger.Debug("[IPC] Message receiver loop started");
            while (_isRunning && _pipeClient?.IsConnected == true)
            {
                try
                {
                    // 读取一行消息
                    string? message = await _reader?.ReadLineAsync();
                    if (message == null)
                    {
                        _logger.Debug("[IPC] Decoder disconnected - message is null");
                        break;
                    }

                    _logger.Debug($"[IPC] Raw message received: {message.Length} characters");
                    await HandleMessageAsync(message);
                }
                catch (IOException ex)
                {
                    _logger.Debug($"[IPC] Decoder connection closed: {ex.Message}");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Error($"[IPC] Error receiving message: {ex.Message}\n{ex.StackTrace}");
                }
            }
            _logger.Debug("[IPC] Message receiver loop ended");
        }

        /// <summary>
        /// 处理接收到的消息
        /// 根据消息类型进行相应的处理
        /// </summary>
        /// <param name="message">JSON格式的消息</param>
        /// <returns>任务</returns>
        private async Task HandleMessageAsync(string message)
        {
            try
            {
                using var doc = JsonDocument.Parse(message);
                string? type = doc.RootElement.GetProperty("Type").GetString();

                switch (type)
                {
                    case "Frame":
                        // 处理视频帧消息
                        var frame = JsonSerializer.Deserialize<FrameMessage>(message);
                        if (frame != null)
                        {
                            byte[] data = Convert.FromBase64String(frame.DataBase64);
                            _logger.Debug($"[Player] Frame received: {frame.Width}x{frame.Height}, data size: {data.Length} bytes");

                            VideoTimestamp = TimeSpan.FromMilliseconds(frame.VideoTimestamp);
                            AudioTimestamp = TimeSpan.FromMilliseconds(frame.AudioTimestamp);
                            AudioPlayPosition = frame.AudioPlayPosition;
                            Position = TimeSpan.FromMilliseconds(frame.VideoTimestamp);

                            var frameData = new FrameData
                            {
                                Width = frame.Width,
                                Height = frame.Height,
                                Data = data,
                                VideoTimestamp = frame.VideoTimestamp,
                                AudioTimestamp = frame.AudioTimestamp,
                                AudioPlayPosition = frame.AudioPlayPosition
                            };
                            FrameUpdated?.Invoke(this, frameData);
                        }
                        break;

                    case "Status":
                        // 处理播放状态消息
                        var status = JsonSerializer.Deserialize<StatusMessage>(message);
                        if (status != null)
                        {
                            IsPlaying = status.IsPlaying;
                            IsPaused = status.IsPaused;
                            Duration = TimeSpan.FromMilliseconds(status.DurationMs);
                            Position = TimeSpan.FromMilliseconds(status.PositionMs);
                        }
                        break;

                    case "Info":
                        // 处理视频信息消息
                        var info = JsonSerializer.Deserialize<InfoMessage>(message);
                        if (info != null)
                        {
                            Duration = TimeSpan.FromMilliseconds(info.DurationMs);
                            VideoWidth = info.VideoWidth;
                            VideoHeight = info.VideoHeight;
                            _currentDecoderName = info.DecoderName ?? "unknown";
                            _currentDecodeMode = info.DecodeMode ?? "Auto";
                            _logger.Debug($"[Player] Video info received: {info.VideoWidth}x{info.VideoHeight}, {info.Fps:F2}fps, Decoder: {_currentDecoderName}, Mode: {_currentDecodeMode}");
                            _fps = Math.Round(info.Fps, 2);

                            // 尝试使用共享内存
                            TryInitializeSharedMemory(info.VideoWidth, info.VideoHeight);
                        }
                        break;

                    case "PlaybackEnded":
                        // 处理播放结束消息
                        IsPlaying = false;
                        PlaybackEnded?.Invoke(this, EventArgs.Empty);
                        break;

                    case "Error":
                        // 处理错误消息
                        var error = JsonSerializer.Deserialize<ErrorMessage>(message);
                        if (error != null)
                        {
                            _logger.Error($"[Decoder Error] {error.Message}");
                        }
                        break;

                    case "PerformanceWarning":
                        // 处理性能警告消息
                        var performanceWarning = JsonSerializer.Deserialize<DecodePerformanceWarningMessage>(message);
                        if (performanceWarning != null)
                        {
                            _logger.Warning($"[Player] Performance warning received: {performanceWarning.Message}");
                            _logger.Warning($"[Player] Current resolution: {performanceWarning.CurrentWidth}x{performanceWarning.CurrentHeight}, Suggested: {performanceWarning.SuggestedWidth}x{performanceWarning.SuggestedHeight}");
                            PerformanceWarning?.Invoke(this, performanceWarning);
                        }
                        break;

                    case "AudioTracks":
                        // 处理音频轨道消息
                        var audioTracks = JsonSerializer.Deserialize<AudioTracksMessage>(message);
                        if (audioTracks != null)
                        {
                            _logger.Debug($"[Player] Audio tracks received: {audioTracks.TrackCount} tracks");
                            AudioTracksReceived?.Invoke(this, audioTracks);
                        }
                        break;

                    case "SubtitleTracks":
                        // 处理字幕轨道消息
                        var subtitleTracks = JsonSerializer.Deserialize<SubtitleTracksMessage>(message);
                        if (subtitleTracks != null)
                        {
                            _logger.Debug($"[Player] Subtitle tracks received: {subtitleTracks.TrackCount} tracks");
                            SubtitleTracksReceived?.Invoke(this, subtitleTracks);
                        }
                        break;

                    case "ScreenshotResult":
                        var screenshotResult = JsonSerializer.Deserialize<ScreenshotResultMessage>(message);
                        if (screenshotResult != null)
                        {
                            _logger.Debug($"[Player] Screenshot result: {(screenshotResult.Success ? "success" : "failed")}, path: {screenshotResult.FilePath}");
                            ScreenshotResultReceived?.Invoke(this, screenshotResult);
                        }
                        break;

                    case "SubtitleDelay":
                        var subtitleDelay = JsonSerializer.Deserialize<SubtitleDelayMessage>(message);
                        if (subtitleDelay != null)
                        {
                            _logger.Debug($"[Player] Subtitle delay: {subtitleDelay.DelayMs}ms");
                            SubtitleDelayReceived?.Invoke(this, subtitleDelay);
                        }
                        break;

                    case "ResolutionDownscale":
                        var resolutionDownscale = JsonSerializer.Deserialize<ResolutionDownscaleMessage>(message);
                        if (resolutionDownscale != null)
                        {
                            _logger.Information($"[Player] Resolution downscale: {resolutionDownscale.OriginalWidth}x{resolutionDownscale.OriginalHeight} -> {resolutionDownscale.TargetWidth}x{resolutionDownscale.TargetHeight}");
                            ResolutionDownscale?.Invoke(this, resolutionDownscale);
                        }
                        break;

                    case "SubtitleDecoded":
                        var subtitleDecoded = JsonSerializer.Deserialize<SubtitleDecodedMessage>(message);
                        if (subtitleDecoded != null)
                        {
                            _logger.Debug($"[Player] Subtitle decoded: {subtitleDecoded.Text}");
                            SubtitleDecoded?.Invoke(this, subtitleDecoded);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"[IPC] Error parsing message: {ex.Message}");
            }
        }

        /// <summary>
        /// 解码器进程退出事件处理
        /// 处理进程异常退出和自动重启逻辑
        /// </summary>
        /// <param name="sender">发送者</param>
        /// <param name="e">事件参数</param>
        private void OnDecoderProcessExited(object? sender, EventArgs e)
        {
            int exitCode = _decoderProcess?.ExitCode ?? -1;
            _logger.Information($"[Player] Decoder process exited with PID: {_decoderProcess?.Id}, ExitCode: {exitCode}");

            if (exitCode != 0)
            {
                string errorMsg = $"Decoder process crashed with exit code: {exitCode}";
                _logger.Error($"[Player] {errorMsg}");

                // 尝试自动重启
                if (_autoRestartEnabled && !_isReconnecting && !string.IsNullOrEmpty(_currentFilePath) && _retryCount < MaxRetries)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await AttemptReconnectAsync();
                        }
                        catch (Exception ex)
                        {
                            _logger.Error($"[Player] Auto-reconnect failed: {ex.Message}");
                            PlaybackFailed?.Invoke(this, errorMsg);
                        }
                    });
                    return;
                }
                else
                {
                    _logger.Warning($"[Player] Auto-reconnect disabled or max retries reached. Enabled: {_autoRestartEnabled}, Retries: {_retryCount}/{MaxRetries}");
                    PlaybackFailed?.Invoke(this, errorMsg);
                }
            }

            IsPlaying = false;
            Cleanup();
        }

        /// <summary>
        /// 尝试重新连接解码器进程
        /// 在解码器崩溃后自动恢复播放
        /// </summary>
        /// <returns>任务</returns>
        private async Task AttemptReconnectAsync()
        {
            if (_isReconnecting || string.IsNullOrEmpty(_currentFilePath))
                return;

            _isReconnecting = true;
            _retryCount++;

            _logger.Information($"[Player] Attempting auto-reconnect ({_retryCount}/{MaxRetries})...");

            try
            {
                // 等待一段时间再重试
                await Task.Delay(1000);

                // 清理旧资源
                Cleanup();

                // 保存当前播放位置
                var savedPosition = AudioTimestamp;

                // 重新启动解码器
                await StartDecoderAsync(_currentFilePath);

                // 如果有保存的位置，跳转到该位置
                if (savedPosition.TotalSeconds > 0)
                {
                    await Task.Delay(500); // 等待播放开始
                    Seek((int)savedPosition.TotalSeconds);
                    _logger.Information($"[Player] Resumed from position: {savedPosition}");
                }

                _logger.Information($"[Player] Auto-reconnect successful");
            }
            catch (Exception ex)
            {
                _logger.Error($"[Player] Auto-reconnect attempt {_retryCount} failed: {ex.Message}");

                if (_retryCount >= MaxRetries)
                {
                    _logger.Error($"[Player] Max retries reached, giving up");
                    PlaybackFailed?.Invoke(this, $"Decoder crashed and auto-reconnect failed after {MaxRetries} attempts");
                    IsPlaying = false;
                    Cleanup();
                }
                else
                {
                    // 继续重试
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(2000);
                        await AttemptReconnectAsync();
                    });
                }
            }
            finally
            {
                _isReconnecting = false;
            }
        }

        /// <summary>
        /// 发送命令到解码器进程
        /// </summary>
        /// <param name="command">命令对象</param>
        /// <returns>任务</returns>
        public async Task SendCommandAsync(DecoderCommand command)
        {
            try
            {
                if (_writer != null && _pipeClient?.IsConnected == true)
                {
                    string json = JsonSerializer.Serialize(command);
                    await _writer.WriteLineAsync(json);
                    _logger.Debug($"[IPC] Sent command: {command.Command}");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"[IPC] Error sending command: {ex.Message}");
            }
        }

        /// <summary>
        /// 停止播放（非异步版本）
        /// </summary>
        public async Task StopAsync()
        {
            await StopInternalAsync();
        }

        /// <summary>
        /// 停止播放并清理资源（异步版本）
        /// </summary>
        /// <returns>任务</returns>
        private async Task StopInternalAsync()
        {
            _isRunning = false;
            _playbackRequestedByBlazor = false;  // 重置播放请求标志，允许重新播放
            _logger.Debug("[Player] Stopping playback and cleaning up decoder...");

            // 发送退出命令到解码器
            if (_writer != null)
            {
                try
                {
                    await SendCommandAsync(new DecoderCommand { Command = "Quit" });
                    _logger.Debug("[Player] Quit command sent to decoder");
                }
                catch (Exception ex)
                {
                    _logger.Warning($"[Player] Failed to send Quit command: {ex.Message}");
                }
            }

            // 等待解码器进程正常退出（最多等待2秒）
            if (_decoderProcess != null && !_decoderProcess.HasExited)
            {
                try
                {
                    _logger.Debug("[Player] Waiting for decoder process to exit...");
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                    {
                        if (_decoderProcess != null)
                        {
                            await _decoderProcess.WaitForExitAsync(cts.Token);
                            if (_decoderProcess != null && _decoderProcess.HasExited)
                            {
                                _logger.Debug($"[Player] Decoder process exited normally with PID: {_decoderProcess.Id}");
                            }
                            else
                            {
                                _logger.Warning("[Player] Decoder process did not exit in time, forcing kill");
                                _decoderProcess.Kill();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"[Player] Error waiting for decoder exit: {ex.Message}");
                    try
                    {
                        if(_decoderProcess!=null)
                        _decoderProcess.Kill();
                    }
                    catch { }
                }
            }

            // 关闭并释放管道客户端
            if (_pipeClient != null)
            {
                try
                {
                    _pipeClient.Close();
                    _pipeClient.Dispose();
                    _logger.Debug("[Player] Pipe client closed");
                }
                catch { }
                _pipeClient = null;
            }

            // 释放读写器
            try
            {
                _reader?.Dispose();
                _writer?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.Warning($"[Player] Error disposing pipe reader/writer/_displayTimer: {ex.Message}");
            }
            _reader = null;
            _writer = null;

            // 释放解码器进程
            if (_decoderProcess != null)
            {
                try
                {
                    _decoderProcess.Dispose();
                    _logger.Debug("[Player] Decoder process disposed");
                }
                catch { }
                _decoderProcess = null;
            }

            // 清理共享内存
            if (_sharedMemory != null)
            {
                try
                {
                    _sharedMemory.Dispose();
                    _logger.Debug("[Player] Shared memory disposed");
                }
                catch { }
                _sharedMemory = null;
            }

            // 重置播放状态
            IsPlaying = false;
            IsPaused = false;
            Duration = TimeSpan.Zero;
            AudioTimestamp = TimeSpan.Zero;
            VideoTimestamp = TimeSpan.Zero;
            _logger.Debug("[Player] Playback stopped and resources cleaned up");
        }

        /// <summary>
        /// 暂停播放
        /// </summary>
        public void Pause()
        {
            _ = SendCommandAsync(new DecoderCommand { Command = "Pause" });
        }

        /// <summary>
        /// 恢复播放
        /// </summary>
        public void Resume()
        {
            _ = SendCommandAsync(new DecoderCommand { Command = "Resume" });
        }

        /// <summary>
        /// 设置音量
        /// </summary>
        /// <param name="volume">音量值（0-100）</param>
        public void SetVolume(int volume)
        {
            Volume = Math.Clamp(volume, 0, 100) / 100f;
            _ = SendCommandAsync(new DecoderCommand { Command = "SetVolume", Volume = volume });
        }

        /// <summary>
        /// 跳转到指定位置
        /// </summary>
        /// <param name="position">位置（秒）</param>
        public void Seek(int position)
        {
            _ = SendCommandAsync(new DecoderCommand { Command = "Seek", Position = position });
        }

        /// <summary>
        /// 下一个（未实现）
        /// </summary>
        public void Next()
        {
            _logger.Debug("[Player] Next not supported");
        }

        /// <summary>
        /// 上一个（未实现）
        /// </summary>
        public void Previous()
        {
            _logger.Debug("[Player] Previous not supported");
        }

        /// <summary>
        /// 切换全屏（由UI层处理）
        /// </summary>
        public void ToggleFullscreen()
        {
            _logger.Debug("[Player] Toggle fullscreen (handled by UI)");
        }

        /// <summary>
        /// 设置音频轨道（未实现）
        /// </summary>
        /// <param name="trackIndex">轨道索引</param>
        public void SetAudioTrack(int trackIndex)
        {
            _logger.Debug("[Player] SetAudioTrack not supported");
        }

        /// <summary>
        /// 设置字幕轨道（未实现）
        /// </summary>
        /// <param name="trackIndex">轨道索引</param>
        public void SetSpuTrack(int trackIndex)
        {
            _logger.Debug("[Player] SetSpuTrack not supported");
        }

        /// <summary>
        /// 获取音频轨道列表（进程隔离模式下暂不支持）
        /// </summary>
        public System.Collections.Generic.List<MovieAgent.FFmpegDecoder.AudioTrackInfo>? GetAudioTracks()
        {
            _logger.Debug("[Player] GetAudioTracks not supported in process-isolated mode");
            return null;
        }

        /// <summary>
        /// 获取字幕轨道列表（进程隔离模式下暂不支持）
        /// </summary>
        public System.Collections.Generic.List<MovieAgent.FFmpegDecoder.SubtitleTrackInfo>? GetSubtitleTracks()
        {
            _logger.Debug("[Player] GetSubtitleTracks not supported in process-isolated mode");
            return null;
        }

        public void TakeScreenshot()
        {
            _logger.Debug("[Player] TakeScreenshot not supported in isolated mode");
        }

        public void SetSubtitleDelay(double delayMs)
        {
            _logger.Debug($"[Player] SetSubtitleDelay: {delayMs}ms not supported in isolated mode");
        }

        public void SetPlaybackSpeed(double speed)
        {
            _logger.Debug($"[Player] SetPlaybackSpeed: {speed}x not supported in isolated mode");
        }

        /// <summary>
        /// 尝试初始化共享内存
        /// 使用共享内存可以提高视频帧传输效率
        /// </summary>
        /// <param name="width">视频宽度</param>
        /// <param name="height">视频高度</param>
        private void TryInitializeSharedMemory(int width, int height)
        {
            try
            {
                if (_sharedMemory == null)
                {
                    string baseName = _pipeName?.Replace("movieagent_ffmpeg_", "") ?? "default";
                    _sharedMemory = new SharedMemoryManager(baseName);

                    if (_sharedMemory.Open(width, height))
                    {
                        _logger.Debug($"[Player] Shared memory initialized: {width}x{height}");
                        StartFrameReadTask();
                    }
                    else
                    {
                        _sharedMemory.Dispose();
                        _sharedMemory = null;
                        _logger.Debug("[Player] Shared memory not available, falling back to pipe");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"[Player] Failed to initialize shared memory: {ex.Message}");
            }
        }

        /// <summary>
        /// 启动帧读取任务
        /// 从共享内存中读取视频帧
        /// </summary>
        private void StartFrameReadTask()
        {
            if (_frameReadTask != null && !_frameReadTask.IsCompleted)
                return;

            _frameReadTask = Task.Run(async () =>
            {
                while (_isRunning && _sharedMemory != null)
                {
                    try
                    {
                        if (_sharedMemory.ReadFrame(out byte[] frameData, out long timestamp, out long audioTimestamp, out long audioPlayPosition))
                        {
                            AudioTimestamp = TimeSpan.FromMilliseconds(audioTimestamp);
                            VideoTimestamp = TimeSpan.FromMilliseconds(timestamp);
                            AudioPlayPosition = audioPlayPosition;
                            Position = TimeSpan.FromMilliseconds(timestamp);
                            var frameDataObj = new FrameData
                            {
                                Width = 0,  // shared memory doesn't carry dimensions
                                Height = 0,
                                Data = frameData,
                                VideoTimestamp = timestamp,
                                AudioTimestamp = audioTimestamp,
                                AudioPlayPosition = audioPlayPosition
                            };
                            FrameUpdated?.Invoke(this, frameDataObj);
                        }
                        else
                        {
                            await Task.Delay(1);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"[Player] Error reading frame from shared memory: {ex.Message}");
                        break;
                    }
                }
            });
        }

        /// <summary>
        /// 清理资源
        /// </summary>
        private void Cleanup()
        {
            if (_disposed) return;

            _isRunning = false;

            try
            {
                // 释放读取器
                if (_reader != null)
                {
                    try { _reader.Dispose(); }
                    catch (ObjectDisposedException) { }
                    _reader = null;
                }

                // 释放写入器
                if (_writer != null)
                {
                    try { _writer.Dispose(); }
                    catch (ObjectDisposedException) { }
                    _writer = null;
                }

                // 释放管道客户端
                if (_pipeClient != null)
                {
                    try
                    {
                        if (_pipeClient.IsConnected)
                        {
                            _pipeClient.Close();
                        }
                        _pipeClient.Dispose();
                    }
                    catch (ObjectDisposedException) { }
                    catch (IOException) { }
                    _pipeClient = null;
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"[Player] Cleanup error: {ex.Message}");
            }

            // 释放解码器进程
            if (_decoderProcess != null)
            {
                try
                {
                    if (!_decoderProcess.HasExited)
                    {
                        try { _decoderProcess.Kill(); }
                        catch { }
                    }
                    _decoderProcess.Dispose();
                }
                catch { }
                _decoderProcess = null;
            }

            // 清理共享内存
            try
            {
                _sharedMemory?.Dispose();
                _sharedMemory = null;
            }
            catch { }
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                StopInternalAsync().GetAwaiter().GetResult();
                _disposed = true;
            }
        }

        public void SetD3dDevice(ID3D11Device device)
        {
            throw new NotImplementedException();
        }

        public void SetD3d11Device(ID3D11Device device)
        {
            throw new NotImplementedException();
        }

        public void SetD3d9Device(IDirect3DDevice9Ex device)
        {
            throw new NotImplementedException();
        }

        public void SetD3d12Device(ID3D12Device device)
        {
            throw new NotImplementedException();
        }

        public void SetD3d12CommandQueue(IntPtr commandQueuePtr)
        {
            throw new NotImplementedException();
        }

        #endregion

        #region 数据传输类

        /// <summary>
        /// 解码器命令类
        /// 用于向解码器进程发送命令
        /// </summary>
        public class DecoderCommand
        {
            /// <summary>
            /// 命令类型（Play/Stop/Pause/Resume/Quit/Seek/SetVolume等）
            /// </summary>
            public string Command { get; set; } = string.Empty;

            /// <summary>
            /// 文件路径（用于Play命令）
            /// </summary>
            public string? FilePath { get; set; }

            /// <summary>
            /// 位置（用于Seek命令，单位：秒）
            /// </summary>
            public double Position { get; set; }

            /// <summary>
            /// 音量（用于SetVolume命令，0-100）
            /// </summary>
            public int Volume { get; set; }

            /// <summary>
            /// 音频轨道索引（用于SetAudioTrack命令）
            /// </summary>
            public int AudioTrack { get; set; }

            /// <summary>
            /// 字幕轨道索引（用于SetSubtitleTrack命令）
            /// </summary>
            public int SubtitleTrack { get; set; }

            /// <summary>
            /// 解码模式（Auto/Hardware/Software）
            /// </summary>
            public string? DecodeMode { get; set; }

            /// <summary>
            /// 播放速度倍率（用于SetSpeed命令）
            /// </summary>
            public double Speed { get; set; } = 1.0;

            /// <summary>
            /// 截图保存路径（用于Screenshot命令）
            /// </summary>
            public string? ScreenshotPath { get; set; }

            /// <summary>
            /// 字幕延迟（毫秒，用于SetSubtitleDelay命令）
            /// </summary>
            public double SubtitleDelay { get; set; }
        }

        /// <summary>
        /// 帧消息类
        /// 用于解码器向主进程发送视频帧数据
        /// </summary>
        public class FrameMessage
        {
            /// <summary>
            /// 消息类型
            /// </summary>
            public string Type { get; set; } = "Frame";

            /// <summary>
            /// 帧宽度
            /// </summary>
            public int Width { get; set; }

            /// <summary>
            /// 帧高度
            /// </summary>
            public int Height { get; set; }

            /// <summary>
            /// 视频时间戳（毫秒）
            /// </summary>
            public long VideoTimestamp { get; set; }

            /// <summary>
            /// 音频时间戳（毫秒）
            /// </summary>
            public long AudioTimestamp { get; set; }

            /// <summary>
            /// 音频播放位置（字节）
            /// </summary>
            public long AudioPlayPosition { get; set; }

            /// <summary>
            /// Base64编码的帧数据
            /// </summary>
            public string DataBase64 { get; set; } = string.Empty;
        }

        /// <summary>
        /// 状态消息类
        /// 用于解码器向主进程发送播放状态
        /// </summary>
        public class StatusMessage
        {
            /// <summary>
            /// 消息类型
            /// </summary>
            public string Type { get; set; } = "Status";

            /// <summary>
            /// 是否正在播放
            /// </summary>
            public bool IsPlaying { get; set; }

            /// <summary>
            /// 是否处于暂停状态
            /// </summary>
            public bool IsPaused { get; set; }

            /// <summary>
            /// 视频总时长（毫秒）
            /// </summary>
            public long DurationMs { get; set; }

            /// <summary>
            /// 当前播放位置（毫秒）
            /// </summary>
            public long PositionMs { get; set; }
        }

        /// <summary>
        /// 信息消息类
        /// 用于解码器向主进程发送视频信息
        /// </summary>
        public class InfoMessage
        {
            /// <summary>
            /// 消息类型
            /// </summary>
            public string Type { get; set; } = "Info";

            /// <summary>
            /// 视频宽度
            /// </summary>
            public int VideoWidth { get; set; }

            /// <summary>
            /// 视频高度
            /// </summary>
            public int VideoHeight { get; set; }

            /// <summary>
            /// 帧率
            /// </summary>
            public double Fps { get; set; }

            /// <summary>
            /// 视频时长（毫秒）
            /// </summary>
            public long DurationMs { get; set; }

            /// <summary>
            /// 解码器名称
            /// </summary>
            public string? DecoderName { get; set; }

            /// <summary>
            /// 解码模式
            /// </summary>
            public string? DecodeMode { get; set; }
        }

        /// <summary>
        /// 错误消息类
        /// 用于解码器向主进程发送错误信息
        /// </summary>
        public class ErrorMessage
        {
            /// <summary>
            /// 消息类型
            /// </summary>
            public string Type { get; set; } = "Error";

            /// <summary>
            /// 错误消息
            /// </summary>
            public string Message { get; set; } = string.Empty;
        }

        /// <summary>
        /// 性能警告消息类
        /// 用于解码器向主进程发送性能警告
        /// </summary>
        public class DecodePerformanceWarningMessage
        {
            /// <summary>
            /// 消息类型
            /// </summary>
            public string Type { get; set; } = "PerformanceWarning";

            /// <summary>
            /// 警告消息
            /// </summary>
            public string Message { get; set; } = string.Empty;

            /// <summary>
            /// 当前分辨率宽度
            /// </summary>
            public int CurrentWidth { get; set; }

            /// <summary>
            /// 当前分辨率高度
            /// </summary>
            public int CurrentHeight { get; set; }

            /// <summary>
            /// 建议的分辨率宽度
            /// </summary>
            public int SuggestedWidth { get; set; }

            /// <summary>
            /// 建议的分辨率高度
            /// </summary>
            public int SuggestedHeight { get; set; }
        }

        /// <summary>
        /// 音频轨道消息类
        /// 用于解码器向主进程发送音频轨道信息
        /// </summary>
        public class AudioTracksMessage
        {
            /// <summary>
            /// 消息类型
            /// </summary>
            public string Type { get; set; } = "AudioTracks";

            /// <summary>
            /// 轨道数量
            /// </summary>
            public int TrackCount { get; set; }

            /// <summary>
            /// 轨道列表
            /// </summary>
            public List<AudioTrackInfo>? Tracks { get; set; }
            /// <summary>
            /// 当前选中轨道索引
            /// </summary>

            public int CurrentTrack { get; set; }
        }

        /// <summary>
        /// 字幕轨道消息类
        /// 用于解码器向主进程发送字幕轨道信息
        /// </summary>
        public class SubtitleTracksMessage
        {
            /// <summary>
            /// 消息类型
            /// </summary>
            public string Type { get; set; } = "SubtitleTracks";

            /// <summary>
            /// 轨道数量
            /// </summary>
            public int TrackCount { get; set; }

            /// <summary>
            /// 轨道列表
            /// </summary>
            public List<SubtitleTrackInfo>? Tracks { get; set; }
            /// <summary>
            /// 当前选中轨道索引
            /// </summary>
            public int CurrentTrack { get; set; }
        }

        /// <summary>
        /// 音频轨道信息类
        /// </summary>
        public class AudioTrackInfo
        {
            /// <summary>
            /// 轨道索引
            /// </summary>
            public int Index { get; set; }

            /// <summary>
            /// 语言
            /// </summary>
            public string Language { get; set; } = string.Empty;

            /// <summary>
            /// 编码格式
            /// </summary>
            public string Codec { get; set; } = string.Empty;

            /// <summary>
            /// 声道数
            /// </summary>
            public int Channels { get; set; }

            /// <summary>
            /// 描述信息
            /// </summary>
            public string Description { get; set; } = string.Empty;
        }

        /// <summary>
        /// 字幕轨道信息类
        /// </summary>
        public class SubtitleTrackInfo
        {
            /// <summary>
            /// 轨道索引
            /// </summary>
            public int Index { get; set; }

            /// <summary>
            /// 语言
            /// </summary>
            public string Language { get; set; } = string.Empty;

            /// <summary>
            /// 编码格式
            /// </summary>
            public string Codec { get; set; } = string.Empty;

            /// <summary>
            /// 是否为强制字幕
            /// </summary>
            public bool IsForced { get; set; }

            /// <summary>
            /// 描述信息
            /// </summary>
            public string Description { get; set; } = string.Empty;
        }

        /// <summary>
        /// 截图结果消息类
        /// </summary>
        public class ScreenshotResultMessage
        {
            public string Type { get; set; } = "ScreenshotResult";
            public string? FilePath { get; set; }
            public bool Success { get; set; }
        }

        /// <summary>
        /// 字幕延迟消息类
        /// </summary>
        public class SubtitleDelayMessage
        {
            public string Type { get; set; } = "SubtitleDelay";
            public double DelayMs { get; set; }
        }

        /// <summary>
        /// 分辨率降级消息类
        /// </summary>
        public class ResolutionDownscaleMessage
        {
            public string Type { get; set; } = "ResolutionDownscale";
            public string Message { get; set; } = string.Empty;
            public int OriginalWidth { get; set; }
            public int OriginalHeight { get; set; }
            public int TargetWidth { get; set; }
            public int TargetHeight { get; set; }
            public string Reason { get; set; } = string.Empty;
        }

        /// <summary>
        /// 字幕解码消息类
        /// </summary>
        public class SubtitleDecodedMessage
        {
            public string Type { get; set; } = "SubtitleDecoded";
            public string Text { get; set; } = string.Empty;
            public double StartTime { get; set; }
            public double EndTime { get; set; }
        }

        #endregion
    }
}