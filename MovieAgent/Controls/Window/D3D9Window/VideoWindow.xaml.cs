using MovieAgent.Core.Interfaces;
using MovieAgent.FFmpegDecoder;
using System;
using System.Collections.Generic;
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

        private ResizeMode _originalResizeMode;
        private bool _originalTopmost;

        public VideoWindow(IPlayerService playerService, string filePath, string movieTitle)
        {
            InitializeComponent();
            _playerService = playerService;
            _filePath = filePath;
            MovieTitle.Text = movieTitle;
            InitializePlayer();
            InitializeControlsTimer();
            MainGrid.SizeChanged += MainGrid_SizeChanged;
            this.Loaded += VideoWindow_Loaded;
        }

        private void VideoWindow_Loaded(object sender, RoutedEventArgs e)
        {
            VideoRenderer.Initialize();
            UpdateControlsPopupSize();
        }

        private void MainGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateControlsPopupSize();
        }

        private void UpdateControlsPopupSize()
        {
            ControlsPopupGrid.Width = MainGrid.ActualWidth;
            ControlsPopupGrid.Height = MainGrid.ActualHeight;
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
                    TogglePlayPause();
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
            CenterOverlay.Visibility = Visibility.Collapsed;
        }

        private void HideControls()
        {
            ControlsPopup.IsOpen = false;
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
            TogglePlayPause();
        }

        private void CenterPlayButton_Click(object sender, RoutedEventArgs e)
        {
            TogglePlayPause();
        }

        private void TogglePlayPause()
        {
            if (_playerService.IsPlaying)
            {
                _playerService.Pause();
            }
            else
            {
                _playerService.Resume();
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            _playerService.StopAsync().Wait();
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
            _playerService.StopAsync().Wait();
            Close();
        }

        private void CloseButton_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            CloseButton.Background = new SolidColorBrush(Colors.Red);
        }

        private void CloseButton_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            CloseButton.Background = Brushes.Transparent;
        }

        private void ProgressSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isDraggingProgress)
            {
                var percentage = e.NewValue;
                if (_playerService.Duration.TotalSeconds > 0)
                {
                    var time = TimeSpan.FromSeconds(_playerService.Duration.TotalSeconds * (percentage / 100));
                    CurrentTimeText.Text = FormatTime(time);
                }
            }
        }

        private void ProgressSlider_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_playerService.Duration.TotalSeconds > 0)
            {
                //  _playerService.Seek((int)(_playerService.Duration.TotalSeconds * (ProgressSlider.Value / 100)));
                var seekPosition = (int)(_playerService.Duration.TotalSeconds / 1000);


                _playerService.Seek(seekPosition);
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
            LoadAudioTracks();
            AudioPopup.IsOpen = !AudioPopup.IsOpen;
        }

        private void SubtitleButton_Click(object sender, RoutedEventArgs e)
        {
            LoadSubtitleTracks();
            SubtitlePopup.IsOpen = !SubtitlePopup.IsOpen;
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
            var openFileDialog = new OpenFileDialog
            {
                Filter = "字幕文件|*.srt;*.ass;*.ssa|所有文件|*.*",
                Title = "选择字幕文件"
            };
            if (openFileDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _playerService.SetSubtitleDelay(_subtitleDelay);
                SubtitlePopup.IsOpen = false;
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

        private void SearchSubtitleButton_Click(object sender, RoutedEventArgs e)
        {
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
            var tracks = _playerService.GetSubtitleTracks();
            if (tracks != null && tracks.Count > 0)
            {
                NoSubtitleTracksText.Visibility = Visibility.Collapsed;
                foreach (var track in tracks)
                {
                    var btn = new System.Windows.Controls.Button
                    {
                        Content = $"{track.Index + 1}. {GetLanguageName(track.Language)}",
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
            VideoResolutionText.Text = $"{_playerService.VideoWidth}x{_playerService.VideoHeight}";
            FpsText.Text = "未知";
            DurationText.Text = FormatTime(_playerService.Duration);
            DecodeModeText.Text = _playerService.CurrentD3dModel?.ToString() ?? "未知";
            DecoderNameText.Text = "D3D9";
        }

        private void PlayerService_FrameUpdated(object? sender, MovieAgent.FFmpegDecoder.FrameData e)
        {
            if (e == null) return;

            _isPlaying = _playerService.IsPlaying;
            Dispatcher.Invoke(() =>
            {
                PlayPauseButton.Content = _isPlaying ? "⏸" : "▶";
            });
            if (!_isDraggingProgress && _playerService.Duration.TotalSeconds > 0)
            {
                var percentage = (_playerService.Position.TotalSeconds / _playerService.Duration.TotalSeconds) * 100;
                Dispatcher.Invoke(() =>
                {
                    ProgressSlider.Value = percentage;
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
                        // 零拷贝路径：直接传递解码器表面
                        VideoRenderer.RenderHardwareFrame(e.NV12TexturePtr, e.Width, e.Height);
                    }
                    else if (e.YPlane.Length > 0 && e.UPlane.Length > 0 && e.VPlane.Length > 0)
                    {
                        // 软解路径：内部 GPU 转换，无 CPU→GPU 拷贝
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