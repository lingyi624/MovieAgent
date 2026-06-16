using Microsoft.EntityFrameworkCore;
using MovieAgent.Core.Entities;
using MovieAgent.Core.Interfaces;
using MovieAgent.Infrastructure.Data;

namespace MovieAgent.Infrastructure.Repositories;

public class MovieReviewRepository : IMovieReviewRepository
{
    private readonly AppDbContext _db;

    public MovieReviewRepository(AppDbContext db) => _db = db;

    public async Task<List<MovieReview>> GetByMovieIdAsync(int movieId)
    {
        return await _db.MovieReviews
            .Where(r => r.MovieId == movieId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();
    }

    public async Task<MovieReview?> GetByIdAsync(int id)
    {
        return await _db.MovieReviews.FindAsync(id);
    }

    public async Task<MovieReview> AddAsync(MovieReview review)
    {
        review.CreatedAt = DateTime.UtcNow;
        _db.MovieReviews.Add(review);
        await _db.SaveChangesAsync();
        return review;
    }

    public async Task<MovieReview> UpdateAsync(MovieReview review)
    {
        review.UpdatedAt = DateTime.UtcNow;
        _db.MovieReviews.Update(review);
        await _db.SaveChangesAsync();
        return review;
    }

    public async Task DeleteAsync(int id)
    {
        var review = await _db.MovieReviews.FindAsync(id);
        if (review != null)
        {
            _db.MovieReviews.Remove(review);
            await _db.SaveChangesAsync();
        }
    }

    public async Task<bool> HasReviewAsync(int movieId)
    {
        return await _db.MovieReviews.AnyAsync(r => r.MovieId == movieId);
    }

    public async Task<int> GetCountAsync()
    {
        return await _db.MovieReviews.CountAsync();
    }
}