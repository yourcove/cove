using Microsoft.EntityFrameworkCore;
using Cove.Core.Entities;
using Cove.Plugins;

namespace Cove.Data;

public class CoveContext : DbContext
{
    private static IReadOnlyList<IDataExtension> _dataExtensions = [];

    public static void SetDataExtensions(IEnumerable<IDataExtension> extensions)
    {
        _dataExtensions = extensions.ToList();
    }

    public CoveContext(DbContextOptions<CoveContext> options) : base(options) { }

    // Core entities
    public DbSet<Scene> Scenes => Set<Scene>();
    public DbSet<Performer> Performers => Set<Performer>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<Studio> Studios => Set<Studio>();
    public DbSet<Gallery> Galleries => Set<Gallery>();
    public DbSet<Image> Images => Set<Image>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<SceneMarker> SceneMarkers => Set<SceneMarker>();
    public DbSet<SavedFilter> SavedFilters => Set<SavedFilter>();
    public DbSet<GalleryChapter> GalleryChapters => Set<GalleryChapter>();

    // Users
    public DbSet<User> Users => Set<User>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();

    // Extensions
    public DbSet<ExtensionData> ExtensionData => Set<ExtensionData>();

    // Files & Folders
    public DbSet<Folder> Folders => Set<Folder>();
    public DbSet<VideoFile> VideoFiles => Set<VideoFile>();
    public DbSet<ImageFile> ImageFiles => Set<ImageFile>();
    public DbSet<GalleryFile> GalleryFiles => Set<GalleryFile>();
    public DbSet<FileFingerprint> FileFingerprints => Set<FileFingerprint>();
    public DbSet<VideoCaption> VideoCaptions => Set<VideoCaption>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Apply all configurations from this assembly
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CoveContext).Assembly);

        // TPH for file hierarchy
        modelBuilder.Entity<BaseFileEntity>()
            .HasDiscriminator<string>("FileType")
            .HasValue<VideoFile>("Video")
            .HasValue<ImageFile>("Image")
            .HasValue<GalleryFile>("Gallery");

        modelBuilder.Entity<BaseFileEntity>()
            .HasMany(f => f.Fingerprints)
            .WithOne(fp => fp.File)
            .HasForeignKey(fp => fp.FileId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<FileFingerprint>(entity =>
        {
            entity.HasIndex(fp => new { fp.Type, fp.Value });
            entity.HasIndex(fp => fp.FileId);
        });

        modelBuilder.Entity<VideoCaption>()
            .ToTable("VideoCaptions");

        modelBuilder.Entity<VideoFile>()
            .HasMany(v => v.Captions)
            .WithOne(c => c.File)
            .HasForeignKey(c => c.FileId)
            .OnDelete(DeleteBehavior.Cascade);

        foreach (var ext in _dataExtensions)
        {
            ext.ConfigureModel(modelBuilder);
        }
    }

    public override int SaveChanges()
    {
        UpdateTimestamps();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        UpdateTimestamps();
        return base.SaveChangesAsync(cancellationToken);
    }

    private void UpdateTimestamps()
    {
        var entries = ChangeTracker.Entries()
            .Where(e => e.State == EntityState.Added || e.State == EntityState.Modified);
        var now = DateTime.UtcNow;

        foreach (var entry in entries)
        {
            if (entry.Entity is BaseEntity entity)
            {
                if (entry.State == EntityState.Added)
                {
                    if (entity.CreatedAt == default)
                        entity.CreatedAt = now;
                    if (entity.UpdatedAt == default)
                        entity.UpdatedAt = entity.CreatedAt;
                }
                else
                {
                    entity.UpdatedAt = now;
                }
            }
            else if (entry.Entity is BaseFileEntity file)
            {
                if (entry.State == EntityState.Added)
                {
                    if (file.CreatedAt == default)
                        file.CreatedAt = now;
                    if (file.UpdatedAt == default)
                        file.UpdatedAt = file.CreatedAt;
                }
                else
                {
                    file.UpdatedAt = now;
                }
            }
            else if (entry.Entity is Folder folder)
            {
                if (entry.State == EntityState.Added)
                {
                    if (folder.CreatedAt == default)
                        folder.CreatedAt = now;
                    if (folder.UpdatedAt == default)
                        folder.UpdatedAt = folder.CreatedAt;
                }
                else
                {
                    folder.UpdatedAt = now;
                }
            }
        }
    }
}
