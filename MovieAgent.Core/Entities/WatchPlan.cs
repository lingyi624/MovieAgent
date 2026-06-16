namespace MovieAgent.Core.Entities;

public class WatchPlan
{
    public int Id { get; set; }
    public int MovieId { get; set; }
    public DateTime PlannedDate { get; set; }
    public string? Note { get; set; }
    public bool IsCompleted { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    
    public Movie? Movie { get; set; }
}