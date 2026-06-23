using System.Text.Json;
using MovieAgent.Core.Entities;
using MovieAgent.Core.Interfaces;

namespace MovieAgent.Infrastructure.Services;

public interface ITagService
{
    Task AddTagAsync(int movieId, string tag);
    Task RemoveTagAsync(int movieId, string tag);
    Task<List<string>> GetTagsAsync(int movieId);
    Task<List<(string Tag, int Count)>> GetTagStatisticsAsync();
    Task<int> GetTotalTaggedMoviesCountAsync();
    Task AddEmotionTagsAsync(int movieId);
    Task AddSceneTagsAsync(int movieId);
    Task AddStyleTagsAsync(int movieId);
    Task AddAllAiTagsAsync(int movieId);
    Task BatchAddAiTagsAsync(List<int> movieIds);
    Task<List<string>> GetRecommendedTagsAsync(string query);
}

public class TagService : ITagService
{
    private readonly IMovieRepository _movieRepo;
    private readonly IAgentService _agentService;
    private readonly IMovieUpdateService? _movieUpdateService;

    private static readonly List<string> EmotionTags = new List<string>
    {
        "感人", "催泪", "温馨", "治愈", "励志", "热血", "震撼", "深刻",
        "压抑", "惊悚", "紧张", "悬疑", "恐怖", "搞笑", "轻松", "浪漫",
        "悲伤", "愤怒", "希望", "绝望", "温暖", "伤感", "悲壮", "温情"
    };

    private static readonly List<string> SceneTags = new List<string>
    {
        "太空", "海洋", "森林", "城市", "乡村", "沙漠", "雪山", "草原",
        "战争", "监狱", "校园", "家庭", "职场", "历史", "未来", "古代",
        "科幻", "奇幻", "魔法", "冒险", "动作", "犯罪", "爱情", "友情"
    };

    private static readonly List<string> StyleTags = new List<string>
    {
        "文艺", "商业", "独立", "小众", "经典", "现代", "复古", "先锋",
        "写实", "夸张", "细腻", "粗犷", "唯美", "暗黑", "清新", "厚重"
    };

    public TagService(IMovieRepository movieRepo, IAgentService agentService, IMovieUpdateService? movieUpdateService = null)
    {
        _movieRepo = movieRepo;
        _agentService = agentService;
        _movieUpdateService = movieUpdateService;
    }

    public async Task AddTagAsync(int movieId, string tag)
    {
        var movie = await _movieRepo.GetByIdAsync(movieId);
        if (movie == null) return;

        var tags = GetTagsFromMovie(movie);
        if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
        {
            tags.Add(tag);
            movie.Tags = JsonSerializer.Serialize(tags);
            
            // 使用统一更新服务，同步向量数据库
            if (_movieUpdateService != null)
            {
                await _movieUpdateService.UpdateMovieWithVectorAsync(movie);
            }
            else
            {
                await _movieRepo.UpdateAsync(movie);
            }
        }
    }

    public async Task RemoveTagAsync(int movieId, string tag)
    {
        var movie = await _movieRepo.GetByIdAsync(movieId);
        if (movie == null) return;

        var tags = GetTagsFromMovie(movie);
        tags.RemoveAll(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase));
        movie.Tags = tags.Any() ? JsonSerializer.Serialize(tags) : null;
        
        // 使用统一更新服务，同步向量数据库
        if (_movieUpdateService != null)
        {
            await _movieUpdateService.UpdateMovieWithVectorAsync(movie);
        }
        else
        {
            await _movieRepo.UpdateAsync(movie);
        }
    }

    public async Task<List<string>> GetTagsAsync(int movieId)
    {
        var movie = await _movieRepo.GetByIdAsync(movieId);
        return movie != null ? GetTagsFromMovie(movie) : new List<string>();
    }

    public async Task<List<(string Tag, int Count)>> GetTagStatisticsAsync()
    {
        var movies = await _movieRepo.GetAllAsync();
        var tagCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var movie in movies)
        {
            var tags = GetTagsFromMovie(movie);
            foreach (var tag in tags)
            {
                tagCounts[tag] = tagCounts.GetValueOrDefault(tag, 0) + 1;
            }
        }

        return tagCounts.Select(kv => (kv.Key, kv.Value))
                        .OrderByDescending(t => t.Item2)
                        .ToList();
    }

    public async Task<int> GetTotalTaggedMoviesCountAsync()
    {
        var movies = await _movieRepo.GetAllAsync();
        return movies.Count(m => !string.IsNullOrEmpty(m.Tags));
    }

    public async Task AddEmotionTagsAsync(int movieId)
    {
        var movie = await _movieRepo.GetByIdAsync(movieId);
        if (movie == null || string.IsNullOrEmpty(movie.Overview)) return;

        try
        {
            var prompt = $"""
                请根据以下电影简介分析情感基调，从给定的情感标签列表中选择3-5个最贴切的标签。
                情感标签列表：{string.Join('、', EmotionTags)}
                
                只需输出选中的标签，用中文逗号分隔，不要解释。
                
                电影标题：{movie.Title}
                电影简介：{movie.Overview}
                """;
            
            var response = await _agentService.ChatAsync(prompt);
            await ParseAndAddTags(movieId, response);
        }
        catch
        {
            await AddRandomTags(movieId, EmotionTags, 3);
        }
    }

    public async Task AddSceneTagsAsync(int movieId)
    {
        var movie = await _movieRepo.GetByIdAsync(movieId);
        if (movie == null || string.IsNullOrEmpty(movie.Overview)) return;

        try
        {
            var prompt = $"""
                请根据以下电影简介分析场景和主题，从给定的场景标签列表中选择3-5个最贴切的标签。
                场景标签列表：{string.Join('、', SceneTags)}
                
                只需输出选中的标签，用中文逗号分隔，不要解释。
                
                电影标题：{movie.Title}
                电影简介：{movie.Overview}
                """;
            
            var response = await _agentService.ChatAsync(prompt);
            await ParseAndAddTags(movieId, response);
        }
        catch
        {
            await AddRandomTags(movieId, SceneTags, 3);
        }
    }

    public async Task AddStyleTagsAsync(int movieId)
    {
        var movie = await _movieRepo.GetByIdAsync(movieId);
        if (movie == null || string.IsNullOrEmpty(movie.Overview)) return;

        try
        {
            var prompt = $"""
                请根据以下电影简介分析影片风格，从给定的风格标签列表中选择2-3个最贴切的标签。
                风格标签列表：{string.Join('、', StyleTags)}
                
                只需输出选中的标签，用中文逗号分隔，不要解释。
                
                电影标题：{movie.Title}
                电影简介：{movie.Overview}
                """;
            
            var response = await _agentService.ChatAsync(prompt);
            await ParseAndAddTags(movieId, response);
        }
        catch
        {
            await AddRandomTags(movieId, StyleTags, 2);
        }
    }

    public async Task AddAllAiTagsAsync(int movieId)
    {
        await AddEmotionTagsAsync(movieId);
        await AddSceneTagsAsync(movieId);
        await AddStyleTagsAsync(movieId);
    }

    public async Task BatchAddAiTagsAsync(List<int> movieIds)
    {
        foreach (var movieId in movieIds)
        {
            try
            {
                await AddAllAiTagsAsync(movieId);
            }
            catch
            {
                // 单个电影失败不影响其他
            }
        }
    }

    public async Task<List<string>> GetRecommendedTagsAsync(string query)
    {
        var allTags = EmotionTags.Concat(SceneTags).Concat(StyleTags).Distinct().ToList();
        
        if (string.IsNullOrWhiteSpace(query))
            return allTags.Take(10).ToList();

        var filtered = allTags.Where(t => 
            t.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            query.Contains(t, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return filtered.Any() ? filtered : allTags.Take(5).ToList();
    }

    private async Task ParseAndAddTags(int movieId, string response)
    {
        var tags = response.Split(new[] { '，', ',', '、', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                          .Select(t => t.Trim())
                          .Where(t => !string.IsNullOrWhiteSpace(t) && t.Length <= 10)
                          .Distinct(StringComparer.OrdinalIgnoreCase)
                          .ToList();

        foreach (var tag in tags)
        {
            await AddTagAsync(movieId, tag);
        }
    }

    private async Task AddRandomTags(int movieId, List<string> tagPool, int count)
    {
        var random = new Random();
        var shuffled = tagPool.OrderBy(_ => random.Next()).Take(count).ToList();
        foreach (var tag in shuffled)
        {
            await AddTagAsync(movieId, tag);
        }
    }

    private List<string> GetTagsFromMovie(Movie movie)
    {
        if (string.IsNullOrEmpty(movie.Tags))
            return new List<string>();

        try
        {
            return JsonSerializer.Deserialize<List<string>>(movie.Tags) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }
}