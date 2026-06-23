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
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Array = System.Array;

namespace MovieAgent.Infrastructure.Services;

public class LanceDbVectorDatabaseService : IVectorDatabaseService, IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly HttpClient _httpClient;
    private readonly string _embeddingModel;
    private Connection? _connection;
    private lancedb.Table? _moviesTable;
    private bool _initialized;
    private static int VectorDimension = 768;  // 默认768维，支持配置

    private static ILoggerService? _logger;
    private static readonly object _loggerLock = new object();
    
    private static ILoggerService Logger
    {
        get
        {
            if (_logger == null)
            {
                lock (_loggerLock)
                {
                    if (_logger == null)
                    {
                        try
                        {
                            _logger = new LoggerService();
                        }
                        catch
                        {
                            _logger = new SimpleLogger();
                        }
                    }
                }
            }
            return _logger;
        }
    }

    public LanceDbVectorDatabaseService(string? embeddingEndpoint = null, string? embeddingModel = null, int? embeddingDimension = null)
    {
        _dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "lancedb");
        var endpoint = embeddingEndpoint ?? "http://localhost:11434";
        _httpClient = new HttpClient { BaseAddress = new Uri(endpoint) };
        // 确保模型名称格式正确（nomic-embed-text-v2-moe:latest）
        var model = embeddingModel ?? "nomic-embed-text-v2-moe:latest";
        _embeddingModel = model.EndsWith(":latest") ? model : $"{model}:latest";
        
        // 设置向量维度（可选，默认768）
        // 推荐值：768（完整精度）、384（推荐，降低精度提升速度）、256（更快的速度）
        if (embeddingDimension.HasValue && embeddingDimension.Value > 0 && embeddingDimension.Value <= 768)
        {
            VectorDimension = embeddingDimension.Value;
        }
        
        Log($"[LanceDB] 向量维度: {VectorDimension}");
    }
    
    /// <summary>
    /// 调整向量维度（截断或填充到目标维度）
    /// </summary>
    public static float[] AdjustVectorDimension(float[] vector, int targetDimension)
    {
        if (vector.Length == targetDimension)
            return vector;
            
        if (vector.Length > targetDimension)
        {
            // 截断
            return vector.Take(targetDimension).ToArray();
        }
        else
        {
            // 填充（用0填充）
            var result = new float[targetDimension];
            Array.Copy(vector, result, vector.Length);
            return result;
        }
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
            Log($"[LanceDB] 开始创建向量索引，分区数: {numPartitions}");
            var recordCount = await _moviesTable.CountRows();
            Log($"[LanceDB] 当前记录数: {recordCount}");
            
            if (recordCount < 100)
            {
                Log("[LanceDB] 记录数少于100，跳过索引创建");
                return;
            }

            // 检查是否已存在索引
            if (await HasIndexAsync())
            {
                Log("[LanceDB] 索引已存在，跳过创建");
                return;
            }

            // 方法1: 尝试使用 LanceDB 原生 API 创建索引
            try
            {
                // 查找 CreateIndexAsync 方法
                var createIndexMethod = _moviesTable.GetType().GetMethod("CreateIndexAsync");
                if (createIndexMethod != null)
                {
                    // 使用动态对象构建索引配置
                    dynamic indexConfig = CreateIvfIndexConfig(numPartitions);
                    var task = (Task)createIndexMethod.Invoke(_moviesTable, new[] { indexConfig })!;
                    await task;
                    Log($"[LanceDB] 向量索引创建成功（原生API），分区数: {numPartitions}");
                    return;
                }
            }
            catch (Exception apiEx)
            {
                Log($"[LanceDB] 原生API创建索引失败: {apiEx.Message}");
            }

            // 方法2: 尝试通过 SQL 命令创建索引
            try
            {
                var connectionType = _connection!.GetType();
                
                // 尝试 ExecuteAsync 方法
                var executeAsync = connectionType.GetMethod("ExecuteAsync", new[] { typeof(string) });
                if (executeAsync != null)
                {
                    var sql = $"CREATE INDEX idx_vector ON movies USING IVFPQ(vector, num_partitions={numPartitions}, num_sub_vectors=96)";
                    var task = (Task)executeAsync.Invoke(_connection, new[] { sql })!;
                    await task;
                    Log($"[LanceDB] SQL索引创建成功");
                    return;
                }

                // 尝试 Execute 方法
                var execute = connectionType.GetMethod("Execute", new[] { typeof(string) });
                if (execute != null)
                {
                    var sql = $"CREATE INDEX idx_vector ON movies USING IVFPQ(vector, num_partitions={numPartitions})";
                    await (Task)execute.Invoke(_connection, new[] { sql })!;
                    Log($"[LanceDB] SQL索引创建成功");
                    return;
                }
            }
            catch (Exception sqlEx)
            {
                Log($"[LanceDB] SQL创建索引失败: {sqlEx.Message}");
            }

            // 方法3: 尝试通过 Optimize 创建索引
            try
            {
                var optimizeMethod = _moviesTable.GetType().GetMethod("Optimize");
                if (optimizeMethod != null)
                {
                    var task = (Task)optimizeMethod.Invoke(_moviesTable, null)!;
                    await task;
                    Log("[LanceDB] 表优化完成，索引已自动创建");
                }
            }
            catch (Exception optEx)
            {
                Log($"[LanceDB] 表优化失败: {optEx.Message}");
            }

            Log("[LanceDB] 索引创建完成或不可用，向量检索将使用暴力搜索");
        }
        catch (Exception ex)
        {
            Log($"[LanceDB] 索引创建失败: {ex.Message}");
        }
    }

    private object CreateIvfIndexConfig(int numPartitions)
    {
        // 动态创建 LanceDB 的 IvfIndex 配置对象
        var indexType = Type.GetType("LanceDB.IvfIndex, lancedb");
        if (indexType != null)
        {
            return Activator.CreateInstance(indexType, numPartitions)!;
        }
        
        // 如果无法获取类型，返回动态对象
        return new { Type = "IVF", NumPartitions = numPartitions };
    }

    /// <summary>
    /// 检查索引状态并返回详细信息
    /// </summary>
    public async Task<string> GetIndexStatusAsync()
    {
        await EnsureDatabaseAsync();
        if (_moviesTable == null) return "表未初始化";

        var recordCount = await _moviesTable.CountRows();
        var hasIndex = await HasIndexAsync();
        
        return $"记录数: {recordCount}, 索引状态: {(hasIndex ? "已创建" : "未创建")}";
    }

    // 检查是否已存在索引
    public async Task<bool> HasIndexAsync()
    {
        await EnsureDatabaseAsync();
        if (_moviesTable == null) return false;

        try
        {
            // 检查索引目录是否存在（LanceDB 将索引存储在 .lancedb 目录下）
            var indexDir = Path.Combine(_dbPath, "movies.lance", "indices");
            return Directory.Exists(indexDir) && Directory.EnumerateFiles(indexDir).Any();
        }
        catch (Exception ex)
        {
            Log($"[LanceDB] 检查索引失败: {ex.Message}");
            return false;
        }
    }

    // 使用 Ollama 生成嵌入向量（单个）
    public async Task<float[]> GenerateEmbeddingAsync(string text, bool isQuery = false)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<float>();

        try
        {
            
            var prefix = isQuery ? "search_query: " : "search_document: ";
            
            const int maxTextLength = 800;
            if (text.Length > maxTextLength)
            {
                text = text.Substring(0, maxTextLength);
            }
            
            var prompt = prefix + text;
            
            var request = new { model = _embeddingModel, input = prompt };
            var response = await _httpClient.PostAsJsonAsync("/api/embed", request);
            var responseString = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"原始响应: {responseString}");
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Log($"[LanceDB] Ollama 错误详情: {errorContent}");
                response.EnsureSuccessStatusCode();
            }
            
            var result = await response.Content.ReadFromJsonAsync<EmbeddingResponse>();
            var vector = result?.Embeddings ?? new List<float[]>();
            
            //if (vector.Count > 0 && vector[0].Length != VectorDimension)
            //{
            //    vector = AdjustVectorDimension(vector[0],VectorDimension);
            //}
            
            return vector[0];
        }
        catch (Exception ex)
        {
            Log($"[LanceDB] 向量生成失败: {ex.Message}");
            return Array.Empty<float>();
        }
    }

    // 生成查询向量（带 search_query 前缀）
    public async Task<float[]> GenerateQueryEmbeddingAsync(string query)
        => await GenerateEmbeddingAsync(query, isQuery: true);

    // 生成文档向量（带 search_document 前缀）
    public async Task<float[]> GenerateDocumentEmbeddingAsync(string document)
        => await GenerateEmbeddingAsync(document, isQuery: false);

    // 批量生成嵌入向量（使用 Ollama 批量接口，支持分批处理，每批最多1000个）
    public async Task<List<(int Index, float[] Vector)>> BatchGenerateEmbeddingsAsync(
        List<(int Index, string Text)> textsWithIndex,
        bool isQuery = false,
        IProgress<(int Current, int Total)>? progress = null)
    {
        var results = new List<(int Index, float[] Vector)>();
        var total = textsWithIndex.Count;
        var failedCount = 0;
        
        if (total == 0) return results;

        // 设置每批最大数量为 1000
        const int batchSize = 1000;
        var batches = textsWithIndex.Chunk(batchSize).ToList();
        
        Log($"[LanceDB] 使用批量接口生成 {total} 个向量，分为 {batches.Count} 批，每批最多 {batchSize} 个...");

        for (int batchIndex = 0; batchIndex < batches.Count; batchIndex++)
        {
            var batch = batches[batchIndex].ToList();
            var batchTotal = batch.Count;
            
            try
            {
                var prefix = isQuery ? "search_query: " : "search_document: ";
                
                var prompts = batch.Select(item => 
                {
                    var text = item.Text;
                    const int maxTextLength = 800;
                    if (text.Length > maxTextLength)
                    {
                        text = text.Substring(0, maxTextLength);
                    }
                    return prefix + text;
                }).ToList();

                var request = new { model = _embeddingModel, input = prompts };
                var response = await _httpClient.PostAsJsonAsync("/api/embed", request);
                
                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Log($"[LanceDB] 批量嵌入错误详情: {errorContent}");
                    throw new HttpRequestException($"Ollama批量接口失败: {response.StatusCode}");
                }
                
                var result = await response.Content.ReadFromJsonAsync<BatchEmbeddingResponse>();
                
                if (result?.Embeddings != null)
                {
                    for (int i = 0; i < Math.Min(result.Embeddings.Count, batch.Count); i++)
                    {
                        var originalIndex = batch[i].Index;
                        var vector = result.Embeddings[i] ?? Array.Empty<float>();

                        if (vector.Length != VectorDimension)
                        {
                            vector = AdjustVectorDimension(vector, VectorDimension);
                        }

                        results.Add((originalIndex, vector));
                    }
                }
                
                progress?.Report((results.Count, total));
                Log($"[LanceDB] 批次 {batchIndex + 1}/{batches.Count} 完成，生成 {results.Count}/{total} 个向量");
            }
            catch (Exception ex)
            {
                Log($"[LanceDB] 批次 {batchIndex + 1}/{batches.Count} 批量向量生成失败: {ex.Message}");
                Log($"[LanceDB] 降级到串行处理本批次...");
                
                foreach (var item in batch)
                {
                    float[]? vector = null;
                    var retryCount = 0;
                    const int maxRetries = 3;
                    
                    while (retryCount < maxRetries)
                    {
                        try
                        {
                            vector = await GenerateEmbeddingAsync(item.Text, isQuery);
                            if (vector.Length > 0)
                                break;
                        }
                        catch (HttpRequestException ex2) when (ex2.StatusCode == System.Net.HttpStatusCode.InternalServerError)
                        {
                            retryCount++;
                            if (retryCount < maxRetries)
                            {
                                var delay = TimeSpan.FromSeconds(Math.Pow(2, retryCount));
                                Log($"[LanceDB] 向量生成失败 (500)，{retryCount}/{maxRetries} 次重试，等待 {delay.TotalSeconds}s...");
                                await Task.Delay(delay);
                            }
                        }
                        catch (Exception ex2)
                        {
                            Log($"[LanceDB] 向量生成异常: {ex2.Message}");
                            break;
                        }
                    }
                    
                    if (vector == null || vector.Length == 0)
                    {
                        Log($"[LanceDB] 向量生成最终失败，使用零向量替代 (索引: {item.Index})");
                        vector = new float[VectorDimension];
                        failedCount++;
                    }
                    
                    results.Add((item.Index, vector));
                    progress?.Report((results.Count, total));
                }
            }
        }
        
        if (failedCount > 0)
        {
            Log($"[LanceDB] 警告: {failedCount} 个向量生成失败，已使用零向量替代");
        }
        
        Log($"[LanceDB] 批量向量生成完成，共 {results.Count} 个");
        return results.OrderBy(r => r.Index).ToList();
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

    #region 批量处理

    /// <summary>
    /// 批量生成文档向量（带 search_document 前缀）- 使用批量接口
    /// </summary>
    public async Task<List<(int Index, float[] Vector)>> BatchGenerateDocumentEmbeddingsAsync(
        List<(int Index, string Text)> textsWithIndex,
        IProgress<(int Current, int Total)>? progress = null)
    {
        // 使用新的批量接口
        return await BatchGenerateEmbeddingsAsync(textsWithIndex, isQuery: false, progress);
    }

    /// <summary>
    /// 批量添加电影向量（单次批量插入）
    /// </summary>
    public async Task<int> BatchAddMoviesAsync(List<(int MovieId, float[] Vector, string Title, string? Overview)> movies)
    {
        await EnsureDatabaseAsync();
        if (_moviesTable == null) throw new InvalidOperationException("表未初始化");

        if (movies.Count == 0) return 0;

        var records = new List<MovieRecord>();
        var now = DateTime.UtcNow;

        foreach (var (movieId, vector, title, overview) in movies)
        {
            // 确保向量维度正确
            var vec = vector.Length != VectorDimension 
                ? vector.Take(VectorDimension).ToArray() 
                : vector;

            records.Add(new MovieRecord
            {
                MovieId = movieId,
                Title = title,
                Overview = overview ?? "",
                Vector = vec,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        // 批量转换为 RecordBatch
        var batches = ConvertToRecordBatches(records);
        var addedCount = 0;

        foreach (var batch in batches)
        {
            await _moviesTable.Add(batch);
            addedCount += batch.Length;
        }

        Log($"[LanceDB] 批量添加成功: {addedCount} 条记录");
        return addedCount;
    }

    /// <summary>
    /// 批量生成并添加向量（一体化操作，支持分批处理，每批最多1000个）
    /// </summary>
    public async Task<int> BatchGenerateAndAddAsync(
        List<(int MovieId, string Text, string Title, string? Overview)> movies,
        IProgress<(int Current, int Total, string Stage)>? progress = null)
    {
        if (movies.Count == 0) return 0;

        var total = movies.Count;
        var addedCount = 0;
        
        // 设置每批最大数量为 1000
        const int batchSize = 1000;
        var batches = movies.Chunk(batchSize).ToList();
        
        Log($"[LanceDB] 批量生成并添加 {total} 个向量，分为 {batches.Count} 批，每批最多 {batchSize} 个...");

        for (int batchIndex = 0; batchIndex < batches.Count; batchIndex++)
        {
            var batch = batches[batchIndex].ToList();
            var batchStart = batchIndex * batchSize;
            
            // 阶段1：批量生成向量
            progress?.Report((batchStart, total, "生成向量"));
            var textsWithIndex = batch.Select((m, i) => (i, m.Text)).ToList();
            
            var embeddings = await BatchGenerateDocumentEmbeddingsAsync(textsWithIndex, null);

            // 阶段2：批量添加到数据库
            progress?.Report((batchStart + batch.Count, total, "写入数据库"));
            
            // 准备批量数据，确保向量不为空
            var batchData = new List<(int MovieId, float[] Vector, string Title, string? Overview)>();
            
            // 创建字典以便按索引查找向量
            var embeddingDict = embeddings.ToDictionary(e => e.Index, e => e.Vector);
            
            for (int i = 0; i < batch.Count; i++)
            {
                var item = batch[i];
                float[]? vector = null;
                
                // 尝试从字典中获取对应索引的向量
                if (embeddingDict.TryGetValue(i, out var foundVector))
                {
                    vector = foundVector;
                }
                
                // 如果向量为空或无效，使用零向量
                if (vector == null || vector.Length == 0)
                {
                    vector = new float[VectorDimension];
                }
                
                batchData.Add(CreateBatchItem(item.MovieId, vector, item.Title, item.Overview));
            }
            
            addedCount += await BatchAddMoviesAsync(batchData);
            
            // 报告批次进度
            progress?.Report((batchStart + batch.Count, total, "处理中"));
            Log($"[LanceDB] 批次 {batchIndex + 1}/{batches.Count} 完成，已添加 {addedCount}/{total} 个向量");
        }

        progress?.Report((total, total, "完成"));
        return addedCount;
    }

    /// <summary>
    /// 创建批量添加的元组项（辅助方法解决类型推断问题）
    /// </summary>
    private static (int MovieId, float[] Vector, string Title, string? Overview) CreateBatchItem(
        int movieId, float[] vector, string title, string? overview)
    {
        return (movieId, vector, title, overview);
    }

    #endregion

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
              batches = batches
        .GroupBy(r => r["movie_id"].ToString())   // 按 MovieId 分组，转为字符串作为键
        .Select(g => g.First())                  // 每组取第一条（相似度最高）
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
        // 使用 search_query 前缀生成查询向量
        var vec = await GenerateQueryEmbeddingAsync(queryText);
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

/// <summary>
/// 将多个 MovieRecord 转换为批量 RecordBatch（每批1000条）
/// </summary>
private static List<RecordBatch> ConvertToRecordBatches(List<MovieRecord> movies)
{
    const int BatchSize = 1000;
    var batches = new List<RecordBatch>();

    for (int i = 0; i < movies.Count; i += BatchSize)
    {
        var batchMovies = movies.Skip(i).Take(BatchSize).ToList();
        batches.Add(ConvertToRecordBatchBatch(batchMovies));
    }

    return batches;
}

/// <summary>
/// 将多个 MovieRecord 转换为单个 RecordBatch
/// </summary>
private static RecordBatch ConvertToRecordBatchBatch(List<MovieRecord> movies)
{
    var movieIdBuilder = new Int32Array.Builder();
    var titleBuilder = new StringArray.Builder();
    var overviewBuilder = new StringArray.Builder();
    var vectorBuilder = new FixedSizeListArray.Builder(new FloatType(), VectorDimension);
    var createdAtBuilder = new TimestampArray.Builder(TimeUnit.Microsecond, "UTC");
    var updatedAtBuilder = new TimestampArray.Builder(TimeUnit.Microsecond, "UTC");

    foreach (var movie in movies)
    {
        movieIdBuilder.Append(movie.MovieId);
        titleBuilder.Append(movie.Title);
        overviewBuilder.Append(movie.Overview ?? "");

        var valueBuilder = vectorBuilder.ValueBuilder as FloatArray.Builder;
        vectorBuilder.Append();
        foreach (var value in movie.Vector)
        {
            valueBuilder.Append(value);
        }

        createdAtBuilder.Append(movie.CreatedAt);
        updatedAtBuilder.Append(movie.UpdatedAt);
    }

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
            vectorBuilder.Build(),
            createdAtBuilder.Build(),
            updatedAtBuilder.Build()
        },
        movies.Count
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
        [JsonPropertyName("embeddings")]
        public List<float[]> Embeddings { get; set; }
    }

    private class BatchEmbeddingResponse
    {
        [JsonPropertyName("embeddings")]
        public List<float[]> Embeddings { get; set; }
    }

    private class BatchEmbeddingItem
    {
        public int PromptIndex { get; set; }
        [JsonPropertyName("embeddings")]
        public List<float[]> Embeddings { get; set; }
    }

    private static void Log(string message)
        => Logger.Debug($"[{DateTime.Now:HH:mm:ss.fff}] {message}");

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

    // ==================== 简单日志类 ====================
    private class SimpleLogger : ILoggerService
    {
        public void Debug(string message, params object[] args)
        {
            Console.WriteLine($"[DEBUG] {string.Format(message, args)}");
        }

        public void Information(string message, params object[] args)
        {
            Console.WriteLine($"[INFO] {string.Format(message, args)}");
        }

        public void Warning(string message, params object[] args)
        {
            Console.WriteLine($"[WARN] {string.Format(message, args)}");
        }

        public void Error(string message, params object[] args)
        {
            Console.WriteLine($"[ERROR] {string.Format(message, args)}");
        }

        public void Error(Exception exception, string message, params object[] args)
        {
            Console.WriteLine($"[ERROR] {string.Format(message, args)} - {exception}");
        }

        public void Critical(string message, params object[] args)
        {
            Console.WriteLine($"[CRITICAL] {string.Format(message, args)}");
        }

        public void Critical(Exception exception, string message, params object[] args)
        {
            Console.WriteLine($"[CRITICAL] {string.Format(message, args)} - {exception}");
        }
    }
}
