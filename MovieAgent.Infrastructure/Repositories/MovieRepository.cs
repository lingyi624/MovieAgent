using Microsoft.EntityFrameworkCore;
using MovieAgent.Core.Entities;
using MovieAgent.Core.Interfaces;
using MovieAgent.Core.Models;
using MovieAgent.Infrastructure.Data;
using System.IO;
using System.Text.Json;
using System.Drawing;
using System.Drawing.Imaging;

namespace MovieAgent.Infrastructure.Repositories;

public class MovieRepository : IMovieRepository
{
    private readonly AppDbContext _db;

    public MovieRepository(AppDbContext db) => _db = db;

    public async Task<List<Movie>> GetAllAsync(MovieFilter? filter = null)
    {
        var query = _db.Movies.AsQueryable();

        if (filter != null)
        {
            if (!string.IsNullOrWhiteSpace(filter.SearchKeyword))
                query = query.Where(m => m.Title.Contains(filter.SearchKeyword));
            if (filter.Genres is { Count: > 0 })
                foreach (var genre in filter.Genres)
                {
                    string likePattern = $"%\"{genre}\"%";
                    query = query.Where(m => m.Genres != null && EF.Functions.Like(m.Genres, likePattern));
                }
            if (filter.MinYear.HasValue)
                query = query.Where(m => m.ReleaseYear >= filter.MinYear);
            if (filter.MaxYear.HasValue)
                query = query.Where(m => m.ReleaseYear <= filter.MaxYear);
            if (filter.MinRating.HasValue)
                query = query.Where(m => m.Rating >= filter.MinRating);
            if (filter.MaxRating.HasValue)
                query = query.Where(m => m.Rating <= filter.MaxRating);
            if (filter.IsWatched.HasValue)
                query = query.Where(m => m.IsWatched == filter.IsWatched);
            if (filter.IsFavorite.HasValue)
                query = query.Where(m => m.IsFavorite == filter.IsFavorite);
            if (!string.IsNullOrWhiteSpace(filter.Resolution))
                query = query.Where(m => m.Resolution == filter.Resolution);
            if (!string.IsNullOrWhiteSpace(filter.VideoCodec))
                query = query.Where(m => m.VideoCodec == filter.VideoCodec);
            if (!string.IsNullOrWhiteSpace(filter.HdrType))
                query = query.Where(m => m.HdrType == filter.HdrType);
            if (filter.Countries is { Count: > 0 })
                query = query.Where(m => m.Country != null && filter.Countries.Any(c => m.Country.Contains(c)));
            if (filter.Languages is { Count: > 0 })
                query = query.Where(m => m.Language != null && filter.Languages.Any(l => m.Language.Contains(l)));

            query = filter.SortBy?.ToLower() switch
            {
                "title" => filter.SortDescending ? query.OrderByDescending(m => m.Title) : query.OrderBy(m => m.Title),
                "year" => filter.SortDescending ? query.OrderByDescending(m => m.ReleaseYear) : query.OrderBy(m => m.ReleaseYear),
                "rating" => filter.SortDescending ? query.OrderByDescending(m => m.Rating) : query.OrderBy(m => m.Rating),
                "created" => filter.SortDescending ? query.OrderByDescending(m => m.CreatedAt) : query.OrderBy(m => m.CreatedAt),
                _ => query.OrderByDescending(m => m.CreatedAt)
            };

            query = query.Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize);
        }

        return await query.ToListAsync();
    }

    public async Task<Movie?> GetByIdAsync(int id) => await _db.Movies.FindAsync(id);

    public async Task<Movie?> GetByFilePathAsync(string filePath)
        => await _db.Movies.FirstOrDefaultAsync(m => m.FilePath == filePath);

    public async Task<Movie?> GetByTmdbIdAsync(string tmdbId)
        => await _db.Movies.FirstOrDefaultAsync(m => m.TmdbId == tmdbId);

    public async Task<List<Movie>> SearchAsync(string keyword)
        => await _db.Movies.Where(m => m.Title.Contains(keyword)
            || (m.OriginalTitle != null && m.OriginalTitle.Contains(keyword))
            || (m.Director != null && m.Director.Contains(keyword)))
            .OrderByDescending(m => m.Rating)
            .Take(20)
            .ToListAsync();

    public async Task<Movie> AddAsync(Movie movie)
    {
        _db.Movies.Add(movie);
        await _db.SaveChangesAsync();
        return movie;
    }

    public async Task<Movie> UpdateAsync(Movie movie)
    {
        movie.UpdatedAt = DateTime.UtcNow;
        _db.Movies.Update(movie);
        await _db.SaveChangesAsync();
        return movie;
    }

    public async Task DeleteAsync(int id)
    {
        var movie = await _db.Movies.FindAsync(id);
        if (movie != null)
        {
            _db.Movies.Remove(movie);
            await _db.SaveChangesAsync();
        }
    }

    public async Task<bool> ExistsByFilePathAsync(string filePath)
        => await _db.Movies.AnyAsync(m => m.FilePath == filePath);

    public async Task<int> GetCountAsync(MovieFilter? filter = null)
    {
        var query = _db.Movies.AsQueryable();
        
        if (filter != null)
        {
            if (!string.IsNullOrWhiteSpace(filter.SearchKeyword))
                query = query.Where(m => m.Title.Contains(filter.SearchKeyword));
            if (filter.Genres is { Count: > 0 })
                query = query.Where(m => filter.Genres.Any(g => m.Genres != null && m.Genres.Contains(g)));
            if (filter.MinYear.HasValue)
                query = query.Where(m => m.ReleaseYear >= filter.MinYear);
            if (filter.MaxYear.HasValue)
                query = query.Where(m => m.ReleaseYear <= filter.MaxYear);
            if (filter.MinRating.HasValue)
                query = query.Where(m => m.Rating >= filter.MinRating);
            if (filter.MaxRating.HasValue)
                query = query.Where(m => m.Rating <= filter.MaxRating);
            if (filter.IsWatched.HasValue)
                query = query.Where(m => m.IsWatched == filter.IsWatched);
            if (filter.IsFavorite.HasValue)
                query = query.Where(m => m.IsFavorite == filter.IsFavorite);
            if (!string.IsNullOrWhiteSpace(filter.Resolution))
                query = query.Where(m => m.Resolution == filter.Resolution);
            if (!string.IsNullOrWhiteSpace(filter.VideoCodec))
                query = query.Where(m => m.VideoCodec == filter.VideoCodec);
            if (!string.IsNullOrWhiteSpace(filter.HdrType))
                query = query.Where(m => m.HdrType == filter.HdrType);
            if (filter.Countries is { Count: > 0 })
                query = query.Where(m => m.Country != null && filter.Countries.Any(c => m.Country.Contains(c)));
            if (filter.Languages is { Count: > 0 })
                query = query.Where(m => m.Language != null && filter.Languages.Any(l => m.Language.Contains(l)));
        }
        
        return await query.CountAsync();
    }

    public async Task<List<string>> GetAllGenresAsync()
    {
        var movies = await _db.Movies.Where(m => m.Genres != null).Select(m => m.Genres!).ToListAsync();
        var genres = new HashSet<string>();
        foreach (var json in movies)
        {
            try
            {
                var list = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
                if (list != null)
                    foreach (var g in list) genres.Add(g);
            }
            catch { }
        }
        return genres.OrderBy(g => g).ToList();
    }

    public async Task<List<string>> GetAllResolutionsAsync()
    {
        var resolutions = await _db.Movies
            .Where(m => !string.IsNullOrEmpty(m.Resolution))
            .Select(m => m.Resolution!)
            .Distinct()
            .OrderByDescending(r => r)
            .ToListAsync();
        return resolutions;
    }

    public async Task<List<string>> GetAllCountriesAsync()
    {
        var countries = await _db.Movies
            .Where(m => !string.IsNullOrEmpty(m.Country))
            .Select(m => m.Country!)
            .ToListAsync();
        
        var allCountries = new HashSet<string>();
        foreach (var countryList in countries)
        {
            foreach (var country in countryList.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                allCountries.Add(country.Trim());
            }
        }
        
        return allCountries.OrderBy(c => c).ToList();
    }

    public async Task<List<string>> GetAllLanguagesAsync()
    {
        var languages = await _db.Movies
            .Where(m => !string.IsNullOrEmpty(m.Language))
            .Select(m => m.Language!)
            .ToListAsync();
        
        var allLanguages = new HashSet<string>();
        foreach (var languageList in languages)
        {
            foreach (var language in languageList.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                allLanguages.Add(language.Trim());
            }
        }
        
        return allLanguages.OrderBy(l => l).ToList();
    }

    public async Task<List<string>> GetAllVideoCodecsAsync()
    {
        var codecs = await _db.Movies
            .Where(m => !string.IsNullOrEmpty(m.VideoCodec))
            .Select(m => m.VideoCodec!)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync();
        return codecs;
    }

    public async Task<List<string>> GetAllHdrTypesAsync()
    {
        var hdrTypes = await _db.Movies
            .Where(m => !string.IsNullOrEmpty(m.HdrType))
            .Select(m => m.HdrType!)
            .Distinct()
            .OrderBy(h => h)
            .ToListAsync();
        return hdrTypes;
    }

    public async Task<List<Movie>> GetUniqueMoviesAsync(MovieFilter? filter = null)
    {
        var query = _db.Movies.AsQueryable();

        if (filter != null)
        {
            if (!string.IsNullOrWhiteSpace(filter.SearchKeyword))
                query = query.Where(m => m.Title.Contains(filter.SearchKeyword));
            if (filter.Genres is { Count: > 0 })
            {
                var s = JsonSerializer.Serialize(filter.Genres);
                s = s.TrimStart('[').TrimStart('"').TrimEnd('"').TrimEnd(']');
                query = query.Where(m =>   m.Genres.Contains(s));
            }
            if (filter.MinYear.HasValue)
                query = query.Where(m => m.ReleaseYear >= filter.MinYear);
            if (filter.MaxYear.HasValue)
                query = query.Where(m => m.ReleaseYear <= filter.MaxYear);
            if (filter.MinRating.HasValue)
                query = query.Where(m => m.Rating >= filter.MinRating);
            if (filter.MaxRating.HasValue)
                query = query.Where(m => m.Rating <= filter.MaxRating);
            if (filter.IsWatched.HasValue)
                query = query.Where(m => m.IsWatched == filter.IsWatched);
            if (filter.IsFavorite.HasValue)
                query = query.Where(m => m.IsFavorite == filter.IsFavorite);
            if (!string.IsNullOrWhiteSpace(filter.Resolution))
                query = query.Where(m => m.Resolution == filter.Resolution);
            if (!string.IsNullOrWhiteSpace(filter.VideoCodec))
                query = query.Where(m => m.VideoCodec == filter.VideoCodec);
            if (!string.IsNullOrWhiteSpace(filter.HdrType))
                query = query.Where(m => m.HdrType == filter.HdrType);
            if (filter.Countries is { Count: > 0 })
                query = query.Where(m => m.Country != null && filter.Countries.Any(c => m.Country.Contains(c)));
            if (filter.Languages is { Count: > 0 })
                query = query.Where(m => m.Language != null && filter.Languages.Any(l => m.Language.Contains(l)));
            if (filter.Tags is { Count: > 0 })
            {
                foreach (var tag in filter.Tags)
                {
                    string likePattern = $"%\"{tag}\"%";
                    query = query.Where(m => m.Tags != null && EF.Functions.Like(m.Tags, likePattern));
                }
            }
        }

        var movies = await query.ToListAsync();

        var grouped = movies
            .GroupBy(m => Path.GetDirectoryName(m.FilePath))
            .Select(g => g.OrderByDescending(m => m.Resolution).ThenByDescending(m => m.FileSize).First())
            .ToList();

        if (filter != null)
        {
            grouped = filter.SortBy?.ToLower() switch
            {
                "title" => filter.SortDescending ? grouped.OrderByDescending(m => m.Title).ToList() : grouped.OrderBy(m => m.Title).ToList(),
                "year" => filter.SortDescending ? grouped.OrderByDescending(m => m.ReleaseYear).ToList() : grouped.OrderBy(m => m.ReleaseYear).ToList(),
                "rating" => filter.SortDescending ? grouped.OrderByDescending(m => m.Rating).ToList() : grouped.OrderBy(m => m.Rating).ToList(),
                "created" => filter.SortDescending ? grouped.OrderByDescending(m => m.CreatedAt).ToList() : grouped.OrderBy(m => m.CreatedAt).ToList(),
                _ => grouped.OrderByDescending(m => m.CreatedAt).ToList()
            };

            grouped = grouped.Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize).ToList();
        }

        return grouped;
    }

    public async Task<int> GetUniqueMovieCountAsync(MovieFilter? filter = null)
    {
        var query = _db.Movies.AsQueryable();

        if (filter != null)
        {
            if (!string.IsNullOrWhiteSpace(filter.SearchKeyword))
                query = query.Where(m => m.Title.Contains(filter.SearchKeyword));
            if (filter.Genres is { Count: > 0 })
                foreach (var genre in filter.Genres)
                {
                    string likePattern = $"%\"{genre}\"%";
                    query = query.Where(m => m.Genres != null && EF.Functions.Like(m.Genres, likePattern));
                }
            if (filter.MinYear.HasValue)
                query = query.Where(m => m.ReleaseYear >= filter.MinYear);
            if (filter.MaxYear.HasValue)
                query = query.Where(m => m.ReleaseYear <= filter.MaxYear);
            if (filter.MinRating.HasValue)
                query = query.Where(m => m.Rating >= filter.MinRating);
            if (filter.MaxRating.HasValue)
                query = query.Where(m => m.Rating <= filter.MaxRating);
            if (filter.IsWatched.HasValue)
                query = query.Where(m => m.IsWatched == filter.IsWatched);
            if (filter.IsFavorite.HasValue)
                query = query.Where(m => m.IsFavorite == filter.IsFavorite);
            if (!string.IsNullOrWhiteSpace(filter.Resolution))
                query = query.Where(m => m.Resolution == filter.Resolution);
            if (!string.IsNullOrWhiteSpace(filter.VideoCodec))
                query = query.Where(m => m.VideoCodec == filter.VideoCodec);
            if (!string.IsNullOrWhiteSpace(filter.HdrType))
                query = query.Where(m => m.HdrType == filter.HdrType);
            if (filter.Countries is { Count: > 0 })
                query = query.Where(m => m.Country != null && filter.Countries.Any(c => m.Country.Contains(c)));
            if (filter.Languages is { Count: > 0 })
                query = query.Where(m => m.Language != null && filter.Languages.Any(l => m.Language.Contains(l)));
        }

        var movies = await query.ToListAsync();
        return movies.GroupBy(m => Path.GetDirectoryName(m.FilePath)).Count();
    }

    public async Task<List<Movie>> GetUnwatchedAsync()
        => await _db.Movies.Where(m => !m.IsWatched).OrderByDescending(m => m.Rating).Take(50).ToListAsync();

    public async Task<List<Movie>> GetRecentlyAddedAsync(int count = 20)
        => await _db.Movies.OrderByDescending(m => m.CreatedAt).Take(count).ToListAsync();

    public async Task<List<string>> GetMovieVideoPathsAsync(int movieId)
    {
        var movie = await _db.Movies.FindAsync(movieId);
        if (movie == null || string.IsNullOrEmpty(movie.FilePath))
            return new List<string>();

        var directory = Path.GetDirectoryName(movie.FilePath);
        if (string.IsNullOrEmpty(movie.FilePath))
            return new List<string>();

        var videoExtensions = new[] { ".mp4", ".mkv", ".avi", ".mov", ".flv", ".wmv", ".webm", ".m4v" };
        
        var videoFiles = Directory.GetFiles(directory)
            .Where(f => videoExtensions.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(f => f)
            .ToList();

        return videoFiles;
    }

    /// <summary>
    /// 从硬盘目录获取海报图片（当数据库海报字段为空时）
    /// </summary>
    /// <param name="filePath">电影文件路径</param>
    /// <returns>海报图片字节数组，如果未找到则返回null</returns>
    public byte[]? GetPosterFromDirectory(string filePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(directory))
                return null;

            var posterExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp" };
            var posterNames = new[] { "poster", "cover", "folder", "fanart" };

            foreach (var posterName in posterNames)
            {
                foreach (var ext in posterExtensions)
                {
                    var posterPath = Path.Combine(directory, $"{posterName}{ext}");
                    if (File.Exists(posterPath))
                    {
                        return File.ReadAllBytes(posterPath);
                    }

                    var posterPathUpper = Path.Combine(directory, $"{posterName.ToUpper()}{ext}");
                    if (File.Exists(posterPathUpper))
                    {
                        return File.ReadAllBytes(posterPathUpper);
                    }
                }
            }

            // 查找目录中最大的图片文件（可能是海报）
            var imageFiles = Directory.GetFiles(directory)
                .Where(f => posterExtensions.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(f => new FileInfo(f).Length)
                .ToList();

            if (imageFiles.Any())
            {
                return File.ReadAllBytes(imageFiles.First());
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// 从硬盘目录获取媒体信息文件
    /// </summary>
    /// <param name="filePath">电影文件路径</param>
    /// <returns>媒体信息字典，如果未找到则返回null</returns>
    public Dictionary<string, string>? GetMediaInfoFromDirectory(string filePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(directory))
                return null;

            var infoFiles = new[] { "info.txt", "nfo", "movie.nfo", "info.nfo" };

            foreach (var infoFile in infoFiles)
            {
                var infoPath = Path.Combine(directory, infoFile);
                if (File.Exists(infoPath))
                {
                    var content = File.ReadAllText(infoPath);
                    return ParseMediaInfo(content);
                }

                var infoPathUpper = Path.Combine(directory, infoFile.ToUpper());
                if (File.Exists(infoPathUpper))
                {
                    var content = File.ReadAllText(infoPathUpper);
                    return ParseMediaInfo(content);
                }
            }
        }
        catch { }

        return null;
    }

    private Dictionary<string, string> ParseMediaInfo(string content)
    {
        var info = new Dictionary<string, string>();

        try
        {
            // 尝试 JSON 格式
            if (content.TrimStart().StartsWith('{'))
            {
                var jsonDoc = JsonDocument.Parse(content);
                foreach (var prop in jsonDoc.RootElement.EnumerateObject())
                {
                    info[prop.Name] = prop.Value.ToString();
                }
            }
            else
            {
                // 尝试简单键值对格式
                foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split(new[] { '=', ':' }, 2);
                    if (parts.Length == 2)
                    {
                        info[parts[0].Trim()] = parts[1].Trim();
                    }
                }
            }
        }
        catch { }

        return info;
    }

    /// <summary>
    /// 检查并更新电影的海报（从目录获取）
    /// </summary>
    /// <param name="movie">电影实体</param>
    /// <returns>是否成功更新</returns>
    public async Task<bool> UpdatePosterFromDirectoryAsync(Movie movie)
    {
        if (string.IsNullOrEmpty(movie.FilePath))
            return false;

        if (!string.IsNullOrEmpty(movie.PosterPath))
            return false;

        var posterData = GetPosterFromDirectory(movie.FilePath);
        if (posterData != null)
        {
            // 将海报保存到本地目录，并设置 PosterPath 为本地路径
            var directory = Path.GetDirectoryName(movie.FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                var posterPath = Path.Combine(directory, "local_poster.jpg");
                File.WriteAllBytes(posterPath, posterData);
                movie.PosterPath = posterPath;
                await _db.SaveChangesAsync();
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 检查并更新电影的媒体信息（从目录获取）
    /// </summary>
    /// <param name="movie">电影实体</param>
    /// <returns>是否成功更新</returns>
    public async Task<bool> UpdateMediaInfoFromDirectoryAsync(Movie movie)
    {
        if (string.IsNullOrEmpty(movie.FilePath))
            return false;

        var mediaInfo = GetMediaInfoFromDirectory(movie.FilePath);
        if (mediaInfo != null && mediaInfo.Any())
        {
            if (mediaInfo.TryGetValue("Title", out var title) && string.IsNullOrEmpty(movie.Title))
                movie.Title = title;
            if (mediaInfo.TryGetValue("Year", out var year) && !movie.ReleaseYear.HasValue)
                movie.ReleaseYear = int.TryParse(year, out var y) ? y : movie.ReleaseYear;
            if (mediaInfo.TryGetValue("Rating", out var rating) && !movie.Rating.HasValue)
                movie.Rating = double.TryParse(rating, out var r) ? r : movie.Rating;
            if (mediaInfo.TryGetValue("Director", out var director) && string.IsNullOrEmpty(movie.Director))
                movie.Director = director;
            if (mediaInfo.TryGetValue("Genre", out var genre) && string.IsNullOrEmpty(movie.Genres))
                movie.Genres = JsonSerializer.Serialize(new List<string> { genre });

            await _db.SaveChangesAsync();
            return true;
        }

        return false;
    }
}