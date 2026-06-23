using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;
using System.Text.Json;

namespace MovieAgent.FFmpegDecoder
{
    public class DecoderIpcServer
    {
        private readonly string _pipeName;
        private readonly FFmpegDecoderEngine _decoder;
        private NamedPipeServerStream? _pipeServer;
        private StreamReader? _reader;
        private StreamWriter? _writer;
        private bool _isRunning;
        private SharedMemoryManager? _sharedMemory;
 
        public DecoderIpcServer(string pipeName, FFmpegDecoderEngine decoder)
        {
            _pipeName = pipeName;
            _decoder = decoder;
            _decoder.FrameDecoded += OnFrameDecoded;
            _decoder.PlaybackEnded += OnPlaybackEnded;
            _decoder.PlaybackError += OnPlaybackError;
            _decoder.PerformanceWarning += OnPerformanceWarning;
            _decoder.ResolutionDownscale += OnResolutionDownscale;
            _decoder.SubtitleDecoded += OnSubtitleDecoded;
        }

        public async Task RunAsync()
        {
            _isRunning = true;

            while (_isRunning)
            {
                try
                {
                    using (_pipeServer = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous))
                    {
                        DebugLogger.WriteLine($"[IPC] Waiting for client connection on pipe: {_pipeName}");
                        await _pipeServer.WaitForConnectionAsync();

                        DebugLogger.WriteLine("[IPC] Client connected");

                        _reader = new StreamReader(_pipeServer);
                        _writer = new StreamWriter(_pipeServer) { AutoFlush = true };

                        await ProcessMessagesAsync();
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.WriteLine($"[IPC] Server error: {ex.Message}");
                    if (!_isRunning)
                        break;
                }
            }
        }

        private void TryCreateSharedMemory(int width, int height)
        {
            try
            {
                if (_sharedMemory == null && width > 0 && height > 0)
                {
                    string baseName = _pipeName.Replace("movieagent_ffmpeg_", "");
                    _sharedMemory = new SharedMemoryManager(baseName);
                    
                    if (_sharedMemory.Create(width, height))
                    {
                        DebugLogger.WriteLine($"[SharedMemory] Created shared memory for frames: {width}x{height}");
                    }
                    else
                    {
                        _sharedMemory.Dispose();
                        _sharedMemory = null;
                        DebugLogger.WriteLine("[SharedMemory] Failed to create shared memory, using pipe");
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[SharedMemory] Error creating shared memory: {ex.Message}");
            }
        }

        private async Task ProcessMessagesAsync()
        {
            while (_isRunning && _pipeServer?.IsConnected == true)
            {
                try
                {
                    string? message = await _reader?.ReadLineAsync();
                    if (message == null)
                    {
                        DebugLogger.WriteLine("[IPC] Client disconnected");
                        break;
                    }

                    await HandleMessageAsync(message);
                }
                catch (IOException)
                {
                    DebugLogger.WriteLine("[IPC] Client connection closed");
                    break;
                }
                catch (Exception ex)
                {
                    DebugLogger.WriteLine($"[IPC] Error processing message: {ex.Message}");
                }
            }
        }

        private async Task HandleMessageAsync(string message)
        {
            try
            {
                var command = JsonSerializer.Deserialize<DecoderCommand>(message);
                if (command == null)
                    return;

                DebugLogger.WriteLine($"[IPC] Received command: {command.Command}");

                switch (command.Command)
                {
                    case "Play":
                        
                        if (!string.IsNullOrEmpty(command.DecodeMode))
                        {
                            if (Enum.TryParse<FFmpegDecoderEngine.DecodeMode>(command.DecodeMode, out var mode))
                            {
                                _decoder.SetDecodeMode(mode);
                            }
                        }
                        await _decoder.PlayAsync(command.FilePath ?? string.Empty);
                        
                        // 先创建共享内存，再发送Info消息，确保主进程能立即打开
                        TryCreateSharedMemory(_decoder.VideoWidth, _decoder.VideoHeight);
                        
                        await SendInfoAsync();
                        await SendStatusAsync();
                        break;

                    case "Stop":
                        await _decoder.StopAsync();
                        await SendStatusAsync();
                        break;

                    case "Pause":
                        _decoder.Pause();
                        await SendStatusAsync();
                        break;

                    case "Resume":
                        _decoder.Resume();
                        await SendStatusAsync();
                        break;

                    case "Seek":
                        _decoder.Seek(command.Position);
                        await SendStatusAsync();
                        break;

                    case "SetVolume":
                        _decoder.SetVolume(command.Volume);
                        break;

                    case "GetStatus":
                        await SendStatusAsync();
                        break;

                    case "GetInfo":
                        await SendInfoAsync();
                        break;

                    case "GetAudioTracks":
                        await SendAudioTracksAsync();
                        break;

                    case "GetSubtitleTracks":
                        await SendSubtitleTracksAsync();
                        break;

                    case "SetAudioTrack":
                        _decoder.SetAudioTrack(command.AudioTrack);
                        break;

                    case "SetSubtitleTrack":
                        _decoder.SetSubtitleTrack(command.SubtitleTrack);
                        break;

                    case "SetSpeed":
                        _decoder.SetSpeed(command.Speed);
                        break;

                    case "Screenshot":
                        string? screenshotPath = _decoder.SaveScreenshot(command.ScreenshotPath);
                        await SendScreenshotResultAsync(screenshotPath);
                        break;

                    case "GetSubtitleDelay":
                        await SendSubtitleDelayAsync();
                        break;

                    case "SetSubtitleDelay":
                        _decoder.SetSubtitleDelay(command.SubtitleDelay);
                        break;

                    case "Quit":
                        _isRunning = false;
                        break;
                }
            }
            catch (Exception ex)
            {
                await SendErrorAsync(ex.Message);
            }
        }
        /// <summary>
        /// 接收解码后的帧数据，并通过共享内存或管道发送给主进程
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="frame"></param>
        private void OnFrameDecoded(object? sender, FrameData frame)
        {
            try
            {
                if (_sharedMemory != null)
                {
                    bool success = _sharedMemory.WriteFrame(frame.Data, frame.VideoTimestamp,frame.AudioTimestamp,frame.AudioPlayPosition);
                    if (success)
                    {
                        // 4. 减少日志
                        if (_decoder.FrameCount % 100 == 0)
                        {
                            DebugLogger.WriteLine($"[SharedMemory]Write  Frame sent: {frame.Width}x{frame.Height}, VideoTimestamp:{frame.VideoTimestamp} ms, AudioTimestamp:{frame.AudioTimestamp} ms, AudioPlayPosition:{frame.AudioPlayPosition} ms"); 
                        }
                    }
                }
                else if (_writer != null && _pipeServer?.IsConnected == true)
                {
                    var frameMessage = new FrameMessage
                    {
                        Type = "Frame",
                        Width = frame.Width,
                        Height = frame.Height,
                        VideoTimestamp = frame.VideoTimestamp,
                        AudioTimestamp = frame.AudioTimestamp,
                        AudioPlayPosition = frame.AudioPlayPosition, 
                        DataBase64 = Convert.ToBase64String(frame.Data)
                    };

                    string json = JsonSerializer.Serialize(frameMessage);
                    _writer.WriteLine(json);
                    DebugLogger.WriteLine($"[IPC] Frame sent: {frame.Width}x{frame.Height}, VideoTimestamp:{frame.VideoTimestamp} ms, AudioTimestamp:{frame.AudioTimestamp} ms, AudioPlayPosition:{frame.AudioPlayPosition} ms");
                }
                else
                {
                    DebugLogger.WriteLine($"[IPC] Cannot send frame - writer: {_writer != null}, connected: {_pipeServer?.IsConnected}, sharedMemory: {_sharedMemory != null}");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[IPC] Error sending frame: {ex.Message}");
            }
        }

        private void OnPlaybackEnded(object? sender, EventArgs e)
        {
            try
            {
                if (_writer != null && _pipeServer?.IsConnected == true)
                {
                    var endMessage = new
                    {
                        Type = "PlaybackEnded"
                    };

                    string json = JsonSerializer.Serialize(endMessage);
                    _writer.WriteLine(json);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[IPC] Error sending end message: {ex.Message}");
            }
        }

        private void OnPlaybackError(object? sender, string errorMessage)
        {
            try
            {
                if (_writer != null && _pipeServer?.IsConnected == true)
                {
                    var errorMsg = new ErrorMessage
                    {
                        Type = "Error",
                        Message = errorMessage
                    };

                    string json = JsonSerializer.Serialize(errorMsg);
                    _writer.WriteLine(json);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[IPC] Error sending error message: {ex.Message}");
            }
        }

        private async Task SendStatusAsync()
        {
            try
            {
                var status = new StatusMessage
                {
                    Type = "Status",
                    IsPlaying = _decoder.IsPlaying,
                    IsPaused = _decoder.IsPaused,
                    DurationMs = _decoder.DurationMs,
                    PositionMs = _decoder.CurrentTimeMs,
                 };

                string json = JsonSerializer.Serialize(status); 
                await _writer?.WriteLineAsync(json);
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[IPC] Error sending status: {ex.Message}");
            }
        }

        private async Task SendInfoAsync()
        {
            try
            {
                var info = new InfoMessage
                {
                    Type = "Info",
                    VideoWidth = _decoder.VideoWidth,
                    VideoHeight = _decoder.VideoHeight,
                    Fps = _decoder.Fps,
                    DurationMs = _decoder.DurationMs,
                    DecoderName = _decoder.CurrentDecoder,
                    DecodeMode = _decoder.CurrentDecodeMode.ToString()
                };

                string json = JsonSerializer.Serialize(info);
                await _writer?.WriteLineAsync(json);
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[IPC] Error sending info: {ex.Message}");
            }
        }

        private async Task SendAudioTracksAsync()
        {
            try
            {
                var audioTracks = _decoder.GetAudioTracks();
                var response = new
                {
                    Type = "AudioTracks",
                    Tracks = audioTracks,
                    CurrentTrack = _decoder.CurrentAudioTrack,
                    TrackCount = audioTracks.Count
                };

                string json = JsonSerializer.Serialize(response);
                await _writer?.WriteLineAsync(json);
                DebugLogger.WriteLine($"[IPC] Audio tracks info sent: {audioTracks.Count} tracks");
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[IPC] Error sending audio tracks: {ex.Message}");
            }
        }

        private async Task SendSubtitleTracksAsync()
        {
            try
            {
                var subtitleTracks = _decoder.GetSubtitleTracks();
                var response = new
                {
                    Type = "SubtitleTracks",
                    Tracks = subtitleTracks,
                    CurrentTrack = _decoder.CurrentSubtitleTrack,
                    TrackCount = subtitleTracks.Count
                };

                string json = JsonSerializer.Serialize(response);
                await _writer?.WriteLineAsync(json);
                DebugLogger.WriteLine($"[IPC] Subtitle tracks info sent: {subtitleTracks.Count} tracks");
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[IPC] Error sending subtitle tracks: {ex.Message}");
            }
        }

        private async Task SendErrorAsync(string message)
        {
            try
            {
                var error = new ErrorMessage
                {
                    Type = "Error",
                    Message = message
                };

                string json = JsonSerializer.Serialize(error);
                await _writer?.WriteLineAsync(json);
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[IPC] Error sending error: {ex.Message}");
            }
        }

        private void OnPerformanceWarning(object? sender, DecodePerformanceWarning warning)
        {
            try
            {
                if (_writer != null && _pipeServer?.IsConnected == true)
                {
                    var warningMessage = new
                    {
                        Type = "PerformanceWarning",
                        warning.Message,
                        warning.CurrentWidth,
                        warning.CurrentHeight,
                        warning.SuggestedWidth,
                        warning.SuggestedHeight,
                        warning.AverageDecodeTimeMs,
                        warning.TargetFps
                    };

                    string json = JsonSerializer.Serialize(warningMessage);
                    _writer.WriteLine(json);
                    DebugLogger.WriteLine($"[IPC] Performance warning sent: {warning.Message}");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[IPC] Error sending performance warning: {ex.Message}");
            }
        }

        private void OnResolutionDownscale(object? sender, ResolutionDownscaleInfo info)
        {
            try
            {
                if (_writer != null && _pipeServer?.IsConnected == true)
                {
                    var downscaleMessage = new
                    {
                        Type = "ResolutionDownscale",
                        info.Message,
                        info.OriginalWidth,
                        info.OriginalHeight,
                        info.TargetWidth,
                        info.TargetHeight,
                        info.Reason
                    };

                    string json = JsonSerializer.Serialize(downscaleMessage);
                    _writer.WriteLine(json);
                    DebugLogger.WriteLine($"[IPC] Resolution downscale sent: {info.Message}");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[IPC] Error sending resolution downscale: {ex.Message}");
            }
        }

        private void OnSubtitleDecoded(object? sender, SubtitleData subtitle)
        {
            try
            {
                if (_writer != null && _pipeServer?.IsConnected == true)
                {
                    var subtitleMessage = new
                    {
                        Type = "SubtitleDecoded",
                        subtitle.Text,
                        subtitle.StartTime,
                        subtitle.EndTime
                    };

                    string json = JsonSerializer.Serialize(subtitleMessage);
                    _writer.WriteLine(json);
                    DebugLogger.WriteLine($"[IPC] Subtitle decoded sent: {subtitle.Text}");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[IPC] Error sending subtitle decoded: {ex.Message}");
            }
        }

        private async Task SendScreenshotResultAsync(string? filePath)
        {
            try
            {
                var result = new ScreenshotResultMessage
                {
                    Type = "ScreenshotResult",
                    FilePath = filePath,
                    Success = !string.IsNullOrEmpty(filePath)
                };

                string json = JsonSerializer.Serialize(result);
                if (_writer != null)
                    await _writer.WriteLineAsync(json);
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[IPC] Error sending screenshot result: {ex.Message}");
            }
        }

        private async Task SendSubtitleDelayAsync()
        {
            try
            {
                var msg = new SubtitleDelayMessage
                {
                    Type = "SubtitleDelay",
                    DelayMs = _decoder.SubtitleDelayMs
                };

                string json = JsonSerializer.Serialize(msg);
                if (_writer != null)
                    await _writer.WriteLineAsync(json);
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[IPC] Error sending subtitle delay: {ex.Message}");
            }
        }
    }

    public class DecoderCommand
    {
        public string Command { get; set; } = string.Empty;
        public string? FilePath { get; set; }
        public double Position { get; set; }
        public int Volume { get; set; }
        public int AudioTrack { get; set; }
        public int SubtitleTrack { get; set; }
        public string? DecodeMode { get; set; }
        public double Speed { get; set; } = 1.0;
        public string? ScreenshotPath { get; set; }
        public double SubtitleDelay { get; set; }
    }

    public class FrameMessage
    {
        public string Type { get; set; } = "Frame";
        public int Width { get; set; }
        public int Height { get; set; }
        public long VideoTimestamp { get; set; }
        public long AudioTimestamp { get; set; }
        public long AudioPlayPosition { get; set; }

        public string DataBase64 { get; set; } = string.Empty;
    }

    public class StatusMessage
    {
        public string Type { get; set; } = "Status";
        public bool IsPlaying { get; set; }
        public bool IsPaused { get; set; }
        public long DurationMs { get; set; }
        public long PositionMs { get; set; }
    }

    public class InfoMessage
    {
        public string Type { get; set; } = "Info";
        public int VideoWidth { get; set; }
        public int VideoHeight { get; set; }
        public double Fps { get; set; }
        public long DurationMs { get; set; }
        public string? DecoderName { get; set; }
        public string? DecodeMode { get; set; }
    }

    public class ErrorMessage
    {
        public string Type { get; set; } = "Error";
        public string Message { get; set; } = string.Empty;
    }

    public class ScreenshotResultMessage
    {
        public string Type { get; set; } = "ScreenshotResult";
        public string? FilePath { get; set; }
        public bool Success { get; set; }
    }

    public class SubtitleDelayMessage
    {
        public string Type { get; set; } = "SubtitleDelay";
        public double DelayMs { get; set; }
    }
}