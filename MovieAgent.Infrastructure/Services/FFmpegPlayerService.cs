using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen;
using MovieAgent.Core.Interfaces;
using NAudio.Wave;

namespace MovieAgent.Infrastructure.Services;

public class FFmpegPlayerService : IPlayerService, IDisposable
{
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

    public bool IsPlaying => _isPlaying;
    public bool IsPaused => _isPaused;
    public TimeSpan Duration => TimeSpan.FromMilliseconds(_durationMs);
    public TimeSpan Position => TimeSpan.FromMilliseconds(_currentTimeMs);
    public float Volume => _volume;

    public int AudioTrackCount => 0;
    public int CurrentAudioTrack => -1;
    public int SpuTrackCount => 0;
    public int CurrentSpuTrack => -1;

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

    public FFmpegPlayerService()
    {
        InitializeFFmpeg();
    }

    private void InitializeFFmpeg()
    {
        try
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                !RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Debug.WriteLine("[FFmpeg] Platform not supported");
                _isInitialized = false;
                return;
            }

            var baseDir = AppContext.BaseDirectory;

            if (Directory.Exists(baseDir))
            {
                ffmpeg.RootPath = baseDir;
                Debug.WriteLine($"[FFmpeg] Using FFmpeg from: {baseDir}");
            }
            else
            {
                ffmpeg.RootPath = baseDir;
                Debug.WriteLine($"[FFmpeg] Using FFmpeg from app directory: {baseDir}");
            }

            try
            {
                var version = ffmpeg.av_version_info();
                Debug.WriteLine($"[FFmpeg] Version: {version}");
                _isInitialized = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FFmpeg] Initialization failed: {ex.Message}");
                _isInitialized = false;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FFmpeg] Initialization error: {ex.Message}");
            _isInitialized = false;
        }
    }

    private bool OpenFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Debug.WriteLine($"[FFmpeg] File not found: {filePath}");
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
                Debug.WriteLine("[FFmpeg] Failed to allocate format context");
                return false;
            }

            AVDictionary* options = null;
            ffmpeg.av_dict_set(&options, "buffer_size", "1024000", 0);
            ffmpeg.av_dict_set(&options, "probesize", "5000000", 0);
            ffmpeg.av_dict_set(&options, "analyzeduration", "3000000", 0);

            if (ffmpeg.avformat_open_input(&fmtCtx, filePath, null, &options) != 0)
            {
                Debug.WriteLine("[FFmpeg] Failed to open file");
                return false;
            }
            _formatContext = (IntPtr)fmtCtx;

            if (ffmpeg.avformat_find_stream_info(fmtCtx, null) < 0)
            {
                Debug.WriteLine("[FFmpeg] Failed to find stream info");
                return false;
            }

            _durationMs = (long)(fmtCtx->duration / (double)ffmpeg.AV_TIME_BASE * 1000);

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
                else if (codecParams->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO && _audioStreamIndex < 0)
                {
                    _audioStreamIndex = i;
                    InitializeAudioDecoder(stream);
                }
            }

            _videoFrame = (IntPtr)ffmpeg.av_frame_alloc();
            _packet = (IntPtr)ffmpeg.av_packet_alloc();

            if (_videoStreamIndex >= 0)
            {
                Debug.WriteLine($"[FFmpeg] Video opened: {_videoWidth}x{_videoHeight}, {_fps:F2}fps");
                Debug.WriteLine($"[FFmpeg] Duration: {TimeSpan.FromMilliseconds(_durationMs):hh\\:mm\\:ss}");
                if (_audioStreamIndex >= 0)
                {
                    Debug.WriteLine("[FFmpeg] Audio stream found");
                }
                return true;
            }

            Debug.WriteLine("[FFmpeg] No video stream found");
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FFmpeg] Open file error: {ex.Message}");
            return false;
        }
    }

    private unsafe void InitializeVideoDecoder(AVStream* stream)
    {
        var codecParams = stream->codecpar;
        var codec = ffmpeg.avcodec_find_decoder(codecParams->codec_id);
        if (codec == null)
        {
            Debug.WriteLine("[FFmpeg] Video codec not found");
            return;
        }

        var vCodecCtx = ffmpeg.avcodec_alloc_context3(codec);
        _videoCodecContext = (IntPtr)vCodecCtx;
        if (vCodecCtx == null)
        {
            Debug.WriteLine("[FFmpeg] Failed to allocate video codec context");
            return;
        }

        ffmpeg.avcodec_parameters_to_context(vCodecCtx, codecParams);
        vCodecCtx->thread_count = Math.Max(1, Environment.ProcessorCount - 1);

        int ret = TryOpenDecoderWithHardware(codec, codecParams->codec_id, vCodecCtx);
        if (ret < 0)
        {
            Debug.WriteLine("[FFmpeg] Hardware decoding not available, using software decoding");
            ret = ffmpeg.avcodec_open2(vCodecCtx, codec, null);
            if (ret < 0)
            {
                Debug.WriteLine("[FFmpeg] Failed to open video codec");
                return;
            }
        }
        else
        {
            Debug.WriteLine("[FFmpeg] Hardware decoding enabled");
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
        Debug.WriteLine("[FFmpeg] Hardware acceleration disabled, using software decoding");
        return -1;
    }

    private unsafe void InitializeAudioDecoder(AVStream* stream)
    {
        var codecParams = stream->codecpar;
        var codec = ffmpeg.avcodec_find_decoder(codecParams->codec_id);
        if (codec == null)
        {
            Debug.WriteLine("[FFmpeg] Audio codec not found");
            return;
        }

        var aCodecCtx = ffmpeg.avcodec_alloc_context3(codec);
        _audioCodecContext = (IntPtr)aCodecCtx;
        if (aCodecCtx == null)
        {
            Debug.WriteLine("[FFmpeg] Failed to allocate audio codec context");
            return;
        }

        ffmpeg.avcodec_parameters_to_context(aCodecCtx, codecParams);

        if (ffmpeg.avcodec_open2(aCodecCtx, codec, null) < 0)
        {
            Debug.WriteLine("[FFmpeg] Failed to open audio codec");
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

            Debug.WriteLine("[FFmpeg] Audio initialized");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FFmpeg] Audio initialization failed: {ex.Message}");
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
            Debug.WriteLine($"[FFmpeg] Decode error: {ex.Message}");
        }
        finally
        {
            _isPlaying = false;
            try { _waveOut?.Stop(); } catch { }
            Debug.WriteLine("[FFmpeg] DecodeLoop finished");
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
                    Debug.WriteLine($"[FFmpeg] av_seek_frame failed: {ret}");
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
                Debug.WriteLine($"[FFmpeg] Seek to: {position}s");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FFmpeg] ExecuteSeekNow exception: {ex.Message}");
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
                    Debug.WriteLine($"[FFmpeg] HW frame transfer failed");
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

    public void Pause()
    {
        if (_isPlaying && !_isPaused)
        {
            _isPaused = true;
            _clockBase = GetPlaybackClock();
            _waveOut?.Pause();
            Debug.WriteLine("[FFmpeg] Paused");
        }
    }

    public void Resume()
    {
        if (_isPlaying && _isPaused)
        {
            _isPaused = false;
            _clockStartTicks = Stopwatch.GetTimestamp();
            _waveOut?.Play();
            Debug.WriteLine("[FFmpeg] Resumed");
        }
    }

    public void SetVolume(int volume)
    {
        _volume = Math.Clamp(volume, 0, 100);
        if (_waveOut != null)
        {
            _waveOut.Volume = _volume / 100f;
        }
        Debug.WriteLine($"[FFmpeg] Volume set to: {_volume}");
    }

    public void Seek(int position)
    {
        if (position < 0) return;
        _pendingSeekTime = position;
        _pendingSeek = true;
        Debug.WriteLine($"[FFmpeg] Seek requested to: {position}s");
    }

    public void SeekSync(int position)
    {
        try
        {
            lock (_seekLock)
            {
                if (!_isPlaying && !_isPaused)
                {
                    Debug.WriteLine("[FFmpeg] SeekSync ignored - not playing");
                    return;
                }

                if (_formatContext == IntPtr.Zero || _videoStreamIndex < 0)
                {
                    Debug.WriteLine("[FFmpeg] SeekSync ignored - invalid state");
                    return;
                }

                try
                {
                    unsafe
                    {
                        var fmtCtx = (AVFormatContext*)_formatContext;
                        if (fmtCtx == null)
                        {
                            Debug.WriteLine("[FFmpeg] SeekSync ignored - fmtCtx is null");
                            return;
                        }

                        long targetPts = (long)(position * ffmpeg.AV_TIME_BASE);
                        int ret = ffmpeg.av_seek_frame(fmtCtx, -1, targetPts, ffmpeg.AVSEEK_FLAG_BACKWARD);
                        if (ret < 0)
                        {
                            Debug.WriteLine($"[FFmpeg] av_seek_frame failed: {ret}");
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
                    Debug.WriteLine($"[FFmpeg] SeekSync to: {position}s");
                }
                catch (System.AccessViolationException ex)
                {
                    Debug.WriteLine($"[FFmpeg] SeekSync access violation: {ex.Message}");
                }
                catch (System.NullReferenceException ex)
                {
                    Debug.WriteLine($"[FFmpeg] SeekSync null reference: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FFmpeg] SeekSync exception: {ex.Message}");
        }
    }

    public void Next()
    {
        Debug.WriteLine("[FFmpeg] Next not supported");
    }

    public void Previous()
    {
        Debug.WriteLine("[FFmpeg] Previous not supported");
    }

    public void ToggleFullscreen()
    {
        Debug.WriteLine("[FFmpeg] Toggle fullscreen (handled by UI)");
    }

    public void SetAudioTrack(int trackIndex)
    {
    }

    public void SetSpuTrack(int trackIndex)
    {
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

    private string DetectAudioFormat(string codecName, AVCodecID codecId)
    {
        // 检测常见的音频格式
        switch (codecId)
        {
            case AVCodecID.AV_CODEC_ID_AAC:
                return "AAC";
            case AVCodecID.AV_CODEC_ID_AC3:
                return "Dolby Digital (AC3)";
            case AVCodecID.AV_CODEC_ID_EAC3:
                return "Dolby Digital Plus (E-AC3)";
            case AVCodecID.AV_CODEC_ID_DTS:
                return "DTS";
            case AVCodecID.AV_CODEC_ID_FLAC:
                return "FLAC";
            case AVCodecID.AV_CODEC_ID_MP3:
                return "MP3";
            case AVCodecID.AV_CODEC_ID_VORBIS:
                return "Vorbis";
            case AVCodecID.AV_CODEC_ID_OPUS:
                return "Opus";
            default:
                // 处理其他 DTS 变体
                if (codecName.Contains("dts", StringComparison.OrdinalIgnoreCase))
                {
                    if (codecName.Contains("hd", StringComparison.OrdinalIgnoreCase) &&
                        codecName.Contains("ma", StringComparison.OrdinalIgnoreCase))
                    {
                        return "DTS-HD Master Audio";
                    }
                    else if (codecName.Contains("hd", StringComparison.OrdinalIgnoreCase))
                    {
                        return "DTS-HD";
                    }
                    return "DTS";
                }
                return codecName.ToUpper();
        }
    }
}