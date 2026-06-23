using MovieAgent.Core.Entities;

namespace MovieAgent.Core.Interfaces;

/// <summary>
/// 电影扫描服务接口 - 扫描文件夹中的视频文件并导入电影库
/// </summary>
public interface IMovieScannerService
{
    /// <summary>
    /// 扫描进度变化事件
    /// </summary>
    event EventHandler<ScanProgressEventArgs>? ScanProgressChanged;

    /// <summary>
    /// 扫描完成事件
    /// </summary>
    event EventHandler<ScanCompletedEventArgs>? ScanCompleted;

    /// <summary>
    /// 扫描视频文件 - 递归扫描指定路径
    /// </summary>
    /// <param name="sharePaths">要扫描的路径列表（支持UNC路径）</param>
    /// <returns>找到的视频文件路径列表</returns>
    Task<List<string>> ScanVideoFilesAsync(List<string> sharePaths);

    /// <summary>
    /// 导入新电影 - 检查并导入新的视频文件到数据库
    /// </summary>
    /// <param name="filePaths">要导入的文件路径列表</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>成功导入的电影数量</returns>
    Task<int> ImportNewMoviesAsync(List<string> filePaths, CancellationToken ct = default);

    /// <summary>
    /// 批量更新向量数据库（推荐使用，性能更高）
    /// </summary>
    /// <param name="movies">电影列表</param>
    /// <param name="progress">进度回调</param>
    /// <returns>成功更新的数量</returns>
    Task<int> BatchUpdateVectorDatabaseAsync(List<Movie> movies, IProgress<(int Current, int Total)>? progress = null);

    /// <summary>
    /// 重新生成所有电影的向量
    /// </summary>
    /// <param name="progress">进度回调</param>
    /// <returns>成功更新的数量</returns>
    Task<int> RegenerateAllVectorsAsync(IProgress<(int Current, int Total)>? progress = null);
}

/// <summary>
/// 扫描进度事件参数
/// </summary>
public class ScanProgressEventArgs : EventArgs
{
    /// <summary>当前扫描路径</summary>
    public string? CurrentPath { get; set; }

    /// <summary>已找到的电影数量</summary>
    public int FoundCount { get; set; }

    /// <summary>已扫描的文件总数</summary>
    public int TotalScanned { get; set; }

    /// <summary>当前文件名</summary>
    public string? CurrentFileName { get; set; }

    /// <summary>当前文件索引</summary>
    public int CurrentIndex { get; set; }

    /// <summary>总文件数</summary>
    public int TotalFiles { get; set; }

    /// <summary>数据库已导入数量</summary>
    public int DbImported { get; set; }

    /// <summary>向量库已更新数量</summary>
    public int VectorUpdated { get; set; }

    /// <summary>嵌入文本已生成数量</summary>
    public int EmbeddingGenerated { get; set; }

    /// <summary>当前处理阶段：Scanning, Processing, Completed</summary>
    public string Stage { get; set; } = "Scanning";
}

/// <summary>
/// 扫描完成事件参数
/// </summary>
public class ScanCompletedEventArgs : EventArgs
    {
        /// <summary>扫描的总文件数</summary>
        public int TotalFiles { get; set; }

        /// <summary>新导入的电影数</summary>
        public int NewMovies { get; set; }

        /// <summary>更新的电影数</summary>
        public int UpdatedMovies { get; set; }

        /// <summary>跳过的文件数（解析失败等）</summary>
        public int Skipped { get; set; }
    }
