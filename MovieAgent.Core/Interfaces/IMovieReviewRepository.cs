using MovieAgent.Core.Entities;

namespace MovieAgent.Core.Interfaces;

public interface IMovieReviewRepository
{
    Task<List<MovieReview>> GetByMovieIdAsync(int movieId);
    Task<MovieReview?> GetByIdAsync(int id);
    Task<MovieReview> AddAsync(MovieReview review);
    Task<MovieReview> UpdateAsync(MovieReview review);
    Task DeleteAsync(int id);
    Task<bool> HasReviewAsync(int movieId);
    Task<int> GetCountAsync();
}