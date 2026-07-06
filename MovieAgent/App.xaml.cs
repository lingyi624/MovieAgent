using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MovieAgent.Controls.Window;
using MovieAgent.Core.Interfaces;
using MovieAgent.FFmpegDecoder;
using MovieAgent.Infrastructure.Data;
using MovieAgent.Infrastructure.Repositories;
using MovieAgent.Infrastructure.Services;
using MovieAgent.Services;
using MudBlazor.Services;
using SQLitePCL;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
 
namespace MovieAgent;

public partial class App : Application
{
    [DllImport("shcore.dll")]
    private static extern int SetProcessDpiAwareness(int awareness);

    private const int PROCESS_PER_MONITOR_DPI_AWARE = 1;
    private const int PROCESS_PER_MONITOR_DPI_AWARE_V2 = 2;

   
    public IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 这是第一个执行的代码，必须写日志
        try
        { 
            SetProcessDpiAwareness(PROCESS_PER_MONITOR_DPI_AWARE_V2); 
             var logPath = Path.Combine(AppContext.BaseDirectory, "startup.log");
            File.WriteAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] 应用程序启动，开始执行 OnStartup\r\n");
        }
        catch (Exception ex)
        {
            // 回退到旧版本
            SetProcessDpiAwareness(PROCESS_PER_MONITOR_DPI_AWARE);
            // 如果连日志都写不了，那问题很严重
            try { File.WriteAllText(@"C:\temp\movieagent_error.log", $"[{DateTime.Now}] 日志写入失败: {ex.Message}\r\n"); } catch { }
        }
        // 启用全局未处理异常捕获
        this.DispatcherUnhandledException += (s, e) =>
        {
            DebugLogger.WriteLine($"UI线程未处理异常: {e.Exception}");
            e.Handled = false; // 设为 false 让调试器捕获
        };
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            DebugLogger.WriteLine($"AppDomain未处理异常: {e.ExceptionObject}");
        };
        System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.CatchException);
        // 初始化 DebugLogger
        DebugLogger.Initialize(AppContext.BaseDirectory);

        try
        {
            AppendLog("调用 base.OnStartup...");
            base.OnStartup(e);
            AppendLog("base.OnStartup 完成");

            AppendLog("设置 ShutdownMode = OnExplicitShutdown");
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            AppendLog("开始初始化配置...");
            var config = new ConfigurationBuilder()
                .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .Build();
            AppendLog("配置初始化完成");

            AppendLog("开始创建 ServiceCollection...");
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(config);
            AppendLog("ServiceCollection 创建完成");

            AppendLog("开始配置数据库...");
            var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "movies.db");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            services.AddDbContext<AppDbContext>(options =>
                options.UseSqlite($"Data Source={dbPath}"));
            AppendLog("数据库配置完成");

            // AI相关服务
            var modelUrl = config["AI:ModelUrl"] ?? "http://localhost:11434";
            var modelName = config["AI:ModelName"] ?? "phi3.5:3.8b-mini-instruct-q4_K_M";
            var embeddingEndpoint = config["AI:EmbeddingEndpoint"] ?? "http://localhost:11434";
            var embeddingModel = config["AI:EmbeddingModel"] ?? "nomic-embed-text-v2-moe:latest";
            
            // 向量维度配置（可选）：768（完整精度）、384（推荐）、256（更快速度）
            // 注意：减少维度后需要重新生成向量才能生效
            int? embeddingDimension = null;
            if (config["AI:EmbeddingDimension"] != null && int.TryParse(config["AI:EmbeddingDimension"], out int dim))
            {
                embeddingDimension = dim;
                AppendLog($"向量维度设置为: {dim}");
            }
            
            // 先注册向量数据库服务（需要在 MovieScannerService 之前）
            services.AddSingleton<IVectorDatabaseService>(new LanceDbVectorDatabaseService(embeddingEndpoint, embeddingModel, embeddingDimension));

            AppendLog("开始注册服务...");
            
            // 核心服务
            services.AddScoped<IMovieRepository, MovieRepository>();
            services.AddScoped<IPersonRepository, PersonRepository>();
            services.AddScoped<ICompanyRepository, CompanyRepository>();
            services.AddSingleton<IMediaInfoService, MediaInfoService>();
            services.AddScoped<IMovieScannerService, MovieScannerService>();
            services.AddScoped<IMovieUpdateService, MovieUpdateService>();
            services.AddScoped<IPlayHistoryService, PlayHistoryService>();
            services.AddScoped<ITagService, TagService>();
            services.AddScoped<IReportService, ReportService>();
            services.AddScoped<IPersonService, PersonService>();
            services.AddScoped<ICompanyService, CompanyService>();
            services.AddSingleton<FileWatcherService>();
            services.AddSingleton<IConversationMemoryService, ConversationMemoryService>();
            services.AddSingleton<ISearchCacheService, SearchCacheService>();
            services.AddScoped<IHybridSearchService, HybridSearchService>();
            services.AddSingleton<ISpeechService, WindowsSpeechService>();
            services.AddSingleton<IPlayerService, LocalPlayerService>();
            services.AddSingleton<ILoggerService, LoggerService>();
            services.AddSingleton<ILocalizationService, LocalizationService>();
            services.AddSingleton<IThemeService, ThemeService>();
            services.AddSingleton<IKeyboardShortcutService, Services.KeyboardShortcutService>();
            services.AddSingleton<IMovieExportService, MovieExportService>();

            // 配置和备份服务
            var configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MovieAgent", "Config");
            services.AddSingleton<IConfigStorageService>(new ConfigStorageService(configDir));
            
            var backupDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MovieAgent", "Backups");
            var dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            Directory.CreateDirectory(dataDir);
            services.AddSingleton<IBackupService>(new BackupService(dbPath, Path.Combine(dataDir, "lancedb"), backupDir));

            // TMDB服务
            var tmdbApiKey = config["TMDB:ApiKey"] ?? "YOUR_TMDB_API_KEY";
            services.AddSingleton<ITmdbService>(new TmdbService(tmdbApiKey));

            // HTTP客户端服务
            services.AddHttpClient<IDoubanService, DoubanService>();
            services.AddHttpClient<ISubtitleService, SubtitleService>()
                .SetHandlerLifetime(TimeSpan.FromMinutes(5));

            // 评论和观影计划服务
            services.AddScoped<ITransactionService, TransactionService>();
            services.AddScoped<IMovieReviewRepository, MovieReviewRepository>();
            services.AddScoped<IMovieReviewService, MovieReviewService>();
            services.AddScoped<IWatchPlanRepository, WatchPlanRepository>();
            services.AddScoped<IWatchPlanService, WatchPlanService>();

            services.AddSingleton<IAgentService>(sp =>
                new MovieAgentService(
                    sp.GetRequiredService<IMovieRepository>(),
                    sp.GetService<IMovieUpdateService>(),
                    sp.GetRequiredService<IPlayerService>(),
                    sp.GetRequiredService<IConversationMemoryService>(),
                    sp.GetRequiredService<IHybridSearchService>(),
                    sp.GetRequiredService<IVectorDatabaseService>(),
                    sp.GetRequiredService<IConfiguration>()));
            services.AddScoped<IMovieRecommendationService, MovieRecommendationService>();

            AppendLog("服务注册完成");

            AppendLog("开始添加 WPF Blazor 服务...");
            services.AddWpfBlazorWebView();
            services.AddMudServices();
            AppendLog("WPF Blazor 服务添加完成");

            AppendLog("开始构建 ServiceProvider...");
            Services = services.BuildServiceProvider();
            AppendLog("ServiceProvider 构建完成");

            AppendLog("开始初始化数据库...");
            using (var scope = Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.EnsureSchemaUpdatedAsync().GetAwaiter().GetResult();
            }
            AppendLog("数据库初始化完成");

            AppendLog("开始创建主窗口...");
            var mainWindow = new MainWindow();
            AppendLog("主窗口创建完成");

            AppendLog("设置 MainWindow...");
            Application.Current.MainWindow = mainWindow;
            AppendLog("MainWindow 设置完成");

            AppendLog("开始显示主窗口...");
            mainWindow.Show();
            AppendLog("主窗口显示完成");
            
            AppendLog("开始创建精灵窗口...");
            var spriteWindow = new Controls.Window.SpriteWindow();
            spriteWindow.SpriteClicked += (s, args) =>
            {
                Application.Current.MainWindow?.Activate();
            };
            spriteWindow.Show();
            AppendLog("精灵窗口显示完成，启动成功！");
           
        }
        catch (Exception ex)
        {
            AppendLog($"启动失败: {ex.Message}\r\n堆栈: {ex.StackTrace}");
            try
            {
                MessageBox.Show($"启动失败: {ex.Message}\r\n请检查 startup.log 查看详细信息。", "启动错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch
            {
                // 如果 MessageBox 也失败了，就没办法了
            }
            Shutdown();
        }
        
    }

    private void AppendLog(string message)
    {
        try
        {
            var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\r\n");
            Console.WriteLine($"[App] {message}");
        }
        catch { }
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception;
        AppendLog($"未处理的异常: {exception?.Message}\r\n堆栈: {exception?.StackTrace}\r\n内部异常: {exception?.InnerException?.Message}");
    }

    private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        // 忽略 Blazor WebView 的事件追踪错误，让应用程序继续运行
        string innerMsg = e.Exception?.InnerException?.Message ?? "";
        if (innerMsg.Contains("already tracked", StringComparison.OrdinalIgnoreCase))
        {
            AppendLog($"Blazor事件追踪警告(已忽略): {e.Exception.Message}");
            e.Handled = true;
            return;
        }
        AppendLog($"Dispatcher异常: {e.Exception.Message}\r\n堆栈: {e.Exception.StackTrace}\r\n内部异常: {e.Exception.InnerException?.Message}");
        e.Handled = true;
    }
}