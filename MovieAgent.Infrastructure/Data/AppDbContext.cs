using Microsoft.EntityFrameworkCore;
using MovieAgent.Core.Entities;

namespace MovieAgent.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public DbSet<Movie> Movies => Set<Movie>();
    public DbSet<PlayHistory> PlayHistories => Set<PlayHistory>();
    public DbSet<MovieReview> MovieReviews => Set<MovieReview>();
    public DbSet<WatchPlan> WatchPlans => Set<WatchPlan>();

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Movie>(entity =>
        {
            entity.HasIndex(m => m.TmdbId);
            entity.HasIndex(m => m.Title);
            entity.HasIndex(m => m.FilePath).IsUnique();
            entity.HasIndex(m => m.ReleaseYear);
            entity.HasIndex(m => m.IsWatched);
            entity.HasIndex(m => m.CreatedAt);
            entity.HasIndex(m => m.IsFavorite);
            entity.HasIndex(m => m.UserRating);
            entity.HasIndex(m => m.Resolution);
            entity.HasIndex(m => m.VideoCodec);
            entity.HasIndex(m => m.AudioCodec);
            entity.HasIndex(m => m.Genres);
        });

        modelBuilder.Entity<PlayHistory>(entity =>
        {
            entity.HasIndex(h => h.MovieId);
            entity.HasIndex(h => h.PlayedAt);
            entity.HasOne(h => h.Movie)
                  .WithMany()
                  .HasForeignKey(h => h.MovieId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MovieReview>(entity =>
        {
            entity.HasIndex(r => r.MovieId);
            entity.HasIndex(r => r.CreatedAt);
            entity.HasOne(r => r.Movie)
                  .WithMany()
                  .HasForeignKey(r => r.MovieId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WatchPlan>(entity =>
        {
            entity.HasIndex(p => p.MovieId);
            entity.HasIndex(p => p.PlannedDate);
            entity.HasIndex(p => p.IsCompleted);
            entity.HasOne(p => p.Movie)
                  .WithMany()
                  .HasForeignKey(p => p.MovieId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }
}