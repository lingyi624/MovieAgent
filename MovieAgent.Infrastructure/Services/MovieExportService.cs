using MovieAgent.Core.Entities;
using MovieAgent.Core.Interfaces;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace MovieAgent.Infrastructure.Services;

public class MovieExportService : IMovieExportService
{
    private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<string> ExportToJsonAsync(List<Movie> movies)
    {
        await Task.CompletedTask;
        return JsonSerializer.Serialize(movies, _jsonOptions);
    }

    public async Task<List<Movie>> ImportFromJsonAsync(string jsonContent)
    {
        await Task.CompletedTask;
        return JsonSerializer.Deserialize<List<Movie>>(jsonContent) ?? new List<Movie>();
    }

    public async Task<string> ExportToCsvAsync(List<Movie> movies)
    {
        await Task.CompletedTask;
        
        var sb = new StringBuilder();
        // CSV header
        sb.AppendLine("Id,TmdbId,Title,OriginalTitle,Overview,PosterPath,BackdropPath,ReleaseYear,Rating,Runtime,Genres,FilePath,FileSize,VideoCodec,AudioCodec,Resolution,IsWatched,UserRating,WatchedAt,IsFavorite,CreatedAt,UpdatedAt,Tags,Director,Cast,HdrType");
        
        foreach (var movie in movies)
        {
            sb.AppendLine($"{movie.Id},{EscapeCsv(movie.TmdbId)},{EscapeCsv(movie.Title)},{EscapeCsv(movie.OriginalTitle)},{EscapeCsv(movie.Overview)},{EscapeCsv(movie.PosterPath)},{EscapeCsv(movie.BackdropPath)},{movie.ReleaseYear},{movie.Rating},{movie.Runtime},{EscapeCsv(movie.Genres)},{EscapeCsv(movie.FilePath)},{movie.FileSize},{EscapeCsv(movie.VideoCodec)},{EscapeCsv(movie.AudioCodec)},{EscapeCsv(movie.Resolution)},{movie.IsWatched},{movie.UserRating},{FormatDateTime(movie.WatchedAt)},{movie.IsFavorite},{FormatDateTime(movie.CreatedAt)},{FormatDateTime(movie.UpdatedAt)},{EscapeCsv(movie.Tags)},{EscapeCsv(movie.Director)},{EscapeCsv(movie.Cast)},{EscapeCsv(movie.HdrType)}");
        }
        
        return sb.ToString();
    }

    public async Task<List<Movie>> ImportFromCsvAsync(string csvContent)
    {
        await Task.CompletedTask;
        
        var movies = new List<Movie>();
        var lines = csvContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        
        if (lines.Length <= 1) return movies;
        
        // Skip header
        for (int i = 1; i < lines.Length; i++)
        {
            var movie = ParseCsvLine(lines[i]);
            if (movie != null)
            {
                movies.Add(movie);
            }
        }
        
        return movies;
    }

    public async Task<string> ExportToFileAsync(List<Movie> movies, string filePath, ExportFormat format)
    {
        var content = format == ExportFormat.Json 
            ? await ExportToJsonAsync(movies) 
            : await ExportToCsvAsync(movies);
        
        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);
        return filePath;
    }

    public async Task<List<Movie>> ImportFromFileAsync(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLower();
        var content = await File.ReadAllTextAsync(filePath, Encoding.UTF8);
        
        return extension switch
        {
            ".json" => await ImportFromJsonAsync(content),
            ".csv" => await ImportFromCsvAsync(content),
            _ => new List<Movie>()
        };
    }

    private string EscapeCsv(string? value)
    {
        if (value == null) return string.Empty;
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        return value;
    }

    private string FormatDateTime(DateTime? dateTime)
    {
        return dateTime?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private DateTime? ParseDateTime(string value)
    {
        if (DateTime.TryParseExact(value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            return dt;
        }
        return null;
    }

    private Movie? ParseCsvLine(string line)
    {
        try
        {
            var values = ParseCsvValues(line);
            
            if (values.Count < 26) return null;
            
            return new Movie
            {
                Id = int.TryParse(values[0], out var id) ? id : 0,
                TmdbId = values[1],
                Title = values[2] ?? string.Empty,
                OriginalTitle = values[3],
                Overview = values[4],
                PosterPath = values[5],
                BackdropPath = values[6],
                ReleaseYear = int.TryParse(values[7], out var year) ? year : null,
                Rating = double.TryParse(values[8], out var rating) ? rating : null,
                Runtime = int.TryParse(values[9], out var runtime) ? runtime : null,
                Genres = values[10],
                FilePath = values[11] ?? string.Empty,
                FileSize = long.TryParse(values[12], out var size) ? size : 0,
                VideoCodec = values[13],
                AudioCodec = values[14],
                Resolution = values[15],
                IsWatched = bool.TryParse(values[16], out var watched) ? watched : false,
                UserRating = int.TryParse(values[17], out var userRating) ? userRating : null,
                WatchedAt = ParseDateTime(values[18]),
                IsFavorite = bool.TryParse(values[19], out var favorite) ? favorite : false,
                CreatedAt = DateTime.TryParse(values[20], out var createdAt) ? createdAt : DateTime.UtcNow,
                UpdatedAt = DateTime.TryParse(values[21], out var updatedAt) ? updatedAt : DateTime.UtcNow,
                Tags = values[22],
                Director = values[23],
                Cast = values[24],
                HdrType = values[25]
            };
        }
        catch
        {
            return null;
        }
    }

    private List<string?> ParseCsvValues(string line)
    {
        var values = new List<string?>();
        var currentValue = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    currentValue.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                values.Add(currentValue.ToString());
                currentValue.Clear();
            }
            else
            {
                currentValue.Append(c);
            }
        }
        
        values.Add(currentValue.ToString());
        return values;
    }
}