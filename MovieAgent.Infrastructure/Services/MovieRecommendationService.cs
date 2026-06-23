using MovieAgent.Core.Entities;
using MovieAgent.Core.Interfaces;
using MovieAgent.Core.Models;

namespace MovieAgent.Infrastructure.Services;

/// <summary>
/// 电影推荐服务 - 基于向量数据库的相似电影推荐
/// </summary>
public interface IMovieRecommendationService
{
    Task<List<MovieRecommendation>> GetSimilarMoviesAsync(int movieId, int topK = 6);
    Task<List<MovieRecommendation>> GetPersonalRecommendationsAsync(int topK = 10);
}

public class MovieRecommendation
{
    public int MovieId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Overview { get; set; }
    public string? PosterPath { get; set; }
    public int? ReleaseYear { get; set; }
    public double? Rating { get; set; }
    public double Similarity { get; set; }
    public string? Reason { get; set; }
}

public class MovieRecommendationService : IMovieRecommendationService
{
    private readonly IVectorDatabaseService _vectorDb;
    private readonly IMovieRepository _movieRepo;

    public MovieRecommendationService(IVectorDatabaseService vectorDb, IMovieRepository movieRepo)
    {
        _vectorDb = vectorDb;
        _movieRepo = movieRepo;
    }

    public async Task<List<MovieRecommendation>> GetSimilarMoviesAsync(int movieId, int topK = 6)
    {
        try
        {
            // 1. 获取目标电影
            var targetMovie = await _movieRepo.GetByIdAsync(movieId);
            if (targetMovie == null) return new List<MovieRecommendation>();

            // 2. 生成查询向量
            var queryText = $"{targetMovie.Title} {targetMovie.Director} {targetMovie.Cast} {targetMovie.Overview ?? ""} {targetMovie.Genres ?? ""}";
            var queryVector = await _vectorDb.GenerateEmbeddingAsync(queryText);

            // 3. 向量检索
            var results = await _vectorDb.SearchByVectorAsync(queryVector, topK + 1);

            // 4. 过滤掉自身，转换为推荐对象
            var recommendations = new List<MovieRecommendation>();
            foreach (var result in results.Where(r => r.MovieId != movieId).Take(topK))
            {
                var movie = await _movieRepo.GetByIdAsync(result.MovieId);
                if (movie != null)
                {
                    recommendations.Add(new MovieRecommendation
                    {
                        MovieId = movie.Id,
                        Title = movie.Title,
                        Overview = movie.Overview,
                        PosterPath = movie.PosterPath,
                        ReleaseYear = movie.ReleaseYear,
                        Rating = movie.Rating,
                        Similarity = result.Similarity,
                        Reason = GenerateReason(targetMovie, movie, result.Similarity)
                    });
                }
            }

            // 如果向量检索没有有效结果，使用基于类型的降级方案
            if (!recommendations.Any(r => r.Similarity > 0))
            {
                recommendations = await GetSimilarMoviesByGenreAsync(targetMovie, topK);
            }

            return recommendations;
        }
        catch
        {
            // 异常情况下使用降级方案
            var targetMovie = await _movieRepo.GetByIdAsync(movieId);
            if (targetMovie != null)
            {
                return await GetSimilarMoviesByGenreAsync(targetMovie, topK);
            }
            return new List<MovieRecommendation>();
        }
    }

    private async Task<List<MovieRecommendation>> GetSimilarMoviesByGenreAsync(Movie targetMovie, int topK)
    {
        var allMovies = await _movieRepo.GetAllAsync();
        var targetGenres = ParseGenres(targetMovie.Genres);

        var recommendations = allMovies
            .Where(m => m.Id != targetMovie.Id)
            .Select(m => new
            {
                Movie = m,
                Score = CalculateGenreSimilarity(targetGenres, ParseGenres(m.Genres))
            })
            .Where(r => r.Score > 0)
            .OrderByDescending(r => r.Score)
            .Take(topK)
            .Select(r => new MovieRecommendation
            {
                MovieId = r.Movie.Id,
                Title = r.Movie.Title,
                Overview = r.Movie.Overview,
                PosterPath = r.Movie.PosterPath,
                ReleaseYear = r.Movie.ReleaseYear,
                Rating = r.Movie.Rating,
                Similarity = r.Score,
                Reason = GenerateReason(targetMovie, r.Movie, r.Score)
            })
            .ToList();

        return recommendations;
    }

    private List<string> ParseGenres(string? genresJson)
    {
        if (string.IsNullOrEmpty(genresJson))
            return new List<string>();

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(genresJson) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    private double CalculateGenreSimilarity(List<string> genres1, List<string> genres2)
    {
        if (!genres1.Any() || !genres2.Any())
            return 0;

        var intersection = genres1.Intersect(genres2, StringComparer.OrdinalIgnoreCase).Count();
        var union = genres1.Union(genres2, StringComparer.OrdinalIgnoreCase).Count();

        return union > 0 ? (double)intersection / union : 0;
    }

    public async Task<List<MovieRecommendation>> GetPersonalRecommendationsAsync(int topK = 10)
    {
        try
        {
            // 基于用户观影历史的个性化推荐
            var watchedMovies = await _movieRepo.GetUniqueMoviesAsync(new MovieFilter { IsWatched = true });
            if (!watchedMovies.Any()) return new List<MovieRecommendation>();

            // 取最近观看的5部电影作为推荐基础
            var recentWatched = watchedMovies
                .Where(m => m.WatchedAt.HasValue)
                .OrderByDescending(m => m.WatchedAt)
                .Take(5)
                .ToList();

            if (!recentWatched.Any())
                recentWatched = watchedMovies.Take(5).ToList();

            // 聚合所有相似电影
            var allRecommendations = new Dictionary<int, double>();
            foreach (var movie in recentWatched)
            {
                var similar = await GetSimilarMoviesAsync(movie.Id, 5);
                foreach (var s in similar)
                {
                    if (allRecommendations.ContainsKey(s.MovieId))
                        allRecommendations[s.MovieId] = Math.Max(allRecommendations[s.MovieId], s.Similarity);
                    else
                        allRecommendations[s.MovieId] = s.Similarity;
                }
            }

            // 过滤已观看，取Top K
            var watchedIds = watchedMovies.Select(m => m.Id).ToHashSet();
            var topRecommendations = allRecommendations
                .Where(kv => !watchedIds.Contains(kv.Key))
                .OrderByDescending(kv => kv.Value)
                .Take(topK)
                .ToList();

            var results = new List<MovieRecommendation>();
            foreach (var kv in topRecommendations)
            {
                var movie = await _movieRepo.GetByIdAsync(kv.Key);
                if (movie != null)
                {
                    results.Add(new MovieRecommendation
                    {
                        MovieId = movie.Id,
                        Title = movie.Title,
                        Overview = movie.Overview,
                        PosterPath = movie.PosterPath,
                        ReleaseYear = movie.ReleaseYear,
                        Rating = movie.Rating,
                        Similarity = kv.Value,
                        Reason = "基于您的观影历史推荐"
                    });
                }
            }
            return results;
        }
        catch
        {
            return new List<MovieRecommendation>();
        }
    }

    private static string GenerateReason(Movie source, Movie target, double similarity)
    {
        var reasons = new List<string>();
        
        // 共同类型
        if (!string.IsNullOrEmpty(source.Genres) && !string.IsNullOrEmpty(target.Genres))
        {
            try
            {
                var sourceGenres = System.Text.Json.JsonSerializer.Deserialize<List<string>>(source.Genres) ?? new();
                var targetGenres = System.Text.Json.JsonSerializer.Deserialize<List<string>>(target.Genres) ?? new();
                var common = sourceGenres.Intersect(targetGenres, StringComparer.OrdinalIgnoreCase).ToList();
                if (common.Any())
                    reasons.Add($"同属{string.Join("、", common.Take(2))}类型");
            }
            catch { }
        }

        // 年份相近
        if (source.ReleaseYear.HasValue && target.ReleaseYear.HasValue)
        {
            var diff = Math.Abs(source.ReleaseYear.Value - target.ReleaseYear.Value);
            if (diff <= 3)
                reasons.Add("同时期作品");
        }

        // 相似度
        var simPercent = (int)(similarity * 100);
        reasons.Add($"相似度 {simPercent}%");

        return string.Join(" · ", reasons);
    }
}
