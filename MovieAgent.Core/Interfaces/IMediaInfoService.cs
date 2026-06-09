namespace MovieAgent.Core.Interfaces;

/// <summary>
/// 媒体信息服务接口 - 解析视频文件的元数据信息
/// 使用 FFprobe 获取视频编码、分辨率、HDR等详细信息
/// </summary>
public interface IMediaInfoService
{
    /// <summary>
    /// 获取媒体信息
    /// </summary>
    /// <param name="filePath">视频文件路径</param>
    /// <returns>媒体信息结果</returns>
    MediaInfoResult GetMediaInfo(string filePath);
}

/// <summary>
/// 媒体信息结果 - 包含视频文件的所有元数据
/// </summary>
public class MediaInfoResult
{
    /// <summary>视频编码格式（如 H264, HEVC）</summary>
    public string? VideoCodec { get; set; }

    /// <summary>音频编码格式（如 AAC, DTS, TrueHD）</summary>
    public string? AudioCodec { get; set; }

    /// <summary>分辨率字符串（如 1920x1080）</summary>
    public string? Resolution { get; set; }

    /// <summary>视频宽度（像素）</summary>
    public int Width { get; set; }

    /// <summary>视频高度（像素）</summary>
    public int Height { get; set; }

    /// <summary>HDR类型：HDR10 / DolbyVision / HLG / SDR</summary>
    public string? HdrType { get; set; }

    /// <summary>视频码率（Mbps）</summary>
    public double VideoBitrate { get; set; }

    /// <summary>音频码率（Kbps）</summary>
    public double AudioBitrate { get; set; }

    /// <summary>帧率（fps）</summary>
    public double FrameRate { get; set; }

    /// <summary>时长（毫秒）</summary>
    public long Duration { get; set; }

    /// <summary>是否成功解析</summary>
    public bool Success { get; set; }

    /// <summary>错误信息（如果解析失败）</summary>
    public string? ErrorMessage { get; set; }
}
