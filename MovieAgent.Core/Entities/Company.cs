using System.ComponentModel.DataAnnotations;

namespace MovieAgent.Core.Entities;

public class Company
{
    [Key]
    public int Id { get; set; }

    [MaxLength(100)]
    public string? TmdbId { get; set; }

    [Required, MaxLength(500)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Description { get; set; }

    [MaxLength(500)]
    public string? LogoPath { get; set; }

    [MaxLength(200)]
    public string? OriginCountry { get; set; }

    [MaxLength(500)]
    public string? Headquarters { get; set; }

    [MaxLength(500)]
    public string? Homepage { get; set; }

    [MaxLength(200)]
    public string? ParentCompany { get; set; }

    [MaxLength(2000)]
    public string? MovieList { get; set; }

    [MaxLength(2000)]
    public string? PersonList { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}