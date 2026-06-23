namespace MovieAgent.Core.Models;

public class MovieFilter
{
    public List<string>? Genres { get; set; }
    public int? MinYear { get; set; }
    public int? MaxYear { get; set; }
    public double? MinRating { get; set; }
    public double? MaxRating { get; set; }
    public bool? IsWatched { get; set; }
    public bool? IsFavorite { get; set; }
    public string? Resolution { get; set; }
    public string? SearchKeyword { get; set; }
    public string? SortBy { get; set; }
    public bool SortDescending { get; set; } = true;
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    
    public List<string>? Countries { get; set; }
    public List<string>? Languages { get; set; }
    public string? VideoCodec { get; set; }
    public string? HdrType { get; set; }
    
    public List<string>? Tags { get; set; }
    
    public List<string>? Director { get; set; }
    public List<string>? Cast { get; set; }
    public bool? IsTVSeries { get; set; }
}