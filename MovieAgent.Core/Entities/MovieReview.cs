namespace MovieAgent.Core.Entities;

public class MovieReview
{
    public int Id { get; set; }
    public int MovieId { get; set; }
    public string Content { get; set; } = string.Empty;
    public int Rating { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    
    public Movie? Movie { get; set; }
}