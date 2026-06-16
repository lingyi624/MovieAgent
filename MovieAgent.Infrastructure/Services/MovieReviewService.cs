using MovieAgent.Core.Entities;
using MovieAgent.Core.Interfaces;

namespace MovieAgent.Infrastructure.Services;

public interface IMovieReviewService
{
    Task<List<MovieReview>> GetReviewsByMovieAsync(int movieId);
    Task<MovieReview?> GetReviewAsync(int reviewId);
    Task<MovieReview> CreateReviewAsync(int movieId, string content, int rating);
    Task<MovieReview> UpdateReviewAsync(int reviewId, string content, int rating);
    Task DeleteReviewAsync(int reviewId);
    Task<bool> HasReviewAsync(int movieId);
}

public class MovieReviewService : IMovieReviewService
{
    private readonly IMovieReviewRepository _repo;

    public MovieReviewService(IMovieReviewRepository repo)
    {
        _repo = repo;
    }

    public async Task<List<MovieReview>> GetReviewsByMovieAsync(int movieId)
    {
        return await _repo.GetByMovieIdAsync(movieId);
    }

    public async Task<MovieReview?> GetReviewAsync(int reviewId)
    {
        return await _repo.GetByIdAsync(reviewId);
    }

    public async Task<MovieReview> CreateReviewAsync(int movieId, string content, int rating)
    {
        var review = new MovieReview
        {
            MovieId = movieId,
            Content = content,
            Rating = Math.Clamp(rating, 1, 5),
            CreatedAt = DateTime.UtcNow
        };
        
        return await _repo.AddAsync(review);
    }

    public async Task<MovieReview> UpdateReviewAsync(int reviewId, string content, int rating)
    {
        var review = await _repo.GetByIdAsync(reviewId);
        if (review == null)
            throw new ArgumentException("影评不存在");
        
        review.Content = content;
        review.Rating = Math.Clamp(rating, 1, 5);
        review.UpdatedAt = DateTime.UtcNow;
        
        return await _repo.UpdateAsync(review);
    }

    public async Task DeleteReviewAsync(int reviewId)
    {
        await _repo.DeleteAsync(reviewId);
    }

    public async Task<bool> HasReviewAsync(int movieId)
    {
        return await _repo.HasReviewAsync(movieId);
    }
}