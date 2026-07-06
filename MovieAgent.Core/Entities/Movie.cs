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

    /// <summary>IMDB电影数据库中的ID</summary>
    [MaxLength(50)]
    public string? ImdbId { get; set; }

    /// <summary>电影标题（必填）</summary>
    [Required, MaxLength(500)]
    public string Title { get; set; } = string.Empty;

    /// <summary>原始电影标题（非中文电影）</summary>
    [MaxLength(500)]
    public string? OriginalTitle { get; set; }

    /// <summary>电影简介/剧情概述</summary>
    [MaxLength(4000)]
    public string? Overview { get; set; }

    /// <summary>电影标语/ tagline</summary>
    [MaxLength(500)]
    public string? Tagline { get; set; }

    /// <summary>海报图片路径（TMDB CDN）</summary>
    [MaxLength(500)]
    public string? PosterPath { get; set; }

    /// <summary>背景图片路径（TMDB CDN）</summary>
    [MaxLength(500)]
    public string? BackdropPath { get; set; }

    /// <summary>上映日期</summary>
    public DateTime? ReleaseDate { get; set; }

    /// <summary>电影上映年份</summary>
    public int? ReleaseYear { get; set; }

    /// <summary>TMDB评分（1-10分）</summary>
    public double? Rating { get; set; }

    /// <summary>TMDB投票数量</summary>
    public int? VoteCount { get; set; }

    /// <summary>电影人气指数</summary>
    public double? Popularity { get; set; }

    /// <summary>电影时长（分钟）</summary>
    public int? Runtime { get; set; }

    /// <summary>
    /// 电影类型（JSON格式数组，如 ["Action", "Sci-Fi"]）
    /// </summary>
    [MaxLength(1000)]
    public string? Genres { get; set; }

    /// <summary>官方网站</summary>
    [MaxLength(500)]
    public string? Homepage { get; set; }

    /// <summary>电影状态 (Released, Post Production, etc.)</summary>
    [MaxLength(50)]
    public string? Status { get; set; }

    /// <summary>是否成人内容</summary>
    public bool IsAdult { get; set; }

    /// <summary>是否为视频内容（非电影，如预告片）</summary>
    public bool IsVideo { get; set; }

    /// <summary>电影所属系列/集合</summary>
    [MaxLength(500)]
    public string? BelongsToCollection { get; set; }

    /// <summary>制作预算（美元）</summary>
    public long? Budget { get; set; }

    /// <summary>票房收入（美元）</summary>
    public long? Revenue { get; set; }

    /// <summary>原始语言代码</summary>
    [MaxLength(10)]
    public string? OriginalLanguage { get; set; }

    /// <summary>制片公司（JSON格式数组）</summary>
    [MaxLength(2000)]
    public string? ProductionCompanies { get; set; }

    /// <summary>制片国家/地区（JSON格式数组）</summary>
    [MaxLength(1000)]
    public string? ProductionCountries { get; set; }

    /// <summary>上映地区（JSON格式数组）</summary>
    [MaxLength(1000)]
    public string? OriginCountry { get; set; }

    /// <summary>电影关键词（JSON格式数组）</summary>
    [MaxLength(2000)]
    public string? Keywords { get; set; }

    /// <summary>电影别名（JSON格式数组）</summary>
    [MaxLength(2000)]
    public string? AlternativeTitles { get; set; }

    /// <summary>---------------------------------------</summary>
    /// 本地文件信息
    /// <summary>---------------------------------------</summary>

    /// <summary>本地文件路径（必填，支持UNC路径或本地路径）</summary>
    [Required, MaxLength(2000)]
    public string FilePath { get; set; } = string.Empty;

    /// <summary>文件大小（字节）</summary>
    public long FileSize { get; set; }

    /// <summary>视频编码格式（如 H264, HEVC, AV1）</summary>
    [MaxLength(50)]
    public string? VideoCodec { get; set; }

    /// <summary>视频格式（如 NV12, YUV420P, P010）</summary>
    [MaxLength(50)]
    public string? VideoFormat { get; set; }

    /// <summary>视频比特率（bps）</summary>
    public long? VideoBitrate { get; set; }

    /// <summary>帧率</summary>
    public double? FrameRate { get; set; }

    /// <summary>音频编码格式（如 AAC, DTS, AC3, EAC3）</summary>
    [MaxLength(100)]
    public string? AudioCodec { get; set; }

    /// <summary>音频通道（如 5.1, 7.1, Stereo）</summary>
    [MaxLength(50)]
    public string? AudioChannels { get; set; }

    /// <summary>音频比特率（bps）</summary>
    public long? AudioBitrate { get; set; }

    /// <summary>音频语言（如 zh, en, ja）</summary>
    [MaxLength(200)]
    public string? AudioLanguages { get; set; }

    /// <summary>分辨率（如 1920x1080, 3840x2160）</summary>
    [MaxLength(20)]
    public string? Resolution { get; set; }

    /// <summary>视频宽度（像素）</summary>
    public int? Width { get; set; }

    /// <summary>视频高度（像素）</summary>
    public int? Height { get; set; }

    /// <summary>宽高比（如 2.35:1, 16:9）</summary>
    [MaxLength(20)]
    public string? AspectRatio { get; set; }

    /// <summary>HDR类型：HDR10 / DolbyVision / HLG / SDR</summary>
    [MaxLength(50)]
    public string? HdrType { get; set; }

    /// <summary>色彩空间（如 BT.709, BT.2020）</summary>
    [MaxLength(50)]
    public string? ColorSpace { get; set; }

    /// <summary>色深（如 8bit, 10bit）</summary>
    [MaxLength(20)]
    public string? BitDepth { get; set; }

    /// <summary>---------------------------------------</summary>
    /// 演职人员
    /// <summary>---------------------------------------</summary>

    /// <summary>导演</summary>
    [MaxLength(500)]
    public string? Director { get; set; }

    [MaxLength(500)]
    public string? DirectorTmdbId { get; set; }

    /// <summary>编剧</summary>
    [MaxLength(2000)]
    public string? Writer { get; set; }

    [MaxLength(2000)]
    public string? WriterTmdbIds { get; set; }

    /// <summary>演员列表</summary>
    [MaxLength(4000)]
    public string? Cast { get; set; }

    [MaxLength(4000)]
    public string? CastTmdbIds { get; set; }

    [MaxLength(2000)]
    public string? ProductionCompanyIds { get; set; }

    /// <summary>---------------------------------------</summary>
    /// 其他信息
    /// <summary>---------------------------------------</summary>

    /// <summary>影片语言（逗号分隔）</summary>
    [MaxLength(200)]
    public string? Language { get; set; }

    /// <summary>国籍</summary>
    [MaxLength(200)]
    public string? Country { get; set; }

    /// <summary>发布组/压制组名称</summary>
    [MaxLength(200)]
    public string? ReleaseGroup { get; set; }

    /// <summary>字幕格式（如 SRT, ASS, PGS）</summary>
    [MaxLength(200)]
    public string? SubtitleFormats { get; set; }

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

    /// <summary>---------------------------------------</summary>
    /// 播放状态
    /// <summary>---------------------------------------</summary>

    /// <summary>是否已观看</summary>
    public bool IsWatched { get; set; }

    /// <summary>用户评分（1-5星）</summary>
    public int? UserRating { get; set; }

    /// <summary>最后观看时间</summary>
    public DateTime? WatchedAt { get; set; }

    /// <summary>观看进度（秒）</summary>
    public double? PlaybackPosition { get; set; }

    /// <summary>---------------------------------------</summary>
    /// 电视剧相关
    /// <summary>---------------------------------------</summary>

    /// <summary>是否为电视剧</summary>
    public bool IsTVSeries { get; set; }

    /// <summary>季号</summary>
    public int? SeasonNumber { get; set; }

    /// <summary>集号</summary>
    public int? EpisodeNumber { get; set; }

    /// <summary>---------------------------------------</summary>
    /// 向量数据库
    /// <summary>---------------------------------------</summary>

    /// <summary>向量嵌入文本（用于 AI 检索）</summary>
    [MaxLength(10000)]
    public string? EmbeddingText { get; set; }

    /// <summary>向量嵌入时间</summary>
    public DateTime? EmbeddingAt { get; set; }
}
