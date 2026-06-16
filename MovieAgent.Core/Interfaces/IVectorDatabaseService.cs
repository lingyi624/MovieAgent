namespace MovieAgent.Core.Interfaces;

/// <summary>
/// 向量数据库服务接口 - 管理电影向量嵌入和相似度搜索
/// 使用 LanceDB 存储和检索电影向量
/// </summary>
public interface IVectorDatabaseService
{
    /// <summary>
    /// 确保数据库连接已初始化（懒加载）
    /// </summary>
    Task EnsureDatabaseAsync();

    /// <summary>
    /// 添加或更新电影向量记录
    /// </summary>
    /// <param name="movieId">电影ID</param>
    /// <param name="vector">电影向量嵌入（768维）</param>
    /// <param name="title">电影标题</param>
    /// <param name="overview">电影简介（可选）</param>
    Task AddMovieAsync(int movieId, float[] vector, string title, string? overview = null);

    /// <summary>
    /// 更新电影向量记录
    /// </summary>
    /// <param name="movieId">电影ID</param>
    /// <param name="vector">新的向量嵌入</param>
    /// <param name="title">电影标题</param>
    /// <param name="overview">电影简介（可选）</param>
    Task UpdateMovieAsync(int movieId, float[] vector, string title, string? overview = null);

    /// <summary>
    /// 删除电影向量记录
    /// </summary>
    /// <param name="movieId">电影ID</param>
    Task RemoveMovieAsync(int movieId);

    /// <summary>
    /// 文本语义搜索 - 将文本转为向量后搜索相似电影
    /// </summary>
    /// <param name="queryText">查询文本</param>
    /// <param name="topK">返回结果数量，默认10</param>
    /// <returns>相似电影列表</returns>
    Task<List<VectorSearchResult>> SearchAsync(string queryText, int topK = 10);

    /// <summary>
    /// 向量相似度搜索 - 直接使用向量搜索相似电影
    /// </summary>
    /// <param name="queryVector">查询向量</param>
    /// <param name="topK">返回结果数量，默认10</param>
    /// <returns>相似电影列表</returns>
    Task<List<VectorSearchResult>> SearchByVectorAsync(float[] queryVector, int topK = 10);

    /// <summary>
    /// 使用 Ollama 生成文本嵌入向量
    /// </summary>
    /// <param name="text">输入文本</param>
    /// <returns>768维浮点向量</returns>
    Task<float[]> GenerateEmbeddingAsync(string text);

    /// <summary>
    /// 获取数据库中的记录总数
    /// </summary>
    /// <returns>向量记录数量</returns>
    Task<int> GetRecordCountAsync();

    /// <summary>
    /// 检查电影是否已存在于向量数据库
    /// </summary>
    /// <param name="movieId">电影ID</param>
    /// <returns>是否存在</returns>
    Task<bool> HasMovieAsync(int movieId);

    /// <summary>
    /// 创建向量索引（IVF）
    /// </summary>
    /// <param name="numPartitions">索引分区数，默认128</param>
    Task CreateIndexAsync(int numPartitions = 128);

    /// <summary>
    /// 检查是否已存在向量索引
    /// </summary>
    /// <returns>是否存在索引</returns>
    Task<bool> HasIndexAsync();
}

/// <summary>
/// 向量搜索结果 - 包含电影信息和相似度分数
/// </summary>
public class VectorSearchResult
{
    /// <summary>电影ID</summary>
    public int MovieId { get; set; }

    /// <summary>电影标题</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>电影简介</summary>
    public string? Overview { get; set; }

    /// <summary>相似度分数（0-1之间，越高越相似）</summary>
    public double Similarity { get; set; }

    /// <summary>电影向量嵌入</summary>
    public float[]? Vector { get; set; }
}
