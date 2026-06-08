using MovieAgent.Core.Entities;

namespace MovieAgent.Core.Interfaces;

public interface IPlayHistoryService
{
    Task AddPlayHistoryAsync(int movieId, int? progress = null, int? duration = null, bool completed = false);
    Task<List<PlayHistory>> GetRecentHistoryAsync(int count = 10);
    Task<PlayHistory?> GetLastPlayHistoryAsync(int movieId);
    Task<int> GetWatchedCountAsync(DateTime? startDate = null, DateTime? endDate = null);
    Task<TimeSpan> GetTotalWatchTimeAsync(DateTime? startDate = null, DateTime? endDate = null);
    Task<List<DailyWatchCount>> GetDailyWatchCountAsync(int days = 30);
}

public class DailyWatchCount
{
    public DateTime Date { get; set; }
    public int Count { get; set; }
}
