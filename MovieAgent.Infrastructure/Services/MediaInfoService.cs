using System.Diagnostics;
using System.IO;
using FFmpeg.AutoGen;
using MovieAgent.Core.Interfaces;

namespace MovieAgent.Infrastructure.Services;

public class MediaInfoService : IMediaInfoService
{
    public MediaInfoResult GetMediaInfo(string filePath)
    {
        var result = new MediaInfoResult();

        if (!File.Exists(filePath))
        {
            result.ErrorMessage = "File not found";
            return result;
        }

        try
        {
            var infoResult = ParseMediaInfoUnsafe(filePath);
            if (infoResult != null)
            {
                result = infoResult;
            }
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            Debug.WriteLine($"[MediaInfo] Error parsing {filePath}: {ex.Message}");
        }

        return result;
    }

    private static unsafe MediaInfoResult ParseMediaInfoUnsafe(string filePath)
    {
        var result = new MediaInfoResult();
        AVFormatContext* fmtCtx = null;

        try
        {
            fmtCtx = ffmpeg.avformat_alloc_context();
            if (fmtCtx == null)
            {
                result.ErrorMessage = "Failed to allocate format context";
                return result;
            }

            if (ffmpeg.avformat_open_input(&fmtCtx, filePath, null, null) != 0)
            {
                result.ErrorMessage = "Failed to open file";
                Debug.WriteLine($"[MediaInfo] avformat_open_input failed: {filePath}");
                return result;
            }

            if (ffmpeg.avformat_find_stream_info(fmtCtx, null) < 0)
            {
                result.ErrorMessage = "Failed to find stream info";
                Debug.WriteLine($"[MediaInfo] avformat_find_stream_info failed: {filePath}");
                return result;
            }

            // 获取时长
            result.Duration = (long)(fmtCtx->duration / ffmpeg.AV_TIME_BASE * 1000);

            // 查找视频流
            int videoStreamIndex = -1;
            for (int i = 0; i < (int)fmtCtx->nb_streams; i++)
            {
                var stream = fmtCtx->streams[i];
                if (stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    videoStreamIndex = i;
                    break;
                }
            }

            // 查找音频流
            int audioStreamIndex = -1;
            for (int i = 0; i < (int)fmtCtx->nb_streams; i++)
            {
                var stream = fmtCtx->streams[i];
                if (stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    audioStreamIndex = i;
                    break;
                }
            }

            // 解析视频信息
            if (videoStreamIndex >= 0)
            {
                var videoStream = fmtCtx->streams[videoStreamIndex];
                var codecParams = videoStream->codecpar;

                // 获取视频编码
                var codecId = codecParams->codec_id;
                var codecDescriptor = ffmpeg.avcodec_descriptor_get(codecId);
                string codecName = codecDescriptor != null
                    ? new string((sbyte*)codecDescriptor->name)
                    : codecId.ToString();
                result.VideoCodec = NormalizeVideoCodec(codecName);

                // 分辨率
                result.Width = codecParams->width;
                result.Height = codecParams->height;
                result.Resolution = DetermineResolution(result.Width, result.Height);

                // 帧率
                if (videoStream->avg_frame_rate.num != 0 && videoStream->avg_frame_rate.den != 0)
                {
                    result.FrameRate = (double)videoStream->avg_frame_rate.num / videoStream->avg_frame_rate.den;
                }

                // 视频码率
                if (codecParams->bit_rate > 0)
                {
                    result.VideoBitrate = codecParams->bit_rate / 1000.0;
                }
                else if (fmtCtx->bit_rate > 0)
                {
                    result.VideoBitrate = fmtCtx->bit_rate / 1000.0;
                }

                // HDR 检测
                result.HdrType = DetectHdrTypeUnsafe(codecParams);
            }

            // 解析音频信息
            if (audioStreamIndex >= 0)
            {
                var audioStream = fmtCtx->streams[audioStreamIndex];
                var codecParams = audioStream->codecpar;

                // 获取音频编码
                var codecId = codecParams->codec_id;
                var codecDescriptor = ffmpeg.avcodec_descriptor_get(codecId);
                string audioCodecName = codecDescriptor != null
                    ? new string((sbyte*)codecDescriptor->name)
                    : codecId.ToString();

                // 检查 profile
                string profileName = string.Empty;
                var audioDecoder = ffmpeg.avcodec_find_decoder(codecId);
                if (audioDecoder != null)
                {
                    var audioCodecCtx = ffmpeg.avcodec_alloc_context3(audioDecoder);
                    if (audioCodecCtx != null)
                    {
                        if (ffmpeg.avcodec_parameters_to_context(audioCodecCtx, codecParams) >= 0)
                        {
                            profileName = audioCodecCtx->profile.ToString();
                        }
                        ffmpeg.avcodec_free_context(&audioCodecCtx);
                    }
                }

                result.AudioCodec = NormalizeAudioCodec(audioCodecName, profileName, codecId);

                // 音频码率
                if (codecParams->bit_rate > 0)
                {
                    result.AudioBitrate = codecParams->bit_rate / 1000.0;
                }
            }

            result.Success = true;
            Debug.WriteLine($"[MediaInfo] Parsed: {filePath}");
            Debug.WriteLine($"  Video: {result.VideoCodec}, {result.Resolution} ({result.Width}x{result.Height}), HDR: {result.HdrType}, {result.FrameRate:F2} fps, {result.VideoBitrate:F0} kbps");
            Debug.WriteLine($"  Audio: {result.AudioCodec}, {result.AudioBitrate:F0} kbps, Duration: {result.Duration}ms");
        }
        finally
        {
            if (fmtCtx != null)
            {
                ffmpeg.avformat_close_input(&fmtCtx);
            }
        }

        return result;
    }

    private static string NormalizeVideoCodec(string codec)
    {
        if (string.IsNullOrEmpty(codec)) return "Unknown";

        codec = codec.ToUpperInvariant();

        if (codec.Contains("HEVC") || codec.Contains("H265"))
            return "HEVC";

        if (codec.Contains("H264") || codec == "AVC1" || codec == "AVC")
            return "H.264";

        if (codec.Contains("AV1"))
            return "AV1";

        if (codec.Contains("VP9"))
            return "VP9";

        if (codec.Contains("VP8"))
            return "VP8";

        if (codec.Contains("MPEG"))
            return "MPEG";

        if (codec.Contains("VC1") || codec.Contains("WMV"))
            return "VC-1";

        return codec;
    }

    private static string NormalizeAudioCodec(string codec, string profile, AVCodecID codecId)
    {
        if (string.IsNullOrEmpty(codec)) return "Unknown";

        codec = codec.ToUpperInvariant();
        profile = profile.ToUpperInvariant();

        if (codecId == AVCodecID.AV_CODEC_ID_TRUEHD || codec.Contains("TRUEHD"))
            return "TrueHD";

        if (codecId == AVCodecID.AV_CODEC_ID_EAC3 || codec.Contains("EAC3") || codec.Contains("DDP") || profile.Contains("E-AC-3"))
            return "E-AC-3";

        if (codecId == AVCodecID.AV_CODEC_ID_AC3 || codec.Contains("AC3"))
            return "AC3";

        if (codecId == AVCodecID.AV_CODEC_ID_DTS || codec.Contains("DTS"))
        {
            if (profile.Contains("MA") || profile.Contains("HD") || codec.Contains("HD"))
                return "DTS-HD";
            return "DTS";
        }

        if (codecId == AVCodecID.AV_CODEC_ID_AAC || codec.Contains("AAC"))
            return "AAC";

        if (codecId == AVCodecID.AV_CODEC_ID_MP3 || codec.Contains("MP3") || codec.Contains("MP2"))
            return "MP3";

        if (codecId == AVCodecID.AV_CODEC_ID_FLAC || codec.Contains("FLAC"))
            return "FLAC";

        if (codecId == AVCodecID.AV_CODEC_ID_VORBIS || codec.Contains("VORBIS") || codec.Contains("OGG"))
            return "Vorbis";

        if (codecId == AVCodecID.AV_CODEC_ID_OPUS || codec.Contains("OPUS"))
            return "Opus";

        if (codecId.ToString().StartsWith("AV_CODEC_ID_PCM") || codec.Contains("PCM") || codec.Contains("LPCM"))
            return "PCM";

        return codec;
    }

    private static string DetermineResolution(int width, int height)
    {
        if (height >= 2160 || width >= 3840)
            return "4K";

        if (height >= 1080 || width >= 1920)
            return "1080p";

        if (height >= 720 || width >= 1280)
            return "720p";

        if (height >= 480 || width >= 720)
            return "480p";

        if (height >= 360)
            return "SD";

        return "Other";
    }

    private static unsafe string DetectHdrTypeUnsafe(AVCodecParameters* codecParams)
    {
        var colorPrimaries = codecParams->color_primaries;
        var colorTransfer = codecParams->color_trc;

        bool isBT2020 = colorPrimaries == AVColorPrimaries.AVCOL_PRI_BT2020;
        bool isPQ = colorTransfer == AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084;
        bool isHLG = colorTransfer == AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67;

        if (isBT2020 && isPQ)
            return "HDR10";

        if (isBT2020 && isHLG)
            return "HLG";

        if (isBT2020)
            return "HDR";

        return "SDR";
    }
}
