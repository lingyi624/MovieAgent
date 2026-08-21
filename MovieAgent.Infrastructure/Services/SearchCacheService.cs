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
    // 每个缓存条目持有完整 Movie 列表（含海报字段），必须设上限防止单例长期持有导致内存膨胀
    private const int MaxCacheEntries = 200;
    private long _cacheHits;
    private long _cacheMisses;

    private void AddOrUpdateWithLimit(ConcurrentDictionary<string, CachedSearchResult> cache, string key, CachedSearchResult result)
    {
        cache[key] = result;
        // 超出容量上限时，清除过期项；仍超出则强制清空最早过期的一半
        if (cache.Count > MaxCacheEntries)
        {
            var expired = cache.Where(kv => kv.Value.ExpiresAt <= DateTime.UtcNow).Select(kv => kv.Key).ToList();
            foreach (var k in expired) cache.TryRemove(k, out _);
            if (cache.Count > MaxCacheEntries)
            {
                var toRemove = cache
                    .OrderBy(kv => kv.Value.ExpiresAt)
                    .Take(cache.Count - MaxCacheEntries)
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (var k in toRemove) cache.TryRemove(k, out _);
            }
        }
    }

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
        AddOrUpdateWithLimit(_queryCache, key, new CachedSearchResult
        {
            Movies = results.ToList(),
            ExpiresAt = DateTime.UtcNow.Add(_cacheDuration)
        });
        
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
        AddOrUpdateWithLimit(_semanticCache, key, new CachedSearchResult
        {
            Movies = results.ToList(),
            ExpiresAt = DateTime.UtcNow.Add(_cacheDuration)
        });
        
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