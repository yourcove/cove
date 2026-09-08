using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cove.Data.Configuration;

public sealed class DuplicateSearchConfiguration : IEntityTypeConfiguration<DuplicateSearch>
{
    public void Configure(EntityTypeBuilder<DuplicateSearch> builder)
    {
        builder.ToTable("duplicate_searches");
        builder.HasKey(search => search.Id);
        builder.Property(search => search.OwnerKey).HasMaxLength(96);
        builder.Property(search => search.JobId).HasMaxLength(32);
        builder.Property(search => search.MatchType).HasMaxLength(32);
        builder.Property(search => search.FingerprintAlgorithm).HasMaxLength(16).HasDefaultValue("any");
        builder.Property(search => search.FolderMode).HasMaxLength(16).HasDefaultValue("all");
        builder.Property(search => search.FolderPathsJson).HasColumnType("jsonb").HasDefaultValue("[]");
        builder.Property(search => search.RankingMode).HasMaxLength(16).HasDefaultValue("balanced");
        builder.Property(search => search.PreferredCodecsJson).HasColumnType("jsonb").HasDefaultValue("[\"av1\",\"hevc\",\"h264\",\"vp9\",\"mpeg4\"]");
        builder.Property(search => search.KeeperRulesJson).HasColumnType("jsonb").HasDefaultValue("[\"resolution\",\"codec\",\"bitrate\",\"duration\",\"metadata\",\"oldest\"]");
        builder.Property(search => search.Status).HasConversion<string>().HasMaxLength(24);
        builder.Property(search => search.Error).HasMaxLength(2_000);
        builder.Property(search => search.DeletionJobId).HasMaxLength(32);
        builder.HasIndex(search => new { search.OwnerKey, search.CreatedAt });
        builder.HasIndex(search => search.ExpiresAt);
        builder.HasMany(search => search.Groups)
            .WithOne(group => group.Search)
            .HasForeignKey(group => group.SearchId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(search => search.KeeperReservations)
            .WithOne(reservation => reservation.Search)
            .HasForeignKey(reservation => reservation.SearchId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class DuplicateDeletionKeeperReservationConfiguration : IEntityTypeConfiguration<DuplicateDeletionKeeperReservation>
{
    public void Configure(EntityTypeBuilder<DuplicateDeletionKeeperReservation> builder)
    {
        builder.ToTable("duplicate_deletion_keeper_reservations");
        builder.HasKey(item => new { item.SearchId, item.VideoId });
        builder.HasIndex(item => item.VideoId);
        builder.HasOne(item => item.Video)
            .WithMany()
            .HasForeignKey(item => item.VideoId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class DuplicateSearchGroupConfiguration : IEntityTypeConfiguration<DuplicateSearchGroup>
{
    public void Configure(EntityTypeBuilder<DuplicateSearchGroup> builder)
    {
        builder.ToTable("duplicate_search_groups");
        builder.HasKey(group => group.Id);
        builder.HasIndex(group => new { group.SearchId, group.Position }).IsUnique();
        builder.Property(group => group.RecommendationReason).HasMaxLength(512);
        builder.Property(group => group.RiskNotesJson).HasColumnType("jsonb").HasDefaultValue("[]");
        builder.HasMany(group => group.Items)
            .WithOne(item => item.Group)
            .HasForeignKey(item => item.GroupId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ImageDuplicateSearchConfiguration : IEntityTypeConfiguration<ImageDuplicateSearch>
{
    public void Configure(EntityTypeBuilder<ImageDuplicateSearch> builder)
    {
        builder.ToTable("image_duplicate_searches");
        builder.HasKey(search => search.Id);
        builder.Property(search => search.OwnerKey).HasMaxLength(96);
        builder.Property(search => search.JobId).HasMaxLength(32);
        builder.Property(search => search.Status).HasConversion<string>().HasMaxLength(24);
        builder.Property(search => search.Error).HasMaxLength(2_000);
        builder.Property(search => search.CleanupJobId).HasMaxLength(32);
        builder.HasIndex(search => new { search.OwnerKey, search.CreatedAt });
        builder.HasIndex(search => search.ExpiresAt);
        builder.HasMany(search => search.Groups).WithOne(group => group.Search)
            .HasForeignKey(group => group.SearchId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(search => search.KeeperReservations).WithOne(item => item.Search)
            .HasForeignKey(item => item.SearchId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ImageDuplicateSearchGroupConfiguration : IEntityTypeConfiguration<ImageDuplicateSearchGroup>
{
    public void Configure(EntityTypeBuilder<ImageDuplicateSearchGroup> builder)
    {
        builder.ToTable("image_duplicate_search_groups");
        builder.HasKey(group => group.Id);
        builder.Property(group => group.Hash).HasMaxLength(256);
        builder.HasIndex(group => new { group.SearchId, group.Position }).IsUnique();
        builder.HasMany(group => group.Items).WithOne(item => item.Group)
            .HasForeignKey(item => item.GroupId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ImageDuplicateSearchItemConfiguration : IEntityTypeConfiguration<ImageDuplicateSearchItem>
{
    public void Configure(EntityTypeBuilder<ImageDuplicateSearchItem> builder)
    {
        builder.ToTable("image_duplicate_search_items");
        builder.HasKey(item => new { item.GroupId, item.FileId });
        builder.HasIndex(item => item.ImageId);
        builder.HasOne(item => item.File).WithMany().HasForeignKey(item => item.FileId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(item => item.Image).WithMany().HasForeignKey(item => item.ImageId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ImageDuplicateKeeperReservationConfiguration : IEntityTypeConfiguration<ImageDuplicateKeeperReservation>
{
    public void Configure(EntityTypeBuilder<ImageDuplicateKeeperReservation> builder)
    {
        builder.ToTable("image_duplicate_keeper_reservations");
        builder.HasKey(item => new { item.SearchId, item.FileId });
        builder.HasIndex(item => item.ImageId);
        builder.HasOne(item => item.Image).WithMany().HasForeignKey(item => item.ImageId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(item => item.File).WithMany().HasForeignKey(item => item.FileId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class DuplicateSearchItemConfiguration : IEntityTypeConfiguration<DuplicateSearchItem>
{
    public void Configure(EntityTypeBuilder<DuplicateSearchItem> builder)
    {
        builder.ToTable("duplicate_search_items");
        builder.HasKey(item => new { item.GroupId, item.VideoId });
        builder.HasIndex(item => item.VideoId);
        builder.HasOne(item => item.Video)
            .WithMany()
            .HasForeignKey(item => item.VideoId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
