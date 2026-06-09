using MovieAgent.Core.Entities;

namespace MovieAgent.Core.Interfaces;

/// <summary>
/// TMDB API服务接口 - 与The Movie Database API交互
/// 用于搜索电影、获取元数据、下载图片
/// </summary>
public interface ITmdbService
{
    /// <summary>
    /// 搜索电影
    /// </summary>
    /// <param name="title">电影标题</param>
    /// <param name="year">上映年份（可选，用于精确匹配）</param>
    /// <returns>搜索结果或null</returns>
    Task<TmdbSearchResult?> SearchMovieAsync(string title, int? year = null);

    /// <summary>
    /// 填充电影元数据 - 从TMDB获取完整信息
    /// </summary>
    /// <param name="movie">待填充的电影实体（需包含TmdbId）</param>
    /// <returns>填充后的电影实体</returns>
    Task<Movie?> FillMovieMetadataAsync(Movie movie);

    /// <summary>
    /// 下载电影海报图片
    /// </summary>
    /// <param name="posterPath">海报路径（来自TMDB）</param>
    /// <param name="size">图片尺寸（默认w500）</param>
    /// <returns>图片字节数据</returns>
    Task<byte[]?> DownloadPosterAsync(string posterPath, string size = "w500");

    /// <summary>
    /// 下载电影背景图片
    /// </summary>
    /// <param name="backdropPath">背景图路径（来自TMDB）</param>
    /// <param name="size">图片尺寸（默认w780）</param>
    /// <returns>图片字节数据</returns>
    Task<byte[]?> DownloadBackdropAsync(string backdropPath, string size = "w780");
}

/// <summary>
/// TMDB搜索结果 - 电影基本信息
/// </summary>
public class TmdbSearchResult
{
    /// <summary>TMDB电影ID</summary>
    public long Id { get; set; }

    /// <summary>电影标题</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>原始标题（非英语电影）</summary>
    public string? OriginalTitle { get; set; }

    /// <summary>电影简介</summary>
    public string? Overview { get; set; }

    /// <summary>海报图片路径</summary>
    public string? PosterPath { get; set; }

    /// <summary>背景图片路径</summary>
    public string? BackdropPath { get; set; }

    /// <summary>上映年份</summary>
    public int? ReleaseYear { get; set; }

    /// <summary>TMDB评分（1-10分）</summary>
    public double? Rating { get; set; }

    /// <summary>电影类型列表</summary>
    public List<string> Genres { get; set; } = new();

    /// <summary>电影时长（分钟）</summary>
    public int? Runtime { get; set; }

    /// <summary>导演</summary>
    public string? Director { get; set; }

    /// <summary>主演列表</summary>
    public string? Cast { get; set; }
}
