using MovieAgent.Core.Entities;

namespace MovieAgent.Core.Interfaces;

public interface ISearchCacheService
{
    Task<List<Movie>> GetCachedSearchAsync(string query);
    
    Task CacheSearchAsync(string query, List<Movie> results);
    
    Task<List<Movie>> GetSemanticCachedSearchAsync(string query);
    
    Task CacheSemanticSearchAsync(string query, List<Movie> results);
    
    Task ClearCacheAsync();
    
    Task ClearExpiredCacheAsync();
    
    long GetCacheHitCount();
    
    long GetCacheMissCount();
    
    double GetCacheHitRate();
}