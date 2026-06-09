using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieAgent.Core.Entities;

/// <summary>
/// 电影实体类 - 表示电影库中的一部电影
/// </summary>
public class Movie
{
    /// <summary>电影唯一标识符（自增主键）</summary>
    [Key]
    public int Id { get; set; }

    /// <summary>TMDB电影数据库中的ID</summary>
    [MaxLength(50)]
    public string? TmdbId { get; set; }

    /// <summary>电影标题（必填）</summary>
    [Required, MaxLength(500)]
    public string Title { get; set; } = string.Empty;

    /// <summary>原始电影标题（非中文电影）</summary>
    [MaxLength(500)]
    public string? OriginalTitle { get; set; }

    /// <summary>电影简介/剧情概述</summary>
    [MaxLength(4000)]
    public string? Overview { get; set; }

    /// <summary>海报图片路径（TMDB CDN）</summary>
    [MaxLength(500)]
    public string? PosterPath { get; set; }

    /// <summary>背景图片路径（TMDB CDN）</summary>
    [MaxLength(500)]
    public string? BackdropPath { get; set; }

    /// <summary>电影上映年份</summary>
    public int? ReleaseYear { get; set; }

    /// <summary>TMDB评分（1-10分）</summary>
    public double? Rating { get; set; }

    /// <summary>电影时长（分钟）</summary>
    public int? Runtime { get; set; }

    /// <summary>
    /// 电影类型（JSON格式数组，如 ["Action", "Sci-Fi"]）
    /// </summary>
    [MaxLength(1000)]
    public string? Genres { get; set; }

    /// <summary>本地文件路径（必填，支持UNC路径或本地路径）</summary>
    [Required, MaxLength(2000)]
    public string FilePath { get; set; } = string.Empty;

    /// <summary>文件大小（字节）</summary>
    public long FileSize { get; set; }

    /// <summary>视频编码格式（如 H264, HEVC）</summary>
    [MaxLength(50)]
    public string? VideoCodec { get; set; }

    /// <summary>音频编码格式（如 AAC, DTS）</summary>
    [MaxLength(50)]
    public string? AudioCodec { get; set; }

    /// <summary>分辨率（如 1920x1080, 3840x2160）</summary>
    [MaxLength(20)]
    public string? Resolution { get; set; }

    /// <summary>是否已观看</summary>
    public bool IsWatched { get; set; }

    /// <summary>用户评分（1-5星）</summary>
    public int? UserRating { get; set; }

    /// <summary>最后观看时间</summary>
    public DateTime? WatchedAt { get; set; }

    /// <summary>是否收藏</summary>
    public bool IsFavorite { get; set; }

    /// <summary>记录创建时间（UTC）</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>最后更新时间（UTC）</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 自定义标签（JSON格式数组）
    /// </summary>
    [MaxLength(2000)]
    public string? Tags { get; set; }

    /// <summary>导演</summary>
    [MaxLength(500)]
    public string? Director { get; set; }

    /// <summary>演员列表</summary>
    [MaxLength(2000)]
    public string? Cast { get; set; }

    /// <summary>HDR类型：HDR10 / DolbyVision / SDR</summary>
    [MaxLength(50)]
    public string? HdrType { get; set; }
}
