using Microsoft.EntityFrameworkCore;
using MovieAgent.Core.Entities;
using MovieAgent.Core.Interfaces;
using MovieAgent.Infrastructure.Data;

namespace MovieAgent.Infrastructure.Repositories;

public class WatchPlanRepository : IWatchPlanRepository
{
    private readonly AppDbContext _db;

    public WatchPlanRepository(AppDbContext db) => _db = db;

    public async Task<List<WatchPlan>> GetAllAsync()
    {
        return await _db.WatchPlans
            .Include(p => p.Movie)
            .OrderBy(p => p.PlannedDate)
            .ToListAsync();
    }

    public async Task<List<WatchPlan>> GetPendingPlansAsync()
    {
        return await _db.WatchPlans
            .Include(p => p.Movie)
            .Where(p => !p.IsCompleted)
            .OrderBy(p => p.PlannedDate)
            .ToListAsync();
    }

    public async Task<List<WatchPlan>> GetCompletedPlansAsync()
    {
        return await _db.WatchPlans
            .Include(p => p.Movie)
            .Where(p => p.IsCompleted)
            .OrderByDescending(p => p.UpdatedAt)
            .ToListAsync();
    }

    public async Task<List<WatchPlan>> GetPlansByDateAsync(DateTime date)
    {
        var startOfDay = date.Date;
        var endOfDay = startOfDay.AddDays(1);
        
        return await _db.WatchPlans
            .Include(p => p.Movie)
            .Where(p => p.PlannedDate >= startOfDay && p.PlannedDate < endOfDay)
            .OrderBy(p => p.PlannedDate)
            .ToListAsync();
    }

    public async Task<WatchPlan?> GetByIdAsync(int id)
    {
        return await _db.WatchPlans
            .Include(p => p.Movie)
            .FirstOrDefaultAsync(p => p.Id == id);
    }

    public async Task<WatchPlan> AddAsync(WatchPlan plan)
    {
        plan.CreatedAt = DateTime.UtcNow;
        _db.WatchPlans.Add(plan);
        await _db.SaveChangesAsync();
        return plan;
    }

    public async Task<WatchPlan> UpdateAsync(WatchPlan plan)
    {
        plan.UpdatedAt = DateTime.UtcNow;
        _db.WatchPlans.Update(plan);
        await _db.SaveChangesAsync();
        return plan;
    }

    public async Task DeleteAsync(int id)
    {
        var plan = await _db.WatchPlans.FindAsync(id);
        if (plan != null)
        {
            _db.WatchPlans.Remove(plan);
            await _db.SaveChangesAsync();
        }
    }

    public async Task CompletePlanAsync(int id)
    {
        var plan = await _db.WatchPlans.FindAsync(id);
        if (plan != null)
        {
            plan.IsCompleted = true;
            plan.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
    }

    public async Task<bool> HasPlanAsync(int movieId)
    {
        return await _db.WatchPlans.AnyAsync(p => p.MovieId == movieId && !p.IsCompleted);
    }
}