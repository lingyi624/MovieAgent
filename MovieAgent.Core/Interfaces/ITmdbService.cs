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

        /// <summary>电影标语/tagline</summary>
        public string? Tagline { get; set; }

        /// <summary>海报图片路径</summary>
        public string? PosterPath { get; set; }

        /// <summary>背景图片路径</summary>
        public string? BackdropPath { get; set; }

        /// <summary>上映日期</summary>
        public DateTime? ReleaseDate { get; set; }

        /// <summary>上映年份</summary>
        public int? ReleaseYear { get; set; }

        /// <summary>TMDB评分（1-10分）</summary>
        public double? Rating { get; set; }

        /// <summary>投票数量</summary>
        public int? VoteCount { get; set; }

        /// <summary>人气指数</summary>
        public double? Popularity { get; set; }

        /// <summary>电影类型列表</summary>
        public List<string> Genres { get; set; } = new();

        /// <summary>电影时长（分钟）</summary>
        public int? Runtime { get; set; }

        /// <summary>导演</summary>
        public string? Director { get; set; }

        public string? DirectorTmdbId { get; set; }

        /// <summary>编剧列表</summary>
        public string? Writer { get; set; }

        public List<string> WriterTmdbIds { get; set; } = new();

        /// <summary>主演列表</summary>
        public string? Cast { get; set; }

        public List<string> CastTmdbIds { get; set; } = new();

        /// <summary>制片国家/地区列表</summary>
        public List<string> Countries { get; set; } = new();

        /// <summary>语言列表</summary>
        public List<string> Languages { get; set; } = new();

        /// <summary>官方网站</summary>
        public string? Homepage { get; set; }

        /// <summary>电影状态 (Released, Post Production, etc.)</summary>
        public string? Status { get; set; }

        /// <summary>是否成人内容</summary>
        public bool IsAdult { get; set; }

        /// <summary>所属系列/集合</summary>
        public string? BelongsToCollection { get; set; }

        /// <summary>制作预算（美元）</summary>
        public long? Budget { get; set; }

        /// <summary>票房收入（美元）</summary>
        public long? Revenue { get; set; }

        /// <summary>原始语言代码</summary>
        public string? OriginalLanguage { get; set; }

        /// <summary>制片公司列表</summary>
        public List<string> ProductionCompanies { get; set; } = new();

        public List<string> ProductionCompanyIds { get; set; } = new();

        /// <summary>关键词列表</summary>
        public List<string> Keywords { get; set; } = new();

        /// <summary>IMDB ID</summary>
        public string? ImdbId { get; set; }
    }

    public class TmdbPersonResult
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? OriginalName { get; set; }
        public string? Biography { get; set; }
        public string? ProfilePath { get; set; }
        public DateTime? Birthday { get; set; }
        public DateTime? Deathday { get; set; }
        public string? PlaceOfBirth { get; set; }
        public int? Gender { get; set; }
        public string? KnownForDepartment { get; set; }
        public double? Popularity { get; set; }
        public List<string> AlsoKnownAs { get; set; } = new();
        public List<string> KnownForTitles { get; set; } = new();
        public List<PersonCredit> Credits { get; set; } = new();
    }

    public class PersonCredit
    {
        public string? Title { get; set; }
        public string? Character { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public string? PosterPath { get; set; }
        public string? TmdbId { get; set; }
    }

    public class TmdbCompanyResult
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? LogoPath { get; set; }
        public string? OriginCountry { get; set; }
        public string? Headquarters { get; set; }
        public string? Homepage { get; set; }
        public string? ParentCompany { get; set; }
        public List<CompanyMovie> MovieList { get; set; } = new();
        public List<string> PersonList { get; set; } = new();
    }

    public class CompanyMovie
    {
        public string? Title { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public string? PosterPath { get; set; }
        public string? TmdbId { get; set; }
    }
