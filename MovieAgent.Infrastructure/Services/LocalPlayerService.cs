 using MovieAgent.Core.Interfaces;
using MovieAgent.FFmpegDecoder;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Vortice.Direct3D11;
using Vortice.Direct3D12;
using Vortice.Direct3D9;
using static MovieAgent.FFmpegDecoder.FFmpegDecoderEngine;
 
namespace MovieAgent.Infrastructure.Services
{
    /// <summary>
    /// 本地播放器服务
    /// 直接在主进程中使用FFmpeg解码，取消进程隔离和内存共享，减少内存开销
    /// </summary>
    public class LocalPlayerService : IPlayerService, IDisposable
    {
        #region 字段

        private readonly ILoggerService _logger;
        private FFmpegDecoderEngine? _decoder;
        private ID3D11Device? _d3d11Device;
        private IDirect3DDevice9Ex? _d3d9Device;
        private bool _disposed;
        private string? _currentRequestedFilePath;
        private bool _playbackRequestedByBlazor;
        private ID3D12Device? _d3d12Device;

        #endregion

        #region 属性

        public bool IsPlaying => _decoder?.IsPlaying ?? false;
        public D3DMode? CurrentD3dModel => _decoder?.CurrentD3dModel;

        public bool IsPaused => _decoder?.IsPaused ?? false;

        public TimeSpan Duration => TimeSpan.FromMilliseconds(_decoder?.DurationMs ?? 0);

        public TimeSpan AudioTimestamp => TimeSpan.FromMilliseconds(_decoder?.AudioPlayPosition ?? 0);

        public TimeSpan Position => TimeSpan.FromMilliseconds(_decoder?.CurrentTimeMs ?? 0);

        public long AudioPlayPosition => _decoder?.AudioPlayPosition ?? 0;

        public TimeSpan VideoTimestamp => TimeSpan.FromMilliseconds(_decoder?.CurrentTimeMs ?? 0);

        public float Volume => (_decoder?.Volume ?? 100) / 100f;

        public int VideoWidth => _decoder?.VideoWidth ?? 0;

        public int VideoHeight => _decoder?.VideoHeight ?? 0;

        public int AudioTrackCount => _decoder?.GetAudioTracks()?.Count ?? 0;

        public int CurrentAudioTrack => _decoder?.CurrentAudioTrack ?? -1;

        public int SpuTrackCount => _decoder?.GetSubtitleTracks()?.Count ?? 0;

        public int CurrentSpuTrack => _decoder?.CurrentSubtitleTrack ?? -1;

        /// <summary>当前帧率</summary>
        public double Fps => _decoder?.Fps ?? 0;

        /// <summary>获取音频轨道列表</summary>
        public List<AudioTrackInfo>? GetAudioTracks() => _decoder?.GetAudioTracks();

        /// <summary>获取字幕轨道列表</summary>
        public List<SubtitleTrackInfo>? GetSubtitleTracks() => _decoder?.GetSubtitleTracks();

        /// <summary>当前解码器名称</summary>
        public string? CurrentDecoder => _decoder?.CurrentDecoder;
        public ID3D11Device? D3dDevice => _d3d11Device;

 
        public void SetD3d11Device(Vortice.Direct3D11.ID3D11Device d3d11Device)
        {
            _d3d11Device=d3d11Device;
        }
        public void SetD3d9Device(IDirect3DDevice9Ex d3d9Device)
        {
            _d3d9Device = d3d9Device;
        }
        public void SetD3d12Device(ID3D12Device d3d12Device)
        {
            _d3d12Device = d3d12Device;
        }
        #endregion

        #region 事件

        public event EventHandler<FrameData>? FrameUpdated;

        public event EventHandler? PlaybackEnded;

        public event EventHandler? PlaybackRequestedByBlazor;

        public event EventHandler<string>? PlaybackFailed;

        public event EventHandler<DecodePerformanceWarning>? PerformanceWarning;

        public event EventHandler<ResolutionDownscaleInfo>? ResolutionDownscale;

        public event EventHandler<SubtitleData>? SubtitleDecoded;

        #endregion

        #region 构造函数

        public LocalPlayerService(ILoggerService logger)
        {
            _logger = logger; 
        }

        #endregion

        #region 方法

        public void RequestPlayback(string filePath)
        {
            _currentRequestedFilePath = filePath;
            _playbackRequestedByBlazor = true;
            PlaybackRequestedByBlazor?.Invoke(this, EventArgs.Empty);
        }

        public string? GetCurrentRequestedFilePath()
        {
            return _currentRequestedFilePath;
        }

        public async Task PlayAsync(string filePath)
        {
            try
            {
                _logger.Debug($"[LocalPlayer] ===== PlayAsync 开始 ===== ");
                _logger.Debug($"[LocalPlayer] 文件路径: {filePath}");
                
                // 停止之前的播放
                if (_decoder != null)
                {
                    _logger.Debug("[LocalPlayer] 停止之前的播放...");
                    await _decoder.StopAsync();
                    _decoder.Dispose();
                    _decoder = null;
                }

                // 创建新的解码器实例
                _logger.Debug("[LocalPlayer] 创建 FFmpegDecoderEngine 实例...");
                try
                {
                    _decoder = new FFmpegDecoderEngine(DecodeMode.Auto,D3DMode.D3D12);
                     _logger.Debug("[LocalPlayer] FFmpegDecoderEngine 实例创建成功");
                    if (_decoder.CurrentD3dModel == D3DMode.D3D9)
                    {
                        _decoder.SetD3d9dDevice(_d3d9Device);
                    }
                    else if (_decoder.CurrentD3dModel == D3DMode.D3D11)
                    {

                        _decoder.SetD311dDevice(_d3d11Device);
                    } else if (_decoder.CurrentD3dModel == D3DMode.D3D12)
                    { 
                        _decoder.SetD3d12dDevice(_d3d12Device); 
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "[LocalPlayer] 创建 FFmpegDecoderEngine 失败");
                    PlaybackFailed?.Invoke(this, $"解码器初始化失败: {ex.Message}");
                    return;
                }
                
                // 订阅解码器事件
                _logger.Debug("[LocalPlayer] 订阅解码器事件...");
                _decoder.FrameDecoded += OnFrameDecoded;
                _decoder.PlaybackEnded += OnPlaybackEnded;
                _decoder.PlaybackError += OnPlaybackError;
                _decoder.PerformanceWarning += OnPerformanceWarning;
                _decoder.ResolutionDownscale += OnResolutionDownscale;
                _decoder.SubtitleDecoded += OnSubtitleDecoded;
                _logger.Debug("[LocalPlayer] 解码器事件订阅完成");

                _logger.Debug("[LocalPlayer] 调用解码器 PlayAsync...");
                try
                {
                    await _decoder.PlayAsync(filePath);
                    _logger.Debug("[LocalPlayer] 解码器 PlayAsync 完成");
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "[LocalPlayer] 解码器 PlayAsync 失败");
                    PlaybackFailed?.Invoke(this, $"播放失败: {ex.Message}");
                    return;
                }
                
                _logger.Debug($"[LocalPlayer] ===== PlayAsync 结束 ===== ");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[LocalPlayer] Playback failed");
                PlaybackFailed?.Invoke(this, ex.Message);
            }
        }

        public Task StopAsync()
        {
            try
            {
                _logger.Debug("[LocalPlayer] Stopping playback");
                return _decoder?.StopAsync() ?? Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[LocalPlayer] Stop failed");
                return Task.CompletedTask;
            }
        }

        public void Pause()
        {
            try
            {
                _decoder?.Pause();
                _logger.Debug("[LocalPlayer] Paused");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[LocalPlayer] Pause failed");
            }
        }

        public void Resume()
        {
            try
            {
                _decoder?.Resume();
                _logger.Debug("[LocalPlayer] Resumed");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[LocalPlayer] Resume failed");
            }
        }

        public void SetVolume(int volume)
        {
            try
            {
                _decoder?.SetVolume(volume);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[LocalPlayer] SetVolume failed");
            }
        }

        public void Seek(int position)
        {
            try
            {
                _decoder?.Seek(position);
                _logger.Debug($"[LocalPlayer] Seek to {position}s");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[LocalPlayer] Seek failed");
            }
        }

        public void Next()
        {
            // 单文件播放，Next功能留空
        }

        public void Previous()
        {
            // 单文件播放，Previous功能留空
        }

        public void ToggleFullscreen()
        {
            // 全屏切换由UI层处理
           
        }

        public void SetAudioTrack(int trackIndex)
        {
            try
            {
                _decoder?.SetAudioTrack(trackIndex);
                 _logger.Debug($"[LocalPlayer] Audio track set to {trackIndex}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[LocalPlayer] SetAudioTrack failed");
            }
        }

        public void SetSpuTrack(int trackIndex)
        {
            try
            {
                _decoder?.SetSubtitleTrack(trackIndex);
                _logger.Debug($"[LocalPlayer] Subtitle track set to {trackIndex}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[LocalPlayer] SetSpuTrack failed");
            }
        }

        public void TakeScreenshot()
        {
            try
            {
                _decoder?.TakeScreenshot();
                _logger.Debug("[LocalPlayer] Screenshot taken");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[LocalPlayer] TakeScreenshot failed");
            }
        }

        public void SetSubtitleDelay(double delayMs)
        {
            try
            {
                _decoder?.SetSubtitleDelay(delayMs);
                _logger.Debug($"[LocalPlayer] Subtitle delay set to {delayMs}ms");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[LocalPlayer] SetSubtitleDelay failed");
            }
        }

        public void SetPlaybackSpeed(double speed)
        {
            try
            {
                _decoder?.SetSpeed(speed);
                _logger.Debug($"[LocalPlayer] Playback speed set to {speed}x");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[LocalPlayer] SetPlaybackSpeed failed");
            }
        }

        #endregion

        #region 事件处理

        private void OnFrameDecoded(object? sender, FrameData frame)
        {
            try
            {
                //_logger.Debug($"[LocalPlayer] OnFrameDecoded: IsHardwareFrame={frame.IsHardwareFrame}, NV12Ptr=0x{frame.NV12TexturePtr:X8}");
                FrameUpdated?.Invoke(this, frame);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[LocalPlayer] Error processing frame");
            }
        }

        private void OnPlaybackEnded(object? sender, EventArgs e)
        {
            _logger.Debug("[LocalPlayer] Playback ended");
            PlaybackEnded?.Invoke(this, EventArgs.Empty);
        }

        private void OnPlaybackError(object? sender, string errorMessage)
        {
            _logger.Error($"[LocalPlayer] Playback error: {errorMessage}");
            PlaybackFailed?.Invoke(this, errorMessage);
        }

        private void OnPerformanceWarning(object? sender, DecodePerformanceWarning warning)
        {
            _logger.Warning($"[LocalPlayer] Performance warning: {warning.Message}");
            PerformanceWarning?.Invoke(this, warning);
        }

        private void OnResolutionDownscale(object? sender, ResolutionDownscaleInfo info)
        {
            _logger.Information($"[LocalPlayer] Resolution downscale: {info.OriginalWidth}x{info.OriginalHeight} -> {info.TargetWidth}x{info.TargetHeight}");
            ResolutionDownscale?.Invoke(this, info);
        }

        private void OnSubtitleDecoded(object? sender, SubtitleData subtitle)
        {
            _logger.Debug($"[LocalPlayer] Subtitle decoded: {subtitle.Text}");
            SubtitleDecoded?.Invoke(this, subtitle);
        }

        #endregion

        #region 资源释放

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                _decoder?.Dispose();
                _decoder = null;
            }

            _disposed = true;
        }

        #endregion

        #region BDMV/ISO 蓝光支持

        /// <summary>
        /// 检测路径是否为 BDMV 蓝光结构
        /// </summary>
        public static bool IsBdmvStructure(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            
            string bdmvDir = Path.Combine(path, "BDMV");
            if (Directory.Exists(bdmvDir))
            {
                return File.Exists(Path.Combine(bdmvDir, "index.bdmv")) ||
                       File.Exists(Path.Combine(bdmvDir, "MovieObject.bdmv"));
            }
            return false;
        }

        /// <summary>
        /// 获取 BDMV 蓝光光盘中的标题列表
        /// </summary>
        public static List<BdmvTitleInfo> GetBdmvTitles(string bdmvRootPath)
        {
            var titles = new List<BdmvTitleInfo>();
            
            try
            {
                string bdmvDir = Path.Combine(bdmvRootPath, "BDMV");
                if (!Directory.Exists(bdmvDir))
                {
                    bdmvDir = bdmvRootPath;
                }

                string streamDir = Path.Combine(bdmvDir, "STREAM");
                if (!Directory.Exists(streamDir))
                {
                    return titles;
                }

                var m2tsFiles = Directory.GetFiles(streamDir, "*.m2ts");
                foreach (var file in m2tsFiles)
                {
                    var info = new FileInfo(file);
                    titles.Add(new BdmvTitleInfo
                    {
                        FilePath = file,
                        FileName = Path.GetFileName(file),
                        SizeBytes = info.Length,
                        DisplayName = $"{Path.GetFileName(file)} ({FormatFileSize(info.Length)})"
                    });
                }

                // 按文件大小降序排列（主电影通常最大）
                titles.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BDMV] GetBdmvTitles error: {ex.Message}");
            }

            return titles;
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes >= 1073741824)
                return $"{bytes / 1073741824.0:F1} GB";
            if (bytes >= 1048576)
                return $"{bytes / 1048576.0:F1} MB";
            if (bytes >= 1024)
                return $"{bytes / 1024.0:F1} KB";
            return $"{bytes} B";
        }

        #endregion
    }

    /// <summary>
    /// BDMV 蓝光标题信息
    /// </summary>
    public class BdmvTitleInfo
    {
        public string FilePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public string DisplayName { get; set; } = string.Empty;
    }

}
