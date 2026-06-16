using MovieAgent.Core.Entities;

namespace MovieAgent.Core.Interfaces;

public interface IWatchPlanRepository
{
    Task<List<WatchPlan>> GetAllAsync();
    Task<List<WatchPlan>> GetPendingPlansAsync();
    Task<List<WatchPlan>> GetCompletedPlansAsync();
    Task<List<WatchPlan>> GetPlansByDateAsync(DateTime date);
    Task<WatchPlan?> GetByIdAsync(int id);
    Task<WatchPlan> AddAsync(WatchPlan plan);
    Task<WatchPlan> UpdateAsync(WatchPlan plan);
    Task DeleteAsync(int id);
    Task CompletePlanAsync(int id);
    Task<bool> HasPlanAsync(int movieId);
}