namespace MovieAgent.Core.Interfaces;

public class MediaInfoResult
{
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public string? Resolution { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string? HdrType { get; set; }
    public double VideoBitrate { get; set; }
    public double AudioBitrate { get; set; }
    public double FrameRate { get; set; }
    public long Duration { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}

public interface IMediaInfoService
{
    MediaInfoResult GetMediaInfo(string filePath);
}
