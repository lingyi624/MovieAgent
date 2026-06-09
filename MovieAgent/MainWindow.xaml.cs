using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using MovieAgent.Core.Interfaces;
using MovieAgent.Infrastructure.Services;

namespace MovieAgent;

public partial class MainWindow : Window
{
    private FFmpegPlayerService? _ffmpegPlayer;
    private readonly ILoggerService _logger;
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
            _ffmpegPlayer = new FFmpegPlayerService(_logger);
            _logger.Debug($"[MainWindow] FFmpegPlayer created, IsAvailable: {_ffmpegPlayer.IsAvailable}");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[MainWindow] FFmpegPlayer 创建失败");
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

                if (_ffmpegPlayer != null && _ffmpegPlayer.IsAvailable)
                {
                    _ffmpegPlayer.FrameUpdated += OnFrameUpdated;
                    await _ffmpegPlayer.PlayAsync(filePath);

                    // 显示视频播放层，隐藏 Blazor WebView
                    BlazorWebView.Visibility = Visibility.Collapsed;
                    VideoOverlay.Visibility = Visibility.Visible;

                    // 切换到全屏
                    EnterFullScreen();

                    // 更新信息显示
                    UpdateAudioInfo();

                    // 启动进度更新定时器
                    StartProgressUpdate();

                    _frameCount = 0;
                    _logger.Debug($"[Player] FFmpeg 播放开始: {filePath}");
                    _logger.Debug($"[Player] 视频尺寸: {_ffmpegPlayer.VideoWidth}x{_ffmpegPlayer.VideoHeight}");
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

    private void UpdateAudioInfo()
    {
        if (_ffmpegPlayer == null) return;

        // 更新顶部音频信息
        var currentAudio = _ffmpegPlayer.CurrentAudioTrack >= 0 && _ffmpegPlayer.CurrentAudioTrack < _ffmpegPlayer.AudioStreams.Count
            ? _ffmpegPlayer.AudioStreams[_ffmpegPlayer.CurrentAudioTrack]
            : null;
        
        string audioText = currentAudio != null
            ? $"音频: {currentAudio.FormatType} | {currentAudio.Channels}声道 | {currentAudio.SampleRate}Hz"
            : $"音频: {_ffmpegPlayer.AudioFormat} | 声道: {_ffmpegPlayer.AudioChannels}ch";
        AudioInfo.Text = audioText;
        string videoText = $"视频: {_ffmpegPlayer.VideoWidth}x{_ffmpegPlayer.VideoHeight} | {_ffmpegPlayer.Fps:0.0} FPS";
        VideoInfo.Text = videoText;
        
        // 更新音频流列表
        UpdateAudioTrackList();
        
        // 更新字幕列表
        UpdateSubtitleTrackList();
        
        _logger.Debug($"[Player] 音频信息: {audioText}, 视频信息: {videoText}");
    }

    private void UpdateAudioTrackList()
    {
        if (_ffmpegPlayer == null) return;
        
        AudioTrackListPanel.Children.Clear();
        
        var audioStreams = _ffmpegPlayer.AudioStreams;
        if (audioStreams.Count > 0)
        {
            for (int i = 0; i < audioStreams.Count; i++)
            {
                var audio = audioStreams[i];
                var button = new Button
                {
                    Content = audio.DisplayName,
                    Tag = i,
                    Style = (Style)FindResource(i == _ffmpegPlayer.CurrentAudioTrack ? "SelectedListButtonStyle" : "ListButtonStyle")
                };
                button.Click += AudioTrackItem_Click;
                AudioTrackListPanel.Children.Add(button);
            }
            NoAudioTracksText.Visibility = Visibility.Collapsed;
        }
        else
        {
            NoAudioTracksText.Visibility = Visibility.Visible;
        }
    }

    private void UpdateSubtitleTrackList()
    {
        if (_ffmpegPlayer == null) return;
        
        SubtitleTrackListPanel.Children.Clear();
        
        var subtitleStreams = _ffmpegPlayer.SubtitleStreams;
        if (subtitleStreams.Count > 0)
        {
            for (int i = 0; i < subtitleStreams.Count; i++)
            {
                var subtitle = subtitleStreams[i];
                var button = new Button
                {
                    Content = subtitle.DisplayName,
                    Tag = i,
                    Style = (Style)FindResource(i == _ffmpegPlayer.CurrentSpuTrack ? "SelectedListButtonStyle" : "ListButtonStyle")
                };
                button.Click += SubtitleTrackItem_Click;
                SubtitleTrackListPanel.Children.Add(button);
            }
            NoSubtitleTracksText.Visibility = Visibility.Collapsed;
        }
        else
        {
            NoSubtitleTracksText.Visibility = Visibility.Visible;
        }
        
        // 更新外部字幕状态
        if (!string.IsNullOrEmpty(_ffmpegPlayer.ExternalSubtitlePath))
        {
            var fileName = Path.GetFileName(_ffmpegPlayer.ExternalSubtitlePath);
            CurrentSubtitleText.Text = $"已加载: {fileName}";
            CurrentSubtitleText.Visibility = Visibility.Visible;
            UnloadSubtitleButton.Visibility = Visibility.Visible;
        }
        else
        {
            CurrentSubtitleText.Visibility = Visibility.Collapsed;
            UnloadSubtitleButton.Visibility = Visibility.Collapsed;
        }
    }

    private System.Windows.Threading.DispatcherTimer? _progressTimer;

    private void StartProgressUpdate()
    {
        _progressTimer?.Stop();
        _progressTimer = new System.Windows.Threading.DispatcherTimer();
        _progressTimer.Interval = TimeSpan.FromMilliseconds(200);
        _progressTimer.Tick += (s, e) => UpdateProgress();
        _progressTimer.Start();
    }

    private void UpdateProgress()
    {
        if (_ffmpegPlayer == null || !_ffmpegPlayer.IsPlaying) return;

        try
        {
            var position = _ffmpegPlayer.Position.TotalMilliseconds;
            var duration = _ffmpegPlayer.Duration.TotalMilliseconds;

            if (duration > 0)
            {
                if (!ProgressSlider.IsMouseCaptureWithin)
                {
                    ProgressSlider.Maximum = duration;
                    ProgressSlider.Value = position;
                }
                CurrentTimeText.Text = FormatTime(_ffmpegPlayer.Position);
                TotalTimeText.Text = FormatTime(_ffmpegPlayer.Duration);
                
                PlayPauseButton.Content = _ffmpegPlayer.IsPaused ? "▶" : "⏸";
            }
            
            UpdateSubtitle();
        }
        catch { }
    }

    private void UpdateSubtitle()
    {
        if (_ffmpegPlayer == null) return;

        try
        {
            var subtitle = _ffmpegPlayer.GetCurrentSubtitle(_ffmpegPlayer.Position);
            
            if (!string.IsNullOrEmpty(subtitle))
            {
                SubtitleTextBlock.Text = subtitle;
                SubtitleTextBlock.Visibility = Visibility.Visible;
            }
            else
            {
                SubtitleTextBlock.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"[Player] Update subtitle error: {ex.Message}");
        }
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

        // 显示控制栏
        TopBar.Visibility = Visibility.Visible;
        BottomBar.Visibility = Visibility.Visible;
        _hideControlsTimer?.Start();

        _logger.Debug("[Player] 进入全屏模式");
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

    private void OnFrameUpdated(object? sender, byte[] frameData)
    {
        _frameCount++;
        if (_frameCount % 120 == 0)
        {
            _logger.Debug($"[Player] 已渲染 {_frameCount} 帧, 数据大小: {frameData?.Length ?? 0} bytes");
        }

        try
        {
            if (_ffmpegPlayer == null || VideoRendererControl == null)
                return;

            var width = _ffmpegPlayer.VideoWidth;
            var height = _ffmpegPlayer.VideoHeight;

            if (width <= 0 || height <= 0 || frameData == null || frameData.Length == 0)
                return;

            VideoRendererControl.UpdateFrame(frameData, width, height);
        }
        catch (Exception ex)
        {
            _logger.Debug($"[Player] 帧更新失败: {ex.Message}");
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

            if (_ffmpegPlayer != null)
            {
                _ffmpegPlayer.FrameUpdated -= OnFrameUpdated;
                _ffmpegPlayer.Stop();
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
                _ffmpegPlayer?.Pause();
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
                _ffmpegPlayer?.Resume();
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
        if (_ffmpegPlayer?.IsPaused == true)
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
        if (_ffmpegPlayer == null) return;

        try
        {
            var newPosition = TimeSpan.FromMilliseconds(e.NewValue);
            CurrentTimeText.Text = FormatTime(newPosition);
        }
        catch { }
    }

    private void ProgressSlider_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_ffmpegPlayer == null || !_ffmpegPlayer.IsPlaying)
        {
            _logger.Debug("[Player] Seek ignored - player is null or not playing");
            return;
        }

        try
        {
            // 捕获当前值，避免在 Task.Run 期间 UI 冻结
            var currentValue = ProgressSlider.Value;
            var duration = _ffmpegPlayer.Duration.TotalSeconds;
            
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
            
            // 使用 Dispatcher 在后台线程执行 Seek，避免 UI 冻结
            Task.Run(() =>
            {
                try
                {
                    if (_ffmpegPlayer != null && _ffmpegPlayer.IsPlaying)
                    {
                        _ffmpegPlayer.Seek(seekPosition);
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
        if (_ffmpegPlayer != null)
        {
            _ffmpegPlayer.SetVolume((int)e.NewValue);
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
            TopBar.Visibility = Visibility.Visible;
            BottomBar.Visibility = Visibility.Visible;
            _hideControlsTimer?.Stop();
            _hideControlsTimer?.Start();
        }
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // ESC 键 - 先退出全屏，再次按ESC才退出播放
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

            // 空格键暂停/播放
            if (e.Key == System.Windows.Input.Key.Space)
            {
                if (_ffmpegPlayer?.IsPaused == true)
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

            // 左箭头键 - 快退5秒
            if (e.Key == System.Windows.Input.Key.Left)
            {
                var currentPos = (int)_ffmpegPlayer.Position.TotalSeconds;
                var newPos = Math.Max(0, currentPos - 5);
                _ffmpegPlayer.Seek(newPos);
                ShowControls();
                e.Handled = true;
                return;
            }

            // 右箭头键 - 快进5秒
            if (e.Key == System.Windows.Input.Key.Right)
            {
                var currentPos = (int)_ffmpegPlayer.Position.TotalSeconds;
                var maxPos = (int)_ffmpegPlayer.Duration.TotalSeconds;
                var newPos = Math.Min(maxPos, currentPos + 5);
                _ffmpegPlayer.Seek(newPos);
                ShowControls();
                e.Handled = true;
                return;
            }

            // 上箭头键 - 增加音量
            if (e.Key == System.Windows.Input.Key.Up)
            {
                var currentVol = int.Parse(VolumeLabel.Text);
                var newVol = Math.Min(100, currentVol + 10);
                VolumeSlider.Value = newVol;
                _ffmpegPlayer.SetVolume(newVol);
                VolumeLabel.Text = newVol.ToString();
                ShowControls();
                e.Handled = true;
                return;
            }

            // 下箭头键 - 减少音量
            if (e.Key == System.Windows.Input.Key.Down)
            {
                var currentVol = int.Parse(VolumeLabel.Text);
                var newVol = Math.Max(0, currentVol - 10);
                VolumeSlider.Value = newVol;
                _ffmpegPlayer.SetVolume(newVol);
                VolumeLabel.Text = newVol.ToString();
                ShowControls();
                e.Handled = true;
                return;
            }
        }

    private void AudioButton_Click(object sender, RoutedEventArgs e)
    {
        if (_ffmpegPlayer == null) return;
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
            _ffmpegPlayer?.SetAudioTrack(trackIndex);
            AudioPopup.IsOpen = false;
            UpdateAudioInfo();
            _logger.Debug($"[Player] Switched to audio track {trackIndex}");
        }
    }

    private void SubtitleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_ffmpegPlayer == null) return;
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
            try
            {
                _ffmpegPlayer?.SetSpuTrack(trackIndex);
                SubtitlePopup.IsOpen = false;
                UpdateSubtitleTrackList();
                _logger.Debug($"[Player] Switched to subtitle track {trackIndex}");
            }
            catch (Exception ex)
            {
                _logger.Debug($"[Player] Failed to switch subtitle track: {ex.Message}");
            }
        }
    }

    private void LoadSubtitleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_ffmpegPlayer == null) return;
        
        var dialog = new OpenFileDialog
        {
            Title = "选择字幕文件",
            Filter = "字幕文件 (*.srt;*.ass;*.ssa;*.sub)|*.srt;*.ass;*.ssa;*.sub|SRT文件 (*.srt)|*.srt|所有文件 (*.*)|*.*",
            FilterIndex = 1
        };
        
        if (dialog.ShowDialog() == true)
        {
            var success = _ffmpegPlayer.LoadExternalSubtitle(dialog.FileName);
            if (success)
            {
                UpdateSubtitleTrackList();
                _logger.Debug($"[Player] Loaded external subtitle: {dialog.FileName}");
            }
            else
            {
                MessageBox.Show("无法加载字幕文件，请确保文件格式正确。", "加载失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void UnloadSubtitleButton_Click(object sender, RoutedEventArgs e)
    {
        _ffmpegPlayer?.UnloadExternalSubtitle();
        SubtitleTextBlock.Visibility = Visibility.Collapsed;
        UpdateSubtitleTrackList();
        _logger.Debug("[Player] Unloaded external subtitle");
    }

    /// <summary>
    /// 加载窗口图标
    /// </summary>
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

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _logger.Debug("[MainWindow] OnClosing 被调用");
        _logger.Debug($"[MainWindow] StackTrace: {Environment.StackTrace}");
        StopPlaybackInternal();
        _ffmpegPlayer?.Dispose();
        _hideControlsTimer?.Stop();
        _progressTimer?.Stop();
        base.OnClosing(e);
        // 正常关闭，不需要显式调用 Shutdown
    }
}
