using System.Collections.Concurrent;
using System.Text.Json;
using MovieAgent.Core.Entities;
using MovieAgent.Core.Interfaces;

namespace MovieAgent.Infrastructure.Services;

public class SearchCacheService : ISearchCacheService
{
    private readonly ConcurrentDictionary<string, CachedSearchResult> _queryCache = new();
    private readonly ConcurrentDictionary<string, CachedSearchResult> _semanticCache = new();
    private readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(30);
    private long _cacheHits;
    private long _cacheMisses;

    public async Task<List<Movie>> GetCachedSearchAsync(string query)
    {
        var key = NormalizeQuery(query);
        
        if (_queryCache.TryGetValue(key, out var cached))
        {
            if (cached.ExpiresAt > DateTime.UtcNow)
            {
                _cacheHits++;
                return cached.Movies.ToList();
            }
            _queryCache.TryRemove(key, out _);
        }
        
        _cacheMisses++;
        return await Task.FromResult(new List<Movie>());
    }

    public Task CacheSearchAsync(string query, List<Movie> results)
    {
        var key = NormalizeQuery(query);
        _queryCache[key] = new CachedSearchResult
        {
            Movies = results.ToList(),
            ExpiresAt = DateTime.UtcNow.Add(_cacheDuration)
        };
        
        return Task.CompletedTask;
    }

    public async Task<List<Movie>> GetSemanticCachedSearchAsync(string query)
    {
        var key = NormalizeQuery(query);
        
        if (_semanticCache.TryGetValue(key, out var cached))
        {
            if (cached.ExpiresAt > DateTime.UtcNow)
            {
                _cacheHits++;
                return cached.Movies.ToList();
            }
            _semanticCache.TryRemove(key, out _);
        }
        
        _cacheMisses++;
        return await Task.FromResult(new List<Movie>());
    }

    public Task CacheSemanticSearchAsync(string query, List<Movie> results)
    {
        var key = NormalizeQuery(query);
        _semanticCache[key] = new CachedSearchResult
        {
            Movies = results.ToList(),
            ExpiresAt = DateTime.UtcNow.Add(_cacheDuration)
        };
        
        return Task.CompletedTask;
    }

    public Task ClearCacheAsync()
    {
        _queryCache.Clear();
        _semanticCache.Clear();
        return Task.CompletedTask;
    }

    public Task ClearExpiredCacheAsync()
    {
        var now = DateTime.UtcNow;
        foreach (var key in _queryCache.Keys.ToArray())
        {
            if (_queryCache[key].ExpiresAt <= now)
            {
                _queryCache.TryRemove(key, out _);
            }
        }
        
        foreach (var key in _semanticCache.Keys.ToArray())
        {
            if (_semanticCache[key].ExpiresAt <= now)
            {
                _semanticCache.TryRemove(key, out _);
            }
        }
        
        return Task.CompletedTask;
    }

    public long GetCacheHitCount() => _cacheHits;

    public long GetCacheMissCount() => _cacheMisses;

    public double GetCacheHitRate()
    {
        var total = _cacheHits + _cacheMisses;
        return total == 0 ? 0 : (double)_cacheHits / total * 100;
    }

    private string NormalizeQuery(string query)
    {
        return query?.Trim().ToLowerInvariant() ?? string.Empty;
    }

    private class CachedSearchResult
    {
        public List<Movie> Movies { get; set; } = new();
        public DateTime ExpiresAt { get; set; }
    }
}