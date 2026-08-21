using MovieAgent.FFmpegDecoder;
using System;
using System.Collections.Generic;
using Vortice.Direct3D11;
using Vortice.Direct3D12;
using static MovieAgent.FFmpegDecoder.FFmpegDecoderEngine;

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
    D3DMode? CurrentD3dModel { get;  }

    /// <summary>视频时间戳</summary>
    TimeSpan VideoTimestamp { get; }

    /// <summary>音量（0-1）</summary>
    float Volume { get; }

    /// <summary>视频宽度</summary>
    int VideoWidth { get; }

    /// <summary>视频高度</summary>
    int VideoHeight { get; }

    /// <summary>是否为杜比视界视频</summary>
    bool IsDolbyVision { get; }

    /// <summary>是否为HDR传输特性（PQ/HLG/DV）。SDR 10bit视频同样输出P010，渲染器不能仅凭P010判HDR</summary>
    bool IsPqTransfer { get; }

    /// <summary>是否为ICtCp色彩空间输入（杜比视界Profile 5，需专用着色器渲染）</summary>
    bool IsIctcpInput { get; }

    /// <summary>最近一帧的杜比视界 RPU 渲染元数据（ycc_to_rgb_matrix 等），供着色器使用</summary>
    DoviRenderMetadata? DoviMetadata { get; }

    /// <summary>帧更新事件</summary>
    event EventHandler<FrameData>? FrameUpdated;

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
    /// 获取音频轨道列表
    /// </summary>
    List<AudioTrackInfo>? GetAudioTracks();

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
    /// 获取字幕轨道列表
    /// </summary>
    List<SubtitleTrackInfo>? GetSubtitleTracks();

    /// <summary>
    /// 切换字幕轨
    /// </summary>
    /// <param name="trackIndex">字幕轨索引</param>
    void SetSpuTrack(int trackIndex);

    /// <summary>
    /// 截图
    /// </summary>
    void TakeScreenshot();

    /// <summary>
    /// 设置字幕延迟
    /// </summary>
    /// <param name="delayMs">延迟时间（毫秒）</param>
    void SetSubtitleDelay(double delayMs);

    /// <summary>
    /// 设置播放速度
    /// </summary>
    /// <param name="speed">播放速度</param>
    void SetPlaybackSpeed(double speed);
    void SetD3d11Device(ID3D11Device device);
    void SetD3d9Device(Vortice.Direct3D9.IDirect3DDevice9Ex device);
    void SetD3d12Device(ID3D12Device device);
    void SetD3d12CommandQueue(IntPtr commandQueuePtr);
}
