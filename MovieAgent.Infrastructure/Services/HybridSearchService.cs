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
    
    // 支持的分隔符列表
    private static readonly char[] Separators = { ' ', ',', '，', '、', ';', '；', '|', '\\', '/', '\t' };

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
            // 拆分关键词
            var keywords = SplitKeywords(query);
            
            if (keywords.Count > 1)
            {
                Debug.WriteLine($"[HybridSearch] Query '{query}' split into {keywords.Count} keywords: {string.Join(", ", keywords)}");
                // 多关键词搜索 - 合并结果
                return await SearchWithMultipleKeywords(keywords, filter, topK);
            }
            
            // 单关键词搜索
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

    /// <summary>
    /// 拆分关键词 - 支持多种分隔符：空格、逗号、顿号等
    /// </summary>
    private List<string> SplitKeywords(string query)
    {
        var keywords = query.Split(Separators, StringSplitOptions.RemoveEmptyEntries)
                           .Select(k => k.Trim())
                           .Where(k => !string.IsNullOrWhiteSpace(k))
                           .Distinct(StringComparer.OrdinalIgnoreCase)
                           .ToList();
        return keywords;
    }

    /// <summary>
    /// 多关键词搜索 - 合并所有关键词的搜索结果
    /// </summary>
    private async Task<List<Movie>> SearchWithMultipleKeywords(List<string> keywords, MovieFilter? filter, int topK)
    {
        var allVectorResults = new List<VectorSearchResult>();
        var allKeywordResults = new List<Movie>();

        // 并行搜索每个关键词
        foreach (var keyword in keywords)
        {
            var vectorResults = await _vectorDb.SearchAsync(keyword, InitialRecall);
            var keywordResults = await _movieRepo.SearchAsync(keyword);
            
            allVectorResults.AddRange(vectorResults);
            allKeywordResults.AddRange(keywordResults);
        }

        // 合并结果（去重并保持排序）
        var mergedResults = await MergeResultsWithRRF(allVectorResults, allKeywordResults, filter, topK);
        
        await _cacheService.CacheSemanticSearchAsync(string.Join(" ", keywords), mergedResults);
        
        return mergedResults;
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
        
        // 关键修复：根据 topMovieIds 的顺序重新排列电影，保持 RRF 评分排序
        var filteredMovies = new List<Movie>();
        foreach (var movieId in topMovieIds)
        {
            var movie = allMovies.FirstOrDefault(m => m.Id == movieId);
            if (movie != null)
            {
                filteredMovies.Add(movie);
            }
        }

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