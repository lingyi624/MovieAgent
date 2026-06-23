using MovieAgent.Core.Entities;
using MovieAgent.Core.Interfaces;
using System.Diagnostics;

namespace MovieAgent.Infrastructure.Services;

/// <summary>
/// 统一的电影更新服务
/// 负责协调数据库更新和向量数据库同步
/// </summary>
public interface IMovieUpdateService
{
    /// <summary>
    /// 更新电影并同步向量数据库
    /// </summary>
    /// <param name="movie">电影实体</param>
    /// <returns>更新后的电影</returns>
    Task<Movie> UpdateMovieWithVectorAsync(Movie movie);

    /// <summary>
    /// 根据标题更新电影元数据（保留用户数据）
    /// </summary>
    /// <param name="movieId">电影ID</param>
    /// <param name="newTitle">新标题</param>
    /// <returns>是否更新成功</returns>
    Task<bool> UpdateMovieByTitleAsync(int movieId, string newTitle);

    /// <summary>
    /// 批量更新向量数据库
    /// </summary>
    /// <param name="movieIds">电影ID列表</param>
    /// <returns>更新数量</returns>
    Task<int> BatchUpdateVectorsAsync(List<int> movieIds);

    /// <summary>
    /// 重新生成所有电影的向量
    /// </summary>
    Task RegenerateAllVectorsAsync();
}

public class MovieUpdateService : IMovieUpdateService
{
    private readonly IMovieRepository _movieRepo;
    private readonly ITmdbService _tmdbService;
    private readonly IVectorDatabaseService? _vectorDb;

    public MovieUpdateService(IMovieRepository movieRepo, ITmdbService tmdbService, IVectorDatabaseService? vectorDb = null)
    {
        _movieRepo = movieRepo;
        _tmdbService = tmdbService;
        _vectorDb = vectorDb;
    }

    /// <summary>
    /// 更新电影并同步向量数据库
    /// </summary>
    public async Task<Movie> UpdateMovieWithVectorAsync(Movie movie)
    {
        // 更新数据库
        var updatedMovie = await _movieRepo.UpdateAsync(movie);

        // 同步向量数据库
        if (_vectorDb != null && movie.Id > 0)
        {
            try
            {
                await UpdateVectorDatabaseAsync(updatedMovie);
                Debug.WriteLine($"[MovieUpdateService] Vector updated for movie: {updatedMovie.Title}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MovieUpdateService] Failed to update vector for {updatedMovie.Title}: {ex.Message}");
            }
        }

        return updatedMovie;
    }

    /// <summary>
    /// 根据标题更新电影元数据（保留用户数据）
    /// </summary>
    public async Task<bool> UpdateMovieByTitleAsync(int movieId, string newTitle)
    {
        // 获取现有电影记录
        var existingMovie = await _movieRepo.GetByIdAsync(movieId);
        if (existingMovie == null)
        {
            Debug.WriteLine($"[MovieUpdateService] Movie not found: {movieId}");
            return false;
        }

        // 保存用户数据（需要保留的字段）
        var userRating = existingMovie.UserRating;
        var isFavorite = existingMovie.IsFavorite;
        var isWatched = existingMovie.IsWatched;
        var watchedAt = existingMovie.WatchedAt;
        var playbackPosition = existingMovie.PlaybackPosition;
        var tags = existingMovie.Tags;
        var createdAt = existingMovie.CreatedAt;

        // 使用新标题搜索TMDB元数据
        var searchResult = await _tmdbService.SearchMovieAsync(newTitle);
        if (searchResult == null)
        {
            Debug.WriteLine($"[MovieUpdateService] TMDB search failed for: {newTitle}");
            return false;
        }

        // 更新元数据（保留用户数据）
        existingMovie.Title = searchResult.Title;
        existingMovie.OriginalTitle = searchResult.OriginalTitle;
        existingMovie.Overview = searchResult.Overview;
        existingMovie.Tagline = searchResult.Tagline;
        existingMovie.PosterPath = searchResult.PosterPath;
        existingMovie.BackdropPath = searchResult.BackdropPath;
        existingMovie.ReleaseDate = searchResult.ReleaseDate;
        if (searchResult.ReleaseYear.HasValue)
            existingMovie.ReleaseYear = searchResult.ReleaseYear;
        if (searchResult.Rating.HasValue)
            existingMovie.Rating = searchResult.Rating;
        if (searchResult.VoteCount.HasValue)
            existingMovie.VoteCount = searchResult.VoteCount;
        if (searchResult.Popularity.HasValue)
            existingMovie.Popularity = searchResult.Popularity;
        if (searchResult.Runtime.HasValue)
            existingMovie.Runtime = searchResult.Runtime;
        existingMovie.Genres = System.Text.Json.JsonSerializer.Serialize(searchResult.Genres);
        existingMovie.Homepage = searchResult.Homepage;
        existingMovie.Status = searchResult.Status;
        existingMovie.IsAdult = searchResult.IsAdult;
        existingMovie.BelongsToCollection = searchResult.BelongsToCollection;
        if (searchResult.Budget.HasValue)
            existingMovie.Budget = searchResult.Budget;
        if (searchResult.Revenue.HasValue)
            existingMovie.Revenue = searchResult.Revenue;
        existingMovie.OriginalLanguage = searchResult.OriginalLanguage;
        existingMovie.ProductionCompanies = System.Text.Json.JsonSerializer.Serialize(searchResult.ProductionCompanies);
        existingMovie.ProductionCountries = System.Text.Json.JsonSerializer.Serialize(searchResult.Countries);
        existingMovie.OriginCountry = searchResult.Countries != null && searchResult.Countries.Any() 
            ? string.Join(", ", searchResult.Countries) : null;
        existingMovie.Keywords = System.Text.Json.JsonSerializer.Serialize(searchResult.Keywords);
        existingMovie.Director = searchResult.Director;
        existingMovie.Writer = searchResult.Writer;
        existingMovie.Cast = searchResult.Cast;
        existingMovie.Language = searchResult.Languages != null && searchResult.Languages.Any() 
            ? string.Join(", ", searchResult.Languages) : null;
        existingMovie.Country = searchResult.Countries != null && searchResult.Countries.Any() 
            ? string.Join(", ", searchResult.Countries) : null;
        existingMovie.ImdbId = searchResult.ImdbId;

        // 恢复用户数据
        existingMovie.UserRating = userRating;
        existingMovie.IsFavorite = isFavorite;
        existingMovie.IsWatched = isWatched;
        existingMovie.WatchedAt = watchedAt;
        existingMovie.PlaybackPosition = playbackPosition;
        existingMovie.Tags = tags;
        existingMovie.CreatedAt = createdAt;
        existingMovie.UpdatedAt = DateTime.UtcNow;
        var text = BuildEmbeddingText(existingMovie);
        if (string.IsNullOrWhiteSpace(text))
        {
            existingMovie.EmbeddingText = text;
            existingMovie.UpdatedAt = DateTime.UtcNow;
        }
        // 更新数据库和向量
        await UpdateMovieWithVectorAsync(existingMovie);

        Debug.WriteLine($"[MovieUpdateService] Movie updated by title: {newTitle} -> {existingMovie.Title}");
        return true;
    }

    /// <summary>
    /// 批量更新向量数据库
    /// </summary>
    public async Task<int> BatchUpdateVectorsAsync(List<int> movieIds)
    {
        int updatedCount = 0;

        foreach (var movieId in movieIds)
        {
            var movie = await _movieRepo.GetByIdAsync(movieId);
            if (movie != null)
            {
                try
                {
                    await UpdateVectorDatabaseAsync(movie);
                    updatedCount++;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MovieUpdateService] Failed to update vector for movie {movieId}: {ex.Message}");
                }
            }
        }

        return updatedCount;
    }

    /// <summary>
    /// 重新生成所有电影的向量
    /// </summary>
    public async Task RegenerateAllVectorsAsync()
    {
        var allMovies = await _movieRepo.GetAllAsync();
        Debug.WriteLine($"[MovieUpdateService] Regenerating vectors for {allMovies.Count} movies...");

        foreach (var movie in allMovies)
        {
            try
            {
                await UpdateVectorDatabaseAsync(movie);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MovieUpdateService] Failed to regenerate vector for {movie.Title}: {ex.Message}");
            }
        }

        Debug.WriteLine("[MovieUpdateService] Vector regeneration completed");
    }

    /// <summary>
    /// 更新向量数据库
    /// </summary>
    private async Task UpdateVectorDatabaseAsync(Movie movie)
    {
        if (_vectorDb == null || movie.Id == 0)
            return;

        try
        {
            var text = BuildEmbeddingText(movie);
            if (string.IsNullOrWhiteSpace(text))
            {
                Debug.WriteLine($"[MovieUpdateService] No text for embedding: {movie.Title}");
                return;
            }

            // 使用 search_document 前缀生成文档向量
            var vector = await _vectorDb.GenerateDocumentEmbeddingAsync(text);
            if (vector == null || vector.Length == 0)
            {
                Debug.WriteLine($"[MovieUpdateService] Empty embedding for: {movie.Title}");
                return;
            }

            await _vectorDb.AddMovieAsync(movie.Id, vector, movie.Title, movie.Overview);
            Debug.WriteLine($"[MovieUpdateService] Added to vector DB: {movie.Title} (ID: {movie.Id})");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MovieUpdateService] UpdateVectorDatabaseAsync failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 构建向量嵌入文本
    /// </summary>
    private string BuildEmbeddingText(Movie movie)
    {
        var parts = new List<string>();

        // 基本信息
        if (!string.IsNullOrEmpty(movie.Title))
            parts.Add($"电影标题：{movie.Title}");
        if (!string.IsNullOrEmpty(movie.OriginalTitle))
            parts.Add($"原名：{movie.OriginalTitle}");
        if (!string.IsNullOrEmpty(movie.Tagline))
            parts.Add($"标语：{movie.Tagline}"); 
        if (movie.ReleaseYear.HasValue)
            parts.Add($"上映年份：{movie.ReleaseYear}年");
        if (movie.ReleaseDate.HasValue)
            parts.Add($"上映日期：{movie.ReleaseDate:yyyy-MM-dd}");

        // 评分和人气
        if (movie.Rating.HasValue)
            parts.Add($"评分：{movie.Rating}分");
        if (movie.VoteCount.HasValue)
            parts.Add($"投票数：{movie.VoteCount}");
        if (movie.Popularity.HasValue)
            parts.Add($"人气：{movie.Popularity}");

        // 内容信息
        if (!string.IsNullOrEmpty(movie.Overview))
            parts.Add($"简介：{movie.Overview}");
        if (!string.IsNullOrEmpty(movie.Genres))
        {
            try
            {
                var genres = System.Text.Json.JsonSerializer.Deserialize<List<string>>(movie.Genres);
                if (genres != null && genres.Any())
                    parts.Add($"类型：{string.Join("、", genres)}");
            }
            catch { parts.Add($"类型：{movie.Genres}"); }
        }
        if (movie.Runtime.HasValue)
            parts.Add($"时长：{movie.Runtime}分钟");

        // 财务信息
        if (movie.Budget.HasValue && movie.Budget > 0)
            parts.Add($"预算：{movie.Budget:N0}美元");
        if (movie.Revenue.HasValue && movie.Revenue > 0)
            parts.Add($"票房：{movie.Revenue:N0}美元");

        // 演职人员
        if (!string.IsNullOrEmpty(movie.Director))
            parts.Add($"导演：{movie.Director}");
        if (!string.IsNullOrEmpty(movie.Writer))
            parts.Add($"编剧：{movie.Writer}");
        if (!string.IsNullOrEmpty(movie.Cast))
            parts.Add($"演员：{movie.Cast}");

        // 制片信息
        if (!string.IsNullOrEmpty(movie.Country))
            parts.Add($"国家：{movie.Country}");
        if (!string.IsNullOrEmpty(movie.Language))
            parts.Add($"语言：{movie.Language}");
        if (!string.IsNullOrEmpty(movie.ProductionCompanies))
        {
            try
            {
                var companies = System.Text.Json.JsonSerializer.Deserialize<List<string>>(movie.ProductionCompanies);
                if (companies != null && companies.Any())
                    parts.Add($"制片公司：{string.Join("、", companies.Take(3))}");
            }
            catch { }
        }

        // 关键词
        if (!string.IsNullOrEmpty(movie.Keywords))
        {
            try
            {
                var keywords = System.Text.Json.JsonSerializer.Deserialize<List<string>>(movie.Keywords);
                if (keywords != null && keywords.Any())
                    parts.Add($"关键词：{string.Join("、", keywords.Take(5))}");
            }
            catch { }
        }

        return string.Join("。", parts);
    }
}