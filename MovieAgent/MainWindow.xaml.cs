using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using MovieAgent.Core.Interfaces;
using MovieAgent.Infrastructure.Services;
using MovieAgent.Services;
using System;
using System.IO;
using System.Management;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static MovieAgent.Infrastructure.Services.ProcessIsolatedPlayerService;

namespace MovieAgent;

public partial class MainWindow : Window
    {
        private IPlayerService? _playerService;
        private readonly ILoggerService _logger;
        private ISubtitleService? _subtitleService;
        private int _frameCount = 0;
        private System.Windows.Threading.DispatcherTimer? _hideControlsTimer;
        private bool _isFullScreen = false;
        private WindowState _previousWindowState;
        private WindowStyle _previousWindowStyle;
        private bool _previousTopmost;
        private double _previousWidth;
        private double _previousHeight;
        private double _previousLeft;
        private double _previousTop;
        private string _currentMovieTitle = string.Empty;
        private string _currentAudioTrack = "立体声";
        private string _currentSubtitle = "无";
    private string _currentDecoderName = string.Empty;
    private string _currentDecodeMode = "自动";
    private long AudioPlayPosition = -1;
    private bool Syncing=false;//播放同步中，避免重复调用同步逻辑

    public MainWindow()
    {
        var services = ((App)Application.Current).Services;
        _logger = services.GetRequiredService<ILoggerService>();
        
        _logger.Debug("[MainWindow] 开始构造...");
        InitializeComponent();
        _logger.Debug("[MainWindow] InitializeComponent 完成");
        
        // 加载窗口图标
        LoadWindowIcon();
        
        try
        {
            _logger.Debug("[MainWindow] 获取 Services 完成");
            
            BlazorWebView.HostPage = "wwwroot/index.html";
            BlazorWebView.Services = services;
            BlazorWebView.RootComponents.Add(
                new RootComponent { Selector = "#app", ComponentType = typeof(Components.Routes) });
            _logger.Debug("[MainWindow] BlazorWebView 配置完成");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[MainWindow] BlazorWebView 配置失败");
        }

        try
        {
            _playerService = services.GetRequiredService<IPlayerService>();
            _logger.Debug($"[MainWindow] PlayerService created, IsPlaying: {_playerService.IsPlaying}");

            // 订阅PlaybackRequestedByBlazor事件，当Blazor请求播放时显示视频overlay
            _playerService.PlaybackRequestedByBlazor += OnPlaybackRequestedByBlazor;
            _logger.Debug("[MainWindow] 已订阅 PlaybackRequestedByBlazor 事件");

            // 订阅性能警告事件
            if (_playerService is ProcessIsolatedPlayerService processPlayer)
            {
                processPlayer.PerformanceWarning += OnPerformanceWarning;
                processPlayer.AudioTracksReceived += OnAudioTracksReceived;
                processPlayer.SubtitleTracksReceived += OnSubtitleTracksReceived;
                _logger.Debug("[MainWindow] 已订阅 PerformanceWarning, AudioTracksReceived, SubtitleTracksReceived 事件");
            }

            // 获取字幕服务
            _subtitleService = services.GetService<ISubtitleService>();
            _logger.Debug($"[MainWindow] SubtitleService acquired: {_subtitleService != null}");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[MainWindow] PlayerService 获取失败");
        }

        _hideControlsTimer = new System.Windows.Threading.DispatcherTimer();
        _hideControlsTimer.Interval = TimeSpan.FromSeconds(3);
        _hideControlsTimer.Tick += (s, e) =>
        {
            if (VideoOverlay.Visibility == Visibility.Visible)
            {
                TopBar.Visibility = Visibility.Collapsed;
                BottomBar.Visibility = Visibility.Collapsed;
            }
        };
      
        _logger.Debug("[MainWindow] 构造函数完成");
    }

    public void PlayMovie(string filePath)
        {
            Dispatcher.Invoke(async () =>
            {
                try
                {
                      StopPlaybackInternal();

                    if (!File.Exists(filePath))
                    {
                        MessageBox.Show($"文件不存在: {filePath}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    // 保存当前播放文件路径，用于性能警告时切换到系统播放器
                    _currentPlayingFilePath = filePath;

                    // 提取电影标题（从文件路径中获取）
                    _currentMovieTitle = Path.GetFileNameWithoutExtension(filePath);

                    if (_playerService != null)
                    {
                        _logger.Debug("[Player] 订阅 FrameUpdated 事件");
                        _playerService.FrameUpdated += OnFrameUpdated;
                        _logger.Debug("[Player] FrameUpdated 事件已订阅");
                        _logger.Debug($"[Player] ===== 开始播放流程 ===== ");
                        _logger.Debug($"[Player] 文件路径: {filePath}");
                        _logger.Debug($"[Player] 电影标题: {_currentMovieTitle}");

                        await _playerService.PlayAsync(filePath);
                        _logger.Debug($"[Player] PlayAsync 调用完成");

                        // 显示视频播放层，隐藏 Blazor WebView
                        _logger.Debug($"[Player] 切换到视频播放层");
                        BlazorWebView.Visibility = Visibility.Collapsed;
                        VideoOverlay.Visibility = Visibility.Visible;

                        // 切换到全屏
                        _logger.Debug($"[Player] 进入全屏模式");
                        EnterFullScreen();

                        // 更新信息显示
                        _logger.Debug($"[Player] 更新播放信息");
                        UpdatePlaybackInfo();

                        // 启动进度更新定时器
                        _logger.Debug($"[Player] 启动进度更新定时器");
                        StartProgressUpdate();

                        _frameCount = 0;
                        _logger.Debug($"[Player] ===== 播放流程初始化完成 ===== ");
                        _logger.Debug($"[Player] 播放层可见性: {VideoOverlay.Visibility}");
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

    private void UpdatePlaybackInfo()
    {
        if (_playerService == null) return;

        // 更新电影标题
        MovieTitleText.Text = _currentMovieTitle;
        
        // 更新播放状态
        PlayStatusText.Text = _playerService.IsPaused ? "⏸ 已暂停" : "▶ 正在播放";
        
        // 更新解码器信息（只有在收到Info消息后才更新）
        var processPlayer = _playerService as ProcessIsolatedPlayerService;
        if (processPlayer != null)
        {
            _currentDecoderName = processPlayer.CurrentDecoderName;
            _currentDecodeMode = processPlayer.CurrentDecodeMode;
        }
        
        // 更新视频信息
        string videoText = _playerService.VideoWidth > 0 && _playerService.VideoHeight > 0 
            ? $"视频: {_playerService.VideoWidth}x{_playerService.VideoHeight}" 
            : "视频: 加载中...";
        VideoInfo.Text = videoText;
        
        // 更新解码方式信息
        string decodeModeText = !string.IsNullOrEmpty(_currentDecoderName) 
            ? $"解码: {_currentDecodeMode} ({_currentDecoderName})" 
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
            DecodeModeInfo.Text = $"{modeDisplay} - {_currentDecoderName}";
        }
        else
        {
            DecodeModeInfo.Text = "初始化中...";
        }
        
        _logger.Debug($"[Player] 更新播放信息 - 标题: {_currentMovieTitle}, 状态: {PlayStatusText.Text}, 解码方式: {_currentDecodeMode}, 解码器: {_currentDecoderName}");
    }

    public void UpdatePlayStatus()
    {
        if (_playerService == null) return;
        
        PlayStatusText.Text = _playerService.IsPaused ? "⏸ 已暂停" : "▶ 正在播放";
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
        // 进程隔离模式下通过IPC获取音频轨道信息
        if (_playerService is ProcessIsolatedPlayerService processPlayer)
        {
            _ = processPlayer.SendCommandAsync(new DecoderCommand { Command = "GetAudioTracks" });
        }
    }

    private void UpdateSubtitleTrackList()
    {
        // 进程隔离模式下通过IPC获取字幕轨道信息
        if (_playerService is ProcessIsolatedPlayerService processPlayer)
        {
            _ = processPlayer.SendCommandAsync(new DecoderCommand { Command = "GetSubtitleTracks" });
        }
    }

    private void OnAudioTracksReceived(object? sender, AudioTracksMessage message)
    {
        Dispatcher.Invoke(() =>
        {
            AudioTrackListPanel.Children.Clear();
            
            if (message.Tracks != null && message.Tracks.Count > 0)
            {
                NoAudioTracksText.Visibility = Visibility.Collapsed;
                
                foreach (var track in message.Tracks)
                {
                    Button button = new Button();
                    button.Content = track.Description ?? $"轨道 {track.Index}";
                    button.Tag = track.Index;
                    button.Click += AudioTrackButton_Click;
                    button.Style = (Style)FindResource("ListButtonStyle");
                    
                    if (track.Index == message.CurrentTrack)
                    {
                        button.Background = new SolidColorBrush(Color.FromRgb(233, 69, 96));
                    }
                    
                    AudioTrackListPanel.Children.Add(button);
                }
            }
            else
            {
                NoAudioTracksText.Visibility = Visibility.Visible;
            }
        });
    }

    private void OnSubtitleTracksReceived(object? sender, SubtitleTracksMessage message)
    {
        Dispatcher.Invoke(() =>
        {
            SubtitleTrackListPanel.Children.Clear();
            
            // 添加"无字幕"选项
            Button noneButton = new Button();
            noneButton.Content = "无字幕";
            noneButton.Tag = -1;
            noneButton.Click += SubtitleTrackButton_Click;
            noneButton.Style = (Style)FindResource("ListButtonStyle");
            
            if (message.CurrentTrack < 0)
            {
                noneButton.Background = new SolidColorBrush(Color.FromRgb(233, 69, 96));
            }
            
            SubtitleTrackListPanel.Children.Add(noneButton);
            
            if (message.Tracks != null && message.Tracks.Count > 0)
            {
                NoSubtitleTracksText.Visibility = Visibility.Collapsed;
                
                foreach (var track in message.Tracks)
                {
                    Button button = new Button();
                    button.Content = track.Description ?? $"字幕 {track.Index}";
                    button.Tag = track.Index;
                    button.Click += SubtitleTrackButton_Click;
                    button.Style = (Style)FindResource("ListButtonStyle");
                    
                    if (track.Index == message.CurrentTrack)
                    {
                        button.Background = new SolidColorBrush(Color.FromRgb(233, 69, 96));
                    }
                    
                    SubtitleTrackListPanel.Children.Add(button);
                }
            }
            else
            {
                NoSubtitleTracksText.Visibility = Visibility.Collapsed;
            }
        });
    }

    private void AudioTrackButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is int trackIndex)
        {
            _logger.Debug($"[Player] 选择音频轨道: {trackIndex}");
            if (_playerService is ProcessIsolatedPlayerService processPlayer)
            {
                _ = processPlayer.SendCommandAsync(new DecoderCommand { Command = "SetAudioTrack", AudioTrack = trackIndex });
            }
            AudioPopup.IsOpen = false;
        }
    }

    private void SubtitleTrackButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is int trackIndex)
        {
            _logger.Debug($"[Player] 选择字幕轨道: {trackIndex}");
            if (_playerService is ProcessIsolatedPlayerService processPlayer)
            {
                _ = processPlayer.SendCommandAsync(new DecoderCommand { Command = "SetSubtitleTrack", SubtitleTrack = trackIndex });
            }
            SubtitlePopup.IsOpen = false;
            
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

    private System.Windows.Threading.DispatcherTimer? _progressTimer;

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
                    ProgressSlider.Maximum = duration;
                    ProgressSlider.Value = position;
                }
                CurrentTimeText.Text = FormatTime(_playerService.Position);
                TotalTimeText.Text = FormatTime(_playerService.Duration);
            //_logger.Information("[Player] 更新当前播放进度: {0} / {1}", position, duration);

                PlayPauseButton.Content = _playerService.IsPaused ? "▶" : "⏸";
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
            _hideControlsTimer?.Start();

            _logger.Debug("[Player] 进入全屏模式 - 顶部面板已隐藏");
        }

    private void ExitFullScreen()
    {
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

    private void OnPerformanceWarning(object? sender, DecodePerformanceWarningMessage warning)
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
                          // $"解码性能不足（平均解码时间 {warning.AverageDecodeTimeMs:F1}ms）。\n\n" +
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
                // 重置警告状态，允许继续播放
                if (_playerService is ProcessIsolatedPlayerService processPlayer)
                {
                    // 尝试恢复播放
                    _logger.Debug("[Player] 尝试恢复播放...");
                }
            }
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
            VideoOverlay.Visibility = Visibility.Collapsed;
            BlazorWebView.Visibility = Visibility.Visible;

            // 退出全屏
            ExitFullScreen();
        }
        catch (Exception ex)
        {
            _logger.Error($"[Player] 使用系统播放器失败: {ex.Message}");
            MessageBox.Show($"无法打开系统播放器: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnFrameUpdated(object? sender, byte[] frameData)
    {
        _frameCount++;
        if (_frameCount % 30 == 0)
        {
            _logger.Debug($"[Player] 第 {_frameCount} 帧已渲染, 数据大小: {frameData?.Length ?? 0} bytes");
        }

        try
        {
            if (_playerService == null)
            {
                _logger.Debug($"[Player] 帧更新跳过: _playerService 为 null");
                return;
            }
            
            if (VideoRendererControl == null)
            {
                _logger.Debug($"[Player] 帧更新跳过: VideoRendererControl 为 null");
                return;
            }

            var width = _playerService.VideoWidth;
            var height = _playerService.VideoHeight;

            if (width <= 0 || height <= 0)
            {
                _logger.Debug($"[Player] 帧更新跳过: 无效尺寸 {width}x{height}");
                return;
            }
            
            if (frameData == null || frameData.Length == 0)
            {
                _logger.Debug($"[Player] 帧更新跳过: 帧数据为空");
                return;
            }
            VideoRendererControl.UpdateFrame(frameData, width, height);
            return;

            // 保存第10帧
            if (_frameCount == 10)
            {
                string path = Path.Combine(AppContext.BaseDirectory, "test_frame.bmp");
                 SaveAsBmp(frameData, width, height, path);
                _logger.Debug($"[VideoRenderer] SaveAsBmp Saved 保存第{_frameCount}帧 to {path}");
            }
            //写同步逻辑，声音与画面保持一致
            // 假设这是从队列中取出的一帧视频数据
            //VideoFrame currentFrame = GetNextVideoFrame();
            if (!Syncing)
            {
                AudioPlayPosition = _playerService.AudioPlayPosition;
                Syncing = true;
                _logger.Debug($"[播放]存缓存区 AudioPlayPosition {AudioPlayPosition}ms ，Syncing={Syncing}");

            }
            if (AudioPlayPosition < 0) return;
            // 1. 获取这一帧应该被显示的时间戳 (PTS)
            double videoPtsMs = _playerService.VideoTimestamp.TotalMilliseconds;
             double audioPtsMs = _playerService.AudioTimestamp.TotalMilliseconds;

            // 3. 计算差值: 正数 => 视频快了；负数 => 视频慢了
            double diff = videoPtsMs - AudioPlayPosition;
            _logger.Debug($"[播放]实时取 AudioPlayPosition: {_playerService.AudioPlayPosition} ms videoPtsMs={videoPtsMs} ms，audioPtsMs={audioPtsMs} ms");

            // 4. 同步策略
            const int MAX_EARLY_MS = 30;     // 视频最多领先30毫秒，视为正常
            const int MAX_LATE_MS = 50;      // 视频最多落后50毫秒，视为正常
            const int MAX_WAIT_MS = 300;     // 视频如果领先太多，最多等待300毫秒

            if (diff > MAX_EARLY_MS)
            {
                // 场景：视频跑得比声音快 (diff为正，比如 100ms)
                // 处理：需要让视频等一等声音
                int waitTimeMs = (int)Math.Min(diff, MAX_WAIT_MS);
                _logger.Debug($"视频快了 {diff}ms，等待 {waitTimeMs}ms");
                Thread.Sleep(waitTimeMs);
                 // 等待结束，显示当前帧
                VideoRendererControl.UpdateFrame(frameData, width, height);
                Syncing = false;//同步完成
                _logger.Debug($"[播放]同步完成 实时取 AudioPlayPosition {_playerService.AudioPlayPosition}ms ，Syncing={Syncing}");

            }
            else if (diff < -MAX_LATE_MS)
            {
                // 场景：视频跑得比声音慢 (diff为负，比如 -100ms)
                // 处理：视频已经落后太多，应该放弃这一帧，立即去取并显示下一帧
                _logger.Debug($"视频慢了 {diff}ms，丢弃当前帧"); 
                return; // 丢弃此帧，不进行渲染
            }
            else
            {
                // 场景：同步良好 (diff 在 [-50, 30] 毫秒范围内)
                // 处理：立即显示当前帧
                VideoRendererControl.UpdateFrame(frameData, width, height);
                Syncing = false;//同步完成
                _logger.Debug($"[播放]同步完成 实时取 AudioPlayPosition {_playerService.AudioPlayPosition}ms ，Syncing={Syncing}");

            }

        }
        catch (Exception ex)
        {
            _logger.Debug($"[Player] 帧更新失败: {ex.Message}\n{ex.StackTrace}");
        }
    }
    private void SaveAsBmp(byte[] bgrData, int width, int height, string path)
    {
        using (var fs = new FileStream(path, FileMode.Create))
        using (var bw = new BinaryWriter(fs))
        {
            int stride = width * 3;
            int imageSize = stride * height;

            // BMP 头
            bw.Write((byte)0x42); bw.Write((byte)0x4D); // "BM"
            bw.Write(54 + imageSize);
            bw.Write(0);
            bw.Write(54);

            // 信息头
            bw.Write(40);
            bw.Write(width);
            bw.Write(height);  // 正数表示从上往下
            bw.Write((short)1);
            bw.Write((short)24);
            bw.Write(0);
            bw.Write(imageSize);
            bw.Write(0); bw.Write(0);
            bw.Write(0); bw.Write(0);

            // 翻转数据（BMP 需要从下往上）
            for (int y = height - 1; y >= 0; y--)
            {
                int offset = y * stride;
                bw.Write(bgrData, offset, stride);
            }
        }
    }
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

    private void StopPlaybackInternal()
    {
        try
        {
            _progressTimer?.Stop();
            _progressTimer = null;
            _hideControlsTimer?.Stop();

            if (_playerService != null)
            {
                _playerService.FrameUpdated -= OnFrameUpdated;
                _playerService.StopAsync().ConfigureAwait(false);
            }

            VideoRendererControl?.Clear();

            // 退出全屏并恢复 Blazor WebView
            if (_isFullScreen)
            {
                ExitFullScreen();
            }

            VideoOverlay.Visibility = Visibility.Collapsed;
            BlazorWebView.Visibility = Visibility.Visible;

            _logger.Debug("[Player] 播放已停止");
        }
        catch (Exception ex)
        {
            _logger.Debug($"[Player] 停止播放出错: {ex.Message}");
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
        if (_playerService?.IsPaused == true)
        {
            ResumePlayback();
            PlayPauseButton.Content = "⏸"; // 暂停图标
        }
        else
        {
            PausePlayback();
            PlayPauseButton.Content = "▶"; // 播放图标
        }
        ShowControls();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        PlayPauseButton.Content = "▶"; // 重置为播放图标
    }

    private void FullscreenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isFullScreen)
        {
            ExitFullScreen();
            FullscreenButton.Content = "全屏";
        }
        else
        {
            EnterFullScreen();
            FullscreenButton.Content = "退出全屏";
        }
        ShowControls();
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
            VolumeLabel.Text = ((int)e.NewValue).ToString();
        }
    }

    private void VideoOverlay_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        ShowControls();
    }

    private void ShowControls()
        {
            if (VideoOverlay.Visibility == Visibility.Visible)
            {
                // 全屏模式下只显示底部控制栏，非全屏模式下显示顶部和底部控制栏
                if (!_isFullScreen)
                {
                    TopBar.Visibility = Visibility.Visible;
                }
                BottomBar.Visibility = Visibility.Visible;
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

            if (VideoOverlay.Visibility != Visibility.Visible)
                return;

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
        MessageBox.Show("进程隔离模式下暂不支持加载外部字幕", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void UnloadSubtitleButton_Click(object sender, RoutedEventArgs e)
    {
        SubtitleTextBlock.Visibility = Visibility.Collapsed;
        _logger.Debug("[Player] Unloaded external subtitle");
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

    private void InfoButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateSystemInfo();
            UpdateMediaInfo();
            InfoPopup.IsOpen = true;
            ShowControls();
        }

        private void CloseInfoButton_Click(object sender, RoutedEventArgs e)
        {
            InfoPopup.IsOpen = false;
        }

        private void UpdateSystemInfo()
        {
            try
            {
                // CPU信息
                CpuInfoText.Text = GetCpuInfo();
                
                // 内存信息
                MemoryInfoText.Text = GetMemoryInfo();
                
                // 显卡信息
                GpuInfoText.Text = GetGpuInfo();
                
                // 分辨率信息
                ResolutionText.Text = $"{System.Windows.SystemParameters.PrimaryScreenWidth:F0} x {System.Windows.SystemParameters.PrimaryScreenHeight}";
                
                // 操作系统信息
                OSInfoText.Text = GetOSInfo();
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
            if (_playerService != null && _playerService is ProcessIsolatedPlayerService processPlayer)
            {
               fps = processPlayer.fps;
            }
            FpsText.Text = fps > 0 ? $"{fps:F2} fps" : "未知";
            
            DurationText.Text = _playerService != null && _playerService.Duration.TotalMilliseconds > 0 ? FormatTime(_playerService.Duration) : "未知";
            DecodeModeText.Text = string.IsNullOrEmpty(_currentDecodeMode) ? "Auto" : _currentDecodeMode;
            DecoderNameText.Text = string.IsNullOrEmpty(_currentDecoderName) ? "初始化中..." : _currentDecoderName;
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
                
                // 取消进程隔离播放器的事件订阅
                var processPlayer = _playerService as ProcessIsolatedPlayerService;
                if (processPlayer != null)
                {
                    processPlayer.PerformanceWarning -= OnPerformanceWarning;
                    processPlayer.AudioTracksReceived -= OnAudioTracksReceived;
                    processPlayer.SubtitleTracksReceived -= OnSubtitleTracksReceived;
                }
            }
            _logger.Debug("[MainWindow] 事件订阅已取消");
            
            // 停止播放并等待解码器进程退出
            StopPlaybackInternal();
            
            // 显式等待播放器服务完全清理
            var processPlayerService = _playerService as ProcessIsolatedPlayerService;
            if (processPlayerService != null)
            {
                _logger.Debug("[MainWindow] Disposing ProcessIsolatedPlayerService...");
                processPlayerService.Dispose();
                _logger.Debug("[MainWindow] ProcessIsolatedPlayerService disposed");
            }
            else
            {
                (_playerService as IDisposable)?.Dispose();
            }
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
                    
                    // 如果播放器支持，加载新下载的字幕
                    if (_playerService is FFmpegPlayerService ffmpegPlayer)
                    {
                        ffmpegPlayer.LoadExternalSubtitle(savedPath);
                        _logger.Debug("[MainWindow] Subtitle loaded successfully");
                    }
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
}
