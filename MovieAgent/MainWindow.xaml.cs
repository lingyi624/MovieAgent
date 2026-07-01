using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using MovieAgent.Controls;
using MovieAgent.Core.Interfaces;
 using MovieAgent.FFmpegDecoder;
using MovieAgent.Infrastructure.Services;
using MovieAgent.Services;
using NAudio.Gui;
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
using System.Windows.Threading;
using static MovieAgent.FFmpegDecoder.FFmpegDecoderEngine;

namespace MovieAgent;

public partial class MainWindow : Window
{
    private IPlayerService? _playerService;
    private readonly ILoggerService _logger;
    private string _currentIsoMountPoint;
    private string _currentPlayingFilePath;

    public MainWindow()
    {
        Console.WriteLine($"[MainWindow] 构造函数开始, 线程ID: {Thread.CurrentThread.ManagedThreadId}");
        
        var app = (App)Application.Current;
        Console.WriteLine("[MainWindow] 获取 Application.Current 完成");
        
        var services = app.Services;
        Console.WriteLine($"[MainWindow] 获取 Services 完成, Services != null: {services != null}");
        
        if (services == null)
        {
            Console.WriteLine("[MainWindow] Services 为 null，显示错误消息");
            MessageBox.Show("应用程序服务未初始化，请重启应用。", "初始化错误", MessageBoxButton.OK, MessageBoxImage.Error);
            Application.Current.Shutdown();
            Console.WriteLine("[MainWindow] 调用 Shutdown 后返回");
            return;
        }
        
        _logger = services.GetRequiredService<ILoggerService>();
        Console.WriteLine("[MainWindow] 获取 ILoggerService 完成");
        _playerService = services.GetRequiredService<IPlayerService>();

        _logger.Debug("[MainWindow] 开始构造...");
        InitializeComponent();
        Console.WriteLine("[MainWindow] InitializeComponent 完成");
        
        // 加载窗口图标
        LoadWindowIcon();
        Console.WriteLine("[MainWindow] LoadWindowIcon 完成");
        
        try
        {
            BlazorWebView.HostPage = "wwwroot/index.html";
            BlazorWebView.Services = services;
            BlazorWebView.RootComponents.Add(
                new RootComponent { Selector = "#app", ComponentType = typeof(Components.Routes) });
            Console.WriteLine("[MainWindow] BlazorWebView 配置完成");

            // 订阅PlaybackRequestedByBlazor事件，当Blazor请求播放时显示视频overlay
            _playerService.PlaybackRequestedByBlazor += OnPlaybackRequestedByBlazor;
            Console.WriteLine("[MainWindow] 已订阅 PlaybackRequestedByBlazor 事件");

        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MainWindow] BlazorWebView 配置失败: {ex.Message}");
            _logger.Error(ex, "[MainWindow] BlazorWebView 配置失败");
        } 


        Console.WriteLine($"[MainWindow] 构造函数完成,线程id:{Thread.CurrentThread.ManagedThreadId}");
        //TestDirectImage();
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
    private void OnPlaybackRequestedByBlazor(object? sender, EventArgs e)
    {
        _logger.Debug("[Player] OnPlaybackRequestedByBlazor 被调用");
        string? filePath = _playerService?.GetCurrentRequestedFilePath();
        if (!string.IsNullOrEmpty(filePath))
        {
            _logger.Debug($"[Player] 从RequestPlayback获取到文件路径: {filePath}");
            PlayMovieInNewWindow(filePath);
        }
        else
        {
            _logger.Warning("[Player] GetCurrentRequestedFilePath 返回空");
        }
    }
    private async void StopPlaybackInternal()
    {
        try
        {

            if (_playerService != null)
            { 
                await _playerService.StopAsync();
            } 
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

    private void PlayMovieInNewWindow(string filePath)
    {
        // 1. 处理 BDMV/ISO 逻辑（和原来的 PlayMovie 前半部分一样）
        // ... 获取 actualPlayPath, _currentMovieTitle 等
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
        string _currentMovieTitle = Path.GetFileNameWithoutExtension(filePath);
        if (string.IsNullOrEmpty(_currentMovieTitle))
            _currentMovieTitle = Path.GetFileName(filePath);

        // 2. 创建并显示视频窗口
        var CurrentD3dModel = D3DMode.D3D12;
         if (CurrentD3dModel == D3DMode.D3D11)
        {
            var videoWindow = new MovieAgent.D3D11Window.VideoWindow();
            videoWindow.Hide();
            videoWindow.Show();
            // videoWindow.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            // 3. 创建控制窗口，传入视频窗口引用
            var controlWindow = new MovieAgent.D3D11Window.ControlWindow(videoWindow);
            controlWindow.Show();
            // controlWindow.Hide();
            controlWindow.Activate();
            // 4. 让控制窗口开始播放（设备就绪、播放、UI更新都在控制窗口内部完成）
            _ = controlWindow.StartPlaybackAsync(actualPlayPath, _currentMovieTitle);
        }
        else if (CurrentD3dModel == D3DMode.D3D12)
        {
            var videoWindow = new MovieAgent.D3D12Window.VideoWindow(); 
            videoWindow.Show();
            // videoWindow.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            // 3. 创建控制窗口，传入视频窗口引用
           var controlWindow = new MovieAgent.D3D12Window.ControlWindow(videoWindow);
           // controlWindow.Show();
           //  controlWindow.Hide();
          //  controlWindow.Activate();
            // 4. 让控制窗口开始播放（设备就绪、播放、UI更新都在控制窗口内部完成）
            _ = controlWindow.StartPlaybackAsync(actualPlayPath, _currentMovieTitle);
        } 

        // 5. 可选：隐藏主窗口（取决于你的需求）
        // this.Hide();
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
}
