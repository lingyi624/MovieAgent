using MovieAgent.Core.Entities;
using MovieAgent.Infrastructure.Services;
using Xunit;

namespace MovieAgent.Tests;

public class MovieExportServiceTests
{
    private readonly MovieExportService _service = new MovieExportService();

    [Fact]
    public async Task ExportToJson_ShouldReturnValidJson()
    {
        // Arrange
        var movies = new List<Movie>
        {
            new Movie
            {
                Id = 1,
                Title = "Test Movie",
                OriginalTitle = "Test Original",
                Overview = "Test overview",
                ReleaseYear = 2024,
                Rating = 8.5,
                Runtime = 120,
                Genres = "Action,Adventure",
                FilePath = "/path/to/movie.mp4",
                FileSize = 1024 * 1024 * 100,
                IsWatched = true,
                IsFavorite = false,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            }
        };

        // Act
        var result = await _service.ExportToJsonAsync(movies);

        // Assert
        Assert.NotNull(result);
        Assert.Contains("Test Movie", result);
        Assert.Contains("Action,Adventure", result);
    }

    [Fact]
    public async Task ExportToCsv_ShouldReturnValidCsv()
    {
        // Arrange
        var movies = new List<Movie>
        {
            new Movie
            {
                Id = 1,
                Title = "Test Movie",
                ReleaseYear = 2024,
                Rating = 8.5,
                Genres = "Action",
                FilePath = "/path/to/movie.mp4"
            }
        };

        // Act
        var result = await _service.ExportToCsvAsync(movies);

        // Assert
        Assert.NotNull(result);
        Assert.Contains("Id,TmdbId,Title", result);
        Assert.Contains("Test Movie", result);
    }

    [Fact]
    public async Task ImportFromJson_ShouldReturnMovies()
    {
        // Arrange
        var json = @"[{""Id"":1,""Title"":""Test Movie"",""ReleaseYear"":2024}]";

        // Act
        var result = await _service.ImportFromJsonAsync(json);

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("Test Movie", result[0].Title);
        Assert.Equal(2024, result[0].ReleaseYear);
    }

    [Fact]
    public async Task ImportFromCsv_ShouldReturnMovies()
    {
        // Arrange
        var movies = new List<Movie>
        {
            new Movie
            {
                Id = 1,
                Title = "Test Movie",
                ReleaseYear = 2024,
                Rating = 8.5,
                Genres = "Action",
                FilePath = "/path/to/movie.mp4"
            }
        };

        // First export to CSV
        var csv = await _service.ExportToCsvAsync(movies);
        
        // Act - import from the exported CSV
        var result = await _service.ImportFromCsvAsync(csv);

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("Test Movie", result[0].Title);
        Assert.Equal(2024, result[0].ReleaseYear);
    }
}