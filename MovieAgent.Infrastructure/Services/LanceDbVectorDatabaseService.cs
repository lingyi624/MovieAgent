using Apache.Arrow;
using Apache.Arrow.Types;
using FFmpeg.AutoGen;
using lancedb;
using MovieAgent.Core.Interfaces;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Array = System.Array;

namespace MovieAgent.Infrastructure.Services;

public class LanceDbVectorDatabaseService : IVectorDatabaseService, IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly HttpClient _httpClient;
    private Connection? _connection;
    private lancedb.Table? _moviesTable;
    private bool _initialized;
    private  static int VectorDimension = 768; // 与 embedding 模型匹配（nomic-embed-text） 原来是768

    public LanceDbVectorDatabaseService()
    {
        _dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "lancedb");
        _httpClient = new HttpClient { BaseAddress = new Uri("http://localhost:11434") };
       
    }

    // 初始化：连接数据库，打开或创建表
    public async Task EnsureDatabaseAsync()
    {
        if (_initialized) return;

        try
        {
            Directory.CreateDirectory(_dbPath);
            _connection = new Connection();
            await _connection.Connect(_dbPath);

            // 检查表是否存在
            var tableNames = await _connection.TableNames();
            if (tableNames.Contains("movies"))
            {
                 _moviesTable =await _connection.OpenTable("movies");
                Log($"[LanceDB] 打开已有表，记录数: {await _moviesTable.CountRows()}");
            }
            else
            {
                // 创建表，需要先定义 schema
                var vectorField = new Field("item", FloatType.Default, nullable: false);
                var vectorType = new FixedSizeListType(vectorField, VectorDimension);

                var schema = CreateTableSchema();
                var options = new CreateTableOptions { Mode = "create", Schema = schema };
                _moviesTable = await _connection.CreateTable("movies", options);
                Log($"[LanceDB] 创建新表，向量维度 {VectorDimension}");
            }

            _initialized = true;
            Log("[LanceDB] 初始化成功");
        }
        catch (Exception ex)
        {
            Log($"[LanceDB] 初始化失败: {ex.Message}");
            throw;
        }
    }

    // 定义 Arrow Schema，包括 vector 列（固定大小列表）
    private static Schema CreateTableSchema()
    {
        var schema = new Schema.Builder()
      .Field(f => f.Name("movie_id").DataType(Int32Type.Default).Nullable(false))
      .Field(f => f.Name("title").DataType(StringType.Default).Nullable(false))
      .Field(f => f.Name("overview").DataType(StringType.Default).Nullable(true))  // ← 添加这个
      .Field(f => f.Name("vector").DataType(new FixedSizeListType(new Field("item", new FloatType(), false), VectorDimension)).Nullable(false))
      .Field(f => f.Name("created_at").DataType(new TimestampType(TimeUnit.Microsecond, "UTC")).Nullable(false))
      .Field(f => f.Name("updated_at").DataType(new TimestampType(TimeUnit.Microsecond, "UTC")).Nullable(false))
      .Build();
        return schema;
    }

    // 生成嵌入向量（调用 Ollama）
    public async Task<float[]> GenerateEmbeddingAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<float>();

        try
        {
            var request = new { model = "nomic-embed-text", prompt = text };
            var response = await _httpClient.PostAsJsonAsync("/api/embeddings", request);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<EmbeddingResponse>();
            return result?.Embedding ?? Array.Empty<float>();
        }
        catch (Exception ex)
        {
            Log($"[LanceDB] 生成向量失败: {ex.Message}");
            return Array.Empty<float>();
        }
    }
 

    // 添加或更新（先删后加）
    public async Task AddMovieAsync(int movieId, float[] vector, string title, string? overview = null)
    {
        await EnsureDatabaseAsync();
        if (_moviesTable == null) throw new InvalidOperationException("表未初始化");

        // 确保向量长度正确
        if (vector.Length != VectorDimension)
            vector = vector.Take(VectorDimension).ToArray();

        var now = DateTime.UtcNow;
        var record = new MovieRecord
        {
            MovieId = movieId,
            Title = title,
            Overview = overview ?? "",
            Vector = vector,
            CreatedAt = now,
            UpdatedAt = now
        };

        // 删除旧记录
        await _moviesTable.Delete($"movie_id = {movieId}");
        // 添加新记录
        var batch = ConvertToRecordBatch(record);
        await _moviesTable.Add(batch);
        Log($"[LanceDB] 添加/更新成功: {title} (id={movieId})");
    }

    public async Task UpdateMovieAsync(int movieId, float[] vector, string title, string? overview = null)
        => await AddMovieAsync(movieId, vector, title, overview);

    // 删除
    public async Task RemoveMovieAsync(int movieId)
    {
        await EnsureDatabaseAsync();
        if (_moviesTable == null) return;
        await _moviesTable.Delete($"movie_id = {movieId}");
        Log($"[LanceDB] 删除成功: movie_id = {movieId}");
    }

    // 向量检索（核心）
    public async Task<List<VectorSearchResult>> SearchByVectorAsync(float[] queryVector, int topK = 10)
    {
        await EnsureDatabaseAsync();
        if (_moviesTable == null) return new List<VectorSearchResult>();

        try
        {
            var batches = await _moviesTable.Query()
                .NearestTo(queryVector)
                .Limit(topK)
                .ToList();

            var results = new List<VectorSearchResult>();
            foreach (var row in batches)  // row 是 Dictionary<string, object>
            {
                results.Add(new VectorSearchResult
                {
                    MovieId = Convert.ToInt32(row["movie_id"]),
                    Title = row["title"]?.ToString() ?? "",
                    Overview = row["overview"]?.ToString(),
                    Similarity = 1 - Convert.ToSingle(row["_distance"]),
                    Vector = row["vector"] as float[]  // 向量可能已经是 float[]
                });
            }
            //foreach (var batch in batches)
            //{
            //    var movieIds = batch["movie_id"] as Int32Array;
            //    var titles = batch["title"] as StringArray;
            //    var overviews = batch["overview"] as StringArray;
            //    var vectors = batch["vector"] as FixedSizeListArray;
            //    var distances = batch["_distance"] as FloatArray;

            //    for (int i = 0; i < batch.Count; i++)
            //    {
            //        // 正确提取 FixedSizeListArray 中的向量
            //        float[] vector = null;
            //        if (vectors != null)
            //        {
            //            // 获取第 i 个列表切片
            //            var slice = vectors.GetSlicedValues(i);
            //            var floatArray = slice as FloatArray;
            //            if (floatArray != null)
            //            {
            //                vector = new float[floatArray.Length];
            //                for (int j = 0; j < floatArray.Length; j++)
            //                {
            //                    vector[j] = floatArray.GetValue(j).Value;
            //                }
            //            }
            //        }

            //        results.Add(new VectorSearchResult
            //        {
            //            MovieId = movieIds?.GetValue(i) ?? 0,
            //            Title = titles?.GetString(i) ?? "",
            //            Overview = overviews?.GetString(i),
            //            Similarity = distances != null ? 1 - distances.GetValue(i).Value : 0,
            //            Vector = vector
            //        });
            //    }
            //}

            // 注意：LanceDB 已经按距离排序返回了，这里再排序其实多余
            // 直接返回 results 即可，不需要再次 OrderByDescending
            return results;
        }
        catch (Exception ex)
        {
            Log($"[LanceDB] 向量搜索失败: {ex.Message}");
            return new List<VectorSearchResult>();
        }
    }

    // 文本搜索（先转为向量再检索）
    public async Task<List<VectorSearchResult>> SearchAsync(string queryText, int topK = 10)
    {
        var vec = await GenerateEmbeddingAsync(queryText);
        return await SearchByVectorAsync(vec, topK);
    }

    // 辅助方法：获取记录数
    public async Task<int> GetRecordCountAsync()
    {
        await EnsureDatabaseAsync();
        return _moviesTable == null ? 0 : (int)await _moviesTable.CountRows();
    }


 

    private static RecordBatch ConvertToRecordBatch(MovieRecord movie)
{
    var movieIdBuilder = new Int32Array.Builder();
    var titleBuilder = new StringArray.Builder(); 
    var overviewBuilder = new StringArray.Builder();
    var vectorBuilder = new FixedSizeListArray.Builder(new FloatType(), VectorDimension);
    var createdAtBuilder = new TimestampArray.Builder(TimeUnit.Microsecond, "UTC");
    var updatedAtBuilder = new TimestampArray.Builder(TimeUnit.Microsecond, "UTC");

  
        movieIdBuilder.Append(movie.MovieId);
        titleBuilder.Append(movie.Title);
        overviewBuilder.Append(movie.Overview ?? "");
        var valueBuilder = vectorBuilder.ValueBuilder as FloatArray.Builder;

        // 1. 先调用 Append() 开始一个新的列表
        vectorBuilder.Append();

        // 2. 然后添加这个列表的元素
        foreach (var value in movie.Vector)
        {
            valueBuilder.Append(value);
        }

        // 3. 如果有多个向量，重复 1-2 步骤
        // 4. 最后 Build
        var vectorsArray = vectorBuilder.Build();

        createdAtBuilder.Append(movie.CreatedAt);
        updatedAtBuilder.Append(movie.UpdatedAt);

        var vectorField = new Field("item", FloatType.Default, nullable: false);
        var vectorType = new FixedSizeListType(vectorField, VectorDimension);
        var schema = CreateTableSchema();

    return new RecordBatch(
        schema,
        new IArrowArray[]
        {
            movieIdBuilder.Build(),
            titleBuilder.Build(),
            overviewBuilder.Build(),
            vectorsArray,
            createdAtBuilder.Build(),
            updatedAtBuilder.Build()
        },
        1
    );
}

private static long ToArrowTimestamp(DateTimeOffset dateTime)
{
    var unixEpoch = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);
    return (long)(dateTime - unixEpoch).TotalMicroseconds;
}
// 私有转换方法：将单个 MovieRecord 转为 RecordBatch
 
    // 辅助内部类
    private class MovieRecord
    {
        public int MovieId { get; set; }
        public string Title { get; set; } = "";
        public string Overview { get; set; } = "";
        public float[] Vector { get; set; } = Array.Empty<float>();
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    private class EmbeddingResponse
    {
        public float[]? Embedding { get; set; }
    }

    private static void Log(string message)
        => System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");

    public async ValueTask DisposeAsync()
    {
        if (_connection != null)
            _connection.Dispose();
        _httpClient.Dispose();
        await Task.CompletedTask;
    }

    public async  Task<bool> HasMovieAsync(int movieId)
    {
        await EnsureDatabaseAsync();
        if (_moviesTable == null) return false;

        try
        {
            // 使用 Where 进行过滤，直接写字段条件
            var results = await _moviesTable.Query()
                .Where($"movie_id = {movieId}")
                .Limit(1)
                .ToList();

            return results != null && results.Count > 0;
        }
        catch (Exception ex)
        {
            Log($"[LanceDB] 检查电影存在性失败: {ex.Message}");
            return false;
        }
    }
}