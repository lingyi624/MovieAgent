using MovieAgent.Core.Entities;

namespace MovieAgent.Core.Interfaces;

/// <summary>
/// 电影导出服务接口 - 导出/导入电影库数据
/// 支持 JSON 和 CSV 两种格式
/// </summary>
public interface IMovieExportService
{
    /// <summary>
    /// 导出为 JSON 格式
    /// </summary>
    /// <param name="movies">电影列表</param>
    /// <returns>JSON 字符串</returns>
    Task<string> ExportToJsonAsync(List<Movie> movies);

    /// <summary>
    /// 从 JSON 导入
    /// </summary>
    /// <param name="jsonContent">JSON 字符串</param>
    /// <returns>电影列表</returns>
    Task<List<Movie>> ImportFromJsonAsync(string jsonContent);

    /// <summary>
    /// 导出为 CSV 格式
    /// </summary>
    /// <param name="movies">电影列表</param>
    /// <returns>CSV 字符串</returns>
    Task<string> ExportToCsvAsync(List<Movie> movies);

    /// <summary>
    /// 从 CSV 导入
    /// </summary>
    /// <param name="csvContent">CSV 字符串</param>
    /// <returns>电影列表</returns>
    Task<List<Movie>> ImportFromCsvAsync(string csvContent);

    /// <summary>
    /// 导出到文件
    /// </summary>
    /// <param name="movies">电影列表</param>
    /// <param name="filePath">目标文件路径</param>
    /// <param name="format">导出格式</param>
    /// <returns>文件路径</returns>
    Task<string> ExportToFileAsync(List<Movie> movies, string filePath, ExportFormat format);

    /// <summary>
    /// 从文件导入
    /// </summary>
    /// <param name="filePath">源文件路径</param>
    /// <returns>电影列表</returns>
    Task<List<Movie>> ImportFromFileAsync(string filePath);
}

/// <summary>
/// 导出格式枚举
/// </summary>
public enum ExportFormat
{
    /// <summary>JSON 格式</summary>
    Json,
    /// <summary>CSV 格式</summary>
    Csv
}
