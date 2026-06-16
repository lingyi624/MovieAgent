using MovieAgent.Core.Entities;
using MovieAgent.Infrastructure.Services;
using Xunit;

namespace MovieAgent.Tests;

public class CacheServiceTests
{
    private readonly SearchCacheService _service = new SearchCacheService();

    [Fact]
    public async Task GetCachedSearch_WithExistingCache_ShouldReturnResults()
    {
        // Arrange
        var movies = new List<Movie> { new Movie { Id = 1, Title = "Test Movie" } };
        await _service.CacheSearchAsync("test query", movies);

        // Act
        var result = await _service.GetCachedSearchAsync("test query");

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("Test Movie", result[0].Title);
    }

    [Fact]
    public async Task GetCachedSearch_WithDifferentCase_ShouldReturnResults()
    {
        // Arrange
        var movies = new List<Movie> { new Movie { Id = 1, Title = "Test Movie" } };
        await _service.CacheSearchAsync("Test Query", movies);

        // Act
        var result = await _service.GetCachedSearchAsync("test query");

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
    }

    [Fact]
    public async Task GetCachedSearch_WithNonExistentCache_ShouldReturnEmpty()
    {
        // Act
        var result = await _service.GetCachedSearchAsync("non existent query");

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void GetCacheHitRate_WithNoRequests_ShouldReturnZero()
    {
        // Act
        var rate = _service.GetCacheHitRate();

        // Assert
        Assert.Equal(0, rate);
    }

    [Fact]
    public void GetCacheHitRate_WithMixedHitsAndMisses_ShouldCalculateCorrectly()
    {
        // Arrange - simulate some hits and misses
        var _ = _service.GetCachedSearchAsync("query1").Result; // miss
        var movies = new List<Movie> { new Movie { Id = 1 } };
        _service.CacheSearchAsync("query2", movies).Wait();
        _ = _service.GetCachedSearchAsync("query2").Result; // hit

        // Act
        var rate = _service.GetCacheHitRate();

        // Assert - 1 hit out of 2 requests = 50%
        Assert.Equal(50, rate);
    }

    [Fact]
    public async Task ClearCache_ShouldRemoveAllEntries()
    {
        // Arrange
        var movies = new List<Movie> { new Movie { Id = 1, Title = "Test" } };
        await _service.CacheSearchAsync("query1", movies);
        await _service.CacheSearchAsync("query2", movies);

        // Act
        await _service.ClearCacheAsync();
        var result1 = await _service.GetCachedSearchAsync("query1");
        var result2 = await _service.GetCachedSearchAsync("query2");

        // Assert
        Assert.Empty(result1);
        Assert.Empty(result2);
    }
}