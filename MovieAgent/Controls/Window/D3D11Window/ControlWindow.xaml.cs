using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using MovieAgent.Controls;
using MovieAgent.Controls.Window;
using MovieAgent.Core.Interfaces;
using MovieAgent.FFmpegDecoder;
using static MovieAgent.FFmpegDecoder.FFmpegDecoderEngine;
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
using Vortice.Direct3D11;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;

namespace MovieAgent.D3D11Window;

public partial class ControlWindow : System.Windows.Window
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

    // 去重调度（无锁）：用 Interlocked 操作 _pendingFrame 和 _renderScheduled
    private volatile int _renderScheduled;
    private FrameData? _pendingFrame;

    // 外部字幕
    private string? _externalSubtitlePath;
    private List<SubtitleItem> _externalSubtitles = new();
    private DispatcherTimer? _subtitleUpdateTimer;
    private DispatcherTimer? _subtitleHideTimer;
    // 内部字幕缓存：引擎 SubtitleDecoded 是"解码即推送"（跟随 Demux 速度，远快于播放），
    // 不能直接显示，必须缓存后由定时器按播放位置调度，否则字幕提前闪过、切轨看不出变化
    private List<SubtitleData> _internalSubtitles = new();
    private readonly object _subtitleLock = new(); // _internalSubtitles 的同步锁（readonly，MT1003）
    private int _lastInternalTrack;   // 卸载外部字幕后恢复的内嵌轨道
    private string? _lastSubtitleShown; // 当前已显示文本（去重，避免定时器反复刷TextBlock）
    private SubtitleBitmap? _lastSubtitleBitmap; // 当前已显示位图字幕（去重，避免定时器反复刷Image）
    private int _lastDiagSec = -1;      // 诊断：上次输出外挂字幕诊断的秒数
    private bool _subtitleFirstTickLogged; // 诊断：首次UpdateActiveSubtitle日志

    // 字幕独立透明覆盖窗口（解决D3D HwndHost遮挡Popup的z-order问题）
    private System.Windows.Window? _subtitleOverlay;
    private TextBlock? _subtitleOverlayText;
    private System.Windows.Controls.Image? _subtitleOverlayImage;

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
 

    public ControlWindow()
    {
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


        
        // this.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // ARGB: #01000000

        this.Loaded += ControlWindow_Loaded;
    }

    private void ControlWindow_Loaded(object sender, RoutedEventArgs e)
    {
        //MessageBox.Show("ControlWindow 已加载");
        this.LocationChanged += (s, e) => { if (!_isFullScreen) SyncControlPosition(); SyncSubtitleOverlay(); };
        this.SizeChanged += (s, e) => { if (!_isFullScreen) SyncControlPosition(); SyncSubtitleOverlay(); };
        CreateSubtitleOverlay();
    }

    /// <summary>
    /// 创建独立透明覆盖窗口用于字幕显示（TOPMOST确保覆盖D3D渲染面）
    /// </summary>
    private void CreateSubtitleOverlay()
    {
        _subtitleOverlayText = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(40, 0, 40, 80),
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 24,
            FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei"),
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Padding = new Thickness(15, 10, 15, 10),
            MaxWidth = 800,
            Visibility = Visibility.Collapsed,
            // 文字阴影增强可读性（防止白色字幕在亮色背景上不可见）
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 4,
                ShadowDepth = 1,
                Opacity = 0.8
            }
        };

        // PGS/VOBSUB 图形字幕 Image 控件
        _subtitleOverlayImage = new System.Windows.Controls.Image
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = Visibility.Collapsed,
            Stretch = System.Windows.Media.Stretch.None
        };

        var overlayGrid = new Grid();
        overlayGrid.Children.Add(_subtitleOverlayImage);
        overlayGrid.Children.Add(_subtitleOverlayText);

        _subtitleOverlay = new System.Windows.Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            IsHitTestVisible = false, // 点击穿透到下层窗口
            // 不设置Owner：避免全屏时Owner窗口遮挡TOPMOST子窗口
            Content = overlayGrid
        };

        SyncSubtitleOverlay();
        _subtitleOverlay.Show();
        // 确保TOPMOST标志在Show后仍然生效（全屏窗口可能抢占）
        _subtitleOverlay.Topmost = true;
    }

    private void SyncSubtitleOverlay()
    {
        if (_subtitleOverlay == null) return;
        _subtitleOverlay.Left = this.Left;
        _subtitleOverlay.Top = this.Top;
        _subtitleOverlay.Width = this.ActualWidth;
        _subtitleOverlay.Height = this.ActualHeight;
    }
    private void SyncControlPosition()
    {
        this.Left = this.Left;
        this.Top = this.Top;
        this.Width = this.Width;
        this.Height = this.Height;

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
                this.Title = _currentMovieTitle;
                if (_playerService != null)
                {
                    _logger.Debug("[Player] 订阅 FrameUpdated 事件");
                    _playerService.FrameUpdated += OnFrameUpdated;
                    _logger.Debug("[Player] FrameUpdated 事件已订阅");
                    _logger.Debug($"[Player] ===== 开始播放流程 ===== ");
                    _logger.Debug($"[Player] 文件路径: {actualPlayPath}");
                    _logger.Debug($"[Player] 电影标题: {_currentMovieTitle}");
                    //var renderer = this.Renderer;
                    //var device = renderer?.GetDevice();
                    //if (device != null)
                    //{
                    //    _playerService.SetD3dDevice(device);
                    //    // 用 device 初始化硬件解码器
                    //    _logger.Debug($"[VideoRendererControl] GetDevice已调用,初始化硬件解码器");
                    //}
                    await _playerService.PlayAsync(actualPlayPath);
                    _logger.Debug($"[Player] PlayAsync 调用完成");

                    // 杜比视界：通知渲染器（DV使用与HDR10相同的PQ EOTF，自定义着色器同样适用）
                    if (_playerService.IsDolbyVision)
                    {
                        _logger.Debug("[Player] 杜比视界内容，渲染器将使用自定义HDR着色器管线");
                        VideoView.IsDolbyVision = true;
                    }
                    else
                    {
                        VideoView.IsDolbyVision = false;
                    }

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

                    // 重置字幕状态：新文件的内嵌字幕缓存从零填充，调度定时器随播放启动
                    lock (_subtitleLock) _internalSubtitles.Clear();
                    _lastSubtitleShown = null;
                    _lastSubtitleBitmap = null;
                    _lastInternalTrack = _playerService?.CurrentSpuTrack ?? 0;
                    if (_lastInternalTrack >= 0)
                        StartSubtitleUpdateTimer();

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

    private void EnterFullScreen()
    {

        if (_isFullScreen) return;

        // 保存当前窗口状态
        _previousWindowState = this.WindowState;
        _previousWindowStyle = this.WindowStyle;
        _previousTopmost = this.Topmost;
        _previousWidth = this.Width;
        _previousHeight = this.Height;
        _previousLeft = this.Left;
        _previousTop = this.Top;

        // 正确的全屏模式：覆盖任务栏
        // 步骤：先恢复正常状态，再设置无边框，最后最大化
        if (this.WindowState == WindowState.Normal)
            this.WindowState = WindowState.Maximized;
        this.WindowStyle = WindowStyle.None;
        this.ResizeMode = ResizeMode.NoResize;
        // this.Topmost = true;
        // this.ShowActivated = true;
        this.Left = 0;
        this.Top = 0;
        this.Width = SystemParameters.PrimaryScreenWidth;
        this.Height = SystemParameters.PrimaryScreenHeight;
        _isFullScreen = true;
        FullscreenButton.Content = "退出全屏";
        DebugLogger.WriteLine("进入全屏模式");
        // 全屏后同步字幕覆盖窗口位置（窗口尺寸/位置变化事件在全屏时可能不触发）
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => SyncSubtitleOverlay()));
    }

    private void OnFrameUpdated(object? sender, FrameData frame)
    {
        _frameCount++;
        // if (_frameCount % 30 == 0)
        // _logger.Debug($"[Control] 第 {_frameCount} 帧");

        try
        {
            if (_playerService == null) return;
            if (_playerService.VideoWidth <= 0 || _playerService.VideoHeight <= 0) return;
            if (frame == null) return;

            // 仅保留最新帧，丢弃中间帧以避免堆积
            Interlocked.Exchange(ref _pendingFrame, frame);

            // 用无锁方式调度：仅在空闲时入队一次
            if (Interlocked.CompareExchange(ref _renderScheduled, 1, 0) == 0)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Render, RenderPendingFrame);
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"[Control] 帧更新失败: {ex.Message}");
        }
    }

    private void RenderPendingFrame()
    {
        // 立即取出最新帧并释放调度标志，允许新帧再次调度
        var frameToRender = Interlocked.Exchange(ref _pendingFrame, null);
        Interlocked.Exchange(ref _renderScheduled, 0);

        if (frameToRender == null) return;
        if (this.GetDevice() == null) return;

        try
        {
            // 同步杜比视界Profile 5标志：解码器在首帧才检测ICtCp色彩空间（晚于PlayAsync返回），
            // 每帧同步确保渲染器拿到最新值（ICtCp直通管线 vs HDR10路径）
            VideoView.IsIctcpInput = _playerService.IsIctcpInput;
            VideoView.DoviMetadata = _playerService.DoviMetadata;

            if (frameToRender.IsHardwareFrame)
            {
                this.VideoView.RenderD3D11VATexture(
                    frameToRender.NV12TexturePtr,
                    frameToRender.Width,
                    frameToRender.Height,
                    frameToRender.TextureArrayIndex);
            }
            else
            {
                this.VideoView.UpdateFrame(
                    frameToRender.YPlane, frameToRender.UPlane, frameToRender.VPlane,
                    frameToRender.Width, frameToRender.Height,
                    frameToRender.YStride, frameToRender.UStride, frameToRender.VStride);
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"[Control] 渲染调用失败: {ex.Message}");
        }
        // 不再续接调度：续接会导致同一 VSYNC 周期内 Present 两次，
        // 第二次会被 VSYNC 阻塞，导致下一帧延迟和抖动。
        // 新帧到达时会通过 OnFrameUpdated 重新调度。
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
                var displayText = (i + 1 + "、") + track.Description;
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
                //if ((track.Index == _playerService?.CurrentAudioTrack))
                //{
                //    button.Background = new SolidColorBrush(Color.FromRgb(0, 120, 215));
                //    button.FontWeight = FontWeights.Bold;
                //}
                // 如果是当前选中的音频轨道，高亮显示
                if (track.Index == _playerService?.CurrentAudioTrack)
                {
                    button.Content = $"✓ {displayText}";
                    button.FontWeight = System.Windows.FontWeights.Bold;
                    // 选中状态使用不同颜色
                    button.Background = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0, 120, 215) // 蓝色高亮
                    );
                }

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
                // 清空内部字幕缓存并隐藏字幕
                lock (_subtitleLock) _internalSubtitles.Clear();
                _lastSubtitleShown = null;
                _lastSubtitleBitmap = null;
                _subtitleUpdateTimer?.Stop();
                SubtitleTextBlock.Text = "";
                SubtitleTextBlock.Visibility = Visibility.Collapsed;
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

                    // 清空内部字幕缓存（切换轨道后旧轨道条目必须作废，避免两轨字幕混合）
                    lock (_subtitleLock) _internalSubtitles.Clear();
                    _lastSubtitleShown = null;
                    _lastSubtitleBitmap = null;
                    // 清空覆盖窗口残留字幕
                    if (_subtitleOverlayText != null)
                    {
                        _subtitleOverlayText.Text = "";
                        _subtitleOverlayText.Visibility = Visibility.Collapsed;
                    }
                    if (_subtitleOverlayImage != null)
                    {
                        _subtitleOverlayImage.Source = null;
                        _subtitleOverlayImage.Visibility = Visibility.Collapsed;
                    }

                    if (trackIndex >= 0)
                    {
                        // 切换到内部字幕：禁用并卸载外部字幕
                        _externalSubtitlePath = null;
                        _externalSubtitles.Clear();
                        UnloadSubtitleButton.Visibility = Visibility.Collapsed;
                        CurrentSubtitleText.Text = "";
                        _lastInternalTrack = trackIndex;
                        // 确保调度定时器运行（内嵌字幕同样按播放位置显示）
                        StartSubtitleUpdateTimer();
                        _logger.Debug($"[Player] 已切换到内部字幕轨道 {trackIndex}，外部字幕已卸载");
                    }
                    else
                    {
                        // 关闭字幕
                        _subtitleUpdateTimer?.Stop();
                        SubtitleTextBlock.Text = "";
                        SubtitleTextBlock.Visibility = Visibility.Collapsed;
                    }

                    UpdateSubtitleTrackList();
                };

                if (track.Index == _playerService?.CurrentSpuTrack)
                {
                    button.Content = $"✓ {displayText}";
                    button.FontWeight = System.Windows.FontWeights.Bold;
                    _logger.Debug($"[Player] 当前字幕=✓ {displayText}");

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

    //#region 字幕和轨道切换

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

                var downloadButton = new System.Windows.Controls.Button
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
        if (_subtitleService == null || sender is not System.Windows.Controls.Button button || button.Tag is not SubtitleResult subtitle)
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

    private void ToggleFullScreen()
    {
        if (_isFullScreen)
        {
            ExitFullScreen();
        }
        else
        {
            EnterFullScreen();
        }
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
        // 如果已加载外部字幕，忽略内部字幕解码事件，避免冲突
        if (_externalSubtitlePath != null)
            return;

        // 引擎在 Demux 阶段即推送字幕（带绝对时间戳），此处只缓存，
        // 由 _subtitleUpdateTimer 按播放位置调度显示
        lock (_subtitleLock)
        {
            _internalSubtitles.Add(subtitle);
            // 诊断：每50条输出一次缓存计数，确认内部字幕在填充
            if (_internalSubtitles.Count % 50 == 0)
                _logger.Debug($"[Player] 内部字幕缓存: {_internalSubtitles.Count}条, first=[{_internalSubtitles[0].StartTime:F1}s-{_internalSubtitles[0].EndTime:F1}s]");
            // 诊断：前5条字幕详细日志
            else if (_internalSubtitles.Count <= 5)
                _logger.Debug($"[Player] 字幕解码 #{_internalSubtitles.Count}: [{subtitle.StartTime:F1}s-{subtitle.EndTime:F1}s] {(subtitle.Bitmap != null ? $"[PGS {subtitle.Bitmap.Width}x{subtitle.Bitmap.Height}]" : $"'{subtitle.Text?.Substring(0, Math.Min(subtitle.Text?.Length ?? 0, 30))}'")}");
        }
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

    private void ExitFullScreen()
    {
        if (!_isFullScreen) return;    

        this.WindowStyle = _previousWindowStyle;
        this.ResizeMode = ResizeMode.CanResize;
        this.Topmost = _previousTopmost;
        this.ShowActivated = true;

        // 退出全屏后，窗口最大化到工作区（除任务栏外的整个屏幕区域）
        Rect area = SystemParameters.WorkArea;
        this.Left = area.Left;
        this.Top = area.Top;
        this.Width = area.Width;
        this.Height = area.Height;
        // this.WindowState = WindowState.Maximized;
        // 恢复窗口样式
        if (this.WindowState == WindowState.Maximized)
            this.WindowState = WindowState.Normal;
        _isFullScreen = false;
        FullscreenButton.Content = "全屏";
        DebugLogger.WriteLine("退出全屏模式");
        // 退出全屏后同步字幕覆盖窗口位置
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => SyncSubtitleOverlay()));


    }
    private void ShowControls()
    { 
        ControlsPopup.IsOpen = true;
        TopBarPopup.IsOpen = true;
        //TopBarPopup.Visibility = Visibility.Visible;
        //TopBar.Visibility = Visibility.Visible;
        //BottomBar.Visibility = Visibility.Visible;
       // ControlsPopup.Visibility = Visibility.Visible;
       // _hideControlsTimer?.Stop();
        //_hideControlsTimer?.Start();


    }

    private void HideControls()
    {
        //_logger.Debug($"[Player] TopBarPopup.IsOpen={TopBarPopup.IsOpen}");

        // 不能关闭 Popup（IsOpen=false），否则鼠标事件无法穿透到下面的 VideoRenderer
        // 保持 Popup 打开，只隐藏内部控件内容

        // AudioPopup.IsOpen = false;
        // SpeedPopup.IsOpen = false;
        // SubtitlePopup.IsOpen = false;
        // InfoPopup.IsOpen = false;
        //TopBar.Visibility = Visibility.Collapsed;
        // BottomBar.Visibility = Visibility.Collapsed;
        _isClosingPopup = true;  // 1. 先锁住

        TopBarPopup.IsOpen = false;
        ControlsPopup.IsOpen = false;
        // ControlsPopup.Visibility = Visibility.Collapsed;

        //TopBarPopup.Visibility = Visibility.Collapsed;

        // 2. 延迟 200 毫秒解锁（等待界面渲染稳定，避免触发 MouseMove）
        DispatcherTimer unlockTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        unlockTimer.Tick += (s, e) =>
        {
            _isClosingPopup = false;
            unlockTimer.Stop();
        };
        unlockTimer.Start();
    }
    private bool _isClosingPopup = false;

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isClosingPopup) return; // 如果是正在关闭，直接忽略这次移动

        _hideControlsTimer?.Stop();
        _hideControlsTimer?.Start();
        ShowControls();
    }


    public ID3D11Device? GetDevice()
    {
        return VideoView.GetDevice();
    }
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

            // 退出全屏并恢复 Blazor WebView
            if (_isFullScreen)
            {
                ExitFullScreen();
            }

            // 重置播放状态
            _externalSubtitlePath = null;
            _externalSubtitles.Clear();
            lock (_subtitleLock) _internalSubtitles.Clear();
            _lastSubtitleShown = null;
            _lastSubtitleBitmap = null;
            _lastPlayPauseContent = null;
            PlayPauseButton.Content = "▶";

            // 卸载ISO挂载点
            if (!string.IsNullOrEmpty(_currentIsoMountPoint))
            {
                DismountIso(_currentPlayingFilePath ?? "");
                _currentIsoMountPoint = null;
            }

            _logger.Debug("[Player] 播放已停止");

            // 关闭壁纸窗口，防止遮挡任务栏
            var wallpaperWindow = Application.Current.Windows.OfType<MovieWallpaperWindow>().FirstOrDefault();
            if (wallpaperWindow != null)
            {
               // wallpaperWindow.Hide();
            }

            // 完成所有清理后关闭窗口
            this.Close();
        }
        catch (Exception ex)
        {
            _logger.Debug($"[Player] 停止播放出错: {ex.Message}");
            try
            {
                this.Close();
            }
            catch { }
        }
    }

    public void StopPlayback()
    {
        Dispatcher.Invoke(StopPlaybackInternal);
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
     }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
    }

    private void FullscreenButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleFullScreen();
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

            // Seek 前立即清空待渲染帧和渲染器缓冲，避免渲染旧帧导致卡顿
            Interlocked.Exchange(ref _pendingFrame, null);
            Interlocked.Exchange(ref _renderScheduled, 0);
            this.VideoView.ResetBuffers();

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

            // Seek稳定期后恢复渲染（VideoView.ResetBuffers已设置_isSeeking=true阻止渲染）
            // 使用2秒延迟确保FFmpeg解码器seek稳定期结束（30帧@~24fps ≈ 1.25s）
            Task.Delay(2000).ContinueWith(_ =>
            {
                Dispatcher.Invoke(() => this.VideoView.OnSeekCompleted());
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

 
     

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
             
           ToggleFullScreen();
            
            e.Handled = true;
            return;
        }
        if (e.Key == System.Windows.Input.Key.Enter)
        {
           ShowControls();
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
        if (sender is System.Windows.Forms.Button button && button.Tag is int trackIndex)
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
        if (sender is System.Windows.Forms.Button button && double.TryParse(button.Tag?.ToString(), out double speed))
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
        if (sender is System.Windows.Forms.Button button && double.TryParse(button.Tag?.ToString(), out double delayMs))
        {
            _logger.Debug($"[Player] Subtitle delay set to {delayMs}ms");
            _playerService?.SetSubtitleDelay(delayMs);
            SubtitleDelayText.Text = delayMs == 0 ? "延迟: 0ms" : $"延迟: {delayMs:+#;-#;0}ms";
            SubtitlePopup.IsOpen = false;
        }
    }

    private void AudioTrackButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Forms.Button button && button.Tag is int trackIndex)
        {
            _logger.Debug($"[Player] 选择音频轨道: {trackIndex}");
            _playerService?.SetAudioTrack(trackIndex);
            AudioPopup.IsOpen = false;
        }
    }

    private void SubtitleTrackButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Forms.Button button && button.Tag is int trackIndex)
        {
            _logger.Debug($"[Player] 选择字幕轨道: {trackIndex}");
            _playerService?.SetSpuTrack(trackIndex);
            SubtitlePopup.IsOpen = false;

            // 清空内部字幕缓存（切换轨道后旧轨道条目必须作废）
            lock (_subtitleLock) _internalSubtitles.Clear();
            _lastSubtitleShown = null;
            _lastSubtitleBitmap = null;

            // 如果切换到内部字幕，禁用外部字幕
            if (trackIndex >= 0)
            {
                if (_externalSubtitlePath != null)
                {
                    _externalSubtitlePath = null;
                    _externalSubtitles.Clear();
                    UnloadSubtitleButton.Visibility = Visibility.Collapsed;
                    CurrentSubtitleText.Text = "";
                }
                _lastInternalTrack = trackIndex;
                StartSubtitleUpdateTimer();
                _logger.Debug("[Player] 已切换到内部字幕，外部字幕已卸载");
                SubtitleTextBlock.Visibility = Visibility.Visible;
            }
            else
            {
                _subtitleUpdateTimer?.Stop();
                SubtitleTextBlock.Text = "";
                SubtitleTextBlock.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void SubtitleTrackItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Forms.Button button && button.Tag is int trackIndex)
        {
            SubtitlePopup.IsOpen = false;
            _logger.Debug($"[Player] Subtitle track switch not supported in isolated mode");
        }
    }

    private void LoadSubtitleButton_Click(object sender, RoutedEventArgs e)
    {
        _logger.Debug("[Player] LoadSubtitleButton_Click 被触发");

        // 先关闭Popup，避免它干扰后续的 OpenFileDialog（StaysOpen=False 的Popup
        // 在点击内部按钮时也会关闭，但时序上可能阻塞对话框消息循环）
        SubtitlePopup.IsOpen = false;

        // 延迟一帧再弹出对话框，确保Popup完全关闭、消息循环恢复
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            // 创建打开文件对话框
            var dialog = new OpenFileDialog
            {
                Title = "选择字幕文件",
                Filter = "字幕文件 (*.srt;*.ass;*.ssa)|*.srt;*.ass;*.ssa|SRT文件 (*.srt)|*.srt|ASS文件 (*.ass)|*.ass|所有文件 (*.*)|*.*",
                FilterIndex = 1
            };

            _logger.Debug("[Player] 弹出字幕文件选择对话框");

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    _externalSubtitlePath = dialog.FileName;
                    _externalSubtitles = SubtitleParser.Parse(_externalSubtitlePath);

                    if (_externalSubtitles.Count > 0)
                    {
                        _logger.Debug($"[Player] 加载外部字幕成功: {dialog.FileName}, 共 {_externalSubtitles.Count} 条");

                        // 禁用内部字幕轨道，避免与外部字幕冲突
                        _playerService?.SetSpuTrack(-1);
                        _logger.Debug("[Player] 已禁用内部字幕轨道，使用外部字幕");

                        // 清空内部字幕缓存并重置显示状态（调度器切换到外部字幕源）
                        lock (_subtitleLock) _internalSubtitles.Clear();
                        _lastSubtitleShown = null;
                        _lastSubtitleBitmap = null;
                        SubtitleTextBlock.Text = ""; // 立即清除旧内部字幕文本，防止残留
                        _logger.Debug("[Player] 内部字幕缓存已清除，SubtitleTextBlock已重置");

                        // 更新UI
                        UnloadSubtitleButton.Visibility = Visibility.Visible;
                        CurrentSubtitleText.Text = $"当前字幕: {Path.GetFileName(_externalSubtitlePath)}";

                        // 启用字幕显示
                        SubtitleTextBlock.Visibility = Visibility.Visible;

                        // 启动字幕更新定时器
                        StartSubtitleUpdateTimer();

                        // 如果正在播放，同步到当前播放位置
                        if (_playerService != null && _playerService.IsPlaying)
                        {
                            UpdateActiveSubtitle();
                        }
                    }
                    else
                    {
                        _logger.Debug("[Player] 字幕文件解析结果为空");
                        MessageBox.Show("无法解析字幕文件或字幕文件为空", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "[Player] 加载字幕文件失败");
                    MessageBox.Show($"加载字幕文件失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                _logger.Debug("[Player] 用户取消了字幕文件选择");
            }
        }));
    }

    private void UnloadSubtitleButton_Click(object sender, RoutedEventArgs e)
    {
        // 停止字幕更新定时器
        _subtitleUpdateTimer?.Stop();

        // 清除外部字幕
        _externalSubtitlePath = null;
        _externalSubtitles.Clear();
        _lastSubtitleShown = null;
        _lastSubtitleBitmap = null;

        // 更新UI
        SubtitleTextBlock.Text = "";
        SubtitleTextBlock.Visibility = Visibility.Collapsed;
        UnloadSubtitleButton.Visibility = Visibility.Collapsed;
        CurrentSubtitleText.Text = "";

        // 恢复之前选中的内部字幕轨道（默认0），并重启调度定时器
        // 恢复后引擎会重新推送该轨道字幕，缓存从零开始填充
        _playerService?.SetSpuTrack(_lastInternalTrack);
        StartSubtitleUpdateTimer();

        UpdateSubtitleTrackList();
        _logger.Debug($"[Player] Unloaded external subtitle, restored internal track {_lastInternalTrack}");
    }

    private void StartSubtitleUpdateTimer()
    {
        _subtitleUpdateTimer?.Stop();
        _subtitleUpdateTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _subtitleUpdateTimer.Tick += (s, e) => UpdateActiveSubtitle();
        _subtitleUpdateTimer.Start();
        _subtitleFirstTickLogged = false;
        _lastDiagSec = -1;
        _logger.Debug("[Player] 字幕更新定时器已启动");
    }

    /// <summary>
    /// 统一字幕调度：外部字幕优先，否则按播放位置查找内部字幕缓存。
    /// 内部字幕事件在 Demux 阶段就已全部推入 _internalSubtitles（时间戳为绝对时间），
    /// 因此 Seek 后无需清空缓存，按当前播放时刻查找即可命中正确条目。
    /// </summary>
    private void UpdateActiveSubtitle()
    {
        if (_playerService == null) return;

        try
        {
            double currentSec = _playerService.Position.TotalSeconds;

            // 诊断：首次Tick日志，确认定时器在跑
            if (!_subtitleFirstTickLogged)
            {
                _subtitleFirstTickLogged = true;
                _logger.Debug($"[Player] UpdateActiveSubtitle首次触发: pos={currentSec:F1}s, extPath={_externalSubtitlePath != null}, extCount={_externalSubtitles.Count}, intCount=...");
            }

            string? text = null;
            SubtitleBitmap? bitmap = null;

            if (_externalSubtitlePath != null)
            {
                var item = _externalSubtitles.FirstOrDefault(s => s.IsActive(_playerService.Position));
                text = item?.Text;

                // 诊断：每10秒输出一次匹配状态，确认定时器在跑且能匹配到条目
                if ((int)currentSec % 10 == 0 && (int)currentSec != _lastDiagSec)
                {
                    _lastDiagSec = (int)currentSec;
                    _logger.Debug($"[Player] 外挂字幕诊断: pos={currentSec:F1}s, count={_externalSubtitles.Count}, " +
                        $"first=[{_externalSubtitles.FirstOrDefault()?.StartTime}-{_externalSubtitles.FirstOrDefault()?.EndTime}]" +
                        $"{(item != null ? $", matched='{item.Text.Substring(0, Math.Min(item.Text.Length, 20))}'" : ", no match")}");
                }
            }
            else
            {
                lock (_subtitleLock)
                {
                    foreach (var s in _internalSubtitles)
                    {
                        if (currentSec >= s.StartTime && currentSec <= s.EndTime)
                        {
                            if (s.Bitmap != null)
                            {
                                bitmap = s.Bitmap;
                                text = null;
                            }
                            else
                            {
                                text = s.Text;
                            }
                            break;
                        }
                    }
                }
            }

            // 去重：文本和位图都没变化才跳过（PGS位图字幕text始终为null，需用bitmap判断）
            if (text == _lastSubtitleShown && bitmap == _lastSubtitleBitmap) return;
            // 注意：text和_lastSubtitleShown同时为null时仍需清理覆盖窗口（可能残留旧字幕文本）
            if (text == null && _lastSubtitleShown == null && bitmap == null &&
                string.IsNullOrEmpty(_subtitleOverlayText?.Text ?? "") &&
                _subtitleOverlayImage?.Source == null)
                return;
            _lastSubtitleShown = text;
            _lastSubtitleBitmap = bitmap;

            if (!string.IsNullOrEmpty(text) || bitmap != null)
            {
                // 隐藏图形字幕Image
                if (_subtitleOverlayImage != null)
                    _subtitleOverlayImage.Visibility = Visibility.Collapsed;

                if (bitmap != null)
                {
                    // 渲染PGS图形字幕
                    var bmp = System.Windows.Media.Imaging.BitmapSource.Create(
                        bitmap.Width, bitmap.Height, 96, 96,
                        System.Windows.Media.PixelFormats.Bgra32, null,
                        bitmap.Pixels, bitmap.Width * 4);
                    if (_subtitleOverlayImage != null)
                    {
                        _subtitleOverlayImage.Source = bmp;

                        // 将PGS位图坐标从视频分辨率缩放到叠加窗口分辨率
                        // 视频分辨率（如4K: 3840x2160）与叠加窗口分辨率（如全屏: 2580x1460）不同，
                        // 不缩放会导致位图渲染到屏幕外
                        if (_playerService != null && _playerService.VideoWidth > 0 && _playerService.VideoHeight > 0
                            && _subtitleOverlay != null && _subtitleOverlay.ActualWidth > 0 && _subtitleOverlay.ActualHeight > 0)
                        {
                            double scaleX = _subtitleOverlay.ActualWidth / _playerService.VideoWidth;
                            double scaleY = _subtitleOverlay.ActualHeight / _playerService.VideoHeight;

                            _subtitleOverlayImage.Width = bitmap.Width * scaleX;
                            _subtitleOverlayImage.Height = bitmap.Height * scaleY;
                            _subtitleOverlayImage.Stretch = System.Windows.Media.Stretch.Fill;

                            // 字幕底部距叠加窗口底部的距离
                            double bottomMargin;
                            if (bitmap.Y > 0)
                            {
                                // PGS 提供了有效坐标：视频底部空白 * 缩放 + 偏移
                                bottomMargin = (_playerService.VideoHeight - bitmap.Y - bitmap.Height) * scaleY + 50;
                            }
                            else
                            {
                                // Y=0 表示无显式坐标，使用默认底部位置（与文字字幕一致）
                                bottomMargin = 80;
                            }
                            _subtitleOverlayImage.Margin = new Thickness(0, 0, 0,  150);
                        }
                        else
                        {
                            // 回退：无缩放信息时使用原始坐标
                            _subtitleOverlayImage.Stretch = System.Windows.Media.Stretch.None;
                            _subtitleOverlayImage.Margin = new Thickness(0, 0, 0, bitmap.Y + 80);
                        }

                        _subtitleOverlayImage.Visibility = Visibility.Visible;
                    }
                    // 隐藏文字TextBlock
                    if (_subtitleOverlayText != null)
                        _subtitleOverlayText.Visibility = Visibility.Collapsed;
                }
                else if (_subtitleOverlayText != null)
                {
                    _subtitleOverlayText.Text = text;
                    _subtitleOverlayText.Visibility = Visibility.Visible;
                }
                _logger.Debug($"[Player] 字幕已显示: {(bitmap != null ? $"[PGS {bitmap.Width}x{bitmap.Height} Y={bitmap.Y}]" : $"'{text?.Substring(0, Math.Min(text?.Length ?? 0, 30))}'")}");
            }
            else
            {
                if (_subtitleOverlayText != null)
                {
                    _subtitleOverlayText.Text = "";
                    _subtitleOverlayText.Visibility = Visibility.Collapsed;
                }
                if (_subtitleOverlayImage != null)
                {
                    _subtitleOverlayImage.Source = null;
                    _subtitleOverlayImage.Visibility = Visibility.Collapsed;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"[Player] 更新字幕失败: {ex.Message}");
        }
    }

    // 兼容旧调用：外部字幕同步显示
    private void UpdateExternalSubtitle() => UpdateActiveSubtitle();

    private void InfoButton_Click(object sender, RoutedEventArgs e)
    {
        InfoPopup.IsOpen = !InfoPopup.IsOpen;
        if (InfoPopup.IsOpen)
        {
            LoadSystemInfo();
            LoadMediaInfo();
        }
    }

    private void LoadSystemInfo()
    {
        CpuInfoText.Text = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "未知";
        MemoryInfoText.Text = $"{(GC.GetTotalMemory(false) / (1024 * 1024))} MB";
        GpuInfoText.Text = "Direct3D11";
        ResolutionText.Text = $"{SystemParameters.PrimaryScreenWidth}x{SystemParameters.PrimaryScreenHeight}";
        OSInfoText.Text = Environment.OSVersion.VersionString;
    }

    private void LoadMediaInfo()
    {
        FileNameText.Text = System.IO.Path.GetFileName(_currentPlayingFilePath ?? "");
        VideoResolutionText.Text = $"{_playerService?.VideoWidth}x{_playerService?.VideoHeight}";
        FpsText.Text = "未知";
        DurationText.Text = FormatTime(_playerService?.Duration ?? TimeSpan.Zero);
        DecodeModeText.Text = _playerService?.CurrentD3dModel?.ToString() ?? "未知";
        DecoderNameText.Text = _currentDecoderName;
    }

    private void CloseInfoButton_Click(object sender, RoutedEventArgs e)
    {
        InfoPopup.IsOpen = false;
    }

    #region ISO挂载相关

    private string? MountIsoForBdmv(string isoPath)
    {
        try
        {
            using var managementClass = new ManagementClass("Win32_CDROMDrive");
            var drivesBefore = managementClass.GetInstances().Cast<ManagementObject>()
                .Select(m => m["DeviceID"]?.ToString()).ToList();

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-Command \"Mount-DiskImage -ImagePath '{isoPath}' -PassThru | Get-Volume | Select-Object -ExpandProperty DriveLetter\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            process?.WaitForExit(5000);
            var output = process?.StandardOutput.ReadToEnd().Trim();

            if (!string.IsNullOrEmpty(output))
            {
                return $"{output}:\\";
            }

            using var managementClassAfter = new ManagementClass("Win32_CDROMDrive");
            var drivesAfter = managementClassAfter.GetInstances().Cast<ManagementObject>()
                .Select(m => m["DeviceID"]?.ToString()).ToList();

            var newDrive = drivesAfter.Except(drivesBefore).FirstOrDefault();
            if (!string.IsNullOrEmpty(newDrive))
            {
                return newDrive;
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"[Player] ISO挂载失败: {ex.Message}");
        }

        return null;
    }

    private void DismountIso(string isoPath)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-Command \"Dismount-DiskImage -ImagePath '{isoPath}'\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            process?.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            _logger.Debug($"[Player] ISO卸载失败: {ex.Message}");
        }
    }

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
        var dialog = new System.Windows.Window
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

        var playButton = new System.Windows.Controls.Button
        {
            Content = "播放选中",
            Width = 100,
            Height = 30,
            Margin = new Thickness(5),
            IsDefault = true
        };

        var cancelButton = new System.Windows.Controls.Button
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

        //_videoWindow.Width = 1200;
        //_videoWindow.Height = 800;
        //_videoWindow.Left = 100;
        //_videoWindow.Top = 100;
        // _videoWindow.WindowState = WindowState.Normal;
        this.Topmost = false;
        SyncControlPosition();

        // 等待 VideoRenderer 设备就绪 
        // 强制同步等待最多 5 秒
        for (int i = 0; i < 50; i++)
        {


            if (this.GetDevice() != null)
            {
                var device = this.GetDevice();
                if (device != null)
                {
                    _playerService.SetD3d11Device(device);
                    // 用 device 初始化硬件解码器
                    _logger.Debug($"[VideoRendererControl] GetDevice已调用,初始化硬件解码器");
                }
                DebugLogger.WriteLine("设备就绪！");
                break;
            }
            System.Threading.Thread.Sleep(100);
            DebugLogger.WriteLine($"等待设备中... 尝试 {i + 1}");
        }
        if (this.GetDevice() == null)
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

        // 重置字幕状态：新文件的内嵌字幕缓存从零填充，调度定时器随播放启动
        lock (_subtitleLock) _internalSubtitles.Clear();
        _lastSubtitleShown = null;
        _lastSubtitleBitmap = null;
        _lastInternalTrack = _playerService?.CurrentSpuTrack ?? 0;
        if (_lastInternalTrack >= 0)
            StartSubtitleUpdateTimer();

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
        // 关闭字幕覆盖窗口
        if (_subtitleOverlay != null)
        {
            _subtitleOverlay.Close();
            _subtitleOverlay = null;
        }
         base.OnClosed(e);
    }

    private void Min_Click(object sender, RoutedEventArgs e)
    {
        //最小化
        this.WindowState=WindowState.Minimized;
    }

 

 

    private void CloseWindow_Click(object sender, RoutedEventArgs e)
    {
        //关闭
        this.Close();
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        //还原
        this.WindowState = WindowState.Normal;

    }

   
}
