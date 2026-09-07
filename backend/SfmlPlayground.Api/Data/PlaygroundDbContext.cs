using Microsoft.EntityFrameworkCore;
using SfmlPlayground.Api.Models;

namespace SfmlPlayground.Api.Data;

public class PlaygroundDbContext : DbContext
{
    public PlaygroundDbContext(DbContextOptions<PlaygroundDbContext> options)
        : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectFile> ProjectFiles => Set<ProjectFile>();
    public DbSet<ProjectAsset> ProjectAssets => Set<ProjectAsset>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // User
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Username).IsRequired().HasMaxLength(50);
            entity.HasIndex(u => u.Username).IsUnique();
        });

        // Project
        modelBuilder.Entity<Project>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Name).IsRequired().HasMaxLength(100);
            entity.Property(p => p.Description).HasMaxLength(500);

            entity.HasOne(p => p.User)
                .WithMany(u => u.Projects)
                .HasForeignKey(p => p.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ProjectFile
        modelBuilder.Entity<ProjectFile>(entity =>
        {
            entity.HasKey(f => f.Id);
            entity.Property(f => f.Path).IsRequired().HasMaxLength(260);
            entity.Property(f => f.Content).IsRequired();

            entity.HasOne(f => f.Project)
                .WithMany(p => p.Files)
                .HasForeignKey(f => f.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(f => new { f.ProjectId, f.Path }).IsUnique();
        });

        // ProjectAsset
        modelBuilder.Entity<ProjectAsset>(entity =>
        {
            entity.HasKey(a => a.Id);
            entity.Property(a => a.FileName).IsRequired().HasMaxLength(260);
            entity.Property(a => a.RelativePath).IsRequired().HasMaxLength(260);
            entity.Property(a => a.StoragePath).IsRequired().HasMaxLength(500);
            entity.Property(a => a.ContentType).HasMaxLength(100);

            entity.HasOne(a => a.Project)
                .WithMany(p => p.Assets)
                .HasForeignKey(a => a.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(a => new { a.ProjectId, a.RelativePath }).IsUnique();
        });
    }
}
