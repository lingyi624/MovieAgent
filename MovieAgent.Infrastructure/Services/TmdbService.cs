using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using MovieAgent.Core.Entities;
using MovieAgent.Core.Interfaces;
using TMDbLib.Client;
using TMDbLib.Objects.General;
using TMDbLib.Objects.Search;
using MovieEntity = MovieAgent.Core.Entities.Movie;
using TmdbMovie = TMDbLib.Objects.Movies.Movie;
using TmdbMovieMethods = TMDbLib.Objects.Movies.MovieMethods;

namespace MovieAgent.Infrastructure.Services;

public class TmdbService : ITmdbService
{
    private readonly TMDbClient _client;
    private readonly HttpClient _http;
    private readonly string? _apiKey;

    public TmdbService(string apiKey)
    {
        _apiKey = apiKey;
        _client = new TMDbClient(apiKey);
        _client.DefaultLanguage = "zh-CN";
        _http = new HttpClient();
    }

    public async Task<TmdbSearchResult?> SearchMovieAsync(string title, int? year = null)
    {
        try
        {
            var cleanTitle = CleanTitle(title);
            var search = await _client.SearchMovieAsync(cleanTitle, language: "zh-CN", year: year ?? 0);
            if (search.Results.Count == 0) return null;

            var best = search.Results[0];
            var movie = await _client.GetMovieAsync(best.Id, TmdbMovieMethods.Credits);

            var genres = movie.Genres.Select(g => g.Name).ToList();
            var director = movie.Credits?.Crew?.FirstOrDefault(c => c.Job == "Director")?.Name;
            var cast = movie.Credits?.Cast?.Take(5).Select(c => c.Name).ToList();
            var countries = movie.ProductionCountries?.Select(p => p.Name).ToList() ?? new List<string>();
            var languages = movie.SpokenLanguages?.Select(l => l.Name).ToList() ?? new List<string>();

            return new TmdbSearchResult
            {
                Id = movie.Id,
                Title = movie.Title,
                OriginalTitle = movie.OriginalTitle,
                Overview = movie.Overview,
                PosterPath = movie.PosterPath,
                BackdropPath = movie.BackdropPath,
                ReleaseYear = movie.ReleaseDate?.Year,
                Rating = movie.VoteAverage,
                Genres = genres,
                Runtime = movie.Runtime,
                Director = director,
                Cast = cast != null ? string.Join(", ", cast) : null,
                Countries = countries,
                Languages = languages
            };
        }
        catch
        {
            return null;
        }
    }

    public async Task<Movie?> FillMovieMetadataAsync(Movie movie)
    {
        try
        {
            var title = movie.Title;
            var year = movie.ReleaseYear;
            var result = await SearchMovieAsync(title, year);
            if (result == null) return null;

            movie.TmdbId = result.Id.ToString();
            movie.Title = result.Title;
            movie.OriginalTitle = result.OriginalTitle;
            movie.Overview = result.Overview;
            movie.PosterPath = result.PosterPath;
            movie.BackdropPath = result.BackdropPath;
            movie.ReleaseYear ??= result.ReleaseYear;
            movie.Rating ??= result.Rating;
            movie.Runtime ??= result.Runtime;
            movie.Genres = JsonSerializer.Serialize(result.Genres);
            movie.Director = result.Director;
            movie.Cast = result.Cast;
            movie.Country = result.Countries != null && result.Countries.Any() ? string.Join(", ", result.Countries) : null;
            movie.Language = result.Languages != null && result.Languages.Any() ? string.Join(", ", result.Languages) : null;
            movie.UpdatedAt = DateTime.UtcNow;

            return movie;
        }
        catch
        {
            return null;
        }
    }

    public async Task<byte[]?> DownloadPosterAsync(string posterPath, string size = "w500")
    {
        if (string.IsNullOrWhiteSpace(posterPath)) return null;
        try
        {
            var url = $"https://image.tmdb.org/t/p/{size}{posterPath}";
            return await _http.GetByteArrayAsync(url);
        }
        catch { return null; }
    }

    public async Task<byte[]?> DownloadBackdropAsync(string backdropPath, string size = "w780")
    {
        if (string.IsNullOrWhiteSpace(backdropPath)) return null;
        try
        {
            var url = $"https://image.tmdb.org/t/p/{size}{backdropPath}";
            return await _http.GetByteArrayAsync(url);
        }
        catch { return null; }
    }

    private static string CleanTitle(string filename)
    {
        var title = Path.GetFileNameWithoutExtension(filename);
        title = Regex.Replace(title, @"[\[\(].*?(19|20)\d{2}[\]\)]", "");
        title = Regex.Replace(title, @"(\d{4})", "");
        title = Regex.Replace(title, @"[\.\-_]", " ");
        title = Regex.Replace(title, @"(4K|1080[pP]|720[pP]|2160[pP]|HDR|HDR10|DV|HEVC|H\.?264|H\.?265|AVC|AV1|BluRay|WEB-DL|WEBRip|REMUX|PROPER|REPACK|DSNP|NF|AMZN|HMAX|ATVP|DDP?5\.1|Atmos|TrueHD|DTS-HD|DTS|MA|AAC|AC3|MP3|FLAC|HDRip|BDRip|XviD|DivX|S\d{2}E\d{2}|Complete)", "",
            RegexOptions.IgnoreCase);
        title = Regex.Replace(title, @"\s+", " ").Trim();
        return title;
    }
}