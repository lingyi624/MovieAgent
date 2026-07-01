using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using MovieAgent.Controls;
using MovieAgent.Core.Interfaces;
using MovieAgent.FFmpegDecoder;
using MovieAgent.Infrastructure.Services;
using MovieAgent.Services;
using NAudio.CoreAudioApi;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MovieAgent.D3D12Window;

public partial class ControlWindow : Window
{
    private IPlayerService? _playerService;
    private readonly ILoggerService _logger;
    private ISubtitleService? _subtitleService;
    private int _frameCount = 0;
    private DispatcherTimer? _hideControlsTimer;
    private bool _isFullScreen = false; // 此窗口始终全屏浮于视频之上
    private string _currentMovieTitle = string.Empty;
    private string _currentAudioTrack = "立体声";
    private string _currentSubtitle = "无";
    private string _currentDecoderName = string.Empty;
    private string _currentDecodeMode = "自动";

     private Rect _normalVideoRect;
    private Rect _normalControlRect;

    // 去重调度
    private volatile bool _renderScheduled;
    private FrameData? _pendingFrame;
    private readonly object _pendingFrameLock = new();

    // 外部字幕
    private string? _externalSubtitlePath;
    private List<SubtitleItem> _externalSubtitles = new();
    private DispatcherTimer? _subtitleUpdateTimer;
    private DispatcherTimer? _subtitleHideTimer;

    // 进度更新
    private DispatcherTimer? _progressTimer;
    private string? _lastPlayPauseContent;

    // 系统信息缓存
    private string _cachedCpuInfo = string.Empty;
    private string _cachedMemoryInfo = string.Empty;
    private string _cachedGpuInfo = string.Empty;
    private string _cachedOSInfo = string.Empty;

    private WindowState _previousWindowState;
    private WindowStyle _previousWindowStyle;
    private bool _previousTopmost;
    private double _previousWidth;
    private double _previousHeight;
    private double _previousLeft;
    private double _previousTop;

    // 视频窗口引用
    private readonly VideoWindow _videoWindow;

    public ControlWindow(VideoWindow videoWindow)
    {
        _videoWindow = videoWindow ?? throw new ArgumentNullException(nameof(videoWindow));

        var services = ((App)Application.Current).Services;
        _logger = services.GetRequiredService<ILoggerService>();
        _playerService = services.GetRequiredService<IPlayerService>();
        _subtitleService = services.GetService<ISubtitleService>();

        InitializeComponent();
 
        // 订阅事件
        if (_playerService is LocalPlayerService localPlayer)
        {
            localPlayer.PerformanceWarning += OnPerformanceWarning;
            localPlayer.ResolutionDownscale += OnResolutionDownscale;
            localPlayer.SubtitleDecoded += OnSubtitleDecoded;
            localPlayer.PlaybackFailed += OnPlaybackFailed;
        }
        _playerService.FrameUpdated += OnFrameUpdated;

        // 初始化定时器
        _hideControlsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _hideControlsTimer.Tick += (s, e) => HideControls();

        // 设置窗口全屏透明覆盖
        // this.WindowState = WindowState.Maximized;
        //this.Width = SystemParameters.PrimaryScreenWidth;
        //this.Height = SystemParameters.PrimaryScreenHeight;
        //this.Left = 0;
        //this.Top = 0;
        //this.Topmost = true;
        //this.ShowActivated = false;

        this.Left = 0;
        this.Top = 0;
        this.Width = SystemParameters.PrimaryScreenWidth;
        this.Height = SystemParameters.PrimaryScreenHeight;
        this.WindowState = WindowState.Normal;   // 显式保持 Normal

        // this.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // ARGB: #01000000
        ShowControls(); // 强制显示顶部和底部栏

        this.Loaded += ControlWindow_Loaded;
    }

    private void ControlWindow_Loaded(object sender, RoutedEventArgs e)
    {
        //MessageBox.Show("ControlWindow 已加载");
        _videoWindow.LocationChanged += (s, e) => { if (!_isFullScreen) SyncControlPosition(); };
        _videoWindow.SizeChanged += (s, e) => { if (!_isFullScreen) SyncControlPosition(); };
    }
    private void SyncControlPosition()
    {
        this.Left = _videoWindow.Left;
        this.Top = _videoWindow.Top;
        this.Width = _videoWindow.Width;
        this.Height = _videoWindow.Height;
    }
    public void PlayMovie(string filePath)
    {
        Dispatcher.Invoke(async () =>
        {
            try
            { 
                StopPlaybackInternal();

                bool isFile = File.Exists(filePath);
                bool isDir = Directory.Exists(filePath);

                if (!isFile && !isDir)
                {
                    MessageBox.Show($"路径不存在: {filePath}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // 检测 BDMV / ISO 蓝光，显示标题选择对话框
                string actualPlayPath = filePath;
                string? isoMountPoint = null;

                // ISO 文件：先挂载
                if (isFile && filePath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                {
                    isoMountPoint = MountIsoForBdmv(filePath);
                    if (!string.IsNullOrEmpty(isoMountPoint) && LocalPlayerService.IsBdmvStructure(isoMountPoint))
                    {
                        actualPlayPath = ShowBdmvTitleDialog(isoMountPoint, $"ISO: {Path.GetFileName(filePath)}");
                        if (string.IsNullOrEmpty(actualPlayPath))
                        {
                            // 用户取消，卸载ISO
                            DismountIso(filePath);
                            return;
                        }
                    }
                    else if (!string.IsNullOrEmpty(isoMountPoint))
                    {
                        // 非BDMV结构的ISO，直接播放ISO文件
                        actualPlayPath = filePath;
                    }
                }
                // BDMV 文件夹
                else if (isDir && LocalPlayerService.IsBdmvStructure(filePath))
                {
                    actualPlayPath = ShowBdmvTitleDialog(filePath, Path.GetFileName(filePath));
                    if (string.IsNullOrEmpty(actualPlayPath))
                    {
                        return; // 用户取消
                    }
                }

                // 保存当前播放文件路径，用于性能警告时切换到系统播放器
                _currentPlayingFilePath = actualPlayPath;
                _currentIsoMountPoint = isoMountPoint;

                // 提取电影标题（从文件路径中获取）
                _currentMovieTitle = Path.GetFileNameWithoutExtension(filePath);
                if (string.IsNullOrEmpty(_currentMovieTitle))
                    _currentMovieTitle = Path.GetFileName(filePath);

                if (_playerService != null)
                {
                    _logger.Debug("[Player] 订阅 FrameUpdated 事件");
                    _playerService.FrameUpdated += OnFrameUpdated;
                    _logger.Debug("[Player] FrameUpdated 事件已订阅");
                    _logger.Debug($"[Player] ===== 开始播放流程 ===== ");
                    _logger.Debug($"[Player] 文件路径: {actualPlayPath}");
                    _logger.Debug($"[Player] 电影标题: {_currentMovieTitle}");
                    //var renderer = _videoWindow.Renderer;
                    //var device = renderer?.GetDevice();
                    //if (device != null)
                    //{
                    //    _playerService.SetD3dDevice(device);
                    //    // 用 device 初始化硬件解码器
                    //    _logger.Debug($"[VideoRendererControl] GetDevice已调用,初始化硬件解码器");
                    //}
                    await _playerService.PlayAsync(actualPlayPath);
                    _logger.Debug($"[Player] PlayAsync 调用完成");

                    // 显示视频播放层，隐藏 Blazor WebView
                    _logger.Debug($"[Player] 切换到视频播放层");
                 

                    // 切换到全屏
                    _logger.Debug($"[Player] 进入全屏模式");
                    EnterFullScreen();

                    // 更新信息显示
                    _logger.Debug("[Player] 更新播放信息");
                    UpdatePlaybackInfo();

                    // 启动进度更新定时器
                    _logger.Debug("[Player] 启动进度更新定时器");
                    StartProgressUpdate();

                    // 更新音频和字幕轨道列表
                    _logger.Debug("[Player] 更新轨道列表");
                    UpdateAudioTrackList();
                    UpdateSubtitleTrackList();

                    _frameCount = 0;
                    _logger.Debug("[Player] ===== 播放流程初始化完成 ===== ");
                    

                    UpdatePlayStatus();

                    ShowControls();
                    return;
                }

                FallbackToSystemPlayer(filePath);
            }
            catch (Exception ex)
            {
                _logger.Debug($"[Player] FFmpeg 播放失败: {ex.Message}");
                FallbackToSystemPlayer(filePath);
            }
        });
    }


    private void OnFrameUpdated(object? sender, FrameData frame)
    {
        _frameCount++;
        if (_frameCount % 30 == 0)
            _logger.Debug($"[Control] 第 {_frameCount} 帧");
       
        try
        {
            var renderer = _videoWindow.GetDevice();
            if (renderer == null) return;
            if (_playerService == null ) return;
            if (_playerService.VideoWidth <= 0 || _playerService.VideoHeight <= 0) return;

            bool needSchedule = false;
            lock (_pendingFrameLock)
            {
                _pendingFrame = frame;
                if (!_renderScheduled)
                {
                    _renderScheduled = true;
                    needSchedule = true;
                }
            }

            if (needSchedule)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
                {
                    FrameData? frameToRender = null;
                    lock (_pendingFrameLock)
                    {
                        frameToRender = _pendingFrame;
                        _pendingFrame = null;
                        _renderScheduled = false;
                    }

                    if (frameToRender != null && _videoWindow.GetDevice() != null)
                    {
                        if (frameToRender.IsHardwareFrame)
                            _videoWindow.VideoView.RenderHardwareTexture(
                                frameToRender.NV12TexturePtr,
                                frameToRender.Width,
                                frameToRender.Height,
                                frameToRender.TextureArrayIndex,
                                frameToRender.IsTextureArray
                                  );
                        else
                        {
                            _videoWindow.VideoView.UpdateFrame(
                                frameToRender.YPlane, frameToRender.UPlane, frameToRender.VPlane,
                                frameToRender.Width, frameToRender.Height,
                                frameToRender.YStride, frameToRender.UStride, frameToRender.VStride); 
                        }

                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"[Control] 帧更新失败: {ex.Message}");
        }
    }

    // 以下是移植自 MainWindow 的所有控制方法（进度、音量、字幕、全屏、事件处理等），
    // 由于篇幅限制，此处省略具体实现，实际应完整复制过来。
    // 需注意：所有控件引用现在指向 ControlWindow.xaml 中定义的控件。
    // 我已将核心方法签名列出，你需要把 MainWindow.cs 中对应方法体复制过来，
    // 并将 "this" 替换为 "this"（相同），删去 Blazor 相关代码即可。
    // 关键修改：ExitFullScreen/EnterFullScreen 不再切换主窗口，仅影响此控制窗口的显示与否。
    private void UpdatePlaybackInfo()
    {
        if (_playerService == null) return;

        // 更新电影标题
        MovieTitleText.Text = _currentMovieTitle;

        // 更新播放状态
        PlayStatusText.Text = _playerService.IsPaused ? "⏸ 已暂停" : "▶ 正在播放";

        // 更新解码器信息
        var localPlayer = _playerService as LocalPlayerService;
        if (localPlayer != null)
        {
            var decoderName = localPlayer.CurrentDecoder.ToUpper();
            _currentDecoderName = !string.IsNullOrEmpty(decoderName) ? decoderName : "FFmpeg";
            _currentDecodeMode = "自动";
        }

        // 更新视频信息
        string videoText = _playerService.VideoWidth > 0 && _playerService.VideoHeight > 0
            ? $"视频: {_playerService.VideoWidth}x{_playerService.VideoHeight}"
            : "视频: 加载中...";
        VideoInfo.Text = videoText;

        // 更新总时长
        if (_playerService.Duration.TotalMilliseconds > 0)
        {
            TotalTimeText.Text = FormatTime(_playerService.Duration);
            ProgressSlider.Maximum = _playerService.Duration.TotalMilliseconds;
        }

        // 更新解码方式信息
        string decodeModeText = !string.IsNullOrEmpty(_currentDecoderName)
            ? $"解码: {_currentDecodeMode} ({_currentDecoderName.ToUpper()})"
            : "解码: 初始化中...";
        AudioInfo.Text = decodeModeText;

        // 更新右侧解码方式显示
        if (!string.IsNullOrEmpty(_currentDecoderName))
        {
            string modeDisplay = _currentDecodeMode switch
            {
                "Auto" => "自动检测",
                "Hardware" => "硬件加速",
                "Software" => "软件解码",
                _ => _currentDecodeMode
            };
            DecodeModeInfo.Text = $"{modeDisplay} - {_currentDecoderName.ToUpper()}";
        }
        else
        {
            DecodeModeInfo.Text = "初始化中...";
        }

        _logger.Debug($"[Player] 更新播放信息 - 标题: {_currentMovieTitle}, 状态: {PlayStatusText.Text}, 解码方式: {DecodeModeInfo.Text}, 解码器: {_currentDecoderName.ToUpper()}");
    }

    public void UpdatePlayStatus()
    {
        if (_playerService == null) return;

        PlayStatusText.Text = _playerService.IsPaused ? "⏸ 已暂停" : "▶ 正在播放";

        // 同步更新播放/暂停按钮
        string newContent = _playerService.IsPaused ? "▶" : "⏸";
        if (_lastPlayPauseContent != newContent)
        {
            PlayPauseButton.Content = newContent;
            _lastPlayPauseContent = newContent;
        }
    }

    public void SetAudioTrack(string trackName)
    {
        _currentAudioTrack = trackName;
        _logger.Debug($"[Player] 音效已切换: {trackName}");
    }

    public void SetSubtitle(string subtitleName)
    {
        _currentSubtitle = subtitleName;
        _logger.Debug($"[Player] 字幕已切换: {subtitleName}");
    }

    private void UpdateAudioTrackList()
    {
        try
        {
            AudioTrackListPanel.Children.Clear();

            var audioTracks = _playerService?.GetAudioTracks();
            if (audioTracks == null || audioTracks.Count == 0)
            {
                NoAudioTracksText.Visibility = Visibility.Visible;
                _logger.Debug("[Player] No audio tracks found");
                return;
            }

            NoAudioTracksText.Visibility = Visibility.Collapsed;

            for (int i = 0; i < audioTracks.Count; i++)
            {
                var track = audioTracks[i];
                // var codecName = !string.IsNullOrEmpty(track.Codec) ? track.Codec.ToUpper() : "Unknown";
                //var channelInfo = track.Channels > 0 ? $"{track.Channels}频道" : "";
                var displayText = (i + 1+"、") + track.Description;
                //$"{(i + 1)}. {track.Language ?? "未知"} - {codecName}{(string.IsNullOrEmpty(channelInfo) ? "" : $"-{channelInfo}")}";

                var button = new System.Windows.Controls.Button
                {
                    Content = displayText,
                    ToolTip = displayText,
                    Tag = track.Index,
                    Margin = new System.Windows.Thickness(0, 2, 0, 2),
                    Padding = new System.Windows.Thickness(8, 4, 8, 4),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                    Width = 240
                };

                int trackIndex = track.Index;
                button.Click += async (s, e) =>
                {
                    await Task.Run(() => _playerService?.SetAudioTrack(trackIndex));
                    Dispatcher.Invoke(UpdateAudioTrackList);
                };
                button.Style = (Style)this.FindResource("ControlButtonStyle");
                // 先应用基础样式
                if ((track.Index == _playerService?.CurrentAudioTrack))
                {
                    button.Background = new SolidColorBrush(Color.FromRgb(0, 120, 215));
                    button.FontWeight = FontWeights.Bold;
                }
                // 如果是当前选中的音频轨道，高亮显示
                //if (track.Index == _playerService?.CurrentAudioTrack)
                //{
                //    button.Content = $"✓ {displayText}";
                //    button.FontWeight = System.Windows.FontWeights.Bold;
                //    // 选中状态使用不同颜色
                //    button.Background = new System.Windows.Media.SolidColorBrush(
                //        System.Windows.Media.Color.FromRgb(0, 120, 215) // 蓝色高亮
                //    );
                //}

                AudioTrackListPanel.Children.Add(button);
                //if (SubtitleTrackListPanel.TryFindResource("ControlButtonStyle") is Style style1)
                //{
                //    button.Style = style1;
                //}
            }

            _logger.Debug($"[Player] Audio track list updated: {audioTracks.Count} tracks");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[Player] Failed to update audio track list");
        }
    }





    private void UpdateSubtitleTrackList()
    {
        try
        {
            SubtitleTrackListPanel.Children.Clear();

            var subtitleTracks = _playerService?.GetSubtitleTracks();

            // 添加"无字幕"选项 
            var noSubtitleButton = new System.Windows.Controls.Button
            {
                Content = "关闭字幕",
                ToolTip = Content,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                Tag = -1,
                Margin = new System.Windows.Thickness(0, 2, 0, 2),
                Padding = new System.Windows.Thickness(8, 4, 8, 4),
                Cursor = System.Windows.Input.Cursors.Hand,
                Width = 250
            };
            noSubtitleButton.Click += (s, e) =>
            {
                _playerService?.SetSpuTrack(-1);
                UpdateSubtitleTrackList();
            };

            if (_playerService?.CurrentSpuTrack < 0 || _playerService?.SpuTrackCount == 0)
            {
                noSubtitleButton.Content = "关闭字幕";
                noSubtitleButton.FontWeight = System.Windows.FontWeights.Bold;
            }
            SubtitleTrackListPanel.Children.Add(noSubtitleButton);
            if (SubtitleTrackListPanel.TryFindResource("ControlButtonStyle") is Style style)
            {
                noSubtitleButton.Style = style;
            }
            if (subtitleTracks == null || subtitleTracks.Count == 0)
            {
                NoSubtitleTracksText.Visibility = Visibility.Visible;
                _logger.Debug("[Player] No subtitle tracks found");
                return;
            }

            NoSubtitleTracksText.Visibility = Visibility.Collapsed;

            for (int i = 0; i < subtitleTracks.Count; i++)
            {
                var track = subtitleTracks[i];
                var codecName = !string.IsNullOrEmpty(track.Codec) ? track.Codec.ToUpper() : "Unknown";
                var forcedFlag = track.IsForced ? "[强制] " : "";
                var displayText = track.Description;
                //$"{(i + 1)}. {forcedFlag}{track.Language ?? "未知"} - {codecName}";

                var button = new System.Windows.Controls.Button
                {
                    Content = displayText,
                    Tag = track.Index,
                    Margin = new System.Windows.Thickness(0, 2, 0, 2),
                    Padding = new System.Windows.Thickness(8, 4, 8, 4),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Width = 250
                };

                int trackIndex = track.Index;
                button.Click += (s, e) =>
                {
                    _playerService?.SetSpuTrack(trackIndex);

                    // 如果切换到内部字幕，禁用外部字幕
                    if (trackIndex >= 0 && _externalSubtitlePath != null)
                    {
                        _subtitleUpdateTimer?.Stop();
                        _externalSubtitlePath = null;
                        _externalSubtitles.Clear();
                        UnloadSubtitleButton.Visibility = Visibility.Collapsed;
                        CurrentSubtitleText.Text = "";
                        _logger.Debug("[Player] 已切换到内部字幕，外部字幕已卸载");
                    }

                    UpdateSubtitleTrackList();
                };

                if (track.Index == _playerService?.CurrentSpuTrack)
                {
                    button.Content = $"✓ {displayText}";
                    button.FontWeight = System.Windows.FontWeights.Bold;
                }

                SubtitleTrackListPanel.Children.Add(button);
                if (SubtitleTrackListPanel.TryFindResource("ControlButtonStyle") is Style style1)
                {
                    button.Style = style1;
                }
            }


            _logger.Debug($"[Player] Subtitle track list updated: {subtitleTracks.Count} tracks");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[Player] Failed to update subtitle track list");
        }
    }

    #region 字幕和轨道切换

 
    private void StartProgressUpdate()
    {
        _progressTimer?.Stop();
        _progressTimer = new System.Windows.Threading.DispatcherTimer();
        _progressTimer.Interval = TimeSpan.FromMilliseconds(200);
        _progressTimer.Tick += (s, e) => UpdateProgress();
        _progressTimer.Start();//用来更新播放器文字信息
    }

 
    private void UpdateProgress()
    {
        if (_playerService == null || !_playerService.IsPlaying) return;

        try
        {
            var position = _playerService.Position.TotalMilliseconds;
            var duration = _playerService.Duration.TotalMilliseconds;

            if (duration > 0)
            {
                if (!ProgressSlider.IsMouseCaptureWithin)
                {
                    // 只在值变化超过100ms时更新，减少刷新频率
                    if (Math.Abs(ProgressSlider.Value - position) > 100)
                    {
                        ProgressSlider.Maximum = duration;
                        ProgressSlider.Value = position;
                    }
                }

                // 只更新时间文本，避免频繁更新控件
                CurrentTimeText.Text = FormatTime(_playerService.Position);

                // 更新总时长（首次获取到有效时长后设置）
                if (duration > 0)
                {
                    TotalTimeText.Text = FormatTime(_playerService.Duration);
                }

                // 只在播放状态改变时更新按钮内容
                string newContent = _playerService.IsPaused ? "▶" : "⏸";
                if (_lastPlayPauseContent != newContent)
                {
                    PlayPauseButton.Content = newContent;
                    _lastPlayPauseContent = newContent;
                }
            }
        }
        catch { }
    }



    private string FormatTime(TimeSpan time)
    {
        if (time.TotalHours >= 1)
            return $"{time.Hours:D2}:{time.Minutes:D2}:{time.Seconds:D2}";
        return $"{time.Minutes:D2}:{time.Seconds:D2}";
    }

    private void EnterFullScreen()
    {
        _normalVideoRect = new Rect(_videoWindow.Left, _videoWindow.Top, _videoWindow.Width, _videoWindow.Height);
        _normalControlRect = new Rect(this.Left, this.Top, this.Width, this.Height);

        _videoWindow.WindowStyle = WindowStyle.None;
        _videoWindow.ResizeMode = ResizeMode.NoResize;
        _videoWindow.Topmost = false;
        _videoWindow.Left = 0;
        _videoWindow.Top = 0;
        _videoWindow.Width = SystemParameters.PrimaryScreenWidth;
        _videoWindow.Height = SystemParameters.PrimaryScreenHeight;

        this.Left = 0;
        this.Top = 0;
        this.Width = SystemParameters.PrimaryScreenWidth;
        this.Height = SystemParameters.PrimaryScreenHeight;

        _isFullScreen = true;
        FullscreenButton.Content = "退出全屏";

        return; 
        if (_isFullScreen) return;

        _isFullScreen = true;

        // 保存当前状态
        _previousWindowState = this.WindowState;
        _previousWindowStyle = this.WindowStyle;
        _previousTopmost = this.Topmost;
        _previousWidth = this.Width;
        _previousHeight = this.Height;
        _previousLeft = this.Left;
        _previousTop = this.Top;

        // 切换到真正的全屏（隐藏任务栏）
        this.WindowStyle = WindowStyle.None;
        this.Topmost = true;
        this.WindowState = WindowState.Normal; // 先恢复正常状态
        this.Width = System.Windows.SystemParameters.PrimaryScreenWidth;
        this.Height = System.Windows.SystemParameters.PrimaryScreenHeight;
        this.Left = 0;
        this.Top = 0;
        this.WindowState = WindowState.Maximized;

        // 全屏模式下：显示底部控制栏，隐藏顶部面板
        TopBar.Visibility = Visibility.Collapsed;
        BottomBar.Visibility = Visibility.Visible;
        //ShowInTaskbar = false;
        //_videoWindow.ShowInTaskbar = ShowInTaskbar;

        _hideControlsTimer?.Start();

        _logger.Debug("[Player] 进入全屏模式 - 顶部面板已隐藏");
    }
    private void ToggleFullScreen()
    {
        if (_isFullScreen) ExitFullScreen();
        else EnterFullScreen();
    }

    private void ExitFullScreen()
    {
        _videoWindow.Left = _normalVideoRect.Left;
        _videoWindow.Top = _normalVideoRect.Top;
        _videoWindow.Width = _normalVideoRect.Width;
        _videoWindow.Height = _normalVideoRect.Height;
        _videoWindow.WindowStyle = WindowStyle.None;
        _videoWindow.ResizeMode = ResizeMode.CanResize;
        _videoWindow.Topmost = false;

        this.Left = _normalControlRect.Left;
        this.Top = _normalControlRect.Top;
        this.Width = _normalControlRect.Width;
        this.Height = _normalControlRect.Height;

        _isFullScreen = false;
        FullscreenButton.Content = "全屏";
        return;
        if (!_isFullScreen) return;

        _isFullScreen = false;

        // 恢复之前的窗口状态
        this.Topmost = _previousTopmost;
        this.WindowStyle = _previousWindowStyle;
        this.WindowState = _previousWindowState;
        this.Left = _previousLeft;
        this.Top = _previousTop;
        this.Width = _previousWidth;
        this.Height = _previousHeight;

        //ShowInTaskbar= true;
        //_videoWindow.ShowInTaskbar = ShowInTaskbar;
        _logger.Debug("[Player] 退出全屏模式");
    }

    private void OnPlaybackRequestedByBlazor(object? sender, EventArgs e)
    {
        _logger.Debug("[Player] OnPlaybackRequestedByBlazor 被调用");
        string? filePath = _playerService?.GetCurrentRequestedFilePath();
        if (!string.IsNullOrEmpty(filePath))
        {
            _logger.Debug($"[Player] 从RequestPlayback获取到文件路径: {filePath}");
            PlayMovie(filePath);
        }
        else
        {
            _logger.Warning("[Player] GetCurrentRequestedFilePath 返回空");
        }
    }

    private string? _currentPlayingFilePath;
    private string? _currentIsoMountPoint;

    private void OnPerformanceWarning(object? sender, DecodePerformanceWarning warning)
    {
        _logger.Warning($"[Player] 性能警告: {warning.Message}");
        _logger.Warning($"[Player] 当前分辨率: {warning.CurrentWidth}x{warning.CurrentHeight}");
        _logger.Warning($"[Player] 建议分辨率: {warning.SuggestedWidth}x{warning.SuggestedHeight}");

        Dispatcher.Invoke(() =>
        {
            // 停止进度更新
            _progressTimer?.Stop();

            // 显示警告消息框
            string message = $"当前视频分辨率较高 ({warning.CurrentWidth}x{warning.CurrentHeight})，" +
                           $"建议降低分辨率到 {warning.SuggestedWidth}x{warning.SuggestedHeight} 以获得流畅播放。\n\n" +
                           $"是否使用系统播放器播放？\n" +
                           $"(系统播放器可以更好地处理高分辨率视频)";

            MessageBoxResult result = MessageBox.Show(
                message,
                "解码性能不足",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                _logger.Information("[Player] 用户选择使用系统播放器播放");
                UseSystemPlayerForCurrentFile();
            }
            else
            {
                _logger.Information("[Player] 用户选择继续当前播放");
            }
        });
    }

    private void OnPlaybackFailed(object? sender, string errorMessage)
    {
        _logger.Error($"[Player] 播放失败: {errorMessage}");

        Dispatcher.Invoke(() =>
        {
            MessageBox.Show($"播放失败: {errorMessage}", "播放错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        });
    }

    private void OnResolutionDownscale(object? sender, ResolutionDownscaleInfo info)
    {
        _logger.Information($"[Player] 分辨率降级通知: {info.Message}");
        _logger.Information($"[Player] 原始分辨率: {info.OriginalWidth}x{info.OriginalHeight}");
        _logger.Information($"[Player] 目标分辨率: {info.TargetWidth}x{info.TargetHeight}");

        Dispatcher.Invoke(() =>
        {
            // 显示降级提示消息框
            string message = $"{info.Message}\n\n" +
                           $"原始分辨率: {info.OriginalWidth}x{info.OriginalHeight}\n" +
                           $"降级后分辨率: {info.TargetWidth}x{info.TargetHeight}\n\n" +
                           $"原因: {info.Reason}";

            MessageBox.Show(
                message,
                "分辨率降级提示",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        });
    }

    private void OnSubtitleDecoded(object? sender, SubtitleData subtitle)
    {
        _logger.Debug($"[Player] 字幕解码: {subtitle.Text} ({subtitle.StartTime:F2}s - {subtitle.EndTime:F2}s)");

        Dispatcher.Invoke(() =>
        {
            // 更新字幕显示
            SubtitleTextBlock.Text = subtitle.Text;
            SubtitleTextBlock.Visibility = Visibility.Visible;

            // 设置字幕隐藏定时器
            _subtitleHideTimer?.Stop();
            _subtitleHideTimer = new System.Windows.Threading.DispatcherTimer();
            _subtitleHideTimer.Interval = TimeSpan.FromSeconds(subtitle.EndTime - subtitle.StartTime);
            _subtitleHideTimer.Tick += (s, e) =>
            {
                SubtitleTextBlock.Visibility = Visibility.Collapsed;
                _subtitleHideTimer?.Stop();
            };
            _subtitleHideTimer.Start();
        });
    }

 
    private void UseSystemPlayerForCurrentFile()
    {
        if (string.IsNullOrEmpty(_currentPlayingFilePath))
        {
            _logger.Warning("[Player] 没有正在播放的文件路径");
            return;
        }

        try
        {
            StopPlaybackInternal();

            // 使用系统默认播放器
            _logger.Information($"[Player] 使用系统播放器播放: {_currentPlayingFilePath}");
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = _currentPlayingFilePath,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);

            // 隐藏视频播放层
          

            // 退出全屏
            ExitFullScreen();
        }
        catch (Exception ex)
        {
            _logger.Error($"[Player] 使用系统播放器失败: {ex.Message}");
            MessageBox.Show($"无法打开系统播放器: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowControls()
    {
        //if (!_isFullScreen)
        //{
        //    TopBar.Visibility = Visibility.Visible;
        //}
         
        TopBar.Visibility = Visibility.Visible; 
          BottomBar.Visibility = Visibility.Visible;

        _hideControlsTimer?.Stop();
        _hideControlsTimer?.Start();
    }

    private void HideControls()
    {
        AudioPopup.IsOpen = false;
        SpeedPopup.IsOpen = false;

        SubtitlePopup.IsOpen = false;
        InfoPopup.IsOpen = false;
        TopBar.Visibility = Visibility.Collapsed;
        BottomBar.Visibility = Visibility.Collapsed;
    }

    private void Window_MouseMove(object sender, MouseEventArgs e) => ShowControls(); 

 

    // 其余事件处理方法（VolumeSlider_ValueChanged, AudioButton_Click, SubtitleButton_Click, SpeedOption_Click,
    // LoadSubtitleButton_Click, InfoButton_Click, KeyDown 等）请从 MainWindow.cs 复制，并删除不相关的部分。
    private void FallbackToSystemPlayer(string filePath)
    {
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = filePath;
            process.StartInfo.UseShellExecute = true;
            process.Start();
            _logger.Debug($"[Player] 使用系统播放器播放: {filePath}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"播放失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            _logger.Debug($"[Player] 系统播放器启动失败: {ex.Message}");
        }
    }

    private async void StopPlaybackInternal()
    {
        try
        {
            _progressTimer?.Stop();
            _progressTimer = null;
            _hideControlsTimer?.Stop();
            _subtitleUpdateTimer?.Stop();

            if (_playerService != null)
            {
                _playerService.FrameUpdated -= OnFrameUpdated;
                await _playerService.StopAsync();
            }
            //var renderer = _videoWindow.Renderer;
            // renderer?.ClearScreen();

            // 退出全屏并恢复 Blazor WebView
            if (_isFullScreen)
            {
                ExitFullScreen();
            }

       

            // 重置播放状态
            _externalSubtitlePath = null;
            _externalSubtitles.Clear();
            _lastPlayPauseContent = null;
            PlayPauseButton.Content = "▶";

            // 卸载ISO挂载点
            if (!string.IsNullOrEmpty(_currentIsoMountPoint))
            {
                DismountIso(_currentPlayingFilePath ?? "");
                _currentIsoMountPoint = null;
            }

            _logger.Debug("[Player] 播放已停止");
        }
        catch (Exception ex)
        {
            _logger.Debug($"[Player] 停止播放出错: {ex.Message}");
        }
    }

    public void StopPlayback()
    {
        // 在后台线程执行停止操作，避免UI卡死
        Task.Run(() =>
        {
            Dispatcher.BeginInvoke(new Action(StopPlaybackInternal));
        });
    }

    public void PausePlayback()
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                _playerService?.Pause();
                UpdatePlayStatus();
                _logger.Debug("[Player] 已暂停");
            }
            catch (Exception ex)
            {
                _logger.Debug($"[Player] 暂停出错: {ex.Message}");
            }
        });
    }

    public void ResumePlayback()
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                _playerService?.Resume();
                UpdatePlayStatus();
                _logger.Debug("[Player] 已恢复播放");
            }
            catch (Exception ex)
            {
                _logger.Debug($"[Player] 恢复出错: {ex.Message}");
            }
        });
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_playerService == null) return;

        if (_playerService.IsPaused)
        {
            ResumePlayback();
        }
        else if (_playerService.IsPlaying)
        {
            PausePlayback();
        }
        ShowControls();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
     
        StopPlayback();
        PlayPauseButton.Content = "▶"; // 重置为播放图标
        _videoWindow.Close();
        this.Close();
    }

    private void FullscreenButton_Click(object sender, RoutedEventArgs e)
    {
        this.WindowState = WindowState.Minimized;
        ToggleFullScreen();
        ShowControls();
        //if (_isFullScreen)
        //{
        //    ExitFullScreen();
        //    FullscreenButton.Content = "全屏";
        //}
        //else
        //{
        //    EnterFullScreen();
        //    FullscreenButton.Content = "退出全屏";
        //}
        //ShowControls();
    }

    private void ProgressSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_playerService == null) return;

        try
        {
            var newPosition = TimeSpan.FromMilliseconds(e.NewValue);
            CurrentTimeText.Text = FormatTime(newPosition);
        }
        catch { }

        if (_playerService == null) return;

       
    }

    private void ProgressSlider_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_playerService == null || !_playerService.IsPlaying)
        {
            _logger.Debug("[Player] Seek ignored - player is null or not playing");
            return;
        }

        try
        {
            var currentValue = ProgressSlider.Value;
            var duration = _playerService.Duration.TotalSeconds;

            if (duration <= 0)
            {
                _logger.Debug("[Player] Seek ignored - invalid duration");
                return;
            }

            var seekPosition = (int)(currentValue / 1000);

            if (seekPosition < 0 || seekPosition > duration)
            {
                _logger.Debug($"[Player] Seek ignored - invalid position: {seekPosition}");
                return;
            }

            _logger.Debug($"[Player] Seek to {seekPosition} seconds");

            Task.Run(() =>
            {
                try
                {
                    if (_playerService != null && _playerService.IsPlaying)
                    {
                        _playerService.Seek(seekPosition);
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => _logger.Debug($"[Player] Seek exception: {ex.Message}"));
                }
            });
        }
        catch (Exception ex)
        {
            _logger.Debug($"[Player] Seek failed: {ex.Message}");
        }
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_playerService != null)
        {
            _playerService.SetVolume((int)e.NewValue);
            if (VolumeLabel == null) return;
            VolumeLabel.Text = ((int)e.NewValue).ToString();
        }
    }

    private void VideoOverlay_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        ShowControls();
    }

    private void ShowControls2()
    {
        //if (VideoOverlay.Visibility == Visibility.Visible)
        {
            // 全屏模式下只显示底部控制栏，非全屏模式下显示顶部和底部控制栏
            if (!_isFullScreen)
            {
                TopBar.Visibility = Visibility.Visible;
            }
            BottomBar.Visibility = Visibility.Visible;

            // ControlsPopup.IsOpen = true;
            // 强制隐藏所有可能覆盖在视频上的 WPF 控件
            //ControlsPopup.IsOpen = false;
            //AudioPopup.IsOpen = false;
            //SpeedPopup.IsOpen = false;
            //SubtitlePopup.IsOpen = false;
            //InfoPopup.IsOpen = false;
            //TopBar.Visibility = Visibility.Collapsed;
            //BottomBar.Visibility = Visibility.Collapsed;
            //SubtitleTextBlock.Visibility = Visibility.Collapsed;


            _hideControlsTimer?.Stop();
            _hideControlsTimer?.Start();
        }
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            if (_isFullScreen)
            {
                ExitFullScreen();
                FullscreenButton.Content = "全屏";
            }
            e.Handled = true;
            return;
        } 
     

        if (e.Key == System.Windows.Input.Key.Space)
        {
            if (_playerService?.IsPaused == true)
            {
                ResumePlayback();
            }
            else
            {
                PausePlayback();
            }
            ShowControls();
            e.Handled = true;
            return;
        }

        if (e.Key == System.Windows.Input.Key.Left)
        {
            if (_playerService != null)
            {
                var currentPos = (int)_playerService.Position.TotalSeconds;
                var newPos = Math.Max(0, currentPos - 5);
                _playerService.Seek(newPos);
            }
            ShowControls();
            e.Handled = true;
            return;
        }

        if (e.Key == System.Windows.Input.Key.Right)
        {
            if (_playerService != null)
            {
                var currentPos = (int)_playerService.Position.TotalSeconds;
                var maxPos = (int)_playerService.Duration.TotalSeconds;
                var newPos = Math.Min(maxPos, currentPos + 5);
                _playerService.Seek(newPos);
            }
            ShowControls();
            e.Handled = true;
            return;
        }

        if (e.Key == System.Windows.Input.Key.Up)
        {
            var currentVol = int.Parse(VolumeLabel.Text);
            var newVol = Math.Min(100, currentVol + 10);
            VolumeSlider.Value = newVol;
            _playerService?.SetVolume(newVol);
            VolumeLabel.Text = newVol.ToString();
            ShowControls();
            e.Handled = true;
            return;
        }

        if (e.Key == System.Windows.Input.Key.Down)
        {
            var currentVol = int.Parse(VolumeLabel.Text);
            var newVol = Math.Max(0, currentVol - 10);
            VolumeSlider.Value = newVol;
            _playerService?.SetVolume(newVol);
            VolumeLabel.Text = newVol.ToString();
            ShowControls();
            e.Handled = true;
            return;
        }
    }

    private void AudioButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateAudioTrackList();
        AudioPopup.IsOpen = !AudioPopup.IsOpen;
        if (AudioPopup.IsOpen)
        {
            SubtitlePopup.IsOpen = false;
        }
    }

    private void AudioTrackItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is int trackIndex)
        {
            AudioPopup.IsOpen = false;
            _logger.Debug($"[Player] Audio track switch not supported in isolated mode");
        }
    }

    private void SubtitleButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateSubtitleTrackList();
        SubtitlePopup.IsOpen = !SubtitlePopup.IsOpen;
        if (SubtitlePopup.IsOpen)
        {
            AudioPopup.IsOpen = false;
        }
    }

    private void ScreenshotButton_Click(object sender, RoutedEventArgs e)
    {
        _logger.Debug("[Player] Screenshot command sent");
        _playerService?.TakeScreenshot();
    }

    private void SpeedButton_Click(object sender, RoutedEventArgs e)
    {
        SpeedPopup.IsOpen = !SpeedPopup.IsOpen;
    }

    private void SpeedOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && double.TryParse(button.Tag?.ToString(), out double speed))
        {
            _logger.Debug($"[Player] Speed set to {speed}x");
            _playerService?.SetPlaybackSpeed(speed);
            SpeedPopup.IsOpen = false;

            // 更新按钮文字
            SpeedButton.Content = speed == 1.0 ? "⚡ 速度" : $"⚡ {speed}x";
        }
    }

    private void SubtitleDelayButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && double.TryParse(button.Tag?.ToString(), out double delayMs))
        {
            _logger.Debug($"[Player] Subtitle delay set to {delayMs}ms");
            _playerService?.SetSubtitleDelay(delayMs);
            SubtitleDelayText.Text = delayMs == 0 ? "延迟: 0ms" : $"延迟: {delayMs:+#;-#;0}ms";
            SubtitlePopup.IsOpen = false;
        }
    }

    private void AudioTrackButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is int trackIndex)
        {
            _logger.Debug($"[Player] 选择音频轨道: {trackIndex}");
            _playerService?.SetAudioTrack(trackIndex);
            AudioPopup.IsOpen = false;
        }
    }

    private void SubtitleTrackButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is int trackIndex)
        {
            _logger.Debug($"[Player] 选择字幕轨道: {trackIndex}");
            _playerService?.SetSpuTrack(trackIndex);
            SubtitlePopup.IsOpen = false;

            // 如果切换到内部字幕，禁用外部字幕
            if (trackIndex >= 0 && _externalSubtitlePath != null)
            {
                _subtitleUpdateTimer?.Stop();
                _externalSubtitlePath = null;
                _externalSubtitles.Clear();
                UnloadSubtitleButton.Visibility = Visibility.Collapsed;
                CurrentSubtitleText.Text = "";
                _logger.Debug("[Player] 已切换到内部字幕，外部字幕已卸载");
            }

            // 更新字幕显示状态
            if (trackIndex >= 0)
            {
                SubtitleTextBlock.Visibility = Visibility.Visible;
            }
            else
            {
                SubtitleTextBlock.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void SubtitleTrackItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is int trackIndex)
        {
            SubtitlePopup.IsOpen = false;
            _logger.Debug($"[Player] Subtitle track switch not supported in isolated mode");
        }
    }

    private void LoadSubtitleButton_Click(object sender, RoutedEventArgs e)
    {
        // 创建打开文件对话框
        var dialog = new OpenFileDialog
        {
            Title = "选择字幕文件",
            Filter = "字幕文件 (*.srt;*.ass;*.ssa)|*.srt;*.ass;*.ssa|SRT文件 (*.srt)|*.srt|ASS文件 (*.ass)|*.ass|所有文件 (*.*)|*.*",
            FilterIndex = 1
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                _externalSubtitlePath = dialog.FileName;
                _externalSubtitles = SubtitleParser.Parse(_externalSubtitlePath);

                if (_externalSubtitles.Count > 0)
                {
                    _logger.Debug($"[Player] 加载外部字幕成功: {dialog.FileName}, 共 {_externalSubtitles.Count} 条");

                    // 更新UI
                    SubtitlePopup.IsOpen = false;
                    UnloadSubtitleButton.Visibility = Visibility.Visible;
                    CurrentSubtitleText.Text = $"当前字幕: {Path.GetFileName(_externalSubtitlePath)}";

                    // 启用字幕显示
                    SubtitleTextBlock.Visibility = Visibility.Visible;

                    // 启动字幕更新定时器
                    StartSubtitleUpdateTimer();

                    // 如果正在播放，同步到当前播放位置
                    if (_playerService != null && _playerService.IsPlaying)
                    {
                        UpdateExternalSubtitle();
                    }
                }
                else
                {
                    MessageBox.Show("无法解析字幕文件或字幕文件为空", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[Player] 加载字幕文件失败");
                MessageBox.Show($"加载字幕文件失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void UnloadSubtitleButton_Click(object sender, RoutedEventArgs e)
    {
        // 停止字幕更新定时器
        _subtitleUpdateTimer?.Stop();

        // 清除外部字幕
        _externalSubtitlePath = null;
        _externalSubtitles.Clear();

        // 更新UI
        SubtitleTextBlock.Visibility = Visibility.Collapsed;
        UnloadSubtitleButton.Visibility = Visibility.Collapsed;
        CurrentSubtitleText.Text = "";

        _logger.Debug("[Player] Unloaded external subtitle");
    }

    private void StartSubtitleUpdateTimer()
    {
        _subtitleUpdateTimer?.Stop();
        _subtitleUpdateTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _subtitleUpdateTimer.Tick += (s, e) => UpdateExternalSubtitle();
        _subtitleUpdateTimer.Start();
        _logger.Debug("[Player] 字幕更新定时器已启动");
    }

    private void UpdateExternalSubtitle()
    {
        if (_externalSubtitles.Count == 0 || _playerService == null)
            return;

        try
        {
            var currentTime = _playerService.Position;
            var currentSubtitle = _externalSubtitles.FirstOrDefault(s => s.IsActive(currentTime));

            if (currentSubtitle != null)
            {
                Dispatcher.Invoke(() =>
                {
                    SubtitleTextBlock.Text = currentSubtitle.Text;
                    SubtitleTextBlock.Visibility = Visibility.Visible;
                });
            }
            else
            {
                Dispatcher.Invoke(() =>
                {
                    SubtitleTextBlock.Text = "";
                });
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"[Player] 更新字幕失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 加载窗口图标
    /// </summary>
    public void SetDecoderInfo(string decoderName, string decodeMode)
    {
        _currentDecoderName = decoderName;
        _currentDecodeMode = decodeMode;
    }

    private void LoadWindowIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logo.png");
            if (File.Exists(iconPath))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(iconPath);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                Icon = bitmap;
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"[MainWindow] Failed to load window icon: {ex.Message}");
        }
    }

    private async void InfoButton_Click(object sender, RoutedEventArgs e)
    {

        InfoPopup.IsOpen = true;
        ShowControls();

        UpdateMediaInfo();
        await UpdateSystemInfoAsync(); // 异步调用，不卡 UI
    }

    private void CloseInfoButton_Click(object sender, RoutedEventArgs e)
    {
        InfoPopup.IsOpen = false;
    }

    private async Task UpdateSystemInfoAsync()
    {
        try
        {
            CpuInfoText.Text = "进在获取";
            MemoryInfoText.Text = "进在获取";
            GpuInfoText.Text = "进在获取";
            ResolutionText.Text = "进在获取";
            OSInfoText.Text = "进在获取";
            // 使用 ConfigureAwait(false) 避免不必要的上下文切换
            var cpuTask = Task.Run(() => GetCpuInfo());
            var memoryTask = Task.Run(() => GetMemoryInfo());
            var gpuTask = Task.Run(() => GetGpuInfo());
            var osTask = Task.Run(() => GetOSInfo());

            // 等待所有任务
            await Task.WhenAll(cpuTask, memoryTask, gpuTask, osTask)
                      .ConfigureAwait(false);

            // 获取结果（此时在后台线程）
            var cpu = cpuTask.Result;
            var memory = memoryTask.Result;
            var gpu = gpuTask.Result;
            var os = osTask.Result;
            var resolution = $"{System.Windows.SystemParameters.PrimaryScreenWidth:F0} x {System.Windows.SystemParameters.PrimaryScreenHeight}";

            // 使用 Dispatcher 回到 UI 线程更新控件
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                CpuInfoText.Text = cpu;
                MemoryInfoText.Text = memory;
                GpuInfoText.Text = gpu;
                ResolutionText.Text = resolution;
                OSInfoText.Text = os;
            });
        }
        catch (Exception ex)
        {
            _logger.Debug($"[Player] Error getting system info: {ex.Message}");
        }
    }

    private void UpdateMediaInfo()
    {
        FileNameText.Text = _currentMovieTitle;
        VideoResolutionText.Text = _playerService != null && _playerService.VideoWidth > 0 ? $"{_playerService.VideoWidth} x {_playerService.VideoHeight}" : "未知";

        // 显示实际帧率
        double fps = 0;
        if (_playerService is LocalPlayerService localPlayer)
        {
            fps = localPlayer.Fps;
        }
        FpsText.Text = fps > 0 ? $"{fps:F2} fps" : "未知";

        DurationText.Text = _playerService != null && _playerService.Duration.TotalMilliseconds > 0 ? FormatTime(_playerService.Duration) : "未知";
        DecodeModeText.Text = string.IsNullOrEmpty(_currentDecodeMode) ? "Auto" : _currentDecodeMode;
        DecoderNameText.Text = string.IsNullOrEmpty(_currentDecoderName) ? "初始化中..." : _currentDecoderName.ToUpper();
    }

    private string GetCpuInfo()
    {
        try
        {
            using (var searcher = new System.Management.ManagementObjectSearcher("SELECT * FROM Win32_Processor"))
            {
                foreach (var obj in searcher.Get())
                {
                    string name = obj["Name"]?.ToString() ?? "";
                    if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
                    {
                        return $"{name.Trim()}, {Environment.ProcessorCount} 核心";
                    }
                    else if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase))
                    {
                        return $"{name.Trim()}, {Environment.ProcessorCount} 核心";
                    }
                }
            }
            return $"{Environment.ProcessorCount} 核心 CPU";
        }
        catch { }
        return "未知";
    }

    private string GetMemoryInfo()
    {
        try
        {
            ulong installedMemory = GetInstalledMemory();
            double gb = installedMemory / (1024.0 * 1024.0 * 1024.0);
            return $"{gb:F1} GB";
        }
        catch { }
        return "未知";
    }

    private ulong GetInstalledMemory()
    {
        try
        {
            using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
            {
                if (key != null)
                {
                    var value = key.GetValue("ProcessorNameString");
                    if (value != null)
                    {
                        // 尝试从注册表获取内存信息
                        using (var memKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management"))
                        {
                            if (memKey != null)
                            {
                                var physicalMemory = memKey.GetValue("PhysicalMemory");
                                if (physicalMemory is byte[] bytes)
                                {
                                    return BitConverter.ToUInt64(bytes, 0);
                                }
                            }
                        }
                    }
                }
            }
        }
        catch { }
        return 8UL * 1024 * 1024 * 1024; // 默认返回8GB
    }

    private string GetGpuInfo()
    {
        try
        {
            var decoderType = HardwareDetectionService.GetUseGpuInfo();

            return decoderType;
        }
        catch { }
        return "未知";
    }

    private string GetOSInfo()
    {
        try
        {
            var os = Environment.OSVersion;
            if (os.Platform == PlatformID.Win32NT)
            {
                if (os.Version.Major == 10 && os.Version.Build >= 22000)
                {
                    return "Windows 11";
                }
                else if (os.Version.Major == 10)
                {
                    return "Windows 10";
                }
            }
            return $"Windows {os.Version.Major}.{os.Version.Minor}";
        }
        catch { }
        return "未知";
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _logger.Debug("[MainWindow] OnClosing 被调用");

        try
        {
            // 取消所有事件订阅，防止事件污染
            _logger.Debug("[MainWindow] 取消事件订阅...");

            // 取消 FrameUpdated 事件订阅
            if (_playerService != null)
            {
                _playerService.FrameUpdated -= OnFrameUpdated;
                _playerService.PlaybackRequestedByBlazor -= OnPlaybackRequestedByBlazor;
            }
            // 停止播放
            StopPlaybackInternal();

            // 释放播放器服务
            _logger.Debug("[MainWindow] Disposing player service...");
            (_playerService as IDisposable)?.Dispose();
            _logger.Debug("[MainWindow] Player service disposed");
        }
        catch (Exception ex)
        {
            _logger.Error($"[MainWindow] Error during closing: {ex.Message}");
        }

        _hideControlsTimer?.Stop();
        _progressTimer?.Stop();

        _logger.Debug("[MainWindow] Closing completed");
        base.OnClosing(e);
    }


#endregion

    #region 控制进度条显示的计时器
    private bool _isDragging = false;
    private double _dragValue = 0;

    /// <summary>
    /// 鼠标按下（开始拖动）
    /// </summary>
    //private void ProgressSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    //{
    //    _isDragging = true;

    //    // 获取鼠标点击位置对应的值
    //    var slider = (Slider)sender;
    //    Point point = e.GetPosition(slider);
    //    double percent = point.X / slider.ActualWidth;
    //    _dragValue = percent * slider.Maximum;

    //    // 显示时间标签
    //    SeekTimeLabel.Visibility = Visibility.Visible;
    //    UpdateSeekTimeLabel(_dragValue);

    //    // 可选：显示小手光标
    //    slider.Cursor = Cursors.Hand;
    //}

    /// <summary>
    /// 鼠标移动（拖动中）
    /// </summary>
    //private void ProgressSlider_PreviewMouseMove(object sender, MouseEventArgs e)
    //{
    //    if (!_isDragging) return;

    //    var slider = (Slider)sender;
    //    Point point = e.GetPosition(slider);
    //    double percent = Math.Max(0, Math.Min(1, point.X / slider.ActualWidth));
    //    _dragValue = percent * slider.Maximum;

    //    // 实时更新时间显示
    //    UpdateSeekTimeLabel(_dragValue);
    //}

    /// <summary>
    /// 鼠标松开（结束拖动，执行 Seek）
    /// </summary>
    //private void ProgressSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    //{
    //    if (!_isDragging) return;
    //    _isDragging = false;

    //    var slider = (Slider)sender;

    //    // 隐藏时间标签
    //    SeekTimeLabel.Visibility = Visibility.Collapsed;
    //    slider.Cursor = Cursors.Arrow;

    //    // 执行 Seek 跳转
    //    SeekTo(_dragValue);
    //}

    /// <summary>
    /// 滑块值改变时（用于播放进度更新）
    /// </summary>
    //private void ProgressSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    //{
    //    // 如果不是用户拖动，更新滑块值（来自播放器的进度更新）
    //    if (!_isDragging)
    //    {
    //        _dragValue = e.NewValue;
    //        UpdateSeekTimeLabel(_dragValue);
    //    }
    //}

    /// <summary>
    /// 更新时间标签显示
    /// </summary>
    //private void UpdateSeekTimeLabel(double milliseconds)
    //{
    //    TimeSpan time = TimeSpan.FromMilliseconds(milliseconds);
    //    SeekTimeLabel.Text = time.ToString(@"hh\:mm\:ss");
    //}

    ///// <summary>
    ///// 执行 Seek 跳转
    ///// </summary>
    //private void SeekTo(double milliseconds)
    //{ 
    //    // 发送 Seek 命令到解码器
    //    //  _decoder?.Seek((long)milliseconds);

    //    //DebugLogger.WriteLine($"[Seek] 跳转到: {TimeSpan.FromMilliseconds(milliseconds):hh\\:mm\\:ss}");
    //    try
    //    {
    //        ProgressSlider_MouseUp(null,null);
    //        // 更新滑块值
    //       //        ProgressSlider.Value = milliseconds;
    //        var newPosition = TimeSpan.FromMilliseconds(milliseconds);
    //        CurrentTimeText.Text = FormatTime(newPosition);
    //    }
    //    catch { }
    //}

    #endregion

    #region 字幕下载功能

    /// <summary>
    /// 打开字幕下载对话框
    /// </summary>
    private void DownloadSubtitleButton_Click(object sender, RoutedEventArgs e)
    {
        SubtitlePopup.IsOpen = false;

        // 默认填充当前电影名称作为搜索关键词
        if (!string.IsNullOrEmpty(_currentMovieTitle))
        {
            SubtitleSearchBox.Text = _currentMovieTitle;
        }

        // 默认选择中文
        SubtitleLanguageCombo.SelectedIndex = 0;
        SubtitleSearchStatus.Text = string.Empty;
        SubtitleResultsPanel.Children.Clear();

        SubtitleDownloadPopup.IsOpen = true;
        _logger.Debug("[MainWindow] Subtitle download popup opened");
    }

    /// <summary>
    /// 关闭字幕下载对话框
    /// </summary>
    private void CloseSubtitleDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        SubtitleDownloadPopup.IsOpen = false;
        _logger.Debug("[MainWindow] Subtitle download popup closed");
    }

    /// <summary>
    /// 搜索字幕
    /// </summary>
    private async void SearchSubtitleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_subtitleService == null)
        {
            SubtitleSearchStatus.Text = "字幕服务不可用";
            return;
        }

        string query = SubtitleSearchBox.Text.Trim();
        if (string.IsNullOrEmpty(query))
        {
            SubtitleSearchStatus.Text = "请输入搜索关键词";
            return;
        }

        string language = "zh";
        if (SubtitleLanguageCombo.SelectedItem is ComboBoxItem selectedItem)
        {
            language = selectedItem.Tag?.ToString() ?? "zh";
        }

        SubtitleSearchStatus.Text = "正在搜索...";
        SubtitleResultsPanel.Children.Clear();

        try
        {
            _logger.Debug($"[MainWindow] Searching subtitles for: {query}, language: {language}");
            var results = await _subtitleService.SearchSubtitlesAsync(query, language);

            if (results.Count == 0)
            {
                SubtitleSearchStatus.Text = "未找到匹配的字幕";
                return;
            }

            SubtitleSearchStatus.Text = $"找到 {results.Count} 个字幕";

            foreach (var subtitle in results)
            {
                var itemPanel = new StackPanel { Orientation = Orientation.Horizontal };
                itemPanel.Margin = new Thickness(5);

                var infoPanel = new StackPanel { Width = 350 };

                var titleText = new TextBlock
                {
                    Text = subtitle.Title,
                    Foreground = Brushes.White,
                    FontSize = 13,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };

                var detailText = new TextBlock
                {
                    Text = $"语言: {subtitle.Language} | 评分: {subtitle.Rating}",
                    Foreground = Brushes.Gray,
                    FontSize = 11,
                    Margin = new Thickness(0, 2, 0, 0)
                };

                infoPanel.Children.Add(titleText);
                infoPanel.Children.Add(detailText);

                var downloadButton = new Button
                {
                    Content = "下载",
                    Style = (Style)FindResource("ControlButtonStyle"),
                    Width = 70,
                    Tag = subtitle,
                    Background = new SolidColorBrush(Color.FromRgb(233, 69, 96))
                };
                downloadButton.Click += DownloadSubtitleItem_Click;

                itemPanel.Children.Add(infoPanel);
                itemPanel.Children.Add(downloadButton);

                SubtitleResultsPanel.Children.Add(itemPanel);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[MainWindow] Subtitle search failed");
            SubtitleSearchStatus.Text = "搜索失败，请稍后重试";
        }
    }

    /// <summary>
    /// 下载选中的字幕
    /// </summary>
    private async void DownloadSubtitleItem_Click(object sender, RoutedEventArgs e)
    {
        if (_subtitleService == null || sender is not Button button || button.Tag is not SubtitleResult subtitle)
            return;

        button.IsEnabled = false;
        SubtitleSearchStatus.Text = "正在下载...";

        try
        {
            _logger.Debug($"[MainWindow] Downloading subtitle: {subtitle.Title}");
            var subtitleData = await _subtitleService.DownloadSubtitleAsync(subtitle.DownloadUrl);

            if (subtitleData == null || subtitleData.Length == 0)
            {
                SubtitleSearchStatus.Text = "下载失败";
                button.IsEnabled = true;
                return;
            }

            // 保存字幕到视频文件所在目录
            string savedPath = string.Empty;

            // 如果正在播放视频，保存到视频目录
            if (_playerService != null && _playerService.IsPlaying)
            {
                // 尝试获取当前播放的文件路径
                string? videoPath = _playerService.GetCurrentRequestedFilePath();
                if (!string.IsNullOrEmpty(videoPath) && File.Exists(videoPath))
                {
                    string directory = Path.GetDirectoryName(videoPath)!;
                    string videoName = Path.GetFileNameWithoutExtension(videoPath);
                    string subtitleFileName = $"{videoName}{subtitle.Extension}";
                    savedPath = Path.Combine(directory, subtitleFileName);

                    await File.WriteAllBytesAsync(savedPath, subtitleData);
                    _logger.Debug($"[MainWindow] Subtitle saved to: {savedPath}");

                    // 字幕加载功能暂不支持
                    _logger.Debug("[MainWindow] Subtitle downloaded successfully");
                }
            }

            // 如果没有正在播放的视频，保存到默认目录
            if (string.IsNullOrEmpty(savedPath))
            {
                savedPath = await _subtitleService.SaveSubtitleAsync(0, subtitleData, subtitle.Extension);
            }

            SubtitleSearchStatus.Text = $"字幕已保存: {Path.GetFileName(savedPath)}";
            button.Content = "已下载";
            _logger.Debug($"[MainWindow] Subtitle download completed: {savedPath}");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[MainWindow] Subtitle download failed");
            SubtitleSearchStatus.Text = "下载失败";
            button.IsEnabled = true;
        }
    }

    #endregion

    #region BDMV/ISO 蓝光支持

    /// <summary>
    /// 挂载 ISO 文件为虚拟驱动器，返回挂载点路径
    /// </summary>
    private string? MountIsoForBdmv(string isoPath)
    {
        try
        {
            _logger.Debug($"[BDMV] Mounting ISO: {isoPath}");

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
                _logger.Error("[BDMV] Failed to start PowerShell for ISO mount");
                return null;
            }

            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(10000);

            if (!string.IsNullOrEmpty(output) && output.Length >= 2)
            {
                string mountPoint = output + "\\";
                _logger.Debug($"[BDMV] ISO mounted at: {mountPoint}");
                return mountPoint;
            }

            _logger.Error($"[BDMV] Mount failed, output: {output}");
        }
        catch (Exception ex)
        {
            _logger.Error($"[BDMV] Mount ISO error: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// 卸载 ISO 虚拟驱动器
    /// </summary>
    private void DismountIso(string isoPath)
    {
        try
        {
            _logger.Debug($"[BDMV] Dismounting ISO: {isoPath}");

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"Dismount-DiskImage -ImagePath '{isoPath}'\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            process?.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            _logger.Error($"[BDMV] Dismount ISO error: {ex.Message}");
        }
    }

    /// <summary>
    /// 显示 BDMV 标题选择对话框，返回用户选择的文件路径，取消返回 null
    /// </summary>
    private string? ShowBdmvTitleDialog(string bdmvPath, string title)
    {
        var titles = MovieAgent.Infrastructure.Services.LocalPlayerService.GetBdmvTitles(bdmvPath);

        if (titles.Count == 0)
        {
            MessageBox.Show("未找到可播放的蓝光视频文件", "蓝光播放", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        if (titles.Count == 1)
        {
            _logger.Debug($"[BDMV] 只有一个标题，直接播放: {titles[0].FilePath}");
            return titles[0].FilePath;
        }

        // 创建选择对话框
        var dialog = new Window
        {
            Title = $"蓝光标题选择 - {title}",
            Width = 500,
            Height = 450,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var listBox = new ListBox
        {
            Margin = new Thickness(10),
            DisplayMemberPath = "DisplayName",
            ItemsSource = titles
        };
        listBox.SelectedIndex = 0;
        Grid.SetRow(listBox, 0);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(10)
        };
        Grid.SetRow(buttonPanel, 1);

        var playButton = new Button
        {
            Content = "播放选中",
            Width = 100,
            Height = 30,
            Margin = new Thickness(5),
            IsDefault = true
        };

        var cancelButton = new Button
        {
            Content = "取消",
            Width = 100,
            Height = 30,
            Margin = new Thickness(5),
            IsCancel = true
        };

        string? selectedPath = null;

        playButton.Click += (s, e) =>
        {
            if (listBox.SelectedItem is BdmvTitleInfo selected)
            {
                selectedPath = selected.FilePath;
            }
            dialog.DialogResult = true;
            dialog.Close();
        };

        cancelButton.Click += (s, e) =>
        {
            dialog.DialogResult = false;
            dialog.Close();
        };

        buttonPanel.Children.Add(playButton);
        buttonPanel.Children.Add(cancelButton);
        grid.Children.Add(listBox);
        grid.Children.Add(buttonPanel);
        dialog.Content = grid;

        bool? result = dialog.ShowDialog();
        return result == true ? selectedPath : null;
    }

    #endregion
    public async Task StartPlaybackAsync(string filePath, string movieTitle)
    {
        _currentMovieTitle = movieTitle;
        if (_playerService == null) return;

        _videoWindow.Width = 1200;
        _videoWindow.Height = 800;
        _videoWindow.Left = 100;
        _videoWindow.Top = 100;
        _videoWindow.WindowState = WindowState.Normal;
        _videoWindow.Topmost = false;
        SyncControlPosition();

        // 等待 VideoRenderer 设备就绪 
        // 强制同步等待最多 5 秒
        for (int i = 0; i < 50; i++)
        {
            

            if (_videoWindow.GetDevice() != null)
            { 
                var device = _videoWindow.GetDevice();
                if (device != null)
                {
                    _playerService.SetD3d12Device(device);
                    // 用 device 初始化硬件解码器
                    _logger.Debug($"[VideoRendererControl] GetDevice已调用,初始化硬件解码器");
                }
                DebugLogger.WriteLine("设备就绪！"); 
                break;
            }
            System.Threading.Thread.Sleep(100);
            DebugLogger.WriteLine($"等待设备中... 尝试 {i + 1}");
        }
        if (_videoWindow.GetDevice() == null)
            DebugLogger.WriteLine("设备最终仍然为 null，初始化失败");

        // 订阅帧事件（由 ControlWindow 接管渲染）
        _playerService.FrameUpdated += OnFrameUpdated;

        // 开始播放
        await _playerService.PlayAsync(filePath);

        // 更新UI
        UpdatePlaybackInfo();
        StartProgressUpdate();
        UpdateAudioTrackList();
        UpdateSubtitleTrackList();
        _frameCount = 0;
        UpdatePlayStatus();
        ShowControls();
        EnterFullScreen();
    }


    // 注意：关闭窗口时需清理资源
    protected override void OnClosed(EventArgs e)
    {
        _progressTimer?.Stop();
        _hideControlsTimer?.Stop();
        _subtitleUpdateTimer?.Stop();
        if (_playerService != null)
        {
            _playerService.FrameUpdated -= OnFrameUpdated;
            _ = _playerService.StopAsync();
        }
        base.OnClosed(e);
    }
}