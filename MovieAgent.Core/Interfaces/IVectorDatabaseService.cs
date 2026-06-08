namespace MovieAgent.Core.Interfaces;

public interface IVectorDatabaseService
{
    Task EnsureDatabaseAsync();
    Task AddMovieAsync(int movieId, float[] vector, string title, string? overview = null);
    Task UpdateMovieAsync(int movieId, float[] vector, string title, string? overview = null);
    Task RemoveMovieAsync(int movieId);
    Task<List<VectorSearchResult>> SearchAsync(string queryText, int topK = 10);
    Task<List<VectorSearchResult>> SearchByVectorAsync(float[] queryVector, int topK = 10);
    Task<float[]> GenerateEmbeddingAsync(string text);
    Task<int> GetRecordCountAsync();
    Task<bool> HasMovieAsync(int movieId);
}

public class VectorSearchResult
{
    public int MovieId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Overview { get; set; }
    public double Similarity { get; set; }
    public float[]? Vector { get; set; }
}
