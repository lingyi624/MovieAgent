using FFmpeg.AutoGen;
using NAudio.Wave;
using System;
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
using static MovieAgent.FFmpegDecoder.FFmpegDecoderEngine;

namespace MovieAgent.FFmpegDecoder
{
    /// <summary>
    /// FFmpeg解码器引擎
    /// 负责音视频解码、硬件加速选择、音视频同步和性能监控
    /// </summary>
    public class FFmpegDecoderEngine : IDisposable
    {
        #region FFmpeg原生上下文指针

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
        /// Seek稳定期标志，用于Seek后等待帧稳定
        /// </summary>
        private bool _isSeekingStabilizing = false;

        /// <summary>
        /// 稳定期帧计数
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
        /// 容量为30帧，防止内存溢出
        /// </summary>
        private BlockingCollection<FrameData> _displayQueue = new BlockingCollection<FrameData>(30);

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

        #endregion

        #region 解码模式与性能

        /// <summary>
        /// 当前解码模式（自动/硬件/软件），决定使用硬件还是软件解码
        /// </summary>
        private DecodeMode _decodeMode = DecodeMode.Auto;

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

        #endregion

        #region 属性

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

        /// <summary>
        /// 已解码帧数
        /// </summary>
        public int FrameCount => _frameCount;

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

        #endregion

        #region 构造函数

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="mode">解码模式，默认为自动选择</param>
        public FFmpegDecoderEngine(DecodeMode mode = DecodeMode.Auto)
        {
            _decodeMode = mode;
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

                if (!File.Exists(Path.Combine(ffmpeg.RootPath, "avcodec-62.dll")))
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
                        DebugLogger.WriteLine($"[FFmpeg] WARNING: FFmpeg libraries not found in any path!");
                    }
                }

                var version = ffmpeg.av_version_info();
                DebugLogger.WriteLine($"[FFmpeg] Version: {version}");
                  _detector = new HardwareAccelerationDetector("MovieAgentPlayer"); ;
                DebugLogger.WriteLine($"[FFmpeg] 硬件检测初始化完成.");
                // 初始化时启动显示线程
                _displayThread = new Thread(() =>
                {
                    try
                    {
                        foreach (var frame in _displayQueue.GetConsumingEnumerable(_displayCts.Token))
                        {
                            if (!_isPlaying)
                            {
                                Thread.Sleep(10);
                                continue;
                            }

                            // 音视频同步：根据音频播放位置延迟显示帧
                            double currentAudioPos = GetAudioPlaybackPosition2();
                            double frameTime = frame.AudioTimestamp / 1000.0;
                            double diff = frameTime - currentAudioPos;

                            // 如果帧太早到达，等待
                            if (diff > 0.01 && diff < 1.0)
                            {
                                int waitMs = (int)(diff * 1000);
                                Thread.Sleep(waitMs);
                            }

                            FrameDecoded?.Invoke(this, frame);

                            // 限制帧率，避免显示过快
                            Thread.Sleep(1);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        DebugLogger.WriteLine("[FFmpeg] Display thread 正常停止");
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.WriteLine($"[FFmpeg] Display thread error: {ex.Message}"); 
                    }
                });

                _displayThread.IsBackground = true;
                _displayThread.Start();

            }
            catch (Exception ex)
            {
                DebugLogger.WriteLine($"[FFmpeg] Initialization error: {ex.Message}");
                DebugLogger.WriteLine($"[FFmpeg] Stack trace: {ex.StackTrace}");
            }
        }
    
        /// <summary>
        /// 开始播放视频
        /// </summary>
        /// <param name="filePath">视频文件路径</param>
        /// <returns>任务</returns>
        /// <exception cref="InvalidOperationException">当无法打开视频文件时抛出</exception>
        public async Task PlayAsync(string filePath)
        {
            DebugLogger.WriteLine($"[FFmpeg] Starting playback for file: {filePath}");
            
            await StopInternalAsync();

            bool success = OpenFile(filePath);
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

       
            // 启动音频播放器
            if (_audioOutput != null && _audioBuffer != null)
            {
                _audioBuffer.ClearBuffer();
                _audioOutput.Play();
                DebugLogger.WriteLine("[FFmpeg] Audio playback started");
            }

            DebugLogger.WriteLine("[FFmpeg] Starting decode loop...");
            var ct = _playCts.Token;
            _playTask = Task.Run(() => DecodeLoopAsync(ct), ct);

           // await MonitorAudioLoop();
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
                bool exists = File.Exists(filePath);
                DebugLogger.WriteLine($"[FFmpeg] File exists check: {exists}");
                
                if (!exists)
                {
                    DebugLogger.WriteLine($"[FFmpeg] File not found: {filePath}");
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

                _durationMs = (long)(fmtCtx->duration / (double)ffmpeg.AV_TIME_BASE * 1000);

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
                        string language = "null";
                         
                 

                         _audioTracks.Add(new AudioTrackInfo
                        {
                            Index = i,
                            Language = language,
                            Codec = codecName,
                            Channels = channels,
                            Description = $"轨道 {i} - {codecName} - {channels} channels"
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
                        string language = "null";  
                    
                        AVDictionaryEntry* langTag = ffmpeg.av_dict_get(stream->metadata, "language", null, i);
                        if (langTag != null)
                        {
                            language = Marshal.PtrToStringAnsi((IntPtr)langTag->value);
                        }

                        _subtitleTracks.Add(new SubtitleTrackInfo
                        {
                            Index = i,
                            Language = language,
                            Codec = codecName,
                            IsForced = isForced,
                            Description = $"{(isForced ? "[强制] " : "")}字幕 {i} - {codecName}"
                        });

                        if (_subtitleStreamIndex < 0 && !isForced)
                        {
                            _subtitleStreamIndex = i;
                        }
                    }
                }

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
                vCodecCtx->thread_count = Math.Max(1, Environment.ProcessorCount - 1);

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

            // 例如：超过 4K (3840x2160 = 8,294,400 像素) 就降级
            const long fourK = 3840 * 2160;

            // 可以根据实际性能调整
            if (totalPixels > fourK)
            {
                DebugLogger.WriteLine($"[FFmpeg] Resolution {width}x{height} exceeds 4K, will downscale for performance");
                return true;
            }

            // 也可以根据用户设置
           // return _preferLowResolution;
           return false; // 默认不降级，除非超过 4K
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
            _videoWidth = vCodecCtx->width;
            _videoHeight = vCodecCtx->height;

            DebugLogger.WriteLine($"[FFmpeg] Decoder output resolution: {_videoWidth}x{_videoHeight}");

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
            // 注意：如果已经是目标分辨率，可以直接用 BGR24 输出
            int swsWidth = _videoWidth;
            int swsHeight = _videoHeight;

            if (needDownscale)
            {
                swsWidth = targetWidth;
                swsHeight = targetHeight;
            }

            _swsContext = (IntPtr)ffmpeg.sws_getContext(
                _videoWidth, _videoHeight, vCodecCtx->pix_fmt,
                swsWidth, swsHeight, AVPixelFormat.AV_PIX_FMT_BGR24,
                1, null, null, null);

            _rgbBuffer = new byte[swsWidth * swsHeight * 3];
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

            // 目标分辨率（如果需要降级）
            int targetWidth = codecParams->width;
            int targetHeight = codecParams->height;
            bool needDownscale = ShouldDownscaleResolution(codecParams->width, codecParams->height);

            if (needDownscale)
            {
                targetWidth = 1920;
                targetHeight = 1080;
                DebugLogger.WriteLine($"[FFmpeg] Will downscale to {targetWidth}x{targetHeight}");
            }

            // 检测硬件支持
            var hwSupport = CheckHardwareAccelerationSupport(codecParams->codec_id).GetAwaiter().GetResult();
            bool hwAvailable = hwSupport.Item1;
            AVHWDeviceType? hwType = hwSupport.Item2;

            // 用于存储硬件设备上下文（新式方式）
            AVBufferRef* hwDeviceCtx = null;

            if (hwAvailable && hwType.HasValue)
            {
                _cachedDeviceType = hwType.Value;

                // 使用新式 hwaccel 方式：软件解码器 + 硬件设备
                codec = ffmpeg.avcodec_find_decoder(codecParams->codec_id);

                if (codec != null)
                {
                    // 创建硬件设备上下文
                    int ret = ffmpeg.av_hwdevice_ctx_create(&hwDeviceCtx, hwType.Value, null, null, 0);
                    if (ret >= 0)
                    {
                        _decodeMode = DecodeMode.Hardware;
                        DebugLogger.WriteLine($"[FFmpeg] ✅ Hardware device created: {ffmpeg.av_hwdevice_get_type_name(hwType.Value)}");
                    }
                    else
                    {
                        DebugLogger.WriteLine($"[FFmpeg] ❌ Failed to create hardware device");
                        hwDeviceCtx = null;
                        codec = null;
                    }
                }
            }

            // 如果没有硬件或创建失败，回退到软件
            if (codec == null)
            {
                _decodeMode = DecodeMode.Software;
                codec = ffmpeg.avcodec_find_decoder(codecParams->codec_id);
                DebugLogger.WriteLine("[FFmpeg] Using software decoding");
            }

            if (codec == null)
            {
                DebugLogger.WriteLine("[FFmpeg] Video codec not found");
                return;
            }

            // 分配解码器上下文
            var vCodecCtx = ffmpeg.avcodec_alloc_context3(codec); 
            if (vCodecCtx == null)
            {
                DebugLogger.WriteLine("[FFmpeg] Failed to allocate video codec context");
                return;
            }
            //_videoCodecContext = (IntPtr)vCodecCtx;

            ffmpeg.avcodec_parameters_to_context(vCodecCtx, codecParams);
            vCodecCtx->thread_count = Math.Max(1, Environment.ProcessorCount - 1);

            // 设置输出分辨率（如果硬件支持缩放）
            if (needDownscale && hwDeviceCtx != null)
            {
                // 某些硬件支持设置输出尺寸
                vCodecCtx->width = targetWidth;
                vCodecCtx->height = targetHeight;
            }

            // 绑定硬件设备（新式方式的关键步骤）
            AVDictionary* options = null;
            int openRet = -1;

            if (hwDeviceCtx != null)
            {
                vCodecCtx->hw_device_ctx = ffmpeg.av_buffer_ref(hwDeviceCtx);

                // 可以传递额外的硬件加速参数
                ffmpeg.av_dict_set(&options, "hwaccel", ffmpeg.av_hwdevice_get_type_name(hwType.Value), 0);

                // 如果需要降分辨率，尝试传递缩放参数
                if (needDownscale)
                {
                    ffmpeg.av_dict_set(&options, "width", targetWidth.ToString(), 0);
                    ffmpeg.av_dict_set(&options, "height", targetHeight.ToString(), 0);
                }

                openRet = ffmpeg.avcodec_open2(vCodecCtx, codec, &options);

                // 如果硬件解码打开失败，清理硬件上下文，回退到软件
                if (openRet < 0)
                {
                    DebugLogger.WriteLine("[FFmpeg] Hardware decoder open failed, falling back to software");
                    ffmpeg.av_buffer_unref(&hwDeviceCtx);
                    vCodecCtx->hw_device_ctx = null;

                    // 重新打开软件解码器
                    openRet = ffmpeg.avcodec_open2(vCodecCtx, codec, null);
                }
              
            }
            else
            {
                openRet = ffmpeg.avcodec_open2(vCodecCtx, codec, null); 
            }

            if (options != null)
                ffmpeg.av_dict_free(&options);

            if (openRet < 0)
            {
                DebugLogger.WriteLine("[FFmpeg] Failed to open video codec");
                return;
            }
            _videoCodecContext = (IntPtr)vCodecCtx;
            // 保存硬件设备上下文供后续使用
            if (hwDeviceCtx != null && vCodecCtx->hw_device_ctx != null)
            {
                _hwDeviceContext  = (IntPtr)hwDeviceCtx;
            }

            _currentDecoder = Marshal.PtrToStringAnsi((IntPtr)codec->name) ?? "unknown";
            _videoWidth = vCodecCtx->width;
            _videoHeight = vCodecCtx->height;

            DebugLogger.WriteLine($"[FFmpeg] Decoder: {_currentDecoder}, Output: {_videoWidth}x{_videoHeight}");

            // 设置 FPS
            if (vCodecCtx->framerate.num > 0 && vCodecCtx->framerate.den > 0)
                _fps = (double)vCodecCtx->framerate.num / vCodecCtx->framerate.den;
            else if (stream->avg_frame_rate.num > 0)
                _fps = (double)stream->avg_frame_rate.num / stream->avg_frame_rate.den;
            else
                _fps = 30.0;

            // 创建缩放上下文（如果输出分辨率不是目标分辨率）
            int swsWidth = _videoWidth;
            int swsHeight = _videoHeight;

            if (needDownscale && (_videoWidth != targetWidth || _videoHeight != targetHeight))
            {
                swsWidth = targetWidth;
                swsHeight = targetHeight;
            }
            // 创建缩放上下文（使用正确的像素格式）
            AVPixelFormat outputFormat = AVPixelFormat.AV_PIX_FMT_BGR24;
            //_swsContext = (IntPtr)ffmpeg.sws_getContext(
            //    _videoWidth, _videoHeight, vCodecCtx->pix_fmt,
            //    swsWidth, swsHeight, AVPixelFormat.AV_PIX_FMT_BGR24,
            //    1, null, null, null);
            // 注意：硬件解码时，vCodecCtx->pix_fmt 可能是硬件格式（如 AV_PIX_FMT_CUDA）
            // 需要先转换到软件格式再缩放
            _swsContext = (IntPtr)ffmpeg.sws_getContext(
        _videoWidth, _videoHeight, AVPixelFormat.AV_PIX_FMT_YUV420P,  // 使用 YUV420P 作为中间格式
        swsWidth, swsHeight, outputFormat,
        1, null, null, null);
            _rgbBuffer = new byte[swsWidth * swsHeight * 3];
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
            var codecParams = stream->codecpar;
            var codec = ffmpeg.avcodec_find_decoder(codecParams->codec_id);
            if (codec == null)
            {
                DebugLogger.WriteLine("[FFmpeg] Audio codec not found");
                return;
            }
            
            string codecName = Marshal.PtrToStringAnsi((IntPtr)codec->name) ?? "unknown";
            DebugLogger.WriteLine($"[FFmpeg] Found audio codec: {codecName}");

            var aCodecCtx = ffmpeg.avcodec_alloc_context3(codec);
            _audioCodecContext = (IntPtr)aCodecCtx;
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

            _audioFrame = (IntPtr)ffmpeg.av_frame_alloc();

            try
            {
                var swr = ffmpeg.swr_alloc();
                _swrContext = (IntPtr)swr;
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


        private unsafe void _InitializeAudioDecoder(AVStream* stream)
        {
            var codecParams = stream->codecpar;
            var codec = ffmpeg.avcodec_find_decoder(codecParams->codec_id);
            if (codec == null) return;

            var aCodecCtx = ffmpeg.avcodec_alloc_context3(codec);
            _audioCodecContext = (IntPtr)aCodecCtx;

            ffmpeg.avcodec_parameters_to_context(aCodecCtx, codecParams);
            ffmpeg.avcodec_open2(aCodecCtx, codec, null);

            _audioFrame = (IntPtr)ffmpeg.av_frame_alloc();

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
                _swrContext = (IntPtr)swr;
            }

            // 初始化播放器
            var waveFormat = new WaveFormat(48000, 16, 2);
            _audioBuffer = new BufferedWaveProvider(waveFormat);
            _audioBuffer.BufferDuration = TimeSpan.FromSeconds(3);
            _audioBuffer.DiscardOnBufferOverflow = true;
            _audioOutput = new WaveOutEvent();
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
                _audioOutput.DesiredLatency = 200;
                _audioOutput.Init(_audioBuffer);
                 
                DebugLogger.WriteLine($"[FFmpeg] Audio player initialized - SampleRate: {sampleRate}, Latency: 200ms");
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
            _audioOutput.DesiredLatency = 100;
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

                    // 1. 计算目标 PTS
                    var videoStream = fmtCtx->streams[_videoStreamIndex];
                    long targetPts = (long)(position / ffmpeg.av_q2d(videoStream->time_base));

                    int ret = ffmpeg.av_seek_frame(fmtCtx, _videoStreamIndex, targetPts, ffmpeg.AVSEEK_FLAG_BACKWARD);
                    if (ret < 0)
                    {
                        DebugLogger.WriteLine($"[FFmpeg] av_seek_frame failed: {ret}");
                        return;
                    }

                    // 2. 清空解码器缓冲区
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

                    // 3. 重置播放时钟（加锁）
                    lock (_clockLock)
                    { 
                        _seekBaseTimeMs = (long)(position * 1000);
                        _clockBase = position;
                        _clockStartTicks = Stopwatch.GetTimestamp();
                        _isPaused = false;  // 强制退出暂停 
                    } 

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
        private unsafe void DecodeVideoPacket(ref int framesDropped, int maxConsecutiveDrops)
        {
            var vCodecCtx = (AVCodecContext*)_videoCodecContext;
            var pkt = (AVPacket*)_packet;
            var frm = (AVFrame*)_videoFrame;

            int ret = ffmpeg.avcodec_send_packet(vCodecCtx, pkt);
            if (ret < 0) return;

            while (true)
            {
                var decodeStartTime = Stopwatch.GetTimestamp();
                
                ret = ffmpeg.avcodec_receive_frame(vCodecCtx, frm);
                var decodeTimeMs = Stopwatch.GetElapsedTime(decodeStartTime).TotalMilliseconds;

                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                    break;
                if (ret < 0) break;

                UpdateDecodePerformance(decodeTimeMs);

                if (CheckPerformanceDegradation())
                {
                    Pause();
                    SendPerformanceWarning();
                    continue;
                }

                if (frm->pts != ffmpeg.AV_NOPTS_VALUE)
                {
                    _currentTimeMs = (long)(frm->pts * _videoTimeBase * 1000);
                }

                var currentClock = GetPlaybackClock();
                var frameTime = frm->pts != ffmpeg.AV_NOPTS_VALUE
                    ? frm->pts * _videoTimeBase
                    : currentClock;

                var diff = frameTime - currentClock;

                if (diff < -0.08 && framesDropped < maxConsecutiveDrops)
                {
                    framesDropped++;
                    continue;
                }

                if (_swsContext != IntPtr.Zero && _rgbBuffer != null)
                {
                    var sws = (SwsContext*)_swsContext;
                    fixed (byte* pData = _rgbBuffer)
                    {
                        byte*[] dstData = { pData };
                        int[] dstStride = { _videoWidth * 3 };

                        ffmpeg.sws_scale(sws,
                            frm->data, frm->linesize,
                            0, frm->height,
                            dstData, dstStride);
                    }

                    FrameDecoded?.Invoke(this, new FrameData
                    {
                        Width = _videoWidth,
                        Height = _videoHeight,
                        Data = _rgbBuffer,
                        //(byte[])_rgbBuffer.Clone(),

                        VideoTimestamp = _currentTimeMs,
                       //AudioTimestamp = _currentAudioTimeMs
                    });
                }

                if (diff > 0.5)
                {
                    _clockBase = frameTime;
                    _clockStartTicks = Stopwatch.GetTimestamp();
                }
                else if (diff > 0.02)
                {
                    int waitMs = (int)(diff * 1000);
                    if (waitMs > 100) waitMs = 100;
                    Thread.Sleep(waitMs);
                }

                framesDropped = 0;
 
            }
        }

        #region 优化后的解码性能监控和自适应机制
        private unsafe void NewDecodeVideoPacket(ref int framesDropped, int maxConsecutiveDrops)
        { 
            var vCodecCtx = (AVCodecContext*)_videoCodecContext;
            var pkt = (AVPacket*)_packet;
            var frm = (AVFrame*)_videoFrame;

            int ret = ffmpeg.avcodec_send_packet(vCodecCtx, pkt);
            if (ret < 0) return;

            while (true)
            {
                var decodeStartTime = Stopwatch.GetTimestamp();

                ret = ffmpeg.avcodec_receive_frame(vCodecCtx, frm);
                var decodeTimeMs = Stopwatch.GetElapsedTime(decodeStartTime).TotalMilliseconds;

                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                    break;
                if (ret < 0) break;

                UpdateDecodePerformance(decodeTimeMs);

                if (CheckPerformanceDegradation())
                {
                    Pause();
                    SendPerformanceWarning();
                    continue;
                }

                // ========== 新增：处理硬件帧 ==========
                AVFrame* displayFrame = frm;
                bool needFreeFrame = false;

                // 检查是否是硬件帧
                bool isHardwareFrame = frm->format == (int)AVPixelFormat.AV_PIX_FMT_CUDA ||
                                       frm->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11 ||
                                       frm->format == (int)AVPixelFormat.AV_PIX_FMT_DXVA2_VLD;

                if (isHardwareFrame && _hwDeviceContext != IntPtr.Zero)
                {
                    DebugLogger.WriteLine("[FFmpeg] Converting hardware frame to software");

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
                            ffmpeg.av_frame_free(&swFrame);
                            displayFrame = frm;
                        }
                    }
                }
                // ====================================

                if (displayFrame->pts != ffmpeg.AV_NOPTS_VALUE)
                {
                    _currentTimeMs = (long)(displayFrame->pts * _videoTimeBase * 1000);
                }

                var currentClock = GetPlaybackClock();
                var frameTime = displayFrame->pts != ffmpeg.AV_NOPTS_VALUE
                    ? displayFrame->pts * _videoTimeBase
                    : currentClock;

                var diff = frameTime - currentClock;

                if (diff < -0.08 && framesDropped < maxConsecutiveDrops)
                {
                    framesDropped++;
                    if (needFreeFrame) ffmpeg.av_frame_free(&displayFrame);
                    continue;
                }

                if (_swsContext != IntPtr.Zero && _rgbBuffer != null)
                {
                    var sws = (SwsContext*)_swsContext;
                    fixed (byte* pData = _rgbBuffer)
                    {
                        byte*[] dstData = { pData };
                        int[] dstStride = { _videoWidth * 3 };

                        ffmpeg.sws_scale(sws,
                            displayFrame->data, displayFrame->linesize,
                            0, displayFrame->height,
                            dstData, dstStride);
                    }

                    FrameDecoded?.Invoke(this, new FrameData
                    {
                        Width = _videoWidth,
                        Height = _videoHeight,
                        Data = (byte[])_rgbBuffer.Clone(),
                       // Timestamp = _currentTimeMs
                    });
                }

                if (diff > 0.5)
                {
                    _clockBase = frameTime;
                    _clockStartTicks = Stopwatch.GetTimestamp();
                }
                else if (diff > 0.02)
                {
                    int waitMs = (int)(diff * 1000);
                    if (waitMs > 100) waitMs = 100;
                    Thread.Sleep(waitMs);
                }

                // 清理临时帧
                if (needFreeFrame)
                {
                    ffmpeg.av_frame_free(&displayFrame);
                }

                ffmpeg.av_frame_unref(frm);
                framesDropped = 0;
            }
        }
            
        
      

        /// <summary>
        /// 硬件帧转软件帧
        /// </summary>
        private unsafe AVFrame* TransferHardwareFrameToSoftware(AVFrame* hwFrame)
        {
            if (_videoCodecContext == IntPtr.Zero)
                return hwFrame;

            AVFrame* swFrame = ffmpeg.av_frame_alloc();
            if (swFrame == null)
                return null;

            int ret = ffmpeg.av_hwframe_transfer_data(swFrame, hwFrame, 0);
            if (ret < 0)
            {
                ffmpeg.av_frame_free(&swFrame);
                return null;
            }

            // 复制元数据
            swFrame->pts = hwFrame->pts;
            swFrame->pkt_dts = hwFrame->pkt_dts;
            swFrame->best_effort_timestamp = hwFrame->best_effort_timestamp;

            return swFrame;
        }

       

        /// <summary>
        /// 微秒级精确等待
        /// </summary>
        private void SleepPrecise(int microseconds)
        {
            if (microseconds <= 0) return;

            if (microseconds < 5000)  // < 5ms 使用自旋等待
            {
                var start = Stopwatch.GetTimestamp();
                long targetTicks = start + microseconds * Stopwatch.Frequency / 1_000_000;
                while (Stopwatch.GetTimestamp() < targetTicks)
                {
                    Thread.SpinWait(1);
                }
            }
            else  // >= 5ms 使用 Thread.Sleep
            {
                Thread.Sleep(microseconds / 1000);
            }
        }


        #endregion

        private unsafe void TestDecodeVideoPacket(ref int framesDropped, int maxConsecutiveDrops)
        {
            var vCodecCtx = (AVCodecContext*)_videoCodecContext;
            var pkt = (AVPacket*)_packet;
            var frm = (AVFrame*)_videoFrame;

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
                long frameStartStamp = Stopwatch.GetTimestamp();

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

                if (isHardwareFrame && _hwDeviceContext != IntPtr.Zero)
                {
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
 
         

                // ========== 图像转换 ==========
                if (_swsContext != IntPtr.Zero && _rgbBuffer != null)
                {
                    // 动态重建 swsContext
                    if (_currentSwsInputFormat != displayFrame->format)
                    {
                        if (_swsContext != IntPtr.Zero)
                        {
                            ffmpeg.sws_freeContext((SwsContext*)_swsContext);
                        }
                        _swsContext = (IntPtr)ffmpeg.sws_getContext(
                            displayFrame->width, displayFrame->height, (AVPixelFormat)displayFrame->format,
                            _videoWidth, _videoHeight, AVPixelFormat.AV_PIX_FMT_BGR24,
                            1, null, null, null);
                        _currentSwsInputFormat = displayFrame->format;
                    }

                    var sws = (SwsContext*)_swsContext;
                    fixed (byte* pData = _rgbBuffer)
                    {
                        byte*[] dstData = { pData };
                        int[] dstStride = { _videoWidth * 3 };
                        ffmpeg.sws_scale(sws,
                            displayFrame->data, displayFrame->linesize,
                            0, displayFrame->height,
                            dstData, dstStride);
                    }
                }

                // ========== 更新当前时间 ==========
                if (displayFrame->pts != ffmpeg.AV_NOPTS_VALUE)
                {
                    _currentTimeMs = (long)(displayFrame->pts * _videoTimeBase * 1000);
                    _videoClock = _currentTimeMs;
                   
                       // DebugLogger.WriteLine($"[视频] 第{_frameCount}帧原始 PTS: {_currentTimeMs} ms");
                    
                }

                // ========== 解码耗时统计 ==========
                long frameEndStamp = Stopwatch.GetTimestamp();
                double decodeMs = (frameEndStamp - frameStartStamp) * 1000.0 / Stopwatch.Frequency;
               // DebugLogger.WriteLine($"[解码耗时] : {decodeMs:F2} ms");

                // ========== 入队（同步逻辑移到显示线程） ==========
                try
                {

                    // 解码线程
                    byte[] buffer = new byte[_rgbBuffer.Length];  // 每次创建新 buffer
                    Buffer.BlockCopy(_rgbBuffer, 0, buffer, 0, _rgbBuffer.Length);

                    long sendPtsMs = Convert.ToInt64(_currentTimeMs - _seekBaseTimeMs);
                    if (sendPtsMs < 0) sendPtsMs = 0;

                    long sendAudioMs = Convert.ToInt64(_audioClock - _seekBaseTimeMs);
                    if (sendAudioMs < 0) sendAudioMs = 0;
                    _audioClock = sendAudioMs;
                    // 处理负数（第一帧可能稍微靠前）
                    _displayQueue.TryAdd(new FrameData
                    {
                        Width = _videoWidth,
                        Height = _videoHeight,
                        Data = buffer,  // 注意：需要拷贝数据，否则会被覆盖
                        VideoTimestamp = sendPtsMs,
                        AudioTimestamp = (long)sendAudioMs,
                        AudioPlayPosition = (long)GetAudioPlaybackPosition2() 
                    }, 10, _decodeCts.Token);

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
                
                // 优先使用音频时钟作为主时钟进行同步
                // 音频时钟更稳定，因为音频播放由硬件设备控制
                if (_audioOutput != null)
                {
                    // 计算音频播放位置（秒）
                    long bytesPlayed = _audioOutput.GetPosition();
                    // 采样率 * 声道数 * 位深/8 = 每秒字节数
                    double bytesPerSecond = _sampleRate * 2 * 2; // 假设 16-bit, 2 channels
                    double audioPlayTime = bytesPlayed / bytesPerSecond;
                    
                    // 音频播放时间 + 缓冲区延迟 = 当前播放时钟
                    double bufferDelay = 0;
                    if (_audioBuffer != null)
                    {
                        bufferDelay = _audioBuffer.BufferedDuration.TotalSeconds;
                    }
                    
                    double audioClock = _clockBase + audioPlayTime + bufferDelay;
                    
                    // 更新视频时钟以保持同步
                    _videoClock = audioClock;
                    return audioClock;
                }
                
                // 如果音频输出不可用，回退到基于计时器的时钟
                long elapsedTicks = Stopwatch.GetTimestamp() - _clockStartTicks;
                double elapsedSeconds = (double)elapsedTicks / Stopwatch.Frequency;
                
                if (!_isFirstClockLogged && elapsedSeconds > 0)
                {
                    _isFirstClockLogged = true;
                    DebugLogger.WriteLine($"[CLOCK] First clock read: base={_clockBase:F3}, elapsed={elapsedSeconds:F3}, total={_clockBase + elapsedSeconds:F3}");
                }
                
                return _clockBase + elapsedSeconds;
            }
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

                // 如果有 Seek 基准，需要加上
                return _seekBaseTimeMs + audioPlayTime + bufferDelay;
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

                byte[] outBuffer = ConvertAudioFrame(aFrm);  // 使用上面的转换函数

                if (outBuffer != null && outBuffer.Length > 0)
                {
                    // 检查缓冲区，避免溢出
                    if (_audioBuffer.BufferedDuration.TotalMilliseconds < 1000)
                    {
                        _audioBuffer.AddSamples(outBuffer, 0, outBuffer.Length);
                    }
                }

                ffmpeg.av_frame_unref(aFrm);
            }
        }

        private unsafe byte[] ConvertAudioFrame(AVFrame* aFrm)
        {
            int channels = aFrm->ch_layout.nb_channels;
            int samples = aFrm->nb_samples;
            byte[] buffer = new byte[samples * 2 * 2];  // 输出立体声

            float volumeFactor = _volume / 100.0f;

            if (aFrm->format == (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP)
            {
                float** src = (float**)&aFrm->data;

                for (int i = 0; i < samples; i++)
                {
                    float left = 0, right = 0;

                    // 根据声道数混音
                    switch (channels)
                    {
                        case 1: // 单声道 -> 复制到左右
                            left = src[0][i];
                            right = src[0][i];
                            break;

                        case 2: // 立体声
                            left = src[0][i];
                            right = src[1][i];
                            break;

                        case 4: // 4.0 声道: FL, FR, BL, BR
                            left = src[0][i] + src[2][i] * 0.7f;
                            right = src[1][i] + src[3][i] * 0.7f;
                            break;

                        case 5: // 5.0 声道: FL, FR, FC, BL, BR
                            left = src[0][i] + src[2][i] * 0.7f + src[3][i] * 0.5f;
                            right = src[1][i] + src[2][i] * 0.7f + src[4][i] * 0.5f;
                            break;

                        case 6: // 5.1 声道: FL, FR, FC, LFE, BL, BR
                            left = src[0][i] + src[2][i] * 0.7f + src[4][i] * 0.5f;
                            right = src[1][i] + src[2][i] * 0.7f + src[5][i] * 0.5f;
                            // LFE (src[3]) 低音炮通常不混入主声道，或少量混入
                            left += src[3][i] * 0.3f;
                            right += src[3][i] * 0.3f;
                            break;

                        case 8: // 7.1 声道: FL, FR, FC, LFE, BL, BR, SL, SR
                            left = src[0][i] + src[2][i] * 0.7f + src[4][i] * 0.5f + src[6][i] * 0.5f;
                            right = src[1][i] + src[2][i] * 0.7f + src[5][i] * 0.5f + src[7][i] * 0.5f;
                            left += src[3][i] * 0.3f;
                            right += src[3][i] * 0.3f;
                            break;

                        default: // 其他情况，只取前两声道
                            if (channels >= 2)
                            {
                                left = src[0][i];
                                right = src[1][i];
                            }
                            else if (channels == 1)
                            {
                                left = src[0][i];
                                right = src[0][i];
                            }
                            break;
                    }

                    // 限制范围
                    if (left > 1.0f) left = 1.0f;
                    if (left < -1.0f) left = -1.0f;
                    if (right > 1.0f) right = 1.0f;
                    if (right < -1.0f) right = -1.0f;

                    short l = (short)(left * volumeFactor * 32767);
                    short r = (short)(right * volumeFactor * 32767);

                    buffer[i * 4] = (byte)(l & 0xFF);
                    buffer[i * 4 + 1] = (byte)((l >> 8) & 0xFF);
                    buffer[i * 4 + 2] = (byte)(r & 0xFF);
                    buffer[i * 4 + 3] = (byte)((r >> 8) & 0xFF);
                }
            }
            else if (aFrm->format == (int)AVSampleFormat.AV_SAMPLE_FMT_S16)
            {
                // S16 格式同样需要混音
                short** src = (short**)&aFrm->data;

                for (int i = 0; i < samples; i++)
                {
                    float left = 0, right = 0;

                    switch (channels)
                    {
                        case 1:
                            left = src[0][i] / 32767f;
                            right = left;
                            break;
                        case 2:
                            left = src[0][i] / 32767f;
                            right = src[1][i] / 32767f;
                            break;
                        default:
                            if (channels >= 2)
                            {
                                left = src[0][i] / 32767f;
                                right = src[1][i] / 32767f;
                            }
                            else if (channels == 1)
                            {
                                left = src[0][i] / 32767f;
                                right = left;
                            }
                            break;
                    }

                    short l = (short)(left * volumeFactor * 32767);
                    short r = (short)(right * volumeFactor * 32767);

                    buffer[i * 4] = (byte)(l & 0xFF);
                    buffer[i * 4 + 1] = (byte)((l >> 8) & 0xFF);
                    buffer[i * 4 + 2] = (byte)(r & 0xFF);
                    buffer[i * 4 + 3] = (byte)((r >> 8) & 0xFF);
                }
            }

            return buffer;
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
                    _swrContext = IntPtr.Zero;
                    return;
                }

                // 9. 保存重采样上下文
                _swrContext = (IntPtr)swrContext;
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
            DebugLogger.WriteLine($"[FFmpeg] StopInternalAsync;");

            if (_playTask == null && !_isPlaying) return;

            _isPlaying = false;
            _isPaused = false;
            _playCts?.Cancel();
            DebugLogger.WriteLine($"[FFmpeg] start await Task.WhenAny(_playTask, Task.Delay(2000));");

            if (_playTask != null)
            {
                try
                {
                    await Task.WhenAny(_playTask, Task.Delay(2000));
                }
                catch (OperationCanceledException) { }
            }
            DebugLogger.WriteLine($"[FFmpeg] end await Task.WhenAny(_playTask, Task.Delay(2000));");

            Cleanup();
            _playTask?.Dispose();
            _playCts?.Dispose();
            _displayCts?.Cancel();
            _displayCts?.Dispose();
            _displayCts = null;
            _displayQueue.CompleteAdding();
            _displayThread.Join(1000);


            _decodeCts?.Cancel();
            _decodeCts?.Dispose();
            _decodeCts = null;
            _playTask = null;
            _playCts = null;
            _currentTimeMs = 0;
        }

        public async Task StopAsync()
        {
            await StopInternalAsync();
        }

        private unsafe void Cleanup()
        {
            try
            {
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
                    var ctx = (SwrContext*)_swrContext;
                    ffmpeg.swr_free(&ctx);
                    _swrContext = IntPtr.Zero;
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
                DebugLogger.WriteLine($"[FFmpeg] Performance - Frame: {_frameCount}, Decode: {decodeTimeMs:F2}ms, Avg: {_avgDecodeTimeMs:F2}ms, Target: {targetTimeMs:F2}ms");
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

        public void SetAudioTrack(int index)
        {
            if (index >= 0 && index < _audioTracks.Count)
            {
                _audioStreamIndex = index;
                DebugLogger.WriteLine($"[FFmpeg] Audio track changed to {index}");
            }
        }

        public void SetSubtitleTrack(int index)
        {
            _subtitleStreamIndex = index;
            DebugLogger.WriteLine($"[FFmpeg] Subtitle track changed to {index}");
        }
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
        /// 帧像素数据（BGR24格式）
        /// </summary>
        public byte[] Data { get; set; } = Array.Empty<byte>();

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