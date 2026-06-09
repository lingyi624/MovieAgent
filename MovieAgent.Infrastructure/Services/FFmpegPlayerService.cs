using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen;
using MovieAgent.Core.Interfaces;
using NAudio.Wave;

namespace MovieAgent.Infrastructure.Services;

/// <summary>
/// 音频流信息
/// </summary>
public class AudioStreamInfo
{
    public int Index { get; set; }
    public string CodecName { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int Channels { get; set; }
    public int SampleRate { get; set; }
    public string FormatType { get; set; } = string.Empty;
}

/// <summary>
/// 字幕流信息
/// </summary>
public class SubtitleStreamInfo
{
    public int Index { get; set; }
    public string CodecName { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

public class FFmpegPlayerService : IPlayerService, IDisposable
{
    private readonly ILoggerService _logger;
    
    private IntPtr _formatContext;
    private IntPtr _videoCodecContext;
    private IntPtr _audioCodecContext;
    private IntPtr _swsContext;
    private IntPtr _swrContext;
    private IntPtr _videoFrame;
    private IntPtr _audioFrame;
    private IntPtr _packet;
    private IntPtr _transferFrame;
    private bool _useHardwareDecoder;
    private AVPixelFormat _currentSourcePixFmt = AVPixelFormat.AV_PIX_FMT_NONE;

    private int _videoStreamIndex = -1;
    private int _audioStreamIndex = -1;
    private double _videoTimeBase;

    private CancellationTokenSource? _playCts;
    private Task? _playTask;

    private bool _isInitialized;
    private volatile bool _isPlaying;
    private volatile bool _isPaused;

    private long _currentTimeMs;
    private long _durationMs;
    private int _volume = 100;

    private int _videoWidth;
    private int _videoHeight;
    private double _fps;
    private string _audioCodecName = string.Empty;
    private string _audioFormat = string.Empty;
    private int _audioChannels = 2;
    private int _audioSampleRate = 44100;

    private byte[]? _rgbBuffer;
    private BufferedWaveProvider? _audioProvider;
    private WaveOutEvent? _waveOut;
    private readonly object _audioLock = new();
    private readonly object _seekLock = new();
    private readonly SemaphoreSlim _playLock = new(1, 1);

    private double _clockBase = 0;
    private long _clockStartTicks;

    // 异步 Seek 相关
    private volatile bool _pendingSeek;
    private double _pendingSeekTime;
    private volatile bool _isSeeking;

    // 音频流和字幕流信息
    private readonly List<AudioStreamInfo> _audioStreams = new();
    private readonly List<SubtitleStreamInfo> _subtitleStreams = new();
    private string? _externalSubtitlePath;
    private string? _currentSubtitleEncoding;
    
    // 外部字幕解析结果
    private List<SubtitleItem>? _externalSubtitles;
    private readonly object _subtitleLock = new();
    
    // 内嵌字幕相关
    private IntPtr _subtitleCodecContext;
    private IntPtr _subtitleFrame;
    private int _subtitleStreamIndex = -1;
    private string? _currentEmbeddedSubtitle;
    
    public FFmpegPlayerService(ILoggerService logger)
    {
        _logger = logger;
        InitializeFFmpeg();
    }
    
    [Obsolete("Use constructor with ILoggerService parameter")]
    public FFmpegPlayerService() : this(new LoggerService())
    {
    }

    public bool IsPlaying => _isPlaying;
    public bool IsPaused => _isPaused;
    public TimeSpan Duration => TimeSpan.FromMilliseconds(_durationMs);
    public TimeSpan Position => TimeSpan.FromMilliseconds(_currentTimeMs);
    public float Volume => _volume;

    public int AudioTrackCount => _audioStreams.Count;
    public int CurrentAudioTrack { get; private set; } = -1;
    public int SpuTrackCount => _subtitleStreams.Count;
    public int CurrentSpuTrack { get; private set; } = -1;
    public IReadOnlyList<AudioStreamInfo> AudioStreams => _audioStreams.AsReadOnly();
    public IReadOnlyList<SubtitleStreamInfo> SubtitleStreams => _subtitleStreams.AsReadOnly();
    public string? ExternalSubtitlePath => _externalSubtitlePath;

    public bool HasExternalSubtitle => _externalSubtitles != null && _externalSubtitles.Count > 0;

    public bool IsAvailable => _isInitialized;

    public int VideoWidth => _videoWidth;
    public int VideoHeight => _videoHeight;
    public double Fps => _fps;
    public string AudioCodecName => _audioCodecName;
    public string AudioFormat => _audioFormat;
    public int AudioChannels => _audioChannels;
    public int AudioSampleRate => _audioSampleRate;

    public event EventHandler<byte[]>? FrameUpdated;
    public event EventHandler? PlaybackEnded;

    private void InitializeFFmpeg()
    {
        try
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                !RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                _logger.Debug("[FFmpeg] Platform not supported");
                _isInitialized = false;
                return;
            }

            var baseDir = AppContext.BaseDirectory;

            if (Directory.Exists(baseDir))
            {
                ffmpeg.RootPath = baseDir;
                _logger.Debug($"[FFmpeg] Using FFmpeg from: {baseDir}");
            }
            else
            {
                ffmpeg.RootPath = baseDir;
                _logger.Debug($"[FFmpeg] Using FFmpeg from app directory: {baseDir}");
            }

            try
            {
                var version = ffmpeg.av_version_info();
                _logger.Debug($"[FFmpeg] Version: {version}");
                _isInitialized = true;
            }
            catch (Exception ex)
            {
                _logger.Debug($"[FFmpeg] Initialization failed: {ex.Message}");
                _isInitialized = false;
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"[FFmpeg] Initialization error: {ex.Message}");
            _isInitialized = false;
        }
    }

    private bool OpenFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            _logger.Debug($"[FFmpeg] File not found: {filePath}");
            return false;
        }

        return OpenFileUnsafe(filePath);
    }

    private unsafe bool OpenFileUnsafe(string filePath)
    {
        try
        {
            AVFormatContext* fmtCtx = ffmpeg.avformat_alloc_context();
            _formatContext = (IntPtr)fmtCtx;
            if (fmtCtx == null)
            {
                _logger.Debug("[FFmpeg] Failed to allocate format context");
                return false;
            }

            AVDictionary* options = null;
            ffmpeg.av_dict_set(&options, "buffer_size", "1024000", 0);
            ffmpeg.av_dict_set(&options, "probesize", "5000000", 0);
            ffmpeg.av_dict_set(&options, "analyzeduration", "3000000", 0);

            if (ffmpeg.avformat_open_input(&fmtCtx, filePath, null, &options) != 0)
            {
                _logger.Debug("[FFmpeg] Failed to open file");
                return false;
            }
            _formatContext = (IntPtr)fmtCtx;

            if (ffmpeg.avformat_find_stream_info(fmtCtx, null) < 0)
            {
                _logger.Debug("[FFmpeg] Failed to find stream info");
                return false;
            }

            _durationMs = (long)(fmtCtx->duration / (double)ffmpeg.AV_TIME_BASE * 1000);

            // 清空之前的流列表
            _audioStreams.Clear();
            _subtitleStreams.Clear();
            _audioStreamIndex = -1;
            _videoStreamIndex = -1;

            for (int i = 0; i < (int)fmtCtx->nb_streams; i++)
            {
                var stream = fmtCtx->streams[i];
                var codecParams = stream->codecpar;

                if (codecParams->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && _videoStreamIndex < 0)
                {
                    _videoStreamIndex = i;
                    _videoTimeBase = ffmpeg.av_q2d(stream->time_base);
                    InitializeVideoDecoder(stream);
                }
                else if (codecParams->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    var audioInfo = CollectAudioStreamInfo(stream, i);
                    _audioStreams.Add(audioInfo);
                    
                    // 只初始化第一个音频流
                    if (_audioStreamIndex < 0)
                    {
                        _audioStreamIndex = i;
                        CurrentAudioTrack = 0;
                        InitializeAudioDecoder(stream);
                    }
                }
                else if (codecParams->codec_type == AVMediaType.AVMEDIA_TYPE_SUBTITLE)
                {
                    var subtitleInfo = CollectSubtitleStreamInfo(stream, i);
                    _subtitleStreams.Add(subtitleInfo);
                    
                    // 初始化第一个字幕流
                    if (_subtitleStreamIndex < 0)
                    {
                        _subtitleStreamIndex = i;
                        CurrentSpuTrack = 0;
                    }
                }
            }

            _videoFrame = (IntPtr)ffmpeg.av_frame_alloc();
            _audioFrame = (IntPtr)ffmpeg.av_frame_alloc();
            _subtitleFrame = (IntPtr)ffmpeg.av_frame_alloc();
            _packet = (IntPtr)ffmpeg.av_packet_alloc();

            if (_videoStreamIndex >= 0)
            {
                _logger.Debug($"[FFmpeg] Video opened: {_videoWidth}x{_videoHeight}, {_fps:F2}fps");
                _logger.Debug($"[FFmpeg] Duration: {TimeSpan.FromMilliseconds(_durationMs):hh\\:mm\\:ss}");
                _logger.Debug($"[FFmpeg] Found {_audioStreams.Count} audio stream(s)");
                _logger.Debug($"[FFmpeg] Found {_subtitleStreams.Count} subtitle stream(s)");
                
                // 输出所有音频流信息
                for (int i = 0; i < _audioStreams.Count; i++)
                {
                    var audio = _audioStreams[i];
                    _logger.Debug($"[FFmpeg] Audio[{i}]: {audio.DisplayName} ({audio.FormatType}, {audio.Channels}ch, {audio.SampleRate}Hz)");
                }
                return true;
            }

            _logger.Debug("[FFmpeg] No video stream found");
            return false;
        }
        catch (Exception ex)
        {
            _logger.Debug($"[FFmpeg] Open file error: {ex.Message}");
            return false;
        }
    }

    private unsafe AudioStreamInfo CollectAudioStreamInfo(AVStream* stream, int index)
    {
        var codecParams = stream->codecpar;
        var codec = ffmpeg.avcodec_find_decoder(codecParams->codec_id);
        var codecName = codec != null ? Marshal.PtrToStringAnsi((IntPtr)codec->name) ?? "unknown" : "unknown";
        
        // 获取语言信息
        string language = "unknown";
        var tags = stream->metadata;
        if (tags != null)
        {
            var lang = ffmpeg.av_dict_get(tags, "language", null, 0);
            if (lang != null && lang->value != null)
            {
                language = Marshal.PtrToStringAnsi((IntPtr)lang->value) ?? "unknown";
            }
        }

        // 检测音频格式类型
        var formatType = DetectAudioFormat(codecName, codecParams->codec_id);
        
        // 格式化语言名称
        string displayLang = language.ToUpper();
        if (language == "und" || string.IsNullOrEmpty(language))
            displayLang = "未知";
        else if (language == "chi" || language == "zho")
            displayLang = "中文";
        else if (language == "eng")
            displayLang = "英语";
        else if (language == "jpn")
            displayLang = "日语";
        else if (language == "kor")
            displayLang = "韩语";
        else if (language == "fre" || language == "fra")
            displayLang = "法语";
        else if (language == "ger" || language == "deu")
            displayLang = "德语";

        var audioInfo = new AudioStreamInfo
        {
            Index = index,
            CodecName = codecName,
            Language = language,
            Channels = codecParams->ch_layout.nb_channels,
            SampleRate = codecParams->sample_rate,
            FormatType = formatType,
            DisplayName = $"{displayLang} - {formatType}"
        };

        return audioInfo;
    }

    private unsafe SubtitleStreamInfo CollectSubtitleStreamInfo(AVStream* stream, int index)
    {
        var codecParams = stream->codecpar;
        var codec = ffmpeg.avcodec_find_decoder(codecParams->codec_id);
        var codecName = codec != null ? Marshal.PtrToStringAnsi((IntPtr)codec->name) ?? "unknown" : "unknown";
        
        // 获取语言信息
        string language = "unknown";
        var tags = stream->metadata;
        if (tags != null)
        {
            var lang = ffmpeg.av_dict_get(tags, "language", null, 0);
            if (lang != null && lang->value != null)
            {
                language = Marshal.PtrToStringAnsi((IntPtr)lang->value) ?? "unknown";
            }
        }

        // 格式化语言名称
        string displayLang = language.ToUpper();
        if (language == "und" || string.IsNullOrEmpty(language))
            displayLang = "未知";
        else if (language == "chi" || language == "zho")
            displayLang = "中文";
        else if (language == "eng")
            displayLang = "英语";
        else if (language == "jpn")
            displayLang = "日语";
        else if (language == "kor")
            displayLang = "韩语";

        var subtitleInfo = new SubtitleStreamInfo
        {
            Index = index,
            CodecName = codecName,
            Language = language,
            DisplayName = displayLang
        };

        return subtitleInfo;
    }

    private string DetectAudioFormat(string codecName, AVCodecID codecId)
    {
        // 根据编解码器名称或ID检测音频格式
        var upperCodec = codecName.ToUpper();
        
        if (upperCodec.Contains("DTS") || codecId == AVCodecID.AV_CODEC_ID_DTS)
            return "DTS";
        if (upperCodec.Contains("EAC3") || upperCodec.Contains("E-AC-3") || upperCodec.Contains("DOLBY") || codecId == AVCodecID.AV_CODEC_ID_EAC3)
            return "Dolby Digital+";
        if (upperCodec.Contains("AC3") || upperCodec.Contains("Dolby") || codecId == AVCodecID.AV_CODEC_ID_AC3)
            return "Dolby Digital";
        if (upperCodec.Contains("TRUEHD") || upperCodec.Contains("MLP") || codecId == AVCodecID.AV_CODEC_ID_TRUEHD)
            return "Dolby TrueHD";
        if (upperCodec.Contains("ATMOS") || upperCodec.Contains("AAC"))
            return "AAC";
        if (upperCodec.Contains("FLAC") || codecId == AVCodecID.AV_CODEC_ID_FLAC)
            return "FLAC";
        if (upperCodec.Contains("MP3") || upperCodec.Contains("MP2") || codecId == AVCodecID.AV_CODEC_ID_MP3)
            return "MP3";
        if (upperCodec.Contains("OPUS") || codecId == AVCodecID.AV_CODEC_ID_OPUS)
            return "Opus";
        if (upperCodec.Contains("VORBIS") || codecId == AVCodecID.AV_CODEC_ID_VORBIS)
            return "Vorbis";
        if (upperCodec.Contains("PCM") || upperCodec.Contains("WAV"))
            return "PCM/WAV";
        
        return codecName.ToUpper();
    }

    private unsafe void InitializeVideoDecoder(AVStream* stream)
    {
        var codecParams = stream->codecpar;
        var codec = ffmpeg.avcodec_find_decoder(codecParams->codec_id);
        if (codec == null)
        {
            _logger.Debug("[FFmpeg] Video codec not found");
            return;
        }

        var vCodecCtx = ffmpeg.avcodec_alloc_context3(codec);
        _videoCodecContext = (IntPtr)vCodecCtx;
        if (vCodecCtx == null)
        {
            _logger.Debug("[FFmpeg] Failed to allocate video codec context");
            return;
        }

        ffmpeg.avcodec_parameters_to_context(vCodecCtx, codecParams);
        vCodecCtx->thread_count = Math.Max(1, Environment.ProcessorCount - 1);

        int ret = TryOpenDecoderWithHardware(codec, codecParams->codec_id, vCodecCtx);
        if (ret < 0)
        {
            _logger.Debug("[FFmpeg] Hardware decoding not available, using software decoding");
            ret = ffmpeg.avcodec_open2(vCodecCtx, codec, null);
            if (ret < 0)
            {
                _logger.Debug("[FFmpeg] Failed to open video codec");
                return;
            }
        }
        else
        {
            _logger.Debug("[FFmpeg] Hardware decoding enabled");
        }

        _videoWidth = vCodecCtx->width;
        _videoHeight = vCodecCtx->height;

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

        if (!_useHardwareDecoder)
        {
            _swsContext = (IntPtr)ffmpeg.sws_getContext(
                _videoWidth, _videoHeight, vCodecCtx->pix_fmt,
                _videoWidth, _videoHeight, AVPixelFormat.AV_PIX_FMT_BGR24,
                1, null, null, null);
        }

        _rgbBuffer = new byte[_videoWidth * _videoHeight * 3];
    }

    private unsafe int TryOpenDecoderWithHardware(AVCodec* codec, AVCodecID codecId, AVCodecContext* ctx)
    {
        // 禁用硬件加速 - ExecutionEngineException 在某些环境下会发生
        // 软件解码更稳定，对于大多数视频性能足够
        _logger.Debug("[FFmpeg] Hardware acceleration disabled, using software decoding");
        return -1;
    }

    private unsafe void InitializeAudioDecoder(AVStream* stream)
    {
        var codecParams = stream->codecpar;
        var codec = ffmpeg.avcodec_find_decoder(codecParams->codec_id);
        if (codec == null)
        {
            _logger.Debug("[FFmpeg] Audio codec not found");
            return;
        }

        var aCodecCtx = ffmpeg.avcodec_alloc_context3(codec);
        _audioCodecContext = (IntPtr)aCodecCtx;
        if (aCodecCtx == null)
        {
            _logger.Debug("[FFmpeg] Failed to allocate audio codec context");
            return;
        }

        ffmpeg.avcodec_parameters_to_context(aCodecCtx, codecParams);

        if (ffmpeg.avcodec_open2(aCodecCtx, codec, null) < 0)
        {
            _logger.Debug("[FFmpeg] Failed to open audio codec");
            return;
        }

        // 提取音频信息
        _audioCodecName = Marshal.PtrToStringAnsi((IntPtr)codec->name) ?? "Unknown";
        _audioChannels = codecParams->ch_layout.nb_channels;
        _audioSampleRate = codecParams->sample_rate;

        // 检测音频格式
        _audioFormat = DetectAudioFormat(_audioCodecName, codecParams->codec_id);

        _audioFrame = (IntPtr)ffmpeg.av_frame_alloc();

        try
        {
            var swr = ffmpeg.swr_alloc();
            _swrContext = (IntPtr)swr;
            if (swr == null)
                return;

            ffmpeg.av_opt_set(swr, "in_chlayout", GetChannelLayout(codecParams->ch_layout.nb_channels), 0);
            ffmpeg.av_opt_set(swr, "in_sample_rate", codecParams->sample_rate.ToString(), 0);
            ffmpeg.av_opt_set(swr, "in_sample_fmt", GetSampleFormatName((AVSampleFormat)codecParams->format), 0);
            ffmpeg.av_opt_set(swr, "out_chlayout", "stereo", 0);
            ffmpeg.av_opt_set(swr, "out_sample_rate", "44100", 0);
            ffmpeg.av_opt_set(swr, "out_sample_fmt", "s16", 0);

            var ret = ffmpeg.swr_init(swr);
            if (ret < 0)
            {
                ffmpeg.swr_free(&swr);
                _swrContext = IntPtr.Zero;
                return;
            }

            _audioProvider = new BufferedWaveProvider(new WaveFormat(44100, 16, 2))
            {
                BufferDuration = TimeSpan.FromSeconds(3.0),
                DiscardOnBufferOverflow = true
            };

            _waveOut = new WaveOutEvent();
            _waveOut.Volume = _volume / 100f;
            _waveOut.NumberOfBuffers = 4;
            _waveOut.Init(_audioProvider);

            _logger.Debug("[FFmpeg] Audio initialized");
        }
        catch (Exception ex)
        {
            _logger.Debug($"[FFmpeg] Audio initialization failed: {ex.Message}");
        }
    }

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

    public async Task PlayAsync(string filePath)
    {
        if (!_isInitialized)
            throw new PlatformNotSupportedException("FFmpeg player not available");

        await _playLock.WaitAsync();
        try
        {
            await StopInternalAsync();

            // 将耗时的文件打开操作移到线程池，避免阻塞UI
            bool success = await Task.Run(() => OpenFile(filePath));
            if (!success)
                throw new InvalidOperationException("Failed to open video file");

            _playCts = new CancellationTokenSource();
            _isPlaying = true;
            _isPaused = false;
            _isSeeking = false;
            _clockBase = 0;
            _clockStartTicks = Stopwatch.GetTimestamp();

            InitializeSubtitleDecoderIfNeeded();

            var ct = _playCts.Token;
            _playTask = Task.Run(() => DecodeLoopUnsafeAsync(ct), ct);
        }
        finally
        {
            _playLock.Release();
        }
    }

    private async Task StopInternalAsync()
    {
        if (_playTask == null && !_isPlaying) return;

        _isPlaying = false;
        _isPaused = false;
        _playCts?.Cancel();

        if (_playTask != null)
        {
            try
            {
                await Task.WhenAny(_playTask, Task.Delay(2000));
            }
            catch (OperationCanceledException) { }
        }

        CleanupFFmpeg();
        _playTask?.Dispose();
        _playCts?.Dispose();
        _playTask = null;
        _playCts = null;
        _currentTimeMs = 0;
    }

    public async void Stop()
    {
        await _playLock.WaitAsync();
        try
        {
            await StopInternalAsync();
        }
        finally
        {
            _playLock.Release();
        }
    }

    private async Task DecodeLoopUnsafeAsync(CancellationToken ct)
    {
        try
        {
            if (_audioProvider != null && _waveOut != null)
            {
                _waveOut.Play();
                int waitCount = 0;
                int targetBytes = _audioProvider.WaveFormat.AverageBytesPerSecond / 20;
                while (_audioProvider.BufferedBytes < targetBytes)
                {
                    if (ct.IsCancellationRequested) return;
                    if (waitCount++ > 50) break;
                    await Task.Delay(10, ct).ConfigureAwait(false);
                }
            }

            int framesDropped = 0;
            const int MAX_CONSECUTIVE_DROPS = 5;

            while (!ct.IsCancellationRequested && _isPlaying)
            {
                if (_isPaused)
                {
                    await Task.Delay(20, ct).ConfigureAwait(false);
                    continue;
                }

                if (_pendingSeek && !_isSeeking)
                {
                    double seekTs = _pendingSeekTime;
                    _pendingSeek = false;
                    _isSeeking = true;
                    ExecuteSeekNow(seekTs);
                    continue;
                }

                if (_isSeeking)
                {
                    await Task.Delay(1, ct).ConfigureAwait(false);
                    continue;
                }

                bool shouldBreak = ProcessNextPacket(ct, ref framesDropped, MAX_CONSECUTIVE_DROPS);
                if (shouldBreak) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.Debug($"[FFmpeg] Decode error: {ex.Message}");
        }
        finally
        {
            _isPlaying = false;
            try { _waveOut?.Stop(); } catch { }
            _logger.Debug("[FFmpeg] DecodeLoop finished");
        }
    }

    private unsafe bool ProcessNextPacket(CancellationToken ct, ref int framesDropped, int maxConsecutiveDrops)
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

        if (readResult < 0)
        {
            if (readResult == ffmpeg.AVERROR_EOF)
            {
                _ = HandleEndOfFileAsync(ct);
                return true;
            }
            return true;
        }

        if (pkt->stream_index == _videoStreamIndex)
        {
            DecodeVideoPacketUnsafe(ref framesDropped, maxConsecutiveDrops);
        }
        else if (pkt->stream_index == _audioStreamIndex)
        {
            DecodeAudioPacketUnsafe();
        }
        else if (pkt->stream_index == _subtitleStreamIndex)
        {
            DecodeSubtitlePacketUnsafe();
        }

        ffmpeg.av_packet_unref(pkt);
        return false;
    }

    private async Task HandleEndOfFileAsync(CancellationToken ct)
    {
        while (_audioProvider != null && _audioProvider.BufferedBytes > 0)
        {
            if (ct.IsCancellationRequested) break;
            await Task.Delay(10, ct).ConfigureAwait(false);
        }
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
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
                    _logger.Debug($"[FFmpeg] av_seek_frame failed: {ret}");
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

                lock (_audioLock)
                {
                    _audioProvider?.ClearBuffer();
                }

                _currentTimeMs = (long)(position * 1000);
                _clockBase = position;
                _clockStartTicks = Stopwatch.GetTimestamp();
                _logger.Debug($"[FFmpeg] Seek to: {position}s");
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"[FFmpeg] ExecuteSeekNow exception: {ex.Message}");
        }
        finally
        {
            _isSeeking = false;
        }
    }

    private unsafe void DecodeVideoPacketUnsafe(ref int framesDropped, int maxConsecutiveDrops)
    {
        var vCodecCtx = (AVCodecContext*)_videoCodecContext;
        var pkt = (AVPacket*)_packet;
        var frm = (AVFrame*)_videoFrame;

        int ret = ffmpeg.avcodec_send_packet(vCodecCtx, pkt);
        if (ret < 0) return;

        while (true)
        {
            ret = ffmpeg.avcodec_receive_frame(vCodecCtx, frm);
            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                break;
            if (ret < 0) break;

            AVFrame* frameToRender = frm;

            if (_useHardwareDecoder)
            {
                if (_transferFrame == IntPtr.Zero)
                    _transferFrame = (IntPtr)ffmpeg.av_frame_alloc();

                var transferFrm = (AVFrame*)_transferFrame;
                ffmpeg.av_frame_unref(transferFrm);
                var transferRet = ffmpeg.av_hwframe_transfer_data(transferFrm, frm, 0);
                if (transferRet < 0)
                {
                    _logger.Debug($"[FFmpeg] HW frame transfer failed");
                    break;
                }
                transferFrm->pts = frm->pts;
                frameToRender = transferFrm;

                var hwPixFmt = (AVPixelFormat)transferFrm->format;
                if (_currentSourcePixFmt != hwPixFmt)
                {
                    if (_swsContext != IntPtr.Zero)
                        ffmpeg.sws_freeContext((SwsContext*)_swsContext);

                    _swsContext = (IntPtr)ffmpeg.sws_getContext(
                        transferFrm->width, transferFrm->height, hwPixFmt,
                        transferFrm->width, transferFrm->height, AVPixelFormat.AV_PIX_FMT_BGR24,
                        1, null, null, null);
                    _currentSourcePixFmt = hwPixFmt;
                }
            }

            if (frm->pts != ffmpeg.AV_NOPTS_VALUE)
            {
                _currentTimeMs = (long)(frm->pts * _videoTimeBase * 1000);
            }

            // 帧同步和丢帧控制
            var currentClock = GetPlaybackClock();
            var frameTime = frm->pts != ffmpeg.AV_NOPTS_VALUE
                ? frm->pts * _videoTimeBase
                : currentClock;

            var diff = frameTime - currentClock;

            // 帧落后超过 80ms，且未达到最大连续丢帧数时，跳过此帧
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
                        frameToRender->data, frameToRender->linesize,
                        0, frameToRender->height,
                        dstData, dstStride);
                }

                FrameUpdated?.Invoke(this, _rgbBuffer);
            }

            // 帧超前控制
            if (diff > 0.5) // 超前超过 500ms，重新同步时钟
            {
                _clockBase = frameTime;
                _clockStartTicks = Stopwatch.GetTimestamp();
            }
            else if (diff > 0.02) // 超前 20ms~500ms，轻微等待
            {
                int waitMs = (int)(diff * 1000);
                if (waitMs > 100) waitMs = 100;
                Thread.Sleep(waitMs);
            }

            framesDropped = 0;
        }
    }

    private double GetPlaybackClock()
    {
        if (_isPaused)
            return _clockBase;

        return _clockBase + (Stopwatch.GetTimestamp() - _clockStartTicks) / (double)Stopwatch.Frequency;
    }

    private unsafe void DecodeAudioPacketUnsafe()
    {
        if (_audioCodecContext == IntPtr.Zero || _audioFrame == IntPtr.Zero || _swrContext == IntPtr.Zero)
            return;

        var aCodecCtx = (AVCodecContext*)_audioCodecContext;
        var pkt = (AVPacket*)_packet;
        var aFrm = (AVFrame*)_audioFrame;
        var swr = (SwrContext*)_swrContext;

        int ret = ffmpeg.avcodec_send_packet(aCodecCtx, pkt);
        if (ret < 0) return;

        while (true)
        {
            ret = ffmpeg.avcodec_receive_frame(aCodecCtx, aFrm);
            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                break;
            if (ret < 0) break;

            var outSamples = ffmpeg.swr_get_out_samples(swr, aFrm->nb_samples);
            var outBufferSize = outSamples * 2 * 2;
            var outBuffer = new byte[outBufferSize];

            fixed (byte* pOut = outBuffer)
            {
                var converted = ffmpeg.swr_convert(swr,
                    &pOut, outSamples,
                    (byte**)(&aFrm->data), aFrm->nb_samples);

                if (converted > 0 && _audioProvider != null)
                {
                    var totalBytes = converted * 2 * 2;
                    lock (_audioLock)
                    {
                        _audioProvider.AddSamples(outBuffer, 0, totalBytes);
                    }
                }
            }
        }
    }
    
    private unsafe void DecodeSubtitlePacketUnsafe()
    {
        if (_subtitleCodecContext == IntPtr.Zero || _subtitleFrame == IntPtr.Zero)
            return;

        var sCodecCtx = (AVCodecContext*)_subtitleCodecContext;
        var pkt = (AVPacket*)_packet;
        var sFrm = (AVFrame*)_subtitleFrame;

        int ret = ffmpeg.avcodec_send_packet(sCodecCtx, pkt);
        if (ret < 0)
        {
            _logger.Debug($"[FFmpeg] Subtitle send_packet failed: {ret}");
            return;
        }

        while (true)
        {
            ret = ffmpeg.avcodec_receive_frame(sCodecCtx, sFrm);
            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                break;
            }
            if (ret == ffmpeg.AVERROR_EOF)
            {
                break;
            }
            if (ret < 0)
            {
                _logger.Debug($"[FFmpeg] Subtitle receive_frame failed: {ret}");
                break;
            }

            if (sFrm->data[0] != null)
            {
                string subtitleText = Marshal.PtrToStringAnsi((IntPtr)sFrm->data[0]) ?? string.Empty;
                
                if (!string.IsNullOrEmpty(subtitleText))
                {
                    subtitleText = CleanSubtitleText(subtitleText);
                    
                    if (!string.Equals(_currentEmbeddedSubtitle, subtitleText))
                    {
                        _currentEmbeddedSubtitle = subtitleText;
                        _logger.Debug($"[FFmpeg] Subtitle decoded: {subtitleText.Substring(0, Math.Min(subtitleText.Length, 50))}...");
                    }
                }
            }
            else if (sFrm->data[1] != null)
            {
                string subtitleText = Marshal.PtrToStringAnsi((IntPtr)sFrm->data[1]) ?? string.Empty;
                
                if (!string.IsNullOrEmpty(subtitleText))
                {
                    subtitleText = CleanSubtitleText(subtitleText);
                    
                    if (!string.Equals(_currentEmbeddedSubtitle, subtitleText))
                    {
                        _currentEmbeddedSubtitle = subtitleText;
                        _logger.Debug($"[FFmpeg] Subtitle decoded from data[1]: {subtitleText.Substring(0, Math.Min(subtitleText.Length, 50))}...");
                    }
                }
            }
            else if (sFrm->pts != ffmpeg.AV_NOPTS_VALUE)
            {
                _logger.Debug($"[FFmpeg] Subtitle frame has PTS but no text data: pts={sFrm->pts}");
            }
            
            ffmpeg.av_frame_unref(sFrm);
        }
    }
    
    private string CleanSubtitleText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
            
        text = text.Replace("\r\n", "\n").Replace("\r", "\n");
        
        text = text.Trim();
        
        while (text.Contains("\n\n"))
        {
            text = text.Replace("\n\n", "\n");
        }
        
        return text;
    }

    public void Pause()
    {
        if (_isPlaying && !_isPaused)
        {
            _isPaused = true;
            _clockBase = GetPlaybackClock();
            _waveOut?.Pause();
            _logger.Debug("[FFmpeg] Paused");
        }
    }

    public void Resume()
    {
        if (_isPlaying && _isPaused)
        {
            _isPaused = false;
            _clockStartTicks = Stopwatch.GetTimestamp();
            _waveOut?.Play();
            _logger.Debug("[FFmpeg] Resumed");
        }
    }

    public void SetVolume(int volume)
    {
        _volume = Math.Clamp(volume, 0, 100);
        if (_waveOut != null)
        {
            _waveOut.Volume = _volume / 100f;
        }
        _logger.Debug($"[FFmpeg] Volume set to: {_volume}");
    }

    public void Seek(int position)
    {
        if (position < 0) return;
        _pendingSeekTime = position;
        _pendingSeek = true;
        _logger.Debug($"[FFmpeg] Seek requested to: {position}s");
    }

    public void SeekSync(int position)
    {
        try
        {
            lock (_seekLock)
            {
                if (!_isPlaying && !_isPaused)
                {
                    _logger.Debug("[FFmpeg] SeekSync ignored - not playing");
                    return;
                }

                if (_formatContext == IntPtr.Zero || _videoStreamIndex < 0)
                {
                    _logger.Debug("[FFmpeg] SeekSync ignored - invalid state");
                    return;
                }

                try
                {
                    unsafe
                    {
                        var fmtCtx = (AVFormatContext*)_formatContext;
                        if (fmtCtx == null)
                        {
                            _logger.Debug("[FFmpeg] SeekSync ignored - fmtCtx is null");
                            return;
                        }

                        long targetPts = (long)(position * ffmpeg.AV_TIME_BASE);
                        int ret = ffmpeg.av_seek_frame(fmtCtx, -1, targetPts, ffmpeg.AVSEEK_FLAG_BACKWARD);
                        if (ret < 0)
                        {
                            _logger.Debug($"[FFmpeg] av_seek_frame failed: {ret}");
                            return;
                        }

                        if (_videoCodecContext != IntPtr.Zero)
                        {
                            var vCtx = (AVCodecContext*)_videoCodecContext;
                            if (vCtx != null)
                            {
                                ffmpeg.avcodec_flush_buffers(vCtx);
                            }
                        }

                        if (_audioCodecContext != IntPtr.Zero)
                        {
                            var aCtx = (AVCodecContext*)_audioCodecContext;
                            if (aCtx != null)
                            {
                                ffmpeg.avcodec_flush_buffers(aCtx);
                            }
                        }
                    }

                    lock (_audioLock)
                    {
                        _audioProvider?.ClearBuffer();
                    }

                    _currentTimeMs = position * 1000L;
                    _clockBase = position;
                    _clockStartTicks = Stopwatch.GetTimestamp();
                    _logger.Debug($"[FFmpeg] SeekSync to: {position}s");
                }
                catch (System.AccessViolationException ex)
                {
                    _logger.Debug($"[FFmpeg] SeekSync access violation: {ex.Message}");
                }
                catch (System.NullReferenceException ex)
                {
                    _logger.Debug($"[FFmpeg] SeekSync null reference: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"[FFmpeg] SeekSync exception: {ex.Message}");
        }
    }

    public void Next()
    {
        _logger.Debug("[FFmpeg] Next not supported");
    }

    public void Previous()
    {
        _logger.Debug("[FFmpeg] Previous not supported");
    }

    public void ToggleFullscreen()
    {
        _logger.Debug("[FFmpeg] Toggle fullscreen (handled by UI)");
    }

    public void SetAudioTrack(int trackIndex)
        {
            if (trackIndex < 0 || trackIndex >= _audioStreams.Count)
            {
                _logger.Debug($"[FFmpeg] Invalid audio track index: {trackIndex}");
                return;
            }

            var newAudioInfo = _audioStreams[trackIndex];
            _logger.Debug($"[FFmpeg] Switching to audio track {trackIndex}: {newAudioInfo.DisplayName}");
            
            if (trackIndex == CurrentAudioTrack)
                return;

            if (_isPlaying)
            {
                try
                {
                    CleanupAudioDecoder();
                    
                    _audioStreamIndex = _audioStreams[trackIndex].Index;
                    CurrentAudioTrack = trackIndex;
                    
                    unsafe
                    {
                        var fmtCtx = (AVFormatContext*)_formatContext;
                        if (fmtCtx != null)
                        {
                            var stream = fmtCtx->streams[_audioStreamIndex];
                            InitializeAudioDecoder(stream);
                            
                            lock (_audioLock)
                            {
                                _audioProvider?.ClearBuffer();
                            }
                            
                            _waveOut?.Play();
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug($"[FFmpeg] Failed to switch audio track: {ex.Message}");
                }
            }
            else
            {
                _audioStreamIndex = _audioStreams[trackIndex].Index;
                CurrentAudioTrack = trackIndex;
            }
            
            _audioCodecName = newAudioInfo.CodecName;
            _audioFormat = newAudioInfo.FormatType;
            _audioChannels = newAudioInfo.Channels;
            _audioSampleRate = newAudioInfo.SampleRate;
            
            _logger.Debug($"[FFmpeg] Audio track switched to: {newAudioInfo.DisplayName}");
        }
    
    private unsafe void CleanupAudioDecoder()
    {
        try
        {
            _waveOut?.Stop();
            _waveOut?.Dispose();
            _waveOut = null;
            _audioProvider = null;
            
            if (_audioFrame != IntPtr.Zero)
            {
                var frame = (AVFrame*)_audioFrame;
                ffmpeg.av_frame_free(&frame);
                _audioFrame = IntPtr.Zero;
            }
            
            if (_swrContext != IntPtr.Zero)
            {
                var ctx = (SwrContext*)_swrContext;
                ffmpeg.swr_free(&ctx);
                _swrContext = IntPtr.Zero;
            }
            
            if (_audioCodecContext != IntPtr.Zero)
            {
                var ctx = (AVCodecContext*)_audioCodecContext;
                ffmpeg.avcodec_free_context(&ctx);
                _audioCodecContext = IntPtr.Zero;
            }
        }
        catch { }
    }

    public void SetSpuTrack(int trackIndex)
        {
            if (trackIndex < 0 || trackIndex >= _subtitleStreams.Count)
            {
                _logger.Debug($"[FFmpeg] Invalid subtitle track index: {trackIndex}");
                return;
            }

            var newSubtitleInfo = _subtitleStreams[trackIndex];
            _logger.Debug($"[FFmpeg] Switching to subtitle track {trackIndex}: {newSubtitleInfo.DisplayName}");
            
            if (trackIndex == CurrentSpuTrack)
                return;

            CurrentSpuTrack = trackIndex;
            _subtitleStreamIndex = _subtitleStreams[trackIndex].Index;
            _currentEmbeddedSubtitle = null;
            
            if (_isPlaying)
            {
                try
                {
                    CleanupSubtitleDecoder();
                    
                    unsafe
                    {
                        if (_formatContext == IntPtr.Zero)
                        {
                            _logger.Debug("[FFmpeg] Format context is null, cannot switch subtitle");
                            return;
                        }
                        
                        var fmtCtx = (AVFormatContext*)_formatContext;
                        if (fmtCtx != null && _subtitleStreamIndex >= 0 && _subtitleStreamIndex < (int)fmtCtx->nb_streams)
                        {
                            var stream = fmtCtx->streams[_subtitleStreamIndex];
                            if (stream != null)
                            {
                                InitializeSubtitleDecoder(stream);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug($"[FFmpeg] Failed to switch subtitle track: {ex.Message}");
                }
            }
            
            _logger.Debug($"[FFmpeg] Subtitle track switched to: {newSubtitleInfo.DisplayName}");
        }
    
    /// <summary>
    /// 获取当前时间对应的字幕文本（支持外部字幕和内嵌字幕）
    /// </summary>
    /// <param name="currentTime">当前播放时间</param>
    /// <returns>字幕文本，如果没有则返回null</returns>
    public string? GetCurrentSubtitle(TimeSpan currentTime)
    {
        // 优先使用外部字幕
        if (HasExternalSubtitle)
        {
            return GetExternalSubtitle(currentTime);
        }
        
        // 尝试获取内嵌字幕
        return GetEmbeddedSubtitle(currentTime);
    }
    
    private string? GetExternalSubtitle(TimeSpan currentTime)
    {
        lock (_subtitleLock)
        {
            if (_externalSubtitles == null || _externalSubtitles.Count == 0)
                return null;

            var subtitle = _externalSubtitles.FirstOrDefault(s => s.IsActive(currentTime));
            return subtitle?.Text;
        }
    }
    
    private string? GetEmbeddedSubtitle(TimeSpan currentTime)
    {
        return _currentEmbeddedSubtitle;
    }
    
    private void InitializeSubtitleDecoderIfNeeded()
    {
        if (_subtitleStreamIndex >= 0 && _subtitleCodecContext == IntPtr.Zero)
        {
            try
            {
                unsafe
                {
                    var fmtCtx = (AVFormatContext*)_formatContext;
                    if (fmtCtx != null)
                    {
                        var stream = fmtCtx->streams[_subtitleStreamIndex];
                        InitializeSubtitleDecoder(stream);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"[FFmpeg] Failed to initialize subtitle decoder: {ex.Message}");
            }
        }
    }
    
    private unsafe void InitializeSubtitleDecoder(AVStream* stream)
    {
        var codecParams = stream->codecpar;
        var codec = ffmpeg.avcodec_find_decoder(codecParams->codec_id);
        if (codec == null)
        {
            _logger.Debug("[FFmpeg] Subtitle codec not found");
            return;
        }

        var sCodecCtx = ffmpeg.avcodec_alloc_context3(codec);
        _subtitleCodecContext = (IntPtr)sCodecCtx;
        if (sCodecCtx == null)
        {
            _logger.Debug("[FFmpeg] Failed to allocate subtitle codec context");
            return;
        }

        ffmpeg.avcodec_parameters_to_context(sCodecCtx, codecParams);

        if (ffmpeg.avcodec_open2(sCodecCtx, codec, null) < 0)
        {
            _logger.Debug("[FFmpeg] Failed to open subtitle codec");
            return;
        }

        if (_subtitleFrame == IntPtr.Zero)
        {
            _subtitleFrame = (IntPtr)ffmpeg.av_frame_alloc();
        }
        _logger.Debug("[FFmpeg] Subtitle decoder initialized");
    }
    
    private unsafe void CleanupSubtitleDecoder()
    {
        try
        {
            if (_subtitleFrame != IntPtr.Zero)
            {
                var frame = (AVFrame*)_subtitleFrame;
                ffmpeg.av_frame_free(&frame);
                _subtitleFrame = IntPtr.Zero;
            }
            
            if (_subtitleCodecContext != IntPtr.Zero)
            {
                var ctx = (AVCodecContext*)_subtitleCodecContext;
                ffmpeg.avcodec_free_context(&ctx);
                _subtitleCodecContext = IntPtr.Zero;
            }
            
            _currentEmbeddedSubtitle = null;
        }
        catch { }
    }

    /// <summary>
    /// 加载外部字幕文件
    /// </summary>
    /// <param name="subtitlePath">字幕文件路径（支持 SRT, ASS, SSA, SUB 格式）</param>
    /// <param name="encoding">字幕文件编码，默认自动检测</param>
    /// <returns>是否成功加载</returns>
    public bool LoadExternalSubtitle(string subtitlePath, string? encoding = null)
    {
        if (string.IsNullOrEmpty(subtitlePath) || !File.Exists(subtitlePath))
        {
            _logger.Debug($"[FFmpeg] Subtitle file not found: {subtitlePath}");
            return false;
        }

        var extension = Path.GetExtension(subtitlePath).ToLowerInvariant();
        var supportedExtensions = new[] { ".srt", ".ass", ".ssa", ".sub", ".txt" };
        if (!supportedExtensions.Contains(extension))
        {
            _logger.Debug($"[FFmpeg] Unsupported subtitle format: {extension}");
            return false;
        }

        try
        {
            _currentSubtitleEncoding = encoding ?? DetectSubtitleEncoding(subtitlePath);
            
            lock (_subtitleLock)
            {
                _externalSubtitles = SubtitleParser.Parse(subtitlePath, _currentSubtitleEncoding);
            }
            
            _externalSubtitlePath = subtitlePath;
            
            _logger.Debug($"[FFmpeg] External subtitle loaded: {subtitlePath}");
            _logger.Debug($"[FFmpeg] Subtitle encoding: {_currentSubtitleEncoding}");
            _logger.Debug($"[FFmpeg] Subtitle count: {_externalSubtitles?.Count ?? 0}");
            return _externalSubtitles != null && _externalSubtitles.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.Debug($"[FFmpeg] Failed to load subtitle: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 卸载外部字幕
    /// </summary>
    public void UnloadExternalSubtitle()
    {
        lock (_subtitleLock)
        {
            _externalSubtitles = null;
        }
        _externalSubtitlePath = null;
        _currentSubtitleEncoding = null;
        _logger.Debug("[FFmpeg] External subtitle unloaded");
    }

    /// <summary>
    /// 检测字幕文件编码
    /// </summary>
    private string DetectSubtitleEncoding(string filePath)
    {
        try
        {
            // 尝试检测 BOM
            var bom = new byte[4];
            using (var fs = File.OpenRead(filePath))
            {
                fs.Read(bom, 0, 4);
            }

            // 检测 UTF-8 BOM
            if (bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
                return "UTF-8";
            
            // 检测 UTF-16 LE BOM
            if (bom[0] == 0xFF && bom[1] == 0xFE)
                return "UTF-16";
            
            // 检测 UTF-16 BE BOM
            if (bom[0] == 0xFE && bom[1] == 0xFF)
                return "UTF-16BE";
            
            // 尝试用默认编码读取前几行来检测
            try
            {
                var lines = File.ReadLines(filePath, System.Text.Encoding.Default).Take(10).ToList();
                // 如果能读到非 ASCII 内容，可能是 GB 编码（中文环境常见）
                if (lines.Any(l => l.Any(c => c > 127)))
                {
                    // 简单检测：常见中文字符范围
                    if (lines.Any(l => l.Any(c => c >= 0x4E00 && c <= 0x9FFF)))
                    {
                        // 尝试 GB2312 或 GBK
                        return "GB2312";
                    }
                }
            }
            catch { }

            return "UTF-8";
        }
        catch
        {
            return "UTF-8";
        }
    }

    private unsafe void CleanupFFmpeg()
    {
        // 停止并清理音频播放器
        try
        {
            _waveOut?.Stop();
            _waveOut?.Dispose();
            _waveOut = null;
        }
        catch { }
        _audioProvider = null;

        // 清理 FFmpeg 资源，每个都单独 try-catch
        try
        {
            if (_packet != IntPtr.Zero)
            {
                var pkt = (AVPacket*)_packet;
                ffmpeg.av_packet_free(&pkt);
                _packet = IntPtr.Zero;
            }
        }
        catch { }

        try
        {
            if (_transferFrame != IntPtr.Zero)
            {
                var frame = (AVFrame*)_transferFrame;
                ffmpeg.av_frame_free(&frame);
                _transferFrame = IntPtr.Zero;
            }
        }
        catch { }

        try
        {
            if (_videoFrame != IntPtr.Zero)
            {
                var frame = (AVFrame*)_videoFrame;
                ffmpeg.av_frame_free(&frame);
                _videoFrame = IntPtr.Zero;
            }
        }
        catch { }

        try
        {
            if (_audioFrame != IntPtr.Zero)
            {
                var frame = (AVFrame*)_audioFrame;
                ffmpeg.av_frame_free(&frame);
                _audioFrame = IntPtr.Zero;
            }
        }
        catch { }

        try
        {
            if (_subtitleFrame != IntPtr.Zero)
            {
                var frame = (AVFrame*)_subtitleFrame;
                ffmpeg.av_frame_free(&frame);
                _subtitleFrame = IntPtr.Zero;
            }
        }
        catch { }

        try
        {
            if (_swsContext != IntPtr.Zero)
            {
                ffmpeg.sws_freeContext((SwsContext*)_swsContext);
                _swsContext = IntPtr.Zero;
            }
        }
        catch { }

        try
        {
            if (_swrContext != IntPtr.Zero)
            {
                var ctx = (SwrContext*)_swrContext;
                ffmpeg.swr_free(&ctx);
                _swrContext = IntPtr.Zero;
            }
        }
        catch { }

        try
        {
            if (_videoCodecContext != IntPtr.Zero)
            {
                var ctx = (AVCodecContext*)_videoCodecContext;
                ffmpeg.avcodec_free_context(&ctx);
                _videoCodecContext = IntPtr.Zero;
            }
        }
        catch { }

        try
        {
            if (_audioCodecContext != IntPtr.Zero)
            {
                var ctx = (AVCodecContext*)_audioCodecContext;
                ffmpeg.avcodec_free_context(&ctx);
                _audioCodecContext = IntPtr.Zero;
            }
        }
        catch { }

        try
        {
            if (_subtitleCodecContext != IntPtr.Zero)
            {
                var ctx = (AVCodecContext*)_subtitleCodecContext;
                ffmpeg.avcodec_free_context(&ctx);
                _subtitleCodecContext = IntPtr.Zero;
            }
        }
        catch { }

        try
        {
            if (_formatContext != IntPtr.Zero)
            {
                var ctx = (AVFormatContext*)_formatContext;
                ffmpeg.avformat_close_input(&ctx);
                _formatContext = IntPtr.Zero;
            }
        }
        catch { }

        _videoStreamIndex = -1;
        _audioStreamIndex = -1;
        _subtitleStreamIndex = -1;
        _useHardwareDecoder = false;
        _currentSourcePixFmt = AVPixelFormat.AV_PIX_FMT_NONE;
    }

    public void Dispose()
    {
        _ = StopAsync();
        _isInitialized = false;
    }

    private async Task StopAsync()
    {
        await _playLock.WaitAsync();
        try
        {
            await StopInternalAsync();
        }
        finally
        {
            _playLock.Release();
        }
    }
}