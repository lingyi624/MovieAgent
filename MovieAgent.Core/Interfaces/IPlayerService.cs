namespace MovieAgent.Core.Interfaces;

/// <summary>
/// 播放器服务接口 - 提供视频播放控制功能
/// 支持 FFmpeg 硬解码播放
/// </summary>
public interface IPlayerService
{
    /// <summary>是否正在播放</summary>
    bool IsPlaying { get; }

    /// <summary>是否暂停</summary>
    bool IsPaused { get; }

    /// <summary>视频总时长</summary>
    TimeSpan Duration { get; }

    /// <summary>音频时间戳</summary>
    TimeSpan AudioTimestamp { get; }
    /// <summary>当前播放位置</summary>
    TimeSpan Position { get; }
    long AudioPlayPosition { get; }

    /// <summary>视频时间戳</summary>
    TimeSpan VideoTimestamp { get; }

    /// <summary>音量（0-1）</summary>
    float Volume { get; }

    /// <summary>视频宽度</summary>
    int VideoWidth { get; }

    /// <summary>视频高度</summary>
    int VideoHeight { get; }

    /// <summary>帧更新事件</summary>
    event EventHandler<byte[]>? FrameUpdated;

    /// <summary>播放结束事件</summary>
    event EventHandler? PlaybackEnded;

    /// <summary>
    /// Blazor请求播放事件 - 用于通知WPF显示视频overlay
    /// </summary>
    event EventHandler? PlaybackRequestedByBlazor;

    /// <summary>
    /// 请求播放 - Blazor UI触发，通知MainWindow显示视频overlay
    /// </summary>
    /// <param name="filePath">视频文件路径</param>
    void RequestPlayback(string filePath);

    /// <summary>
    /// 获取当前请求播放的文件路径
    /// </summary>
    string? GetCurrentRequestedFilePath();

    /// <summary>
    /// 开始播放
    /// </summary>
    /// <param name="filePath">视频文件路径</param>
    Task PlayAsync(string filePath);

    /// <summary>停止播放</summary>
    Task StopAsync();

    /// <summary>暂停播放</summary>
    void Pause();

    /// <summary>继续播放</summary>
    void Resume();

    /// <summary>
    /// 设置音量
    /// </summary>
    /// <param name="volume">音量值（0-100）</param>
    void SetVolume(int volume);

    /// <summary>
    /// 跳转到指定位置
    /// </summary>
    /// <param name="position">位置（秒）</param>
    void Seek(int position);

    /// <summary>播放下一个</summary>
    void Next();

    /// <summary>播放上一个</summary>
    void Previous();

    /// <summary>切换全屏</summary>
    void ToggleFullscreen();

    /// <summary>音轨数量</summary>
    int AudioTrackCount { get; }

    /// <summary>当前音轨索引</summary>
    int CurrentAudioTrack { get; }

    /// <summary>
    /// 切换音轨
    /// </summary>
    /// <param name="trackIndex">音轨索引</param>
    void SetAudioTrack(int trackIndex);

    /// <summary>字幕轨数量</summary>
    int SpuTrackCount { get; }

    /// <summary>当前字幕轨索引</summary>
    int CurrentSpuTrack { get; }

    /// <summary>
    /// 切换字幕轨
    /// </summary>
    /// <param name="trackIndex">字幕轨索引</param>
    void SetSpuTrack(int trackIndex);
}
