using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MovieAgent.Core.Entities;
using MovieAgent.Core.Interfaces;
using MovieAgent.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace MovieAgent.Controls.Window;

public partial class MovieWallpaperWindow : System.Windows.Window
{
    private int _currentIndex = 0;
    private List<MovieWallpaperData> _movieWallpapers = new();
    private DispatcherTimer? _checkTimer;
    private DispatcherTimer? _switchTimer;
    private const int IdleMinutes = 5;
    private bool _isWallpaperVisible = false;
    private IPlayerService? _playerService;

    // Win32 API：获取系统最后一次输入时间，用于检测全局鼠标/键盘空闲
    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    private class MovieWallpaperData
    {
        public string PosterUrl { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Director { get; set; } = string.Empty;
        public string ReleaseDate { get; set; } = string.Empty;
        public string Overview { get; set; } = string.Empty;
    }

    public MovieWallpaperWindow()
    {
        InitializeComponent();
        InitializeMovieData();
        ShowCurrentMovie();
        InitializeTimers();
        // 启动时隐藏壁纸窗口
        this.Hide();
    }

    private void InitializeTimers()
    {
        // 获取 IPlayerService 引用，用于检测视频播放状态
        try
        {
            var serviceProvider = ((App)Application.Current).Services;
            _playerService = serviceProvider.GetService<IPlayerService>();
        }
        catch { }

        // 检测定时器：每秒检查视频播放状态和系统空闲时间
        _checkTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _checkTimer.Tick += CheckTimer_Tick;
        _checkTimer.Start();

        // 切换定时器：壁纸显示后每5分钟切换一次
        _switchTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(IdleMinutes)
        };
        _switchTimer.Tick += SwitchTimer_Tick;
    }

    private void CheckTimer_Tick(object? sender, EventArgs e)
    {
        // 1. 视频正在播放 → 隐藏壁纸
        if (_playerService != null && _playerService.IsPlaying)
        {
            if (_isWallpaperVisible)
            {
                HideWallpaper();
            }
            return;
        }

        // 2. 鼠标或键盘在使用（系统空闲时间 < 5分钟）→ 隐藏壁纸
        uint idleMs = GetSystemIdleTimeMs();
        if (idleMs < (uint)(IdleMinutes * 60 * 1000))
        {
            if (_isWallpaperVisible)
            {
                HideWallpaper();
            }
            return;
        }

        // 3. 空闲超过5分钟且无视频播放 → 显示壁纸
        if (!_isWallpaperVisible)
        {
            ShowWallpaper();
        }
    }

    private void SwitchTimer_Tick(object? sender, EventArgs e)
    {
        // 只有壁纸显示时才切换
        if (_isWallpaperVisible)
        {
            _currentIndex = (_currentIndex + 1) % _movieWallpapers.Count;
            FadeTransition(ShowCurrentMovie);
        }
    }

    /// <summary>
    /// 获取系统空闲时间（毫秒），从最后一次鼠标/键盘输入算起
    /// </summary>
    private uint GetSystemIdleTimeMs()
    {
        LASTINPUTINFO info = new LASTINPUTINFO();
        info.cbSize = (uint)Marshal.SizeOf(info);
        if (GetLastInputInfo(ref info))
        {
            uint now = (uint)Environment.TickCount;
            return now - info.dwTime;
        }
        return 0;
    }

    private void ShowWallpaper()
    {
        _isWallpaperVisible = true;
        this.Show();
        _switchTimer?.Start();
    }

    private void HideWallpaper()
    {
        _isWallpaperVisible = false;
        this.Hide();
        _switchTimer?.Stop();
    }

    private void InitializeMovieData()
    {
        _movieWallpapers = new List<MovieWallpaperData>();

        try
        {
            var serviceProvider = ((App)Application.Current).Services;
            var movieRepository = serviceProvider.GetService<IMovieRepository>();

            if (movieRepository != null)
            {
                var movies = movieRepository.GetAllAsync(new MovieFilter
                {
                    Genres = new List<string> { "动作" },
                    PageSize = 5,
                    SortBy = "CreatedAt",
                    SortDescending = true
                }).GetAwaiter().GetResult();

                foreach (var movie in movies)
                {
                    if (!string.IsNullOrEmpty(movie.BackdropPath))
                    {
                        _movieWallpapers.Add(new MovieWallpaperData
                        {
                            PosterUrl = $"https://image.tmdb.org/t/p/original{movie.BackdropPath}",
                            Title = movie.Title,
                            Director = string.IsNullOrEmpty(movie.Director) ? "导演: 未知" : $"导演: {movie.Director}",
                            ReleaseDate = movie.ReleaseDate.HasValue ? $"上映日期: {movie.ReleaseDate.Value:yyyy-MM-dd}" : "上映日期: 未知",
                            Overview = string.IsNullOrEmpty(movie.Overview) ? "暂无简介" : movie.Overview
                        });
                    }
                    else if (!string.IsNullOrEmpty(movie.PosterPath))
                    {
                        _movieWallpapers.Add(new MovieWallpaperData
                        {
                            PosterUrl = $"https://image.tmdb.org/t/p/original{movie.PosterPath}",
                            Title = movie.Title,
                            Director = string.IsNullOrEmpty(movie.Director) ? "导演: 未知" : $"导演: {movie.Director}",
                            ReleaseDate = movie.ReleaseDate.HasValue ? $"上映日期: {movie.ReleaseDate.Value:yyyy-MM-dd}" : "上映日期: 未知",
                            Overview = string.IsNullOrEmpty(movie.Overview) ? "暂无简介" : movie.Overview
                        });
                    }
                }
            }
        }
        catch
        {
        }

        if (_movieWallpapers.Count == 0)
        {
            AddDefaultMovies();
        }
    }

    private void AddDefaultMovies()
    {
        _movieWallpapers = new List<MovieWallpaperData>
        {
            new MovieWallpaperData
            {
                PosterUrl = "https://trae-api-cn.mchost.guru/api/ide/v1/text_to_image?prompt=sci-fi%20movie%20poster%20blade%20runner%202049%20style%20futuristic%20city%20neon%20lights%20cyberpunk%20wide%20cinematic&image_size=landscape_16_9",
                Title = "银翼杀手 2049",
                Director = "导演: 丹尼斯·维伦纽瓦",
                ReleaseDate = "上映日期: 2017-10-06",
                Overview = "在2049年，新一代银翼杀手K发现了一个深藏已久的秘密，这个秘密可能会使人类与复制人之间的战争再次爆发。他必须找到失踪多年的前银翼杀手瑞克·戴克。"
            },
            new MovieWallpaperData
            {
                PosterUrl = "https://trae-api-cn.mchost.guru/api/ide/v1/text_to_image?prompt=sci-fi%20movie%20poster%20interstellar%20black%20hole%20space%20exploration%20epic%20cinematic%20wide%20landscape&image_size=landscape_16_9",
                Title = "星际穿越",
                Director = "导演: 克里斯托弗·诺兰",
                ReleaseDate = "上映日期: 2014-11-07",
                Overview = "在不远的未来，地球环境恶化，人类面临灭绝的危机。宇航员库珀穿越虫洞，进入未知的星际空间，寻找人类新的家园。影片探讨了爱、时间和牺牲的主题。"
            },
            new MovieWallpaperData
            {
                PosterUrl = "https://trae-api-cn.mchost.guru/api/ide/v1/text_to_image?prompt=sci-fi%20movie%20poster%20dune%20desert%20planet%20spice%20harvest%20epic%20sci-fi%20cinematic%20wide&image_size=landscape_16_9",
                Title = "沙丘",
                Director = "导演: 丹尼斯·维伦纽瓦",
                ReleaseDate = "上映日期: 2021-10-22",
                Overview = "遥远的未来，人类帝国依赖一种名为\"香料\"的珍贵物质。厄拉科斯星球是唯一的香料产地，多个家族为此展开争夺。年轻的保罗·亚崔迪必须在沙漠中生存并崛起。"
            },
            new MovieWallpaperData
            {
                PosterUrl = "https://trae-api-cn.mchost.guru/api/ide/v1/text_to_image?prompt=sci-fi%20movie%20poster%20inception%20dream%20within%20dream%20city%20folding%20mind%20bending%20cinematic%20wide&image_size=landscape_16_9",
                Title = "盗梦空间",
                Director = "导演: 克里斯托弗·诺兰",
                ReleaseDate = "上映日期: 2010-09-01",
                Overview = "道姆·柯布是一名专门从事梦境潜入的盗贼，他能够进入目标的潜意识窃取机密。现在他面临一个几乎不可能完成的任务：在目标的潜意识中植入一个想法。"
            },
            new MovieWallpaperData
            {
                PosterUrl = "https://trae-api-cn.mchost.guru/api/ide/v1/text_to_image?prompt=sci-fi%20movie%20poster%20arrival%20alien%20spaceship%20first%20contact%20linguistics%20mysterious%20cinematic%20wide&image_size=landscape_16_9",
                Title = "降临",
                Director = "导演: 丹尼斯·维伦纽瓦",
                ReleaseDate = "上映日期: 2016-11-11",
                Overview = "12艘外星飞船突然降临地球，语言学家露易丝·班克斯被派去与外星人交流。随着她逐渐理解外星人的语言，她开始感知到时间的非线性本质。"
            }
        };
    }

    private void ShowCurrentMovie()
    {
        var movie = _movieWallpapers[_currentIndex];
        PosterImage.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(movie.PosterUrl));
        MovieTitle.Text = movie.Title;
        MovieDirector.Text = movie.Director;
        MovieReleaseDate.Text = movie.ReleaseDate;
        MovieOverview.Text = movie.Overview;
    }

    private void FadeTransition(Action action)
    {
        var fadeOut = (Storyboard)Resources["FadeOutAnimation"];
        fadeOut.Completed += (s, e) =>
        {
            action();
            var fadeIn = (Storyboard)Resources["FadeInAnimation"];
            fadeIn.Begin(MainGrid);
        };
        fadeOut.Begin(MainGrid);
    }

    private void PrevButton_Click(object sender, RoutedEventArgs e)
    {
        _currentIndex = (_currentIndex - 1 + _movieWallpapers.Count) % _movieWallpapers.Count;
        FadeTransition(ShowCurrentMovie);
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        _currentIndex = (_currentIndex + 1) % _movieWallpapers.Count;
        FadeTransition(ShowCurrentMovie);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        HideWallpaper();
    }

    private void Window_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // 鼠标点击壁纸 → 隐藏（用户有操作）
        HideWallpaper();
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // 键盘按键 → 隐藏壁纸（用户有操作）
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            HideWallpaper();
        }
        else if (e.Key == System.Windows.Input.Key.Left)
        {
            PrevButton_Click(null, null);
        }
        else if (e.Key == System.Windows.Input.Key.Right)
        {
            NextButton_Click(null, null);
        }
    }

    private void Window_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // 鼠标在壁纸上移动不隐藏（允许用户查看壁纸）
        // 系统级鼠标/键盘活动由 CheckTimer_Tick 中的 GetSystemIdleTimeMs 检测
    }
}
