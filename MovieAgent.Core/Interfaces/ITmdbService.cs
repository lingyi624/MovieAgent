using MovieAgent.Core.Entities;

namespace MovieAgent.Core.Interfaces;

public interface ITmdbService
{
    Task<TmdbSearchResult?> SearchMovieAsync(string title, int? year = null);
    Task<Movie?> FillMovieMetadataAsync(Movie movie);
    Task<byte[]?> DownloadPosterAsync(string posterPath, string size = "w500");
    Task<byte[]?> DownloadBackdropAsync(string backdropPath, string size = "w780");
}

public class TmdbSearchResult
{
    public long Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? OriginalTitle { get; set; }
    public string? Overview { get; set; }
    public string? PosterPath { get; set; }
    public string? BackdropPath { get; set; }
    public int? ReleaseYear { get; set; }
    public double? Rating { get; set; }
    public List<string> Genres { get; set; } = new();
    public int? Runtime { get; set; }
    public string? Director { get; set; }
    public string? Cast { get; set; }
}