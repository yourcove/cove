using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;

namespace Cove.Data.Configuration;

public class VideoConfiguration : IEntityTypeConfiguration<Video>
{
    public void Configure(EntityTypeBuilder<Video> builder)
    {
        builder.ToTable("videos");
        builder.HasKey(s => s.Id);

        builder.HasOne(s => s.Studio)
            .WithMany(st => st.Videos)
            .HasForeignKey(s => s.StudioId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(s => s.ParentVideo)
            .WithMany(s => s.ChildVideos)
            .HasForeignKey(s => s.ParentVideoId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(s => s.Urls).WithOne(u => u.Video).HasForeignKey(u => u.VideoId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(s => s.Files).WithOne(f => f.Video).HasForeignKey(f => f.VideoId).OnDelete(DeleteBehavior.SetNull);
        builder.HasMany(s => s.RemoteIds).WithOne(si => si.Video).HasForeignKey(si => si.VideoId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(s => s.PlayHistory).WithOne(h => h.Video).HasForeignKey(h => h.VideoId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(s => s.Title);
        builder.HasIndex(s => s.StudioId);
        builder.HasIndex(s => s.ParentVideoId);
        builder.HasIndex(s => s.Date);
        builder.HasIndex(s => s.CreatedAt);
        builder.HasIndex(s => s.UpdatedAt);
        builder.HasIndex(s => s.Organized);
        builder.HasIndex(s => s.IsVr);
        builder.Property(s => s.IsVr).HasDefaultValue(false);
        builder.Property(s => s.FileCount).HasDefaultValue(0);
        builder.Property(s => s.MaxDuration).HasDefaultValue(0d);
        builder.Property(s => s.MaxResolution).HasDefaultValue(0);
        builder.Property(s => s.MaxHeight).HasDefaultValue(0);
        builder.Property(s => s.MaxFrameRate).HasDefaultValue(0d);
        builder.Property(s => s.MaxBitRate).HasDefaultValue(0L);
        builder.Property(s => s.MaxFileSize).HasDefaultValue(0L);
        builder.Property(s => s.HasDimensionData).HasDefaultValue(false);
        builder.Property(s => s.HasLandscapeFiles).HasDefaultValue(false);
        builder.Property(s => s.HasPortraitFiles).HasDefaultValue(false);
        builder.Property(s => s.HasSquareFiles).HasDefaultValue(false);
        builder.HasIndex(s => s.FileCount);
        builder.HasIndex(s => s.MaxDuration);
        builder.HasIndex(s => s.MaxResolution);
        builder.HasIndex(s => s.MaxHeight);
        builder.HasIndex(s => s.MaxFrameRate);
        builder.HasIndex(s => s.MaxBitRate);
        builder.HasIndex(s => s.MaxFileSize);
        builder.HasIndex(s => s.MaxFileModTime);
        builder.HasIndex(s => s.MinPath);
        builder.HasIndex(s => s.MaxPath);
        builder.HasIndex(s => s.HasDimensionData);
        builder.HasIndex(s => s.HasLandscapeFiles);
        builder.HasIndex(s => s.HasPortraitFiles);
        builder.HasIndex(s => s.HasSquareFiles);

        // GIN-indexed denormalized id sets for tag/performer combo filters.
        builder.Property(s => s.TagIds).HasColumnType("integer[]");
        builder.Property(s => s.PerformerIds).HasColumnType("integer[]");
        builder.HasIndex(s => s.TagIds).HasMethod("gin");
        builder.HasIndex(s => s.PerformerIds).HasMethod("gin");
    }
}

public class VideoTagConfiguration : IEntityTypeConfiguration<VideoTag>
{
    public void Configure(EntityTypeBuilder<VideoTag> builder)
    {
        builder.ToTable("video_tags");
        builder.HasKey(st => new { st.VideoId, st.TagId });
        builder.HasOne(st => st.Video).WithMany(s => s.VideoTags).HasForeignKey(st => st.VideoId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(st => st.Tag).WithMany(t => t.VideoTags).HasForeignKey(st => st.TagId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(st => st.TagId);
    }
}

public class VideoPerformerConfiguration : IEntityTypeConfiguration<VideoPerformer>
{
    public void Configure(EntityTypeBuilder<VideoPerformer> builder)
    {
        builder.ToTable("video_performers");
        builder.HasKey(sp => new { sp.VideoId, sp.PerformerId });
        builder.HasOne(sp => sp.Video).WithMany(s => s.VideoPerformers).HasForeignKey(sp => sp.VideoId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(sp => sp.Performer).WithMany(p => p.VideoPerformers).HasForeignKey(sp => sp.PerformerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(sp => sp.PerformerId);
    }
}

public class VideoRemoteIdConfiguration : IEntityTypeConfiguration<VideoRemoteId>
{
    public void Configure(EntityTypeBuilder<VideoRemoteId> builder)
    {
        builder.ToTable("video_remote_ids");
        builder.HasKey(remoteId => remoteId.Id);
        builder.Property(remoteId => remoteId.Endpoint).IsRequired();
        builder.Property(remoteId => remoteId.RemoteId).IsRequired();
        builder.HasIndex(remoteId => remoteId.VideoId);
    }
}

public class VideoGalleryConfiguration : IEntityTypeConfiguration<VideoGallery>
{
    public void Configure(EntityTypeBuilder<VideoGallery> builder)
    {
        builder.ToTable("video_galleries");
        builder.HasKey(sg => new { sg.VideoId, sg.GalleryId });
        builder.HasOne(sg => sg.Video).WithMany(s => s.VideoGalleries).HasForeignKey(sg => sg.VideoId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(sg => sg.Gallery).WithMany(g => g.VideoGalleries).HasForeignKey(sg => sg.GalleryId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class GroupItemConfiguration : IEntityTypeConfiguration<GroupItem>
{
    public void Configure(EntityTypeBuilder<GroupItem> builder)
    {
        builder.ToTable("group_items");
        builder.HasKey(item => item.Id);
        builder.HasOne(item => item.Group).WithMany(group => group.GroupItems).HasForeignKey(item => item.GroupId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(item => item.HostType).IsRequired().HasMaxLength(50).HasDefaultValue("video");
        builder.HasOne(item => item.Video).WithMany(video => video.GroupItems).HasForeignKey(item => item.VideoId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(item => item.Image).WithMany().HasForeignKey(item => item.ImageId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(item => item.ChildGroup).WithMany().HasForeignKey(item => item.ChildGroupId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(item => new { item.GroupId, item.OrderIndex });
        builder.HasIndex(item => new { item.HostType, item.HostId });
        builder.HasIndex(item => new { item.Kind, item.HostId });
        builder.HasIndex(item => item.VideoId);
        builder.HasIndex(item => item.ImageId);
        builder.HasIndex(item => item.ChildGroupId);
        builder.HasIndex(item => item.SourceProfileId);
    }
}

public class AudioConfiguration : IEntityTypeConfiguration<Audio>
{
    public void Configure(EntityTypeBuilder<Audio> builder)
    {
        builder.ToTable("audios");
        builder.HasKey(audio => audio.Id);
        builder.HasOne(audio => audio.Studio).WithMany().HasForeignKey(audio => audio.StudioId).OnDelete(DeleteBehavior.SetNull);
        builder.HasMany(audio => audio.Urls).WithOne(url => url.Audio).HasForeignKey(url => url.AudioId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(audio => audio.Files).WithOne(file => file.Audio).HasForeignKey(file => file.AudioId).OnDelete(DeleteBehavior.SetNull);
        builder.HasMany(audio => audio.Tracks).WithOne(track => track.Audio).HasForeignKey(track => track.AudioId).OnDelete(DeleteBehavior.Cascade);

        builder.Property(audio => audio.TagIds).HasColumnType("integer[]");
        builder.Property(audio => audio.PerformerIds).HasColumnType("integer[]");
        builder.Property(audio => audio.FileCount).HasDefaultValue(0);
        builder.Property(audio => audio.MaxDuration).HasDefaultValue(0d);
        builder.Property(audio => audio.MaxBitRate).HasDefaultValue(0L);
        builder.Property(audio => audio.MaxFileSize).HasDefaultValue(0L);
        builder.Property(audio => audio.HasVideoFiles).HasDefaultValue(false);

        builder.HasIndex(audio => audio.Title);
        builder.HasIndex(audio => audio.StudioId);
        builder.HasIndex(audio => audio.Date);
        builder.HasIndex(audio => audio.CreatedAt);
        builder.HasIndex(audio => audio.UpdatedAt);
        builder.HasIndex(audio => audio.MaxDuration);
        builder.HasIndex(audio => audio.TagIds).HasMethod("gin");
        builder.HasIndex(audio => audio.PerformerIds).HasMethod("gin");
    }
}

public class AudioUrlConfiguration : IEntityTypeConfiguration<AudioUrl>
{
    public void Configure(EntityTypeBuilder<AudioUrl> builder)
    {
        builder.ToTable("audio_urls");
        builder.HasKey(url => url.Id);
        builder.Property(url => url.Url).IsRequired();
        builder.HasIndex(url => url.AudioId);
    }
}

public class AudioTrackConfiguration : IEntityTypeConfiguration<AudioTrack>
{
    public void Configure(EntityTypeBuilder<AudioTrack> builder)
    {
        builder.ToTable("audio_tracks");
        builder.HasKey(track => track.Id);
        builder.HasIndex(track => new { track.AudioId, track.OrderIndex });
    }
}

public class AudioTagConfiguration : IEntityTypeConfiguration<AudioTag>
{
    public void Configure(EntityTypeBuilder<AudioTag> builder)
    {
        builder.ToTable("audio_tags");
        builder.HasKey(link => new { link.AudioId, link.TagId });
        builder.HasOne(link => link.Audio).WithMany(audio => audio.AudioTags).HasForeignKey(link => link.AudioId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(link => link.Tag).WithMany().HasForeignKey(link => link.TagId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(link => link.TagId);
    }
}

public class AudioPerformerConfiguration : IEntityTypeConfiguration<AudioPerformer>
{
    public void Configure(EntityTypeBuilder<AudioPerformer> builder)
    {
        builder.ToTable("audio_performers");
        builder.HasKey(link => new { link.AudioId, link.PerformerId });
        builder.HasOne(link => link.Audio).WithMany(audio => audio.AudioPerformers).HasForeignKey(link => link.AudioId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(link => link.Performer).WithMany(performer => performer.AudioPerformers).HasForeignKey(link => link.PerformerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(link => link.PerformerId);
    }
}

public class TextDocumentConfiguration : IEntityTypeConfiguration<TextDocument>
{
    public void Configure(EntityTypeBuilder<TextDocument> builder)
    {
        builder.ToTable("text_documents");
        builder.HasKey(text => text.Id);
        builder.HasOne(text => text.Studio).WithMany().HasForeignKey(text => text.StudioId).OnDelete(DeleteBehavior.SetNull);
        builder.HasMany(text => text.Urls).WithOne(url => url.TextDocument).HasForeignKey(url => url.TextDocumentId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(text => text.Files).WithOne(file => file.TextDocument).HasForeignKey(file => file.TextDocumentId).OnDelete(DeleteBehavior.SetNull);

        builder.Property(text => text.TagIds).HasColumnType("integer[]");
        builder.Property(text => text.PerformerIds).HasColumnType("integer[]");
        builder.Property(text => text.FileCount).HasDefaultValue(0);
        builder.Property(text => text.MaxFileSize).HasDefaultValue(0L);

        builder.HasIndex(text => text.Title);
        builder.HasIndex(text => text.StudioId);
        builder.HasIndex(text => text.Date);
        builder.HasIndex(text => text.CreatedAt);
        builder.HasIndex(text => text.UpdatedAt);
        builder.HasIndex(text => text.TagIds).HasMethod("gin");
        builder.HasIndex(text => text.PerformerIds).HasMethod("gin");
    }
}

public class TextUrlConfiguration : IEntityTypeConfiguration<TextUrl>
{
    public void Configure(EntityTypeBuilder<TextUrl> builder)
    {
        builder.ToTable("text_urls");
        builder.HasKey(url => url.Id);
        builder.Property(url => url.Url).IsRequired();
        builder.HasIndex(url => url.TextDocumentId);
    }
}

public class TextTagConfiguration : IEntityTypeConfiguration<TextTag>
{
    public void Configure(EntityTypeBuilder<TextTag> builder)
    {
        builder.ToTable("text_tags");
        builder.HasKey(link => new { link.TextDocumentId, link.TagId });
        builder.HasOne(link => link.TextDocument).WithMany(text => text.TextTags).HasForeignKey(link => link.TextDocumentId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(link => link.Tag).WithMany().HasForeignKey(link => link.TagId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(link => link.TagId);
    }
}

public class TextPerformerConfiguration : IEntityTypeConfiguration<TextPerformer>
{
    public void Configure(EntityTypeBuilder<TextPerformer> builder)
    {
        builder.ToTable("text_performers");
        builder.HasKey(link => new { link.TextDocumentId, link.PerformerId });
        builder.HasOne(link => link.TextDocument).WithMany(text => text.TextPerformers).HasForeignKey(link => link.TextDocumentId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(link => link.Performer).WithMany(performer => performer.TextPerformers).HasForeignKey(link => link.PerformerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(link => link.PerformerId);
    }
}

public class CustomFieldDefinitionConfiguration : IEntityTypeConfiguration<CustomFieldDefinition>
{
    public void Configure(EntityTypeBuilder<CustomFieldDefinition> builder)
    {
        builder.ToTable("custom_field_definitions");
        builder.HasKey(definition => definition.Id);
        builder.Property(definition => definition.Key).IsRequired().HasMaxLength(100);
        builder.Property(definition => definition.Label).IsRequired().HasMaxLength(200);
        builder.Property(definition => definition.Type).IsRequired().HasMaxLength(50);
        builder.Property(definition => definition.EntityTypes).HasColumnType("text[]");
        builder.Property(definition => definition.Options).HasColumnType("text[]");

        builder.HasMany(definition => definition.Values)
            .WithOne(value => value.Definition)
            .HasForeignKey(value => value.DefinitionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(definition => definition.JsonPaths)
            .WithOne(path => path.Definition)
            .HasForeignKey(path => path.DefinitionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(definition => definition.Key).IsUnique();
        builder.HasIndex(definition => definition.DisplayOrder);
    }
}

public class CustomFieldJsonPathDefinitionConfiguration : IEntityTypeConfiguration<CustomFieldJsonPathDefinition>
{
    public void Configure(EntityTypeBuilder<CustomFieldJsonPathDefinition> builder)
    {
        builder.ToTable("custom_field_json_paths");
        builder.HasKey(path => path.Id);
        builder.Property(path => path.Path).IsRequired().HasMaxLength(500);
        builder.Property(path => path.Label).IsRequired().HasMaxLength(200);
        builder.Property(path => path.Type).IsRequired().HasMaxLength(50);
        builder.HasIndex(path => new { path.DefinitionId, path.Path }).IsUnique();
        builder.HasIndex(path => new { path.DefinitionId, path.DisplayOrder });
    }
}

public class CustomFieldValueConfiguration : IEntityTypeConfiguration<CustomFieldValue>
{
    public void Configure(EntityTypeBuilder<CustomFieldValue> builder)
    {
        builder.ToTable("custom_field_values");
        builder.HasKey(value => value.Id);
        builder.Property(value => value.EntityType).IsRequired().HasMaxLength(50);
        builder.Property(value => value.TextValue).HasMaxLength(4000);
        builder.Property(value => value.LongTextValue).HasColumnType("text");
        // Keep structured JSON separate from both bounded, indexed text and unbounded long text.
        builder.Property(value => value.JsonValue).HasColumnType("jsonb");
        builder.Property(value => value.NumberValue).HasPrecision(18, 6);

        builder.HasIndex(value => new { value.DefinitionId, value.EntityType, value.EntityId, value.Position }).IsUnique();
        builder.HasIndex(value => new { value.EntityType, value.EntityId });
        builder.HasIndex(value => new { value.DefinitionId, value.EntityType, value.TextValue });
        builder.HasIndex(value => new { value.DefinitionId, value.EntityType, value.NumberValue });
        builder.HasIndex(value => new { value.DefinitionId, value.EntityType, value.BoolValue });
        builder.HasIndex(value => new { value.DefinitionId, value.EntityType, value.DateValue });
        builder.HasIndex(value => new { value.DefinitionId, value.EntityType, value.TimestampValue });
        builder.HasIndex(value => new { value.DefinitionId, value.EntityType, value.IntegerValue });
    }
}

public class PerformerConfiguration : IEntityTypeConfiguration<Performer>
{
    public void Configure(EntityTypeBuilder<Performer> builder)
    {
        builder.ToTable("performers");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Name).IsRequired().HasMaxLength(500);
        builder.Property(p => p.IdentityKey).IsRequired();
        builder.Property(p => p.VideoCount).HasDefaultValue(0);
        builder.Property(p => p.ImageCount).HasDefaultValue(0);
        builder.Property(p => p.GalleryCount).HasDefaultValue(0);
        builder.Property(p => p.TagCount).HasDefaultValue(0);

        builder.HasMany(p => p.Urls).WithOne(u => u.Performer).HasForeignKey(u => u.PerformerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(p => p.Aliases).WithOne(a => a.Performer).HasForeignKey(a => a.PerformerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(p => p.RemoteIds).WithOne(si => si.Performer).HasForeignKey(si => si.PerformerId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(p => p.Name);
        builder.HasIndex(p => p.Favorite);
        builder.HasIndex(p => p.VideoCount);
        builder.HasIndex(p => p.ImageCount);
        builder.HasIndex(p => p.GalleryCount);
        builder.HasIndex(p => p.TagCount);
    }
}

public class PerformerTagConfiguration : IEntityTypeConfiguration<PerformerTag>
{
    public void Configure(EntityTypeBuilder<PerformerTag> builder)
    {
        builder.ToTable("performer_tags");
        builder.HasKey(pt => new { pt.PerformerId, pt.TagId });
        builder.HasOne(pt => pt.Performer).WithMany(p => p.PerformerTags).HasForeignKey(pt => pt.PerformerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(pt => pt.Tag).WithMany(t => t.PerformerTags).HasForeignKey(pt => pt.TagId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(pt => pt.TagId);
    }
}

public class PerformerRemoteIdConfiguration : IEntityTypeConfiguration<PerformerRemoteId>
{
    public void Configure(EntityTypeBuilder<PerformerRemoteId> builder)
    {
        builder.ToTable("performer_remote_ids");
        builder.HasKey(remoteId => remoteId.Id);
        builder.Property(remoteId => remoteId.Endpoint).IsRequired();
        builder.Property(remoteId => remoteId.RemoteId).IsRequired();
        builder.HasIndex(remoteId => remoteId.PerformerId);
    }
}

public class TagConfiguration : IEntityTypeConfiguration<Tag>
{
    public void Configure(EntityTypeBuilder<Tag> builder)
    {
        builder.ToTable("tags");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Name).IsRequired().HasMaxLength(500);
        builder.Property(t => t.NamespaceKey).IsRequired();
        builder.Property(t => t.Color).HasMaxLength(9);
        builder.Property(t => t.VideoCount).HasDefaultValue(0);
        builder.Property(t => t.SegmentCount).HasDefaultValue(0);
        builder.Property(t => t.ImageCount).HasDefaultValue(0);
        builder.Property(t => t.GalleryCount).HasDefaultValue(0);
        builder.Property(t => t.GroupCount).HasDefaultValue(0);
        builder.Property(t => t.PerformerCount).HasDefaultValue(0);
        builder.Property(t => t.StudioCount).HasDefaultValue(0);

        builder.HasMany(t => t.Aliases).WithOne(a => a.Tag).HasForeignKey(a => a.TagId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(t => t.RemoteIds).WithOne(si => si.Tag).HasForeignKey(si => si.TagId).OnDelete(DeleteBehavior.Cascade);
    builder.HasOne(t => t.TagGroup).WithMany(group => group.Tags).HasForeignKey(t => t.TagGroupId).OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(t => t.Name).IsUnique();
    builder.HasIndex(t => t.TagGroupId);
        builder.HasIndex(t => t.Favorite);
        builder.HasIndex(t => t.Organized);
        builder.HasIndex(t => t.VideoCount);
        builder.HasIndex(t => t.SegmentCount);
        builder.HasIndex(t => t.ImageCount);
        builder.HasIndex(t => t.GalleryCount);
        builder.HasIndex(t => t.GroupCount);
        builder.HasIndex(t => t.PerformerCount);
        builder.HasIndex(t => t.StudioCount);
    }
}

public class TagParentConfiguration : IEntityTypeConfiguration<TagParent>
{
    public void Configure(EntityTypeBuilder<TagParent> builder)
    {
        builder.ToTable("tag_parents");
        builder.HasKey(tp => new { tp.ParentId, tp.ChildId });
        builder.HasOne(tp => tp.Parent).WithMany(t => t.ChildRelations).HasForeignKey(tp => tp.ParentId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(tp => tp.Child).WithMany(t => t.ParentRelations).HasForeignKey(tp => tp.ChildId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class TagRemoteIdConfiguration : IEntityTypeConfiguration<TagRemoteId>
{
    public void Configure(EntityTypeBuilder<TagRemoteId> builder)
    {
        builder.ToTable("tag_remote_ids");
        builder.HasKey(remoteId => remoteId.Id);
        builder.Property(remoteId => remoteId.Endpoint).IsRequired();
        builder.Property(remoteId => remoteId.RemoteId).IsRequired();
        builder.HasIndex(remoteId => remoteId.TagId);
    }
}

public class StudioConfiguration : IEntityTypeConfiguration<Studio>
{
    public void Configure(EntityTypeBuilder<Studio> builder)
    {
        builder.ToTable("studios");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Name).IsRequired().HasMaxLength(500);
        builder.Property(s => s.NameKey).IsRequired();
        builder.Property(s => s.VideoCount).HasDefaultValue(0);
        builder.Property(s => s.ImageCount).HasDefaultValue(0);
        builder.Property(s => s.GalleryCount).HasDefaultValue(0);
        builder.Property(s => s.GroupCount).HasDefaultValue(0);
        builder.Property(s => s.PerformerCount).HasDefaultValue(0);
        builder.Property(s => s.ChildStudioCount).HasDefaultValue(0);
        builder.Property(s => s.TagCount).HasDefaultValue(0);

        builder.HasOne(s => s.Parent).WithMany(s => s.Children).HasForeignKey(s => s.ParentId).OnDelete(DeleteBehavior.SetNull);
        builder.HasMany(s => s.Urls).WithOne(u => u.Studio).HasForeignKey(u => u.StudioId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(s => s.Aliases).WithOne(a => a.Studio).HasForeignKey(a => a.StudioId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(s => s.RemoteIds).WithOne(si => si.Studio).HasForeignKey(si => si.StudioId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(s => s.Name);
        builder.HasIndex(s => s.ParentId);
        builder.HasIndex(s => s.Favorite);
        builder.HasIndex(s => s.Organized);
        builder.HasIndex(s => s.VideoCount);
        builder.HasIndex(s => s.ImageCount);
        builder.HasIndex(s => s.GalleryCount);
        builder.HasIndex(s => s.GroupCount);
        builder.HasIndex(s => s.PerformerCount);
        builder.HasIndex(s => s.ChildStudioCount);
        builder.HasIndex(s => s.TagCount);
    }
}

public class StudioTagConfiguration : IEntityTypeConfiguration<StudioTag>
{
    public void Configure(EntityTypeBuilder<StudioTag> builder)
    {
        builder.ToTable("studio_tags");
        builder.HasKey(st => new { st.StudioId, st.TagId });
        builder.HasOne(st => st.Studio).WithMany(s => s.StudioTags).HasForeignKey(st => st.StudioId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(st => st.Tag).WithMany(t => t.StudioTags).HasForeignKey(st => st.TagId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class StudioRemoteIdConfiguration : IEntityTypeConfiguration<StudioRemoteId>
{
    public void Configure(EntityTypeBuilder<StudioRemoteId> builder)
    {
        builder.ToTable("studio_remote_ids");
        builder.HasKey(remoteId => remoteId.Id);
        builder.Property(remoteId => remoteId.Endpoint).IsRequired();
        builder.Property(remoteId => remoteId.RemoteId).IsRequired();
        builder.HasIndex(remoteId => remoteId.StudioId);
    }
}

public class GalleryConfiguration : IEntityTypeConfiguration<Gallery>
{
    public void Configure(EntityTypeBuilder<Gallery> builder)
    {
        builder.ToTable("galleries");
        builder.HasKey(g => g.Id);
        builder.Property(g => g.ImageCount).HasDefaultValue(0);
        builder.Property(g => g.VideoCount).HasDefaultValue(0);
        builder.Property(g => g.PerformerCount).HasDefaultValue(0);
        builder.Property(g => g.TagCount).HasDefaultValue(0);

        builder.HasOne(g => g.Studio).WithMany(s => s.Galleries).HasForeignKey(g => g.StudioId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne(g => g.Folder).WithMany().HasForeignKey(g => g.FolderId).OnDelete(DeleteBehavior.SetNull);
        builder.HasMany(g => g.Urls).WithOne(u => u.Gallery).HasForeignKey(u => u.GalleryId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(g => g.Files).WithOne(f => f.Gallery).HasForeignKey(f => f.GalleryId).OnDelete(DeleteBehavior.SetNull);
        builder.HasMany(g => g.Chapters).WithOne(c => c.Gallery).HasForeignKey(c => c.GalleryId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(g => g.Title);
        builder.HasIndex(g => g.StudioId);
        builder.HasIndex(g => g.Date);
        builder.HasIndex(g => g.Organized);
        builder.HasIndex(g => g.CreatedAt);
        builder.HasIndex(g => g.UpdatedAt);
        builder.HasIndex(g => g.ImageCount);
        builder.HasIndex(g => g.VideoCount);
        builder.HasIndex(g => g.PerformerCount);
        builder.HasIndex(g => g.TagCount);

        builder.Property(g => g.TagIds).HasColumnType("integer[]");
        builder.Property(g => g.PerformerIds).HasColumnType("integer[]");
        builder.HasIndex(g => g.TagIds).HasMethod("gin");
        builder.HasIndex(g => g.PerformerIds).HasMethod("gin");
    }
}

public class GalleryTagConfiguration : IEntityTypeConfiguration<GalleryTag>
{
    public void Configure(EntityTypeBuilder<GalleryTag> builder)
    {
        builder.ToTable("gallery_tags");
        builder.HasKey(gt => new { gt.GalleryId, gt.TagId });
        builder.HasOne(gt => gt.Gallery).WithMany(g => g.GalleryTags).HasForeignKey(gt => gt.GalleryId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(gt => gt.Tag).WithMany(t => t.GalleryTags).HasForeignKey(gt => gt.TagId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(gt => gt.TagId);
    }
}

public class GalleryPerformerConfiguration : IEntityTypeConfiguration<GalleryPerformer>
{
    public void Configure(EntityTypeBuilder<GalleryPerformer> builder)
    {
        builder.ToTable("gallery_performers");
        builder.HasKey(gp => new { gp.GalleryId, gp.PerformerId });
        builder.HasOne(gp => gp.Gallery).WithMany(g => g.GalleryPerformers).HasForeignKey(gp => gp.GalleryId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(gp => gp.Performer).WithMany(p => p.GalleryPerformers).HasForeignKey(gp => gp.PerformerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(gp => gp.PerformerId);
    }
}

public class ImageConfiguration : IEntityTypeConfiguration<Image>
{
    public void Configure(EntityTypeBuilder<Image> builder)
    {
        builder.ToTable("images");
        builder.HasKey(i => i.Id);

        builder.HasOne(i => i.Studio).WithMany(s => s.Images).HasForeignKey(i => i.StudioId).OnDelete(DeleteBehavior.SetNull);
        builder.HasMany(i => i.Urls).WithOne(u => u.Image).HasForeignKey(u => u.ImageId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(i => i.Files).WithOne(f => f.Image).HasForeignKey(f => f.ImageId).OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(i => i.Title);
        builder.HasIndex(i => i.StudioId);
        builder.HasIndex(i => i.Organized);
        builder.HasIndex(i => i.CreatedAt);
        builder.HasIndex(i => i.UpdatedAt);
        builder.Property(i => i.TagCount).HasDefaultValue(0);
        builder.Property(i => i.PerformerCount).HasDefaultValue(0);
        builder.Property(i => i.GalleryCount).HasDefaultValue(0);
        builder.Property(i => i.FileCount).HasDefaultValue(0);
        builder.Property(i => i.MaxResolution).HasDefaultValue(0);
        builder.Property(i => i.MaxFileSize).HasDefaultValue(0L);
        builder.Property(i => i.HasDimensionData).HasDefaultValue(false);
        builder.Property(i => i.HasLandscapeFiles).HasDefaultValue(false);
        builder.Property(i => i.HasPortraitFiles).HasDefaultValue(false);
        builder.Property(i => i.HasSquareFiles).HasDefaultValue(false);
        builder.HasIndex(i => i.TagCount);
        builder.HasIndex(i => i.PerformerCount);
        builder.HasIndex(i => i.GalleryCount);
        builder.HasIndex(i => i.FileCount);
        builder.HasIndex(i => i.MaxResolution);
        builder.HasIndex(i => i.MaxFileSize);
        builder.HasIndex(i => i.MaxFileModTime);
        builder.HasIndex(i => i.MinPath);
        builder.HasIndex(i => i.MaxPath);
        builder.HasIndex(i => i.HasDimensionData);
        builder.HasIndex(i => i.HasLandscapeFiles);
        builder.HasIndex(i => i.HasPortraitFiles);
        builder.HasIndex(i => i.HasSquareFiles);

        builder.Property(i => i.TagIds).HasColumnType("integer[]");
        builder.Property(i => i.PerformerIds).HasColumnType("integer[]");
        builder.HasIndex(i => i.TagIds).HasMethod("gin");
        builder.HasIndex(i => i.PerformerIds).HasMethod("gin");
    }
}

public class ImageTagConfiguration : IEntityTypeConfiguration<ImageTag>
{
    public void Configure(EntityTypeBuilder<ImageTag> builder)
    {
        builder.ToTable("image_tags");
        builder.HasKey(it => new { it.ImageId, it.TagId });
        builder.HasOne(it => it.Image).WithMany(i => i.ImageTags).HasForeignKey(it => it.ImageId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(it => it.Tag).WithMany(t => t.ImageTags).HasForeignKey(it => it.TagId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(it => it.TagId);
    }
}

public class ImagePerformerConfiguration : IEntityTypeConfiguration<ImagePerformer>
{
    public void Configure(EntityTypeBuilder<ImagePerformer> builder)
    {
        builder.ToTable("image_performers");
        builder.HasKey(ip => new { ip.ImageId, ip.PerformerId });
        builder.HasOne(ip => ip.Image).WithMany(i => i.ImagePerformers).HasForeignKey(ip => ip.ImageId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(ip => ip.Performer).WithMany(p => p.ImagePerformers).HasForeignKey(ip => ip.PerformerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(ip => ip.PerformerId);
    }
}

public class ImageGalleryConfiguration : IEntityTypeConfiguration<ImageGallery>
{
    public void Configure(EntityTypeBuilder<ImageGallery> builder)
    {
        builder.ToTable("image_galleries");
        builder.HasKey(ig => new { ig.ImageId, ig.GalleryId });
        builder.HasOne(ig => ig.Image).WithMany(i => i.ImageGalleries).HasForeignKey(ig => ig.ImageId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(ig => ig.Gallery).WithMany(g => g.ImageGalleries).HasForeignKey(ig => ig.GalleryId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(ig => ig.GalleryId);
    }
}

public class GroupConfiguration : IEntityTypeConfiguration<Group>
{
    public void Configure(EntityTypeBuilder<Group> builder)
    {
        builder.ToTable("groups");
        builder.HasKey(g => g.Id);
        builder.Property(g => g.Name).IsRequired().HasMaxLength(500);

        builder.HasOne(g => g.Studio).WithMany(s => s.Groups).HasForeignKey(g => g.StudioId).OnDelete(DeleteBehavior.SetNull);
        builder.HasMany(g => g.Urls).WithOne(u => u.Group).HasForeignKey(u => u.GroupId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(g => g.SortOrder).HasDefaultValue(0);

        builder.HasIndex(g => g.Name);
        builder.HasIndex(g => g.StudioId);
        builder.HasIndex(g => g.SortOrder);
    }
}

public class GroupTagConfiguration : IEntityTypeConfiguration<GroupTag>
{
    public void Configure(EntityTypeBuilder<GroupTag> builder)
    {
        builder.ToTable("group_tags");
        builder.HasKey(gt => new { gt.GroupId, gt.TagId });
        builder.HasOne(gt => gt.Group).WithMany(g => g.GroupTags).HasForeignKey(gt => gt.GroupId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(gt => gt.Tag).WithMany(t => t.GroupTags).HasForeignKey(gt => gt.TagId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class GroupRelationConfiguration : IEntityTypeConfiguration<GroupRelation>
{
    public void Configure(EntityTypeBuilder<GroupRelation> builder)
    {
        builder.ToTable("group_relations");
        builder.HasKey(gr => new { gr.ContainingGroupId, gr.SubGroupId });
        builder.HasOne(gr => gr.ContainingGroup).WithMany(g => g.SubGroupRelations).HasForeignKey(gr => gr.ContainingGroupId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(gr => gr.SubGroup).WithMany(g => g.ContainingGroupRelations).HasForeignKey(gr => gr.SubGroupId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class TagApplicationConfiguration : IEntityTypeConfiguration<TagApplication>
{
    public void Configure(EntityTypeBuilder<TagApplication> builder)
    {
        builder.ToTable("tag_applications");
        builder.HasKey(application => application.Id);
        builder.Property(application => application.ContextType).HasMaxLength(16);
        builder.Property(application => application.SourceKey).IsRequired();
        builder.Property(application => application.SourceRunId).IsRequired().HasDefaultValue(string.Empty);
        builder.Property(application => application.ModelKey).IsRequired().HasDefaultValue(string.Empty);
        builder.HasOne(application => application.Tag).WithMany().HasForeignKey(application => application.TagId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(application => new { application.HostType, application.HostId });
        builder.HasIndex(application => new { application.ContextType, application.ContextId });
        builder.HasIndex(application => new { application.HostType, application.HostId, application.ContextType, application.ContextId });
        builder.HasIndex(application => application.TagId);
        builder.HasIndex(application => application.SourceKey);
        builder.HasIndex(application => new { application.HostType, application.HostId, application.TagId, application.SourceKey, application.SourceRunId, application.ModelKey })
            .IsUnique()
            .HasFilter("\"ContextType\" IS NULL AND \"ContextId\" IS NULL");
        builder.HasIndex(application => new { application.HostType, application.HostId, application.ContextType, application.ContextId, application.TagId, application.SourceKey, application.SourceRunId, application.ModelKey })
            .IsUnique()
            .HasFilter("\"ContextType\" IS NOT NULL AND \"ContextId\" IS NOT NULL");
    }
}

public class FieldProvenanceConfiguration : IEntityTypeConfiguration<FieldProvenance>
{
    public void Configure(EntityTypeBuilder<FieldProvenance> builder)
    {
        builder.ToTable("field_provenance");
        builder.HasKey(provenance => provenance.Id);
        builder.Property(provenance => provenance.FieldKey).IsRequired().HasMaxLength(100);
        builder.Property(provenance => provenance.ValueJson).HasColumnType("jsonb");
        builder.Property(provenance => provenance.SourceKey).IsRequired();
        builder.Property(provenance => provenance.SourceRunId).IsRequired().HasDefaultValue(string.Empty);
        builder.Property(provenance => provenance.ModelKey).IsRequired().HasDefaultValue(string.Empty);

        builder.HasIndex(provenance => new { provenance.HostType, provenance.HostId });
        builder.HasIndex(provenance => new { provenance.HostType, provenance.HostId, provenance.FieldKey });
        builder.HasIndex(provenance => provenance.SourceKey);
        builder.HasIndex(provenance => new { provenance.HostType, provenance.HostId, provenance.FieldKey, provenance.SourceKey, provenance.SourceRunId, provenance.ModelKey })
            .IsUnique();
    }
}

public class TagGroupConfiguration : IEntityTypeConfiguration<TagGroup>
{
    public void Configure(EntityTypeBuilder<TagGroup> builder)
    {
        builder.ToTable("tag_groups");
        builder.HasKey(group => group.Id);
        builder.Property(group => group.Name).IsRequired().HasMaxLength(500);
        builder.Property(group => group.Color).HasMaxLength(9);
        builder.HasIndex(group => group.Name).IsUnique();
        builder.HasIndex(group => group.SortOrder);
    }
}

public class SegmentConfiguration : IEntityTypeConfiguration<Segment>
{
    public void Configure(EntityTypeBuilder<Segment> builder)
    {
        builder.ToTable("segments");
        builder.HasKey(segment => segment.Id);
        builder.Property(segment => segment.Payload).HasColumnType("jsonb");
        builder.Property(segment => segment.SourceKey).IsRequired();
        builder.HasOne(segment => segment.Tag).WithMany().HasForeignKey(segment => segment.TagId).OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(segment => new { segment.HostType, segment.HostId, segment.StartSec });
        builder.HasIndex(segment => segment.TagId);
        builder.HasIndex(segment => segment.SourceKey);
        builder.HasIndex(segment => segment.SourceRunId);
        builder.HasIndex(segment => segment.Kind);
        builder.HasIndex(segment => new { segment.Kind, segment.RefId });
    }
}

public class SegmentDisplayRuleConfiguration : IEntityTypeConfiguration<SegmentDisplayRule>
{
    public void Configure(EntityTypeBuilder<SegmentDisplayRule> builder)
    {
        builder.ToTable("segment_display_rules");
        builder.HasKey(rule => rule.Id);
        builder.HasOne(rule => rule.Profile).WithMany(profile => profile.Rules).HasForeignKey(rule => rule.ProfileId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(rule => rule.Tag).WithMany().HasForeignKey(rule => rule.TagId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<User>().WithMany().HasForeignKey(rule => rule.UserId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(rule => rule.ProfileId);
        builder.HasIndex(rule => rule.UserId);
        builder.HasIndex(rule => new { rule.ProfileId, rule.SourceKey, rule.Kind, rule.TagId, rule.TagCategory, rule.HostType, rule.Priority });
    }
}

public class SegmentDisplayProfileConfiguration : IEntityTypeConfiguration<SegmentDisplayProfile>
{
    public void Configure(EntityTypeBuilder<SegmentDisplayProfile> builder)
    {
        builder.ToTable("segment_display_profiles");
        builder.HasKey(profile => profile.Id);
        builder.Property(profile => profile.Name).IsRequired().HasMaxLength(200);
        builder.Property(profile => profile.Description).HasMaxLength(1000);
        builder.Property(profile => profile.Version).HasDefaultValue(1);
        builder.HasOne<User>().WithMany().HasForeignKey(profile => profile.UserId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(profile => profile.UserId);
        builder.HasIndex(profile => new { profile.UserId, profile.IsDefault });
        builder.HasIndex(profile => new { profile.IsSystem, profile.Name });
    }
}

public class FaceConfiguration : IEntityTypeConfiguration<Face>
{
    public void Configure(EntityTypeBuilder<Face> builder)
    {
        builder.ToTable("faces");
        builder.HasKey(face => face.Id);
        builder.Property(face => face.Label).HasMaxLength(500);
        builder.Property(face => face.PrimarySourceKey).HasMaxLength(200);
        builder.Property(face => face.DetectionCount).HasDefaultValue(0);
        builder.Property(face => face.AppearanceCount).HasDefaultValue(0);
        builder.Property(face => face.FrameSampleCount).HasDefaultValue(0);
        builder.Property(face => face.VideoCount).HasDefaultValue(0);
        builder.Property(face => face.ImageCount).HasDefaultValue(0);

        builder.HasOne(face => face.Performer)
            .WithMany()
            .HasForeignKey(face => face.PerformerId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(face => face.MergedIntoFace)
            .WithMany(face => face.MergedFaces)
            .HasForeignKey(face => face.MergedIntoFaceId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasMany(face => face.Appearances)
            .WithOne(appearance => appearance.Face)
            .HasForeignKey(appearance => appearance.FaceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(face => face.PerformerId);
        builder.HasIndex(face => face.MergedIntoFaceId);
        builder.HasIndex(face => face.Ignored);
        builder.HasIndex(face => face.PrimarySourceKey);
        builder.HasIndex(face => face.Label);
    }
}

public class FaceAppearanceConfiguration : IEntityTypeConfiguration<FaceAppearance>
{
    public void Configure(EntityTypeBuilder<FaceAppearance> builder)
    {
        builder.ToTable("face_appearances");
        builder.HasKey(appearance => appearance.Id);
        builder.Property(appearance => appearance.SourceKey).IsRequired().HasMaxLength(200);
        builder.Property(appearance => appearance.SourceRunId).HasMaxLength(200);
        builder.Property(appearance => appearance.GroupKey).HasMaxLength(200);
        builder.Property(appearance => appearance.Payload).HasColumnType("jsonb");
        builder.Property(appearance => appearance.SampleCount).HasDefaultValue(0);
        builder.Property(appearance => appearance.RetainedSpatialSampleCount).HasDefaultValue(0);
        builder.Property(appearance => appearance.SegmentCount).HasDefaultValue(0);

        builder.HasOne(appearance => appearance.Face)
            .WithMany(face => face.Appearances)
            .HasForeignKey(appearance => appearance.FaceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(appearance => appearance.FaceId);
        builder.HasIndex(appearance => new { appearance.HostType, appearance.HostId });
        builder.HasIndex(appearance => new { appearance.FaceId, appearance.HostType, appearance.HostId });
        builder.HasIndex(appearance => appearance.GroupKey);
        builder.HasIndex(appearance => appearance.SourceKey);
        builder.HasIndex(appearance => appearance.SourceRunId);
    }
}

public class EmbeddingConfiguration : IEntityTypeConfiguration<Embedding>
{
    public void Configure(EntityTypeBuilder<Embedding> builder)
    {
        builder.ToTable("embeddings");
        builder.HasKey(embedding => embedding.Id);
        builder.Property(embedding => embedding.Vector);
        builder.Property(embedding => embedding.Kind).IsRequired().HasMaxLength(200);
        builder.Property(embedding => embedding.KindFamily).HasMaxLength(200);
        builder.Property(embedding => embedding.SourceKey).IsRequired().HasMaxLength(200);
        builder.Property(embedding => embedding.SourceRunId).HasMaxLength(200);
        builder.Property(embedding => embedding.Meta).HasColumnType("jsonb");

        builder.HasIndex(embedding => new { embedding.HostType, embedding.HostId });
        // Fetch-by-ids for scoring (GetVisualPairAsync etc.) filters HostType + HostId IN + Modality +
        // SectionIndex; this composite lets the planner seek straight to the asset-level rows instead of
        // reading every section/modality embedding for each host and filtering in the heap.
        builder.HasIndex(embedding => new { embedding.HostType, embedding.HostId, embedding.Modality, embedding.SectionIndex });
        builder.HasIndex(embedding => new { embedding.KindFamily, embedding.Modality });
        builder.HasIndex(embedding => new { embedding.Kind, embedding.Dim });
        builder.HasIndex(embedding => embedding.SourceKey);
        builder.HasIndex(embedding => embedding.SourceRunId);
    }
}


public class FaceSuggestionDecisionConfiguration : IEntityTypeConfiguration<FaceSuggestionDecision>
{
    public void Configure(EntityTypeBuilder<FaceSuggestionDecision> builder)
    {
        builder.ToTable("face_suggestion_decisions");
        builder.HasKey(decision => decision.Id);
        builder.Property(decision => decision.Decision).IsRequired().HasMaxLength(16);

        builder.HasOne(decision => decision.Face)
            .WithMany()
            .HasForeignKey(decision => decision.FaceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(decision => decision.Performer)
            .WithMany()
            .HasForeignKey(decision => decision.PerformerId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(decision => decision.UserId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(decision => new { decision.FaceId, decision.PerformerId, decision.UserId }).IsUnique();
        builder.HasIndex(decision => new { decision.FaceId, decision.UserId });
        builder.HasIndex(decision => new { decision.UserId, decision.Decision });
    }
}

public class AiRunConfiguration : IEntityTypeConfiguration<AiRun>
{
    public void Configure(EntityTypeBuilder<AiRun> builder)
    {
        builder.ToTable("ai_runs");
        builder.HasKey(run => run.Id);
        builder.Property(run => run.RunKey).IsRequired().HasMaxLength(200);
        builder.Property(run => run.SourceKey).IsRequired().HasMaxLength(200);
        builder.Property(run => run.Trigger).HasMaxLength(100);
        builder.Property(run => run.JobId).HasMaxLength(100);
        builder.Property(run => run.LoadPolicy).HasMaxLength(100);
        builder.Property(run => run.Error).HasMaxLength(4000);
        builder.Property(run => run.Request).HasColumnType("jsonb");
        builder.Property(run => run.Models).HasColumnType("jsonb");
        builder.Property(run => run.Summary).HasColumnType("jsonb");

        builder.HasIndex(run => run.RunKey).IsUnique();
        builder.HasIndex(run => new { run.TargetType, run.TargetId, run.CreatedAt });
        builder.HasIndex(run => run.Status);
        builder.HasIndex(run => run.SourceKey);
        builder.HasIndex(run => run.JobId);
    }
}

public class UserEntityAffinityConfiguration : IEntityTypeConfiguration<UserEntityAffinity>
{
    public void Configure(EntityTypeBuilder<UserEntityAffinity> builder)
    {
        builder.ToTable("user_entity_affinities");
        builder.HasKey(affinity => affinity.Id);
        builder.HasOne<User>().WithMany().HasForeignKey(affinity => affinity.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(affinity => new { affinity.UserId, affinity.HostType, affinity.HostId }).IsUnique();
        builder.HasIndex(affinity => new { affinity.UserId, affinity.IsFavorite });
        builder.HasIndex(affinity => new { affinity.UserId, affinity.LastConsumedAt });
        builder.Property(affinity => affinity.TotalConsumedSec).HasDefaultValue(0d);
        builder.Property(affinity => affinity.ViewCount).HasDefaultValue(0);
        builder.Property(affinity => affinity.CompleteCount).HasDefaultValue(0);
        builder.Property(affinity => affinity.LikeCount).HasDefaultValue(0);
        builder.Property(affinity => affinity.DerivedLikeCount).HasDefaultValue(0);
        builder.Property(affinity => affinity.PageVisitCount).HasDefaultValue(0);
        builder.Property(affinity => affinity.InteractionCount).HasDefaultValue(0);
        builder.Property(affinity => affinity.OpenDetailCount).HasDefaultValue(0);
        builder.Property(affinity => affinity.OpenLightboxCount).HasDefaultValue(0);
        builder.Property(affinity => affinity.NavigateCount).HasDefaultValue(0);
        builder.Property(affinity => affinity.PauseCount).HasDefaultValue(0);
        builder.Property(affinity => affinity.SeekCount).HasDefaultValue(0);
        builder.Property(affinity => affinity.PlayerControlCount).HasDefaultValue(0);
        builder.Property(affinity => affinity.SearchInteractionCount).HasDefaultValue(0);
        builder.Property(affinity => affinity.FilterInteractionCount).HasDefaultValue(0);
        builder.Property(affinity => affinity.ZoomCount).HasDefaultValue(0);
        builder.Property(affinity => affinity.IsBookmarked).HasDefaultValue(false);
        builder.Property(affinity => affinity.MaxDwellSec).HasDefaultValue(0d);
        builder.Property(affinity => affinity.MaxDwellStartSec).HasDefaultValue(0d);
    }
}

public class UserBookmarkConfiguration : IEntityTypeConfiguration<UserBookmark>
{
    public void Configure(EntityTypeBuilder<UserBookmark> builder)
    {
        builder.ToTable("user_bookmarks");
        builder.HasKey(bookmark => new { bookmark.UserId, bookmark.HostType, bookmark.HostId });
        builder.HasOne<User>().WithMany().HasForeignKey(bookmark => bookmark.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(bookmark => new { bookmark.UserId, bookmark.CreatedAt });
    }
}

public class InteractionConfiguration : IEntityTypeConfiguration<Interaction>
{
    public void Configure(EntityTypeBuilder<Interaction> builder)
    {
        builder.ToTable("interactions");
        builder.HasKey(interaction => interaction.Id);
        builder.HasOne<User>().WithMany().HasForeignKey(interaction => interaction.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(interaction => interaction.Meta).HasColumnType("jsonb");
        builder.HasIndex(interaction => new { interaction.UserId, interaction.HostType, interaction.HostId, interaction.At });
    }
}

public class RatingConfiguration : IEntityTypeConfiguration<Rating>
{
    public void Configure(EntityTypeBuilder<Rating> builder)
    {
        builder.ToTable("ratings");
        builder.HasKey(rating => rating.Id);
        builder.HasOne<User>().WithMany().HasForeignKey(rating => rating.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(rating => rating.Aspect).IsRequired().HasMaxLength(100);
        builder.Property(rating => rating.Value).HasAnnotation("Range", new[] { 0, 100 });
        builder.HasIndex(rating => new { rating.UserId, rating.HostType, rating.HostId, rating.Aspect }).IsUnique();
        builder.HasIndex(rating => new { rating.HostType, rating.HostId, rating.Aspect });
    }
}

public class DetectionConfiguration : IEntityTypeConfiguration<Detection>
{
    public void Configure(EntityTypeBuilder<Detection> builder)
    {
        builder.ToTable("detections");
        builder.HasKey(detection => detection.Id);
        builder.Property(detection => detection.Extra).HasColumnType("jsonb");
        builder.Property(detection => detection.Class).IsRequired();
        builder.Property(detection => detection.SourceKey).IsRequired();

        builder.HasIndex(detection => new { detection.HostType, detection.HostId, detection.ObservedAtSec });
        builder.HasIndex(detection => detection.SourceKey);
        builder.HasIndex(detection => detection.SourceRunId);
        builder.HasIndex(detection => detection.Class);
        builder.HasIndex(detection => new { detection.RefKind, detection.RefId });
        builder.HasIndex(detection => detection.GroupKey);
    }
}

public class SavedFilterConfiguration : IEntityTypeConfiguration<SavedFilter>
{
    public void Configure(EntityTypeBuilder<SavedFilter> builder)
    {
        builder.ToTable("saved_filters");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Mode).HasMaxLength(200);
        builder.Property(f => f.FindFilter).HasColumnType("jsonb");
        builder.Property(f => f.ObjectFilter).HasColumnType("jsonb");
        builder.Property(f => f.UIOptions).HasColumnType("jsonb");
        builder.HasOne(f => f.User).WithMany().HasForeignKey(f => f.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(f => new { f.UserId, f.Mode });
    }
}

public class GalleryChapterConfiguration : IEntityTypeConfiguration<GalleryChapter>
{
    public void Configure(EntityTypeBuilder<GalleryChapter> builder)
    {
        builder.ToTable("gallery_chapters");
        builder.HasKey(c => c.Id);
    }
}

public class ScrapeAttemptConfiguration : IEntityTypeConfiguration<ScrapeAttempt>
{
    public void Configure(EntityTypeBuilder<ScrapeAttempt> builder)
    {
        builder.ToTable("scrape_attempts");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.ScraperId).IsRequired().HasMaxLength(500);
        builder.Property(a => a.EntityType).IsRequired().HasMaxLength(100);
        builder.Property(a => a.InputKind).IsRequired().HasMaxLength(50);
        builder.Property(a => a.InputJson).IsRequired();
        builder.Property(a => a.CandidateResultsJson);
        builder.Property(a => a.Status).IsRequired().HasMaxLength(50);
        builder.Property(a => a.AppliedByUser).HasMaxLength(200);
        builder.HasIndex(a => a.CreatedAt);
        builder.HasIndex(a => a.Status);
        builder.HasIndex(a => new { a.EntityType, a.EntityId, a.CreatedAt });
    }
}

// User/Role/Permission configurations live in AuthConfigurations.cs (Cove.Data.Configuration).

public class FolderConfiguration : IEntityTypeConfiguration<Folder>
{
    public void Configure(EntityTypeBuilder<Folder> builder)
    {
        builder.ToTable("folders");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Path).IsRequired();
        builder.Property(f => f.ScanSignature).HasMaxLength(64);
        builder.HasOne(f => f.ParentFolder).WithMany(f => f.SubFolders).HasForeignKey(f => f.ParentFolderId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(f => f.Files).WithOne(file => file.ParentFolder).HasForeignKey(file => file.ParentFolderId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(f => f.Path).IsUnique();
    }
}

public class BaseFileEntityConfiguration : IEntityTypeConfiguration<BaseFileEntity>
{
    public void Configure(EntityTypeBuilder<BaseFileEntity> builder)
    {
        builder.ToTable("files");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Basename).IsRequired();
        builder.Property(f => f.Path).IsRequired();
        builder.HasIndex(f => new { f.ParentFolderId, f.Basename }).IsUnique();
        builder.HasIndex(f => f.Path);
    }
}

public class PendingPhysicalFileDeletionConfiguration : IEntityTypeConfiguration<PendingPhysicalFileDeletion>
{
    public void Configure(EntityTypeBuilder<PendingPhysicalFileDeletion> builder)
    {
        builder.ToTable("pending_physical_file_deletions");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Path).IsRequired();
        builder.Property(item => item.LastError).HasMaxLength(2_000);
        builder.HasIndex(item => new { item.BatchId, item.Id });
        builder.HasIndex(item => item.CreatedAt);
    }
}

public class VideoDeletionCommitMarkerConfiguration : IEntityTypeConfiguration<VideoDeletionCommitMarker>
{
    public void Configure(EntityTypeBuilder<VideoDeletionCommitMarker> builder)
    {
        builder.ToTable("video_deletion_commit_markers");
        builder.HasKey(item => new { item.BatchId, item.VideoId });
        builder.HasIndex(item => item.CreatedAt);
    }
}

public class VideoFileConfiguration : IEntityTypeConfiguration<VideoFile>
{
    public void Configure(EntityTypeBuilder<VideoFile> builder)
    {
        // Composite (VideoId, Path) lets MIN(Path) WHERE VideoId = ? be answered
        // by an Index Only Scan, which is what the video-list "path" sort relies on.
        builder.HasIndex(v => new { v.VideoId, v.Path })
            .HasFilter("\"VideoId\" IS NOT NULL");
    }
}

public class ImageFileConfiguration : IEntityTypeConfiguration<ImageFile>
{
    public void Configure(EntityTypeBuilder<ImageFile> builder)
    {
        builder.HasIndex(i => new { i.ImageId, i.Path })
            .HasFilter("\"ImageId\" IS NOT NULL");
        // The image-list "title" sort falls back to MIN/MAX(Basename) WHERE ImageId = ? (images have no
        // denormalized title). Without this, that correlated subquery scans files per row and the list times
        // out. (ImageId, Basename) lets it be answered by an index seek. PostgreSQL indexes nulls, so
        // explicitly exclude non-image files.
        builder.HasIndex(i => new { i.ImageId, i.Basename })
            .HasFilter("\"ImageId\" IS NOT NULL");
    }
}

public class GalleryFileConfiguration : IEntityTypeConfiguration<GalleryFile>
{
    public void Configure(EntityTypeBuilder<GalleryFile> builder)
    {
        builder.HasIndex(g => new { g.GalleryId, g.Path })
            .IncludeProperties(g => new { g.Size, g.ModTime })
            .HasFilter("\"GalleryId\" IS NOT NULL");
    }
}

public class AudioFileConfiguration : IEntityTypeConfiguration<AudioFile>
{
    public void Configure(EntityTypeBuilder<AudioFile> builder)
    {
        builder.HasIndex(file => new { file.AudioId, file.Path })
            .HasFilter("\"AudioId\" IS NOT NULL");
    }
}

public class TextFileConfiguration : IEntityTypeConfiguration<TextFile>
{
    public void Configure(EntityTypeBuilder<TextFile> builder)
    {
        builder.HasIndex(file => new { file.TextDocumentId, file.Path })
            .HasFilter("\"TextDocumentId\" IS NOT NULL");
    }
}

public class ExtensionDataConfiguration : IEntityTypeConfiguration<ExtensionData>
{
    public void Configure(EntityTypeBuilder<ExtensionData> builder)
    {
        builder.ToTable("extension_data");
        builder.HasKey(e => new { e.ExtensionId, e.Key });
        builder.Property(e => e.ExtensionId).HasMaxLength(256);
        builder.Property(e => e.Key).HasMaxLength(512);
        builder.Property(e => e.Value).IsRequired();
        builder.HasIndex(e => e.ExtensionId);
    }
}
