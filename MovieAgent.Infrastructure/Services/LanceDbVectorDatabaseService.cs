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
    private static int VectorDimension = 768;
    private static readonly ILoggerService _logger = new LoggerService();

    public LanceDbVectorDatabaseService(string? embeddingEndpoint = null)
    {
        _dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "lancedb");
        var endpoint = embeddingEndpoint ?? "http://localhost:11434";
        _httpClient = new HttpClient { BaseAddress = new Uri(endpoint) };
    }

    // 初始化数据库连接，打开或创建表
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
                Log($"[LanceDB] 已加载电影表，共记录数: {await _moviesTable.CountRows()}");
            }
            else
            {
                // 创建表需要先定义 schema
                var vectorField = new Field("item", FloatType.Default, nullable: false);
                var vectorType = new FixedSizeListType(vectorField, VectorDimension);

                var schema = CreateTableSchema();
                var options = new CreateTableOptions { Mode = "create", Schema = schema };
                _moviesTable = await _connection.CreateTable("movies", options);
                Log($"[LanceDB] 已创建新表，维度 {VectorDimension}");
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

    // 创建 Arrow Schema，定义 vector 列，固定大小列表类型
    private static Schema CreateTableSchema()
    {
        var schema = new Schema.Builder()
      .Field(f => f.Name("movie_id").DataType(Int32Type.Default).Nullable(false))
      .Field(f => f.Name("title").DataType(StringType.Default).Nullable(false))
      .Field(f => f.Name("overview").DataType(StringType.Default).Nullable(true))  // 简介可选
      .Field(f => f.Name("vector").DataType(new FixedSizeListType(new Field("item", new FloatType(), false), VectorDimension)).Nullable(false))
      .Field(f => f.Name("created_at").DataType(new TimestampType(TimeUnit.Microsecond, "UTC")).Nullable(false))
      .Field(f => f.Name("updated_at").DataType(new TimestampType(TimeUnit.Microsecond, "UTC")).Nullable(false))
      .Build();
        return schema;
    }

    // 创建向量索引（IVF）
    public async Task CreateIndexAsync(int numPartitions = 128)
    {
        await EnsureDatabaseAsync();
        if (_moviesTable == null) return;

        try
        {
            Log($"[LanceDB] 开始创建 IVF 索引，分区数: {numPartitions}");
            var recordCount = await _moviesTable.CountRows();
            if (recordCount < 100)
            {
                Log("[LanceDB] 记录数少于100，跳过索引创建");
                return;
            }
            Log("[LanceDB] IVF 索引创建已跳过（LanceDB .NET API 限制）");
        }
        catch (Exception ex)
        {
            Log($"[LanceDB] 索引创建失败: {ex.Message}");
        }
    }

    // 检查是否已存在索引
    public async Task<bool> HasIndexAsync()
    {
        await EnsureDatabaseAsync();
        return false;
    }

    // 使用 Ollama 生成嵌入向量
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
            Log($"[LanceDB] 向量生成失败: {ex.Message}");
            return Array.Empty<float>();
        }
    }
 

    // 添加或更新电影记录
    public async Task AddMovieAsync(int movieId, float[] vector, string title, string? overview = null)
    {
        await EnsureDatabaseAsync();
        if (_moviesTable == null) throw new InvalidOperationException("表未初始化");

        // 确保向量维度正确
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

    // 删除记录
    public async Task RemoveMovieAsync(int movieId)
    {
        await EnsureDatabaseAsync();
        if (_moviesTable == null) return;
        await _moviesTable.Delete($"movie_id = {movieId}");
        Log($"[LanceDB] 删除成功: movie_id = {movieId}");
    }

    // 向量相似度搜索模型
    public async Task<List<VectorSearchResult>> SearchByVectorAsync(float[] queryVector, int topK = 10)
        {
            await EnsureDatabaseAsync();
            if (_moviesTable == null) return new List<VectorSearchResult>();

            try
            {
                if (queryVector == null || queryVector.Length == 0)
                {
                    Log("[LanceDB] 查询向量为空");
                    return new List<VectorSearchResult>();
                }

                var batches = await _moviesTable.Query()
                    .NearestTo(queryVector)
                    .Limit(topK)
                    .ToList();

                var results = new List<VectorSearchResult>();
                Log($"[LanceDB] 搜索结果数量: {batches.Count}");
                
                if (batches.Count == 0)
                    return results;

                var distances = new List<float>();
                foreach (var row in batches)
                {
                    if (row.TryGetValue("_distance", out var distanceObj))
                    {
                        float distance = 1f;
                        if (distanceObj is float f)
                            distance = f;
                        else if (distanceObj is double d)
                            distance = (float)d;
                        else if (float.TryParse(distanceObj.ToString(), out float parsed))
                            distance = parsed;
                        distances.Add(distance);
                    }
                }

                float maxDistance = distances.Any() ? distances.Max() : 1f;
                float minDistance = distances.Any() ? distances.Min() : 0f;
                float distanceRange = maxDistance - minDistance;
                if (distanceRange < 0.0001f) distanceRange = 1f;

                foreach (var row in batches)
                {
                    float distance = 1f;
                    if (row.TryGetValue("_distance", out var distanceObj))
                    {
                        if (distanceObj is float f)
                            distance = f;
                        else if (distanceObj is double d)
                            distance = (float)d;
                        else if (float.TryParse(distanceObj.ToString(), out float parsed))
                            distance = parsed;
                        
                        Log($"[LanceDB] 距离值类型: {distanceObj.GetType().Name}, 值: {distanceObj}");
                    }
                    else
                    {
                        Log("[LanceDB] 未找到 _distance 字段");
                    }
                    
                    float similarity = 1.0f - ((distance - minDistance) / distanceRange);
                    similarity = Math.Max(0.01f, Math.Min(1f, similarity));
                    Log($"[LanceDB] movie_id={row["movie_id"]}, distance={distance}, similarity={similarity:F4}");
                    
                    results.Add(new VectorSearchResult
                    {
                        MovieId = Convert.ToInt32(row["movie_id"]),
                        Title = row["title"]?.ToString() ?? "",
                        Overview = row["overview"]?.ToString(),
                        Similarity = similarity,
                        Vector = row["vector"] as float[]
                    });
                }

            return results;
        }
        catch (Exception ex)
        {
            Log($"[LanceDB] 搜索执行失败: {ex.Message}");
            return new List<VectorSearchResult>();
        }
    }

    // 文本搜索转换为向量搜索
    public async Task<List<VectorSearchResult>> SearchAsync(string queryText, int topK = 10)
    {
        var vec = await GenerateEmbeddingAsync(queryText);
        return await SearchByVectorAsync(vec, topK);
    }

    // 获取数据库记录数
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

        // 2. 然后添加向量列表元素
        foreach (var value in movie.Vector)
        {
            valueBuilder.Append(value);
        }

        // 3. 会有多余的填充 1-2 次
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
// 私有转换方法，将内存对象 MovieRecord 转换为 RecordBatch
 
    // 私有内部类
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
        => _logger.Debug($"[{DateTime.Now:HH:mm:ss.fff}] {message}");

    public async ValueTask DisposeAsync()
    {
        if (_connection != null)
            _connection.Dispose();
        _httpClient.Dispose();
        await Task.CompletedTask;
    }

    public async Task<bool> HasMovieAsync(int movieId)
    {
        await EnsureDatabaseAsync();
        if (_moviesTable == null) return false;

        try
        {
            // 使用 Where 过滤记录，直接写字段条件
            var results = await _moviesTable.Query()
                .Where($"movie_id = {movieId}")
                .Limit(1)
                .ToList();

            return results != null && results.Count > 0;
        }
        catch (Exception ex)
        {
            Log($"[LanceDB] 电影影响查询失败: {ex.Message}");
            return false;
        }
    }
}
