using MovieAgent.Core.Entities;
using MovieAgent.Core.Models;

namespace MovieAgent.Core.Interfaces;

/// <summary>
/// 电影仓储接口 - 提供电影数据的CRUD操作
/// </summary>
public interface IMovieRepository
{
    /// <summary>
    /// 获取所有电影（支持过滤）
    /// </summary>
    /// <param name="filter">过滤条件（可选）</param>
    /// <returns>电影列表</returns>
    Task<List<Movie>> GetAllAsync(MovieFilter? filter = null);

    /// <summary>
    /// 根据ID获取电影
    /// </summary>
    /// <param name="id">电影ID</param>
    /// <returns>电影实体或null</returns>
    Task<Movie?> GetByIdAsync(int id);

    /// <summary>
    /// 根据文件路径获取电影
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <returns>电影实体或null</returns>
    Task<Movie?> GetByFilePathAsync(string filePath);

    /// <summary>
    /// 根据TMDB ID获取电影
    /// </summary>
    /// <param name="tmdbId">TMDB电影ID</param>
    /// <returns>电影实体或null</returns>
    Task<Movie?> GetByTmdbIdAsync(string tmdbId);

    /// <summary>
    /// 搜索电影（按标题、简介、类型搜索）
    /// </summary>
    /// <param name="keyword">搜索关键词</param>
    /// <returns>匹配的电影列表</returns>
    Task<List<Movie>> SearchAsync(string keyword);

    /// <summary>
    /// 添加电影
    /// </summary>
    /// <param name="movie">电影实体</param>
    /// <returns>添加后的电影实体（含ID）</returns>
    Task<Movie> AddAsync(Movie movie);

    /// <summary>
    /// 更新电影
    /// </summary>
    /// <param name="movie">电影实体</param>
    /// <returns>更新后的电影实体</returns>
    Task<Movie> UpdateAsync(Movie movie);

    /// <summary>
    /// 删除电影
    /// </summary>
    /// <param name="id">电影ID</param>
    Task DeleteAsync(int id);

    /// <summary>
    /// 检查电影是否已存在（按文件路径）
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <returns>是否存在</returns>
    Task<bool> ExistsByFilePathAsync(string filePath);

    /// <summary>
    /// 获取电影总数（支持过滤）
    /// </summary>
    /// <param name="filter">过滤条件（可选）</param>
    /// <returns>电影数量</returns>
    Task<int> GetCountAsync(MovieFilter? filter = null);

    /// <summary>
    /// 获取所有电影类型列表
    /// </summary>
    /// <returns>类型名称列表</returns>
    Task<List<string>> GetAllGenresAsync();

    /// <summary>
    /// 获取所有分辨率列表
    /// </summary>
    /// <returns>分辨率列表</returns>
    Task<List<string>> GetAllResolutionsAsync();

    /// <summary>
    /// 获取所有国家/地区列表
    /// </summary>
    /// <returns>国家/地区列表</returns>
    Task<List<string>> GetAllCountriesAsync();

    /// <summary>
    /// 获取所有导演列表
    /// </summary>
    /// <returns>导演列表</returns>
    Task<List<string>> GetAllDirectorsAsync();

    /// <summary>
    /// 获取所有演员列表
    /// </summary>
    /// <returns>演员列表</returns>
    Task<List<string>> GetAllCastsAsync();

    /// <summary>
    /// 获取所有语言列表
    /// </summary>
    /// <returns>语言列表</returns>
    Task<List<string>> GetAllLanguagesAsync();

    /// <summary>
    /// 获取所有视频编码格式列表
    /// </summary>
    /// <returns>视频编码格式列表</returns>
    Task<List<string>> GetAllVideoCodecsAsync();

    /// <summary>
    /// 获取所有HDR类型列表
    /// </summary>
    /// <returns>HDR类型列表</returns>
    Task<List<string>> GetAllHdrTypesAsync();

    /// <summary>
    /// 获取去重后的电影列表（同一电影只返回一条）
    /// </summary>
    /// <param name="filter">过滤条件（可选）</param>
    /// <returns>去重后的电影列表</returns>
    Task<List<Movie>> GetUniqueMoviesAsync(MovieFilter? filter = null);

    /// <summary>
    /// 获取去重后的电影总数
    /// </summary>
    /// <param name="filter">过滤条件（可选）</param>
    /// <returns>电影数量</returns>
    Task<int> GetUniqueMovieCountAsync(MovieFilter? filter = null);

    /// <summary>
    /// 获取未观看的电影列表
    /// </summary>
    /// <returns>未观看的电影列表</returns>
    Task<List<Movie>> GetUnwatchedAsync();

    /// <summary>
    /// 获取最近添加的电影
    /// </summary>
    /// <param name="count">返回数量，默认20部</param>
    /// <returns>最近添加的电影列表</returns>
    Task<List<Movie>> GetRecentlyAddedAsync(int count = 20);

    /// <summary>
    /// 获取电影的所有视频文件路径
    /// </summary>
    /// <param name="movieId">电影ID</param>
    /// <returns>视频文件路径列表</returns>
    Task<List<string>> GetMovieVideoPathsAsync(int movieId);

    /// <summary>
    /// 从硬盘目录获取海报图片（当数据库海报字段为空时）
    /// </summary>
    /// <param name="filePath">电影文件路径</param>
    /// <returns>海报图片字节数组，如果未找到则返回null</returns>
    byte[]? GetPosterFromDirectory(string filePath);

    /// <summary>
    /// 从硬盘目录获取媒体信息文件
    /// </summary>
    /// <param name="filePath">电影文件路径</param>
    /// <returns>媒体信息字典，如果未找到则返回null</returns>
    Dictionary<string, string>? GetMediaInfoFromDirectory(string filePath);

    /// <summary>
    /// 检查并更新电影的海报（从目录获取）
    /// </summary>
    /// <param name="movie">电影实体</param>
    /// <returns>是否成功更新</returns>
    Task<bool> UpdatePosterFromDirectoryAsync(Movie movie);

    /// <summary>
    /// 检查并更新电影的媒体信息（从目录获取）
    /// </summary>
    /// <param name="movie">电影实体</param>
    /// <returns>是否成功更新</returns>
    Task<bool> UpdateMediaInfoFromDirectoryAsync(Movie movie);
}
