using MovieAgent.Core.Entities;
using MovieAgent.Core.Interfaces;
using MovieAgent.Core.Models;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace MovieAgent.Infrastructure.Services;

public class HybridSearchService : IHybridSearchService
{
    private readonly IMovieRepository _movieRepo;
    private readonly IVectorDatabaseService _vectorDb;
    private readonly ISearchCacheService _cacheService;
    private const int RrfK = 60;
    private const int InitialRecall = 50;

    public HybridSearchService(IMovieRepository movieRepo, IVectorDatabaseService vectorDb, ISearchCacheService cacheService)
    {
        _movieRepo = movieRepo;
        _vectorDb = vectorDb;
        _cacheService = cacheService;
    }

    public async Task<List<Movie>> SearchAsync(string query, MovieFilter? filter = null, int topK = 10)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return await _movieRepo.GetUniqueMoviesAsync(filter);
        }

        var cachedResults = await _cacheService.GetSemanticCachedSearchAsync(query);
        if (cachedResults.Any())
        {
            Debug.WriteLine($"[HybridSearch] Cache hit for query: {query}");
            return ApplyFilter(cachedResults, filter, topK);
        }

        try
        {
            var vectorResultsTask = _vectorDb.SearchAsync(query, InitialRecall);
            var keywordResultsTask = _movieRepo.SearchAsync(query);

            await Task.WhenAll(vectorResultsTask, keywordResultsTask);

            var vectorResults = vectorResultsTask.Result;
            var keywordResults = keywordResultsTask.Result;

            var mergedResults = await MergeResultsWithRRF(vectorResults, keywordResults, filter, topK);
            
            await _cacheService.CacheSemanticSearchAsync(query, mergedResults);
            
            return mergedResults;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HybridSearch] Search failed: {ex.Message}");
            return await _movieRepo.SearchAsync(query);
        }
    }

    public async Task<List<Movie>> SearchWithMemoryAsync(string query, List<ChatMessage> history, MovieFilter? filter = null, int topK = 10)
    {
        var enhancedQuery = EnhanceQueryWithHistory(query, history);
        return await SearchAsync(enhancedQuery, filter, topK);
    }

    private string EnhanceQueryWithHistory(string query, List<ChatMessage> history)
    {
        if (history == null || history.Count == 0)
            return query;

        var preferences = new List<string>();
        foreach (var msg in history)
        {
            if (msg.User.Contains("喜欢", StringComparison.OrdinalIgnoreCase) || 
                msg.User.Contains("推荐", StringComparison.OrdinalIgnoreCase))
            {
                preferences.Add(msg.User);
            }
            if (!string.IsNullOrEmpty(msg.Agent))
            {
                preferences.Add(msg.Agent);
            }
        }

        if (preferences.Any())
        {
            var context = string.Join(" ", preferences.Take(5));
            return $"{query} 参考历史：{context}";
        }

        return query;
    }

    private async Task<List<Movie>> MergeResultsWithRRF(
        List<VectorSearchResult> vectorResults,
        List<Movie> keywordResults,
        MovieFilter? filter,
        int topK)
    {
        var scoreMap = new ConcurrentDictionary<int, double>();

        for (int i = 0; i < vectorResults.Count; i++)
        {
            double score = 1.0 / (RrfK + i + 1);
            scoreMap.AddOrUpdate(vectorResults[i].MovieId, score, (_, existing) => existing + score);
        }

        for (int i = 0; i < keywordResults.Count; i++)
        {
            double score = 1.0 / (RrfK + i + 1);
            scoreMap.AddOrUpdate(keywordResults[i].Id, score, (_, existing) => existing + score);
        }

        var scoredMovies = new List<(int MovieId, double Score)>();
        foreach (var kvp in scoreMap)
        {
            scoredMovies.Add((kvp.Key, kvp.Value));
        }

        scoredMovies.Sort((a, b) => b.Score.CompareTo(a.Score));

        var topMovieIds = scoredMovies.Take(InitialRecall).Select(s => s.MovieId).ToList();
        var allMovies = await _movieRepo.GetAllAsync();
        var filteredMovies = allMovies.Where(m => topMovieIds.Contains(m.Id)).ToList();

        return ApplyFilter(filteredMovies, filter, topK);
    }

    private List<Movie> ApplyFilter(List<Movie> movies, MovieFilter? filter, int topK)
    {
        if (filter == null)
            return movies.Take(topK).ToList();

        var result = movies.AsEnumerable();

        if (filter.Genres != null && filter.Genres.Any())
            result = result.Where(m => filter.Genres.Any(g => m.Genres?.Contains(g) == true));
        if (!string.IsNullOrEmpty(filter.Resolution))
            result = result.Where(m => m.Resolution == filter.Resolution);
        if (filter.MinRating.HasValue)
            result = result.Where(m => m.Rating >= filter.MinRating.Value);
        if (filter.MinYear.HasValue)
            result = result.Where(m => m.ReleaseYear >= filter.MinYear.Value);
        if (filter.MaxYear.HasValue)
            result = result.Where(m => m.ReleaseYear <= filter.MaxYear.Value);
        if (filter.IsWatched.HasValue)
            result = result.Where(m => m.IsWatched == filter.IsWatched.Value);
        if (filter.IsFavorite.HasValue)
            result = result.Where(m => m.IsFavorite == filter.IsFavorite.Value);

        return result.Take(topK).ToList();
    }
}