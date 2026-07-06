using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MovieAgent.Core.Entities;

namespace MovieAgent.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public DbSet<Movie> Movies => Set<Movie>();
    public DbSet<PlayHistory> PlayHistories => Set<PlayHistory>();
    public DbSet<MovieReview> MovieReviews => Set<MovieReview>();
    public DbSet<WatchPlan> WatchPlans => Set<WatchPlan>();
    public DbSet<ConversationRecord> ConversationRecords => Set<ConversationRecord>();
    public DbSet<Person> Persons => Set<Person>();
    public DbSet<Company> Companies => Set<Company>();

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    /// <summary>
    /// 确保数据库架构是最新的（添加缺失的列）
    /// 此方法会自动检测并添加新的列，不会丢失现有数据
    /// </summary>
    public async Task EnsureSchemaUpdatedAsync()
    {
        await Database.EnsureCreatedAsync();

        await CreatePersonTableIfNotExistsAsync();
        await CreateCompanyTableIfNotExistsAsync();

        var movieNewColumns = new Dictionary<string, string>
        {
            { "ImdbId", "NVARCHAR(50)" },
            { "Tagline", "NVARCHAR(500)" },
            { "Homepage", "NVARCHAR(500)" },
            { "Status", "NVARCHAR(50)" },
            { "IsAdult", "INTEGER DEFAULT 0" },
            { "IsVideo", "INTEGER DEFAULT 0" },
            { "BelongsToCollection", "NVARCHAR(500)" },
            { "Budget", "INTEGER" },
            { "Revenue", "INTEGER" },
            { "Popularity", "REAL" },
            { "VoteCount", "INTEGER" },
            { "OriginalLanguage", "NVARCHAR(10)" },
            { "ProductionCompanies", "NVARCHAR(2000)" },
            { "ProductionCountries", "NVARCHAR(1000)" },
            { "OriginCountry", "NVARCHAR(1000)" },
            { "Keywords", "NVARCHAR(2000)" },
            { "AlternativeTitles", "NVARCHAR(2000)" },
            { "ReleaseDate", "TEXT" },
            { "VideoFormat", "NVARCHAR(50)" },
            { "VideoBitrate", "INTEGER" },
            { "FrameRate", "REAL" },
            { "Width", "INTEGER" },
            { "Height", "INTEGER" },
            { "AspectRatio", "NVARCHAR(20)" },
            { "ColorSpace", "NVARCHAR(50)" },
            { "BitDepth", "NVARCHAR(20)" },
            { "AudioChannels", "NVARCHAR(50)" },
            { "AudioBitrate", "INTEGER" },
            { "AudioLanguages", "NVARCHAR(200)" },
            { "SubtitleFormats", "NVARCHAR(200)" },
            { "Writer", "NVARCHAR(2000)" },
            { "PlaybackPosition", "REAL" },
            { "EmbeddingText", "NVARCHAR(10000)" },
            { "EmbeddingAt", "TEXT" },
            { "DirectorTmdbId", "NVARCHAR(500)" },
            { "WriterTmdbIds", "NVARCHAR(2000)" },
            { "CastTmdbIds", "NVARCHAR(4000)" },
            { "ProductionCompanyIds", "NVARCHAR(2000)" }
        };

        await AddMissingColumnsAsync("Movies", movieNewColumns);

        var conversationNewColumns = new Dictionary<string, string>
        {
            { "UserId", "NVARCHAR(100) NOT NULL DEFAULT 'default'" },
            { "UserMessage", "NVARCHAR(4000) NOT NULL DEFAULT ''" },
            { "AgentResponse", "NVARCHAR(8000) NOT NULL DEFAULT ''" },
            { "Timestamp", "TEXT" },
            { "IsSummary", "INTEGER DEFAULT 0" }
        };

        await AddMissingColumnsAsync("ConversationRecords", conversationNewColumns);
    }

    private async Task CreatePersonTableIfNotExistsAsync()
    {
        try
        {
            var tableExists = await Database
                .SqlQueryRaw<string>("SELECT name FROM sqlite_master WHERE type='table' AND name='Persons'")
                .AnyAsync();

            if (!tableExists)
            {
                await Database.ExecuteSqlRawAsync(@"
                    CREATE TABLE Persons (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        TmdbId NVARCHAR(100),
                        Name NVARCHAR(500) NOT NULL,
                        OriginalName NVARCHAR(500),
                        Biography NVARCHAR(5000),
                        ProfilePath NVARCHAR(500),
                        Birthday TEXT,
                        Deathday TEXT,
                        PlaceOfBirth NVARCHAR(200),
                        Gender NVARCHAR(50),
                        KnownForDepartment NVARCHAR(200),
                        Popularity REAL,
                        AlsoKnownAs NVARCHAR(2000),
                        KnownForTitles NVARCHAR(2000),
                        Credits NVARCHAR(5000),
                        Company NVARCHAR(500),
                        CreatedAt TEXT,
                        UpdatedAt TEXT,
                        INDEX idx_persons_tmdbid ON Persons(TmdbId),
                        INDEX idx_persons_name ON Persons(Name),
                        INDEX idx_persons_popularity ON Persons(Popularity)
                    )");
                Console.WriteLine("[AppDbContext] Created Persons table");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AppDbContext] Failed to create Persons table: {ex.Message}");
        }
    }

    private async Task CreateCompanyTableIfNotExistsAsync()
    {
        try
        {
            var tableExists = await Database
                .SqlQueryRaw<string>("SELECT name FROM sqlite_master WHERE type='table' AND name='Companies'")
                .AnyAsync();

            if (!tableExists)
            {
                await Database.ExecuteSqlRawAsync(@"
                    CREATE TABLE Companies (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        TmdbId NVARCHAR(100),
                        Name NVARCHAR(500) NOT NULL,
                        Description NVARCHAR(5000),
                        LogoPath NVARCHAR(500),
                        OriginCountry NVARCHAR(200),
                        Headquarters NVARCHAR(500),
                        Homepage NVARCHAR(500),
                        ParentCompany NVARCHAR(500),
                        MovieList NVARCHAR(5000),
                        PersonList NVARCHAR(5000),
                        CreatedAt TEXT,
                        UpdatedAt TEXT,
                        INDEX idx_companies_tmdbid ON Companies(TmdbId),
                        INDEX idx_companies_name ON Companies(Name)
                    )");
                Console.WriteLine("[AppDbContext] Created Companies table");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AppDbContext] Failed to create Companies table: {ex.Message}");
        }
    }

    /// <summary>
    /// 添加缺失的列（如果不存在）
    /// </summary>
    private async Task AddMissingColumnsAsync(string tableName, Dictionary<string, string> columns)
    {
        try
        {
            // 获取现有列
            var existingColumns = await Database
                .SqlQueryRaw<string>($"PRAGMA table_info({tableName})")
                .ToListAsync();

            var existingColumnNames = existingColumns
                .Select(col =>
                {
                    // PRAGMA table_info 返回格式：cid,name,type,notnull,dflt_value,pk
                    var parts = col.Split(',');
                    return parts.Length > 1 ? parts[1].Trim() : null;
                })
                .Where(n => n != null)
                .ToHashSet();

            foreach (var column in columns)
            {
                if (!existingColumnNames.Contains(column.Key))
                {
                    try
                    {
                        await Database.ExecuteSqlRawAsync(
                            $"ALTER TABLE {tableName} ADD COLUMN {column.Key} {column.Value}");
                        Console.WriteLine($"[AppDbContext] Added column {column.Key} to {tableName}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[AppDbContext] Failed to add column {column.Key}: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AppDbContext] Error checking columns for {tableName}: {ex.Message}");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ==================== Movie 实体配置 ====================
        modelBuilder.Entity<Movie>(entity =>
        {
            // 表名
            entity.ToTable("Movies");

            // 主键
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Id).ValueGeneratedOnAdd();

            // 基础标识字段
            entity.Property(m => m.TmdbId).HasMaxLength(50);
            entity.Property(m => m.ImdbId).HasMaxLength(50);
            entity.Property(m => m.Title).IsRequired().HasMaxLength(500);
            entity.Property(m => m.OriginalTitle).HasMaxLength(500);
            entity.Property(m => m.Overview).HasMaxLength(4000);
            entity.Property(m => m.Tagline).HasMaxLength(500);
            entity.Property(m => m.PosterPath).HasMaxLength(500);
            entity.Property(m => m.BackdropPath).HasMaxLength(500);

            // 上映信息
            entity.Property(m => m.ReleaseDate);
            entity.Property(m => m.ReleaseYear);
            entity.Property(m => m.Status).HasMaxLength(50);

            // 评分信息
            entity.Property(m => m.Rating).HasPrecision(3, 1);
            entity.Property(m => m.VoteCount);
            entity.Property(m => m.Popularity).HasPrecision(10, 2);

            // 内容信息
            entity.Property(m => m.Runtime);
            entity.Property(m => m.Genres).HasMaxLength(1000);
            entity.Property(m => m.Homepage).HasMaxLength(500);
            entity.Property(m => m.IsAdult);
            entity.Property(m => m.IsVideo);
            entity.Property(m => m.BelongsToCollection).HasMaxLength(500);

            // 财务信息
            entity.Property(m => m.Budget);
            entity.Property(m => m.Revenue);

            // 语言和制片信息
            entity.Property(m => m.OriginalLanguage).HasMaxLength(10);
            entity.Property(m => m.ProductionCompanies).HasMaxLength(2000);
            entity.Property(m => m.ProductionCountries).HasMaxLength(1000);
            entity.Property(m => m.OriginCountry).HasMaxLength(1000);
            entity.Property(m => m.Keywords).HasMaxLength(2000);
            entity.Property(m => m.AlternativeTitles).HasMaxLength(2000);

            // 本地文件信息
            entity.Property(m => m.FilePath).IsRequired().HasMaxLength(2000);
            entity.Property(m => m.FileSize);

            // 视频技术信息
            entity.Property(m => m.VideoCodec).HasMaxLength(50);
            entity.Property(m => m.VideoFormat).HasMaxLength(50);
            entity.Property(m => m.VideoBitrate);
            entity.Property(m => m.FrameRate).HasPrecision(5, 3);
            entity.Property(m => m.Width);
            entity.Property(m => m.Height);
            entity.Property(m => m.AspectRatio).HasMaxLength(20);
            entity.Property(m => m.HdrType).HasMaxLength(50);
            entity.Property(m => m.ColorSpace).HasMaxLength(50);
            entity.Property(m => m.BitDepth).HasMaxLength(20);

            // 音频技术信息
            entity.Property(m => m.AudioCodec).HasMaxLength(100);
            entity.Property(m => m.AudioChannels).HasMaxLength(50);
            entity.Property(m => m.AudioBitrate);
            entity.Property(m => m.AudioLanguages).HasMaxLength(200);
            entity.Property(m => m.SubtitleFormats).HasMaxLength(200);

            // 分辨率
            entity.Property(m => m.Resolution).HasMaxLength(20);

            // 演职人员
            entity.Property(m => m.Director).HasMaxLength(500);
            entity.Property(m => m.Writer).HasMaxLength(2000);
            entity.Property(m => m.Cast).HasMaxLength(4000);

            // 其他信息
            entity.Property(m => m.Language).HasMaxLength(200);
            entity.Property(m => m.Country).HasMaxLength(200);
            entity.Property(m => m.ReleaseGroup).HasMaxLength(200);
            entity.Property(m => m.Tags).HasMaxLength(2000);

            // 收藏和评价
            entity.Property(m => m.IsFavorite);
            entity.Property(m => m.UserRating);
            entity.Property(m => m.WatchedAt);
            entity.Property(m => m.IsWatched);
            entity.Property(m => m.PlaybackPosition).HasPrecision(10, 2);

            // 电视剧相关
            entity.Property(m => m.IsTVSeries);
            entity.Property(m => m.SeasonNumber);
            entity.Property(m => m.EpisodeNumber);

            // 时间戳
            entity.Property(m => m.CreatedAt);
            entity.Property(m => m.UpdatedAt);

            // 向量数据库
            entity.Property(m => m.EmbeddingText).HasMaxLength(10000);
            entity.Property(m => m.EmbeddingAt);

            // ==================== 索引 ====================
            entity.HasIndex(m => m.TmdbId);
            entity.HasIndex(m => m.ImdbId);
            entity.HasIndex(m => m.Title);
            entity.HasIndex(m => m.FilePath).IsUnique();
            entity.HasIndex(m => m.ReleaseYear);
            entity.HasIndex(m => m.ReleaseDate);
            entity.HasIndex(m => m.Rating);
            entity.HasIndex(m => m.VoteCount);
            entity.HasIndex(m => m.Popularity);
            entity.HasIndex(m => m.IsWatched);
            entity.HasIndex(m => m.CreatedAt);
            entity.HasIndex(m => m.IsFavorite);
            entity.HasIndex(m => m.UserRating);
            entity.HasIndex(m => m.Resolution);
            entity.HasIndex(m => m.VideoCodec);
            entity.HasIndex(m => m.AudioCodec);
            entity.HasIndex(m => m.Genres);
            entity.HasIndex(m => m.Director);
            entity.HasIndex(m => m.Language);
            entity.HasIndex(m => m.Country);
            entity.HasIndex(m => m.ReleaseGroup);
            entity.HasIndex(m => m.IsTVSeries);
            entity.HasIndex(m => m.SeasonNumber);
            entity.HasIndex(m => m.EpisodeNumber);
        });

        // ==================== PlayHistory 实体配置 ====================
        modelBuilder.Entity<PlayHistory>(entity =>
        {
            entity.ToTable("PlayHistories");
            entity.HasKey(h => h.Id);
            entity.Property(h => h.Id).ValueGeneratedOnAdd();

            entity.Property(h => h.MovieId);
            entity.Property(h => h.PlayedAt);
            entity.Property(h => h.Progress);
            entity.Property(h => h.Duration);

            entity.HasIndex(h => h.MovieId);
            entity.HasIndex(h => h.PlayedAt);

            entity.HasOne(h => h.Movie)
                  .WithMany()
                  .HasForeignKey(h => h.MovieId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // ==================== MovieReview 实体配置 ====================
        modelBuilder.Entity<MovieReview>(entity =>
        {
            entity.ToTable("MovieReviews");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedOnAdd();

            entity.Property(r => r.MovieId);
            entity.Property(r => r.Content).IsRequired().HasMaxLength(10000);
            entity.Property(r => r.Rating);
            entity.Property(r => r.CreatedAt);
            entity.Property(r => r.UpdatedAt);

            entity.HasIndex(r => r.MovieId);
            entity.HasIndex(r => r.CreatedAt);

            entity.HasOne(r => r.Movie)
                  .WithMany()
                  .HasForeignKey(r => r.MovieId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // ==================== WatchPlan 实体配置 ====================
        modelBuilder.Entity<WatchPlan>(entity =>
        {
            entity.ToTable("WatchPlans");
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Id).ValueGeneratedOnAdd();

            entity.Property(p => p.MovieId);
            entity.Property(p => p.PlannedDate);
            entity.Property(p => p.Note).HasMaxLength(2000);
            entity.Property(p => p.IsCompleted);
            entity.Property(p => p.CreatedAt);
            entity.Property(p => p.UpdatedAt);

            entity.HasIndex(p => p.MovieId);
            entity.HasIndex(p => p.PlannedDate);
            entity.HasIndex(p => p.IsCompleted);

            entity.HasOne(p => p.Movie)
                  .WithMany()
                  .HasForeignKey(p => p.MovieId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // ==================== ConversationRecord 实体配置 ====================
        modelBuilder.Entity<ConversationRecord>(entity =>
        {
            entity.ToTable("ConversationRecords");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Id).ValueGeneratedOnAdd();

            entity.Property(c => c.UserId).IsRequired().HasMaxLength(100);
            entity.Property(c => c.UserMessage).IsRequired().HasMaxLength(4000);
            entity.Property(c => c.AgentResponse).IsRequired().HasMaxLength(8000);
            entity.Property(c => c.Timestamp);
            entity.Property(c => c.IsSummary);

            entity.HasIndex(c => c.UserId);
            entity.HasIndex(c => c.Timestamp);
        });

        // ==================== Person 实体配置 ====================
        modelBuilder.Entity<Person>(entity =>
        {
            entity.ToTable("Persons");
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Id).ValueGeneratedOnAdd();

            entity.Property(p => p.TmdbId).HasMaxLength(100);
            entity.Property(p => p.Name).IsRequired().HasMaxLength(500);
            entity.Property(p => p.OriginalName).HasMaxLength(500);
            entity.Property(p => p.Biography).HasMaxLength(5000);
            entity.Property(p => p.ProfilePath).HasMaxLength(500);
            entity.Property(p => p.PlaceOfBirth).HasMaxLength(200);
            entity.Property(p => p.Gender).HasMaxLength(50);
            entity.Property(p => p.KnownForDepartment).HasMaxLength(200);
            entity.Property(p => p.AlsoKnownAs).HasMaxLength(2000);
            entity.Property(p => p.KnownForTitles).HasMaxLength(2000);
            entity.Property(p => p.Credits).HasMaxLength(5000);
            entity.Property(p => p.Company).HasMaxLength(500);

            entity.HasIndex(p => p.TmdbId);
            entity.HasIndex(p => p.Name);
            entity.HasIndex(p => p.Popularity);
        });

        // ==================== Company 实体配置 ====================
        modelBuilder.Entity<Company>(entity =>
        {
            entity.ToTable("Companies");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Id).ValueGeneratedOnAdd();

            entity.Property(c => c.TmdbId).HasMaxLength(100);
            entity.Property(c => c.Name).IsRequired().HasMaxLength(500);
            entity.Property(c => c.Description).HasMaxLength(5000);
            entity.Property(c => c.LogoPath).HasMaxLength(500);
            entity.Property(c => c.OriginCountry).HasMaxLength(200);
            entity.Property(c => c.Headquarters).HasMaxLength(500);
            entity.Property(c => c.Homepage).HasMaxLength(500);
            entity.Property(c => c.ParentCompany).HasMaxLength(500);
            entity.Property(c => c.MovieList).HasMaxLength(5000);
            entity.Property(c => c.PersonList).HasMaxLength(5000);

            entity.HasIndex(c => c.TmdbId);
            entity.HasIndex(c => c.Name);
        });
    }
}
