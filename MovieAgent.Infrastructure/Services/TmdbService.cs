using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using MovieAgent.Core.Entities;
using MovieAgent.Core.Interfaces;
using TMDbLib.Client;
using TMDbLib.Objects.General;
using TMDbLib.Objects.People;
using TMDbLib.Objects.Search;
using MovieEntity = MovieAgent.Core.Entities.Movie;
using TmdbMovie = TMDbLib.Objects.Movies.Movie;
using TmdbMovieMethods = TMDbLib.Objects.Movies.MovieMethods;
using TmdbPerson = TMDbLib.Objects.People.Person;

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
            // 获取完整的电影信息，包括 Credits 和 Keywords
            var movie = await _client.GetMovieAsync(best.Id, TmdbMovieMethods.Credits | TmdbMovieMethods.Keywords);

            var genres = movie.Genres.Select(g => g.Name).ToList();
            var director = movie.Credits?.Crew?.FirstOrDefault(c => c.Job == "Director")?.Name;
            var directorId = movie.Credits?.Crew?.FirstOrDefault(c => c.Job == "Director")?.Id.ToString();
            var writers = movie.Credits?.Crew?.Where(c => c.Job == "Writer" || c.Job == "Screenplay").Select(c => c.Name).ToList();
            var writerIds = movie.Credits?.Crew?.Where(c => c.Job == "Writer" || c.Job == "Screenplay").Select(c => c.Id.ToString()).ToList() ?? new List<string>();
            var cast = movie.Credits?.Cast?.Take(8).Select(c => c.Name).ToList();
            var castIds = movie.Credits?.Cast?.Take(8).Select(c => c.Id.ToString()).ToList() ?? new List<string>();
            var countries = movie.ProductionCountries?.Select(p => p.Name).ToList() ?? new List<string>();
            var languages = movie.SpokenLanguages?.Select(l => l.Name).ToList() ?? new List<string>();
            var productionCompanies = movie.ProductionCompanies?.Select(p => p.Name).ToList() ?? new List<string>();
            var productionCompanyIds = movie.ProductionCompanies?.Select(p => p.Id.ToString()).ToList() ?? new List<string>();
            var keywords = movie.Keywords?.Keywords?.Select(k => k.Name).ToList() ?? new List<string>();

            return new TmdbSearchResult
            {
                Id = movie.Id,
                Title = movie.Title,
                OriginalTitle = movie.OriginalTitle,
                Overview = movie.Overview,
                Tagline = movie.Tagline,
                PosterPath = movie.PosterPath,
                BackdropPath = movie.BackdropPath,
                ReleaseDate = movie.ReleaseDate,
                ReleaseYear = movie.ReleaseDate?.Year,
                Rating = movie.VoteAverage,
                VoteCount = movie.VoteCount,
                Popularity = movie.Popularity,
                Genres = genres,
                Runtime = movie.Runtime,
                Director = director,
                DirectorTmdbId = directorId,
                Writer = writers != null && writers.Any() ? string.Join(", ", writers) : null,
                WriterTmdbIds = writerIds,
                Cast = cast != null ? string.Join(", ", cast) : null,
                CastTmdbIds = castIds,
                Countries = countries,
                Languages = languages,
                Homepage = movie.Homepage,
                Status = movie.Status,
                IsAdult = movie.Adult,
                BelongsToCollection = movie.BelongsToCollection?.Name,
                Budget = movie.Budget,
                Revenue = movie.Revenue,
                OriginalLanguage = movie.OriginalLanguage,
                ProductionCompanies = productionCompanies,
                ProductionCompanyIds = productionCompanyIds,
                Keywords = keywords,
                ImdbId = movie.ImdbId
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

            // 基本信息
            movie.TmdbId = result.Id.ToString();
            movie.Title = result.Title;
            movie.OriginalTitle = result.OriginalTitle;
            movie.Overview = result.Overview;
            movie.Tagline = result.Tagline;
            movie.PosterPath = result.PosterPath;
            movie.BackdropPath = result.BackdropPath;
            movie.ReleaseDate = result.ReleaseDate;
            movie.ReleaseYear ??= result.ReleaseYear;
            
            // 评分和人气
            movie.Rating ??= result.Rating;
            movie.VoteCount ??= result.VoteCount;
            movie.Popularity ??= result.Popularity;
            
            // 内容信息
            movie.Runtime ??= result.Runtime;
            movie.Genres = JsonSerializer.Serialize(result.Genres);
            movie.Homepage = result.Homepage;
            movie.Status = result.Status;
            movie.IsAdult = result.IsAdult;
            movie.BelongsToCollection = result.BelongsToCollection;
            
            // 财务信息
            movie.Budget ??= result.Budget;
            movie.Revenue ??= result.Revenue;
            
            // 语言和制片信息
            movie.OriginalLanguage = result.OriginalLanguage;
            movie.ProductionCompanies = JsonSerializer.Serialize(result.ProductionCompanies);
            movie.ProductionCountries = JsonSerializer.Serialize(result.Countries);
            movie.OriginCountry = result.Countries != null && result.Countries.Any() ? string.Join(", ", result.Countries) : null;
            movie.Keywords = JsonSerializer.Serialize(result.Keywords);
            
            // 演职人员
            movie.Director = result.Director;
            movie.DirectorTmdbId = result.DirectorTmdbId;
            movie.Writer = result.Writer;
            movie.WriterTmdbIds = JsonSerializer.Serialize(result.WriterTmdbIds);
            movie.Cast = result.Cast;
            movie.CastTmdbIds = JsonSerializer.Serialize(result.CastTmdbIds);
            movie.Language = result.Languages != null && result.Languages.Any() ? string.Join(", ", result.Languages) : null;
            movie.Country = result.Countries != null && result.Countries.Any() ? string.Join(", ", result.Countries) : null;
            
            // 制片公司ID
            movie.ProductionCompanyIds = JsonSerializer.Serialize(result.ProductionCompanyIds);
            
            // IMDB ID
            movie.ImdbId = result.ImdbId;
            
            // 更新时间
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

    public async Task<TmdbPersonResult?> GetPersonAsync(long personId)
    {
        try
        {
            var tmdbPerson = await _client.GetPersonAsync((int)personId, PersonMethods.MovieCredits | PersonMethods.TvCredits | PersonMethods.ExternalIds);
            
            if (tmdbPerson == null) return null;

            var movieCredits = tmdbPerson.MovieCredits?.Cast?.Take(10).Select(c => new PersonCredit
            {
                Title = c.Title,
                Character = c.Character,
                ReleaseDate = c.ReleaseDate,
                PosterPath = c.PosterPath,
                TmdbId = c.Id.ToString()
            }).ToList() ?? new List<PersonCredit>();

            var tvCredits = tmdbPerson.TvCredits?.Cast?.Take(5).Select(c => new PersonCredit
            {
                Title = c.Name,
                Character = c.Character,
                ReleaseDate = c.FirstAirDate,
                PosterPath = c.PosterPath,
                TmdbId = c.Id.ToString()
            }).ToList() ?? new List<PersonCredit>();

            var knownFor = new List<string>();

            return new TmdbPersonResult
            {
                Id = tmdbPerson.Id,
                Name = tmdbPerson.Name,
                OriginalName = tmdbPerson.AlsoKnownAs?.FirstOrDefault(),
                Biography = tmdbPerson.Biography,
                ProfilePath = tmdbPerson.ProfilePath,
                Birthday = tmdbPerson.Birthday,
                Deathday = tmdbPerson.Deathday,
                PlaceOfBirth = tmdbPerson.PlaceOfBirth,
                Gender = (int?)tmdbPerson.Gender,
                KnownForDepartment = tmdbPerson.KnownForDepartment,
                Popularity = tmdbPerson.Popularity,
                AlsoKnownAs = tmdbPerson.AlsoKnownAs ?? new List<string>(),
                KnownForTitles = knownFor,
                Credits = movieCredits.Concat(tvCredits).ToList()
            };
        }
        catch
        {
            return null;
        }
    }

    public async Task<TmdbCompanyResult?> GetCompanyAsync(long companyId)
    {
        try
        {
            var company = await _client.GetCompanyAsync((int)companyId);
            
            if (company == null) return null;

            var movies = company.Movies?.Results?.Take(10).Select(m => new CompanyMovie
            {
                Title = m.Title,
                ReleaseDate = m.ReleaseDate,
                PosterPath = m.PosterPath,
                TmdbId = m.Id.ToString()
            }).ToList() ?? new List<CompanyMovie>();

            return new TmdbCompanyResult
            {
                Id = company.Id,
                Name = company.Name,
                Description = company.Description,
                LogoPath = company.LogoPath,
                OriginCountry = company.OriginCountry,
                Headquarters = company.Headquarters,
                Homepage = company.Homepage,
                ParentCompany = company.ParentCompany?.Name,
                MovieList = movies
            };
        }
        catch
        {
            return null;
        }
    }
}