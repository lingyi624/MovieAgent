using MovieAgent.Core.Interfaces;
using MovieAgent.FFmpegDecoder;
using MovieAgent.Infrastructure.Services;
using MovieAgent.Services;
using System;
using System.Collections.Generic;
using System.Management;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using Vortice.Direct3D12;
using Vortice.Direct3D9;

namespace MovieAgent.Controls.Window.D3D9Window
{
    public partial class VideoWindow : System.Windows.Window
    {
        private readonly IPlayerService _playerService;
        private readonly string _filePath;
        private bool _isFullscreen;
        private bool _isPlaying;
        private bool _isDraggingProgress;
        private double _originalWidth;
        private double _originalHeight;
        private double _originalLeft;
        private double _originalTop;
        private WindowStyle _originalWindowStyle;
        private bool _originalAllowTransparency;
        private System.Windows.Threading.DispatcherTimer _hideControlsTimer;
        private double _subtitleDelay;
        private string _currentMovieTitle = string.Empty;
        private string _currentAudioTrack = "立体声";
        private string _currentSubtitle = "无";
        private string _currentDecoderName = string.Empty;
        private string _currentDecodeMode = "自动";
        private string? _lastPlayPauseContent;
        private ISubtitleService? _subtitleService;
        private ILoggerService? _logger;
        private string? _externalSubtitlePath;
        private List<SubtitleItem>? _externalSubtitles;

        private ResizeMode _originalResizeMode;
        private bool _originalTopmost;
        private bool _isClosingPopup = false;

        public VideoWindow(IPlayerService playerService, string filePath, string movieTitle)
        {
            InitializeComponent();
            _playerService = playerService;
            _filePath = filePath;
            _currentMovieTitle = movieTitle;
            MovieTitle.Text = movieTitle;

            var services = ((MovieAgent.App)System.Windows.Application.Current).Services;
            _logger = (ILoggerService)services.GetService(typeof(ILoggerService));
            _subtitleService = (ISubtitleService?)services.GetService(typeof(ISubtitleService));

            InitializePlayer();
            InitializeControlsTimer();
             this.Loaded += VideoWindow_Loaded;
        }

        private void VideoWindow_Loaded(object sender, RoutedEventArgs e)
        {
            VideoRenderer.Initialize();
         }

       

        
 
        public IDirect3DDevice9Ex? GetDevice()
        {
            _playerService.SetD3d9Device(VideoRenderer?.Device);
            return VideoRenderer?.Device;
        }

        private void InitializePlayer()
        {
            _playerService.FrameUpdated += PlayerService_FrameUpdated;
            _playerService.PlaybackEnded += PlayerService_PlaybackEnded;
        }

        private void InitializeControlsTimer()
        {
            _hideControlsTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _hideControlsTimer.Tick += HideControlsTimer_Tick;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            ControlsPopup.IsOpen = true;
            TopBarPopup.IsOpen = true;
            _hideControlsTimer.Start();
            GetDevice();
            EnterFullscreen();
            ShowControls();
            _playerService.PlayAsync(_filePath).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    Dispatcher.Invoke(() => System.Windows.MessageBox.Show("播放失败: " + t.Exception?.Message));
                }
                else
                {
                    Dispatcher.Invoke(() =>
                    {
                        LoadAudioTracks();
                        LoadSubtitleTracks();
                        UpdateMediaInfo();
                    });
                }
            });
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            _hideControlsTimer?.Stop();
            _hideControlsTimer = null;

            _playerService.FrameUpdated -= PlayerService_FrameUpdated;
            _playerService.PlaybackEnded -= PlayerService_PlaybackEnded;

            _playerService.StopAsync().Wait();
        }

        private void Window_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_isClosingPopup) return;
            ShowControls();
            _hideControlsTimer.Stop();
            _hideControlsTimer.Start();
        }

        private void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_isPlaying)
                HideControls();
        }

        private void Window_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Space:
                    e.Handled = true;
                    if (_playerService.IsPaused)
                    {
                        ResumePlayback();
                    }
                    else if (_playerService.IsPlaying)
                    {
                        PausePlayback();
                    }
                    break;
                case Key.F:
                case Key.Escape:
                    e.Handled = true;
                    ToggleFullscreen();
                    break;
                case Key.Left:
                    e.Handled = true;
                    _playerService.Seek((int)_playerService.Position.TotalSeconds - 10);
                    break;
                case Key.Right:
                    e.Handled = true;
                    _playerService.Seek((int)_playerService.Position.TotalSeconds + 10);
                    break;
                case Key.Up:
                    e.Handled = true;
                    var currentVol = (int)(_playerService.Volume * 100);
                    _playerService.SetVolume(Math.Min(100, currentVol + 10));
                    break;
                case Key.Down:
                    e.Handled = true;
                    currentVol = (int)(_playerService.Volume * 100);
                    _playerService.SetVolume(Math.Max(0, currentVol - 10));
                    break;
                case Key.Enter:
                    e.Handled = true;
                    ToggleControls();
                    if (ControlsPopup.IsOpen)
                    {
                        _hideControlsTimer.Stop();
                        _hideControlsTimer.Start();
                    }
                    else
                    {
                        _hideControlsTimer.Stop();
                    }
                    break;
            }
        }

   

        private void ShowControls()
        {
            ControlsPopup.IsOpen = true;
            TopBarPopup.IsOpen = true;
            CenterOverlay.Visibility = Visibility.Collapsed;
        }

        private void HideControls()
        {
            _isClosingPopup = true;
            ControlsPopup.IsOpen = false;
            TopBarPopup.IsOpen = false;

            System.Windows.Threading.DispatcherTimer unlockTimer = new System.Windows.Threading.DispatcherTimer
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

        private void ToggleControls()
        {
            ControlsPopup.IsOpen = !ControlsPopup.IsOpen;
        }

        private void HideControlsTimer_Tick(object sender, EventArgs e)
        {
            if (_isPlaying)
                HideControls();
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

        private void CenterPlayButton_Click(object sender, RoutedEventArgs e)
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
            else
            {
                ResumePlayback();
            }
        }

        private async void StopPlaybackInternal()
        {
            try
            {
                _hideControlsTimer?.Stop();

                if (_playerService != null)
                {
                    _playerService.FrameUpdated -= PlayerService_FrameUpdated;
                    await _playerService.StopAsync();
                }

                _lastPlayPauseContent = null;
                PlayPauseButton.Content = "▶";

                _logger?.Debug("[Player] 播放已停止");

                this.Close();
            }
            catch (Exception ex)
            {
                _logger?.Debug($"[Player] 停止播放出错: {ex.Message}");
                try { this.Close(); } catch { }
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
                    _logger?.Debug("[Player] 已暂停");
                }
                catch (Exception ex)
                {
                    _logger?.Debug($"[Player] 暂停出错: {ex.Message}");
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
                    _logger?.Debug("[Player] 已恢复播放");
                }
                catch (Exception ex)
                {
                    _logger?.Debug($"[Player] 恢复出错: {ex.Message}");
                }
            });
        }

        private void UpdatePlayStatus()
        {
            if (_playerService == null) return;

            PlayStatusText.Text = _playerService.IsPaused ? "⏸ 已暂停" : "▶ 正在播放";

            string newContent = _playerService.IsPaused ? "▶" : "⏸";
            if (_lastPlayPauseContent != newContent)
            {
                PlayPauseButton.Content = newContent;
                _lastPlayPauseContent = newContent;
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            StopPlayback();
        }

        private void FullscreenButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleFullscreen();
        }

        private void ToggleFullscreen()
        {
            if (_isFullscreen)
            {
                ExitFullscreen();
            }
            else
            {
                EnterFullscreen();
            }
        }

        private void EnterFullscreen()
        {
            _isFullscreen = true;

            _originalWidth = Width;
            _originalHeight = Height;
            _originalLeft = Left;
            _originalTop = Top;
            _originalWindowStyle = WindowStyle;
            _originalAllowTransparency = AllowsTransparency;
            _originalResizeMode = ResizeMode;
            _originalTopmost = Topmost;

            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            //AllowsTransparency = false;
            Topmost = true;
            WindowState = WindowState.Maximized;

            FullscreenButton.Content = "⛶";
        }

        private void ExitFullscreen()
        {
            _isFullscreen = false;

            WindowState = WindowState.Normal;
            WindowStyle = _originalWindowStyle;
            ResizeMode = _originalResizeMode;
           /// AllowsTransparency = _originalAllowTransparency;
            Topmost = _originalTopmost;

            Width = _originalWidth;
            Height = _originalHeight;
            Left = _originalLeft;
            Top = _originalTop;

            FullscreenButton.Content = "⛶";
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            StopPlayback();
        }

        private void Min_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void Restore_Click(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized)
                WindowState = WindowState.Normal;
            else
                WindowState = WindowState.Maximized;
        }

        private void CloseWindow_Click(object sender, RoutedEventArgs e)
        {
            StopPlayback();
        }

        private void ProgressSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isDraggingProgress)
            {
                var newPosition = TimeSpan.FromMilliseconds(e.NewValue);
                CurrentTimeText.Text = FormatTime(newPosition);
            }
        }

        private void ProgressSlider_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingProgress = true;
        }

        private void ProgressSlider_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _isDraggingProgress = false;

            if (_playerService == null || !_playerService.IsPlaying)
            {
                _logger?.Debug("[Player] Seek ignored - player is null or not playing");
                return;
            }

            try
            {
                var currentValue = ProgressSlider.Value;
                var duration = _playerService.Duration.TotalSeconds;

                if (duration <= 0)
                {
                    _logger?.Debug("[Player] Seek ignored - invalid duration");
                    return;
                }

                var seekPosition = (int)(currentValue / 1000);

                if (seekPosition < 0 || seekPosition > duration)
                {
                    _logger?.Debug($"[Player] Seek ignored - invalid position: {seekPosition}");
                    return;
                }

                _logger?.Debug($"[Player] Seek to {seekPosition} seconds");

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
                        _logger?.Error(ex, "[Player] Seek failed");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[Player] Seek mouse up handler failed");
            }
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _playerService?.SetVolume((int)e.NewValue);
            if (_playerService != null)
            {
                VolumeLabel.Text = e.NewValue.ToString("N0");
                UpdateVolumeIcon(e.NewValue);
            }
        }

        private void UpdateVolumeIcon(double volume)
        {
            if (volume == 0)
                VolumeLabel.Text = "🔇";
            else if (volume < 50)
                VolumeLabel.Text = "🔉";
            else
                VolumeLabel.Text = "🔊";
        }

        private void AudioButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosingPopup)
                return;

            LoadAudioTracks();
            AudioPopup.IsOpen = !AudioPopup.IsOpen;

            if (!AudioPopup.IsOpen)
            {
                _isClosingPopup = true;
                System.Windows.Threading.DispatcherTimer timer = new System.Windows.Threading.DispatcherTimer();
                timer.Interval = TimeSpan.FromMilliseconds(200);
                timer.Tick += (s, args) =>
                {
                    timer.Stop();
                    _isClosingPopup = false;
                };
                timer.Start();
            }
        }

        private void SubtitleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosingPopup)
                return;

            LoadSubtitleTracks();
            SubtitlePopup.IsOpen = !SubtitlePopup.IsOpen;

            if (!SubtitlePopup.IsOpen)
            {
                _isClosingPopup = true;
                System.Windows.Threading.DispatcherTimer timer = new System.Windows.Threading.DispatcherTimer();
                timer.Interval = TimeSpan.FromMilliseconds(200);
                timer.Tick += (s, args) =>
                {
                    timer.Stop();
                    _isClosingPopup = false;
                };
                timer.Start();
            }
        }

        private void ScreenshotButton_Click(object sender, RoutedEventArgs e)
        {
            _playerService.TakeScreenshot();
        }

        private void SpeedButton_Click(object sender, RoutedEventArgs e)
        {
            SpeedPopup.IsOpen = !SpeedPopup.IsOpen;
        }

        private void SpeedOption_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as System.Windows.Controls.Button;
            if (button != null && double.TryParse(button.Tag?.ToString(), out double speed))
            {
                _playerService.SetPlaybackSpeed(speed);
                SpeedPopup.IsOpen = false;
            }
        }

        private void InfoButton_Click(object sender, RoutedEventArgs e)
        {
            LoadSystemInfo();
            LoadMediaInfo();
            InfoPopup.IsOpen = !InfoPopup.IsOpen;
        }

        private void CloseInfoButton_Click(object sender, RoutedEventArgs e)
        {
            InfoPopup.IsOpen = false;
        }

        private void LoadSubtitleButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择字幕文件",
                Filter = "字幕文件 (*.srt;*.ass;*.ssa)|*.srt;*.ass;*.ssa|SRT文件 (*.srt)|*.srt|ASS文件 (*.ass)|*.ass|所有文件 (*.*)|*.*",
                FilterIndex = 1
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                try
                {
                    _externalSubtitlePath = dialog.FileName;
                    _externalSubtitles = SubtitleParser.Parse(_externalSubtitlePath);

                    if (_externalSubtitles.Count > 0)
                    {
                        _logger?.Debug($"[Player] 加载外部字幕成功: {dialog.FileName}, 共 {_externalSubtitles.Count} 条");

                        SubtitlePopup.IsOpen = false;
                        _subtitleDelay = 0;
                        SubtitleDelayText.Text = $"延迟: 0ms";
                    }
                    else
                    {
                        _logger?.Debug("[Player] 加载外部字幕失败: 未找到字幕");
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "[Player] 加载外部字幕失败");
                }
            }
        }

        private void DownloadSubtitleButton_Click(object sender, RoutedEventArgs e)
        {
            SubtitleDownloadPopup.IsOpen = true;
        }

        private void UnloadSubtitleButton_Click(object sender, RoutedEventArgs e)
        {
            _playerService.SetSpuTrack(-1);
            LoadSubtitleTracks();
        }

        private void SubtitleDelayButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as System.Windows.Controls.Button;
            if (button != null && int.TryParse(button.Tag?.ToString(), out int delay))
            {
                _subtitleDelay += delay;
                _playerService.SetSubtitleDelay(_subtitleDelay);
                SubtitleDelayText.Text = $"延迟: {_subtitleDelay}ms";
            }
        }

        private void CloseSubtitleDownloadButton_Click(object sender, RoutedEventArgs e)
        {
            SubtitleDownloadPopup.IsOpen = false;
        }

        private async void SearchSubtitleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_subtitleService == null || string.IsNullOrEmpty(_filePath))
                return;

            var query = SubtitleSearchBox.Text.Trim();
            if (string.IsNullOrEmpty(query))
            {
                query = System.IO.Path.GetFileNameWithoutExtension(_filePath);
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
                _logger?.Debug($"[Player] Searching subtitles for: {query}, language: {language}");
                var results = await _subtitleService.SearchSubtitlesAsync(query, language);

                if (results.Count == 0)
                {
                    SubtitleSearchStatus.Text = "未找到匹配的字幕";
                    return;
                }

                SubtitleSearchStatus.Text = $"找到 {results.Count} 个字幕";

                foreach (var subtitle in results)
                {
                    var itemPanel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
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
                _logger?.Error(ex, "[Player] 搜索字幕失败");
                SubtitleSearchStatus.Text = "搜索失败，请稍后重试";
            }
        }

        private async void DownloadSubtitleItem_Click(object sender, RoutedEventArgs e)
        {
            if (_subtitleService == null || sender is not System.Windows.Controls.Button button || button.Tag is not SubtitleResult subtitle)
                return;

            button.IsEnabled = false;
            SubtitleSearchStatus.Text = "正在下载...";

            try
            {
                _logger?.Debug($"[Player] Downloading subtitle: {subtitle.Title}");
                var subtitleData = await _subtitleService.DownloadSubtitleAsync(subtitle.DownloadUrl);

                if (subtitleData == null || subtitleData.Length == 0)
                {
                    SubtitleSearchStatus.Text = "下载失败";
                    button.IsEnabled = true;
                    return;
                }

                string savedPath = string.Empty;

                if (_playerService != null && _playerService.IsPlaying)
                {
                    string? videoPath = _playerService.GetCurrentRequestedFilePath();
                    if (!string.IsNullOrEmpty(videoPath) && System.IO.File.Exists(videoPath))
                    {
                        string directory = System.IO.Path.GetDirectoryName(videoPath)!;
                        string videoName = System.IO.Path.GetFileNameWithoutExtension(videoPath);
                        string subtitleFileName = $"{videoName}{subtitle.Extension}";
                        savedPath = System.IO.Path.Combine(directory, subtitleFileName);

                        await System.IO.File.WriteAllBytesAsync(savedPath, subtitleData);
                        _logger?.Debug($"[Player] Subtitle saved to: {savedPath}");
                    }
                }

                if (string.IsNullOrEmpty(savedPath))
                {
                    savedPath = await _subtitleService.SaveSubtitleAsync(0, subtitleData, subtitle.Extension);
                }

                SubtitleSearchStatus.Text = $"字幕已保存: {System.IO.Path.GetFileName(savedPath)}";
                button.Content = "已下载";
                _logger?.Debug($"[Player] Subtitle download completed: {savedPath}");
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[Player] Subtitle download failed");
                SubtitleSearchStatus.Text = "下载失败";
                button.IsEnabled = true;
            }
        }

        private void LoadAudioTracks()
        {
            AudioTrackListPanel.Children.Clear();
            var tracks = _playerService.GetAudioTracks();
            if (tracks != null && tracks.Count > 0)
            {
                NoAudioTracksText.Visibility = Visibility.Collapsed;
                foreach (var track in tracks)
                {
                    var btn = new System.Windows.Controls.Button
                    {
                        Content = $"{track.Index + 1}. {GetLanguageName(track.Language)}",
                        Tag = track.Index,
                        Style = track.Index == _playerService.CurrentAudioTrack
                            ? (Style)Resources["SelectedListButtonStyle"]
                            : (Style)Resources["ListButtonStyle"]
                    };
                    btn.Click += AudioTrackButton_Click;
                    AudioTrackListPanel.Children.Add(btn);
                }
            }
            else
            {
                NoAudioTracksText.Visibility = Visibility.Visible;
            }
        }

        private void AudioTrackButton_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as System.Windows.Controls.Button;
            if (btn != null && int.TryParse(btn.Tag?.ToString(), out int index))
            {
                _playerService.SetAudioTrack(index);
                LoadAudioTracks();
            }
        }

        private void LoadSubtitleTracks()
        {
            SubtitleTrackListPanel.Children.Clear();

            var closeSubBtn = new System.Windows.Controls.Button
            {
                Content = "关闭字幕",
                Tag = -1,
                Style = _playerService.CurrentSpuTrack == -1
                    ? (Style)Resources["SelectedListButtonStyle"]
                    : (Style)Resources["ListButtonStyle"]
            };
            closeSubBtn.Click += SubtitleTrackButton_Click;
            SubtitleTrackListPanel.Children.Add(closeSubBtn);

            var tracks = _playerService.GetSubtitleTracks();
            if (tracks != null && tracks.Count > 0)
            {
                NoSubtitleTracksText.Visibility = Visibility.Collapsed;
                foreach (var track in tracks)
                {
                    var btn = new System.Windows.Controls.Button
                    {
                        Content = $"内置字幕 {track.Index + 1}. {GetLanguageName(track.Language)}",
                        Tag = track.Index,
                        Style = track.Index == _playerService.CurrentSpuTrack
                            ? (Style)Resources["SelectedListButtonStyle"]
                            : (Style)Resources["ListButtonStyle"]
                    };
                    btn.Click += SubtitleTrackButton_Click;
                    SubtitleTrackListPanel.Children.Add(btn);
                }
            }
            else
            {
                NoSubtitleTracksText.Visibility = Visibility.Visible;
            }
        }

        private void SubtitleTrackButton_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as System.Windows.Controls.Button;
            if (btn != null && int.TryParse(btn.Tag?.ToString(), out int index))
            {
                _playerService.SetSpuTrack(index);
                LoadSubtitleTracks();
            }
        }

        private string GetLanguageName(string? langCode)
        {
            if (string.IsNullOrEmpty(langCode)) return "未知";
            var langMap = new Dictionary<string, string>
            {
                {"zh", "中文"}, {"zh-CN", "中文(简体)"}, {"zh-TW", "中文(繁体)"},
                {"en", "English"}, {"ja", "日本語"}, {"ko", "한국어"},
                {"fr", "Français"}, {"de", "Deutsch"}, {"es", "Español"},
                {"ru", "Русский"}, {"und", "未知"}
            };
            return langMap.TryGetValue(langCode, out var name) ? name : langCode;
        }

        private void UpdateMediaInfo()
        {
            VideoInfo.Text = $"{_playerService.VideoWidth}x{_playerService.VideoHeight}";
            AudioInfo.Text = $"FPS: 未知";
            DecodeModeInfo.Text = _playerService.CurrentD3dModel?.ToString() ?? "未知";
        }

        private void LoadSystemInfo()
        {
            CpuInfoText.Text = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "未知";
            MemoryInfoText.Text = $"{(GC.GetTotalMemory(false) / (1024 * 1024))} MB";
            GpuInfoText.Text = "Direct3D9";
            ResolutionText.Text = $"{SystemParameters.PrimaryScreenWidth}x{SystemParameters.PrimaryScreenHeight}";
            OSInfoText.Text = Environment.OSVersion.VersionString;
        }

        private void LoadMediaInfo()
        {
            FileNameText.Text = System.IO.Path.GetFileName(_filePath);
            VideoResolutionText.Text = $"{_playerService?.VideoWidth}x{_playerService?.VideoHeight}";
            FpsText.Text = "未知";
            DurationText.Text = FormatTime(_playerService?.Duration ?? TimeSpan.Zero);
            DecodeModeText.Text = _playerService?.CurrentD3dModel?.ToString() ?? "未知";
            DecoderNameText.Text = _currentDecoderName;
        }

        private void PlayerService_FrameUpdated(object? sender, MovieAgent.FFmpegDecoder.FrameData e)
        {
            if (e == null) return;

            _isPlaying = _playerService.IsPlaying;
            Dispatcher.Invoke(() =>
            {
                UpdatePlayStatus();
            });
            if (!_isDraggingProgress && _playerService.Duration.TotalSeconds > 0)
            {
                var position = _playerService.Position.TotalMilliseconds;
                var duration = _playerService.Duration.TotalMilliseconds;
                Dispatcher.Invoke(() =>
                {
                    if (!ProgressSlider.IsMouseCaptureWithin)
                    {
                        if (Math.Abs(ProgressSlider.Value - position) > 100)
                        {
                            ProgressSlider.Maximum = duration;
                            ProgressSlider.Value = position;
                        }
                    }
                    CurrentTimeText.Text = FormatTime(_playerService.Position);
                    TotalTimeText.Text = FormatTime(_playerService.Duration);
                });
            }

            Dispatcher.Invoke(() =>
            {
                if (VideoRenderer != null)
                {
                    if (e.IsHardwareFrame && e.NV12TexturePtr != IntPtr.Zero)
                    {
                        VideoRenderer.RenderHardwareFrame(e.NV12TexturePtr, e.Width, e.Height);
                    }
                    else if (e.YPlane.Length > 0 && e.UPlane.Length > 0 && e.VPlane.Length > 0)
                    {
                        VideoRenderer.RenderSoftwareFrame(e.YPlane, e.UPlane, e.VPlane, e.Width, e.Height);
                    }
                }
            });
        }

        private void PlayerService_PlaybackEnded(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                _isPlaying = false;
                PlayPauseButton.Content = "▶";
                ControlsPopup.IsOpen = true;
                TopBarPopup.IsOpen = true;
                CenterOverlay.Visibility = Visibility.Visible;
                ProgressSlider.Value = 0;
            });
        }

        private string FormatTime(TimeSpan time)
        {
            return $"{time.Hours:D2}:{time.Minutes:D2}:{time.Seconds:D2}";
        }

        private void VideoArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            ShowControls();
            _hideControlsTimer.Stop();
            _hideControlsTimer.Start();
        }
    }
}