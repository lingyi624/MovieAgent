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
        AddTranslation("Menu", "菜单", "Menu");
        AddTranslation("Home", "首页(海报墙)", "Home (Poster Wall)");
        AddTranslation("Dashboard", "观影统计", "Dashboard");
        AddTranslation("Movies", "电影库", "Movies");
        AddTranslation("Search", "语义搜索", "Semantic Search");
        AddTranslation("Chat", "AI 对话", "AI Chat");
        AddTranslation("Report", "观影报告", "Report");
        AddTranslation("Settings", "设置", "Settings");
        AddTranslation("WatchPlans", "观影计划", "Watch Plans");
        AddTranslation("Tags", "标签管理", "Tags");
        AddTranslation("Logs", "系统日志", "Logs");

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

        // 语言设置
        AddTranslation("Language", "语言", "Language");
        AddTranslation("CurrentLanguage", "当前语言", "Current Language");
        AddTranslation("LanguageRestartHint", "语言设置将在下次启动时生效", "Language settings will take effect on next restart");

        // 首页相关
        AddTranslation("MoviePosterWall", "电影海报墙", "Movie Poster Wall");
        AddTranslation("ScanNAS", "扫描NAS", "Scan NAS");
        AddTranslation("ScanProgress", "扫描进度", "Scan Progress");
        AddTranslation("Status", "状态", "Status");
        AddTranslation("Found", "已找到", "Found");
        AddTranslation("Imported", "已导入", "Imported");
        AddTranslation("CurrentProcessing", "当前处理", "Current Processing");
        AddTranslation("TotalMovies", "共 {0} 部电影", "Total {0} movies");
        AddTranslation("Watched", "已看", "Watched");
        AddTranslation("Unknown", "未知", "Unknown");
        AddTranslation("OrConfigureNAS", "，请点击按钮开始扫描，或在设置中配置NAS共享路径。", ", please click the button to scan or configure NAS share path in settings.");
        AddTranslation("ViewMore", "查看更多 ({0} 部电影)", "View more ({0} movies)");
        AddTranslation("EnterKeywordSearch", "输入电影、导演、演员搜索电影", "Enter keyword to search movies");

        // 设置页面相关
        AddTranslation("NASShare", "NAS 共享", "NAS Shares");
        AddTranslation("NASSharePath", "NAS 共享路径配置", "NAS Share Path Configuration");
        AddTranslation("SharePath", "共享路径", "Share Path");
        AddTranslation("Online", "在线", "Online");
        AddTranslation("Offline", "离线", "Offline");
        AddTranslation("TestConnection", "测试连接", "Test Connection");
        AddTranslation("Delete", "删除", "Delete");
        AddTranslation("AddSharePath", "添加共享路径", "Add Share Path");
        AddTranslation("TestAllConnections", "测试所有连接", "Test All Connections");
        AddTranslation("Player", "播放器", "Player");
        AddTranslation("ExternalPlayer", "外部播放器配置", "External Player Settings");
        AddTranslation("PlayerPath", "播放器路径", "Player Path");
        AddTranslation("DefaultPlayerHint", "留空使用系统默认播放器", "Leave empty for system default player");
        AddTranslation("Browse", "浏览", "Browse");
        AddTranslation("Save", "保存", "Save");
        AddTranslation("APIKey", "API Key", "API Key");
        AddTranslation("TMDBAPI", "TMDB API", "TMDB API");
        AddTranslation("TMDBAPIKey", "TMDB API Key", "TMDB API Key");
        AddTranslation("EnterTMDBAPIKey", "输入你的 TMDB API Key", "Enter your TMDB API Key");
        AddTranslation("SubtitleDownload", "字幕下载", "Subtitle Download");
        AddTranslation("OpenSubtitlesAPI", "OpenSubtitles API 配置", "OpenSubtitles API Settings");
        AddTranslation("EnterOpenSubtitlesAPIKey", "输入你的 OpenSubtitles API Key", "Enter your OpenSubtitles API Key");
        AddTranslation("OpenSubtitlesWebsite", "OpenSubtitles 官网", "OpenSubtitles Website");
        AddTranslation("AIModel", "AI 模型", "AI Model");
        AddTranslation("OllamaSettings", "Ollama 配置", "Ollama Settings");
        AddTranslation("ModelUrl", "Ollama 地址", "Ollama URL");
        AddTranslation("ModelName", "模型名称", "Model Name");
        AddTranslation("DatabaseBackup", "数据库备份", "Database Backup");
        AddTranslation("BackupStrategy", "数据库备份策略", "Database Backup Strategy");
        AddTranslation("CreateBackup", "创建备份", "Create Backup");
        AddTranslation("CleanupOldBackups", "清理旧备份（保留最近7天）", "Cleanup Old Backups (Keep last 7 days)");
        AddTranslation("BackupFiles", "备份文件列表", "Backup Files List");
        AddTranslation("Restore", "恢复", "Restore");
        AddTranslation("RestoreConfirm", "恢复备份将覆盖当前数据库，此操作不可撤销。确定要继续吗？", "Restoring backup will overwrite current database. This operation cannot be undone. Continue?");
        AddTranslation("ConfirmRestore", "确认恢复", "Confirm Restore");
        AddTranslation("ConfirmDelete", "确认删除", "Confirm Delete");
        AddTranslation("AreYouSureDelete", "确定要删除此备份吗？", "Are you sure you want to delete this backup?");
        AddTranslation("BackupLocation", "备份文件存储位置", "Backup File Location");
        AddTranslation("DataExportImport", "数据导出/导入", "Data Export/Import");
        AddTranslation("MovieLibraryManagement", "电影库数据管理", "Movie Library Management");
        AddTranslation("ExportDescription", "导出电影库数据为 JSON 或 CSV 格式，方便备份和迁移。", "Export movie library data to JSON or CSV format for backup and migration.");
        AddTranslation("ExportToJson", "导出为 JSON", "Export to JSON");
        AddTranslation("ExportToCsv", "导出为 CSV", "Export to CSV");
        AddTranslation("ImportFromFile", "从文件导入", "Import from File");
        AddTranslation("Exporting", "导出中", "Exporting");
        AddTranslation("ExportCancelled", "导出已取消", "Export cancelled");
        AddTranslation("ImportCancelled", "导入已取消", "Import cancelled");
        AddTranslation("UnsupportedFormat", "不支持的文件格式", "Unsupported file format");
        AddTranslation("ReadingFile", "正在读取文件...", "Reading file...");
        AddTranslation("ImportingData", "正在导入数据...", "Importing data...");
        AddTranslation("ImportCompleted", "导入完成: 成功 {0} 部，跳过 {1} 部（已存在）", "Import completed: {0} imported, {1} skipped (already exists)");

        // 播放器相关
        AddTranslation("Volume", "音量", "Volume");
        AddTranslation("Mute", "静音", "Mute");
        AddTranslation("Unmute", "取消静音", "Unmute");
        AddTranslation("Fullscreen", "全屏", "Fullscreen");
        AddTranslation("ExitFullscreen", "退出全屏", "Exit Fullscreen");
        AddTranslation("AudioTrack", "音轨", "Audio Track");
        AddTranslation("SubtitleTrack", "字幕", "Subtitle");
        AddTranslation("Speed", "速度", "Speed");
        AddTranslation("Screenshot", "截图", "Screenshot");
        AddTranslation("Previous", "上一个", "Previous");
        AddTranslation("Next", "下一个", "Next");
        AddTranslation("Rewind", "后退", "Rewind");
        AddTranslation("Forward", "快进", "Forward");

        // 电影详情
        AddTranslation("MovieDetails", "电影详情", "Movie Details");
        AddTranslation("Overview", "简介", "Overview");
        AddTranslation("Cast", "演员", "Cast");
        AddTranslation("Director", "导演", "Director");
        AddTranslation("ReleaseDate", "上映日期", "Release Date");
        AddTranslation("Runtime", "片长", "Runtime");
        AddTranslation("Genres", "类型", "Genres");
        AddTranslation("Rating", "评分", "Rating");
        AddTranslation("Reviews", "影评", "Reviews");
        AddTranslation("AddReview", "添加影评", "Add Review");
        AddTranslation("WatchTrailer", "观看预告片", "Watch Trailer");
        AddTranslation("AddToWatchlist", "添加到观影计划", "Add to Watchlist");
        AddTranslation("DownloadSubtitle", "下载字幕", "Download Subtitle");
        AddTranslation("MarkAsWatched", "标记为已看", "Mark as Watched");
        AddTranslation("MarkAsUnwatched", "标记为未看", "Mark as Unwatched");

        // 消息提示
        AddTranslation("BackupCreated", "备份创建成功", "Backup created successfully");
        AddTranslation("BackupRestored", "备份恢复成功，请重启应用以生效", "Backup restored successfully. Please restart the application.");
        AddTranslation("BackupDeleted", "备份已删除", "Backup deleted");
        AddTranslation("OldBackupsCleaned", "旧备份清理完成", "Old backups cleaned");
        AddTranslation("ShareSaved", "NAS 共享路径已保存", "NAS share paths saved");
        AddTranslation("PlayerSettingsSaved", "播放器配置已保存", "Player settings saved");
        AddTranslation("TMDBAPISettingsSaved", "TMDB API 配置已保存", "TMDB API settings saved");
        AddTranslation("SubtitleSettingsSaved", "字幕下载 API 配置已保存", "Subtitle API settings saved");
        AddTranslation("AISettingsSaved", "AI 模型配置已保存", "AI model settings saved");
        AddTranslation("ExportSuccessful", "成功导出 {0} 部电影到 {1}", "Successfully exported {0} movies to {1}");
        AddTranslation("EnterSharePath", "请先输入共享路径", "Please enter a share path first");
        AddTranslation("ConnectionSuccessful", "路径 \"{0}\" 连接成功", "Path \"{0}\" connected successfully");
        AddTranslation("ConnectionFailed", "路径 \"{0}\" 无法访问", "Path \"{0}\" is not accessible");
        AddTranslation("AllConnectionsSuccessful", "所有 {0} 个共享路径连接成功", "All {0} share paths connected successfully");
        AddTranslation("AllConnectionsFailed", "所有 {0} 个共享路径连接失败", "All {0} share paths failed to connect");
        AddTranslation("PartialConnections", "{0} 个成功，{1} 个失败", "{0} succeeded, {1} failed");

        // 错误消息
        AddTranslation("BackupFailed", "备份创建失败: {0}", "Backup creation failed: {0}");
        AddTranslation("RestoreFailed", "恢复失败: {0}", "Restore failed: {0}");
        AddTranslation("DeleteFailed", "删除失败: {0}", "Delete failed: {0}");
        AddTranslation("CleanupFailed", "清理失败: {0}", "Cleanup failed: {0}");
        AddTranslation("SaveFailed", "保存失败: {0}", "Save failed: {0}");
        AddTranslation("ExportFailed", "导出失败: {0}", "Export failed: {0}");
        AddTranslation("ImportFailed", "导入失败: {0}", "Import failed: {0}");
        AddTranslation("TestConnectionFailed", "测试连接失败: {0}", "Connection test failed: {0}");
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