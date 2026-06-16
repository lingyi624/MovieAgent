using System.IO;
using System.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MovieAgent.Core.Interfaces;
using MovieAgent.Infrastructure.Data;
using MovieAgent.Infrastructure.Repositories;
using MovieAgent.Infrastructure.Services;
using MovieAgent.Services;
using MudBlazor.Services;

namespace MovieAgent;

public partial class App : Application
{
    public IServiceProvider Services { get; private set; } = null!;
    private SplashScreen? _splashScreen;

    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        DispatcherUnhandledException += App_DispatcherUnhandledException;

        base.OnStartup(e);

        // 生成应用图标
        try
        {
            IconGenerator.GenerateIcons();
        }
        catch { }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            _splashScreen = new SplashScreen();
            _splashScreen.Show();
        }
        catch (Exception ex)
        {
            LogError($"启动画面创建失败: {ex.Message}");
        }

        InitializeApplication();
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception;
        string errorMsg = $"未处理的异常: {exception?.Message}\r\n堆栈: {exception?.StackTrace}";
        Console.WriteLine(errorMsg);
        LogError(errorMsg);
    }

    private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        string errorMsg = $"Dispatcher异常: {e.Exception.Message}\r\n堆栈: {e.Exception.StackTrace}";
        Console.WriteLine(errorMsg);
        LogError(errorMsg);
        e.Handled = true;
    }

    private void LogError(string message)
    {
        try
        {
            var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "error.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}\r\n");
        }
        catch { }
    }

    private void InitializeApplication()
    {
        Task.Run(async () =>
        {
            try
            {
                Console.WriteLine("[App] 开始初始化...");
                UpdateSplashStatus("正在加载配置...");
                await Task.Delay(200);

                var config = new ConfigurationBuilder()
                    .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                    .Build();
                Console.WriteLine("[App] 配置加载完成");

                UpdateSplashStatus("正在注册服务...");
                await Task.Delay(200);

                var services = new ServiceCollection();
                services.AddSingleton<IConfiguration>(config);

                UpdateSplashStatus("正在配置数据库...");
                await Task.Delay(200);

                var dbPath = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "data", "movies.db");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dbPath)!);
                services.AddDbContext<AppDbContext>(options =>
                    options.UseSqlite($"Data Source={dbPath}"));

                services.AddScoped<IMovieRepository, MovieRepository>();

                var tmdbApiKey = config["TMDB:ApiKey"] ?? "YOUR_TMDB_API_KEY";
                services.AddSingleton<ITmdbService>(new TmdbService(tmdbApiKey));

                services.AddSingleton<IMediaInfoService, MediaInfoService>();
                services.AddScoped<IMovieScannerService, MovieScannerService>();
                services.AddScoped<IPlayHistoryService, PlayHistoryService>();
                services.AddScoped<ITagService, TagService>();
                services.AddScoped<IReportService, ReportService>();
                services.AddSingleton<FileWatcherService>();
                services.AddSingleton<IConversationMemoryService, ConversationMemoryService>();
                services.AddSingleton<ISearchCacheService, SearchCacheService>();
                services.AddSingleton<IVideoAnalysisService>(sp => 
                    new VideoAnalysisService(
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MovieAgent", "Analysis"),
                        sp.GetRequiredService<IAgentService>()));
                services.AddScoped<IHybridSearchService, HybridSearchService>();
                services.AddSingleton<ISpeechService, SpeechService>();
                
                var configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MovieAgent", "Config");
                services.AddSingleton<IConfigStorageService>(new ConfigStorageService(configDir));
                
                var backupDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MovieAgent", "Backups");
                var dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
                Directory.CreateDirectory(dataDir);
                services.AddSingleton<IBackupService>(new BackupService(dbPath, Path.Combine(dataDir, "lancedb"), backupDir));
                
                services.AddScoped<ITransactionService, TransactionService>();
                services.AddScoped<IMovieReviewRepository, MovieReviewRepository>();
                services.AddScoped<IMovieReviewService, MovieReviewService>();
                services.AddScoped<IWatchPlanRepository, WatchPlanRepository>();
                services.AddScoped<IWatchPlanService, WatchPlanService>();
                services.AddHttpClient<IDoubanService, DoubanService>();
                services.AddHttpClient<ISubtitleService, SubtitleService>()
                    .SetHandlerLifetime(TimeSpan.FromMinutes(5));

                services.AddSingleton<IPlayerService, ProcessIsolatedPlayerService>();

                var modelUrl = config["AI:ModelUrl"] ?? "http://localhost:11434";
                var modelName = config["AI:ModelName"] ?? "llama3:latest";
                services.AddSingleton<IAgentService>(sp =>
                    new MovieAgentService(
                        sp.GetRequiredService<IMovieRepository>(),
                        sp.GetRequiredService<IPlayerService>(),
                        sp.GetRequiredService<IConversationMemoryService>(),
                        sp.GetRequiredService<IHybridSearchService>(),
                        modelUrl, modelName));

                var embeddingEndpoint = config["AI:EmbeddingEndpoint"] ?? "http://localhost:11434";
                services.AddSingleton<IVectorDatabaseService>(new LanceDbVectorDatabaseService(embeddingEndpoint));
                services.AddScoped<IMovieRecommendationService, MovieRecommendationService>();
                services.AddSingleton<IThemeService, ThemeService>();
                services.AddSingleton<IKeyboardShortcutService, Services.KeyboardShortcutService>();
                services.AddSingleton<IMovieExportService, MovieExportService>();
                services.AddSingleton<ILoggerService, LoggerService>();
                services.AddSingleton<ILocalizationService, LocalizationService>();
                Console.WriteLine("[App] 服务注册完成");

                UpdateSplashStatus("正在初始化Blazor...");
                await Task.Delay(200);

                services.AddWpfBlazorWebView();
                services.AddMudServices();

                Services = services.BuildServiceProvider();
                Console.WriteLine("[App] ServiceProvider 创建完成");

                UpdateSplashStatus("正在连接数据库...");
                await Task.Delay(200);

                using var scope = Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                
                try
                {
                    db.Database.EnsureCreated();
                    Console.WriteLine("[App] 数据库初始化完成");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[App] 数据库初始化失败: {ex.Message}");
                    try
                    {
                        db.Database.EnsureDeleted();
                        db.Database.EnsureCreated();
                    }
                    catch { }
                }

                UpdateSplashStatus("正在初始化AI服务...");
                await Task.Delay(200);

                try
                {
                    var agent = Services.GetRequiredService<IAgentService>();
                    await agent.InitializeAsync();
                    Console.WriteLine("[App] AI服务初始化完成");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[App] AI服务初始化失败: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[App] 初始化过程发生错误: {ex.Message}");
                Console.WriteLine($"[App] 异常堆栈: {ex.StackTrace}");
            }

            Console.WriteLine("[App] 更新状态为准备就绪...");
            UpdateSplashStatus("准备就绪...");
            await Task.Delay(300);

            Console.WriteLine("[App] 准备调用 Dispatcher.Invoke...");
            Dispatcher.Invoke(() =>
            {
                Console.WriteLine("[App] Dispatcher.Invoke 内部: 关闭启动画面...");
                _splashScreen?.Close();
                Console.WriteLine("[App] Dispatcher.Invoke 内部: 创建主窗口...");
                var mainWindow = new MainWindow();
                Console.WriteLine("[App] Dispatcher.Invoke 内部: 显示主窗口...");
                mainWindow.Show();
                Console.WriteLine("[App] Dispatcher.Invoke 内部: 主窗口已显示");

                // 设置主窗口引用，防止应用退出
                if (MainWindow == null)
                {
                    MainWindow = mainWindow;
                }
            });
            Console.WriteLine("[App] Dispatcher.Invoke 完成");
        });
    }

    private void UpdateSplashStatus(string status)
    {
        _splashScreen?.Dispatcher.Invoke(() => _splashScreen.UpdateStatus(status));
    }
}