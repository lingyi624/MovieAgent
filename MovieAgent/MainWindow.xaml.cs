using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using MovieAgent.Infrastructure.Services;

namespace MovieAgent;

public partial class MainWindow : Window
{
    private FFmpegPlayerService? _ffmpegPlayer;
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
        InitializeComponent();
        var services = ((App)Application.Current).Services;
        BlazorWebView.HostPage = "wwwroot/index.html";
        BlazorWebView.Services = services;
        BlazorWebView.RootComponents.Add(
            new RootComponent { Selector = "#app", ComponentType = typeof(Components.Routes) });

        _ffmpegPlayer = new FFmpegPlayerService();
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

        System.Diagnostics.Debug.WriteLine($"[MainWindow] FFmpegPlayer created, IsAvailable: {_ffmpegPlayer.IsAvailable}");
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
                    System.Diagnostics.Debug.WriteLine($"[Player] FFmpeg 播放开始: {filePath}");
                    System.Diagnostics.Debug.WriteLine($"[Player] 视频尺寸: {_ffmpegPlayer.VideoWidth}x{_ffmpegPlayer.VideoHeight}");
                    System.Diagnostics.Debug.WriteLine($"[Player] 播放层可见性: {VideoOverlay.Visibility}");
                    return;
                }

                FallbackToSystemPlayer(filePath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Player] FFmpeg 播放失败: {ex.Message}");
                FallbackToSystemPlayer(filePath);
            }
        });
    }

    private void UpdateAudioInfo()
    {
        if (_ffmpegPlayer == null) return;

        string audioText = $"音频: {_ffmpegPlayer.AudioFormat} | 声道: {_ffmpegPlayer.AudioChannels}ch | 采样率: {_ffmpegPlayer.AudioSampleRate}Hz";
        AudioInfo.Text = audioText;
        string videoText = $"视频: {_ffmpegPlayer.VideoWidth}x{_ffmpegPlayer.VideoHeight} | {_ffmpegPlayer.Fps:0.0} FPS";
        VideoInfo.Text = videoText;
        System.Diagnostics.Debug.WriteLine($"[Player] 音频信息: {audioText}, 视频信息: {videoText}");
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
                TimeDisplay.Text = $"{FormatTime(_ffmpegPlayer.Position)} / {FormatTime(_ffmpegPlayer.Duration)}";
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

        // 显示控制栏
        TopBar.Visibility = Visibility.Visible;
        BottomBar.Visibility = Visibility.Visible;
        _hideControlsTimer?.Start();

        System.Diagnostics.Debug.WriteLine("[Player] 进入全屏模式");
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

        System.Diagnostics.Debug.WriteLine("[Player] 退出全屏模式");
    }

    private void OnFrameUpdated(object? sender, byte[] frameData)
    {
        _frameCount++;
        if (_frameCount % 120 == 0)
        {
            System.Diagnostics.Debug.WriteLine($"[Player] 已渲染 {_frameCount} 帧, 数据大小: {frameData?.Length ?? 0} bytes");
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
            System.Diagnostics.Debug.WriteLine($"[Player] 帧更新失败: {ex.Message}");
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
            System.Diagnostics.Debug.WriteLine($"[Player] 使用系统播放器播放: {filePath}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"播放失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            System.Diagnostics.Debug.WriteLine($"[Player] 系统播放器启动失败: {ex.Message}");
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

            System.Diagnostics.Debug.WriteLine("[Player] 播放已停止");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Player] 停止播放出错: {ex.Message}");
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
                System.Diagnostics.Debug.WriteLine("[Player] 已暂停");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Player] 暂停出错: {ex.Message}");
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
                System.Diagnostics.Debug.WriteLine("[Player] 已恢复播放");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Player] 恢复出错: {ex.Message}");
            }
        });
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        ResumePlayback();
        ShowControls();
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        PausePlayback();
        ShowControls();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
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
            TimeDisplay.Text = $"{FormatTime(newPosition)} / {FormatTime(_ffmpegPlayer.Duration)}";
        }
        catch { }
    }

    private void ProgressSlider_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_ffmpegPlayer == null)
        {
            System.Diagnostics.Debug.WriteLine("[Player] Seek ignored - player is null");
            return;
        }

        try
        {
            // 捕获当前值，避免在 Task.Run 期间 UI 冻结
            var currentValue = ProgressSlider.Value;
            var duration = _ffmpegPlayer.Duration.TotalSeconds;
            var seekPosition = (int)(currentValue / 1000);
            
            if (seekPosition < 0 || seekPosition > duration)
            {
                System.Diagnostics.Debug.WriteLine($"[Player] Seek ignored - invalid position: {seekPosition}");
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[Player] Seek to {seekPosition} seconds");
            
            // 在后台线程执行 Seek，避免 UI 冻结
            Task.Run(() =>
            {
                try
                {
                    _ffmpegPlayer.Seek(seekPosition);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Player] Seek exception: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Player] Seek failed: {ex.Message}");
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
        // ESC 键 - 停止播放或退出全屏
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            if (_isFullScreen && VideoOverlay.Visibility == Visibility.Visible)
            {
                StopPlaybackInternal();
            }
            else if (_isFullScreen)
            {
                ExitFullScreen();
                FullscreenButton.Content = "全屏";
            }
            e.Handled = true;
        }

        // 空格键暂停/播放
        if (e.Key == System.Windows.Input.Key.Space && VideoOverlay.Visibility == Visibility.Visible)
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
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        StopPlaybackInternal();
        _ffmpegPlayer?.Dispose();
        _hideControlsTimer?.Stop();
        _progressTimer?.Stop();
        base.OnClosing(e);
    }
}
