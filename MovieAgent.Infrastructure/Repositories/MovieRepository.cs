using Microsoft.EntityFrameworkCore;
using MovieAgent.Core.Entities;
using MovieAgent.Core.Interfaces;
using MovieAgent.Core.Models;
using MovieAgent.Infrastructure.Data;
using System.IO;
using System.Text.Json;

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
        }

        var movies = await query.ToListAsync();
        return movies.GroupBy(m => Path.GetDirectoryName(m.FilePath)).Count();
    }

    public async Task<List<Movie>> GetUnwatchedAsync()
        => await _db.Movies.Where(m => !m.IsWatched).OrderByDescending(m => m.Rating).Take(50).ToListAsync();

    public async Task<List<Movie>> GetRecentlyAddedAsync(int count = 20)
        => await _db.Movies.OrderByDescending(m => m.CreatedAt).Take(count).ToListAsync();
}