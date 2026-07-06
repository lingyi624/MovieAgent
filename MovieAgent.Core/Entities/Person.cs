using System.ComponentModel.DataAnnotations;

namespace MovieAgent.Core.Entities;

public class Person
{
    [Key]
    public int Id { get; set; }

    [MaxLength(100)]
    public string? TmdbId { get; set; }

    [Required, MaxLength(500)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? OriginalName { get; set; }

    [MaxLength(500)]
    public string? Biography { get; set; }

    [MaxLength(200)]
    public string? ProfilePath { get; set; }

    public DateTime? Birthday { get; set; }

    public DateTime? Deathday { get; set; }

    [MaxLength(200)]
    public string? PlaceOfBirth { get; set; }

    [MaxLength(50)]
    public string? Gender { get; set; }

    [MaxLength(200)]
    public string? KnownForDepartment { get; set; }

    public double? Popularity { get; set; }

    [MaxLength(500)]
    public string? AlsoKnownAs { get; set; }

    [MaxLength(2000)]
    public string? KnownForTitles { get; set; }

    [MaxLength(2000)]
    public string? Credits { get; set; }

    [MaxLength(500)]
    public string? Company { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}