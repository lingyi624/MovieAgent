using System.IO;
using System.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MovieAgent.Core.Interfaces;
using MovieAgent.Infrastructure.Data;
using MovieAgent.Infrastructure.Repositories;
using MovieAgent.Infrastructure.Services;
using MudBlazor.Services;

namespace MovieAgent;

public partial class App : Application
{
    public IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var config = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .Build();

        var services = new ServiceCollection();

        services.AddSingleton<IConfiguration>(config);

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

        var playerPath = config["Player:Path"] ?? "";
        services.AddSingleton<IPlayerService>(new CompositePlayerService(playerPath));

        var modelUrl = config["AI:ModelUrl"] ?? "http://localhost:11434";
        var modelName = config["AI:ModelName"] ?? "llama3:latest";
        services.AddSingleton<IAgentService>(sp =>
            new MovieAgentService(
                sp.GetRequiredService<IMovieRepository>(),
                sp.GetRequiredService<IPlayerService>(),
                modelUrl, modelName));

        var dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
        Directory.CreateDirectory(dataDir);
        var embeddingEndpoint = config["AI:EmbeddingEndpoint"] ?? "http://localhost:11434/api/embeddings";
        var embeddingModel = config["AI:EmbeddingModel"] ?? "llama3.2";
        services.AddSingleton<IVectorDatabaseService>(new LanceDbVectorDatabaseService());

        services.AddWpfBlazorWebView();
        services.AddMudServices();

        Services = services.BuildServiceProvider();

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        
        // 确保数据库存在并包含所有表
        try
        {
            db.Database.EnsureCreated();
            // 检查PlayHistories表是否存在，如果不存在则重新创建数据库
            if (!db.Database.CanConnect() || !db.Database.GetDbConnection().GetType().GetProperty("DataSource")?.GetValue(db.Database.GetDbConnection())?.ToString()?.Contains("PlayHistories") == true)
            {
                db.Database.EnsureDeleted();
                db.Database.EnsureCreated();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] 数据库初始化失败: {ex.Message}");
            try
            {
                db.Database.EnsureDeleted();
                db.Database.EnsureCreated();
            }
            catch { }
        }

        Task.Run(async () =>
        {
            var agent = Services.GetRequiredService<IAgentService>();
            await agent.InitializeAsync();
        });

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            MessageBox.Show($"程序异常: {(args.ExceptionObject as Exception)?.Message}",
                "Movie Agent", MessageBoxButton.OK, MessageBoxImage.Error);
        };
    }
}