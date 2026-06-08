using Microsoft.EntityFrameworkCore;
using MovieAgent.Core.Entities;

namespace MovieAgent.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public DbSet<Movie> Movies => Set<Movie>();
    public DbSet<PlayHistory> PlayHistories => Set<PlayHistory>();

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
    }
}