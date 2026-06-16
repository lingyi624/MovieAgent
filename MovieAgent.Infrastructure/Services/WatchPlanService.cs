using MovieAgent.Core.Entities;
using MovieAgent.Core.Interfaces;

namespace MovieAgent.Infrastructure.Services;

public interface IWatchPlanService
{
    Task<List<WatchPlan>> GetAllPlansAsync();
    Task<List<WatchPlan>> GetPendingPlansAsync();
    Task<List<WatchPlan>> GetCompletedPlansAsync();
    Task<List<WatchPlan>> GetPlansByDateAsync(DateTime date);
    Task<WatchPlan?> GetPlanAsync(int planId);
    Task<WatchPlan> CreatePlanAsync(int movieId, DateTime plannedDate, string? note = null);
    Task<WatchPlan> UpdatePlanAsync(int planId, DateTime? plannedDate = null, string? note = null);
    Task DeletePlanAsync(int planId);
    Task CompletePlanAsync(int planId);
    Task<bool> HasPlanAsync(int movieId);
}

public class WatchPlanService : IWatchPlanService
{
    private readonly IWatchPlanRepository _repo;

    public WatchPlanService(IWatchPlanRepository repo)
    {
        _repo = repo;
    }

    public async Task<List<WatchPlan>> GetAllPlansAsync()
    {
        return await _repo.GetAllAsync();
    }

    public async Task<List<WatchPlan>> GetPendingPlansAsync()
    {
        return await _repo.GetPendingPlansAsync();
    }

    public async Task<List<WatchPlan>> GetCompletedPlansAsync()
    {
        return await _repo.GetCompletedPlansAsync();
    }

    public async Task<List<WatchPlan>> GetPlansByDateAsync(DateTime date)
    {
        return await _repo.GetPlansByDateAsync(date);
    }

    public async Task<WatchPlan?> GetPlanAsync(int planId)
    {
        return await _repo.GetByIdAsync(planId);
    }

    public async Task<WatchPlan> CreatePlanAsync(int movieId, DateTime plannedDate, string? note = null)
    {
        var plan = new WatchPlan
        {
            MovieId = movieId,
            PlannedDate = plannedDate.ToUniversalTime(),
            Note = note,
            IsCompleted = false,
            CreatedAt = DateTime.UtcNow
        };
        
        return await _repo.AddAsync(plan);
    }

    public async Task<WatchPlan> UpdatePlanAsync(int planId, DateTime? plannedDate = null, string? note = null)
    {
        var plan = await _repo.GetByIdAsync(planId);
        if (plan == null)
            throw new ArgumentException("观影计划不存在");
        
        if (plannedDate.HasValue)
            plan.PlannedDate = plannedDate.Value.ToUniversalTime();
        
        if (note != null)
            plan.Note = note;
        
        plan.UpdatedAt = DateTime.UtcNow;
        
        return await _repo.UpdateAsync(plan);
    }

    public async Task DeletePlanAsync(int planId)
    {
        await _repo.DeleteAsync(planId);
    }

    public async Task CompletePlanAsync(int planId)
    {
        await _repo.CompletePlanAsync(planId);
    }

    public async Task<bool> HasPlanAsync(int movieId)
    {
        return await _repo.HasPlanAsync(movieId);
    }
}