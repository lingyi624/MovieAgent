using MovieAgent.Core.Interfaces;
using System.Collections.Generic;
using System.Globalization;

namespace MovieAgent.Infrastructure.Services;

public class LocalizationService : ILocalizationService
{
    private const string LanguageKey = "MovieAgent_Language";
    private Language _currentLanguage = Language.Chinese;
    private readonly Dictionary<string, Dictionary<Language, string>> _translations = new();

    public Language CurrentLanguage => _currentLanguage;

    public event Action<Language>? LanguageChanged;

    public LocalizationService()
    {
        LoadTranslations();
    }

    private void LoadTranslations()
    {
        // 导航菜单
        AddTranslation("Home", "首页(海报墙)", "Home (Poster Wall)");
        AddTranslation("Dashboard", "观影统计", "Dashboard");
        AddTranslation("Movies", "电影库", "Movies");
        AddTranslation("Search", "语义搜索", "Semantic Search");
        AddTranslation("Chat", "AI 对话", "AI Chat");
        AddTranslation("Report", "观影报告", "Report");
        AddTranslation("Settings", "设置", "Settings");
        AddTranslation("Language", "语言", "Language");
        AddTranslation("CurrentLanguage", "当前语言", "Current Language");
        AddTranslation("LanguageRestartHint", "语言设置将在下次启动时生效", "Language settings will take effect on next restart");

        // 按钮和操作
        AddTranslation("Play", "播放", "Play");
        AddTranslation("Pause", "暂停", "Pause");
        AddTranslation("Stop", "停止", "Stop");
        AddTranslation("Refresh", "刷新", "Refresh");
        AddTranslation("SearchPlaceholder", "搜索电影...", "Search movies...");
        AddTranslation("GenerateAI", "AI生成", "AI Generate");
        AddTranslation("AddTag", "添加", "Add");
        AddTranslation("Back", "返回", "Back");
        AddTranslation("Submit", "提交", "Submit");
        AddTranslation("Cancel", "取消", "Cancel");

        // 标签统计
        AddTranslation("TagStatistics", "标签统计", "Tag Statistics");
        AddTranslation("TotalTags", "标签总数", "Total Tags");
        AddTranslation("TaggedMovies", "已标记电影", "Tagged Movies");
        AddTranslation("AvgTagsPerMovie", "平均每部标签数", "Avg Tags Per Movie");
        AddTranslation("MaxTags", "最高标签数", "Max Tags");
        AddTranslation("PopularTags", "热门标签 TOP 5", "Popular Tags TOP 5");
        AddTranslation("TagCloud", "标签云", "Tag Cloud");

        // 相似电影推荐
        AddTranslation("SimilarMovies", "相似电影推荐", "Similar Movies");
        AddTranslation("WatchMore", "查看详情", "View Details");

        // 状态消息
        AddTranslation("Loading", "加载中...", "Loading...");
        AddTranslation("NoData", "暂无数据", "No Data");
        AddTranslation("Searching", "搜索中...", "Searching...");
        AddTranslation("NoResults", "未找到匹配的电影", "No movies found");
        AddTranslation("Success", "操作成功", "Success");
        AddTranslation("Error", "操作失败", "Error");

        // 主题切换
        AddTranslation("DarkMode", "暗色模式", "Dark Mode");
        AddTranslation("LightMode", "亮色模式", "Light Mode");

        // 导出导入
        AddTranslation("Export", "导出", "Export");
        AddTranslation("Import", "导入", "Import");
        AddTranslation("ExportSuccess", "导出成功", "Export successful");
        AddTranslation("ImportSuccess", "导入成功", "Import successful");

        // 快捷键提示
        AddTranslation("Shortcuts", "快捷键", "Shortcuts");
        AddTranslation("CtrlF", "Ctrl+F: 搜索", "Ctrl+F: Search");
        AddTranslation("CtrlR", "Ctrl+R: 刷新", "Ctrl+R: Refresh");
        AddTranslation("CtrlT", "Ctrl+T: 切换主题", "Ctrl+T: Toggle Theme");
        AddTranslation("Space", "空格: 播放/暂停", "Space: Play/Pause");
        AddTranslation("Esc", "Esc: 退出", "Esc: Exit");
    }

    private void AddTranslation(string key, string chinese, string english)
    {
        _translations[key] = new Dictionary<Language, string>
        {
            { Language.Chinese, chinese },
            { Language.English, english }
        };
    }

    public async Task LoadLanguageAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                var saved = Environment.GetEnvironmentVariable(LanguageKey);
                if (Enum.TryParse<Language>(saved, out var language))
                {
                    _currentLanguage = language;
                }
                else
                {
                    // 默认根据系统语言设置
                    var culture = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                    _currentLanguage = culture.Equals("zh", StringComparison.OrdinalIgnoreCase) ? Language.Chinese : Language.English;
                }
            }
            catch
            {
                _currentLanguage = Language.Chinese;
            }
        });
    }

    public async Task SetLanguageAsync(Language language)
    {
        _currentLanguage = language;
        await Task.Run(() =>
        {
            try
            {
                Environment.SetEnvironmentVariable(LanguageKey, language.ToString(), EnvironmentVariableTarget.User);
            }
            catch { }
        });
        LanguageChanged?.Invoke(language);
    }

    public string Translate(string key)
    {
        if (_translations.TryGetValue(key, out var dict) && dict.TryGetValue(_currentLanguage, out var translation))
        {
            return translation;
        }
        return key;
    }

    public string Translate(string key, params object[] args)
    {
        var baseTranslation = Translate(key);
        if (args.Length > 0)
        {
            try
            {
                return string.Format(baseTranslation, args);
            }
            catch
            {
                return baseTranslation;
            }
        }
        return baseTranslation;
    }
}