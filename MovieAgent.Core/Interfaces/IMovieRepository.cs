using MovieAgent.Core.Entities;
using MovieAgent.Core.Models;

namespace MovieAgent.Core.Interfaces;

public interface IMovieRepository
{
    Task<List<Movie>> GetAllAsync(MovieFilter? filter = null);
    Task<Movie?> GetByIdAsync(int id);
    Task<Movie?> GetByFilePathAsync(string filePath);
    Task<Movie?> GetByTmdbIdAsync(string tmdbId);
    Task<List<Movie>> SearchAsync(string keyword);
    Task<Movie> AddAsync(Movie movie);
    Task<Movie> UpdateAsync(Movie movie);
    Task DeleteAsync(int id);
    Task<bool> ExistsByFilePathAsync(string filePath);
    Task<int> GetCountAsync(MovieFilter? filter = null);
    Task<List<string>> GetAllGenresAsync();
    Task<List<string>> GetAllResolutionsAsync();
    Task<List<Movie>> GetUniqueMoviesAsync(MovieFilter? filter = null);
    Task<int> GetUniqueMovieCountAsync(MovieFilter? filter = null);
    Task<List<Movie>> GetUnwatchedAsync();
    Task<List<Movie>> GetRecentlyAddedAsync(int count = 20);
}