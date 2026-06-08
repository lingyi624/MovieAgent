using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieAgent.Core.Entities;

public class Movie
{
    [Key]
    public int Id { get; set; }

    [MaxLength(50)]
    public string? TmdbId { get; set; }

    [Required, MaxLength(500)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? OriginalTitle { get; set; }

    [MaxLength(4000)]
    public string? Overview { get; set; }

    [MaxLength(500)]
    public string? PosterPath { get; set; }

    [MaxLength(500)]
    public string? BackdropPath { get; set; }

    public int? ReleaseYear { get; set; }

    public double? Rating { get; set; }

    public int? Runtime { get; set; }

    /// <summary>JSON array of genre strings</summary>
    [MaxLength(1000)]
    public string? Genres { get; set; }

    /// <summary>UNC or local file path</summary>
    [Required, MaxLength(2000)]
    public string FilePath { get; set; } = string.Empty;

    public long FileSize { get; set; }

    [MaxLength(50)]
    public string? VideoCodec { get; set; }

    [MaxLength(50)]
    public string? AudioCodec { get; set; }

    [MaxLength(20)]
    public string? Resolution { get; set; }

    public bool IsWatched { get; set; }

    public int? UserRating { get; set; }

    public DateTime? WatchedAt { get; set; }

    public bool IsFavorite { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>JSON array of custom tags</summary>
    [MaxLength(2000)]
    public string? Tags { get; set; }

    [MaxLength(500)]
    public string? Director { get; set; }

    [MaxLength(2000)]
    public string? Cast { get; set; }

    /// <summary>HDR type: HDR10 / DolbyVision / SDR</summary>
    [MaxLength(50)]
    public string? HdrType { get; set; }
}