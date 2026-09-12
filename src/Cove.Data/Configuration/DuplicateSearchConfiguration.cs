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
        builder.Property(search => search.Status).HasConversion<string>().HasMaxLength(24);
        builder.Property(search => search.Error).HasMaxLength(2_000);
        builder.Property(search => search.DeletionJobId).HasMaxLength(32);
        builder.Property(search => search.KeeperRulesJson).HasMaxLength(8_000);
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
        builder.HasIndex(group => new { group.SearchId, group.Status });
        builder.Property(group => group.Status).HasConversion<string>().HasMaxLength(24);
        builder.Property(group => group.DecisionSource).HasMaxLength(16);
        builder.Property(group => group.DecisionRule).HasMaxLength(32);
        builder.Property(group => group.ResolutionAction).HasMaxLength(16);
        builder.Property(group => group.Error).HasMaxLength(2_000);
        builder.HasMany(group => group.Items)
            .WithOne(item => item.Group)
            .HasForeignKey(item => item.GroupId)
            .OnDelete(DeleteBehavior.Cascade);
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

public sealed class DuplicateIgnoredPairConfiguration : IEntityTypeConfiguration<DuplicateIgnoredPair>
{
    public void Configure(EntityTypeBuilder<DuplicateIgnoredPair> builder)
    {
        builder.ToTable("duplicate_ignored_pairs", table =>
        {
            table.HasCheckConstraint("CK_duplicate_ignored_pairs_ordered", "\"LowVideoId\" < \"HighVideoId\"");
            table.HasCheckConstraint("CK_duplicate_ignored_pairs_decision_count", "\"DecisionCount\" > 0");
        });
        builder.HasKey(pair => new { pair.LowVideoId, pair.HighVideoId });
        builder.Property(pair => pair.DecisionCount).HasDefaultValue(1);
        builder.HasIndex(pair => pair.HighVideoId);
        builder.HasOne(pair => pair.LowVideo)
            .WithMany()
            .HasForeignKey(pair => pair.LowVideoId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(pair => pair.HighVideo)
            .WithMany()
            .HasForeignKey(pair => pair.HighVideoId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
