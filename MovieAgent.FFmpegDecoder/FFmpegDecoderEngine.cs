using FFmpeg.AutoGen;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SharpGen.Runtime;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Timers;
using Vortice.Direct3D12;
using Vortice.Direct3D9;
 
namespace MovieAgent.FFmpegDecoder
{
    /// <summary>
    /// FFmpeg解码器引擎
    /// 负责音视频解码、硬件加速选择、音视频同步和性能监控
    /// </summary>
    public class FFmpegDecoderEngine : IDisposable
    {
        #region FFmpeg原生上下文指针
        private double _audioFirstPtsMs = -1;
        private double _lastAudioClock = -1;
        private Vortice.Direct3D11.ID3D11Device? _d3d11Device;
        private Vortice.Direct3D9.IDirect3DDevice9Ex? _d3d9Device;
        private  ID3D12Device? _d3d12Device;

        // D3D11 硬解私有纹理池：复制 FFmpeg 纹理数组的 ArraySlice 到私有纹理
        // FFmpeg 的 av_frame_unref 会立即释放 ArraySlice 回解码器池，导致渲染器读到的数据被新帧覆盖
        // 在 av_frame_unref 之前复制到私有纹理，GPU 命令串行执行，确保复制在新帧解码之前完成
        private const int HwCopyTextureSlots = 6; // 比渲染器三缓冲(3)多1个，确保复用时渲染器已释放
        private readonly Vortice.Direct3D11.ID3D11Texture2D?[] _hwCopyTextures = new Vortice.Direct3D11.ID3D11Texture2D?[HwCopyTextureSlots];
        private int _hwCopyIndex = 0;
        private int _hwCopyWidth, _hwCopyHeight;
        private Vortice.DXGI.Format _hwCopyFormat;
 
        /// <summary>
        /// 格式上下文，用于读取媒体文件
        /// </summary>
        private IntPtr _formatContext;

        /// <summary>
        /// 视频解码器上下文
        /// </summary>
        private IntPtr _videoCodecContext;

        /// <summary>
        /// 音频解码器上下文
        /// </summary>
        private IntPtr _audioCodecContext;

        /// <summary>
        /// 音频解码器上下文锁，防止切换音轨时与解码线程竞争
        /// </summary>
        private readonly object _audioCodecLock = new object();

        /// <summary>
        /// 字幕解码器上下文
        /// </summary>
        private IntPtr _subtitleCodecContext;

        /// <summary>
        /// 硬件设备上下文，用于硬件加速解码
        /// </summary>
        private IntPtr _hwDeviceContext;

        /// <summary>
        /// 图像缩放上下文（用于转换像素格式和分辨率）
        /// </summary>
        private IntPtr _swsContext;

        /// <summary>
        /// 音频重采样上下文（用于转换采样率和声道数）
        /// </summary>
        private IntPtr _swrContext;

        /// <summary>
        /// 视频帧缓冲区
        /// </summary>
        private IntPtr _videoFrame;

        /// <summary>
        /// 音频帧缓冲区
        /// </summary>
        private IntPtr _audioFrame;

        /// <summary>
        /// 数据包缓冲区
        /// </summary>
        private IntPtr _packet;

        #endregion

        #region 同步与时钟相关字段

        /// <summary>
        /// 时钟锁，保护时钟相关操作的线程安全
        /// </summary>
        private readonly object _clockLock = new object();

        /// <summary>
        /// 稳定期帧计数（用于旧解码模式）
        /// </summary>
        private int _stabilizeFrameCount = 0;

        /// <summary>
        /// 是否为第一帧视频
        /// </summary>
        private bool _isFirstVideoFrame = true;

        /// <summary>
        /// 时钟首次读取标志，用于日志记录
        /// </summary>
        private bool _isFirstClockLogged = false;

        /// <summary>
        /// 需要稳定的帧数
        /// </summary>
        private const int STABILIZE_FRAMES = 3;

        /// <summary>
        /// 视频时钟（毫秒）
        /// </summary>
        private double _videoClock = 0;

        /// <summary>
        /// 音频时钟（毫秒）
        /// </summary>
        private double _audioClock = 0;

        #endregion

        #region 显示队列相关

        /// <summary>
        /// 显示队列，用于解码线程和显示线程之间的帧传递
        /// 容量为2帧，最小化内存占用（4K降级到1080p后每帧约6MB，2帧约12MB）
        /// </summary>
        private BlockingCollection<FrameData> _displayQueue = new BlockingCollection<FrameData>(50);

        /// <summary>
        /// 显示线程，负责从队列中获取帧并触发FrameDecoded事件
        /// </summary>
        private Thread _displayThread;

        /// <summary>
        /// 解码循环取消令牌源
        /// </summary>
        private CancellationTokenSource? _decodeCts = new CancellationTokenSource();

        /// <summary>
        /// 显示线程取消令牌源
        /// </summary>
        private CancellationTokenSource? _displayCts = new CancellationTokenSource();

        /// <summary>
        /// 帧数据buffer对象池，用于复用byte[]减少GC压力
        /// </summary>
        private ConcurrentStack<byte[]> _frameBufferPool = new ConcurrentStack<byte[]>();

        /// <summary>
        /// 当前buffer大小，用于验证池中的buffer是否可用
        /// </summary>
        private int _currentBufferSize = 0;

        #endregion

        #region 流索引与参数

        /// <summary>
        /// 视频流索引（-1表示未找到）
        /// </summary>
        private int _videoStreamIndex = -1;

        /// <summary>
        /// 音频流索引（-1表示未找到）
        /// </summary>
        private int _audioStreamIndex = -1;

        /// <summary>
        /// 字幕流索引（-1表示未找到）
        /// </summary>
        private int _subtitleStreamIndex = -1;

        /// <summary>
        /// 视频时间基，用于PTS转换
        /// </summary>
        private double _videoTimeBase;

        /// <summary>
        /// 音频采样率
        /// </summary>
        private int _sampleRate;

        #endregion

        #region 音视频轨道信息

        /// <summary>
        /// 音频轨道列表
        /// </summary>
        private List<AudioTrackInfo> _audioTracks = new List<AudioTrackInfo>();

        /// <summary>
        /// 字幕轨道列表
        /// </summary>
        private List<SubtitleTrackInfo> _subtitleTracks = new List<SubtitleTrackInfo>();

        #endregion

        #region 播放控制

        /// <summary>
        /// 播放任务取消令牌源
        /// </summary>
        private CancellationTokenSource? _playCts;

        /// <summary>
        /// 播放任务
        /// </summary>
        private Task? _playTask;
        private Task? _audioTask;//音频解码任务
        private Task? _videoTask;//视频解码任务
        private Task? _demuxTask;//解复用任务
        private Task? _subtitleTask;//字幕解码任务
        private CancellationTokenSource? _audioCts;
        private CancellationTokenSource? _videoCts;
        private CancellationTokenSource? _demuxCts;
        private CancellationTokenSource? _subtitleCts;

        /// <summary>
        /// 音频数据包队列（解复用->音频解码）
        /// </summary>
        private Channel<PacketData> _audioPacketQueue;
        
        /// <summary>
        /// 视频数据包队列（解复用->视频解码）
        /// </summary>
        private Channel<PacketData> _videoPacketQueue;

        /// <summary>
        /// 字幕数据包队列（解复用->字幕解码）
        /// </summary>
        private Channel<PacketData> _subtitlePacketQueue;

        /// <summary>
        /// 硬件加速检测器
        /// </summary>
        private HardwareAccelerationDetector _detector;

        /// <summary>
        /// 当前swsContext的输入像素格式，用于动态重建缩放上下文
        /// </summary>
        private int _currentSwsInputFormat = -1;

        /// <summary>
        /// 是否正在播放
        /// </summary>
        private volatile bool _isPlaying;

        /// <summary>
        /// 是否处于暂停状态
        /// </summary>
        private volatile bool _isPaused;

        /// <summary>
        /// 已解码帧数计数器
        /// </summary>
        private int _frameCount = 0;
        
        /// <summary>
        /// 是否已保存调试帧（只保存一次）
        /// </summary>
        private bool _debugFrameSaved = false;

        #endregion

        #region 时间与状态

        /// <summary>
        /// Seek基准时间（毫秒），用于计算相对时间，Seek操作后更新
        /// </summary>
        private long _seekBaseTimeMs = 0;

        /// <summary>
        /// 当前播放时间（毫秒），由解码帧的PTS计算得出
        /// </summary>
        private long _currentTimeMs;

        /// <summary>
        /// 视频总时长（毫秒），从媒体文件元数据获取
        /// </summary>
        private long _durationMs;

        /// <summary>
        /// 当前音量（0-100），用于控制音频输出音量
        /// </summary>
        private int _volume = 100; 

        /// <summary>
        /// 视频宽度（像素），解码器输出的实际宽度
        /// </summary>
        private int _videoWidth;

        /// <summary>
        /// 视频高度（像素），解码器输出的实际高度
        /// </summary>
        private int _videoHeight;

        /// <summary>
        /// 帧率（fps），每秒帧数，用于计算目标解码时间
        /// </summary>
        private double _fps;

        /// <summary>
        /// 播放速度倍率（1.0为正常速度）
        /// </summary>
        private double _playbackSpeed = 1.0;

        /// <summary>
        /// 字幕延迟（毫秒），正数表示字幕延迟显示，负数表示提前显示
        /// </summary>
        private double _subtitleDelayMs = 0;

        #endregion

        #region 缓冲区与锁

        /// <summary>
        /// RGB缓冲区，用于存储转换后的视频帧数据
        /// </summary>
        private byte[]? _rgbBuffer;

        /// <summary>
        /// 音频操作锁
        /// </summary>
        private readonly object _audioLock = new();

        /// <summary>
        /// Seek操作锁
        /// </summary>
        private readonly object _seekLock = new();

        #endregion

        #region 时钟计算

        /// <summary>
        /// 时钟基准值（秒）
        /// </summary>
        private double _clockBase = 0;

        /// <summary>
        /// 时钟起始时刻的计时周期数
        /// </summary>
        private long _clockStartTicks;

        #endregion

        #region Seek操作

        /// <summary>
        /// 是否有待处理的Seek请求，用于异步Seek操作的标志位
        /// </summary>
        private volatile bool _pendingSeek;

        /// <summary>
        /// 待处理Seek的目标位置（秒），存储用户请求的Seek目标时间
        /// </summary>
        private double _pendingSeekTime;

        /// <summary>
        /// 是否正在执行Seek操作，防止并发Seek
        /// </summary>
        private volatile bool _isSeeking;
        private volatile bool _isSeekingStabilizing = false;  // Seek后稳定期，暂时禁用同步丢弃逻辑
        private int _seekStabilizingFrameCount = 0;  // Seek后稳定期帧计数

        /// <summary>
        /// 当前帧的 D3D11VA NV12 纹理指针 (零拷贝用)
        /// </summary>
        private IntPtr _currentNV12TexturePtr = IntPtr.Zero;

        /// <summary>
        /// 当前帧的 D3D11VA 纹理数组索引
        /// </summary>
        private uint _currentTextureArrayIndex = 0;

        /// <summary>
        /// 当前帧是否为硬件帧
        /// </summary>
        private bool _currentIsHardwareFrame = false;
        
        /// <summary>
        /// Seek时的音频播放位置基准值（字节），用于计算相对时间
        /// </summary>
        private long _seekAudioPositionBytes = 0;

        #endregion

        #region 解码模式与性能

        /// <summary>
        /// 当前解码模式（自动/硬件/软件），决定使用硬件还是软件解码
        /// </summary>
        private DecodeMode _decodeMode = DecodeMode.Auto;
        private D3DMode _d3DMode = D3DMode.D3D12;

        /// <summary>
        /// 当前解码器名称，记录实际使用的解码器标识
        /// </summary>
        private string _currentDecoder = string.Empty;

        /// <summary>
        /// 解码时间历史记录队列，用于计算平均解码时间和性能监控
        /// </summary>
        private readonly Queue<double> _decodeTimeHistory = new Queue<double>();

        /// <summary>
        /// 最大解码历史记录数，限制历史队列长度防止内存增长
        /// </summary>
        private const int MAX_DECODE_HISTORY = 30;

        /// <summary>
        /// 平均解码时间（毫秒），用于判断解码性能是否达标
        /// </summary>
        private double _avgDecodeTimeMs;

        /// <summary>
        /// 上次性能检查时间（计时周期数），用于控制性能检查频率
        /// </summary>
        private long _lastPerformanceCheck;

        /// <summary>
        /// 是否已发送性能警告，避免重复发送警告
        /// </summary>
        private bool _performanceWarningSent;

        /// <summary>
        /// 性能是否下降标志，用于触发降级策略
        /// </summary>
        private bool _isPerformanceDegraded;

        #endregion

        #region 音频输出

        /// <summary>
        /// 音频输出设备（使用NAudio的WaveOutEvent）
        /// </summary>
        private WaveOutEvent? _audioOutput;

        /// <summary>
        /// 音频缓冲区提供者
        /// </summary>
        private BufferedWaveProvider? _audioBuffer;
 
        #endregion

        #region 事件

        /// <summary>
        /// 帧解码完成事件，当新帧解码完成并准备显示时触发
        /// </summary>
        public event EventHandler<FrameData>? FrameDecoded;

        /// <summary>
        /// 播放结束事件
        /// </summary>
        public event EventHandler? PlaybackEnded;

        /// <summary>
        /// 播放错误事件
        /// </summary>
        public event EventHandler<string>? PlaybackError;

        /// <summary>
        /// 状态更新事件
        /// </summary>
        public event EventHandler<DecoderStatus>? StatusUpdated;

        /// <summary>
        /// 性能警告事件，当解码性能下降时触发
        /// </summary>
        public event EventHandler<DecodePerformanceWarning>? PerformanceWarning;

        /// <summary>
        /// 分辨率降级通知事件，当检测到需要降低分辨率时触发
        /// </summary>
        public event EventHandler<ResolutionDownscaleInfo>? ResolutionDownscale;

        /// <summary>
        /// 字幕解码完成事件，当字幕解码完成并准备显示时触发
        /// </summary>
        public event EventHandler<SubtitleData>? SubtitleDecoded;

        #endregion

        #region 属性

        public void SetD311dDevice(Vortice.Direct3D11.ID3D11Device device)
        {
            _d3d11Device = device;
        }
        public void SetD3d9dDevice(IDirect3DDevice9Ex device)
        {
            _d3d9Device = device;
        }

        /// <summary>
        /// 复制 FFmpeg 硬解纹理数组的 ArraySlice 到私有纹理。
        /// FFmpeg 的 av_frame_unref 会立即释放 ArraySlice 回解码器池，渲染器异步读取时数据可能被新帧覆盖。
        /// 在 av_frame_unref 之前复制到私有纹理，GPU 命令串行执行，确保复制在新帧解码之前完成。
        /// 私有纹理池4个槽位循环使用，比渲染器三缓冲多1个，确保复用时渲染器已释放。
        /// </summary>
        private IntPtr CopyHwTextureToPrivate(IntPtr srcTexturePtr, uint arraySlice, int width, int height)
        {
            if (_d3d11Device == null || srcTexturePtr == IntPtr.Zero) return IntPtr.Zero;
            try
            {
                var ctx = _d3d11Device.ImmediateContext;
                if (ctx == null) return IntPtr.Zero;

                var srcTexture = new Vortice.Direct3D11.ID3D11Texture2D(srcTexturePtr);
                var srcDesc = srcTexture.Description;

                int idx = _hwCopyIndex;
                _hwCopyIndex = (_hwCopyIndex + 1) % HwCopyTextureSlots;

                // 尺寸或格式变化时重建当前槽位的纹理
                if (_hwCopyTextures[idx] == null ||
                    _hwCopyWidth != width || _hwCopyHeight != height || _hwCopyFormat != srcDesc.Format)
                {
                    _hwCopyTextures[idx]?.Dispose();
                    var desc = new Vortice.Direct3D11.Texture2DDescription
                    {
                        Width = (uint)width,
                        Height = (uint)height,
                        MipLevels = 1,
                        ArraySize = 1, // 单个纹理，非数组
                        Format = srcDesc.Format,
                        SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
                        Usage = Vortice.Direct3D11.ResourceUsage.Default,
                        BindFlags = Vortice.Direct3D11.BindFlags.Decoder,
                        CPUAccessFlags = Vortice.Direct3D11.CpuAccessFlags.None
                    };
                    _hwCopyTextures[idx] = _d3d11Device.CreateTexture2D(desc);
                    _hwCopyWidth = width;
                    _hwCopyHeight = height;
                    _hwCopyFormat = srcDesc.Format;
                }

                // 复制源纹理的 ArraySlice 到私有纹理
                // Vortice 签名：CopySubresourceRegion(dst, dstSub, dstX, dstY, dstZ, src, srcSub, srcBox)
                ctx.CopySubresourceRegion(
                    _hwCopyTextures[idx]!, 0, 0, 0, 0,
                    srcTexture, arraySlice, null);

                // 不需要 Flush：FFmpeg 和渲染器使用同一个 ImmediateContext
                // 命令在同一个缓冲区中串行执行，复制命令在 av_frame_unref 后的解码命令之前
                // 显式 Flush 会打断 GPU 批处理优化，导致偶尔卡顿

                return _hwCopyTextures[idx]!.NativePointer;
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[FFmpeg] 复制硬件纹理失败: {ex.Message}");
                return IntPtr.Zero;
            }
        }
        public void SetD3d12dDevice(ID3D12Device device)
        {
            _d3d12Device = device;
        }
        /// <summary>
        /// 是否正在播放
        /// </summary>
        public bool IsPlaying => _isPlaying;

        /// <summary>
        /// 是否处于暂停状态
        /// </summary>
        public bool IsPaused => _isPaused;

        /// <summary>
        /// 视频总时长（毫秒）
        /// </summary>
        public long DurationMs => _durationMs;

        /// <summary>
        /// 当前播放时间（毫秒）
        /// </summary>
        public long CurrentTimeMs => _currentTimeMs;

        /// <summary>
        /// 音频播放位置（字节）
        /// </summary>
        public long AudioPlayPosition => (long)GetPlaybackClock();
            //_audioOutput?.GetPosition() ?? 0;

        /// <summary>
        /// 视频宽度
        /// </summary>
        public int VideoWidth => _videoWidth;

        /// <summary>
        /// 视频高度
        /// </summary>
        public int VideoHeight => _videoHeight;

        /// <summary>
        /// 帧率（fps）
        /// </summary>
        public double Fps => _fps;

        /// <summary>
        /// 当前解码器名称
        /// </summary>
        public string CurrentDecoder => _currentDecoder;

        /// <summary>
        /// 当前解码模式
        /// </summary>
        public DecodeMode CurrentDecodeMode => _decodeMode;
        public D3DMode? CurrentD3dModel => _d3DMode;
        /// <summary>
        /// 已解码帧数
        /// </summary>
        public int FrameCount => _frameCount;

        /// <summary>
        /// 当前播放速度（1.0为正常速度）
        /// </summary>
        public double PlaybackSpeed => _playbackSpeed;

        /// <summary>
        /// 字幕延迟（毫秒，正数表示延迟，负数表示提前）
        /// </summary>
        public double SubtitleDelayMs => _subtitleDelayMs;

        #endregion

        #region 解码模式枚举

        /// <summary>
        /// 解码模式枚举
        /// </summary>
        public enum DecodeMode
        {
            /// <summary>
            /// 自动选择（优先硬件，失败回退软件）
            /// </summary>
            Auto,

            /// <summary>
            /// 强制硬件解码
            /// </summary>
            Hardware,

            /// <summary>
            /// 强制软件解码
            /// </summary>
            Software
        }

        public enum D3DMode
        {

            D3D9, D3D10, D3D11, D3D12, D3D13
        }

        #endregion

        #region 构造函数

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="mode">解码模式，默认为自动选择</param>
        public FFmpegDecoderEngine(DecodeMode mode = DecodeMode.Auto, D3DMode d3dModel= D3DMode.D3D12)
        {
            _decodeMode = mode;
            _d3DMode = d3dModel;
            InitializeFFmpeg();
        }

        #endregion

        #region 解码模式设置

        /// <summary>
        /// 设置解码模式
        /// </summary>
        /// <param name="mode">解码模式</param>
        public void SetDecodeMode(DecodeMode mode)
        {
            _decodeMode = mode;
            DebugLogger.WriteLine($"[FFmpeg] Decode mode set to: {mode}");
        }
        public void SetD3d9Model(D3DMode mode)
        {
            _d3DMode = mode;
            DebugLogger.WriteLine($"[FFmpeg] D3DMode mode set to: {mode}"); 
        }
      
        #endregion

        #region 硬件加速检测

        /// <summary>
        /// 检查硬件加速支持情况
        /// </summary>
        /// <param name="codecId">编解码器ID</param>
        /// <returns>返回值：(是否支持硬件加速, 硬件设备类型)</returns>
        private async Task<(bool, AVHWDeviceType?)> CheckHardwareAccelerationSupport(AVCodecID codecId)
        {
            try
            {
                
                // 检测 H.264 硬件解码
                var hwType = await _detector.GetBestHardwareTypeAsync(codecId);

                if (hwType.HasValue)
                {
                    DebugLogger.WriteLine($"Using hardware decoding: {ffmpeg.av_hwdevice_get_type_name(hwType.Value)}");
                    // 使用硬件解码播放...
                    return (true, hwType.Value);
                }
                else
                {
                    DebugLogger.WriteLine("Using software decoding");
                    // 使用软解...
                    return (false, null);
                }
                DebugLogger.WriteLine("[FFmpeg] No hardware decoders found, falling back to software");
                return (false, null);
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[FFmpeg] Hardware detection failed: {ex.Message}");
                return (false, null);
            }
        }

        /// <summary>
        /// 获取硬件解码器名称
        /// </summary>
        /// <param name="codecId">编解码器ID</param>
        /// <returns>硬件解码器名称，如果不支持则返回null</returns>
        private unsafe string? GetHardwareDecoderName(AVCodecID codecId)
        {
            try
            {
                var bestDevice = SelectBestHardwareDevice();
                if (!bestDevice.HasValue || bestDevice.Value == AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
                {
                    DebugLogger.WriteLine("[FFmpeg] No hardware device selected, skipping hardware decoder");
                    return null;
                }

                string devicePrefix = bestDevice.Value switch
                {
                    AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA => "cuvid",
                    AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA => "d3d11va",
                    AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2 => "dxva2",
                    AVHWDeviceType.AV_HWDEVICE_TYPE_QSV => "qsv",
                    AVHWDeviceType.AV_HWDEVICE_TYPE_AMF => "amf",
                    _ => "nvdec"
                };

                string codecName = codecId switch
                {
                    AVCodecID.AV_CODEC_ID_H264 => "h264",
                    AVCodecID.AV_CODEC_ID_HEVC => "hevc",
                    AVCodecID.AV_CODEC_ID_VP9 => "vp9",
                    AVCodecID.AV_CODEC_ID_VP8 => "vp8",
                    _ => null
                };

                if (codecName == null)
                {
                    DebugLogger.WriteLine($"[FFmpeg] Unsupported codec ID: {codecId}");
                    return null;
                }

                string[] possibleNames = devicePrefix switch
                {
                    "cuvid" => new[] { $"{codecName}_{devicePrefix}", $"{codecName}_nvdec" },
                    "nvdec" => new[] { $"{codecName}_{devicePrefix}" },
                    _ => new[] { $"{codecName}_{devicePrefix}" }
                };

                foreach (string name in possibleNames)
                {
                    AVCodec* codec = ffmpeg.avcodec_find_decoder_by_name(name);
                    if (codec != null)
                    {
                        DebugLogger.WriteLine($"[FFmpeg] Found hardware decoder: {name} (device: {devicePrefix})");
                        return name;
                    }
                    DebugLogger.WriteLine($"[FFmpeg] Decoder not found: {name}");
                }

                DebugLogger.WriteLine($"[FFmpeg] No hardware decoder available for {codecName} with {devicePrefix}");
                return null;
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[FFmpeg] Failed to get hardware decoder: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 硬件信息类
        /// 存储系统中检测到的GPU信息
        /// </summary>
        public class HardwareInfo
        {
            /// <summary>
            /// 是否有NVIDIA GPU
            /// </summary>
            public bool HasNvidiaGpu { get; set; }

            /// <summary>
            /// 是否有Intel GPU
            /// </summary>
            public bool HasIntelGpu { get; set; }

            /// <summary>
            /// 是否有AMD GPU
            /// </summary>
            public bool HasAmdGpu { get; set; }

            /// <summary>
            /// NVIDIA驱动版本
            /// </summary>
            public string NvidiaDriverVersion { get; set; } = "";

            /// <summary>
            /// Intel驱动版本
            /// </summary>
            public string IntelDriverVersion { get; set; } = "";

            /// <summary>
            /// AMD驱动版本
            /// </summary>
            public string AmdDriverVersion { get; set; } = "";

            /// <summary>
            /// NVIDIA显存大小（MB）
            /// </summary>
            public long NvidiaMemoryMB { get; set; }

            /// <summary>
            /// Intel显存大小（MB）
            /// </summary>
            public long IntelMemoryMB { get; set; }

            /// <summary>
            /// AMD显存大小（MB）
            /// </summary>
            public long AmdMemoryMB { get; set; }

            /// <summary>
            /// NVIDIA GPU名称
            /// </summary>
            public string NvidiaGpuName { get; set; } = "";

            /// <summary>
            /// Intel GPU名称
            /// </summary>
            public string IntelGpuName { get; set; } = "";

            /// <summary>
            /// AMD GPU名称
            /// </summary>
            public string AmdGpuName { get; set; } = "";
        }

        /// <summary>
        /// 硬件信息实例
        /// </summary>
        private HardwareInfo _hardwareInfo = new HardwareInfo();

        /// <summary>
        /// 缓存的硬件设备类型
        /// </summary>
        private AVHWDeviceType? _cachedDeviceType;

        /// <summary>
        /// 检测系统中的硬件GPU信息
        /// 使用WMI查询Win32_VideoController获取显卡信息
        /// </summary>
        private void DetectHardware()
        {
            _hardwareInfo = new HardwareInfo();
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher("SELECT * FROM Win32_VideoController"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        string name = obj["Name"]?.ToString() ?? "";
                        string driverVersion = obj["DriverVersion"]?.ToString() ?? "";
                        ulong adapterRam = obj["AdapterRAM"] as ulong? ?? 0;
                        long memoryMB = (long)(adapterRam / 1024 / 1024);

                        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                        {
                            _hardwareInfo.HasNvidiaGpu = true;
                            _hardwareInfo.NvidiaDriverVersion = driverVersion;
                            _hardwareInfo.NvidiaMemoryMB = memoryMB;
                            _hardwareInfo.NvidiaGpuName = name;
                            DebugLogger.WriteLine($"[FFmpeg] Found NVIDIA GPU: {name}, Memory: {memoryMB}MB, Driver: {driverVersion}");
                        }
                        else if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
                        {
                            _hardwareInfo.HasIntelGpu = true;
                            _hardwareInfo.IntelDriverVersion = driverVersion;
                            _hardwareInfo.IntelMemoryMB = memoryMB;
                            _hardwareInfo.IntelGpuName = name;
                            DebugLogger.WriteLine($"[FFmpeg] Found Intel GPU: {name}, Memory: {memoryMB}MB, Driver: {driverVersion}");
                        }
                        else if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                                 name.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
                        {
                            _hardwareInfo.HasAmdGpu = true;
                            _hardwareInfo.AmdDriverVersion = driverVersion;
                            _hardwareInfo.AmdMemoryMB = memoryMB;
                            _hardwareInfo.AmdGpuName = name;
                            DebugLogger.WriteLine($"[FFmpeg] Found AMD GPU: {name}, Memory: {memoryMB}MB, Driver: {driverVersion}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[FFmpeg] Hardware detection failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 检查是否有NVIDIA GPU
        /// </summary>
        /// <returns>如果有NVIDIA GPU返回true，否则返回false</returns>
        private bool CheckNvidiaGpu()
        {
            if (_hardwareInfo.HasNvidiaGpu) return true;
            DetectHardware();
            return _hardwareInfo.HasNvidiaGpu;
        }

        /// <summary>
        /// 检查是否有Intel GPU
        /// </summary>
        /// <returns>如果有Intel GPU返回true，否则返回false</returns>
        private bool CheckIntelGpu()
        {
            if (_hardwareInfo.HasIntelGpu) return true;
            DetectHardware();
            return _hardwareInfo.HasIntelGpu;
        }

        /// <summary>
        /// 检查是否有AMD GPU
        /// </summary>
        /// <returns>如果有AMD GPU返回true，否则返回false</returns>
        private bool CheckAmdGpu()
        {
            if (_hardwareInfo.HasAmdGpu) return true;
            DetectHardware();
            return _hardwareInfo.HasAmdGpu;
        }

        /// <summary>
        /// 选择最佳硬件设备
        /// 按优先级顺序测试可用的硬件加速设备
        /// </summary>
        /// <returns>最佳硬件设备类型，如果没有可用设备则返回null</returns>
        private unsafe AVHWDeviceType? SelectBestHardwareDevice()
        {
            if (_cachedDeviceType.HasValue)
            {
                DebugLogger.WriteLine($"[FFmpeg] Returning cached hardware device: {_cachedDeviceType.Value}");
                return _cachedDeviceType.Value;
            }

            if (!_hardwareInfo.HasNvidiaGpu && !_hardwareInfo.HasIntelGpu && !_hardwareInfo.HasAmdGpu)
            {
                DetectHardware();
            }

            var priorityList = new List<Tuple<AVHWDeviceType, string>>
            {
                Tuple.Create(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, "d3d11va"),
                Tuple.Create(AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2, "dxva2"),
                Tuple.Create(AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA, "cuda"),
                Tuple.Create(AVHWDeviceType.AV_HWDEVICE_TYPE_QSV, "qsv"),
                Tuple.Create(AVHWDeviceType.AV_HWDEVICE_TYPE_AMF, "amf")
            };

            foreach (var (deviceType, deviceName) in priorityList)
            {
                if (deviceType == AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA && !_hardwareInfo.HasNvidiaGpu)
                    continue;
                if (deviceType == AVHWDeviceType.AV_HWDEVICE_TYPE_QSV && !_hardwareInfo.HasIntelGpu)
                    continue;
                if (deviceType == AVHWDeviceType.AV_HWDEVICE_TYPE_AMF && !_hardwareInfo.HasAmdGpu)
                    continue;

                if (TestHardwareDevice(deviceType))
                {
                    DebugLogger.WriteLine($"[FFmpeg] Selected hardware device: {deviceName} ({deviceType})");
                    _cachedDeviceType = deviceType;
                    return deviceType;
                }
                DebugLogger.WriteLine($"[FFmpeg] Hardware device not available: {deviceName} ({deviceType})");
            }

            DebugLogger.WriteLine("[FFmpeg] No hardware acceleration available, falling back to software");
            _cachedDeviceType = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
            return null;
        }

        /// <summary>
        /// 测试硬件设备是否可用
        /// 通过尝试创建硬件设备上下文来验证
        /// </summary>
        /// <param name="deviceType">硬件设备类型</param>
        /// <returns>如果设备可用返回true，否则返回false</returns>
        private unsafe bool TestHardwareDevice(AVHWDeviceType deviceType)
        {
            try
            {
                AVBufferRef* hwDeviceCtx = null;
                int ret = ffmpeg.av_hwdevice_ctx_create(&hwDeviceCtx, deviceType, null, null, 0);
                if (ret < 0)
                    return false;

                ffmpeg.av_buffer_unref(&hwDeviceCtx);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 打开硬件编解码器
        /// 创建硬件设备上下文并绑定到解码器上下文
        /// </summary>
        /// <param name="codecCtx">解码器上下文指针</param>
        /// <param name="codec">编解码器指针</param>
        /// <param name="deviceType">硬件设备类型</param>
        /// <returns>0表示成功，负数表示失败</returns>
        private unsafe int OpenHardwareCodec(AVCodecContext* codecCtx, AVCodec* codec, AVHWDeviceType deviceType)
        {
            try
            {
                AVBufferRef* hwDeviceCtx = null;
                int ret = ffmpeg.av_hwdevice_ctx_create(&hwDeviceCtx, deviceType, null, null, 0);
                if (ret < 0)
                {
                    DebugLogger.WriteLine($"[FFmpeg] Failed to create hardware device context: {ret}");
                    return ret;
                }

                codecCtx->hw_device_ctx = ffmpeg.av_buffer_ref(hwDeviceCtx);
                ffmpeg.av_buffer_unref(&hwDeviceCtx);

                if (codecCtx->hw_device_ctx == null)
                {
                    DebugLogger.WriteLine("[FFmpeg] Failed to reference hardware device context");
                    return -1;
                }

                ret = ffmpeg.avcodec_open2(codecCtx, codec, null);
                if (ret >= 0)
                {
                    DebugLogger.WriteLine($"[FFmpeg] Successfully opened hardware codec with device context: {deviceType}");
                }
                else
                {
                    DebugLogger.WriteLine($"[FFmpeg] Failed to open hardware codec with device context: {ret}");
                    if (codecCtx->hw_device_ctx != null)
                    {
                        ffmpeg.av_buffer_unref(&codecCtx->hw_device_ctx);
                        codecCtx->hw_device_ctx = null;
                    }
                }

                return ret;
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[FFmpeg] Error in OpenHardwareCodec: {ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// 初始化FFmpeg库
        /// 设置FFmpeg路径、检测硬件并启动显示线程
        /// </summary>
        private unsafe void InitializeFFmpeg()
        {
            try
            {
                var baseDir = AppContext.BaseDirectory;
                DebugLogger.WriteLine($"[FFmpeg] ===== InitializeFFmpeg 开始 ===== ");
                DebugLogger.WriteLine($"[FFmpeg] Decoder base directory: {baseDir}");

                if (Directory.Exists(baseDir))
                {
                    ffmpeg.RootPath = baseDir;
                }
                else
                {
                    ffmpeg.RootPath = Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName) ?? baseDir;
                }

                DebugLogger.WriteLine($"[FFmpeg] Trying FFmpeg path: {ffmpeg.RootPath}");

                // 检查FFmpeg库是否存在
                string avcodecPath = Path.Combine(ffmpeg.RootPath, "avcodec-62.dll");
                string avformatPath = Path.Combine(ffmpeg.RootPath, "avformat-62.dll");
                string avutilPath = Path.Combine(ffmpeg.RootPath, "avutil-60.dll");
                
                bool avcodecExists = File.Exists(avcodecPath);
                bool avformatExists = File.Exists(avformatPath);
                bool avutilExists = File.Exists(avutilPath);
                
                DebugLogger.WriteLine($"[FFmpeg] avcodec-62.dll exists: {avcodecExists}");
                DebugLogger.WriteLine($"[FFmpeg] avformat-62.dll exists: {avformatExists}");
                DebugLogger.WriteLine($"[FFmpeg] avutil-60.dll exists: {avutilExists}");

                if (!avcodecExists)
                {
                    string mainAppDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "MovieAgent", "bin", "Debug", "net10.0-windows"));
                    DebugLogger.WriteLine($"[FFmpeg] FFmpeg not found in decoder dir, trying main app dir: {mainAppDir}");
                    
                    if (Directory.Exists(mainAppDir) && File.Exists(Path.Combine(mainAppDir, "avcodec-62.dll")))
                    {
                        ffmpeg.RootPath = mainAppDir;
                        DebugLogger.WriteLine($"[FFmpeg] Using FFmpeg from main app directory: {ffmpeg.RootPath}");
                    }
                    else
                    {
                        DebugLogger.WriteLine($"[FFmpeg] ERROR: FFmpeg libraries not found in any path!");
                        DebugLogger.WriteLine($"[FFmpeg] Searched paths:");
                        DebugLogger.WriteLine($"[FFmpeg]   - {ffmpeg.RootPath}");
                        DebugLogger.WriteLine($"[FFmpeg]   - {mainAppDir}");
                    }
                }

                try
                {
                    var version = ffmpeg.av_version_info();
                    DebugLogger.WriteLine($"[FFmpeg] Version: {version}");
                }
                catch (Exception ex)
                {
                    DebugLogger.WriteLine($"[FFmpeg] ERROR: Failed to get FFmpeg version: {ex.Message}");
                    DebugLogger.WriteLine($"[FFmpeg] This usually means FFmpeg libraries are not found or incompatible");
                }
                
                _detector = new HardwareAccelerationDetector("MovieAgentPlayer"); 
                DebugLogger.WriteLine($"[FFmpeg] 硬件检测初始化完成.");
                
               

            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[FFmpeg] Initialization error: {ex.Message}");
                DebugLogger.WriteLine($"[FFmpeg] Stack trace: {ex.StackTrace}");
            }
        }
        private void DiagnoseAudio()
        {
            DebugLogger.WriteLine("================== 🎵 音频完整诊断 ==================");

       
            // 通常设备 0 是默认音频设备

            DebugLogger.WriteLine($"🎵 包:{_audioPacketQueue?.Reader?.Count ?? 0} 帧:{_audioFirstPtsMs} 缓冲:{_audioBuffer?.BufferedBytes ?? 0} 状态:{_audioOutput?.PlaybackState} 设备:{_audioOutput?.DeviceNumber}");



            // 解码器输出格式
            //DebugLogger.WriteLine($"🎵 解码器输出:");
            //DebugLogger.WriteLine($"  采样率: {_audioDecoder.SampleRate}Hz");
            //DebugLogger.WriteLine($"  声道数: {_audioDecoder.Channels}");
            //DebugLogger.WriteLine($"  采样格式: {_audioDecoder.SampleFormat}");

            // 播放器期望格式
            DebugLogger.WriteLine($"🎵 播放器期望:");
            DebugLogger.WriteLine($"  采样率: {_audioBuffer.WaveFormat.SampleRate}Hz");
            DebugLogger.WriteLine($"  声道数: {_audioBuffer.WaveFormat.Channels}");
            DebugLogger.WriteLine($"  位深: {_audioBuffer.WaveFormat.BitsPerSample}");
          
           


            DebugLogger.WriteLine("================== 🎵 诊断完成 ==================");
        }
        /// <summary>
        /// 开始播放视频
        /// </summary>
        /// <param name="filePath">视频文件路径</param>
        /// <returns>任务</returns>
        /// <exception cref="InvalidOperationException">当无法打开视频文件时抛出</exception>
        public async Task PlayAsync(string filePath)
        {
            DebugLogger.WriteLine($"[FFmpeg] ===== PlayAsync 开始 ===== ");
            DebugLogger.WriteLine($"[FFmpeg] Starting playback for file: {filePath}");
            DebugLogger.WriteLine($"[FFmpeg] 当前线程ID: {System.Threading.Thread.CurrentThread.ManagedThreadId}");
            
            try
            {
                DebugLogger.WriteLine($"[FFmpeg] 调用 StopInternalAsync...");
                await StopInternalAsync();
                DebugLogger.WriteLine($"[FFmpeg] StopInternalAsync 完成");

                DebugLogger.WriteLine($"[FFmpeg] 调用 OpenFile...");
                bool success = OpenFile(filePath);
                DebugLogger.WriteLine($"[FFmpeg] OpenFile 完成，结果: {success}");
                
                if (!success)
                {
                    string errorMsg = $"Failed to open video file: {filePath}";
                    DebugLogger.WriteLine($"[FFmpeg] Error: {errorMsg}");
                    PlaybackError?.Invoke(this, errorMsg);
                    throw new InvalidOperationException(errorMsg);
                }

                _playCts = new CancellationTokenSource();
                _isPlaying = true;
                _isPaused = false;
                _isSeeking = false;
                _clockBase = 0;
                _clockStartTicks = Stopwatch.GetTimestamp();
                _frameCount = 0;
                _debugFrameSaved = false;

                // 重新初始化解码取消令牌源（可能在上一次播放停止时被释放）
                _decodeCts = new CancellationTokenSource();
                // 重新初始化显示线程取消令牌源
                _displayCts = new CancellationTokenSource();
                // 重新初始化显示队列（在上一次播放停止时被 CompleteAdding）
                _displayQueue = new BlockingCollection<FrameData>(2);
                DebugLogger.WriteLine($"[FFmpeg] 播放状态已设置，_decodeCts 和 _displayCts 已重新初始化");

                // 启动音频播放器
                if (_audioOutput != null && _audioBuffer != null)
                {
                    _audioBuffer.ClearBuffer();
                    if (_audioOutput.PlaybackState != PlaybackState.Playing)
                    {
                        _audioOutput.Play(); // 启动播放器
                         DebugLogger.WriteLine("[FFmpeg] Audio playback started");
                    }
                }
                else
                {
                    DebugLogger.WriteLine("[FFmpeg] Audio output or buffer is null, skipping audio playback");
                }

                DebugLogger.WriteLine("[FFmpeg] Starting async decode architecture...");

                // 创建数据包队列（带容量限制，防止内存溢出）
                _audioPacketQueue = Channel.CreateBounded<PacketData>(new BoundedChannelOptions(50)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleWriter = false,
                    SingleReader = true
                });
                DebugLogger.WriteLine("[FFmpeg] 音频数据包队列创建完成");
                
                _videoPacketQueue = Channel.CreateBounded<PacketData>(new BoundedChannelOptions(30)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleWriter = false,
                    SingleReader = true
                });
                DebugLogger.WriteLine("[FFmpeg] 视频数据包队列创建完成");

                _subtitlePacketQueue = Channel.CreateBounded<PacketData>(new BoundedChannelOptions(20)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleWriter = false,
                    SingleReader = true
                });
                DebugLogger.WriteLine("[FFmpeg] 字幕数据包队列创建完成");

                // 启动异步解码任务
                _audioCts = CancellationTokenSource.CreateLinkedTokenSource(_playCts.Token);
                _videoCts = CancellationTokenSource.CreateLinkedTokenSource(_playCts.Token);
                _demuxCts = CancellationTokenSource.CreateLinkedTokenSource(_playCts.Token);
                _subtitleCts = CancellationTokenSource.CreateLinkedTokenSource(_playCts.Token);

                // 启动音频解码线程（高优先级）
                DebugLogger.WriteLine("[FFmpeg] 启动音频解码线程...");
                _audioTask = Task.Factory.StartNew(() => AudioDecodeLoopAsync(_audioCts.Token), 
                    _audioCts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                DebugLogger.WriteLine("[FFmpeg] 音频解码线程已启动");

                // 启动视频解码线程
                DebugLogger.WriteLine("[FFmpeg] 启动视频解码线程...");
                 _videoTask = Task.Factory.StartNew(() => VideoDecodeLoopAsync(_videoCts.Token), 
                    _videoCts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                DebugLogger.WriteLine("[FFmpeg] 视频解码线程已启动");

                // 启动字幕解码线程
                DebugLogger.WriteLine("[FFmpeg] 启动字幕解码线程...");
                _subtitleTask = Task.Factory.StartNew(() => SubtitleDecodeLoopAsync(_subtitleCts.Token), 
                    _subtitleCts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                DebugLogger.WriteLine("[FFmpeg] 字幕解码线程已启动");

                // 启动解复用线程（负责读取数据包并分发）
                DebugLogger.WriteLine("[FFmpeg] 启动解复用线程...");
                _demuxTask = Task.Factory.StartNew(() => DemuxLoopAsync(_demuxCts.Token), 
                    _demuxCts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                DebugLogger.WriteLine("[FFmpeg] 解复用线程已启动");

                DebugLogger.WriteLine("[FFmpeg] Async decode threads started");
                DebugLogger.WriteLine($"[FFmpeg] ===== PlayAsync 完成 ===== ");

                // 初始化时启动显示线程
                DebugLogger.WriteLine($"[FFmpeg] 启动显示线程...");
                _displayThread = new Thread(() =>
                {
                    DebugLogger.WriteLine("[FFmpeg] 显示线程启动（VSync 驱动）");
                    try
                    {
                        bool audioOffsetInitialized = false;
                        double audioStartOffset = 0;
                         while (_displayCts!=null&&!_displayCts.Token.IsCancellationRequested)
                        {
                            if (!_isPlaying || _isSeeking)
                            {
                                Thread.Sleep(10);
                                continue;
                            }

                            // 从队列取一帧（不设超时，反复尝试直到取到）
                            if (!_displayQueue.TryTake(out var frame, 1, _displayCts.Token))
                                continue;

                            double speed = _playbackSpeed; // 线程安全的播放速度
                            bool isFastForward = Math.Abs(speed - 1.0) > 0.01;

                            // ========== 快进模式：直接渲染，不处理音频同步 ==========
                            if (isFastForward)
                            {
                                FrameDecoded?.Invoke(this, frame);
                                // 不再手动限制帧率，让 Present(1) 的 VSync 自然控制节奏
                                continue;
                            }

                            // ========== 正常播放模式：音画同步（只做必要的音频等待/丢帧） ==========
                            // 1. 初始化音频偏移（首次）
                            if (!audioOffsetInitialized)
                            {
                                audioStartOffset = GetPlaybackClock();
                                audioOffsetInitialized = true;
                            }

                            double effectiveAudioClock = GetPlaybackClock() - audioStartOffset;
                            double videoPts = frame.VideoTimestamp / 1000.0;
                            double diff = videoPts - effectiveAudioClock;

                            // 2. Seek 跳变检测 → 重置偏移，直接渲染
                            if (Math.Abs(diff) > 3)
                            {
                                audioOffsetInitialized = false;  // 下次重新对齐
                                                                 // 直接渲染，不等
                                FrameDecoded?.Invoke(this, frame);
                                continue;
                            }

                            // 3. 正常同步逻辑
                            const double TOLERANCE = 0.040;
                            const double DROP_THRESHOLD = 0.6;

                            if (diff > DROP_THRESHOLD)          // 领先超过 0.6 秒，丢帧
                                continue;
                            else if (diff > TOLERANCE)          // 视频稍快，等待音频
                            {
                                int waitMs = (int)(diff * 1000);
                                if (waitMs > 100) waitMs = 100; // 最多等 100ms，减少队列堆积
                                if (waitMs > 0) Thread.Sleep(waitMs);
                                FrameDecoded?.Invoke(this, frame);
                            }
                            else if (diff < -TOLERANCE)         // 视频落后，丢帧追赶
                                continue;
                            else                                // 同步良好，直接渲染
                                FrameDecoded?.Invoke(this, frame);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        DebugLogger.WriteLine("[FFmpeg] 显示线程正常停止");
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.WriteLine($"[FFmpeg] 显示线程异常: {ex.Message}");
                    }
                });

                _displayThread.IsBackground = true;
                _displayThread.Start();

                // await Task.Delay(3000);
                // DiagnoseAudio();
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[FFmpeg] PlayAsync 异常: {ex.Message}");
                DebugLogger.WriteLine($"[FFmpeg] PlayAsync 异常堆栈: {ex.StackTrace}");
                PlaybackError?.Invoke(this, ex.Message);
                throw;
            }
        }

        /// <summary>
        /// 打开视频文件
        /// </summary>
        /// <param name="filePath">视频文件路径</param>
        /// <returns>如果成功打开返回true，否则返回false</returns>
        private bool OpenFile(string filePath)
        {
            DebugLogger.WriteLine($"[FFmpeg] OpenFile called with path: {filePath}");
            
            try
            {
                bool isFile = File.Exists(filePath);
                bool isDir = Directory.Exists(filePath);
                DebugLogger.WriteLine($"[FFmpeg] File exists: {isFile}, Dir exists: {isDir}");
                
                if (!isFile && !isDir)
                {
                    DebugLogger.WriteLine($"[FFmpeg] Path not found: {filePath}");
                    return false;
                }

                return OpenFileUnsafe(filePath);
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[FFmpeg] OpenFile exception: {ex.Message}");
                DebugLogger.WriteLine($"[FFmpeg] OpenFile stack trace: {ex.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// 打开视频文件（不安全版本，包含FFmpeg原生指针操作）
        /// </summary>
        /// <param name="filePath">视频文件路径</param>
        /// <returns>如果成功打开返回true，否则返回false</returns>
        private unsafe bool OpenFileUnsafe(string filePath)
        {
            try
            {
                DebugLogger.WriteLine($"[FFmpeg] ===== 开始打开文件 ===== ");
                DebugLogger.WriteLine($"[FFmpeg] 文件路径: {filePath}");
                
                // 检测 ISO 文件
                if (filePath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                {
                    return OpenIsoFile(filePath);
                }
                
                // 检测 BDMV 文件夹
                if (Directory.Exists(filePath) && IsBdmvStructure(filePath))
                {
                    return OpenBdmvFolder(filePath);
                }

                AVFormatContext* fmtCtx = ffmpeg.avformat_alloc_context();
                _formatContext = (IntPtr)fmtCtx;
                if (fmtCtx == null)
                {
                    DebugLogger.WriteLine("[FFmpeg] ❌ 分配格式上下文失败");
                    return false;
                }

                DebugLogger.WriteLine("[FFmpeg] ✅ 格式上下文分配成功");
                
                AVDictionary* options = null;
                ffmpeg.av_dict_set(&options, "buffer_size", "1024000", 0);
                ffmpeg.av_dict_set(&options, "probesize", "5000000", 0);
                ffmpeg.av_dict_set(&options, "analyzeduration", "3000000", 0);

                if (ffmpeg.avformat_open_input(&fmtCtx, filePath, null, &options) != 0)
                {
                    DebugLogger.WriteLine("[FFmpeg] Failed to open file");
                    return false;
                }
                _formatContext = (IntPtr)fmtCtx;

                if (ffmpeg.avformat_find_stream_info(fmtCtx, null) < 0)
                {
                    DebugLogger.WriteLine("[FFmpeg] Failed to find stream info");
                    return false;
                }

                // 先初始化流索引
                _videoStreamIndex = -1;
                _audioStreamIndex = -1;
                _subtitleStreamIndex = -1;
                _audioTracks.Clear();
                _subtitleTracks.Clear();

                for (int i = 0; i < (int)fmtCtx->nb_streams; i++)
                {
                    var stream = fmtCtx->streams[i];
                    var codecParams = stream->codecpar;
                    var codec = ffmpeg.avcodec_find_decoder(codecParams->codec_id);

                    if (codecParams->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && _videoStreamIndex < 0)
                    {
                        _videoStreamIndex = i;
                        _videoTimeBase = ffmpeg.av_q2d(stream->time_base);
                        NewInitializeVideoDecoder(stream);
                    }
                    else if (codecParams->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                    {
                        string codecName = codec != null ? Marshal.PtrToStringAnsi((IntPtr)codec->name) : null;
                        int channels = codecParams->ch_layout.nb_channels;
                        
                        // 获取语言信息
                        AVDictionaryEntry* langTag = ffmpeg.av_dict_get(stream->metadata, "language", null, 0);
                        string language = SafeGetMetadataString(langTag);
                        if (string.IsNullOrEmpty(language))
                            language = "未知";
                        
                        // 获取标题信息（如"普通话"、"英语"等）
                        AVDictionaryEntry* titleTag = ffmpeg.av_dict_get(stream->metadata, "title", null, 0);
                        string title = SafeGetMetadataString(titleTag);

                         _audioTracks.Add(new AudioTrackInfo
                        {
                            Index = i,
                            Language = language,
                            Codec = codecName,
                            Channels = channels,
                            Description = FormatAudioTrackDescription(title, language, codecName, channels)
                        });

                        if (_audioStreamIndex < 0)
                        {
                            _audioStreamIndex = i;
                            InitializeAudioDecoder(stream);
                        }

                    }
                    else if (codecParams->codec_type == AVMediaType.AVMEDIA_TYPE_SUBTITLE)
                    {
                        string codecName = codec != null ? Marshal.PtrToStringAnsi((IntPtr)codec->name) : null;
                        int disposition = stream->disposition;
                        bool isForced = (disposition & ffmpeg.AV_DISPOSITION_FORCED) != 0;
                        string language = "未知";
                    
                        AVDictionaryEntry* langTag = ffmpeg.av_dict_get(stream->metadata, "language", null, 0);
                        if (langTag != null && langTag->value != null)
                        {
                            language = Marshal.PtrToStringUTF8((IntPtr)langTag->value);
                            if (string.IsNullOrEmpty(language))
                                language = "未知";
                        }

                        // 获取字幕标题
                        AVDictionaryEntry* titleTag = ffmpeg.av_dict_get(stream->metadata, "title", null, 0);
                        string title = titleTag != null && titleTag->value != null ? 
                            Marshal.PtrToStringUTF8((IntPtr)titleTag->value) : null;

                        // 生成显示名称：序号从1开始，包含语言信息
                        int displayIndex = _subtitleTracks.Count;
                        string displayName;
                        string languageDisplay = GetLanguageDisplayName(language);
                        
                        if (!string.IsNullOrEmpty(title))
                        {
                            displayName = $"{displayIndex}. {title} ({languageDisplay})";
                        }
                        else
                        {
                            displayName = $"{displayIndex}. {languageDisplay}";
                        }
                        
                        if (isForced)
                        {
                            displayName = "[强制] " + displayName;
                        }

                        _subtitleTracks.Add(new SubtitleTrackInfo
                        {
                            Index = i,
                            Language = language,
                            Codec = codecName,
                            IsForced = isForced,
                            Description = displayName
                        });

                        if (_subtitleStreamIndex < 0 && !isForced)
                        {
                            _subtitleStreamIndex = i;
                            InitializeSubtitleDecoder(stream);
                        }
                    }
                }

                // 计算视频时长（在流索引初始化之后）
                _durationMs = (long)(fmtCtx->duration / (double)ffmpeg.AV_TIME_BASE * 1000);
                
                // 如果duration无效，尝试从视频流获取
                if (_durationMs <= 0 && _videoStreamIndex >= 0)
                {
                    var videoStream = fmtCtx->streams[_videoStreamIndex];
                    if (videoStream->duration != ffmpeg.AV_NOPTS_VALUE)
                    {
                        _durationMs = (long)(videoStream->duration * ffmpeg.av_q2d(videoStream->time_base) * 1000);
                    }
                }
                
                DebugLogger.WriteLine($"[FFmpeg] Video duration: {_durationMs}ms");

                DebugLogger.WriteLine($"[FFmpeg] Found {_audioTracks.Count} audio tracks, {_subtitleTracks.Count} subtitle tracks");
                foreach (var track in _audioTracks)
                {
                    DebugLogger.WriteLine($"[FFmpeg] Audio track {track.Index}: {track.Description}");
                }
                foreach (var track in _subtitleTracks)
                {
                    DebugLogger.WriteLine($"[FFmpeg] Subtitle track {track.Index}: {track.Description} Language:{track.Language}");
                }

                _videoFrame = (IntPtr)ffmpeg.av_frame_alloc();
                _audioFrame = (IntPtr)ffmpeg.av_frame_alloc();
                _packet = (IntPtr)ffmpeg.av_packet_alloc();

                if (_videoStreamIndex >= 0)
                {
                    DebugLogger.WriteLine($"[FFmpeg] ✅ 视频打开成功");
                    DebugLogger.WriteLine($"[FFmpeg] 分辨率: {_videoWidth}x{_videoHeight}");
                    DebugLogger.WriteLine($"[FFmpeg] 帧率: {_fps:F2}fps");
                    DebugLogger.WriteLine($"[FFmpeg] 时长: {TimeSpan.FromMilliseconds(_durationMs):hh\\:mm\\:ss}");
                    DebugLogger.WriteLine($"[FFmpeg] 音频轨道数: {_audioTracks.Count}");
                    DebugLogger.WriteLine($"[FFmpeg] 字幕轨道数: {_subtitleTracks.Count}");
                    DebugLogger.WriteLine($"[FFmpeg] ===== 文件打开完成 ===== ");
                    return true;
                }

                DebugLogger.WriteLine("[FFmpeg] ❌ 未找到视频流");
                return false;
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[FFmpeg] Open file error: {ex.Message}");
                return false;
            }
        }
        #endregion

        #region 专用解码器名称映射

        /// <summary>
        /// 根据硬件设备类型获取专用解码器名称
        /// </summary>
        /// <param name="codecId">编解码器ID</param>
        /// <param name="hwType">硬件设备类型</param>
        /// <returns>专用解码器名称，如果不支持则返回null</returns>
        private string? GetDedicatedDecoderName(AVCodecID codecId, AVHWDeviceType hwType)
        {
            string codecName = ffmpeg.avcodec_get_name(codecId);

            if (hwType == AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA)
                return GetCudaDecoderName(codecName);
            if (hwType == AVHWDeviceType.AV_HWDEVICE_TYPE_QSV)
                return GetQsvDecoderName(codecName);
            if (hwType == AVHWDeviceType.AV_HWDEVICE_TYPE_AMF)
                return GetAmfDecoderName(codecName);
            if (hwType == AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX)
                return GetVideoToolboxDecoderName(codecName);
            if (hwType == AVHWDeviceType.AV_HWDEVICE_TYPE_MEDIACODEC)
                return GetMediaCodecDecoderName(codecName);

            return null;
        }

        /// <summary>
        /// 获取NVIDIA CUDA专用解码器名称
        /// </summary>
        /// <param name="codecName">编解码器名称</param>
        /// <returns>CUDA解码器名称，如果不支持则返回null</returns>
        private string? GetCudaDecoderName(string codecName)
        {
            return codecName switch
            {
                "h264" => "h264_cuvid",
                "hevc" or "h265" => "hevc_cuvid",
                "mpeg2video" => "mpeg2_cuvid",
                "mpeg4" => "mpeg4_cuvid",
                "vp8" => "vp8_cuvid",
                "vp9" => "vp9_cuvid",
                "av1" => "av1_cuvid",
                _ => null
            };
        }

        /// <summary>
        /// 获取Intel QSV专用解码器名称
        /// </summary>
        /// <param name="codecName">编解码器名称</param>
        /// <returns>QSV解码器名称，如果不支持则返回null</returns>
        private string? GetQsvDecoderName(string codecName)
        {
            return codecName switch
            {
                "h264" => "h264_qsv",
                "hevc" or "h265" => "hevc_qsv",
                "mpeg2video" => "mpeg2_qsv",
                "vp8" => "vp8_qsv",
                "vp9" => "vp9_qsv",
                "av1" => "av1_qsv",
                _ => null
            };
        }

        /// <summary>
        /// 获取AMD AMF专用解码器名称
        /// </summary>
        /// <param name="codecName">编解码器名称</param>
        /// <returns>AMF解码器名称，如果不支持则返回null</returns>
        private string? GetAmfDecoderName(string codecName)
        {
            return codecName switch
            {
                "h264" => "h264_amf",
                "hevc" or "h265" => "hevc_amf",
                _ => null
            };
        }

        /// <summary>
        /// 获取macOS VideoToolbox专用解码器名称
        /// </summary>
        /// <param name="codecName">编解码器名称</param>
        /// <returns>VideoToolbox解码器名称，如果不支持则返回null</returns>
        private string? GetVideoToolboxDecoderName(string codecName)
        {
            return codecName switch
            {
                "h264" => "h264_videotoolbox",
                "hevc" or "h265" => "hevc_videotoolbox",
                "prores" => "prores_videotoolbox",
                _ => null
            };
        }

        /// <summary>
        /// 获取Android MediaCodec专用解码器名称
        /// </summary>
        /// <param name="codecName">编解码器名称</param>
        /// <returns>MediaCodec解码器名称，如果不支持则返回null</returns>
        private string? GetMediaCodecDecoderName(string codecName)
        {
            return codecName switch
            {
                "h264" => "h264_mediacodec",
                "hevc" or "h265" => "hevc_mediacodec",
                "mpeg4" => "mpeg4_mediacodec",
                "vp8" => "vp8_mediacodec",
                "vp9" => "vp9_mediacodec",
                _ => null
            };
        }

        #endregion
       

        #region 优化之后的,依赖专用解码器名称

        /// <summary>
        /// 解码器降级级别枚举
        /// 按优先级从高到低排列
        /// </summary>
        private enum DecoderFallbackLevel
        {
            /// <summary>
            /// 1. 硬件解码，原始分辨率（最佳质量和性能）
            /// </summary>
            HardwareNative,

            /// <summary>
            /// 2. 硬件解码 + 缩放（如果硬件支持缩放）
            /// </summary>
            HardwareDownscale,

            /// <summary>
            /// 3. 软件解码，原始分辨率
            /// </summary>
            SoftwareNative,

            /// <summary>
            /// 4. 软件解码 + 缩放（最低质量，用于性能受限场景）
            /// </summary>
            SoftwareDownscale
        }

        /// <summary>
        /// 初始化视频解码器
        /// 根据硬件支持情况和性能需求选择最佳解码方案
        /// </summary>
        /// <param name="stream">视频流</param>
        private unsafe void InitializeVideoDecoder(AVStream* stream)
        {
            var codecParams = stream->codecpar;
            AVCodec* codec = null;
            string? hwDecoderName = null;

            DebugLogger.WriteLine($"[FFmpeg] Video codec ID: {codecParams->codec_id}, Resolution: {codecParams->width}x{codecParams->height}");

            // 目标分辨率（如果需要降级，在这里设置）
            int targetWidth = codecParams->width;
            int targetHeight = codecParams->height;
            bool needDownscale = false;

            // 检查是否需要分辨率降级（例如性能限制）
            if (ShouldDownscaleResolution(codecParams->width, codecParams->height))
            {
                targetWidth = 1920;   // 1080p
                targetHeight = 1080;
                needDownscale = true;
                DebugLogger.WriteLine($"[FFmpeg] Will downscale from {codecParams->width}x{codecParams->height} to {targetWidth}x{targetHeight}");
                
                // 触发分辨率降级通知事件，让上层界面显示提示
                var downscaleInfo = new ResolutionDownscaleInfo
                {
                    Message = $"视频分辨率将从 {codecParams->width}x{codecParams->height} 降级到 {targetWidth}x{targetHeight} 以提升播放性能",
                    OriginalWidth = codecParams->width,
                    OriginalHeight = codecParams->height,
                    TargetWidth = targetWidth,
                    TargetHeight = targetHeight,
                    Reason = "性能优化：高分辨率视频需要更多系统资源"
                };
                ResolutionDownscale?.Invoke(this, downscaleInfo);
            }

            // 1. 检测硬件支持情况
            var hwSupportResult = CheckHardwareAccelerationSupport(codecParams->codec_id).GetAwaiter().GetResult();
            bool hwAvailable = hwSupportResult.Item1;
            AVHWDeviceType? hwType = hwSupportResult.Item2;

            // 2. 按优先级尝试各种解码方案
            DecoderFallbackLevel fallbackLevel = DecoderFallbackLevel.SoftwareNative;

            if (hwAvailable && hwType.HasValue)
            {
                _cachedDeviceType = hwType.Value;

                // 尝试方案1: 专用硬件解码器（原生分辨率）
                if (TryHardwareDecoder(stream, hwType.Value, codecParams->width, codecParams->height, out codec, out hwDecoderName))
                {
                    fallbackLevel = DecoderFallbackLevel.HardwareNative;
                    DebugLogger.WriteLine($"[FFmpeg] ✅ Hardware decoder (native) selected: {hwDecoderName}");
                }
                // 尝试方案2: 硬件解码 + 缩放（如果硬件支持）
                else if (needDownscale && TryHardwareDecoderWithDownscale(stream, hwType.Value, targetWidth, targetHeight, out codec, out hwDecoderName))
                {
                    fallbackLevel = DecoderFallbackLevel.HardwareDownscale;
                    DebugLogger.WriteLine($"[FFmpeg] ✅ Hardware decoder with downscale selected: {hwDecoderName}");
                }
                // 尝试方案3: 软件解码（原始分辨率）
                else if (TrySoftwareDecoder(stream, codecParams->width, codecParams->height, out codec))
                {
                    fallbackLevel = DecoderFallbackLevel.SoftwareNative;
                    DebugLogger.WriteLine($"[FFmpeg] ✅ Software decoder (native) selected");
                }
                // 尝试方案4: 软件解码 + 软件缩放
                else if (needDownscale && TrySoftwareDecoder(stream, targetWidth, targetHeight, out codec))
                {
                    fallbackLevel = DecoderFallbackLevel.SoftwareDownscale;
                    DebugLogger.WriteLine($"[FFmpeg] ✅ Software decoder with software downscale selected");
                }
                else
                {
                    DebugLogger.WriteLine("[FFmpeg] ❌ No decoder available");
                    PlaybackError?.Invoke(this, "无法初始化视频解码器：没有可用的解码器");
                    return;
                }
            }
            else
            {
                // 没有硬件加速，直接尝试软件解码
                if (TrySoftwareDecoder(stream, codecParams->width, codecParams->height, out codec))
                {
                    DebugLogger.WriteLine("[FFmpeg] ✅ Software decoder selected");
                }
                else if (needDownscale && TrySoftwareDecoder(stream, targetWidth, targetHeight, out codec))
                {
                    DebugLogger.WriteLine("[FFmpeg] ✅ Software decoder with downscale selected");
                }
                else
                {
                    DebugLogger.WriteLine("[FFmpeg] ❌ No decoder available");
                    PlaybackError?.Invoke(this, "无法初始化视频解码器：没有可用的解码器");
                    return;
                }
            }

            // 根据最终选择的方案设置解码模式和分辨率
            SetDecodingParameters(codec, stream, targetWidth, targetHeight, needDownscale);
        }

        /// <summary>
        /// 尝试使用专用硬件解码器
        /// </summary>
        private unsafe bool TryHardwareDecoder(AVStream* stream, AVHWDeviceType hwType, int width, int height,
                                               out AVCodec* codec, out string? decoderName)
        {
            codec = null;
            decoderName = null;

            var codecParams = stream->codecpar;
            decoderName = GetDedicatedDecoderName(codecParams->codec_id, hwType);

            if (string.IsNullOrEmpty(decoderName))
                return false;

            codec = ffmpeg.avcodec_find_decoder_by_name(decoderName);
            if (codec == null)
                return false;

            var vCodecCtx = ffmpeg.avcodec_alloc_context3(codec);
            if (vCodecCtx == null)
                return false;

            try
            {
                ffmpeg.avcodec_parameters_to_context(vCodecCtx, codecParams);
                vCodecCtx->thread_count =  Math.Max(1, Environment.ProcessorCount - 1);

                // 如果需要硬件缩放，可以尝试设置输出尺寸（取决于硬件支持）
                if (width != codecParams->width)
                {
                    // 某些硬件解码器支持直接设置输出尺寸
                    vCodecCtx->width = width;
                    vCodecCtx->height = height;
                }

                int ret = ffmpeg.avcodec_open2(vCodecCtx, codec, null);
                if (ret >= 0)
                {
                    _videoCodecContext = (IntPtr)vCodecCtx;
                    return true;
                }

                ffmpeg.avcodec_free_context(&vCodecCtx);
                return false;
            }
            catch
            {
                ffmpeg.avcodec_free_context(&vCodecCtx);
                return false;
            }
        }

        /// <summary>
        /// 尝试使用新式 hwaccel（软件解码器 + 硬件设备）
        /// </summary>
        private unsafe bool TryHwaccelDecoder(AVStream* stream, AVHWDeviceType hwType, out AVCodec* codec)
        {
            codec = null;
            var codecParams = stream->codecpar;

            // 找软件解码器
            codec = ffmpeg.avcodec_find_decoder(codecParams->codec_id);
            if (codec == null)
                return false;

            var vCodecCtx = ffmpeg.avcodec_alloc_context3(codec);
            if (vCodecCtx == null)
                return false;

            try
            {
                ffmpeg.avcodec_parameters_to_context(vCodecCtx, codecParams);
                vCodecCtx->thread_count = Math.Max(1, Environment.ProcessorCount - 1);

                // 创建硬件设备上下文
                AVBufferRef* hwDeviceCtx = null;
                int ret = ffmpeg.av_hwdevice_ctx_create(&hwDeviceCtx, hwType, null, null, 0);
                if (ret < 0)
                {
                    ffmpeg.avcodec_free_context(&vCodecCtx);
                    return false;
                }

                vCodecCtx->hw_device_ctx = ffmpeg.av_buffer_ref(hwDeviceCtx);
                ffmpeg.av_buffer_unref(&hwDeviceCtx);

                ret = ffmpeg.avcodec_open2(vCodecCtx, codec, null);
                if (ret >= 0)
                {
                    _videoCodecContext = (IntPtr)vCodecCtx;
                    return true;
                }

                ffmpeg.avcodec_free_context(&vCodecCtx);
                return false;
            }
            catch
            {
                ffmpeg.avcodec_free_context(&vCodecCtx);
                return false;
            }
        }

        /// <summary>
        /// 尝试软件解码器
        /// </summary>
        private unsafe bool TrySoftwareDecoder(AVStream* stream, int targetWidth, int targetHeight, out AVCodec* codec)
        {
            codec = null;
            var codecParams = stream->codecpar;

            codec = ffmpeg.avcodec_find_decoder(codecParams->codec_id);
            if (codec == null)
                return false;

            var vCodecCtx = ffmpeg.avcodec_alloc_context3(codec);
            if (vCodecCtx == null)
                return false;

            try
            {
                ffmpeg.avcodec_parameters_to_context(vCodecCtx, codecParams);
                vCodecCtx->thread_count = Math.Max(1, Environment.ProcessorCount - 1);

                // 如果目标分辨率与原分辨率不同，使用 options 设置低分辨率解码
                AVDictionary* options = null;
                if (targetWidth != codecParams->width || targetHeight != codecParams->height)
                {
                    // 设置低分辨率解码（如果编解码器支持）
                    // 注意：不是所有编解码器都支持 lowres
                    ffmpeg.av_dict_set(&options, "lowres", "1", 0);  // 1/2 分辨率
                                                                     // 或者直接设置输出尺寸（需要解码器支持）
                    vCodecCtx->width = targetWidth;
                    vCodecCtx->height = targetHeight;
                }

                int ret = ffmpeg.avcodec_open2(vCodecCtx, codec, &options);

                if (options != null)
                    ffmpeg.av_dict_free(&options);

                if (ret >= 0)
                {
                    _videoCodecContext = (IntPtr)vCodecCtx;
                    return true;
                }

                ffmpeg.avcodec_free_context(&vCodecCtx);
                return false;
            }
            catch
            {
                ffmpeg.avcodec_free_context(&vCodecCtx);
                return false;
            }
        }

        /// <summary>
        /// 判断是否需要降低分辨率以提升性能
        /// </summary>
        /// <param name="width">原始宽度</param>
        /// <param name="height">原始高度</param>
        /// <returns>如果需要降级返回true，否则返回false</returns>
        private bool ShouldDownscaleResolution(int width, int height)
        {
            // 根据性能、内存等条件判断是否需要降级
            long totalPixels = (long)width * height;

            // 1080p 像素数：1920x1080 = 2,073,600
            const long fullHD = 1920 * 1080;

            // 4K 像素数：3840x2160 = 8,294,400
            const long fourK = 3840 * 2160;

            // 超过 1080p 就降级，减少内存占用和CPU负载
            // 4K视频降级到1080p可以减少75%的内存占用（从24MB到6MB每帧）
            if (totalPixels > fullHD)
            {
                DebugLogger.WriteLine($"[FFmpeg] Resolution {width}x{height} ({totalPixels} pixels) exceeds 1080p, will downscale to 1080p for performance");
                return true;
            }

            return false;
        }

        /// <summary>
        /// 设置解码参数
        /// </summary>
        private unsafe void SetDecodingParameters(AVCodec* codec, AVStream* stream, int targetWidth, int targetHeight, bool needDownscale)
        {
            var vCodecCtx = (AVCodecContext*)_videoCodecContext;
            if (vCodecCtx == null) return;

            _currentDecoder = Marshal.PtrToStringAnsi((IntPtr)codec->name) ?? "unknown";

            // 使用实际解码器输出的分辨率（不是原始分辨率）
            int decoderWidth = vCodecCtx->width;
            int decoderHeight = vCodecCtx->height;

            DebugLogger.WriteLine($"[FFmpeg] Decoder output resolution: {decoderWidth}x{decoderHeight}");

            // FPS 计算
            if (vCodecCtx->framerate.num > 0 && vCodecCtx->framerate.den > 0)
            {
                _fps = (double)vCodecCtx->framerate.num / vCodecCtx->framerate.den;
            }
            else if (stream->avg_frame_rate.num > 0)
            {
                _fps = (double)stream->avg_frame_rate.num / stream->avg_frame_rate.den;
            }
            else
            {
                _fps = 30.0;
            }

            // 创建缩放上下文（如果需要降级或者像素格式转换）
            int swsWidth = decoderWidth;
            int swsHeight = decoderHeight;

            if (needDownscale)
            {
                swsWidth = targetWidth;
                swsHeight = targetHeight;
            }

            // 使用缩放宽高作为输出分辨率（减少内存占用）
            _videoWidth = swsWidth;
            _videoHeight = swsHeight;
            DebugLogger.WriteLine($"[FFmpeg] Output resolution (after downscale): {_videoWidth}x{_videoHeight}");

            _swsContext = (IntPtr)ffmpeg.sws_getContext(
                decoderWidth, decoderHeight, vCodecCtx->pix_fmt,
                swsWidth, swsHeight, AVPixelFormat.AV_PIX_FMT_BGR24,
                1, null, null, null);

            _rgbBuffer = new byte[swsWidth * swsHeight * 3];
            UpdateBufferSize(_rgbBuffer.Length);
        }
        /// <summary>
        /// 尝试使用 hwaccel + 硬件缩放（新式模式）
        /// </summary>
        private unsafe bool TryHwaccelWithDownscale(AVStream* stream, AVHWDeviceType hwType,
                                                     int targetWidth, int targetHeight,
                                                     out AVCodec* codec)
        {
            codec = null;
            var codecParams = stream->codecpar;

            // 1. 找软件解码器
            codec = ffmpeg.avcodec_find_decoder(codecParams->codec_id);
            if (codec == null)
                return false;

            var vCodecCtx = ffmpeg.avcodec_alloc_context3(codec);
            if (vCodecCtx == null)
                return false;

            try
            {
                ffmpeg.avcodec_parameters_to_context(vCodecCtx, codecParams);
                vCodecCtx->thread_count = Math.Max(1, Environment.ProcessorCount - 1);

                // 2. 创建硬件设备
                AVBufferRef* hwDeviceCtx = null;
                int ret = ffmpeg.av_hwdevice_ctx_create(&hwDeviceCtx, hwType, null, null, 0);
                if (ret < 0)
                {
                    ffmpeg.avcodec_free_context(&vCodecCtx);
                    return false;
                }

                vCodecCtx->hw_device_ctx = ffmpeg.av_buffer_ref(hwDeviceCtx);

                // 3. 设置输出尺寸（如果是 CUDA，可以通过 options 传递）
                AVDictionary* options = null;
                if (hwType == AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA)
                {
                    ffmpeg.av_dict_set(&options, "width", targetWidth.ToString(), 0);
                    ffmpeg.av_dict_set(&options, "height", targetHeight.ToString(), 0);
                }

                ret = ffmpeg.avcodec_open2(vCodecCtx, codec, &options);

                if (options != null)
                    ffmpeg.av_dict_free(&options);
                ffmpeg.av_buffer_unref(&hwDeviceCtx);

                if (ret >= 0)
                {
                    _videoCodecContext = (IntPtr)vCodecCtx;
                    DebugLogger.WriteLine($"[HW] Hwaccel downscale: output {vCodecCtx->width}x{vCodecCtx->height}");
                    return true;
                }

                ffmpeg.avcodec_free_context(&vCodecCtx);
                return false;
            }
            catch
            {
                ffmpeg.avcodec_free_context(&vCodecCtx);
                return false;
            }
        }
        /// <summary>
        /// 尝试使用硬件解码器并直接输出缩放后的画面
        /// </summary>
        private unsafe bool TryHardwareDecoderWithDownscale(AVStream* stream, AVHWDeviceType hwType,
                                                             int targetWidth, int targetHeight,
                                                             out AVCodec* codec, out string? decoderName)
        {
            codec = null;
            decoderName = null;

            var codecParams = stream->codecpar;
            decoderName = GetDedicatedDecoderName(codecParams->codec_id, hwType);

            if (string.IsNullOrEmpty(decoderName))
                return false;

            codec = ffmpeg.avcodec_find_decoder_by_name(decoderName);
            if (codec == null)
                return false;

            var vCodecCtx = ffmpeg.avcodec_alloc_context3(codec);
            if (vCodecCtx == null)
                return false;

            try
            {
                // 复制参数
                ffmpeg.avcodec_parameters_to_context(vCodecCtx, codecParams);
                vCodecCtx->thread_count = Math.Max(1, Environment.ProcessorCount - 1);

                // 方法1: 尝试通过 AVDictionary 传递缩放参数
                AVDictionary* options = null;

                // 不同硬件有不同的缩放参数名
                bool downscaleSupported = false;

                switch (hwType)
                {
                    case AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA:
                        // NVIDIA CUDA 解码器支持通过参数设置输出尺寸
                        ffmpeg.av_dict_set(&options, "output_width", targetWidth.ToString(), 0);
                        ffmpeg.av_dict_set(&options, "output_height", targetHeight.ToString(), 0);
                        // 可选：设置缩放算法
                        ffmpeg.av_dict_set(&options, "scaling", "fast", 0); // fast, default, high
                        downscaleSupported = true;
                        DebugLogger.WriteLine($"[HW] Trying CUDA hardware downscale to {targetWidth}x{targetHeight}");
                        break;

                    case AVHWDeviceType.AV_HWDEVICE_TYPE_QSV:
                        // Intel QSV 解码器
                        ffmpeg.av_dict_set(&options, "width", targetWidth.ToString(), 0);
                        ffmpeg.av_dict_set(&options, "height", targetHeight.ToString(), 0);
                        downscaleSupported = true;
                        DebugLogger.WriteLine($"[HW] Trying QSV hardware downscale to {targetWidth}x{targetHeight}");
                        break;

                    case AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA:
                    case AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2:
                        // D3D11VA/DXVA2 通常不直接支持解码时缩放，需要通过其他方式
                        // 可以尝试使用 hardware context 配合 filter
                        downscaleSupported = false;
                        break;

                    default:
                        downscaleSupported = false;
                        break;
                }

                int ret;
                if (downscaleSupported)
                {
                    ret = ffmpeg.avcodec_open2(vCodecCtx, codec, &options);
                    if (options != null)
                        ffmpeg.av_dict_free(&options);

                    if (ret >= 0)
                    {
                        // 检查实际输出尺寸
                        DebugLogger.WriteLine($"[HW] Hardware decoder output: {vCodecCtx->width}x{vCodecCtx->height}");

                        // 如果硬件没有缩放成功，输出的可能还是原尺寸
                        if (vCodecCtx->width == targetWidth && vCodecCtx->height == targetHeight)
                        {
                            _videoCodecContext = (IntPtr)vCodecCtx;
                            return true;
                        }
                        else
                        {
                            DebugLogger.WriteLine($"[HW] Hardware decoder ignored downscale request, output is {vCodecCtx->width}x{vCodecCtx->height}");
                            ffmpeg.avcodec_free_context(&vCodecCtx);
                            return false;
                        }
                    }
                }
                else
                {
                    ret = ffmpeg.avcodec_open2(vCodecCtx, codec, null);
                }

                if (ret >= 0 && !downscaleSupported)
                {
                    // 硬件不支持解码时缩放，但解码器打开成功
                    // 需要后续通过硬件 filter 或软件缩放着处理
                    _videoCodecContext = (IntPtr)vCodecCtx;
                    DebugLogger.WriteLine($"[HW] Hardware decoder opened, but downscale will be handled by filter");
                    return true;
                }

                ffmpeg.avcodec_free_context(&vCodecCtx);
                return false;
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[HW] Hardware downscale exception: {ex.Message}");
                ffmpeg.avcodec_free_context(&vCodecCtx);
                return false;
            }
        }
        #endregion

        #region 用新的方法, 通过专用解码器或者 hwaccel 来实现硬件加速
        /// <summary>
        /// 用新的方法, 通过专用解码器或者 hwaccel 来实现硬件加速
        /// </summary>
        /// <param name="stream"></param>
        private unsafe void NewInitializeVideoDecoder(AVStream* stream)
        {
            var codecParams = stream->codecpar;
            AVCodec* codec = null;

            DebugLogger.WriteLine($"[FFmpeg] Video codec ID: {codecParams->codec_id}, Resolution: {codecParams->width}x{codecParams->height}");

            int targetWidth = codecParams->width;
            int targetHeight = codecParams->height;
            bool needDownscale = false; // 你的降分辨率逻辑

            // 1. 检测硬件加速支持
            var hwSupport = CheckHardwareAccelerationSupport(codecParams->codec_id).GetAwaiter().GetResult();
            bool hwAvailable = hwSupport.Item1 && (_decodeMode == DecodeMode.Auto || _decodeMode == DecodeMode.Hardware);
            AVHWDeviceType? hwType = hwSupport.Item2;
            DebugLogger.WriteLine($"[FFmpeg] 硬件加速可用: {hwSupport.Item1}, 硬件类型: {hwSupport.Item2}");
            // 硬件设备上下文（所有路径统一使用）
            AVBufferRef* hwDeviceCtx = null;

            if (hwAvailable && hwType.HasValue)
            {
                // 必须使用软件解码器 + 硬件设备上下文（新式 hwaccel）
                codec = ffmpeg.avcodec_find_decoder(codecParams->codec_id);
                if (codec != null)
                {
                    try
                    { 
                        if (CurrentD3dModel == D3DMode.D3D11)
                        {
                            // 2. 分配硬件设备上下文缓冲区
                            hwDeviceCtx = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
                            if (hwDeviceCtx == null)
                                throw new Exception("av_hwdevice_ctx_alloc 失败");

                            var hwCtx = (AVHWDeviceContext*)hwDeviceCtx->data;
                            var d3dCtx = (AVD3D11VADeviceContext*)hwCtx->hwctx;

                            // 3. 获取共享的 D3D11 设备（必须与 VideoRenderer 同一设备）
                            var  device = _d3d11Device; // 你之前通过 VideoRenderer.GetDevice() 获取并存储到 _d3dDevice

                            if (device == null)
                                DebugLogger.WriteLine("[FFmpeg] 共享 D3D11 设备为 null，无法初始化硬件");
                            // 正确增加引用计数（COM 方式）
                            Marshal.AddRef(device.NativePointer);
                            d3dCtx->device = (ID3D11Device*)device.NativePointer;

                            // 同样对 device_context 增加引用 d3d11用
                            var ctx = device.ImmediateContext;
                            Marshal.AddRef(ctx.NativePointer);
                            d3dCtx->device_context = (ID3D11DeviceContext*)ctx.NativePointer;
                            // 4. 初始化硬件设备上下文
                            int ret = ffmpeg.av_hwdevice_ctx_init(hwDeviceCtx);
                            if (ret < 0)
                            {
                                // 如果失败，别忘了释放引用（因为我们已经 AddRef 了）
                                //  Marshal.Release(device.NativePointer);
                                ffmpeg.av_buffer_unref(&hwDeviceCtx);
                                throw new Exception($"av_hwdevice_ctx_init 失败: {ret}");
                            }
                        }
                        else if (CurrentD3dModel == D3DMode.D3D9)
                        { 
                           var   device = _d3d9Device;  
                            if (device == null)
                                DebugLogger.WriteLine("[FFmpeg] 共享 D3D9 设备为 null，无法初始化硬件");
                            var _decoderInit = new D3D9DecoderInitializer();
                            _decoderInit.Initialize(device);
                            hwDeviceCtx = _decoderInit.HardwareDeviceContext;

                            _decoderInit.Dispose();
                        }
                        if (CurrentD3dModel == D3DMode.D3D12)
                        {

                            // 1. 分配硬件设备上下文
                            // 注意：设备类型常量应为 AV_HWDEVICE_TYPE_D3D12VA
                            hwDeviceCtx = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D12VA);
                            if (hwDeviceCtx == null)
                                throw new Exception("Failed to alloc D3D12 HW device context");

                            // 2. 获取 AVHWDeviceContext 并填入设备指针
                            AVHWDeviceContext* deviceCtx = (AVHWDeviceContext*)hwDeviceCtx->data;
                            // 注意：上下文结构体类型应为 AVD3D12VADeviceContext
                            var d3d12Ctx = (AVD3D12VADeviceContext*)deviceCtx->hwctx;
                            // 设置 device 字段（必须）
                            var device = _d3d12Device; // 你之前通过 VideoRenderer.GetDevice() 获取并存储到 _d3dDevice 
                            if (device == null)
                                DebugLogger.WriteLine("[FFmpeg] 共享 D3D12 设备为 null，无法初始化硬件");
                            if (device != null && device.DeviceRemovedReason.Failure)
                            {
                                DebugLogger.WriteLine($"设备移除原因: 0x{device.DeviceRemovedReason.Code:X8}");
                            }
                            d3d12Ctx->device = device.NativePointer;
                             d3d12Ctx->resource_flags = 0;
                            d3d12Ctx->heap_flags = 0;                            // 注意：不需要也不能设置 command_queue 字段
                            // video_device 字段可以留空，让 FFmpeg 在初始化时自动填充

                            // 3. 初始化设备上下文
                            int ret = ffmpeg.av_hwdevice_ctx_init(hwDeviceCtx);
                            if (ret < 0)
                            {
                                ffmpeg.av_buffer_unref(&hwDeviceCtx);
                                throw new Exception($"Failed to init D3D12 HW device context: {ret}");
                            }

                            DebugLogger.WriteLine("FFmpeg D3D12 硬件设备上下文已创建");
                        }

                        // 硬件设备上下文创建成功
                        if (_decodeMode == DecodeMode.Auto)
                            _decodeMode = DecodeMode.Hardware;
                        DebugLogger.WriteLine($"[FFmpeg] ✅ D3D11VA 硬件设备就绪，解码模式: {_decodeMode}");
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.WriteLine($"[FFmpeg] ❌ 硬件初始化失败: {ex.Message}，回退到软件解码");
                        // 清理已分配的资源
                        if (hwDeviceCtx != null)
                        {
                            ffmpeg.av_buffer_unref(&hwDeviceCtx);
                            hwDeviceCtx = null;
                        }
                        codec = null;
                    }
                }
            }

            // 5. 如果硬件不可用或创建失败，回退到软件解码
            if (codec == null)
            {
                _decodeMode = DecodeMode.Software;
                codec = ffmpeg.avcodec_find_decoder(codecParams->codec_id);
                DebugLogger.WriteLine("[FFmpeg] 使用软件解码");
            }

            if (codec == null)
            {
                DebugLogger.WriteLine("[FFmpeg] 未找到视频解码器");
                return;
            }

            // 6. 分配解码器上下文
            var vCodecCtx = ffmpeg.avcodec_alloc_context3(codec);
            if (vCodecCtx == null)
            {
                DebugLogger.WriteLine("[FFmpeg] 分配解码器上下文失败");
                goto cleanup_hwdevice;
            }

            ffmpeg.avcodec_parameters_to_context(vCodecCtx, codecParams);
            vCodecCtx->thread_count = Math.Max(1, Environment.ProcessorCount - 1);

            // 硬件加速绑定
            if (hwDeviceCtx != null && (_decodeMode == DecodeMode.Auto || _decodeMode == DecodeMode.Hardware))
            {
                vCodecCtx->hw_device_ctx = ffmpeg.av_buffer_ref(hwDeviceCtx);
                // 不需要额外设置 "hwaccel" 选项
            }

            // 打开解码器
            AVDictionary* options = null;
            int openRet = ffmpeg.avcodec_open2(vCodecCtx, codec, &options);
            ffmpeg.av_dict_free(&options);

            if (openRet < 0)
            {
                DebugLogger.WriteLine("[FFmpeg] 打开视频解码器失败");
                ffmpeg.avcodec_free_context(&vCodecCtx);
                goto cleanup_hwdevice;
            }

            _videoCodecContext = (IntPtr)vCodecCtx;

            // 保存硬件设备上下文引用（可能后续用到）
            if (hwDeviceCtx != null)
                _hwDeviceContext = (IntPtr)hwDeviceCtx;
            else
                _hwDeviceContext = IntPtr.Zero;

            _currentDecoder = Marshal.PtrToStringAnsi((IntPtr)codec->name) ?? "unknown";
            int decoderWidth = vCodecCtx->width;
            int decoderHeight = vCodecCtx->height;

            DebugLogger.WriteLine($"[FFmpeg] Decoder: {_currentDecoder}, Output: {decoderWidth}x{decoderHeight}");

            // FPS 设置
            if (vCodecCtx->framerate.num > 0 && vCodecCtx->framerate.den > 0)
                _fps = (double)vCodecCtx->framerate.num / vCodecCtx->framerate.den;
            else if (stream->avg_frame_rate.num > 0)
                _fps = (double)stream->avg_frame_rate.num / stream->avg_frame_rate.den;
            else
                _fps = 30.0;

            _videoWidth = decoderWidth;
            _videoHeight = decoderHeight;

            DebugLogger.WriteLine($"[FFmpeg] 输出分辨率: {_videoWidth}x{_videoHeight}");

            // 分配 RGB 缓冲区（如果后续需要）
            _rgbBuffer = new byte[decoderWidth * decoderHeight * 3];
            UpdateBufferSize(_rgbBuffer.Length);
            return;

        cleanup_hwdevice:
            if (hwDeviceCtx != null)
            {
                ffmpeg.av_buffer_unref(&hwDeviceCtx);
                _hwDeviceContext = IntPtr.Zero;
            }
        }
        private unsafe AVFrame* GetHwFrame(AVFrame* hwFrame)
        {
            // 如果解码器输出了硬件帧，需要转换到 CPU
            if (hwFrame->format == (int)AVPixelFormat.AV_PIX_FMT_CUDA ||
                hwFrame->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11)
            {
                // 创建软件帧
                AVFrame* swFrame = ffmpeg.av_frame_alloc();

                // 从硬件帧转换到软件帧
                int ret = ffmpeg.av_hwframe_transfer_data(swFrame, hwFrame, 0);
                if (ret < 0)
                {
                    ffmpeg.av_frame_free(&swFrame);
                    return hwFrame; // 返回原始帧
                }

                // 复制其他属性
                swFrame->pts = hwFrame->pts;
                swFrame->pkt_dts = hwFrame->pkt_dts;

                ffmpeg.av_frame_unref(hwFrame);
                ffmpeg.av_frame_free(&hwFrame);

                return swFrame;
            }

            return hwFrame;
        }
        #endregion

        /// <summary>
        /// 根据解码器名称获取硬件加速类型
        /// </summary>
        /// <param name="decoderName">解码器名称</param>
        /// <returns>硬件加速类型字符串（cuda/dxva2/d3d11va/qsv/auto）</returns>
        private string GetHwAccelType(string decoderName)
        {
            if (decoderName.Contains("cuvid") || decoderName.Contains("cuda"))
                return "cuda";
            if (decoderName.Contains("dxva2"))
                return "dxva2";
            if (decoderName.Contains("d3d11va"))
                return "d3d11va";
            if (decoderName.Contains("qsv"))
                return "qsv";
            return "auto";
        }

        /// <summary>
        /// 初始化音频解码器
        /// 配置音频解码器上下文、重采样器和音频播放器
        /// </summary>
        /// <param name="stream">音频流</param>
        private unsafe void InitializeAudioDecoder(AVStream* stream)
        {
            DebugLogger.WriteLine("[FFmpeg] Initializing audio decoder...");


            // 空指针检查（停止播放时 _formatContext 可能已释放，stream 无效）
            if (stream == null)
            {
                DebugLogger.WriteLine("[FFmpeg] InitializeAudioDecoder 失败: stream 为 null");
                return;
            }
            var codecParams = stream->codecpar;
            if (codecParams == null)
            {
                DebugLogger.WriteLine("[FFmpeg] InitializeAudioDecoder 失败: codecParams 为 null");
                return;
            }

            var codec = ffmpeg.avcodec_find_decoder(codecParams->codec_id);
            if (codec == null)
            {
                DebugLogger.WriteLine("[FFmpeg] Audio codec not found");
                return;
            }
            
            string codecName = Marshal.PtrToStringAnsi((IntPtr)codec->name) ?? "unknown";
            DebugLogger.WriteLine($"[FFmpeg] Found audio codec: {codecName}");

            var aCodecCtx = ffmpeg.avcodec_alloc_context3(codec);
            lock (_audioCodecLock)
            {
                _audioCodecContext = (IntPtr)aCodecCtx;
            }
            if (aCodecCtx == null)
            {
                DebugLogger.WriteLine("[FFmpeg] Failed to allocate audio codec context");
                return;
            }

            ffmpeg.avcodec_parameters_to_context(aCodecCtx, codecParams);

            if (ffmpeg.avcodec_open2(aCodecCtx, codec, null) < 0)
            {
                DebugLogger.WriteLine("[FFmpeg] Failed to open audio codec");
                return;
            }

            lock (_audioCodecLock)
            {
                _audioFrame = (IntPtr)ffmpeg.av_frame_alloc();
            }

            try
            {
                var swr = ffmpeg.swr_alloc();
                lock (_audioCodecLock)
                {
                    _swrContext = (IntPtr)swr;
                }
                if (swr == null)
                    return;

                string inChannelLayout = codecParams->ch_layout.nb_channels == 6 ? "5.1" : 
                                         codecParams->ch_layout.nb_channels == 8 ? "7.1" : 
                                         codecParams->ch_layout.nb_channels == 1 ? "mono" : "stereo";

                ffmpeg.av_opt_set(swr, "in_chlayout", inChannelLayout, 0);
                ffmpeg.av_opt_set(swr, "in_sample_rate", codecParams->sample_rate.ToString(), 0);
                ffmpeg.av_opt_set(swr, "in_sample_fmt", GetSampleFormatName((AVSampleFormat)codecParams->format), 0);
                ffmpeg.av_opt_set(swr, "out_chlayout", "stereo", 0);
                ffmpeg.av_opt_set(swr, "out_sample_rate", codecParams->sample_rate.ToString(), 0);
                ffmpeg.av_opt_set(swr, "out_sample_fmt", "s16", 0);
                ffmpeg.av_opt_set(swr, "resampler", "soxr", 0);

                var ret = ffmpeg.swr_init(swr);
                if (ret < 0)
                {
                    ffmpeg.swr_free(&swr);
                    _swrContext = IntPtr.Zero;
                }

                DebugLogger.WriteLine($"[FFmpeg] Audio resampler initialized - Input: {codecParams->sample_rate}Hz, {codecParams->ch_layout.nb_channels} channels, Output: {codecParams->sample_rate}Hz, stereo");
                
                InitializeAudioPlayer(codecParams->sample_rate);
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[FFmpeg] Audio initialization failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 初始化字幕解码器
        /// 配置字幕解码器上下文
        /// </summary>
        /// <param name="stream">字幕流</param>
        private unsafe void InitializeSubtitleDecoder(AVStream* stream)
        {
            try
            {
                DebugLogger.WriteLine("[FFmpeg] Initializing subtitle decoder...");

                // 释放旧的解码器上下文，避免泄漏和混用
                if (_subtitleCodecContext != IntPtr.Zero)
                {
                    var oldCtx = (AVCodecContext*)_subtitleCodecContext;
                    ffmpeg.avcodec_free_context(&oldCtx);
                    _subtitleCodecContext = IntPtr.Zero;
                }

                var codecParams = stream->codecpar;
                var codec = ffmpeg.avcodec_find_decoder(codecParams->codec_id);
                if (codec == null)
                {
                    DebugLogger.WriteLine("[FFmpeg] Subtitle codec not found");
                    return;
                }

                string codecName = Marshal.PtrToStringAnsi((IntPtr)codec->name) ?? "unknown";
                DebugLogger.WriteLine($"[FFmpeg] Found subtitle codec: {codecName}");

                var sCodecCtx = ffmpeg.avcodec_alloc_context3(codec);
                if (sCodecCtx == null)
                {
                    DebugLogger.WriteLine("[FFmpeg] Failed to allocate subtitle codec context");
                    return;
                }

                // 将流参数复制到解码器上下文
                int ret = ffmpeg.avcodec_parameters_to_context(sCodecCtx, codecParams);
                if (ret < 0)
                {
                    DebugLogger.WriteLine("[FFmpeg] Failed to copy subtitle codec parameters");
                    ffmpeg.avcodec_free_context(&sCodecCtx);
                    return;
                }

                // 打开解码器
                ret = ffmpeg.avcodec_open2(sCodecCtx, codec, null);
                if (ret < 0)
                {
                    DebugLogger.WriteLine("[FFmpeg] Failed to open subtitle codec");
                    ffmpeg.avcodec_free_context(&sCodecCtx);
                    return;
                }

                _subtitleCodecContext = (IntPtr)sCodecCtx;
                DebugLogger.WriteLine("[FFmpeg] Subtitle decoder initialized successfully");
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[FFmpeg] Subtitle initialization failed: {ex.Message}");
            }
        }


        private unsafe void _InitializeAudioDecoder(AVStream* stream)
        {
            var codecParams = stream->codecpar;
            var codec = ffmpeg.avcodec_find_decoder(codecParams->codec_id);
            if (codec == null) return;

            var aCodecCtx = ffmpeg.avcodec_alloc_context3(codec);
            lock (_audioCodecLock)
            {
                _audioCodecContext = (IntPtr)aCodecCtx;
            }

            ffmpeg.avcodec_parameters_to_context(aCodecCtx, codecParams);
            ffmpeg.avcodec_open2(aCodecCtx, codec, null);

            lock (_audioCodecLock)
            {
                _audioFrame = (IntPtr)ffmpeg.av_frame_alloc();
            }

            // 初始化重采样器
            SwrContext* swr = ffmpeg.swr_alloc();
            if (swr != null)
            {
                // 目标格式：48000Hz, 立体声, 16-bit
                AVChannelLayout targetLayout;
                ffmpeg.av_channel_layout_default(&targetLayout, 2);

                ffmpeg.av_opt_set_chlayout(swr, "in_channel_layout", &aCodecCtx->ch_layout, 0);
                ffmpeg.av_opt_set_int(swr, "in_sample_rate", aCodecCtx->sample_rate, 0);
                ffmpeg.av_opt_set_sample_fmt(swr, "in_sample_fmt", aCodecCtx->sample_fmt, 0);
                ffmpeg.av_opt_set_chlayout(swr, "out_channel_layout", &targetLayout, 0);
                ffmpeg.av_opt_set_int(swr, "out_sample_rate", 48000, 0);
                ffmpeg.av_opt_set_sample_fmt(swr, "out_sample_fmt", AVSampleFormat.AV_SAMPLE_FMT_S16, 0);

                ffmpeg.swr_init(swr);
                lock (_audioCodecLock)
                {
                    _swrContext = (IntPtr)swr;
                }
            }

            // 初始化播放器
            var waveFormat = new WaveFormat(48000, 16, 2);
            _audioBuffer = new BufferedWaveProvider(waveFormat);
            _audioBuffer.BufferDuration = TimeSpan.FromSeconds(3);
            _audioBuffer.DiscardOnBufferOverflow = true;
            _audioOutput = new WaveOutEvent();
            _audioOutput.NumberOfBuffers = 4;
            _audioOutput.DesiredLatency = 500;
            _audioOutput.Init(_audioBuffer);
         }

       
         

        
        /// <summary>
        /// 获取当前音频播放位置（毫秒）
        /// </summary>
        /// <returns></returns>
        private long GetCurrentPositionMs()
        {
            if (_audioOutput != null)
            {
                // 或者通过位置字节计算
                long positionBytes = _audioOutput.GetPosition();
                long currentMs = positionBytes * 1000 / _audioOutput.OutputWaveFormat.AverageBytesPerSecond;
                return currentMs;
            }
            return 0;
        }
        /// <summary>
        /// 初始化音频播放器
        /// 创建音频缓冲区和输出设备，配置播放参数
        /// </summary>
        /// <param name="sampleRate">采样率（Hz）</param>
        private void InitializeAudioPlayer(int sampleRate)
        {
            try
            {
                _sampleRate = sampleRate;
                var waveFormat = new WaveFormat(sampleRate, 16, 2);
                _audioBuffer = new BufferedWaveProvider(waveFormat);
                _audioBuffer.BufferDuration = TimeSpan.FromSeconds(3);
                _audioBuffer.DiscardOnBufferOverflow = true;

                _audioOutput = new WaveOutEvent();
                _audioOutput.NumberOfBuffers = 4;
                _audioOutput.DesiredLatency = 500;
                 _audioOutput.Init(_audioBuffer); 

                DebugLogger.WriteLine($"[FFmpeg] Audio player initialized - SampleRate: {sampleRate}, Latency: 500ms");
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[FFmpeg] Failed to initialize audio player: {ex.Message}");
            }
        }

        /// <summary>
        /// 重新初始化音频播放器
        /// 在Seek操作后调用，重置音频缓冲区和输出设备以保持音视频同步
        /// </summary>
        private void ReinitializeAudioPlayer()
        {
            // 1. 停止并释放旧的播放器
            if (_audioOutput != null)
            {
                _audioOutput.Stop();
                _audioOutput.Dispose();
                _audioOutput = null;
            }

            // 2. 释放旧的缓冲区
            if (_audioBuffer != null)
            {
                _audioBuffer.ClearBuffer();
                _audioBuffer = null;
            }

            // 3. 重新创建
            var waveFormat = new WaveFormat(_sampleRate, 16, 2);
            _audioBuffer = new BufferedWaveProvider(waveFormat);
            _audioBuffer.BufferDuration = TimeSpan.FromSeconds(3);
            _audioBuffer.DiscardOnBufferOverflow = true;

            _audioOutput = new WaveOutEvent();
            _audioOutput.NumberOfBuffers = 4;
            _audioOutput.DesiredLatency = 500;
            _audioOutput.Init(_audioBuffer);
            _audioOutput.Play();  // 重新开始播放

            DebugLogger.WriteLine($"[Player] Audio player reinitialized for Seek");
        }

        /// <summary>
        /// 根据声道数获取声道布局名称
        /// </summary>
        /// <param name="channels">声道数</param>
        /// <returns>声道布局名称（mono/stereo/5.1）</returns>
        private static string GetChannelLayout(int channels)
        {
            return channels switch
            {
                1 => "mono",
                2 => "stereo",
                6 => "5.1",
                _ => "stereo"
            };
        }

        /// <summary>
        /// 获取采样格式名称
        /// </summary>
        /// <param name="format">采样格式枚举值</param>
        /// <returns>采样格式名称字符串</returns>
        private static string GetSampleFormatName(AVSampleFormat format)
        {
            return format switch
            {
                AVSampleFormat.AV_SAMPLE_FMT_U8 => "u8",
                AVSampleFormat.AV_SAMPLE_FMT_S16 => "s16",
                AVSampleFormat.AV_SAMPLE_FMT_S32 => "s32",
                AVSampleFormat.AV_SAMPLE_FMT_FLT => "flt",
                AVSampleFormat.AV_SAMPLE_FMT_DBL => "dbl",
                AVSampleFormat.AV_SAMPLE_FMT_U8P => "u8p",
                AVSampleFormat.AV_SAMPLE_FMT_S16P => "s16p",
                AVSampleFormat.AV_SAMPLE_FMT_S32P => "s32p",
                AVSampleFormat.AV_SAMPLE_FMT_FLTP => "fltp",
                AVSampleFormat.AV_SAMPLE_FMT_DBLP => "dblp",
                _ => "fltp"
            };
        }

        /// <summary>
        /// 解码循环主方法
        /// 负责持续读取媒体数据包并分发到音视频解码器
        /// </summary>
        /// <param name="ct">取消令牌，用于停止解码循环</param>
        private async Task DecodeLoopAsync(CancellationToken ct)
        {
            try
            {
                DebugLogger.WriteLine("[FFmpeg] ===== 解码循环开始 ===== ");
                int framesDropped = 0;
                const int MAX_CONSECUTIVE_DROPS = 5;

                while (!ct.IsCancellationRequested && _isPlaying)
                {
                    // 暂停状态等待
                    if (_isPaused)
                    {
                        await Task.Delay(20, ct).ConfigureAwait(false);
                        continue;
                    }

                    // 处理Seek请求
                    if (_pendingSeek && !_isSeeking)
                    {
                        DebugLogger.WriteLine($"[FFmpeg] 执行 Seek 到: {_pendingSeekTime}s");
                        double seekTs = _pendingSeekTime;
                        _pendingSeek = false;
                        _isSeeking = true;
                        framesDropped = 0;
                        TestExecuteSeekNow(seekTs);
                        continue;
                    }

                    // Seek进行中等待
                    if (_isSeeking)
                    {
                        await Task.Delay(1, ct).ConfigureAwait(false);
                        continue;
                    }

                    // 处理下一数据包
                    bool shouldBreak = ProcessNextPacket(ct, ref framesDropped, MAX_CONSECUTIVE_DROPS);
                    if (shouldBreak) break;
                }
            }
            catch (OperationCanceledException) 
            {
                DebugLogger.WriteLine("[FFmpeg] Decode loop cancelled");
            }
            catch (AccessViolationException ex)
            {
                string errorMsg = $"FFmpeg memory access violation: {ex.Message}";
                DebugLogger.WriteLine($"[FFmpeg] Critical error: {errorMsg}");
                PlaybackError?.Invoke(this, errorMsg);
                Environment.Exit(1);
            }
            catch (Exception ex)
            {
                string errorMsg = $"Decode error: {ex.Message}\nStack trace: {ex.StackTrace}";
                DebugLogger.WriteLine($"[FFmpeg] Error: {errorMsg}");
                PlaybackError?.Invoke(this, errorMsg);
            }
            finally
            {
                _isPlaying = false;
                DebugLogger.WriteLine("[FFmpeg] DecodeLoop finished");
                
                if (_playCts?.IsCancellationRequested == true)
                {
                    PlaybackEnded?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        #region 异步解码架构

        /// <summary>
        /// 读取数据包（unsafe方法）
        /// </summary>
        private unsafe int ReadPacket()
        {
            if (_formatContext == IntPtr.Zero) return -1;
            if (_packet == IntPtr.Zero) return -1;
            var fmtCtx = (AVFormatContext*)_formatContext;
            var pkt = (AVPacket*)_packet;
        

            try
            {
                return ffmpeg.av_read_frame(fmtCtx, pkt);
            }
            catch (AccessViolationException)
            {
                return -1;
            }
        }

        /// <summary>
        /// 获取数据包的流索引（unsafe方法）
        /// </summary>
        private unsafe int GetPacketStreamIndex()
        {
            if (_packet == IntPtr.Zero) return -1;
            var pkt = (AVPacket*)_packet;
            return pkt->stream_index;
        }

        /// <summary>
        /// 释放数据包（unsafe方法）
        /// </summary>
        private unsafe void UnrefPacket()
        {
            try
            {
                if (_packet == IntPtr.Zero) return;

                var pkt = (AVPacket*)_packet;
                if (pkt == null) return;

                // 检查是否已经释放（避免重复释放）
                // 注意：无法直接检查，只能通过标志控制

                ffmpeg.av_packet_unref(pkt);
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[错误] UnrefPacket 异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取音频播放位置（毫秒）
        /// </summary>
        private long GetAudioPlayPosition()
        {
            return (long)GetPlaybackClock();
        }

        /// <summary>
        /// 解复用循环 - 读取数据包并分发到音频/视频队列
        /// </summary>
        private async Task DemuxLoopAsync(CancellationToken ct)
        {
            try
            {
                DebugLogger.WriteLine("[FFmpeg] Demux loop started");
                int framesDropped = 0;

                while (!ct.IsCancellationRequested && _isPlaying)
                {
                    // 暂停状态等待
                    if (_isPaused)
                    {
                        await Task.Delay(20, ct).ConfigureAwait(false);
                        continue;
                    }

                    // 处理Seek请求
                    if (_pendingSeek && !_isSeeking)
                    {
                        DebugLogger.WriteLine($"[FFmpeg] Demux: Seek to {_pendingSeekTime}s");
                        double seekTs = _pendingSeekTime;
                        _pendingSeek = false;
                        _isSeeking = true;
                        framesDropped = 0;
                        
                        // 清空队列
                        while (_audioPacketQueue.Reader.TryRead(out _)) { }
                        while (_videoPacketQueue.Reader.TryRead(out _)) { }
                        while (_subtitlePacketQueue.Reader.TryRead(out _)) { }
                        
                        TestExecuteSeekNow(seekTs);
                        continue;
                    }

                    // Seek进行中等待
                    if (_isSeeking)
                    {
                        await Task.Delay(1, ct).ConfigureAwait(false);
                        continue;
                    }

                    // 读取数据包（调用unsafe方法）
                    int readResult = ReadPacket();
                    if (readResult < 0)
                    {
                        if (readResult == ffmpeg.AVERROR_EOF)
                        {
                            DebugLogger.WriteLine("[FFmpeg] Demux: End of file");
                            break;
                        }
                        await Task.Delay(10, ct).ConfigureAwait(false);
                        continue;
                    }

                    // 获取流索引（调用unsafe方法）
                    int streamIndex = GetPacketStreamIndex();
                    
                    // 分发到相应的队列
                    if (streamIndex == _audioStreamIndex)
                    {
                        await SendToAudioQueue(ct);
                    }
                    else if (streamIndex == _videoStreamIndex)
                    {
                        await SendToVideoQueue(ct);
                    }
                    else if (streamIndex == _subtitleStreamIndex && _subtitleStreamIndex >= 0)
                    {
                        await SendToSubtitleQueue(ct);
                    }

                    // 释放数据包（调用unsafe方法）
                    UnrefPacket();
                }

                // 标记队列完成
                _audioPacketQueue.Writer.Complete();
                _videoPacketQueue.Writer.Complete();
                _subtitlePacketQueue.Writer.Complete();
                DebugLogger.WriteLine("[FFmpeg] Demux loop finished");
            }
            catch (OperationCanceledException)
            {
                DebugLogger.WriteLine("[FFmpeg] Demux loop cancelled");
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[FFmpeg] Demux error: {ex.Message}");
            }
        }

        /// <summary>
        /// 将数据包发送到音频队列
        /// </summary>
        private async Task SendToAudioQueue(CancellationToken ct)
        {
            try
            {
                PacketData packetData = null;
                
                unsafe
                {
                    var fmtCtx = (AVFormatContext*)_formatContext;
                    var pkt = (AVPacket*)_packet;
                    var audioStream = fmtCtx->streams[_audioStreamIndex];
                    double timeBase = ffmpeg.av_q2d(audioStream->time_base);

                    // 复制数据包数据
                    byte[] data = new byte[pkt->size];
                    Marshal.Copy((IntPtr)pkt->data, data, 0, pkt->size);

                    packetData = new PacketData
                    {
                        Data = data,
                        Size = pkt->size,
                        PTS = pkt->pts,
                        StreamIndex = pkt->stream_index,
                        IsKeyFrame = (pkt->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0,
                        TimeBase = timeBase
                    };
                }

                if (packetData != null)
                {
                    await _audioPacketQueue.Writer.WriteAsync(packetData, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[FFmpeg] SendToAudioQueue error: {ex.Message}");
            }
        }

        /// <summary>
        /// 将数据包发送到视频队列
        /// </summary>
        private async Task SendToVideoQueue(CancellationToken ct)
        {
            try
            {
                PacketData packetData = null;
                
                unsafe
                {
                    var fmtCtx = (AVFormatContext*)_formatContext;
                    var pkt = (AVPacket*)_packet;
                    var videoStream = fmtCtx->streams[_videoStreamIndex];
                    double timeBase = ffmpeg.av_q2d(videoStream->time_base);

                    // 复制数据包数据
                    byte[] data = new byte[pkt->size];
                    Marshal.Copy((IntPtr)pkt->data, data, 0, pkt->size);

                    packetData = new PacketData
                    {
                        Data = data,
                        Size = pkt->size,
                        PTS = pkt->pts,
                        StreamIndex = pkt->stream_index,
                        IsKeyFrame = (pkt->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0,
                        TimeBase = timeBase
                    };
                }

                if (packetData != null)
                {
                    await _videoPacketQueue.Writer.WriteAsync(packetData, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[FFmpeg] SendToVideoQueue error: {ex.Message}");
            }
        }

        /// <summary>
        /// 将数据包发送到字幕队列
        /// </summary>
        private async Task SendToSubtitleQueue(CancellationToken ct)
        {
            try
            {
                PacketData packetData = null;
                
                unsafe
                {
                    var fmtCtx = (AVFormatContext*)_formatContext;
                    var pkt = (AVPacket*)_packet;
                    var subtitleStream = fmtCtx->streams[_subtitleStreamIndex];
                    double timeBase = ffmpeg.av_q2d(subtitleStream->time_base);

                    // 复制数据包数据
                    byte[] data = new byte[pkt->size];
                    Marshal.Copy((IntPtr)pkt->data, data, 0, pkt->size);

                    packetData = new PacketData
                    {
                        Data = data,
                        Size = pkt->size,
                        PTS = pkt->pts,
                        StreamIndex = pkt->stream_index,
                        IsKeyFrame = (pkt->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0,
                        TimeBase = timeBase
                    };
                }

                if (packetData != null)
                {
                    await _subtitlePacketQueue.Writer.WriteAsync(packetData, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[FFmpeg] SendToSubtitleQueue error: {ex.Message}");
            }
        }

        /// <summary>
        /// 音频解码循环 - 独立线程处理音频解码
        /// </summary>
        private async Task AudioDecodeLoopAsync(CancellationToken ct)
        {
            try
            {
                DebugLogger.WriteLine("[FFmpeg] Audio decode loop started");
                Thread.CurrentThread.Priority = ThreadPriority.Highest;

                while (!ct.IsCancellationRequested && _isPlaying)
                {
                    // 暂停状态等待
                    if (_isPaused)
                    {
                        await Task.Delay(20, ct).ConfigureAwait(false);
                        continue;
                    }

                    // 从队列获取数据包
                    if (!await _audioPacketQueue.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                        break;

                    while (_audioPacketQueue.Reader.TryRead(out var packetData))
                    {
                        // ✅ 添加停止检查
                        if (!_isPlaying || ct.IsCancellationRequested)
                        {
                            packetData.Dispose();
                            DebugLogger.WriteLine("[FFmpeg] Video decode stopped mid-packet");
                            return; // 或 break;
                        }
                        // 将数据包写入共享的 _packet 并调用同步解码方法
                        ProcessAudioPacketFromQueue(packetData);
                        packetData.Dispose();
                    }
                }

                DebugLogger.WriteLine("[FFmpeg] Audio decode loop finished");
            }
            catch (OperationCanceledException)
            {
                DebugLogger.WriteLine("[FFmpeg] Audio decode loop cancelled");
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[FFmpeg] Audio decode error: {ex.Message}");
            }
        }
   
        /// <summary>
        /// 处理队列中的音频数据包
        /// </summary>
        private unsafe void ProcessAudioPacketFromQueue(PacketData packetData)
        {
            // 创建AVPacket并填充数据
            AVPacket pkt;
            ffmpeg.av_init_packet(&pkt);
            pkt.data = (byte*)Marshal.AllocHGlobal(packetData.Size);
            pkt.size = packetData.Size;
            pkt.pts = packetData.PTS;
            pkt.stream_index = packetData.StreamIndex;

            try
            {
                Marshal.Copy(packetData.Data, 0, (IntPtr)pkt.data, packetData.Size);

                // 在获取锁之前检查是否正在切换音轨，避免死锁
                if (_audioTrackSwitchInProgress)
                {
                    return;
                }

                // 使用锁保护音频解码器上下文，防止切换音轨时释放导致崩溃
                lock (_audioCodecLock)
                {
                    if (_audioCodecContext == IntPtr.Zero || _audioFrame == IntPtr.Zero) return;

                    var aCodecCtx = (AVCodecContext*)_audioCodecContext;
                    var aFrm = (AVFrame*)_audioFrame;

                    int ret = ffmpeg.avcodec_send_packet(aCodecCtx, &pkt);
                    if (ret < 0) return;

                    while (true)
                    {
                        if (!_isPlaying)
                            break;
                        if (_audioTrackSwitchInProgress)
                        {
                            break;
                        }

                        ret = ffmpeg.avcodec_receive_frame(aCodecCtx, aFrm);
                        if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF) break;
                        if (ret < 0) break;

                        // 更新音频时钟
                        if (aFrm->pts != ffmpeg.AV_NOPTS_VALUE)
                        {
                            _audioClock = (long)(aFrm->pts * packetData.TimeBase * 1000);
                            if (_audioFirstPtsMs < 0)
                            {
                                DebugLogger.WriteLine($"[音频] 第一帧 PTS: {_audioClock}ms");
                                if (_audioClock > 0)
                                {
                                    _audioClock = 0;
                                    _isClockInitialized = false; 
                                }
                            }
                            _audioFirstPtsMs++;
                        }

                        // 等待缓冲区有空间，保持更大的缓冲避免卡顿
                        while (_audioBuffer != null && _audioBuffer.BufferedDuration.TotalMilliseconds > 500)
                        {
                            Thread.Sleep(5);
                        }

                        byte[] outBuffer = ConvertAudioFrame(aFrm);
                        if (outBuffer != null && outBuffer.Length > 0)
                        {
                            if (_audioBuffer != null && _audioBuffer.BufferedDuration.TotalMilliseconds < 2000)
                            {
                                _audioBuffer.AddSamples(outBuffer, 0, outBuffer.Length);
                            }
                        }
                        if(aFrm!=null)
                        ffmpeg.av_frame_unref(aFrm);
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[音频] ProcessAudioPacketFromQueue error: {ex.Message}");
            }
            finally
            {
                if (pkt.data != null)
                {
                    Marshal.FreeHGlobal((IntPtr)pkt.data);
                }
            }
        }

        /// <summary>
        /// 视频解码循环 - 独立线程处理视频解码
        /// </summary>
        private async Task VideoDecodeLoopAsync(CancellationToken ct)
        {
            try
            {
                DebugLogger.WriteLine("[FFmpeg] Video decode loop started");
                int framesDropped = 0;
                const int MAX_CONSECUTIVE_DROPS = 5;
                int packetsProcessed = 0;

                while (!ct.IsCancellationRequested && _isPlaying)
                {
                    // 暂停状态等待
                    if (_isPaused)
                    {
                        await Task.Delay(20, ct).ConfigureAwait(false);
                        continue;
                    }
               
                    // 从队列获取数据包
                    DebugLogger.WriteLine("[FFmpeg] Waiting for video packets...");
                    if (!await _videoPacketQueue.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                    {
                        DebugLogger.WriteLine("[FFmpeg] Video packet queue completed");
                        break;
                    }

                    DebugLogger.WriteLine("[FFmpeg] Video packets available");
                    while (_videoPacketQueue.Reader.TryRead(out var packetData))
                    {
                        packetsProcessed++;
                       // DebugLogger.WriteLine($"[FFmpeg] Processing video packet {packetsProcessed}, size={packetData.Size}");
                        
                        // ✅ 添加停止检查
                        if (!_isPlaying || ct.IsCancellationRequested)
                        {
                            packetData.Dispose();
                            DebugLogger.WriteLine("[FFmpeg] Video decode stopped mid-packet");
                            return; // 或 break;
                        }
                        ProcessVideoPacket(packetData, ref framesDropped, MAX_CONSECUTIVE_DROPS);
                        packetData.Dispose();
                    }
                }

                DebugLogger.WriteLine("[FFmpeg] Video decode loop finished");
        }
        catch (OperationCanceledException)
        {
            DebugLogger.WriteLine("[FFmpeg] Video decode loop cancelled");
        }
        catch (Exception ex)
        {
            DebugLogger.WriteLine($"[FFmpeg] Video decode error: {ex.Message}");
        }
    }

    /// <summary>
    /// 字幕解码循环 - 独立线程处理字幕解码
    /// </summary>
    private async Task SubtitleDecodeLoopAsync(CancellationToken ct)
    {
        try
        {
            DebugLogger.WriteLine("[FFmpeg] Subtitle decode loop started");

            while (!ct.IsCancellationRequested && _isPlaying)
            {
                // 暂停状态等待
                if (_isPaused)
                {
                    await Task.Delay(20, ct).ConfigureAwait(false);
                    continue;
                }

                // 如果没有选择字幕轨道，跳过解码
                if (_subtitleStreamIndex < 0)
                {
                    await Task.Delay(50, ct).ConfigureAwait(false);
                    continue;
                }

                // 从队列获取数据包
                if (!await _subtitlePacketQueue.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                    break;

                while (_subtitlePacketQueue.Reader.TryRead(out var packetData))
                {
                    ProcessSubtitlePacket(packetData);
                    packetData.Dispose();
                }
            }

            DebugLogger.WriteLine("[FFmpeg] Subtitle decode loop finished");
        }
        catch (OperationCanceledException)
        {
            DebugLogger.WriteLine("[FFmpeg] Subtitle decode loop cancelled");
        }
        catch (Exception ex)
        {
            DebugLogger.WriteLine($"[FFmpeg] Subtitle decode error: {ex.Message}");
        }
    }

    /// <summary>
    /// 处理字幕数据包
    /// </summary>
    private unsafe void ProcessSubtitlePacket(PacketData packetData)
    {
        if (_subtitleCodecContext == IntPtr.Zero) return;

        var sCodecCtx = (AVCodecContext*)_subtitleCodecContext;

        // 创建AVPacket并填充数据
        AVPacket pkt;
        ffmpeg.av_init_packet(&pkt);
        pkt.data = (byte*)Marshal.AllocHGlobal(packetData.Size);
        pkt.size = packetData.Size;
        pkt.pts = packetData.PTS;
        pkt.stream_index = packetData.StreamIndex;

        try
        {
            Marshal.Copy(packetData.Data, 0, (IntPtr)pkt.data, packetData.Size);

            AVSubtitle sub = default;
            int gotSubtitle = 0;

            // 使用 avcodec_decode_subtitle2 进行字幕解码
            int ret = ffmpeg.avcodec_decode_subtitle2(sCodecCtx, &sub, &gotSubtitle, &pkt);
            
            if (ret < 0 || gotSubtitle == 0)
            {
                if (sub.num_rects > 0)
                    ffmpeg.avsubtitle_free(&sub);
                return;
            }

            // 处理解码后的字幕
            ProcessDecodedSubtitle(&sub, packetData.TimeBase, packetData.PTS);

            ffmpeg.avsubtitle_free(&sub);
        }
        finally
        {
            Marshal.FreeHGlobal((IntPtr)pkt.data);
        }
    }
        /// <summary>
        /// 处理解码后的字幕数据
        /// </summary>
        private unsafe void ProcessDecodedSubtitle(AVSubtitle* sub, double timeBase, long pts)
    {
        if (sub->num_rects <= 0) return;

        StringBuilder subtitleText = new StringBuilder();

        for (int i = 0; i < sub->num_rects; i++)
        {
            var rect = sub->rects[i];
            if (rect == null) continue;

            string text = null;

            // 1. 纯文本字幕（text 字段）— 使用 UTF-8 解码，避免中文乱码
            if (rect->text != null)
            {
                text = Marshal.PtrToStringUTF8((IntPtr)rect->text);
            }
            // 2. ASS/SSA 格式字幕（ass 字段）— 大多数内嵌字幕使用此格式，使用 UTF-8 解码
            else if (rect->ass != null)
            {
                string assLine = Marshal.PtrToStringUTF8((IntPtr)rect->ass);
                if (!string.IsNullOrEmpty(assLine))
                {
                    text = ExtractAssText(assLine);
                }
            }
            // 3. 图形字幕（pict 字段）— PGS/VOBSUB 等，暂不支持渲染
            // rect->type == SUBTITLE_BITMAP

            if (!string.IsNullOrEmpty(text))
            {
                if (subtitleText.Length > 0)
                    subtitleText.Append(Environment.NewLine);
                subtitleText.Append(text);
            }
        }

        if (subtitleText.Length > 0)
        {
            // 字幕包 PTS 转为绝对时间（秒）
            double ptsSeconds = (pts != ffmpeg.AV_NOPTS_VALUE) ? pts * timeBase : 0;
            // start_display_time / end_display_time 是相对于 PTS 的偏移（毫秒）
            double startTime = ptsSeconds + sub->start_display_time / 1000.0;
            double endTime = ptsSeconds + sub->end_display_time / 1000.0;

            // 触发字幕解码事件
            SubtitleDecoded?.Invoke(this, new SubtitleData
            {
                Text = subtitleText.ToString(),
                StartTime = startTime,
                EndTime = endTime
            });
        }
    }

    /// <summary>
    /// 从 ASS/SSA 格式行中提取纯文本。
    /// ASS 格式: Dialogue: Marked=0,0:00:01.00,0:00:05.00,Default,,0,0,0,,实际字幕文本
    /// 需要提取第9个逗号后的内容，并去除 {\xxx} 覆盖标记
    /// </summary>
    private string ExtractAssText(string assLine)
    {
        try
        {
            // 跳过 "Dialogue:" 前缀
            int dialogueIdx = assLine.IndexOf("Dialogue:");
            if (dialogueIdx >= 0)
                assLine = assLine.Substring(dialogueIdx + 9);

            // ASS 格式有8个字段（用逗号分隔），第9个字段开始是字幕文本
            // 但文本中可能包含逗号，所以只跳过前8个逗号
            int commaCount = 0;
            int textStart = 0;
            for (int i = 0; i < assLine.Length; i++)
            {
                if (assLine[i] == ',')
                {
                    commaCount++;
                    if (commaCount == 8)
                    {
                        textStart = i + 1;
                        break;
                    }
                }
            }

            string text = textStart > 0 ? assLine.Substring(textStart) : assLine;

            // 去除 ASS 覆盖标记 {\xxx}
            int braceStart;
            while ((braceStart = text.IndexOf('{')) >= 0)
            {
                int braceEnd = text.IndexOf('}', braceStart);
                if (braceEnd < 0) break;
                text = text.Substring(0, braceStart) + text.Substring(braceEnd + 1);
            }

            // 处理 \N（ASS 换行符）
            text = text.Replace("\\N", Environment.NewLine);
            text = text.Replace("\\n", Environment.NewLine);
            text = text.Replace("\\h", " ");

            return text.Trim();
        }
        catch
        {
            return assLine;
        }
    }
        private IDirect3DTexture9 _cachedTexture;

        /// <summary>
        /// 处理视频数据包（适配新的PacketData格式）
        /// </summary>
        private unsafe void ProcessVideoPacket(PacketData packetData, ref int framesDropped, int maxConsecutiveDrops)
        {
            if (_videoCodecContext == IntPtr.Zero || _videoFrame == IntPtr.Zero)
            {
                DebugLogger.WriteLine($"[FFmpeg] ProcessVideoPacket 失败: codec或frame指针为空");
                return;
            }

            var vCodecCtx = (AVCodecContext*)_videoCodecContext;
            var frm = (AVFrame*)_videoFrame;
            if (frm == null) return;

            // 准备数据包
            AVPacket pkt;
            ffmpeg.av_init_packet(&pkt);
            pkt.data = (byte*)Marshal.AllocHGlobal(packetData.Size);
            pkt.size = packetData.Size;
            pkt.pts = packetData.PTS;
            pkt.stream_index = packetData.StreamIndex;

            try
            {
                Marshal.Copy(packetData.Data, 0, (IntPtr)pkt.data, packetData.Size);

                int ret = ffmpeg.avcodec_send_packet(vCodecCtx, &pkt);
                if (ret < 0) return;

                while (true)
                {
                    ret = ffmpeg.avcodec_receive_frame(vCodecCtx, frm);
                    if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                        break;
                    if (ret < 0) break;

                    // 更新时间戳（不强制修改）
                    if (frm->pts != ffmpeg.AV_NOPTS_VALUE)
                    {
                        _currentTimeMs = (long)(frm->pts * _videoTimeBase * 1000);
                        if (_frameCount == 1)
                            DebugLogger.WriteLine($"[视频] 第一帧 PTS: {_currentTimeMs}ms");
                        _videoClock = _currentTimeMs;
                    }

                    // ========== 判断硬件帧类型 ==========
                    bool isHardwareFrame = (_decodeMode == DecodeMode.Auto || _decodeMode == DecodeMode.Hardware) &&
                                           (frm->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11 || frm->format == (int)AVPixelFormat.AV_PIX_FMT_D3D12
                                            ||frm->format == (int)AVPixelFormat.AV_PIX_FMT_CUDA ||
                                            frm->format == (int)AVPixelFormat.AV_PIX_FMT_DXVA2_VLD);

                    // 零拷贝尝试
                    bool canUseZeroCopy = false;
                    IntPtr nv12TexturePtr = IntPtr.Zero;
                    uint textureArrayIndex = 0;
                    bool isTextureArray=false;
                   // IntPtr d3d9SurfacePtr = (IntPtr)frm->data[0];
                    //IntPtr d3d11SurfacePtr = (IntPtr)frm->data[1];
                    if (CurrentD3dModel == D3DMode.D3D11)
                    {
                        if (isHardwareFrame && _hwDeviceContext != IntPtr.Zero && frm->data[1] != null)
                        {
                            try
                            {
                                IntPtr srcTexturePtr = (IntPtr)frm->data[0];
                                uint srcArrayIndex = (uint)(UIntPtr)frm->data[1];

                                if (srcTexturePtr != IntPtr.Zero)
                                {
                                    // ===== 关键修复：在 av_frame_unref 之前复制纹理到私有纹理 =====
                                    // FFmpeg 硬解使用纹理数组循环复用 ArraySlice，av_frame_unref 会立即释放槽位
                                    // 渲染器的 CopySubresourceRegion/VideoProcessorBlt 是异步 GPU 命令
                                    // 若不复制，GPU 可能读到被新帧覆盖的混合数据 → 画面抖动
                                    // 在这里同步复制并 Flush，GPU 命令串行执行，确保复制在新帧解码之前完成
                                    IntPtr privatePtr = CopyHwTextureToPrivate(srcTexturePtr, srcArrayIndex, frm->width, frm->height);
                                    if (privatePtr != IntPtr.Zero)
                                    {
                                        nv12TexturePtr = privatePtr;
                                        textureArrayIndex = 0; // 私有纹理不是数组，ArraySlice=0
                                        canUseZeroCopy = true;
                                        isTextureArray = false;
                                    }
                                    else
                                    {
                                        // 复制失败，回退到原始指针（仍可能抖动，但至少能显示）
                                        nv12TexturePtr = srcTexturePtr;
                                        textureArrayIndex = srcArrayIndex;
                                        canUseZeroCopy = true;
                                        isTextureArray = true;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                DebugLogger.WriteLine($"[FFmpeg] 提取硬件帧描述失败: {ex.Message}");
                            }
                        }
                    }
                    else if (CurrentD3dModel == D3DMode.D3D9)
                    {
                        if (isHardwareFrame && _hwDeviceContext != IntPtr.Zero && frm->data[3] != null)
                        {
                            try
                            {
                                nv12TexturePtr = (IntPtr)frm->data[3];
                                textureArrayIndex = (uint)(UIntPtr)frm->data[1];

                                if (nv12TexturePtr != IntPtr.Zero)
                                {
                                   // var decoderSurface = new Vortice.Direct3D9.IDirect3DSurface9(nv12TexturePtr);
                                    //decoderSurface.AddRef();
                                    //decoderSurface.Release();

                                    canUseZeroCopy = true;
                                    if(_frameCount==20)
                                    DebugLogger.WriteLine($"[FFmpeg] D3D9 VA 零拷贝: {frm->width}x{frm->height}, surfacePtr={nv12TexturePtr}");
                                }
                                else
                                {
                                    DebugLogger.WriteLine("[FFmpeg] D3D9 VA 表面指针为空，回退到软解");
                                }
                            }
                            catch (Exception ex)
                            {
                                DebugLogger.WriteLine($"[FFmpeg] D3D9硬件帧处理失败: {ex.Message}");
                            }
                        }
                    }
                    else if (CurrentD3dModel == D3DMode.D3D12)
                    {
 
                        if (isHardwareFrame && _hwDeviceContext != IntPtr.Zero && frm->data[0] != null)
                        {
                            try
                            {
                                // 仅用于零拷贝标志（这里实际不使用零拷贝，因为我们要做转换）
                                var d3d12Frame = (AVD3D12VAFrame*)frm->data[0];

                                // 2. 获取纹理指针（ID3D12Resource*）
                                nv12TexturePtr = d3d12Frame->texture;
                                textureArrayIndex = (uint)d3d12Frame->subresource_index; // 如果需要，可能用于创建视图
                                DebugLogger.WriteLine($"[FFmpeg] textureArrayIndex: {textureArrayIndex}");

                                isTextureArray = (d3d12Frame->flags & (uint)AVD3D12VAFrameFlags.TextureArray) != 0;
                                // 保存 subresourceIndex 和 isTextureArray 到 frameToRender 中，以便在渲染时使用
                               // DebugLogger.WriteLine($"[FFmpeg] isTextureArray= {isTextureArray}");

                                if (nv12TexturePtr != IntPtr.Zero)
                                {
                                    canUseZeroCopy = true; // 标记为硬件帧，但下面走 CPU 转换
                                }

                            }
                            catch (Exception ex)
                            {
                                DebugLogger.WriteLine($"[FFmpeg] D3D9硬件帧处理失败: {ex.Message}");
                            }
                        }
                    }

                    // ========== 构造 FrameData 并安全入队 ==========
                    FrameData frameData = new FrameData
                    {
                        Width = _videoWidth,
                        Height = _videoHeight,
                        VideoTimestamp = (long)_videoClock,
                        AudioTimestamp = (long)_audioClock,
                        AudioPlayPosition = _frameCount,
                        IsHardwareFrame = canUseZeroCopy,
                        NV12TexturePtr = nv12TexturePtr,
                        TextureArrayIndex = textureArrayIndex,
                        IsTextureArray = isTextureArray
                     };

                    if (canUseZeroCopy)
                    {
                        // 零拷贝路径：直接传递硬件纹理信息
                        _frameCount++;
                    }
                    else
                    {
                        // ========== CPU回退路径 ==========
                        AVFrame* displayFrame = null;
                        bool needFreeFrame = false;

                        if (isHardwareFrame && _hwDeviceContext != IntPtr.Zero)
                        {
                            // 硬件帧下载到CPU
                            AVFrame* swFrame = ffmpeg.av_frame_alloc();
                            if (swFrame != null)
                            {
                                int transferRet = ffmpeg.av_hwframe_transfer_data(swFrame, frm, 0);
                                if (transferRet >= 0)
                                {
                                    swFrame->pts = frm->pts;
                                    displayFrame = swFrame;
                                    needFreeFrame = true;
                                }
                                else
                                {
                                    DebugLogger.WriteLine($"[FFmpeg] 硬件帧传输失败: {transferRet}");
                                    ffmpeg.av_frame_free(&swFrame);
                                }
                            }
                        }
                        else
                        {
                            // 软件帧：直接引用并提取
                            AVFrame* refFrame = ffmpeg.av_frame_alloc();
                            if (refFrame != null)
                            {
                                if (ffmpeg.av_frame_ref(refFrame, frm) >= 0)
                                {
                                    displayFrame = refFrame;
                                    needFreeFrame = true;
                                }
                                else
                                {
                                    ffmpeg.av_frame_free(&refFrame);
                                }
                            }
                        }

                        if (displayFrame != null)
                        {
                            // 提取 YUV420P 数据到 FrameData
                            ExtractYuv420PFrameData(displayFrame, ref frameData);
                            _frameCount++;
                        }

                        if (needFreeFrame && displayFrame != null)
                        {
                            ffmpeg.av_frame_free(&displayFrame);
                        }
                    }

                    // ========== 统一释放原始帧（让解码器继续） ==========
                    if (frm != null) ffmpeg.av_frame_unref(frm);

                    // ========== 安全入队（阻塞等待，不丢帧） ==========
                    if (frameData.YPlane != null || frameData.NV12TexturePtr != IntPtr.Zero) // 确保数据有效
                    {
                        int maxQueue = _decodeMode == DecodeMode.Hardware ? 30 : 6;
                        // 等待队列有空位
                        while (_displayQueue.Count >= maxQueue && !_decodeCts.IsCancellationRequested)
                        {
                            Thread.Sleep(5);
                        }
                        if (!_decodeCts.IsCancellationRequested)
                        {
                            _displayQueue.Add(frameData, _decodeCts.Token);
                        }
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal((IntPtr)pkt.data);
            }
        }
        // 在类中定义缓存资源
        private Vortice.Direct3D9.IDirect3DSurface9 _systemMemorySurface;   // 用于 CPU 填充
        private IDirect3DTexture9 _renderTargetTexture;    // 渲染目标纹理
        private int _cachedWidth, _cachedHeight;
        // 创建/重建资源的辅助方法
        private void EnsureD3D9Resources(IDirect3DDevice9Ex device, int width, int height)
        {
            if (_cachedWidth == width && _cachedHeight == height && _renderTargetTexture != null)
                return;

            // 释放旧资源
            _systemMemorySurface?.Dispose();
            _renderTargetTexture?.Dispose();

            // 1. 创建系统内存表面（用于 CPU 填充）
            _systemMemorySurface = device.CreateOffscreenPlainSurface(
                (uint)width, (uint)height,
                Format.X8R8G8B8,
                Pool.SystemMemory);

            // 2. 创建渲染目标纹理（用于显示）
            _renderTargetTexture = device.CreateTexture(
                (uint)width, (uint)height, 1,
                Usage.RenderTarget,
                Format.X8R8G8B8,
                Pool.Default);

            _cachedWidth = width;
            _cachedHeight = height;
        }
        int _currentSwsInputWidth, _currentSwsInputHeight, SWS_BILINEAR=2;
        // 辅助方法：从 AVFrame 提取 YUV420P 并填充到 FrameData（需实现）
        /// <summary>
        /// 从 AVFrame 提取 YUV420P 数据并填充到 FrameData（自动格式转换、缩放）
        /// </summary>
        private unsafe void ExtractYuv420PFrameData(AVFrame* frame, ref FrameData frameData)
        {
            int targetWidth = frameData.Width;
            int targetHeight = frameData.Height;

            // 判断原始帧是否已经是标准的 YUV420P 格式
            bool isYuv420P = frame->format == (int)AVPixelFormat.AV_PIX_FMT_YUV420P ||
                             frame->format == (int)AVPixelFormat.AV_PIX_FMT_YUVJ420P;

            AVFrame* srcFrame = frame;
            AVFrame* tempFrame = null;
            bool needFreeTemp = false;

            try
            {
                // ---------- 1. 格式/尺寸转换（使用 sws_scale 到 YUV420P） ----------
                if (!isYuv420P || frame->width != targetWidth || frame->height != targetHeight)
                {
                    // 重建 swsContext（仅在输入格式或尺寸变化时）
                    if (_currentSwsInputFormat != frame->format ||
                        _currentSwsInputWidth != frame->width ||
                        _currentSwsInputHeight != frame->height)
                    {
                        if (_swsContext != IntPtr.Zero)
                        {
                            ffmpeg.sws_freeContext((SwsContext*)_swsContext);
                            _swsContext = IntPtr.Zero;
                        }

                        _swsContext = (IntPtr)ffmpeg.sws_getContext(
                            frame->width, frame->height, (AVPixelFormat)frame->format,
                            targetWidth, targetHeight, AVPixelFormat.AV_PIX_FMT_YUV420P,
                            SWS_BILINEAR, null, null, null);      // SWS_BILINEAR 性能优于默认的 Bicubic

                        if (_swsContext == IntPtr.Zero)
                        {
                            DebugLogger.WriteLine($"[FFmpeg] sws_getContext 失败 (fmt:{frame->format} {frame->width}x{frame->height})");
                            return;
                        }
                        _currentSwsInputFormat = frame->format;
                        _currentSwsInputWidth = frame->width;
                        _currentSwsInputHeight = frame->height;
                        DebugLogger.WriteLine($"[FFmpeg] swsContext 重建: {frame->width}x{frame->height} {(AVPixelFormat)frame->format} -> {targetWidth}x{targetHeight} YUV420P");
                    }

                    // 分配临时帧
                    tempFrame = ffmpeg.av_frame_alloc();
                    if (tempFrame == null)
                    {
                        DebugLogger.WriteLine("[FFmpeg] av_frame_alloc 失败");
                        return;
                    }
                    needFreeTemp = true;
                    tempFrame->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
                    tempFrame->width = targetWidth;
                    tempFrame->height = targetHeight;
                    if (ffmpeg.av_frame_get_buffer(tempFrame, 32) < 0)
                    {
                        DebugLogger.WriteLine("[FFmpeg] av_frame_get_buffer 失败");
                        return;
                    }

                    int result = ffmpeg.sws_scale((SwsContext*)_swsContext,
                        frame->data, frame->linesize,
                        0, frame->height,
                        tempFrame->data, tempFrame->linesize);

                    if (result < 0 || result != targetHeight)
                    {
                        DebugLogger.WriteLine($"[FFmpeg] sws_scale 失败: {result}");
                        ffmpeg.sws_freeContext((SwsContext*)_swsContext);
                        _swsContext = IntPtr.Zero;
                        return;
                    }

                    srcFrame = tempFrame;
                }

                // ---------- 2. 拷贝 YUV 平面到 FrameData（考虑 linesize） ----------
                int ySize = targetWidth * targetHeight;
                int uvSize = (targetWidth / 2) * (targetHeight / 2);

                // 只在数组大小不符时重新分配（避免每帧分配）
                if (frameData.YPlane == null || frameData.YPlane.Length != ySize)
                    frameData.YPlane = new byte[ySize];
                if (frameData.UPlane == null || frameData.UPlane.Length != uvSize)
                    frameData.UPlane = new byte[uvSize];
                if (frameData.VPlane == null || frameData.VPlane.Length != uvSize)
                    frameData.VPlane = new byte[uvSize];

                // 真实 linesize（可能大于宽度）
                int yStride = srcFrame->linesize[0];
                int uStride = srcFrame->linesize[1];
                int vStride = srcFrame->linesize[2];

                // 优化拷贝：如果 linesize == width 则直接整块复制，否则逐行拷贝
                if (yStride == targetWidth)
                {
                    // 紧密排列，一次拷贝整个平面
                    fixed (byte* yDest = frameData.YPlane)
                    {
                        Buffer.MemoryCopy(srcFrame->data[0], yDest, ySize, ySize);
                    }
                }
                else
                {
                    // 存在行填充，逐行拷贝
                    for (int row = 0; row < targetHeight; row++)
                    {
                        Marshal.Copy(
                            IntPtr.Add((IntPtr)srcFrame->data[0], row * yStride),
                            frameData.YPlane, row * targetWidth, targetWidth);
                    }
                }

                if (uStride == targetWidth / 2)
                {
                    fixed (byte* uDest = frameData.UPlane, vDest = frameData.VPlane)
                    {
                        Buffer.MemoryCopy(srcFrame->data[1], uDest, uvSize, uvSize);
                        Buffer.MemoryCopy(srcFrame->data[2], vDest, uvSize, uvSize);
                    }
                }
                else
                {
                    for (int row = 0; row < targetHeight / 2; row++)
                    {
                        Marshal.Copy(
                            IntPtr.Add((IntPtr)srcFrame->data[1], row * uStride),
                            frameData.UPlane, row * (targetWidth / 2), targetWidth / 2);
                        Marshal.Copy(
                            IntPtr.Add((IntPtr)srcFrame->data[2], row * vStride),
                            frameData.VPlane, row * (targetWidth / 2), targetWidth / 2);
                    }
                }

                // 记录实际步幅（对于紧密排列，设为宽度；也可保留 linesize，但后续处理往往用宽度）
                frameData.YStride = targetWidth;
                frameData.UStride = targetWidth / 2;
                frameData.VStride = targetWidth / 2;
            }
            finally
            {
                if (needFreeTemp && tempFrame != null)
                    ffmpeg.av_frame_free(&tempFrame);
            }
        }
        /// <summary>
        /// 调试：将硬件帧保存为 BMP 图片（用于定位解码问题）
        /// </summary>
        private unsafe void SaveHardwareFrameAsBmp(AVFrame* hwFrame, int width, int height, string fileName)
        {
            if (hwFrame == null || _hwDeviceContext == IntPtr.Zero)
            {
                DebugLogger.WriteLine($"[FFmpeg] 保存硬件帧失败: hwFrame={hwFrame == null}, hwDeviceContext={_hwDeviceContext == IntPtr.Zero}");
                return;
            }

            try
            {
                // 将硬件帧下载到CPU
                AVFrame* swFrame = ffmpeg.av_frame_alloc();
                if (swFrame == null)
                {
                    DebugLogger.WriteLine($"[FFmpeg] 保存硬件帧失败: 无法分配软件帧");
                    return;
                }

                DebugLogger.WriteLine($"[FFmpeg] 开始下载硬件帧: hwFrame->format={hwFrame->format}, width={hwFrame->width}, height={hwFrame->height}");
                DebugLogger.WriteLine($"[FFmpeg] 硬件帧详情: data[0]=0x{(IntPtr)hwFrame->data[0]:X8}, data[1]=0x{(IntPtr)hwFrame->data[1]:X8}, data[2]=0x{(IntPtr)hwFrame->data[2]:X8}");
                DebugLogger.WriteLine($"[FFmpeg] 硬件帧linesize: linesize[0]={hwFrame->linesize[0]}, linesize[1]={hwFrame->linesize[1]}, linesize[2]={hwFrame->linesize[2]}");
                
                int transferRet = ffmpeg.av_hwframe_transfer_data(swFrame, hwFrame, 0);
                if (transferRet >= 0)
                {
                    DebugLogger.WriteLine($"[FFmpeg] 硬件帧下载成功: swFrame->format={swFrame->format}, data[0]=0x{(IntPtr)swFrame->data[0]:X8}, linesize[0]={swFrame->linesize[0]}");
                    
                    // 检查帧数据是否有效（采样几个像素点）
                    if (swFrame->data[0] != null)
                    {
                        // 检查Y平面（前10个像素）
                        byte* yData = swFrame->data[0];
                        int ySum = 0;
                        for (int i = 0; i < Math.Min(100, width * height); i++)
                        {
                            ySum += yData[i];
                        }
                        DebugLogger.WriteLine($"[FFmpeg] Y平面采样: 前100像素平均值={ySum / 100.0:F2}");
                        
                        // 检查UV平面
                        if (swFrame->data[1] != null)
                        {
                            byte* uData = swFrame->data[1];
                            int uSum = 0;
                            for (int i = 0; i < Math.Min(50, width * height / 4); i++)
                            {
                                uSum += uData[i];
                            }
                            DebugLogger.WriteLine($"[FFmpeg] U平面采样: 前50像素平均值={uSum / 50.0:F2}");
                        }
                    }
                    
                    // 使用 swscale 转换到 RGB
                    SaveFrameToBmpViaSwscale(swFrame, width, height, fileName);
                    DebugLogger.WriteLine($"[FFmpeg] 硬件帧保存成功: {fileName}");
                }
                else
                {
                    DebugLogger.WriteLine($"[FFmpeg] 硬件帧下载失败: {transferRet}");
                }
                ffmpeg.av_frame_free(&swFrame);
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[FFmpeg] 保存硬件帧失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 调试：使用 swscale 将帧转换为 BMP（更安全的方式）
        /// </summary>
        private unsafe void SaveFrameToBmpViaSwscale(AVFrame* frame, int width, int height, string fileName)
        {
            try
            {
                // 确保目录存在
                string debugDir = Path.Combine(AppContext.BaseDirectory, "debug_frames");
                Directory.CreateDirectory(debugDir);
                string filePath = Path.Combine(debugDir, fileName);

                DebugLogger.WriteLine($"[FFmpeg] SaveFrameToBmpViaSwscale: input format={frame->format}, width={width}, height={height}");
                DebugLogger.WriteLine($"[FFmpeg] 帧颜色信息: color_range={frame->color_range}, color_primaries={frame->color_primaries}, color_trc={frame->color_trc}");
                DebugLogger.WriteLine($"[FFmpeg] 帧数据指针: data[0]=0x{(IntPtr)frame->data[0]:X8}, data[1]=0x{(IntPtr)frame->data[1]:X8}");
                DebugLogger.WriteLine($"[FFmpeg] 帧linesize: linesize[0]={frame->linesize[0]}, linesize[1]={frame->linesize[1]}");
                
                // 检查Y平面数据
                if (frame->data[0] != null)
                {
                    byte* yData = frame->data[0];
                    int yMin = 255, yMax = 0, ySum = 0;
                    for (int i = 0; i < Math.Min(1000, frame->width * frame->height); i++)
                    {
                        byte val = yData[i];
                        if (val < yMin) yMin = val;
                        if (val > yMax) yMax = val;
                        ySum += val;
                    }
                    DebugLogger.WriteLine($"[FFmpeg] Y平面: min={yMin}, max={yMax}, avg={ySum / 1000.0:F2}");
                }
                
                // 检查U/V平面数据
                if (frame->data[1] != null)
                {
                    byte* uData = frame->data[1];
                    int uMin = 255, uMax = 0, uSum = 0;
                    for (int i = 0; i < Math.Min(500, frame->width * frame->height / 4); i++)
                    {
                        byte val = uData[i];
                        if (val < uMin) uMin = val;
                        if (val > uMax) uMax = val;
                        uSum += val;
                    }
                    DebugLogger.WriteLine($"[FFmpeg] U平面: min={uMin}, max={uMax}, avg={uSum / 500.0:F2}");
                }

                // 使用 swscale 转换到 BGR24
                AVFrame* rgbFrame = ffmpeg.av_frame_alloc();
                if (rgbFrame == null)
                {
                    DebugLogger.WriteLine("[FFmpeg] SaveFrameToBmpViaSwscale: 无法分配RGB帧");
                    return;
                }

                rgbFrame->format = (int)AVPixelFormat.AV_PIX_FMT_BGR24;
                rgbFrame->width = width;
                rgbFrame->height = height;
                int ret = ffmpeg.av_frame_get_buffer(rgbFrame, 1);
                if (ret < 0)
                {
                    DebugLogger.WriteLine($"[FFmpeg] SaveFrameToBmpViaSwscale: av_frame_get_buffer失败: {ret}");
                    ffmpeg.av_frame_free(&rgbFrame);
                    return;
                }

                // 使用 swscale 转换到 BGR24，设置正确的颜色空间参数
                // 对于NV12格式，需要设置源和目标的颜色范围
                int srcRange = (frame->color_range == AVColorRange.AVCOL_RANGE_JPEG) ? 1 : 0; // JPEG范围=full range
                int dstRange = 1; // 输出使用full range (0-255)
                
                var sws = ffmpeg.sws_getContext(
                    frame->width, frame->height, (AVPixelFormat)frame->format,
                    width, height, AVPixelFormat.AV_PIX_FMT_BGR24,
                    1, null, null, null);
                
                DebugLogger.WriteLine($"[FFmpeg] swscale颜色空间: srcRange={srcRange}, dstRange={dstRange}, color_range={frame->color_range}");

                if (sws == null)
                {
                    DebugLogger.WriteLine("[FFmpeg] SaveFrameToBmpViaSwscale: sws_getContext失败");
                    ffmpeg.av_frame_free(&rgbFrame);
                    return;
                }

                DebugLogger.WriteLine($"[FFmpeg] swscale转换: frame->data[0]=0x{(IntPtr)frame->data[0]:X8}, rgbFrame->data[0]=0x{(IntPtr)rgbFrame->data[0]:X8}");
                
                ffmpeg.sws_scale(sws,
                    frame->data, frame->linesize,
                    0, frame->height,
                    rgbFrame->data, rgbFrame->linesize);

                ffmpeg.sws_freeContext(sws);

                // 检查转换后的数据是否为空
                if (rgbFrame->data[0] == null)
                {
                    DebugLogger.WriteLine("[FFmpeg] SaveFrameToBmpViaSwscale: 转换后RGB数据为空");
                    ffmpeg.av_frame_free(&rgbFrame);
                    return;
                }

                // 写入 BMP 文件
                int rowSize = width * 3;
                int padding = (4 - (rowSize % 4)) % 4;
                int rgbSize = (rowSize + padding) * height;
                
                using (FileStream fs = new FileStream(filePath, FileMode.Create))
                {
                    BinaryWriter bw = new BinaryWriter(fs);

                    // BMP 文件头 (14 bytes)
                    bw.Write((byte)'B');
                    bw.Write((byte)'M');
                    bw.Write(54 + rgbSize);
                    bw.Write((int)0);
                    bw.Write((int)54);

                    // BMP 信息头 (40 bytes)
                    bw.Write(40);
                    bw.Write(width);
                    bw.Write(-height); // 负高度表示从上到下扫描
                    bw.Write((short)1);
                    bw.Write((short)24);
                    bw.Write(0);
                    bw.Write(rgbSize);
                    bw.Write(0);
                    bw.Write(0);
                    bw.Write(0);
                    bw.Write(0);

                    // 写入 BGR 数据（BMP 格式本身就是 BGR 顺序，不需要转换）
                    // 注意：swscale输出的帧是从顶部到底部的，与BMP格式一致
                    for (int y = 0; y < height; y++)
                    {
                        IntPtr rowPtr = (IntPtr)rgbFrame->data[0] + y * rgbFrame->linesize[0];
                        byte[] rowData = new byte[rowSize];
                        Marshal.Copy(rowPtr, rowData, 0, rowSize);
                        
                        bw.Write(rowData);
                        for (int p = 0; p < padding; p++)
                            bw.Write((byte)0);
                    }
                }

                DebugLogger.WriteLine($"[FFmpeg] 帧保存成功: {filePath}");
                ffmpeg.av_frame_free(&rgbFrame);
                DebugLogger.WriteLine($"[FFmpeg] 帧已保存: {filePath}");
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[FFmpeg] 保存帧失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 将视频有限范围颜色(16-235)转换为RGB全范围(0-255)
        /// 视频格式通常使用有限范围，而BMP图片需要全范围
        /// </summary>
        private unsafe void AdjustColorRange(AVFrame* rgbFrame, int width, int height)
        {
            int rowSize = width * 3;
            byte* dataPtr = (byte*)rgbFrame->data[0];
            int linesize = rgbFrame->linesize[0];

            for (int y = 0; y < height; y++)
            {
                byte* rowPtr = dataPtr + y * linesize;
                for (int x = 0; x < rowSize; x++)
                {
                    // 视频有限范围: 16-235 -> RGB全范围: 0-255
                    // 公式: output = ((input - 16) * 255) / 219
                    byte value = rowPtr[x];
                    if (value <= 16)
                    {
                        rowPtr[x] = 0;
                    }
                    else if (value >= 235)
                    {
                        rowPtr[x] = 255;
                    }
                    else
                    {
                        rowPtr[x] = (byte)(((value - 16) * 255) / 219);
                    }
                }
            }
        }

        /// <summary>
        /// 调试：将 YUV420P 帧保存为 BMP 图片（用于定位解码问题）
        /// </summary>
        private unsafe void SaveFrameAsBmp(AVFrame* frame, int width, int height, string fileName)
        {
            try
            {
                // 调试：检查帧数据
                if (frame == null)
                {
                    DebugLogger.WriteLine($"[FFmpeg] SaveFrameAsBmp: frame is null");
                    return;
                }
                if (frame->data[0] == null)
                {
                    DebugLogger.WriteLine($"[FFmpeg] SaveFrameAsBmp: frame->data[0] is null");
                    return;
                }
                DebugLogger.WriteLine($"[FFmpeg] SaveFrameAsBmp: width={width}, height={height}, format={frame->format}, linesize[0]={frame->linesize[0]}, data[0]=0x{(IntPtr)frame->data[0]:X8}");

                // 确保目录存在
                string debugDir = Path.Combine(AppContext.BaseDirectory, "debug_frames");
                Directory.CreateDirectory(debugDir);
                string filePath = Path.Combine(debugDir, fileName);

                // 使用 swscale 转换到 RGB24
                AVFrame* rgbFrame = ffmpeg.av_frame_alloc();
                if (rgbFrame == null)
                {
                    DebugLogger.WriteLine($"[FFmpeg] SaveFrameAsBmp: rgbFrame is null");
                    return;
                }

                rgbFrame->format = (int)AVPixelFormat.AV_PIX_FMT_BGR24;
                rgbFrame->width = width;
                rgbFrame->height = height;
                ffmpeg.av_frame_get_buffer(rgbFrame, 1);

                var sws = ffmpeg.sws_getContext(
                    frame->width, frame->height, (AVPixelFormat)frame->format,
                    width, height, AVPixelFormat.AV_PIX_FMT_BGR24,
                    1, null, null, null);

                if (sws == null)
                {
                    DebugLogger.WriteLine($"[FFmpeg] SaveFrameAsBmp: sws_getContext failed");
                    ffmpeg.av_frame_free(&rgbFrame);
                    return;
                }

                ffmpeg.sws_scale(sws,
                    frame->data, frame->linesize,
                    0, frame->height,
                    rgbFrame->data, rgbFrame->linesize);

                // 调试：检查转换后的RGB数据（采样前100个像素）
                byte* rgbData = (byte*)rgbFrame->data[0];
                int rgbSum = 0;
                for (int i = 0; i < Math.Min(100, width * height * 3); i++)
                {
                    rgbSum += rgbData[i];
                }
                DebugLogger.WriteLine($"[FFmpeg] SaveFrameAsBmp: RGB数据采样总和={rgbSum}, color_range={frame->color_range}");

                // 手动调整颜色范围：将视频有限范围(16-235)转换为全范围(0-255)
                // 这是因为视频通常使用有限范围，而BMP需要全范围
                // 即使color_range未设置，也默认按有限范围处理（视频编码标准做法）
                bool needsAdjustment = frame->color_range != AVColorRange.AVCOL_RANGE_JPEG;
                DebugLogger.WriteLine($"[FFmpeg] SaveFrameAsBmp: 需要调整颜色范围={needsAdjustment}, color_range={frame->color_range}");
                
                // 对于软件解码，强制应用颜色范围调整（因为视频标准使用有限范围）
                // 即使color_range是UNSPECIFIED，也按有限范围处理
                if (needsAdjustment || _decodeMode == DecodeMode.Software)
                {
                    AdjustColorRange(rgbFrame, width, height);
                    DebugLogger.WriteLine($"[FFmpeg] SaveFrameAsBmp: 已应用颜色范围调整");
                }

                DebugLogger.WriteLine($"[FFmpeg] SaveFrameAsBmp: swscale completed, rgbFrame->data[0]=0x{(IntPtr)rgbFrame->data[0]:X8}");

                ffmpeg.sws_freeContext(sws);

                // 写入 BMP 文件
                int rgbSize = width * height * 3;
                using (FileStream fs = new FileStream(filePath, FileMode.Create))
                {
                    BinaryWriter bw = new BinaryWriter(fs);

                    // BMP 文件头 (14 bytes)
                    bw.Write((byte)'B');
                    bw.Write((byte)'M');
                    bw.Write(54 + rgbSize);
                    bw.Write((int)0);
                    bw.Write((int)54);

                    // BMP 信息头 (40 bytes)
                    bw.Write(40);
                    bw.Write(width);
                    bw.Write(-height); // 负高度
                    bw.Write((short)1);
                    bw.Write((short)24);
                    bw.Write(0);
                    bw.Write(rgbSize);
                    bw.Write(0);
                    bw.Write(0);
                    bw.Write(0);
                    bw.Write(0);

                    // 写入 BGR 数据（BMP 格式本身就是 BGR 顺序，不需要转换）
                    int rowSize = width * 3;
                    int padding = (4 - (rowSize % 4)) % 4;

                    for (int y = 0; y < height; y++)
                    {
                        IntPtr rowPtr = (IntPtr)rgbFrame->data[0] + y * rgbFrame->linesize[0];
                        byte[] rowData = new byte[rowSize];
                        Marshal.Copy(rowPtr, rowData, 0, rowSize);
                        
                        bw.Write(rowData);
                        for (int p = 0; p < padding; p++)
                            bw.Write((byte)0);
                    }
                }

                ffmpeg.av_frame_free(&rgbFrame);
                DebugLogger.WriteLine($"[FFmpeg] 帧已保存: {filePath}");
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[FFmpeg] 保存帧失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 将 YUV 有限范围(16-235)转换为全范围(0-255)
        /// 视频编码标准使用有限范围，而渲染需要全范围
        /// </summary>
        private void AdjustYuvColorRange(byte[] yPlane, byte[] uPlane, byte[] vPlane)
        {
            // 调整 Y 平面 (亮度)
            for (int i = 0; i < yPlane.Length; i++)
            {
                byte value = yPlane[i];
                if (value <= 16)
                    yPlane[i] = 0;
                else if (value >= 235)
                    yPlane[i] = 255;
                else
                    yPlane[i] = (byte)(((value - 16) * 255) / 219);
            }

            // 调整 U 平面 (色度)
            for (int i = 0; i < uPlane.Length; i++)
            {
                byte value = uPlane[i];
                if (value <= 16)
                    uPlane[i] = 0;
                else if (value >= 240)
                    uPlane[i] = 255;
                else
                    uPlane[i] = (byte)(((value - 16) * 255) / 224);
            }

            // 调整 V 平面 (色度)
            for (int i = 0; i < vPlane.Length; i++)
            {
                byte value = vPlane[i];
                if (value <= 16)
                    vPlane[i] = 0;
                else if (value >= 240)
                    vPlane[i] = 255;
                else
                    vPlane[i] = (byte)(((value - 16) * 255) / 224);
            }
        }

        /// <summary>
        /// 复制 YUV420P 平面数据到显示队列
        /// </summary>
        private unsafe void CopyYuvPlanesToQueue(AVFrame* frame, int width, int height)
        {
            // 在 avcodec_receive_frame 成功后
            int ySize = frame->linesize[0] * height;
            int uvSize = frame->linesize[1] * (height / 2);
            


            int uvHeight = height / 2;
            int uSize = frame->linesize[1] * uvHeight;
            int vSize = frame->linesize[2] * uvHeight;

            var frameData = new FrameData
            {
                Width = width,
                Height = height,
                YStride = frame->linesize[0],
                UStride = frame->linesize[1],
                VStride = frame->linesize[2],
                VideoTimestamp = (long)_videoClock,
                AudioTimestamp = (long)_audioClock,
                AudioPlayPosition = _frameCount,
                IsHardwareFrame = _currentIsHardwareFrame,
                NV12TexturePtr = _currentNV12TexturePtr,
                TextureArrayIndex = _currentTextureArrayIndex
            };
          //  DebugLogger.WriteLine($"原始 Y 数据指针: 0x{(IntPtr)frame->data[0]:X8}, linesize: {frame->linesize[0]}, height: {frame->height}, ySize: {ySize}");
            // 复制 Y 平面
            if (frame->data[0] != null && ySize > 0)
            {
                frameData.YPlane = new byte[ySize];
                Marshal.Copy((IntPtr)frame->data[0], frameData.YPlane, 0, ySize); 
            } 

            // 复制 U 平面
            if (frame->data[1] != null && uSize > 0)
            {
                frameData.UPlane = new byte[uSize];
                Marshal.Copy((IntPtr)frame->data[1], frameData.UPlane, 0, uSize);
            }

            // 复制 V 平面
            if (frame->data[2] != null && vSize > 0)
            {
                frameData.VPlane = new byte[vSize];
                Marshal.Copy((IntPtr)frame->data[2], frameData.VPlane, 0, vSize);
            }
            // 复制完成后，强制填充为红色（仅用于测试）
            //if (frameData.YPlane != null && frameData.UPlane != null && frameData.VPlane != null)
            //{
            //    // 填充 Y 平面为 82
            //    for (int i = 0; i < frameData.YPlane.Length; i++)
            //        frameData.YPlane[i] = 82;
            //    // 填充 U 平面为 90
            //    for (int i = 0; i < frameData.UPlane.Length; i++)
            //        frameData.UPlane[i] = 90;
            //    // 填充 V 平面为 240
            //    for (int i = 0; i < frameData.VPlane.Length; i++)
            //        frameData.VPlane[i] = 240;
            //}
            // ========== 颜色范围调整 ==========
            // 软件解码输出的是有限范围(16-235)YUV数据，需要转换为全范围(0-255)
            // 否则渲染时画面会偏暗，看起来像黑色背景
            if (_decodeMode == DecodeMode.Software && frameData.YPlane != null && frameData.UPlane != null && frameData.VPlane != null)
            {
                DebugLogger.WriteLine($"[FFmpeg] CopyYuvPlanesToQueue: 准备应用YUV颜色范围调整, _decodeMode={_decodeMode}");
                DebugLogger.WriteLine($"[FFmpeg] CopyYuvPlanesToQueue: YPlane长度={frameData.YPlane.Length}, UPlane={frameData.UPlane.Length}, VPlane={frameData.VPlane.Length}");
                
                // 采样检查调整前的数据
                if (frameData.YPlane.Length > 0)
                {
                    DebugLogger.WriteLine($"[FFmpeg] CopyYuvPlanesToQueue: 调整前 - Y[0]={frameData.YPlane[0]}, Y[100]={frameData.YPlane[Math.Min(100, frameData.YPlane.Length-1)]}");
                }
                
              //  AdjustYuvColorRange(frameData.YPlane, frameData.UPlane, frameData.VPlane);
                
                // 采样检查调整后的数据
                if (frameData.YPlane.Length > 0)
                {
                    DebugLogger.WriteLine($"[FFmpeg] CopyYuvPlanesToQueue: 调整后 - Y[0]={frameData.YPlane[0]}, Y[100]={frameData.YPlane[Math.Min(100, frameData.YPlane.Length-1)]}");
                }
                
                DebugLogger.WriteLine($"[FFmpeg] CopyYuvPlanesToQueue: 已应用YUV颜色范围调整 (有限范围->全范围)");
            }
            else
            {
                DebugLogger.WriteLine($"[FFmpeg] CopyYuvPlanesToQueue: 跳过颜色范围调整, _decodeMode={_decodeMode}, YPlane={frameData.YPlane != null}, UPlane={frameData.UPlane != null}, VPlane={frameData.VPlane != null}");
            }
            if (_displayQueue == null) { return; };
            if (_displayQueue.Count >= 3)
            {
                _displayQueue.TryTake(out _);
            }
            _frameCount++;
            if (_frameCount == 10)
            {
                // SaveFrameAsBmp(frame, width, height, $"decoded_frame_10_3.bmp"); 
                //string yuvPath = "raw_frame_10.yuv";
                //using var fs = new FileStream(yuvPath, FileMode.Create);
                //fs.Write(frameData.YPlane, 0, frameData.YPlane.Length);
                //fs.Write(frameData.UPlane, 0, frameData.UPlane.Length);
                //fs.Write(frameData.VPlane, 0, frameData.VPlane.Length);
                //DebugLogger.WriteLine($"保存原始 YUV 到 {yuvPath}");

                //// 读取第一个像素验证
                //if (frame->data[0] != null)
                //{
                //    byte firstPixel = Marshal.ReadByte((IntPtr)frame->data[0]);
                //    DebugLogger.WriteLine($"第一个像素 Y 值: {firstPixel}");
                //}
                //// 在 CopyYuvPlanesToQueue 方法内，复制完数据后立即检查
                //if (frameData.YPlane != null && frameData.YPlane.Length > 0)
                //{
                //    // 打印第一个和中心像素
                //    int centerY = (height / 2) * width + (width / 2);
                //    DebugLogger.WriteLine($"复制后 Y 平面第一个像素: {frameData.YPlane[0]}, 中心像素: {frameData.YPlane[centerY]}");

                  
                //        string debugPath = $"debug_y_{_frameCount}.bin";
                //        File.WriteAllBytes(debugPath, frameData.YPlane);
                //        DebugLogger.WriteLine($"已保存 Y 平面数据到 {debugPath}，大小: {frameData.YPlane.Length}");
                    
                //}
            }
            _displayQueue.TryAdd(frameData, 10, _decodeCts?.Token ?? CancellationToken.None);
        }
        private unsafe bool IsFrameDataValid(AVFrame* frame)
        {
            if (frame == null)
            {
                DebugLogger.WriteLine("[FFmpeg] Invalid frame: frame pointer is null");
                return false;
            }
            if (frame->data[0] == null)
            {
                DebugLogger.WriteLine("[FFmpeg] Invalid frame: frame->data[0] is null");
                return false;
            }
            if (frame->width <= 0 || frame->height <= 0)
            {
                DebugLogger.WriteLine($"[FFmpeg] Invalid frame: width={frame->width}, height={frame->height}");
                return false;
            }
            if (frame->linesize[0] <= 0)
            {
                DebugLogger.WriteLine($"[FFmpeg] Invalid frame: linesize[0]={frame->linesize[0]}");
                return false;
            }
            return true;
        }

        /// <summary>
        /// 从对象池获取帧数据buffer
        /// </summary>
        /// <returns>复用的byte[] buffer</returns>
        private byte[] GetFrameBufferFromPool()
        {
            if (_frameBufferPool.TryPop(out byte[] buffer))
            {
                // 检查buffer大小是否匹配
                if (buffer.Length == _currentBufferSize)
                {
                    return buffer;
                }
                // 大小不匹配，丢弃旧buffer
            }
            // 创建新buffer
            return new byte[_currentBufferSize];
        }

        /// <summary>
        /// 将帧数据buffer放回对象池
        /// </summary>
        /// <param name="buffer">要回收的buffer</param>
        private void ReturnFrameBufferToPool(byte[] buffer)
        {
            // 只回收大小匹配的buffer，并且限制池大小为4（减少内存占用）
            if (buffer != null && buffer.Length == _currentBufferSize && _frameBufferPool.Count < 4)
            {
                _frameBufferPool.Push(buffer);
            }
        }

        /// <summary>
        /// 更新当前buffer大小并清理不匹配的池
        /// </summary>
        /// <param name="newSize">新的buffer大小</param>
        private void UpdateBufferSize(int newSize)
        {
            if (_currentBufferSize != newSize)
            {
                _currentBufferSize = newSize;
                // 清空旧的不匹配的buffer
                _frameBufferPool.Clear();
            }
        }

        #endregion

        private unsafe void TestExecuteSeekNow(double position)
        {
            try
            {
                lock (_seekLock)
                {
                    if (_formatContext == IntPtr.Zero || _videoStreamIndex < 0)
                        return;

                    var fmtCtx = (AVFormatContext*)_formatContext;
                    if (fmtCtx == null)
                        return;

                    DebugLogger.WriteLine($"[FFmpeg] ExecuteSeekNow: position={position}s");

                    // 1. 清空显示队列，丢弃所有旧帧
                    while (_displayQueue.TryTake(out _)) ;
                    DebugLogger.WriteLine($"[FFmpeg] Display queue cleared after seek");

                    // 2. 计算目标 PTS
                    var videoStream = fmtCtx->streams[_videoStreamIndex];
                    long targetPts = (long)(position / ffmpeg.av_q2d(videoStream->time_base));

                    int ret = ffmpeg.av_seek_frame(fmtCtx, _videoStreamIndex, targetPts, ffmpeg.AVSEEK_FLAG_BACKWARD);
                    if (ret < 0)
                    {
                        DebugLogger.WriteLine($"[FFmpeg] av_seek_frame failed: {ret}");
                        return;
                    }

                    // 3. 清空解码器缓冲区
                    if (_videoCodecContext != IntPtr.Zero)
                    {
                        var vCtx = (AVCodecContext*)_videoCodecContext;
                        ffmpeg.avcodec_flush_buffers(vCtx);
                    }

                    if (_audioCodecContext != IntPtr.Zero)
                    {
                        var aCtx = (AVCodecContext*)_audioCodecContext;
                        ffmpeg.avcodec_flush_buffers(aCtx);
                    }

                    // 4. 重置播放时钟（加锁）
                    lock (_clockLock)
                    { 
                        _seekBaseTimeMs = (long)(position * 1000);
                        _clockBase = position;
                        _clockStartTicks = Stopwatch.GetTimestamp();
                        _isPaused = false;  // 强制退出暂停
                        
                        // 记录Seek时的音频播放位置基准值
                        if (_audioOutput != null)
                        {
                            _seekAudioPositionBytes = _audioOutput.GetPosition();
                        }
                    } 

                    // 5. 设置Seek稳定期，暂时禁用同步丢弃逻辑
                    _isSeekingStabilizing = true;
                    _seekStabilizingFrameCount = 0;
                    DebugLogger.WriteLine($"[FFmpeg] Seek stabilizing period started");

                    DebugLogger.WriteLine($"[FFmpeg] Seek completed to: {position}s, PTS offset set to: {_seekBaseTimeMs}ms");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[FFmpeg] ExecuteSeekNow exception: {ex.Message}");
            }
            finally
            {
                _isSeeking = false;
            }
        }

        /// <summary>
        /// 处理下一个媒体数据包
        /// 读取数据包并根据流类型分发到对应的解码器
        /// </summary>
        /// <param name="ct">取消令牌</param>
        /// <param name="framesDropped">连续丢帧计数（引用参数）</param>
        /// <param name="maxConsecutiveDrops">最大连续丢帧数</param>
        /// <returns>如果需要终止循环返回true，否则返回false</returns>
        private unsafe    bool ProcessNextPacket(CancellationToken ct, ref int framesDropped, int maxConsecutiveDrops)
        {
            var fmtCtx = (AVFormatContext*)_formatContext;
            var pkt = (AVPacket*)_packet;

            int readResult = 0;
            try
            {
                readResult = ffmpeg.av_read_frame(fmtCtx, pkt);
            }
            catch (AccessViolationException)
            {
                return true;
            }

            // 读取失败或到达文件末尾
            if (readResult < 0)
            {
                if (readResult == ffmpeg.AVERROR_EOF)
                {
                    return true;
                }
                return true;
            }

            // 根据流索引分发到对应的解码器
            if (pkt->stream_index == _videoStreamIndex)
            {
               TestDecodeVideoPacket(ref framesDropped, maxConsecutiveDrops);
            }
            else if (pkt->stream_index == _audioStreamIndex)
            {
                 DecodeAudioPacket();
            }

            ffmpeg.av_packet_unref(pkt);
            return false;
        }
        private void SaveAudioToFile(byte[] data, int index)
        {
            string path = $"{Path.Combine(AppContext.BaseDirectory, "Audio")}";
            Directory.CreateDirectory(path);
            path = $"{path}\\test_audio_{index}.raw";
            File.WriteAllBytes(path, data);
            DebugLogger.WriteLine($"[音频] 已保存: {path}");
        }
 
        

        private unsafe void TestDecodeVideoPacket(ref int framesDropped, int maxConsecutiveDrops)
        {
            var vCodecCtx = (AVCodecContext*)_videoCodecContext;
            var pkt = (AVPacket*)_packet;
            var frm = (AVFrame*)_videoFrame;
            DebugLogger.WriteLine($"[视频] 线程ID: {Thread.CurrentThread.ManagedThreadId}, 开始解码");

            // 1. 发送包到解码器
            int ret = ffmpeg.avcodec_send_packet(vCodecCtx, pkt);
            if (ret < 0) return;

            while (true)
            {
                if (_isPaused)
                {
                    Thread.Sleep(10);
                    continue;
                }
                
                // ========== 每帧开始计时 ==========
                // long frameStartStamp = Stopwatch.GetTimestamp();

                ret = ffmpeg.avcodec_receive_frame(vCodecCtx, frm);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                    break;
                if (ret < 0) break;




                // ========== 硬件帧转换 ==========
                AVFrame* displayFrame = frm;
                AVFrame* swFrame = null;
                bool isHardwareFrame = frm->format == (int)AVPixelFormat.AV_PIX_FMT_CUDA ||
                                       frm->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11 ||
                                       frm->format == (int)AVPixelFormat.AV_PIX_FMT_DXVA2_VLD;

                // 零拷贝硬件帧信息
                IntPtr nv12TexturePtr = IntPtr.Zero;
                uint textureArrayIndex = 0;

                if (isHardwareFrame && _hwDeviceContext != IntPtr.Zero)
                {
                    // 提取 D3D11VA 纹理指针
                    if (frm->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11 && frm->data[1] != null)
                    {
                        try
                        {
                            var descPtr = (IntPtr)frm->data[1];
                            nv12TexturePtr = Marshal.ReadIntPtr(descPtr, 0);
                            textureArrayIndex = (uint)Marshal.ReadInt32(descPtr, IntPtr.Size);
                        }
                        catch { }
                    }

                    swFrame = ffmpeg.av_frame_alloc();
                    if (swFrame != null)
                    {
                        int transferRet = ffmpeg.av_hwframe_transfer_data(swFrame, frm, 0);
                        if (transferRet >= 0)
                        {
                            swFrame->pts = frm->pts;
                            displayFrame = swFrame;
                        }
                        else
                        {
                            ffmpeg.av_frame_free(&swFrame);
                        }
                    }
                }

                _currentNV12TexturePtr = nv12TexturePtr;
                _currentTextureArrayIndex = textureArrayIndex;
                _currentIsHardwareFrame = nv12TexturePtr != IntPtr.Zero;

                // ========== YUV420P 数据提取（D3D9 GPU渲染） ==========
                //ExtractYuv420PFrame(displayFrame, _videoWidth, _videoHeight);

                // ========== 更新当前时间 ==========
                if (displayFrame->pts != ffmpeg.AV_NOPTS_VALUE)
                {
                    _currentTimeMs = (long)(displayFrame->pts * _videoTimeBase * 1000);
                    _videoClock = _currentTimeMs; //不要
                       // DebugLogger.WriteLine($"[视频] 第{_frameCount}帧原始 PTS: {_currentTimeMs} ms"); 
                }

                // ========== 解码耗时统计 ==========
                //long frameEndStamp = Stopwatch.GetTimestamp();
                //double decodeMs = (frameEndStamp - frameStartStamp) * 1000.0 / Stopwatch.Frequency;
               // DebugLogger.WriteLine($"[解码耗时] : {decodeMs:F2} ms");

                // ========== 入队（同步逻辑移到显示线程） ==========
                try
                {
                    //var AudioPlayPosition = (long)GetAudioPlaybackPosition2() * 1000;
                    //var diff = _videoClock - AudioPlayPosition;
                    //if (diff > 0) Thread.Sleep((int)diff);

                    //if (diff < 0)
                    //{
                    //    // ========== 清理 ==========
                    //    ffmpeg.av_frame_unref(frm);
                    //    if (swFrame != null)
                    //    {
                    //        ffmpeg.av_frame_free(&swFrame);
                    //    }
                    //    framesDropped = 0;
                    //    _frameCount++;
                    //    continue;
                    //}

                    //// 同步策略
                    //const double MAX_EARLY_MS = 30;     // 视频最多领先30毫秒，视为正常
                    //const double MAX_LATE_MS = 50;       // 视频最多落后50毫秒，视为正常
                    //const double MAX_WAIT_MS = 300;      // 视频如果领先太多，最多等待300毫秒

                    //if (diff > MAX_EARLY_MS)
                    //{
                    //    // 视频快了，需要等待
                    //    double waitTimeMs = Math.Min(diff - MAX_EARLY_MS, MAX_WAIT_MS);
                    //    if (_frameCount % 30 == 0)
                    //    {
                    //        DebugLogger.WriteLine($"[播放同步] 视频快了 {diff:F1}ms，等待 {waitTimeMs:F1}ms ,帧数:{_frameCount}");
                    //    }
                    //    Thread.Sleep((int)waitTimeMs);
                    //    // 解码线程
                    //    byte[] buffer = new byte[_rgbBuffer.Length];  // 每次创建新 buffer
                    //    Buffer.BlockCopy(_rgbBuffer, 0, buffer, 0, _rgbBuffer.Length);

                    //    // 处理负数（第一帧可能稍微靠前）
                    //    _displayQueue.TryAdd(new FrameData
                    //    {
                    //        Width = _videoWidth,
                    //        Height = _videoHeight,
                    //        Data = buffer,  // 注意：需要拷贝数据，否则会被覆盖
                    //        VideoTimestamp = (long)_videoClock,
                    //        AudioTimestamp = (long)_audioClock,
                    //        AudioPlayPosition = AudioPlayPosition
                    //    }, 10, _decodeCts.Token);
                    //    DebugLogger.WriteLine($"[解码]_displayQueue sleep {waitTimeMs:F1}ms后入队,当前数量:{_displayQueue.Count},帧数:{_frameCount} ");

                    //}
                    //else if (diff < -MAX_LATE_MS)
                    //{
                    //    // 视频慢了太多，丢弃此帧
                    //    if (_frameCount % 30 == 0)
                    //    {
                    //        DebugLogger.WriteLine($"[播放同步] 视频慢了 {diff:F1}ms，丢弃当前帧 ,帧数:{_frameCount}");
                    //    }
                    //    // ========== 清理 ==========
                    //    ffmpeg.av_frame_unref(frm);
                    //    if (swFrame != null)
                    //    {
                    //        ffmpeg.av_frame_free(&swFrame);
                    //    }
                    //    framesDropped = 0;
                    //    _frameCount++;
                    //    return;
                    //}
                    //else
                    {

                        // 同步良好，立即显示
                        // 解码线程
                        //byte[] buffer = new byte[_rgbBuffer.Length];  // 每次创建新 buffer
                        //Buffer.BlockCopy(_rgbBuffer, 0, buffer, 0, _rgbBuffer.Length);

                        //// 处理负数（第一帧可能稍微靠前）
                        //_displayQueue.TryAdd(new FrameData
                        //{
                        //    Width = _videoWidth,
                        //    Height = _videoHeight,
                        //    Data = buffer,  // 注意：需要拷贝数据，否则会被覆盖
                        //    VideoTimestamp = (long)_videoClock,
                        //    AudioTimestamp = (long)_audioClock,
                        //    AudioPlayPosition = AudioPlayPosition
                        //}, 10, _decodeCts.Token);

                        DebugLogger.WriteLine($"[解码]_displayQueue 入队正常,当前数量:{_displayQueue.Count},帧数:{_frameCount} ");

                    }




                }
                catch (OperationCanceledException)
                {
                    // 正常取消
                }
              
                // ========== 清理 ==========
                ffmpeg.av_frame_unref(frm);
                if (swFrame != null)
                {
                    ffmpeg.av_frame_free(&swFrame);
                }
                framesDropped = 0;
                _frameCount++;
            }
        }
        private void CheckAudioState()
        {
            if (_audioOutput == null)
            {
                DebugLogger.WriteLine("❌ _waveOut 为 null");
                return;
            }

            DebugLogger.WriteLine($"🎵 音频设备状态: {_audioOutput.PlaybackState}");
            DebugLogger.WriteLine($"🎵 音量: {_audioOutput.Volume}");
            DebugLogger.WriteLine($"🎵 设备号: {_audioOutput.DeviceNumber}");

            if (_audioOutput.PlaybackState != PlaybackState.Playing)
            {
                DebugLogger.WriteLine("❌ 音频设备未处于播放状态!");
                // 尝试重新启动
                try
                {
                    _audioOutput.Play();
                    DebugLogger.WriteLine("🔄 尝试重新启动音频设备");
                }
                catch (Exception ex)
                {
                    DebugLogger.WriteLine($"重启失败: {ex.Message}");
                }
            }
        }
        private DateTime _lastCalibrationTime = DateTime.MinValue;
        private DateTime _playbackStartTime;
        private double _playbackStartPosition = 0;
        private bool _isClockInitialized = false;
        /// <summary>
        /// 获取当前播放时钟（秒）
        /// 优先使用音频时钟作为主时钟进行同步，音频时钟更稳定
        /// </summary>
        /// <returns>当前播放时间（秒）</returns>
        private double GetPlaybackClock()
        { 
            lock (_clockLock)
            {
                if (_isPaused) return _clockBase;
                double hardwarePos = GetHardwarePosition(); // 返回秒，需确保从 0 开始
                return hardwarePos * _playbackSpeed;
            }
        
            //lock (_clockLock)
            //{
            //    if (_isPaused) return _clockBase;

            //    // ✅ 用系统时钟跟踪播放位置
            //    if (!_isClockInitialized)
            //    {
            //        _playbackStartTime = DateTime.Now;
            //        _playbackStartPosition = _audioClock / 1000.0;
            //        _isClockInitialized = true;
            //    }

            //    var elapsed = (DateTime.Now - _playbackStartTime).TotalSeconds;
            //    var clock = _playbackStartPosition + elapsed;

            //    // 定期校准（每 5 秒校准一次）
            //    if ((DateTime.Now - _lastCalibrationTime).TotalSeconds > 5)
            //    {
            //        var hardwarePos = GetHardwarePosition();
            //        if (hardwarePos > 0)
            //        {
            //            // 用硬件位置校准
            //            _playbackStartPosition = hardwarePos;
            //            _playbackStartTime = DateTime.Now;
            //            _lastCalibrationTime = DateTime.Now;
            //        }
            //    }

            //    return clock * _playbackSpeed;
            //}
        }

        private double GetHardwarePosition()
        {
            try
            {
                if (_audioOutput != null && _audioOutput.PlaybackState == PlaybackState.Playing)
                {
                    var position = _audioOutput.GetPosition();          // 已提交的总字节数
                    var bytesPerSecond = _audioBuffer.WaveFormat?.AverageBytesPerSecond ?? 1;
                    var submittedTime = (double)position / bytesPerSecond; // 总提交时长（包含缓冲）

                    // 动态获取当前缓冲区中未播放的时长
                    double bufferDelay = _audioBuffer?.BufferedDuration.TotalSeconds ?? 0;

                    // 实际硬件播放位置 = 提交时长 - 未播放缓冲时长
                    return submittedTime - bufferDelay;
                }
            }
            catch { }
            return 0;
        }

        /// <summary>
        /// 获取音频实际播放位置（秒）
        /// </summary>
        private double GetAudioPlaybackPosition2()
        {
            if (_audioOutput != null && _audioOutput.PlaybackState == PlaybackState.Playing)
            {
                long bytesPlayed = _audioOutput.GetPosition();
                double bytesPerSecond = _sampleRate * 2 * 2;  // 采样率 × 16bit × 2通道
                double audioPlayTime = bytesPlayed / bytesPerSecond;

                // 加上缓冲区延迟，更接近真实播放位置
                double bufferDelay = _audioBuffer?.BufferedDuration.TotalSeconds ?? 0; 
               
                return audioPlayTime + bufferDelay;
            }
            return _audioClock;  // 降级：使用解码器传来的 PTS
        }

        /// <summary>
        /// 解码音频数据包
        /// </summary>
        private unsafe void DecodeAudioPacket()
        {
            if (_audioCodecContext == IntPtr.Zero || _audioFrame == IntPtr.Zero) return;

            var aCodecCtx = (AVCodecContext*)_audioCodecContext;
            var pkt = (AVPacket*)_packet;
            var aFrm = (AVFrame*)_audioFrame;
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
            //DebugLogger.WriteLine($"[音频] 线程ID: {Thread.CurrentThread.ManagedThreadId}, 开始解码");

            int ret = ffmpeg.avcodec_send_packet(aCodecCtx, pkt);
            if (ret < 0) return;

            while (true)
            {
                ret = ffmpeg.avcodec_receive_frame(aCodecCtx, aFrm);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF) break;
                if (ret < 0) break;

                // 获取PTS
                if (aFrm->pts != ffmpeg.AV_NOPTS_VALUE)
                {
                    var fmtCtx = (AVFormatContext*)_formatContext;
                    var audioStream = fmtCtx->streams[_audioStreamIndex];
                    _audioClock = (long)(aFrm->pts * ffmpeg.av_q2d(audioStream->time_base) * 1000);
                }
            
                while (_audioBuffer.BufferedDuration.TotalMilliseconds > 500)
                {
                    Thread.Sleep(5); 
                }

                byte[] outBuffer = ConvertAudioFrame(aFrm);  // 使用上面的转换函数

                if (outBuffer != null && outBuffer.Length > 0)
                {
                    // 检查缓冲区，避免溢出
                    if (_audioBuffer.BufferedDuration.TotalMilliseconds < 2000)
                    { 
                        _audioBuffer.AddSamples(outBuffer, 0, outBuffer.Length);
                        
                        // 每添加一次数据，打印缓冲区状态
                        DebugLogger.WriteLine($"📊 缓冲区: {_audioBuffer.BufferedBytes} bytes, " +
                                        $"时长: {_audioBuffer.BufferedDuration.TotalMilliseconds:F0}ms, " +
                                        $"是否满: {_audioBuffer.BufferedBytes >= _audioBuffer.BufferLength}");

                        // 如果观察到 "是否满: true" 且频繁出现，说明触发溢出了
                    }
                }

                ffmpeg.av_frame_unref(aFrm);
            }
        }

        private unsafe byte[] ConvertAudioFrame(AVFrame* aFrm)
        {
            int channels = aFrm->ch_layout.nb_channels;
            int samples = aFrm->nb_samples;
            byte[] buffer = new byte[samples * 2 * 2];
            float volumeFactor = _volume / 100.0f;

            if (channels == 0 || samples == 0) return buffer;

            int format = aFrm->format;
            ulong channelLayout = aFrm->ch_layout.u.mask;

            // 如果 channelLayout 为 0，使用默认布局
            if (channelLayout == 0)
            {
                channelLayout = GetDefaultChannelLayout(channels);
            }

            // ========== 处理所有格式 ==========
            if (format == (int)AVSampleFormat.AV_SAMPLE_FMT_U8P)
            {
                byte** src = (byte**)&aFrm->data;
                for (int i = 0; i < samples; i++)
                {
                    float[] channelData = new float[channels];
                    for (int c = 0; c < channels; c++)
                    {
                        channelData[c] = (src[c][i] - 128) / 127f;
                    }
                    MixChannelsToStereo(channelData, channelLayout, out float left, out float right);
                    WriteSample(buffer, i, left * volumeFactor, right * volumeFactor);
                }
            }
            else if (format == (int)AVSampleFormat.AV_SAMPLE_FMT_S16P)
            {
                short** src = (short**)&aFrm->data;
                for (int i = 0; i < samples; i++)
                {
                    float[] channelData = new float[channels];
                    for (int c = 0; c < channels; c++)
                    {
                        channelData[c] = src[c][i] / 32767f;
                    }
                    MixChannelsToStereo(channelData, channelLayout, out float left, out float right);
                    WriteSample(buffer, i, left * volumeFactor, right * volumeFactor);
                }
            }
            else if (format == (int)AVSampleFormat.AV_SAMPLE_FMT_S32P)
            {
                int** src = (int**)&aFrm->data;
                for (int i = 0; i < samples; i++)
                {
                    float[] channelData = new float[channels];
                    for (int c = 0; c < channels; c++)
                    {
                        channelData[c] = src[c][i] / 2147483647f;
                    }
                    MixChannelsToStereo(channelData, channelLayout, out float left, out float right);
                    WriteSample(buffer, i, left * volumeFactor, right * volumeFactor);
                }
            }
            else if (format == (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP)
            {
                float** src = (float**)&aFrm->data;
                // 预分配一次，所有采样点复用
                float[] channelData = new float[channels];
                for (int i = 0; i < samples; i++)
                {
                    for (int c = 0; c < channels; c++)
                        channelData[c] = src[c][i];
                    MixChannelsToStereo(channelData, channelLayout, out float left, out float right);
                    WriteSample(buffer, i, left * volumeFactor, right * volumeFactor);
                }
            }
            else if (format == (int)AVSampleFormat.AV_SAMPLE_FMT_DBLP)
            {
                double** src = (double**)&aFrm->data;
                for (int i = 0; i < samples; i++)
                {
                    float[] channelData = new float[channels];
                    for (int c = 0; c < channels; c++)
                    {
                        channelData[c] = (float)src[c][i];
                    }
                    MixChannelsToStereo(channelData, channelLayout, out float left, out float right);
                    WriteSample(buffer, i, left * volumeFactor, right * volumeFactor);
                }
            }
            else if (format == (int)AVSampleFormat.AV_SAMPLE_FMT_U8)
            {
                byte* src = (byte*)aFrm->data[0];
                for (int i = 0; i < samples; i++)
                {
                    float[] channelData = new float[channels];
                    for (int c = 0; c < channels; c++)
                    {
                        channelData[c] = (src[i * channels + c] - 128) / 127f;
                    }
                    MixChannelsToStereo(channelData, channelLayout, out float left, out float right);
                    WriteSample(buffer, i, left * volumeFactor, right * volumeFactor);
                }
            }
            else if (format == (int)AVSampleFormat.AV_SAMPLE_FMT_S16)
            {
                short* src = (short*)aFrm->data[0];
                for (int i = 0; i < samples; i++)
                {
                    float[] channelData = new float[channels];
                    for (int c = 0; c < channels; c++)
                    {
                        channelData[c] = src[i * channels + c] / 32767f;
                    }
                    MixChannelsToStereo(channelData, channelLayout, out float left, out float right);
                    WriteSample(buffer, i, left * volumeFactor, right * volumeFactor);
                }
            }
            else if (format == (int)AVSampleFormat.AV_SAMPLE_FMT_S32)
            {
                int* src = (int*)aFrm->data[0];
                for (int i = 0; i < samples; i++)
                {
                    float[] channelData = new float[channels];
                    for (int c = 0; c < channels; c++)
                    {
                        channelData[c] = src[i * channels + c] / 2147483647f;
                    }
                    MixChannelsToStereo(channelData, channelLayout, out float left, out float right);
                    WriteSample(buffer, i, left * volumeFactor, right * volumeFactor);
                }
            }
            else if (format == (int)AVSampleFormat.AV_SAMPLE_FMT_FLT)
            {
                float* src = (float*)aFrm->data[0];
                for (int i = 0; i < samples; i++)
                {
                    float[] channelData = new float[channels];
                    for (int c = 0; c < channels; c++)
                    {
                        channelData[c] = src[i * channels + c];
                    }
                    MixChannelsToStereo(channelData, channelLayout, out float left, out float right);
                    WriteSample(buffer, i, left * volumeFactor, right * volumeFactor);
                }
            }
            else if (format == (int)AVSampleFormat.AV_SAMPLE_FMT_DBL)
            {
                double* src = (double*)aFrm->data[0];
                for (int i = 0; i < samples; i++)
                {
                    float[] channelData = new float[channels];
                    for (int c = 0; c < channels; c++)
                    {
                        channelData[c] = (float)src[i * channels + c];
                    }
                    MixChannelsToStereo(channelData, channelLayout, out float left, out float right);
                    WriteSample(buffer, i, left * volumeFactor, right * volumeFactor);
                }
            }
            else
            {
                DebugLogger.WriteLine($"⚠️ 未知音频格式: {format}");
                return buffer;
            }

            return buffer;
        }

        // ========== 声道掩码常量 ==========
        private const ulong AV_CH_FRONT_LEFT = 0x1;
        private const ulong AV_CH_FRONT_RIGHT = 0x2;
        private const ulong AV_CH_FRONT_CENTER = 0x4;
        private const ulong AV_CH_LOW_FREQUENCY = 0x8;
        private const ulong AV_CH_BACK_LEFT = 0x10;
        private const ulong AV_CH_BACK_RIGHT = 0x20;
        private const ulong AV_CH_FRONT_LEFT_OF_CENTER = 0x40;
        private const ulong AV_CH_FRONT_RIGHT_OF_CENTER = 0x80;
        private const ulong AV_CH_BACK_CENTER = 0x100;
        private const ulong AV_CH_SIDE_LEFT = 0x200;
        private const ulong AV_CH_SIDE_RIGHT = 0x400;
        private const ulong AV_CH_TOP_CENTER = 0x800;
        private const ulong AV_CH_TOP_FRONT_LEFT = 0x1000;
        private const ulong AV_CH_TOP_FRONT_RIGHT = 0x2000;
        private const ulong AV_CH_TOP_FRONT_CENTER = 0x4000;
        private const ulong AV_CH_TOP_BACK_LEFT = 0x8000;
        private const ulong AV_CH_TOP_BACK_RIGHT = 0x10000;
        private const ulong AV_CH_TOP_BACK_CENTER = 0x20000;

        // ========== 获取默认声道布局 ==========
        private ulong GetDefaultChannelLayout(int channels)
        {
            switch (channels)
            {
                case 1: return AV_CH_FRONT_LEFT;
                case 2: return AV_CH_FRONT_LEFT | AV_CH_FRONT_RIGHT;
                case 3: return AV_CH_FRONT_LEFT | AV_CH_FRONT_RIGHT | AV_CH_FRONT_CENTER;
                case 4: return AV_CH_FRONT_LEFT | AV_CH_FRONT_RIGHT | AV_CH_BACK_LEFT | AV_CH_BACK_RIGHT;
                case 5: return AV_CH_FRONT_LEFT | AV_CH_FRONT_RIGHT | AV_CH_FRONT_CENTER | AV_CH_BACK_LEFT | AV_CH_BACK_RIGHT;
                case 6: return AV_CH_FRONT_LEFT | AV_CH_FRONT_RIGHT | AV_CH_FRONT_CENTER | AV_CH_LOW_FREQUENCY | AV_CH_BACK_LEFT | AV_CH_BACK_RIGHT;
                case 7: return AV_CH_FRONT_LEFT | AV_CH_FRONT_RIGHT | AV_CH_FRONT_CENTER | AV_CH_LOW_FREQUENCY | AV_CH_BACK_LEFT | AV_CH_BACK_RIGHT | AV_CH_BACK_CENTER;
                case 8: return AV_CH_FRONT_LEFT | AV_CH_FRONT_RIGHT | AV_CH_FRONT_CENTER | AV_CH_LOW_FREQUENCY | AV_CH_BACK_LEFT | AV_CH_BACK_RIGHT | AV_CH_SIDE_LEFT | AV_CH_SIDE_RIGHT;
                default: return AV_CH_FRONT_LEFT | AV_CH_FRONT_RIGHT;
            }
        }

        // ========== 声道混合核心方法 ==========
        private void MixChannelsToStereo(float[] channelData, ulong channelLayout, out float left, out float right)
        {
            left = 0;
            right = 0;

            int channels = channelData.Length;

            for (int c = 0; c < channels; c++)
            {
                float val = channelData[c];
                ulong bit = 1UL << c;

                // 检查这个声道是否在布局中
                if ((channelLayout & bit) == 0) continue;

                // ========== 根据声道类型分配 ==========
                // 左声道
                if (bit == AV_CH_FRONT_LEFT || bit == AV_CH_SIDE_LEFT || bit == AV_CH_BACK_LEFT ||
                    bit == AV_CH_TOP_FRONT_LEFT || bit == AV_CH_TOP_BACK_LEFT)
                {
                    left += val;
                }
                // 右声道
                else if (bit == AV_CH_FRONT_RIGHT || bit == AV_CH_SIDE_RIGHT || bit == AV_CH_BACK_RIGHT ||
                         bit == AV_CH_TOP_FRONT_RIGHT || bit == AV_CH_TOP_BACK_RIGHT)
                {
                    right += val;
                }
                // 中置声道（人声主要在这里）
                else if (bit == AV_CH_FRONT_CENTER || bit == AV_CH_BACK_CENTER || bit == AV_CH_TOP_CENTER || bit == AV_CH_TOP_FRONT_CENTER || bit == AV_CH_TOP_BACK_CENTER)
                {
                    float gain = 0.7f;
                    left += val * gain;
                    right += val * gain;
                }
                // LFE（低音炮）
                else if (bit == AV_CH_LOW_FREQUENCY)
                {
                    float gain = 0.3f;
                    left += val * gain;
                    right += val * gain;
                }
                // 前左中/前右中
                else if (bit == AV_CH_FRONT_LEFT_OF_CENTER)
                {
                    left += val * 0.5f;
                }
                else if (bit == AV_CH_FRONT_RIGHT_OF_CENTER)
                {
                    right += val * 0.5f;
                }
                else
                {
                    // 未知声道，平均分配到左右
                    float gain = 0.3f;
                    left += val * gain;
                    right += val * gain;
                }
            }

            // 限制范围
            left = Math.Clamp(left, -1f, 1f);
            right = Math.Clamp(right, -1f, 1f);
        }

        // ========== 写入样本 ==========
        private unsafe void WriteSample(byte[] buffer, int index, float left, float right)
        {
            // 限制范围
            left = Math.Clamp(left, -1f, 1f);
            right = Math.Clamp(right, -1f, 1f);

            short l = (short)(left * 32767);
            short r = (short)(right * 32767);

            int pos = index * 4;
            buffer[pos] = (byte)(l & 0xFF);
            buffer[pos + 1] = (byte)((l >> 8) & 0xFF);
            buffer[pos + 2] = (byte)(r & 0xFF);
            buffer[pos + 3] = (byte)((r >> 8) & 0xFF);
        }
        private unsafe void InitSwrContext(AVCodecContext* codecCtx)
        {
            try
            {
                // 1. 目标格式参数（我们想要的输出格式）
                AVSampleFormat targetSampleFormat = AVSampleFormat.AV_SAMPLE_FMT_S16;  // 16-bit 交错
                int targetSampleRate = 48000;  // 目标采样率
                int targetChannels = 2;  // 立体声

                // 2. 设置目标声道布局（使用新 API）
                AVChannelLayout targetChannelLayout = new AVChannelLayout();
                ffmpeg.av_channel_layout_default(&targetChannelLayout, targetChannels);

                // 3. 源格式参数（从解码器获得）
                AVSampleFormat sourceSampleFormat = codecCtx->sample_fmt;
                int sourceSampleRate = codecCtx->sample_rate;

                // 4. 设置源声道布局
                AVChannelLayout sourceChannelLayout;
                if (codecCtx->ch_layout.order != AVChannelOrder.AV_CHANNEL_ORDER_UNSPEC)
                {
                    // 如果解码器已经设置了布局，直接使用
                    sourceChannelLayout = codecCtx->ch_layout;
                }
                else
                {
                    // 否则根据声道数创建默认布局
                    ffmpeg.av_channel_layout_default(&sourceChannelLayout, codecCtx->ch_layout.nb_channels);
                }

                // 5. 打印调试信息
                DebugLogger.WriteLine($"[重采样] 源: {sourceSampleRate}Hz, {codecCtx->ch_layout.nb_channels}声道, 格式={sourceSampleFormat}");
                DebugLogger.WriteLine($"[重采样] 目标: {targetSampleRate}Hz, {targetChannels}声道, 格式={targetSampleFormat}");

                // 6. 分配重采样上下文
                SwrContext* swrContext = ffmpeg.swr_alloc();
                if (swrContext == null)
                {
                    DebugLogger.WriteLine("[重采样] 分配重采样上下文失败");
                    return;
                }

                // 7. 设置重采样参数（使用 av_opt_set_* 系列函数）
                // 源参数
                ffmpeg.av_opt_set_chlayout(swrContext, "in_channel_layout", &sourceChannelLayout, 0);
                ffmpeg.av_opt_set_int(swrContext, "in_sample_rate", sourceSampleRate, 0);
                ffmpeg.av_opt_set_sample_fmt(swrContext, "in_sample_fmt", sourceSampleFormat, 0);

                // 目标参数
                ffmpeg.av_opt_set_chlayout(swrContext, "out_channel_layout", &targetChannelLayout, 0);
                ffmpeg.av_opt_set_int(swrContext, "out_sample_rate", targetSampleRate, 0);
                ffmpeg.av_opt_set_sample_fmt(swrContext, "out_sample_fmt", targetSampleFormat, 0);

                // 8. 初始化重采样器
                int ret = ffmpeg.swr_init(swrContext);
                if (ret < 0)
                {
                    DebugLogger.WriteLine($"[重采样] 初始化失败: {ret}");
                    ffmpeg.swr_free(&swrContext);
                    lock (_audioCodecLock)
                    {
                        _swrContext = IntPtr.Zero;
                    }
                    return;
                }

                // 9. 保存重采样上下文
                lock (_audioCodecLock)
                {
                    _swrContext = (IntPtr)swrContext;
                }
                DebugLogger.WriteLine("[重采样] 初始化成功");

                // 10. 可选：保存目标格式参数供后续使用
                _sampleRate = targetSampleRate;
                //_targetChannels = targetChannels; 
              //  _targetSampleFormat = targetSampleFormat;
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[重采样] 异常: {ex.Message}");
                _swrContext = IntPtr.Zero;
            }
        }
        private void CheckAudioPlayback()
        {
            if (_audioOutput == null)
            {
                DebugLogger.WriteLine("[音频] 播放器未初始化");
                return;
            }

            DebugLogger.WriteLine($"[音频] 播放器状态: {_audioOutput.PlaybackState}");

            if (_audioOutput.PlaybackState == PlaybackState.Playing)
            {
                try
                {
                    long bytes = _audioOutput.GetPosition();
                    var format = _audioOutput.OutputWaveFormat;
                    double seconds = (double)bytes / format.AverageBytesPerSecond;
                    DebugLogger.WriteLine($"[音频] 播放位置: {seconds:F2}秒 ({bytes} bytes)");
                }
                catch (Exception ex)
                {
                    DebugLogger.WriteLine($"[音频] 获取位置失败: {ex.Message}");
                }
            }
            else if (_audioOutput.PlaybackState == PlaybackState.Stopped)
            {
                DebugLogger.WriteLine("[音频] 播放器已停止，尝试重新启动");
                _audioOutput.Play();
            }
            else if (_audioOutput.PlaybackState == PlaybackState.Paused)
            {
                DebugLogger.WriteLine("[音频] 播放器已暂停");
            }
        }

        // 定期检查
        private async Task MonitorAudioLoop()
        {
            while (_isPlaying)
            {
                await Task.Delay(2000);
                CheckAudioPlayback();
            }
        }
        private void CheckAudioBufferStatus()
        {
            if (_audioBuffer != null)
            {
                DebugLogger.WriteLine($"[音频] 缓冲区状态:");
                DebugLogger.WriteLine($"  - 已缓冲字节: {_audioBuffer.BufferedBytes}");
                DebugLogger.WriteLine($"  - 已缓冲时长: {_audioBuffer.BufferedDuration.TotalMilliseconds}ms");
                DebugLogger.WriteLine($"  - 缓冲区容量: {_audioBuffer.BufferDuration.TotalMilliseconds}ms");
            }
        }

        private unsafe void ExecuteSeekNow(double position)
        {
            try
            {
                lock (_seekLock)
                {
                    if (_formatContext == IntPtr.Zero || _videoStreamIndex < 0)
                        return;

                    var fmtCtx = (AVFormatContext*)_formatContext;
                    if (fmtCtx == null)
                        return;

                    long targetPts = (long)(position * ffmpeg.AV_TIME_BASE);
                    int ret = ffmpeg.av_seek_frame(fmtCtx, -1, targetPts, ffmpeg.AVSEEK_FLAG_BACKWARD);
                    if (ret < 0)
                    {
                        DebugLogger.WriteLine($"[FFmpeg] av_seek_frame failed: {ret}");
                        return;
                    }

                    if (_videoCodecContext != IntPtr.Zero)
                    {
                        var vCtx = (AVCodecContext*)_videoCodecContext;
                        if (vCtx != null)
                            ffmpeg.avcodec_flush_buffers(vCtx);
                    }

                    if (_audioCodecContext != IntPtr.Zero)
                    {
                        var aCtx = (AVCodecContext*)_audioCodecContext;
                        if (aCtx != null)
                            ffmpeg.avcodec_flush_buffers(aCtx);
                    }

                    _currentTimeMs = (long)(position * 1000);
                    _clockBase = position;
                    _clockStartTicks = Stopwatch.GetTimestamp();
                    DebugLogger.WriteLine($"[FFmpeg] Seek to: {position}s");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[FFmpeg] ExecuteSeekNow exception: {ex.Message}");
            }
            finally
            {
                _isSeeking = false;
            }
        }

        public void Pause()
        {
            if (_isPlaying && !_isPaused)
            {
                _isPaused = true;
                _clockBase = GetPlaybackClock();
                DebugLogger.WriteLine("[FFmpeg] Paused");
            }
        }

        public void Resume()
        {
            if (_isPlaying && _isPaused)
            {
                _isPaused = false;
                _clockStartTicks = Stopwatch.GetTimestamp();
                DebugLogger.WriteLine("[FFmpeg] Resumed");
            }
        }

        public void Seek(double position)
        {
            if (position < 0) return;
            
            _pendingSeekTime = position;
            _pendingSeek = true;
            DebugLogger.WriteLine($"[FFmpeg] Seek requested to: {position}s");
        }

        /// <summary>
        /// 获取当前音量值（0-100）
        /// </summary>
        public int Volume => _volume;

        /// <summary>
        /// 设置音量
        /// </summary>
        /// <param name="volume">音量值（0-100）</param>
        public void SetVolume(int volume)
        {
            _volume = Math.Clamp(volume, 0, 100);
            DebugLogger.WriteLine($"[FFmpeg] Volume set to: {_volume}");
        }

        private async Task StopInternalAsync()
        {
            DebugLogger.WriteLine("[FFmpeg] StopInternalAsync - 开始停止");

            if (_playTask == null && !_isPlaying && _demuxTask == null) return;

            _isPlaying = false;
            _isPaused = false;

            // 第一步：取消所有令牌，触发解码循环退出
            DebugLogger.WriteLine("[FFmpeg] 取消所有令牌...");
            _playCts?.Cancel();       // 会连锁取消 _demuxCts, _audioCts, _videoCts, _subtitleCts
            _displayCts?.Cancel();    // 停止显示线程
            _decodeCts?.Cancel();     // 取消解码循环
            DebugLogger.WriteLine("[FFmpeg] 所有令牌已取消");

            // 第二步：完成显示队列，确保显示线程不会阻塞在 TryTake
            _displayQueue?.CompleteAdding();
            DebugLogger.WriteLine("[FFmpeg] 显示队列已完成");

            // 第三步：等待所有异步任务完成
            DebugLogger.WriteLine("[FFmpeg] 等待所有异步任务完成...");
            var allTasks = new List<Task>();
            if (_playTask != null) allTasks.Add(_playTask);
            if (_demuxTask != null) allTasks.Add(_demuxTask);
            if (_audioTask != null) allTasks.Add(_audioTask);
            if (_videoTask != null) allTasks.Add(_videoTask);
            if (_subtitleTask != null) allTasks.Add(_subtitleTask);

            if (allTasks.Count > 0)
            {
                try
                {
                    await Task.WhenAny(Task.WhenAll(allTasks), Task.Delay(5000));
                    DebugLogger.WriteLine("[FFmpeg] 所有异步任务已完成");
                }
                catch (OperationCanceledException)
                {
                    DebugLogger.WriteLine("[FFmpeg] 任务已取消（正常）");
                }
                catch (Exception ex)
                {
                    DebugLogger.WriteLine($"[FFmpeg] 等待任务异常: {ex.Message}");
                }
            }
            DebugLogger.WriteLine("[FFmpeg] 异步任务等待结束");

            // 第四步：等待显示线程结束
            DebugLogger.WriteLine("[FFmpeg] 等待显示线程...");
            if (_displayThread != null && _displayThread.IsAlive)
            {
                _displayThread.Join(3000);
                DebugLogger.WriteLine("[FFmpeg] 显示线程已结束");
            }

            // 第五步：释放所有资源
            DebugLogger.WriteLine("[FFmpeg] 释放资源...");
            _playTask?.Dispose();
            _playCts?.Dispose();
            _demuxTask?.Dispose();
            _audioTask?.Dispose();
            _videoTask?.Dispose();
            _subtitleTask?.Dispose();
            _demuxCts?.Dispose();
            _audioCts?.Dispose();
            _videoCts?.Dispose();
            _subtitleCts?.Dispose();
            _displayCts?.Dispose();
            _decodeCts?.Dispose();
            _displayQueue?.Dispose();

            // 重置引用
            _playTask = null;
            _playCts = null;
            _demuxTask = null;
            _audioTask = null;
            _videoTask = null;
            _subtitleTask = null;
            _demuxCts = null;
            _audioCts = null;
            _videoCts = null;
            _subtitleCts = null;
            _displayCts = null;
            _decodeCts = null;
            _displayQueue = null;
            _currentTimeMs = 0;

            DebugLogger.WriteLine("[FFmpeg] 资源释放完成，开始清理");

            Cleanup();
            DebugLogger.WriteLine("[FFmpeg] StopInternalAsync - 停止完成");
        }

        public async Task StopAsync()
        {
            await StopInternalAsync();
        }

        private unsafe void Cleanup()
        {
            try
            {
                // 释放 D3D11 硬解私有纹理池
                for (int i = 0; i < HwCopyTextureSlots; i++)
                {
                    _hwCopyTextures[i]?.Dispose();
                    _hwCopyTextures[i] = null;
                }

                _systemMemorySurface?.Dispose();
                _renderTargetTexture?.Dispose();
                if (_videoFrame != IntPtr.Zero)
                {
                    var frame = (AVFrame*)_videoFrame;
                    ffmpeg.av_frame_free(&frame);
                    _videoFrame = IntPtr.Zero;
                }

                if (_audioFrame != IntPtr.Zero)
                {
                    var frame = (AVFrame*)_audioFrame;
                    ffmpeg.av_frame_free(&frame);
                    _audioFrame = IntPtr.Zero;
                }

                if (_packet != IntPtr.Zero)
                {
                    var pkt = (AVPacket*)_packet;
                    ffmpeg.av_packet_free(&pkt);
                    _packet = IntPtr.Zero;
                }

                if (_swsContext != IntPtr.Zero)
                {
                    var ctx = (SwsContext*)_swsContext;
                    ffmpeg.sws_freeContext(ctx);
                    _swsContext = IntPtr.Zero;
                }

                if (_swrContext != IntPtr.Zero)
                {
                    lock (_audioCodecLock)
                    {
                        var ctx = (SwrContext*)_swrContext;
                        ffmpeg.swr_free(&ctx);
                        _swrContext = IntPtr.Zero;
                    }
                }

                if (_videoCodecContext != IntPtr.Zero)
                {
                    var ctx = (AVCodecContext*)_videoCodecContext;
                    ffmpeg.avcodec_free_context(&ctx);
                    _videoCodecContext = IntPtr.Zero;
                }

                if (_audioCodecContext != IntPtr.Zero)
                {
                    var ctx = (AVCodecContext*)_audioCodecContext;
                    ffmpeg.avcodec_free_context(&ctx);
                    _audioCodecContext = IntPtr.Zero;
                }

                if (_subtitleCodecContext != IntPtr.Zero)
                {
                    var ctx = (AVCodecContext*)_subtitleCodecContext;
                    ffmpeg.avcodec_free_context(&ctx);
                    _subtitleCodecContext = IntPtr.Zero;
                }

                if (_formatContext != IntPtr.Zero)
                {
                    var ctx = (AVFormatContext*)_formatContext;
                    ffmpeg.avformat_close_input(&ctx);
                    ffmpeg.avformat_free_context(ctx);
                    _formatContext = IntPtr.Zero;
                }
                if (_hwDeviceContext != IntPtr.Zero)
                {
                    AVBufferRef* hwDeviceCtx = (AVBufferRef*)_hwDeviceContext;
                    ffmpeg.av_buffer_unref(&hwDeviceCtx);
                    _hwDeviceContext = IntPtr.Zero;
                }
                // 清理音频播放器
                if (_audioOutput != null)
                {
                    try
                    {
                        _audioOutput.Stop();
                        _audioOutput.Dispose();
                     
                        DebugLogger.WriteLine("[FFmpeg] Audio player disposed");
                    }
                    catch { }
                    _audioOutput = null;
                }
                
                _audioBuffer = null;
            }
            catch { }
        }

        /// <summary>
        /// 截图功能
        /// </summary>
        public void TakeScreenshot()
        {
            try
            {
                // 截图功能暂时不实现
                DebugLogger.WriteLine("[FFmpeg] TakeScreenshot called (not implemented)");
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[FFmpeg] TakeScreenshot error: {ex.Message}");
            }
        }

        /// <summary>
        /// 设置播放速度
        /// </summary>
        public void SetPlaybackSpeed(double speed)
        {
            try
            {
                _playbackSpeed = speed;
                DebugLogger.WriteLine($"[FFmpeg] Playback speed set to {speed}x");
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[FFmpeg] SetPlaybackSpeed error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            //Stop();
            Cleanup();
        }

        private void UpdateDecodePerformance(double decodeTimeMs)
        {
            lock (_decodeTimeHistory)
            {
                _decodeTimeHistory.Enqueue(decodeTimeMs);
                if (_decodeTimeHistory.Count > MAX_DECODE_HISTORY)
                {
                    _decodeTimeHistory.Dequeue();
                }

                if (_decodeTimeHistory.Count > 0)
                {
                    _avgDecodeTimeMs = _decodeTimeHistory.Average();
                }
            }
            _frameCount++;
            if (_frameCount % 30 == 0)
            {
                double targetTimeMs = 1000.0 / _fps;
                //DebugLogger.WriteLine($"[FFmpeg] Performance - Frame: {_frameCount}, Decode: {decodeTimeMs:F2}ms, Avg: {_avgDecodeTimeMs:F2}ms, Target: {targetTimeMs:F2}ms");
            }
        }

        private bool CheckPerformanceDegradation()
        {
            if (_performanceWarningSent)
            {
                return false;
            }

            long now = Stopwatch.GetTimestamp();
            if (now - _lastPerformanceCheck < Stopwatch.Frequency * 2)
            {
                return false;
            }
            _lastPerformanceCheck = now;

            double targetDecodeTimeMs = 1000.0 / _fps;
            double thresholdMs = targetDecodeTimeMs * 3;

            if (_decodeTimeHistory.Count >= MAX_DECODE_HISTORY / 2 && _avgDecodeTimeMs > thresholdMs)
            {
                _isPerformanceDegraded = true;
                return true;
            }

            return false;
        }

        private void SendPerformanceWarning()
        {
            if (_performanceWarningSent) return;

            _performanceWarningSent = true;

            int suggestedWidth = _videoWidth;
            int suggestedHeight = _videoHeight;

            if (_videoWidth >= 3840)
            {
                suggestedWidth = 1920;
                suggestedHeight = (int)(_videoHeight * (1920.0 / _videoWidth));
            }
            else if (_videoWidth >= 2560)
            {
                suggestedWidth = 1920;
                suggestedHeight = (int)(_videoHeight * (1920.0 / _videoWidth));
            }
            else if (_videoWidth >= 1920)
            {
                suggestedWidth = 1280;
                suggestedHeight = (int)(_videoHeight * (1280.0 / _videoWidth));
            }

            var warning = new DecodePerformanceWarning
            {
                Message = $"解码性能不足！当前平均解码时间 {_avgDecodeTimeMs:F1}ms，超过目标 {1000.0/_fps:F1}ms 的3倍。建议降低分辨率以获得流畅播放体验。",
                CurrentWidth = _videoWidth,
                CurrentHeight = _videoHeight,
                SuggestedWidth = suggestedWidth,
                SuggestedHeight = suggestedHeight,
                AverageDecodeTimeMs = _avgDecodeTimeMs,
                TargetFps = _fps
            };

            DebugLogger.WriteLine($"[FFmpeg] Performance warning: {warning.Message}");
            PerformanceWarning?.Invoke(this, warning);
        }

        public void ResetPerformanceWarning()
        {
            _performanceWarningSent = false;
            _isPerformanceDegraded = false;
            lock (_decodeTimeHistory)
            {
                _decodeTimeHistory.Clear();
            }
            _avgDecodeTimeMs = 0;
        }

        public List<AudioTrackInfo> GetAudioTracks() => _audioTracks;
        public List<SubtitleTrackInfo> GetSubtitleTracks() => _subtitleTracks;

        public int CurrentAudioTrack => _audioStreamIndex;
        public int CurrentSubtitleTrack => _subtitleStreamIndex;
        /// <summary>
        /// 音频正在切换true 是 false 否
        /// </summary>
        private volatile bool _audioTrackSwitchInProgress;

        public unsafe void SetAudioTrack(int streamIndex)
        {
            // 0. 播放状态检查：如果正在停止或已停止，不执行切换
            if (_formatContext == IntPtr.Zero || !_isPlaying)
            {
                DebugLogger.WriteLine("[FFmpeg] SetAudioTrack 取消: 播放器未运行");
                return;
            }

            // 1. 验证流索引是否在音频轨道列表中
            if (_audioTracks == null || streamIndex < 0)
                return;

            // 查找轨道列表中是否有此流索引
            bool found = false;
            foreach (var t in _audioTracks)
            {
                if (t.Index == streamIndex) { found = true; break; }
            }
            if (!found)
            {
                DebugLogger.WriteLine($"[FFmpeg] 音频流索引 {streamIndex} 不在轨道列表中");
                return;
            }

            int newStreamIndex = streamIndex;
            if (newStreamIndex == _audioStreamIndex)
                return;

            DebugLogger.WriteLine($"[FFmpeg] 切换音频轨道: {_audioStreamIndex} → {newStreamIndex}");

            var fmtCtx = (AVFormatContext*)_formatContext;
            if (fmtCtx == null) return;

            AVStream* newStream = fmtCtx->streams[newStreamIndex];
            if (newStream == null)
            {
                DebugLogger.WriteLine($"[FFmpeg] 获取流 {newStreamIndex} 失败");
                return;
            }

            // 2. 记录当前播放位置（毫秒），转为 Seek 所需单位（假设 Seek 接受秒）
            long currentPosMs = _currentTimeMs;
            double seekSeconds = currentPosMs / 1000.0;

            // 保存旧索引，初始化失败时恢复
            int oldStreamIndex = _audioStreamIndex;

            // 3. 停止解码线程对音频资源的访问（使用轻量级切换锁）
            _audioTrackSwitchInProgress = true;
            try
            {
                // 等待解码线程完成当前包（如果正在解码音频包）
                Thread.Sleep(50);

                // 清空音频包队列（防止旧包被处理）
                while (_audioPacketQueue.Reader.TryRead(out _)) { }
                DebugLogger.WriteLine("[FFmpeg] 音频包队列已清空");

                // 4. 在释放资源前再次检查播放状态（防止 Stop 并发执行）
                if (_formatContext == IntPtr.Zero || !_isPlaying)
                {
                    DebugLogger.WriteLine("[FFmpeg] 播放器已停止，取消音频切换");
                    return;
                }

                // 5. 释放旧的音频解码器与相关资源（加锁保护）
                lock (_audioCodecLock)
                {
                    if (_audioCodecContext != IntPtr.Zero)
                    {
                        var oldCtx = (AVCodecContext*)_audioCodecContext;
                        ffmpeg.avcodec_free_context(&oldCtx);
                        _audioCodecContext = IntPtr.Zero;
                    }
                    if (_audioFrame != IntPtr.Zero)
                    {
                        var frame = (AVFrame*)_audioFrame;
                        ffmpeg.av_frame_free(&frame);
                        _audioFrame = IntPtr.Zero;
                    }
                    if (_swrContext != IntPtr.Zero)
                    {
                        var swr = (SwrContext*)_swrContext;
                        ffmpeg.swr_free(&swr);
                        _swrContext = IntPtr.Zero;
                    }
                    DebugLogger.WriteLine("[FFmpeg] 旧音频解码器已释放");
                }

                // 6. 更新当前音频流索引
                _audioStreamIndex = newStreamIndex;

                // 7. 暂停并释放旧的音频输出设备（InitializeAudioDecoder 会创建新的）
                if (_audioOutput != null)
                {
                    try { _audioOutput.Pause(); _audioOutput.Stop(); _audioOutput.Dispose(); } catch { }
                    _audioOutput = null;
                    _audioBuffer = null;
                }

                // 8. 重新检查播放状态（Stop 可能在释放旧解码器后执行，_formatContext 已释放）
                if (_formatContext == IntPtr.Zero || !_isPlaying)
                {
                    DebugLogger.WriteLine("[FFmpeg] 播放器已停止，取消音频切换");
                    return;
                }

                // 9. 重新初始化解码器（内部调用 InitializeAudioPlayer 创建正确格式的音频输出）
                try
                {
                    InitializeAudioDecoder(newStream);
                }
                catch (Exception ex)
                {
                    DebugLogger.WriteLine($"[FFmpeg] InitializeAudioDecoder 异常: {ex.Message}");
                    // 如果播放还在，恢复旧索引
                    if (_isPlaying && _formatContext != IntPtr.Zero)
                    {
                        _audioStreamIndex = oldStreamIndex;
                    }
                    return;
                }

                // 检查初始化是否成功（可能被 Stop 并发重置）
                if (_audioCodecContext == IntPtr.Zero || _formatContext == IntPtr.Zero || !_isPlaying)
                {
                    DebugLogger.WriteLine("[FFmpeg] 音频切换中断（初始化失败或播放已停止）");
                    return;
                }

                // 10. Seek 到切换前的位置
                if (seekSeconds > 0)
                {
                    Seek(seekSeconds);
                }

                // 10. 恢复音频播放
                if (_audioOutput != null)
                {
                    _audioOutput.Play();
                    DebugLogger.WriteLine("[FFmpeg] 音频播放已恢复");
                }
            }
            finally
            {
                _audioTrackSwitchInProgress = false;   // 允许解码线程继续工作
            }

            DebugLogger.WriteLine($"[FFmpeg] 音频轨道切换成功");
        }
        public unsafe void SetSubtitleTrack(int index)
        {
            // -1 表示关闭字幕
            if (index == -1)
            {
                _subtitleStreamIndex = -1;
                // 清空字幕队列，避免旧字幕包继续被处理
                if (_subtitlePacketQueue != null)
                {
                    while (_subtitlePacketQueue.Reader.TryRead(out _)) { }
                }
                DebugLogger.WriteLine("[FFmpeg] Subtitle track disabled");
                return;
            }

            _subtitleStreamIndex = index;
            DebugLogger.WriteLine($"[FFmpeg] Subtitle track changed to {index}");

            // 清空字幕队列，避免旧字幕流的数据包被新解码器处理
            if (_subtitlePacketQueue != null)
            {
                while (_subtitlePacketQueue.Reader.TryRead(out _)) { }
            }

            // 重新初始化解码器
            unsafe
            {
                var fmtCtx = (AVFormatContext*)_formatContext;
                if (fmtCtx != null && _subtitleStreamIndex >= 0 && _subtitleStreamIndex < fmtCtx->nb_streams)
                {
                    var stream = fmtCtx->streams[_subtitleStreamIndex];
                    InitializeSubtitleDecoder(stream);
                    DebugLogger.WriteLine($"[FFmpeg] Subtitle decoder reinitialized for track {index}");
                }
            }
        }

        /// <summary>
        /// 设置播放速度
        /// </summary>
        public void SetSpeed(double speed)
        {
            if (speed < 0.1 || speed > 4.0)
            {
                DebugLogger.WriteLine($"[FFmpeg] Invalid speed value: {speed}, must be between 0.1 and 4.0");
                return;
            }
            
            _playbackSpeed = speed;
            DebugLogger.WriteLine($"[FFmpeg] Playback speed set to {speed}x");
            
            // 调整音频输出速度
            if (_audioOutput != null)
            {
                try
                {
                    // NAudio支持通过修改WaveFormat调整播放速度
                    _audioOutput.Volume = speed > 0.5f ? 1.0f : (float)speed * 2.0f;
                }
                catch { }
            }
        }

        /// <summary>
        /// 保存当前帧为截图
        /// </summary>
        public string? SaveScreenshot(string? outputPath)
        {
            try
            {
                if (_displayQueue == null || _displayQueue.Count == 0)
                {
                    DebugLogger.WriteLine("[FFmpeg] No frame available for screenshot");
                    return null;
                }

                // 获取最新帧
                if (!_displayQueue.TryTake(out var frame, 100))
                {
                    DebugLogger.WriteLine("[FFmpeg] Failed to get frame for screenshot");
                    return null;
                }

                if (frame.Data == null || frame.Width <= 0 || frame.Height <= 0)
                {
                    DebugLogger.WriteLine("[FFmpeg] Invalid frame data for screenshot");
                    return null;
                }

                // 如果没有指定路径，生成默认路径
                if (string.IsNullOrEmpty(outputPath))
                {
                    string saveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "MovieAgent");
                    Directory.CreateDirectory(saveDir);
                    outputPath = Path.Combine(saveDir, $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                }

                // 将BGR数据转换为PNG并保存
                SaveBgrToPng(frame.Data, frame.Width, frame.Height, outputPath);
                
                // 把帧放回队列（不影响播放）
                _displayQueue.TryAdd(frame, 0);

                DebugLogger.WriteLine($"[FFmpeg] Screenshot saved to: {outputPath}");
                return outputPath;
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[FFmpeg] Screenshot error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 将BGR格式的帧数据保存为PNG文件
        /// </summary>
        private unsafe void SaveBgrToPng(byte[] bgrData, int width, int height, string outputPath)
        {
            // 使用SkiaSharp或System.Drawing保存PNG
            // 将BGR转换为BMP格式，然后保存
        
            int stride = width * 3;
            int imageSize = stride * height;
            
            using (var fs = new FileStream(outputPath, FileMode.Create))
            using (var bw = new BinaryWriter(fs))
            {
                // BMP文件头
                bw.Write((byte)0x42); bw.Write((byte)0x4D); // "BM"
                bw.Write(54 + imageSize); // 文件大小
                bw.Write(0); // 保留
                bw.Write(54); // 数据偏移

                // 信息头
                bw.Write(40); // 信息头大小
                bw.Write(width);
                bw.Write(-height); // 负数表示从上到下（BGR数据已经是正序）
                bw.Write((short)1); // 平面数
                bw.Write((short)24); // 位深度
                bw.Write(0); // 压缩
                bw.Write(imageSize);
                bw.Write(0); // 水平分辨率
                bw.Write(0); // 垂直分辨率
                bw.Write(0); // 颜色数
                bw.Write(0); // 重要颜色数

                // 像素数据
                bw.Write(bgrData, 0, bgrData.Length);
            }
        }

        /// <summary>
        /// 设置字幕延迟
        /// </summary>
        public void SetSubtitleDelay(double delayMs)
        {
            _subtitleDelayMs = delayMs;
            DebugLogger.WriteLine($"[FFmpeg] Subtitle delay set to {delayMs}ms");
        }
        
        /// <summary>
        /// 安全获取FFmpeg元数据字符串
        /// FFmpeg元数据通常是UTF-8编码，需要正确处理中文
        /// </summary>
        private unsafe string SafeGetMetadataString(AVDictionaryEntry* entry)
        {
            if (entry == null)
                return string.Empty;
            
            try
            {
                // 获取字符串长度
                byte* str = entry->value;
                int len = 0;
                while (str[len] != 0)
                    len++;
                
                if (len == 0)
                    return string.Empty;
                
                // 先尝试用UTF-8解码
                byte[] bytes = new byte[len];
                for (int i = 0; i < len; i++)
                    bytes[i] = str[i];
                
                // 尝试UTF-8解码
                try
                {
                    return System.Text.Encoding.UTF8.GetString(bytes).Trim();
                }
                catch
                {
                    // UTF-8失败，尝试Latin-1
                    try
                    {
                        return System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(bytes).Trim();
                    }
                    catch
                    {
                        // 最后的备选方案
                        return System.Text.Encoding.ASCII.GetString(bytes).Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[FFmpeg] SafeGetMetadataString error: {ex.Message}");
                return string.Empty;
            }
        }
        
        /// <summary>
        /// 格式化音频轨道描述
        /// 格式：普通话-中文-AAC-2频道
        /// </summary>
        private string FormatAudioTrackDescription(string title, string language, string codec, int channels)
        {
            // 语言代码转换为中文显示
            string languageDisplay = GetLanguageDisplayName(language);
            
            // 编码格式转换为友好名称
            string codecDisplay = GetCodecDisplayName(codec);
            
            // 声道数转换为频道显示
            string channelsDisplay = GetChannelsDisplayName(channels);
            
            // 如果有标题，使用标题-语言-编码-频道格式
            if (!string.IsNullOrEmpty(title))
            {
                return $"{title}-{languageDisplay}-{codecDisplay}-{channelsDisplay}";
            }
            
            // 否则使用语言-编码-频道格式
            return $"{languageDisplay}-{codecDisplay}-{channelsDisplay}";
        }
        
        /// <summary>
        /// 获取语言显示名称
        /// </summary>
        private string GetLanguageDisplayName(string languageCode)
        {
            if (string.IsNullOrEmpty(languageCode))
                return "未知";
            
            return languageCode.ToLowerInvariant() switch
            {
                "zh" or "chi" or "chinese" or "zho" => "中文",
                "zh-cn" or "zh_cn" => "中文",
                "zh-tw" or "zh_tw" => "繁体中文",
                "en" or "eng" or "english" => "英语",
                "ja" or "jpn" or "japanese" => "日语",
                "ko" or "kor" or "korean" => "韩语",
                "fr" or "fra" or "french" => "法语",
                "de" or "deu" or "german" => "德语",
                "es" or "spa" or "spanish" => "西班牙语",
                "ru" or "rus" or "russian" => "俄语",
                "it" or "ita" or "italian" => "意大利语",
                "pt" or "por" or "portuguese" => "葡萄牙语",
                "th" or "tha" or "thai" => "泰语",
                "vi" or "vie" or "vietnamese" => "越南语",
                "hi" or "hin" or "hindi" => "印地语",
                "ar" or "ara" or "arabic" => "阿拉伯语",
                "und" => "未知",
                "null" or "NULL" or "" => "未知",
                "yue" or "cantonese" or "zho-yue" => "粤语",
                "nl" or "dut" or "dutch" => "荷兰语",
                "pl" or "pol" or "polish" => "波兰语",
                "sv" or "swe" or "swedish" => "瑞典语",
                "tr" or "tur" or "turkish" => "土耳其语",
                "el" or "gre" or "greek" => "希腊语",
                "he" or "heb" or "hebrew" => "希伯来语",
                "cs" or "ces" or "czech" => "捷克语",
                "hu" or "hun" or "hungarian" => "匈牙利语",
                "ro" or "ron" or "romanian" => "罗马尼亚语",
                "id" or "ind" or "indonesian" => "印尼语",
                "ms" or "may" or "malay" => "马来语",
                "tl" or "fil" or "filipino" => "菲律宾语",
                _ => languageCode.ToUpperInvariant()
            };
        }
        
        /// <summary>
        /// 获取编码格式显示名称
        /// </summary>
        private string GetCodecDisplayName(string codec)
        {
            if (string.IsNullOrEmpty(codec))
                return "未知";
            
            return codec.ToLowerInvariant() switch
            {
                "aac" => "AAC",
                "ac3" => "Dolby Audio",
                "eac3" => "Dolby Digital Plus",
                "dts" => "DTS",
                "dtshd" or "dts-hd" => "DTS-HD",
                "truehd" => "TrueHD",
                "flac" => "FLAC",
                "mp3" or "mp3float" => "MP3",
                "pcm" or "pcm_s16be" or "pcm_s16le" or "pcm_s24be" or "pcm_s24le" or "pcm_s32be" or "pcm_s32le" => "PCM",
                "opus" => "Opus",
                "vorbis" => "Vorbis",
                "wmapro" => "WMA Pro",
                _ => codec.ToUpperInvariant()
            };
        }
        
        /// <summary>
        /// 获取声道数显示名称
        /// </summary>
        private string GetChannelsDisplayName(int channels)
        {
            return channels switch
            {
                1 => "单声道",
                2 => "2频道",
                3 => "3频道",
                4 => "4频道",
                5 => "5频道",
                6 => "6频道",
                7 => "7频道",
                8 => "8频道",
                _ => $"{channels}频道"
            };
        }

        #region BDMV/ISO 蓝光光盘支持

        /// <summary>
        /// 检测是否为 BDMV 蓝光光盘结构
        /// </summary>
        private bool IsBdmvStructure(string path)
        {
            // 检查是否有 BDMV 文件夹结构
            string bdmvDir = Path.Combine(path, "BDMV");
            if (Directory.Exists(bdmvDir))
            {
                // 检查 index.bdmv 或 MovieObject.bdmv
                if (File.Exists(Path.Combine(bdmvDir, "index.bdmv")) ||
                    File.Exists(Path.Combine(bdmvDir, "MovieObject.bdmv")))
                {
                    return true;
                }
            }
            // 也可能是直接指向 BDMV 文件夹
            if (Path.GetFileName(path).Equals("BDMV", StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(Path.Combine(path, "index.bdmv")) ||
                    File.Exists(Path.Combine(path, "MovieObject.bdmv")))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 打开 ISO 文件（挂载为虚拟驱动器后播放）
        /// </summary>
        private unsafe bool OpenIsoFile(string isoPath)
        {
            DebugLogger.WriteLine($"[BDMV] Opening ISO file: {isoPath}");
            
            string mountPoint = MountIsoFile(isoPath);
            if (string.IsNullOrEmpty(mountPoint))
            {
                DebugLogger.WriteLine("[BDMV] Failed to mount ISO file");
                return false;
            }

            DebugLogger.WriteLine($"[BDMV] ISO mounted at: {mountPoint}");
            
            bool result = OpenBdmvFolder(mountPoint);
            
            if (!result)
            {
                // 如果BDMV方式失败，尝试直接打开ISO作为普通文件
                DebugLogger.WriteLine("[BDMV] BDMV open failed, trying direct ISO open");
                result = OpenFileDirectly(isoPath);
            }

            return result;
        }

        /// <summary>
        /// 挂载 ISO 文件为虚拟驱动器
        /// </summary>
        private string MountIsoFile(string isoPath)
        {
            try
            {
                // 使用 PowerShell 挂载 ISO
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -Command \"$result = Mount-DiskImage -ImagePath '{isoPath}' -PassThru; $drive = ($result | Get-Volume).DriveLetter; if ($drive) {{ $drive + ':' }}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = System.Diagnostics.Process.Start(psi);
                if (process == null)
                {
                    DebugLogger.WriteLine("[BDMV] Failed to start PowerShell for ISO mount");
                    return null;
                }

                string output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(10000);

                if (!string.IsNullOrEmpty(output) && output.Length >= 2)
                {
                    string mountPoint = output + "\\";
                    DebugLogger.WriteLine($"[BDMV] ISO mounted at: {mountPoint}");
                    return mountPoint;
                }
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[BDMV] Mount ISO error: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// 打开 BDMV 蓝光文件夹
        /// </summary>
        private unsafe bool OpenBdmvFolder(string folderPath)
        {
            DebugLogger.WriteLine($"[BDMV] Opening BDMV folder: {folderPath}");
            
            // 找到 BDMV 目录
            string bdmvDir = folderPath;
            if (!Path.GetFileName(bdmvDir).Equals("BDMV", StringComparison.OrdinalIgnoreCase))
            {
                bdmvDir = Path.Combine(bdmvDir, "BDMV");
            }
            
            if (!Directory.Exists(bdmvDir))
            {
                DebugLogger.WriteLine($"[BDMV] BDMV directory not found: {bdmvDir}");
                return false;
            }

            DebugLogger.WriteLine($"[BDMV] BDMV directory: {bdmvDir}");

            // 查找主播放列表
            string mainPlaylist = FindMainPlaylist(bdmvDir);
            if (string.IsNullOrEmpty(mainPlaylist))
            {
                DebugLogger.WriteLine("[BDMV] No playlists found, trying direct M2TS files");
                return OpenLargestM2tsFile(bdmvDir);
            }

            DebugLogger.WriteLine($"[BDMV] Main playlist: {mainPlaylist}");

            // 解析播放列表获取 M2TS 文件列表
            var m2tsFiles = ParsePlaylist(mainPlaylist);
            if (m2tsFiles == null || m2tsFiles.Count == 0)
            {
                DebugLogger.WriteLine("[BDMV] Failed to parse playlist, trying direct M2TS files");
                return OpenLargestM2tsFile(bdmvDir);
            }

            DebugLogger.WriteLine($"[BDMV] Found {m2tsFiles.Count} M2TS files in playlist");

            if (m2tsFiles.Count == 1)
            {
                return OpenFileDirectly(m2tsFiles[0]);
            }

            // 多个 M2TS 文件，使用 concat 协议
            return OpenConcatFiles(m2tsFiles);
        }

        /// <summary>
        /// 查找主播放列表（最大的 MPLS 文件）
        /// </summary>
        private string FindMainPlaylist(string bdmvDir)
        {
            string playlistDir = Path.Combine(bdmvDir, "PLAYLIST");
            if (!Directory.Exists(playlistDir))
            {
                DebugLogger.WriteLine("[BDMV] PLAYLIST directory not found");
                return null;
            }

            var mplsFiles = Directory.GetFiles(playlistDir, "*.mpls");
            if (mplsFiles.Length == 0)
            {
                DebugLogger.WriteLine("[BDMV] No MPLS files found");
                return null;
            }

            // 找到最大的播放列表文件（通常是主电影）
            string mainPlaylist = null;
            long maxSize = 0;
            foreach (var file in mplsFiles)
            {
                var info = new FileInfo(file);
                if (info.Length > maxSize)
                {
                    maxSize = info.Length;
                    mainPlaylist = file;
                }
            }

            DebugLogger.WriteLine($"[BDMV] Main playlist: {mainPlaylist} ({maxSize} bytes)");
            return mainPlaylist;
        }

        /// <summary>
        /// 解析 MPLS 播放列表，获取 M2TS 文件路径列表
        /// </summary>
        private List<string> ParsePlaylist(string playlistPath)
        {
            var m2tsFiles = new List<string>();
            try
            {
                byte[] data = File.ReadAllBytes(playlistPath);
                string streamDir = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(playlistPath)), "STREAM");
                
                if (!Directory.Exists(streamDir))
                {
                    streamDir = Path.Combine(Path.GetDirectoryName(playlistPath), "..", "STREAM");
                }

                // MPLS 文件结构：前4字节是类型标识，偏移某些位置有文件名
                // 搜索 .m2ts 文件引用
                using var ms = new MemoryStream(data);
                using var reader = new BinaryReader(ms);
                
                // 读取偏移信息
                if (data.Length >= 8)
                {
                    // 简化解析：扫描文件中的 5 位数字（M2TS 文件名格式）
                    // 完整的 MPLS 解析比较复杂，这里使用简化方法
                    int index = 0;
                    while (index < data.Length - 5)
                    {
                        // 检查是否是有效的 M2TS 文件名标记
                        // M2TS 文件名通常是 5 位数字，如 00000.m2ts，00800.m2ts
                        string m2tsName = $"{System.Text.Encoding.ASCII.GetString(data, index, 5).Trim()}.m2ts";
                        string m2tsPath = Path.Combine(streamDir, m2tsName);
                        
                        if (File.Exists(m2tsPath) && !m2tsFiles.Contains(m2tsPath))
                        {
                            m2tsFiles.Add(m2tsPath);
                            index += 5;
                        }
                        else
                        {
                            index++;
                        }
                    }
                }

                DebugLogger.WriteLine($"[BDMV] Parsed {m2tsFiles.Count} M2TS files from playlist");
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[BDMV] Parse playlist error: {ex.Message}");
            }

            return m2tsFiles;
        }

        /// <summary>
        /// 打开最大的 M2TS 文件（作为后备方案）
        /// </summary>
        private unsafe bool OpenLargestM2tsFile(string bdmvDir)
        {
            string streamDir = Path.Combine(bdmvDir, "STREAM");
            if (!Directory.Exists(streamDir))
            {
                DebugLogger.WriteLine("[BDMV] STREAM directory not found");
                return false;
            }

            var m2tsFiles = Directory.GetFiles(streamDir, "*.m2ts");
            if (m2tsFiles.Length == 0)
            {
                DebugLogger.WriteLine("[BDMV] No M2TS files found");
                return false;
            }

            // 找到最大的 M2TS 文件（通常是主电影）
            string largestFile = null;
            long maxSize = 0;
            foreach (var file in m2tsFiles)
            {
                var info = new FileInfo(file);
                if (info.Length > maxSize)
                {
                    maxSize = info.Length;
                    largestFile = file;
                }
            }

            DebugLogger.WriteLine($"[BDMV] Largest M2TS: {largestFile} ({maxSize} bytes)");
            return OpenFileDirectly(largestFile);
        }

        /// <summary>
        /// 使用 concat 协议打开多个 M2TS 文件
        /// </summary>
        private unsafe bool OpenConcatFiles(List<string> files)
        {
            try
            {
                // 创建临时 concat 列表文件
                string tempFile = Path.Combine(Path.GetTempPath(), $"movieagent_concat_{Guid.NewGuid()}.txt");
                var lines = new List<string>();
                foreach (var file in files)
                {
                    lines.Add($"file '{file.Replace("\\", "\\\\")}'");
                }
                File.WriteAllLines(tempFile, lines);

                DebugLogger.WriteLine($"[BDMV] Concat file list created: {tempFile}");

                // 使用 concat 协议打开
                return OpenFileDirectly($"concat:{tempFile}");
            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[BDMV] Concat open error: {ex.Message}");
                // 后备方案：打开第一个文件
                if (files.Count > 0)
                {
                    return OpenFileDirectly(files[0]);
                }
                return false;
            }
        }

        /// <summary>
        /// 直接打开文件（使用标准 FFmpeg 流程）
        /// </summary>
        private unsafe bool OpenFileDirectly(string filePath)
        {
            AVFormatContext* fmtCtx = ffmpeg.avformat_alloc_context();
            _formatContext = (IntPtr)fmtCtx;
            if (fmtCtx == null)
            {
                DebugLogger.WriteLine("[BDMV] Failed to allocate format context");
                return false;
            }

            AVDictionary* options = null;
            ffmpeg.av_dict_set(&options, "buffer_size", "1024000", 0);
            ffmpeg.av_dict_set(&options, "probesize", "5000000", 0);
            ffmpeg.av_dict_set(&options, "analyzeduration", "3000000", 0);

            if (ffmpeg.avformat_open_input(&fmtCtx, filePath, null, &options) != 0)
            {
                DebugLogger.WriteLine($"[BDMV] Failed to open: {filePath}");
                return false;
            }
            _formatContext = (IntPtr)fmtCtx;

            if (ffmpeg.avformat_find_stream_info(fmtCtx, null) < 0)
            {
                DebugLogger.WriteLine("[BDMV] Failed to find stream info");
                return false;
            }

            return InitializeStreamsFromFormatContext(fmtCtx);
        }

        /// <summary>
        /// 从格式上下文初始化流索引和轨道信息
        /// </summary>
        private unsafe bool InitializeStreamsFromFormatContext(AVFormatContext* fmtCtx)
        {
            _videoStreamIndex = -1;
            _audioStreamIndex = -1;
            _subtitleStreamIndex = -1;
            _audioTracks.Clear();
            _subtitleTracks.Clear();

            for (int i = 0; i < (int)fmtCtx->nb_streams; i++)
            {
                var stream = fmtCtx->streams[i];
                var codecParams = stream->codecpar;
                var codec = ffmpeg.avcodec_find_decoder(codecParams->codec_id);

                if (codecParams->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && _videoStreamIndex < 0)
                {
                    _videoStreamIndex = i;
                    _videoTimeBase = ffmpeg.av_q2d(stream->time_base);
                    NewInitializeVideoDecoder(stream);
                }
                else if (codecParams->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    string codecName = codec != null ? Marshal.PtrToStringAnsi((IntPtr)codec->name) : null;
                    int channels = codecParams->ch_layout.nb_channels;
                    
                    AVDictionaryEntry* langTag = ffmpeg.av_dict_get(stream->metadata, "language", null, 0);
                    string language = langTag != null && langTag->value != null ? 
                        Marshal.PtrToStringAnsi((IntPtr)langTag->value) : "未知";
                    
                    AVDictionaryEntry* titleTag = ffmpeg.av_dict_get(stream->metadata, "title", null, 0);
                    string title = titleTag != null && titleTag->value != null ? 
                        Marshal.PtrToStringAnsi((IntPtr)titleTag->value) : null;

                    _audioTracks.Add(new AudioTrackInfo
                    {
                        Index = i,
                        Language = language,
                        Codec = codecName,
                        Channels = channels,
                        Description = FormatAudioTrackDescription(title, language, codecName, channels)
                    });

                    if (_audioStreamIndex < 0)
                    {
                        _audioStreamIndex = i;
                    }
                }
                else if (codecParams->codec_type == AVMediaType.AVMEDIA_TYPE_SUBTITLE)
                {
                    AVDictionaryEntry* langTag = ffmpeg.av_dict_get(stream->metadata, "language", null, 0);
                    string language = langTag != null && langTag->value != null ? 
                        Marshal.PtrToStringAnsi((IntPtr)langTag->value) : "未知";

                    AVDictionaryEntry* titleTag = ffmpeg.av_dict_get(stream->metadata, "title", null, 0);
                    string title = titleTag != null && titleTag->value != null ? 
                        Marshal.PtrToStringAnsi((IntPtr)titleTag->value) : null;

                    _subtitleTracks.Add(new SubtitleTrackInfo
                    {
                        Index = i,
                        Language = language,
                        Description = FormatSubtitleTrackDescription(title, language)
                    });

                    if (_subtitleStreamIndex < 0)
                    {
                        _subtitleStreamIndex = i;
                    }
                }
            }

            // 计算视频时长
            _durationMs = (long)(fmtCtx->duration / (double)ffmpeg.AV_TIME_BASE * 1000);
            if (_durationMs <= 0 && _videoStreamIndex >= 0)
            {
                var videoStream = fmtCtx->streams[_videoStreamIndex];
                if (videoStream->duration != ffmpeg.AV_NOPTS_VALUE)
                {
                    _durationMs = (long)(videoStream->duration * ffmpeg.av_q2d(videoStream->time_base) * 1000);
                }
            }

            DebugLogger.WriteLine($"[BDMV] Duration: {_durationMs}ms");
            DebugLogger.WriteLine($"[BDMV] Video: {_videoStreamIndex}, Audio: {_audioStreamIndex}, Subtitle: {_subtitleStreamIndex}");

            // 初始化音频解码器
            if (_audioStreamIndex >= 0 && _audioStreamIndex < fmtCtx->nb_streams)
            {
                InitializeAudioDecoder(fmtCtx->streams[_audioStreamIndex]);
            }

            return true;
        }

        private string FormatSubtitleTrackDescription(string title, string language)
        {
            string languageDisplay = GetLanguageDisplayName(language);
            if (!string.IsNullOrEmpty(title))
                return $"{title} ({languageDisplay})";
            return languageDisplay;
        }

        #endregion
    }

    /// <summary>
    /// 解码帧数据类
    /// 包含视频帧的像素数据和时间戳信息
    /// </summary>
    public class FrameData
    {
        /// <summary>
        /// 帧宽度（像素）
        /// </summary>
        public int Width { get; set; }

        /// <summary>
        /// 帧高度（像素）
        /// </summary>
        public int Height { get; set; }

        /// <summary>
        /// 帧像素数据（BGR24格式，兼容旧渲染器）
        /// </summary>
        public byte[] Data { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// YUV420P Y平面数据（D3D9渲染用）
        /// </summary>
        public byte[] YPlane { get; set; } = Array.Empty<byte>();
        
        /// <summary>
        /// YUV420P U平面数据（D3D9渲染用）
        /// </summary>
        public byte[] UPlane { get; set; } = Array.Empty<byte>();
        
        /// <summary>
        /// YUV420P V平面数据（D3D9渲染用）
        /// </summary>
        public byte[] VPlane { get; set; } = Array.Empty<byte>();
        
        /// <summary>
        /// Y平面行跨度
        /// </summary>
        public int YStride { get; set; }
        
        /// <summary>
        /// U平面行跨度
        /// </summary>
        public int UStride { get; set; }
        
        /// <summary>
        /// V平面行跨度
        /// </summary>
        public int VStride { get; set; }

        /// <summary>
        /// 视频时间戳（毫秒）
        /// </summary>
        public long VideoTimestamp { get; set; }

        /// <summary>
        /// 音频时间戳（毫秒）
        /// </summary>
        public long AudioTimestamp { get; set; }

        /// <summary>
        /// 音频播放位置（毫秒）
        /// </summary>
        public long AudioPlayPosition { get; set; }

        /// <summary>
        /// 是否为硬件帧 (D3D11VA解码输出)
        /// </summary>
        public bool IsHardwareFrame { get; set; }

        /// <summary>
        /// D3D11VA 硬件解码 NV12 纹理指针 (零拷贝用)
        /// </summary>
        public IntPtr NV12TexturePtr { get; set; }

        /// <summary>
        /// NV12 纹理数组索引 (D3D11VA 纹理数组中的索引)
        /// </summary>
        public uint TextureArrayIndex { get; set; }
        public bool IsTextureArray { get; set; }
    }

    /// <summary>
    /// 数据包数据类
    /// 用于解复用线程和解码线程之间传递数据包
    /// </summary>
    public class PacketData : IDisposable
    {
        /// <summary>
        /// 数据包数据
        /// </summary>
        public byte[] Data { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// 数据包大小
        /// </summary>
        public int Size { get; set; }

        /// <summary>
        /// 时间戳（PTS）
        /// </summary>
        public long PTS { get; set; }

        /// <summary>
        /// 流索引
        /// </summary>
        public int StreamIndex { get; set; }

        /// <summary>
        /// 是否为关键帧
        /// </summary>
        public bool IsKeyFrame { get; set; }

        /// <summary>
        /// 时间基（用于PTS转换）
        /// </summary>
        public double TimeBase { get; set; }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            if (Data != null)
            {
                Array.Clear(Data, 0, Data.Length);
                Data = Array.Empty<byte>();
            }
        }
    }

    /// <summary>
    /// 解码器状态类
    /// 用于报告当前播放状态
    /// </summary>
    public class DecoderStatus
    {
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
    /// 解码性能警告类
    /// 当解码性能下降时用于通知上层
    /// </summary>
    public class DecodePerformanceWarning
    {
        /// <summary>
        /// 警告消息
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// 当前解码宽度
        /// </summary>
        public int CurrentWidth { get; set; }

        /// <summary>
        /// 当前解码高度
        /// </summary>
        public int CurrentHeight { get; set; }

        /// <summary>
        /// 建议的降级宽度
        /// </summary>
        public int SuggestedWidth { get; set; }

        /// <summary>
        /// 建议的降级高度
        /// </summary>
        public int SuggestedHeight { get; set; }

        /// <summary>
        /// 平均解码时间（毫秒）
        /// </summary>
        public double AverageDecodeTimeMs { get; set; }

        /// <summary>
        /// 目标帧率（fps）
        /// </summary>
        public double TargetFps { get; set; }
    }

    /// <summary>
    /// 分辨率降级信息类
    /// 当检测到需要降低分辨率时用于通知上层
    /// </summary>
    public class ResolutionDownscaleInfo
    {
        /// <summary>
        /// 通知消息
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// 原始宽度
        /// </summary>
        public int OriginalWidth { get; set; }

        /// <summary>
        /// 原始高度
        /// </summary>
        public int OriginalHeight { get; set; }

        /// <summary>
        /// 目标宽度（降级后）
        /// </summary>
        public int TargetWidth { get; set; }

        /// <summary>
        /// 目标高度（降级后）
        /// </summary>
        public int TargetHeight { get; set; }

        /// <summary>
        /// 降级原因
        /// </summary>
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    /// 字幕数据类
    /// 包含解码后的字幕文本和显示时间信息
    /// </summary>
    public class SubtitleData
    {
        /// <summary>
        /// 字幕文本内容
        /// </summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>
        /// 开始显示时间（秒）
        /// </summary>
        public double StartTime { get; set; }

        /// <summary>
        /// 结束显示时间（秒）
        /// </summary>
        public double EndTime { get; set; }
    }

    /// <summary>
    /// 音频轨道信息类
    /// 描述媒体文件中的音频流信息
    /// </summary>
    public class AudioTrackInfo
    {
        /// <summary>
        /// 轨道索引
        /// </summary>
        public int Index { get; set; }

        /// <summary>
        /// 语言代码（如 zh, en, und）
        /// </summary>
        public string? Language { get; set; }

        /// <summary>
        /// 编解码器名称
        /// </summary>
        public string? Codec { get; set; }

        /// <summary>
        /// 声道数
        /// </summary>
        public int Channels { get; set; }

        /// <summary>
        /// 轨道描述信息
        /// </summary>
        public string? Description { get; set; }
    }
    public enum VideoScaleMode
    {
        Fit,
        Stretch,
        Zoom
    }
    /// <summary>
    /// 对应 FFmpeg 的 AVD3D12VADeviceContext
    /// 用于设置 AVHWDeviceContext.hwctx
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AVD3D12VADeviceContext
    {
        public IntPtr device;          // ID3D12Device*
        public IntPtr video_device;    // ID3D12VideoDevice*（可留空，由 FFmpeg 自动填充）
        public IntPtr @lock;           // 函数指针，可留空
        public IntPtr unlock;          // 函数指针，可留空
        public IntPtr lock_ctx;        // 上下文指针，可留空
        public uint resource_flags;    // D3D12_RESOURCE_FLAGS，默认为 0
        public uint heap_flags;        // D3D12_HEAP_FLAGS，默认为 0
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct AVD3D12VASyncContext
    {
        public IntPtr fence;      // ID3D12Fence*
        public IntPtr @event;     // HANDLE
        public ulong fence_value;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AVD3D12VAFrame
    {
        public IntPtr texture;                // ID3D12Resource*
        public int subresource_index;         // int
        public AVD3D12VASyncContext sync_ctx; // 内嵌结构体
        public uint flags;                    // AVD3D12VAFrameFlags (枚举)
    }

    public enum AVD3D12VAFrameFlags : uint
    {
        None = 0,
        TextureArray = 1 << 1,
    }

    /// <summary>
    /// 字幕轨道信息类
    /// 描述媒体文件中的字幕流信息
    /// </summary>
    public class SubtitleTrackInfo
    {
        /// <summary>
        /// 轨道索引
        /// </summary>
        public int Index { get; set; }

        /// <summary>
        /// 语言代码（如 zh, en, und）
        /// </summary>
        public string? Language { get; set; }

        /// <summary>
        /// 编解码器名称
        /// </summary>
        public string? Codec { get; set; }

        /// <summary>
        /// 是否为强制字幕
        /// </summary>
        public bool IsForced { get; set; }

        /// <summary>
        /// 轨道描述信息
        /// </summary>
        public string? Description { get; set; }
    }
}