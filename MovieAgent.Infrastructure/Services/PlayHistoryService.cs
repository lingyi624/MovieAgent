using Microsoft.EntityFrameworkCore;
using MovieAgent.Core.Entities;
using MovieAgent.Core.Interfaces;
using MovieAgent.Infrastructure.Data;

namespace MovieAgent.Infrastructure.Services;

public class PlayHistoryService : IPlayHistoryService
{
    private readonly AppDbContext _dbContext;

    public PlayHistoryService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddPlayHistoryAsync(int movieId, int? progress = null, int? duration = null, bool completed = false)
    {
        var history = new PlayHistory
        {
            MovieId = movieId,
            PlayedAt = DateTime.UtcNow,
            Progress = progress,
            Duration = duration,
            Completed = completed
        };

        _dbContext.PlayHistories.Add(history);
        await _dbContext.SaveChangesAsync();
    }

    public async Task<List<PlayHistory>> GetRecentHistoryAsync(int count = 10)
    {
        return await _dbContext.PlayHistories
            .Include(h => h.Movie)
            .OrderByDescending(h => h.PlayedAt)
            .Take(count)
            .ToListAsync();
    }

    public async Task<PlayHistory?> GetLastPlayHistoryAsync(int movieId)
    {
        return await _dbContext.PlayHistories
            .Where(h => h.MovieId == movieId)
            .OrderByDescending(h => h.PlayedAt)
            .FirstOrDefaultAsync();
    }

    public async Task<int> GetWatchedCountAsync(DateTime? startDate = null, DateTime? endDate = null)
    {
        var query = _dbContext.PlayHistories.AsQueryable();

        if (startDate.HasValue)
            query = query.Where(h => h.PlayedAt >= startDate.Value);

        if (endDate.HasValue)
            query = query.Where(h => h.PlayedAt <= endDate.Value);

        return await query.CountAsync();
    }

    public async Task<TimeSpan> GetTotalWatchTimeAsync(DateTime? startDate = null, DateTime? endDate = null)
    {
        var query = _dbContext.PlayHistories.AsQueryable();

        if (startDate.HasValue)
            query = query.Where(h => h.PlayedAt >= startDate.Value);

        if (endDate.HasValue)
            query = query.Where(h => h.PlayedAt <= endDate.Value);

        var totalMinutes = await query.SumAsync(h => (double?)h.Duration ?? 0);
        return TimeSpan.FromMinutes(totalMinutes);
    }

    public async Task<List<DailyWatchCount>> GetDailyWatchCountAsync(int days = 30)
    {
        var startDate = DateTime.UtcNow.AddDays(-days);

        return await _dbContext.PlayHistories
            .Where(h => h.PlayedAt >= startDate)
            .GroupBy(h => h.PlayedAt.Date)
            .Select(g => new DailyWatchCount { Date = g.Key, Count = g.Count() })
            .OrderBy(g => g.Date)
            .ToListAsync();
    }
}
