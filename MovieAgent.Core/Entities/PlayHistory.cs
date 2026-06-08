using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieAgent.Core.Entities;

public class PlayHistory
{
    [Key]
    public int Id { get; set; }

    public int MovieId { get; set; }

    [ForeignKey(nameof(MovieId))]
    public Movie? Movie { get; set; }

    public DateTime PlayedAt { get; set; } = DateTime.UtcNow;

    public int? Progress { get; set; }

    public int? Duration { get; set; }

    public bool Completed { get; set; }

    public string? Device { get; set; }
}
