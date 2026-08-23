using Cove.Core.Auth;
using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using PermissionKeys = Cove.Core.Auth.Permissions;

namespace Cove.Data;

public partial class CoveContext
{
    private readonly ICurrentPrincipalAccessor? _principalAccessor;

    private CovePrincipal? CurrentPrincipal => _principalAccessor?.Current;

    internal CovePrincipal? CurrentPrincipalForReadOptimization => CurrentPrincipal;

    private bool AuthorizationFiltersBypassed =>
        _authorizationFilterSuppressionDepth > 0 ||
        CurrentPrincipal is null ||
        CurrentPrincipal.Kind == PrincipalKind.System ||
        CurrentPrincipal.Has("*");

    private bool EmbeddingReadAuthorizationFilterBypassed =>
        AuthorizationFiltersBypassed ||
        _embeddingReadAuthorizationFilterSuppressionDepth > 0;

    internal bool AuthorizationBypassedForReadOptimization => AuthorizationFiltersBypassed;
    internal bool CanUseUnfilteredEmbeddingAnn(EmbeddingHostType? hostType)
    {
        if (EmbeddingReadAuthorizationFilterBypassed)
            return true;

        var principal = CurrentPrincipal;
        if (principal?.Has(PermissionKeys.EmbeddingsRead) != true)
            return false;

        bool Unrestricted(string entityKind, string permission)
            => principal.Has(permission) && !principal.ReadRestrictedEntityKinds.Contains(entityKind);

        return hostType switch
        {
            EmbeddingHostType.Video => Unrestricted(EntityKinds.Video, PermissionKeys.VideosRead),
            EmbeddingHostType.Image => Unrestricted(EntityKinds.Image, PermissionKeys.ImagesRead),
            EmbeddingHostType.Performer => Unrestricted(EntityKinds.Performer, PermissionKeys.PerformersRead),
            EmbeddingHostType.Face => principal.Has(PermissionKeys.FacesRead),
            EmbeddingHostType.Segment => Unrestricted(EntityKinds.Segment, PermissionKeys.SegmentsRead)
                && Unrestricted(EntityKinds.Video, PermissionKeys.VideosRead)
                && Unrestricted(EntityKinds.Audio, PermissionKeys.AudiosRead)
                && Unrestricted(EntityKinds.Image, PermissionKeys.ImagesRead),
            _ => Unrestricted(EntityKinds.Video, PermissionKeys.VideosRead)
                && Unrestricted(EntityKinds.Image, PermissionKeys.ImagesRead)
                && Unrestricted(EntityKinds.Performer, PermissionKeys.PerformersRead)
                && principal.Has(PermissionKeys.FacesRead)
                && Unrestricted(EntityKinds.Segment, PermissionKeys.SegmentsRead)
                && Unrestricted(EntityKinds.Audio, PermissionKeys.AudiosRead),
        };
    }

    private string[] CurrentRoleNames => CurrentPrincipal?.Roles.ToArray() ?? [];

    private Guid? CurrentShareLinkId => CurrentPrincipal?.Kind == PrincipalKind.ShareLink ? CurrentPrincipal.TokenId : null;
    private int? CurrentUserId => CurrentPrincipal?.UserId;

    internal Guid? CurrentShareLinkIdForReadOptimization => CurrentShareLinkId;

    private bool CanReadVideos => CurrentPrincipal?.Has(PermissionKeys.VideosRead) == true;
    private bool CanReadAudios => CurrentPrincipal?.Has(PermissionKeys.AudiosRead) == true;
    private bool CanReadTexts => CurrentPrincipal?.Has(PermissionKeys.TextsRead) == true;
    private bool CanReadPerformers => CurrentPrincipal?.Has(PermissionKeys.PerformersRead) == true;
    private bool CanReadTags => CurrentPrincipal?.Has(PermissionKeys.TagsRead) == true;
    private bool CanReadStudios => CurrentPrincipal?.Has(PermissionKeys.StudiosRead) == true;
    private bool CanReadGalleries => CurrentPrincipal?.Has(PermissionKeys.GalleriesRead) == true;
    private bool CanReadImages => CurrentPrincipal?.Has(PermissionKeys.ImagesRead) == true;
    private bool CanReadGroups => CurrentPrincipal?.Has(PermissionKeys.GroupsRead) == true;
    private bool CanReadSegments => CurrentPrincipal?.Has(PermissionKeys.SegmentsRead) == true;
    private bool CanReadFaces => CurrentPrincipal?.Has(PermissionKeys.FacesRead) == true;
    private bool CanReadEmbeddings => CurrentPrincipal?.Has(PermissionKeys.EmbeddingsRead) == true;
    private bool CanReadAiRuns => CurrentPrincipal?.Has(PermissionKeys.AiRunsRead) == true;
    private bool CanReadVideosByRule => CurrentPrincipal?.ReadGrantedEntityKinds.Contains(EntityKinds.Video) == true;
    private bool CanReadAudiosByRule => CurrentPrincipal?.ReadGrantedEntityKinds.Contains(EntityKinds.Audio) == true;
    private bool CanReadTextsByRule => CurrentPrincipal?.ReadGrantedEntityKinds.Contains(EntityKinds.Text) == true;
    private bool CanReadPerformersByRule => CurrentPrincipal?.ReadGrantedEntityKinds.Contains(EntityKinds.Performer) == true;
    private bool CanReadTagsByRule => CurrentPrincipal?.ReadGrantedEntityKinds.Contains(EntityKinds.Tag) == true;
    private bool CanReadStudiosByRule => CurrentPrincipal?.ReadGrantedEntityKinds.Contains(EntityKinds.Studio) == true;
    private bool CanReadGalleriesByRule => CurrentPrincipal?.ReadGrantedEntityKinds.Contains(EntityKinds.Gallery) == true;
    private bool CanReadImagesByRule => CurrentPrincipal?.ReadGrantedEntityKinds.Contains(EntityKinds.Image) == true;
    private bool CanReadGroupsByRule => CurrentPrincipal?.ReadGrantedEntityKinds.Contains(EntityKinds.Group) == true;
    private bool CanReadSegmentsByRule => CurrentPrincipal?.ReadGrantedEntityKinds.Contains(EntityKinds.Segment) == true;

    private bool RequiresVideoReadScopeEvaluation => CurrentShareLinkId != null || CurrentPrincipal?.ReadRestrictedEntityKinds.Contains(EntityKinds.Video) == true;
    private bool RequiresAudioReadScopeEvaluation => CurrentShareLinkId != null || CurrentPrincipal?.ReadRestrictedEntityKinds.Contains(EntityKinds.Audio) == true;
    private bool RequiresTextReadScopeEvaluation => CurrentShareLinkId != null || CurrentPrincipal?.ReadRestrictedEntityKinds.Contains(EntityKinds.Text) == true;
    private bool RequiresPerformerReadScopeEvaluation => CurrentShareLinkId != null || CurrentPrincipal?.ReadRestrictedEntityKinds.Contains(EntityKinds.Performer) == true;
    private bool RequiresTagReadScopeEvaluation => CurrentShareLinkId != null || CurrentPrincipal?.ReadRestrictedEntityKinds.Contains(EntityKinds.Tag) == true;
    private bool RequiresStudioReadScopeEvaluation => CurrentShareLinkId != null || CurrentPrincipal?.ReadRestrictedEntityKinds.Contains(EntityKinds.Studio) == true;
    private bool RequiresGalleryReadScopeEvaluation => CurrentShareLinkId != null || CurrentPrincipal?.ReadRestrictedEntityKinds.Contains(EntityKinds.Gallery) == true;
    private bool RequiresImageReadScopeEvaluation => CurrentShareLinkId != null || CurrentPrincipal?.ReadRestrictedEntityKinds.Contains(EntityKinds.Image) == true;
    private bool RequiresGroupReadScopeEvaluation => CurrentShareLinkId != null || CurrentPrincipal?.ReadRestrictedEntityKinds.Contains(EntityKinds.Group) == true;
    private bool RequiresSegmentReadScopeEvaluation => CurrentShareLinkId != null || CurrentPrincipal?.ReadRestrictedEntityKinds.Contains(EntityKinds.Segment) == true;

    [DbFunction("cove_authz_can_read", "public")]
    public static bool CanReadEntitySql(
        bool bypassAuthorization,
        bool hasReadPermission,
        bool hasReadGrant,
        string[] roleNames,
        Guid? shareLinkId,
        string entityKind,
        int entityId)
        => throw new NotSupportedException();

    public IQueryable<TEntity> ReadSet<TEntity>() where TEntity : class
        => AuthorizationFiltersBypassed ? Set<TEntity>().IgnoreQueryFilters() : Set<TEntity>();

    private void ConfigureAuthorizationFilters(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Video>().HasQueryFilter(video =>
            AuthorizationFiltersBypassed
                ? true
                : !RequiresVideoReadScopeEvaluation
                    ? CanReadVideos
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadVideos, CanReadVideosByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Video, video.Id));

        modelBuilder.Entity<Audio>().HasQueryFilter(audio =>
            AuthorizationFiltersBypassed
                ? true
                : !RequiresAudioReadScopeEvaluation
                    ? CanReadAudios
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadAudios, CanReadAudiosByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Audio, audio.Id));

        modelBuilder.Entity<TextDocument>().HasQueryFilter(text =>
            AuthorizationFiltersBypassed
                ? true
                : !RequiresTextReadScopeEvaluation
                    ? CanReadTexts
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadTexts, CanReadTextsByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Text, text.Id));

        modelBuilder.Entity<Performer>().HasQueryFilter(performer =>
            AuthorizationFiltersBypassed
                ? true
                : !RequiresPerformerReadScopeEvaluation
                    ? CanReadPerformers
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadPerformers, CanReadPerformersByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Performer, performer.Id));

        modelBuilder.Entity<Tag>().HasQueryFilter(tag =>
            AuthorizationFiltersBypassed
                ? true
                : !RequiresTagReadScopeEvaluation
                    ? CanReadTags
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadTags, CanReadTagsByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Tag, tag.Id));

        modelBuilder.Entity<Studio>().HasQueryFilter(studio =>
            AuthorizationFiltersBypassed
                ? true
                : !RequiresStudioReadScopeEvaluation
                    ? CanReadStudios
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadStudios, CanReadStudiosByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Studio, studio.Id));

        modelBuilder.Entity<Gallery>().HasQueryFilter(gallery =>
            AuthorizationFiltersBypassed
                ? true
                : !RequiresGalleryReadScopeEvaluation
                    ? CanReadGalleries
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadGalleries, CanReadGalleriesByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Gallery, gallery.Id));

        modelBuilder.Entity<Image>().HasQueryFilter(image =>
            AuthorizationFiltersBypassed
                ? true
                : !RequiresImageReadScopeEvaluation
                    ? CanReadImages
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadImages, CanReadImagesByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Image, image.Id));

        modelBuilder.Entity<Group>().HasQueryFilter(group =>
            AuthorizationFiltersBypassed
                ? true
                : !RequiresGroupReadScopeEvaluation
                    ? CanReadGroups
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadGroups, CanReadGroupsByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Group, group.Id));

        modelBuilder.Entity<Face>().HasQueryFilter(face =>
            AuthorizationFiltersBypassed || CanReadFaces);

        modelBuilder.Entity<Embedding>().HasQueryFilter(embedding =>
            EmbeddingReadAuthorizationFilterBypassed
            || CanReadEmbeddings
            && (embedding.HostType == EmbeddingHostType.Video
                ? Videos.Any(video => video.Id == embedding.HostId)
                : embedding.HostType == EmbeddingHostType.Image
                    ? Images.Any(image => image.Id == embedding.HostId)
                    : embedding.HostType == EmbeddingHostType.Performer
                        ? Performers.Any(performer => performer.Id == embedding.HostId)
                        : embedding.HostType == EmbeddingHostType.Face
                            ? Faces.Any(face => face.Id == embedding.HostId)
                            : embedding.HostType == EmbeddingHostType.Segment
                              && Segments.Any(segment =>
                                  segment.Id == embedding.HostId
                                  && (!RequiresSegmentReadScopeEvaluation
                                      ? CanReadSegments
                                      : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadSegments, CanReadSegmentsByRule,
                                          CurrentRoleNames, CurrentShareLinkId, EntityKinds.Segment, segment.Id))
                                  && (segment.HostType == SegmentHostType.Video
                                      ? Videos.Any(video => video.Id == segment.HostId)
                                      : segment.HostType == SegmentHostType.Audio
                                          ? Audios.Any(audio => audio.Id == segment.HostId)
                                          : segment.HostType == SegmentHostType.Image
                                              && Images.Any(image => image.Id == segment.HostId)))));

        modelBuilder.Entity<AiRun>().HasQueryFilter(run =>
            AuthorizationFiltersBypassed
            || CanReadAiRuns
            && (run.TargetType == AiRunTargetType.Video
                ? Videos.Any(video => video.Id == run.TargetId)
                : run.TargetType == AiRunTargetType.Image
                    ? Images.Any(image => image.Id == run.TargetId)
                    : run.TargetType == AiRunTargetType.Performer
                        ? Performers.Any(performer => performer.Id == run.TargetId)
                        : run.TargetType == AiRunTargetType.Face
                          && Faces.Any(face => face.Id == run.TargetId)));

        modelBuilder.Entity<UserEntityAffinity>().HasQueryFilter(affinity =>
            AuthorizationFiltersBypassed || (CurrentUserId != null && affinity.UserId == CurrentUserId));

        modelBuilder.Entity<UserBookmark>().HasQueryFilter(bookmark =>
            AuthorizationFiltersBypassed || (CurrentUserId != null && bookmark.UserId == CurrentUserId));

        modelBuilder.Entity<Interaction>().HasQueryFilter(interaction =>
            AuthorizationFiltersBypassed || (CurrentUserId != null && interaction.UserId == CurrentUserId));

        modelBuilder.Entity<PlaybackSession>().HasQueryFilter(session =>
            AuthorizationFiltersBypassed || (CurrentUserId != null && session.UserId == CurrentUserId));

        modelBuilder.Entity<PlaybackInterval>().HasQueryFilter(interval =>
            AuthorizationFiltersBypassed || (CurrentUserId != null && interval.UserId == CurrentUserId));

        modelBuilder.Entity<Rating>().HasQueryFilter(rating =>
            AuthorizationFiltersBypassed || (CurrentUserId != null && rating.UserId == CurrentUserId));

        modelBuilder.Entity<SegmentDisplayProfile>().HasQueryFilter(profile =>
            AuthorizationFiltersBypassed || profile.UserId == null || (CurrentUserId != null && profile.UserId == CurrentUserId));

        modelBuilder.Entity<SegmentDisplayRule>().HasQueryFilter(rule =>
            AuthorizationFiltersBypassed || rule.UserId == null || (CurrentUserId != null && rule.UserId == CurrentUserId));

        modelBuilder.Entity<GalleryChapter>().HasQueryFilter(chapter =>
            AuthorizationFiltersBypassed
                ? true
                : !RequiresGalleryReadScopeEvaluation
                    ? CanReadGalleries
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadGalleries, CanReadGalleriesByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Gallery, chapter.GalleryId));

        modelBuilder.Entity<VideoUrl>().HasQueryFilter(link =>
            AuthorizationFiltersBypassed
                ? true
                : !RequiresVideoReadScopeEvaluation
                    ? CanReadVideos
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadVideos, CanReadVideosByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Video, link.VideoId));

        modelBuilder.Entity<VideoRemoteId>().HasQueryFilter(link =>
            AuthorizationFiltersBypassed
                ? true
                : !RequiresVideoReadScopeEvaluation
                    ? CanReadVideos
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadVideos, CanReadVideosByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Video, link.VideoId));

        modelBuilder.Entity<VideoPlayHistory>().HasQueryFilter(entry =>
            AuthorizationFiltersBypassed
                ? true
                : !RequiresVideoReadScopeEvaluation
                    ? CanReadVideos
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadVideos, CanReadVideosByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Video, entry.VideoId));

        modelBuilder.Entity<PerformerUrl>().HasQueryFilter(link =>
            AuthorizationFiltersBypassed
                ? true
                : !RequiresPerformerReadScopeEvaluation
                    ? CanReadPerformers
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadPerformers, CanReadPerformersByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Performer, link.PerformerId));

        modelBuilder.Entity<PerformerAlias>().HasQueryFilter(link =>
            AuthorizationFiltersBypassed
                ? true
                : !RequiresPerformerReadScopeEvaluation
                    ? CanReadPerformers
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadPerformers, CanReadPerformersByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Performer, link.PerformerId));

        modelBuilder.Entity<PerformerRemoteId>().HasQueryFilter(link =>
            AuthorizationFiltersBypassed
                ? true
                : !RequiresPerformerReadScopeEvaluation
                    ? CanReadPerformers
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadPerformers, CanReadPerformersByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Performer, link.PerformerId));

        modelBuilder.Entity<TagAlias>().HasQueryFilter(link =>
            AuthorizationFiltersBypassed
                ? true
                : !RequiresTagReadScopeEvaluation
                    ? CanReadTags
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadTags, CanReadTagsByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Tag, link.TagId));

        modelBuilder.Entity<TagRemoteId>().HasQueryFilter(link =>
            AuthorizationFiltersBypassed
                ? true
                : !RequiresTagReadScopeEvaluation
                    ? CanReadTags
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadTags, CanReadTagsByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Tag, link.TagId));

        modelBuilder.Entity<StudioUrl>().HasQueryFilter(link =>
            AuthorizationFiltersBypassed
                ? true
                : !RequiresStudioReadScopeEvaluation
                    ? CanReadStudios
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadStudios, CanReadStudiosByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Studio, link.StudioId));

        modelBuilder.Entity<StudioAlias>().HasQueryFilter(link =>
            AuthorizationFiltersBypassed
                ? true
                : !RequiresStudioReadScopeEvaluation
                    ? CanReadStudios
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadStudios, CanReadStudiosByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Studio, link.StudioId));

        modelBuilder.Entity<StudioRemoteId>().HasQueryFilter(link =>
            AuthorizationFiltersBypassed
                ? true
                : !RequiresStudioReadScopeEvaluation
                    ? CanReadStudios
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadStudios, CanReadStudiosByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Studio, link.StudioId));

        modelBuilder.Entity<GalleryUrl>().HasQueryFilter(link =>
            AuthorizationFiltersBypassed
                ? true
                : !RequiresGalleryReadScopeEvaluation
                    ? CanReadGalleries
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadGalleries, CanReadGalleriesByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Gallery, link.GalleryId));

        modelBuilder.Entity<ImageUrl>().HasQueryFilter(link =>
            AuthorizationFiltersBypassed
                ? true
                : !RequiresImageReadScopeEvaluation
                    ? CanReadImages
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadImages, CanReadImagesByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Image, link.ImageId));

        modelBuilder.Entity<GroupUrl>().HasQueryFilter(link =>
            AuthorizationFiltersBypassed
                ? true
                : !RequiresGroupReadScopeEvaluation
                    ? CanReadGroups
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadGroups, CanReadGroupsByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Group, link.GroupId));

        modelBuilder.Entity<VideoTag>().HasQueryFilter(link =>
            AuthorizationFiltersBypassed
                ? true
                : (!RequiresVideoReadScopeEvaluation
                    ? CanReadVideos
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadVideos, CanReadVideosByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Video, link.VideoId))
                && (!RequiresTagReadScopeEvaluation
                    ? CanReadTags
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadTags, CanReadTagsByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Tag, link.TagId)));

        modelBuilder.Entity<VideoPerformer>().HasQueryFilter(link =>
            AuthorizationFiltersBypassed
                ? true
                : (!RequiresVideoReadScopeEvaluation
                    ? CanReadVideos
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadVideos, CanReadVideosByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Video, link.VideoId))
                && (!RequiresPerformerReadScopeEvaluation
                    ? CanReadPerformers
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadPerformers, CanReadPerformersByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Performer, link.PerformerId)));

        modelBuilder.Entity<VideoGallery>().HasQueryFilter(link =>
            AuthorizationFiltersBypassed
                ? true
                : (!RequiresVideoReadScopeEvaluation
                    ? CanReadVideos
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadVideos, CanReadVideosByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Video, link.VideoId))
                && (!RequiresGalleryReadScopeEvaluation
                    ? CanReadGalleries
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadGalleries, CanReadGalleriesByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Gallery, link.GalleryId)));

        modelBuilder.Entity<GroupItem>().HasQueryFilter(item =>
            AuthorizationFiltersBypassed
                ? true
                : (item.HostType == "video"
                    ? (!RequiresVideoReadScopeEvaluation
                        ? CanReadVideos
                        : item.VideoId != null && CanReadEntitySql(AuthorizationFiltersBypassed, CanReadVideos, CanReadVideosByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Video, item.VideoId.Value))
                    : item.HostType == "audio"
                        ? (!RequiresAudioReadScopeEvaluation
                            ? CanReadAudios
                            : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadAudios, CanReadAudiosByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Audio, item.HostId))
                    : item.HostType == "text"
                        ? (!RequiresTextReadScopeEvaluation
                            ? CanReadTexts
                            : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadTexts, CanReadTextsByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Text, item.HostId))
                    : item.HostType == "image"
                        ? (!RequiresImageReadScopeEvaluation
                            ? CanReadImages
                            : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadImages, CanReadImagesByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Image, item.HostId))
                    : item.HostType == "performer"
                        ? (!RequiresPerformerReadScopeEvaluation
                            ? CanReadPerformers
                            : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadPerformers, CanReadPerformersByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Performer, item.HostId))
                        : item.HostType == "studio"
                            ? (!RequiresStudioReadScopeEvaluation
                                ? CanReadStudios
                                : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadStudios, CanReadStudiosByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Studio, item.HostId))
                            : item.HostType == "tag"
                                ? (!RequiresTagReadScopeEvaluation
                                    ? CanReadTags
                                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadTags, CanReadTagsByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Tag, item.HostId))
                                : item.HostType == "gallery"
                                    ? (!RequiresGalleryReadScopeEvaluation
                                        ? CanReadGalleries
                                        : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadGalleries, CanReadGalleriesByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Gallery, item.HostId))
                                    : item.HostType == "face"
                                        ? CanReadFaces
                                        : item.HostType == "segment"
                                            ? (!RequiresSegmentReadScopeEvaluation
                                                ? CanReadSegments
                                                : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadSegments, CanReadSegmentsByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Segment, item.HostId))
                                              && Segments.Any(segment =>
                                                  segment.Id == item.HostId
                                                  && (segment.HostType == SegmentHostType.Video
                                                      ? Videos.Any(video => video.Id == segment.HostId)
                                                      : segment.HostType == SegmentHostType.Audio
                                                          ? Audios.Any(audio => audio.Id == segment.HostId)
                                                          : segment.HostType == SegmentHostType.Image
                                                              ? Images.Any(image => image.Id == segment.HostId)
                                                              : false))
                                            : item.HostType == "group"
                                                ? (!RequiresGroupReadScopeEvaluation
                                                    ? CanReadGroups
                                                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadGroups, CanReadGroupsByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Group, item.HostId))
                                                : false)
                && (!RequiresGroupReadScopeEvaluation
                    ? CanReadGroups
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadGroups, CanReadGroupsByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Group, item.GroupId)));

        modelBuilder.Entity<AudioUrl>().HasQueryFilter(link =>
            AuthorizationFiltersBypassed
                ? true
                : !RequiresAudioReadScopeEvaluation
                    ? CanReadAudios
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadAudios, CanReadAudiosByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Audio, link.AudioId));

        modelBuilder.Entity<AudioTrack>().HasQueryFilter(track =>
            AuthorizationFiltersBypassed
                ? true
                : !RequiresAudioReadScopeEvaluation
                    ? CanReadAudios
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadAudios, CanReadAudiosByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Audio, track.AudioId));

        modelBuilder.Entity<AudioTag>().HasQueryFilter(link =>
            AuthorizationFiltersBypassed
                ? true
                : (!RequiresAudioReadScopeEvaluation
                    ? CanReadAudios
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadAudios, CanReadAudiosByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Audio, link.AudioId))
                && (!RequiresTagReadScopeEvaluation
                    ? CanReadTags
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadTags, CanReadTagsByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Tag, link.TagId)));

        modelBuilder.Entity<AudioPerformer>().HasQueryFilter(link =>
            AuthorizationFiltersBypassed
                ? true
                : (!RequiresAudioReadScopeEvaluation
                    ? CanReadAudios
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadAudios, CanReadAudiosByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Audio, link.AudioId))
                && (!RequiresPerformerReadScopeEvaluation
                    ? CanReadPerformers
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadPerformers, CanReadPerformersByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Performer, link.PerformerId)));

        modelBuilder.Entity<TextUrl>().HasQueryFilter(link =>
            AuthorizationFiltersBypassed
                ? true
                : !RequiresTextReadScopeEvaluation
                    ? CanReadTexts
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadTexts, CanReadTextsByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Text, link.TextDocumentId));

        modelBuilder.Entity<TextTag>().HasQueryFilter(link =>
            AuthorizationFiltersBypassed
                ? true
                : (!RequiresTextReadScopeEvaluation
                    ? CanReadTexts
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadTexts, CanReadTextsByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Text, link.TextDocumentId))
                && (!RequiresTagReadScopeEvaluation
                    ? CanReadTags
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadTags, CanReadTagsByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Tag, link.TagId)));

        modelBuilder.Entity<TextPerformer>().HasQueryFilter(link =>
            AuthorizationFiltersBypassed
                ? true
                : (!RequiresTextReadScopeEvaluation
                    ? CanReadTexts
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadTexts, CanReadTextsByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Text, link.TextDocumentId))
                && (!RequiresPerformerReadScopeEvaluation
                    ? CanReadPerformers
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadPerformers, CanReadPerformersByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Performer, link.PerformerId)));

        modelBuilder.Entity<FaceAppearance>().HasQueryFilter(appearance =>
            AuthorizationFiltersBypassed
                ? true
                : CanReadFaces
                && (appearance.HostType == FaceAppearanceHostType.Video
                    ? (!RequiresVideoReadScopeEvaluation
                        ? CanReadVideos
                        : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadVideos, CanReadVideosByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Video, appearance.HostId))
                    : appearance.HostType == FaceAppearanceHostType.Image
                        ? (!RequiresImageReadScopeEvaluation
                            ? CanReadImages
                            : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadImages, CanReadImagesByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Image, appearance.HostId))
                        : false));

        modelBuilder.Entity<FaceSuggestionDecision>().HasQueryFilter(decision =>
            AuthorizationFiltersBypassed
                ? true
                : (CurrentUserId != null && decision.UserId == CurrentUserId)
                && CanReadFaces
                && (!RequiresPerformerReadScopeEvaluation
                    ? CanReadPerformers
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadPerformers, CanReadPerformersByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Performer, decision.PerformerId)));

        modelBuilder.Entity<TagApplication>().HasQueryFilter(application =>
            AuthorizationFiltersBypassed
                ? true
                : (application.HostType == AffinityHostType.Video
                    ? (!RequiresVideoReadScopeEvaluation
                        ? CanReadVideos
                        : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadVideos, CanReadVideosByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Video, application.HostId))
                    : application.HostType == AffinityHostType.Image
                        ? (!RequiresImageReadScopeEvaluation
                            ? CanReadImages
                            : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadImages, CanReadImagesByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Image, application.HostId))
                        : application.HostType == AffinityHostType.Performer
                            ? (!RequiresPerformerReadScopeEvaluation
                                ? CanReadPerformers
                                : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadPerformers, CanReadPerformersByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Performer, application.HostId))
                            : application.HostType == AffinityHostType.Face
                                ? CanReadFaces
                                : application.HostType == AffinityHostType.Tag
                                    ? (!RequiresTagReadScopeEvaluation
                                        ? CanReadTags
                                        : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadTags, CanReadTagsByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Tag, application.HostId))
                                    : application.HostType == AffinityHostType.Studio
                                        ? (!RequiresStudioReadScopeEvaluation
                                            ? CanReadStudios
                                            : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadStudios, CanReadStudiosByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Studio, application.HostId))
                                        : application.HostType == AffinityHostType.Gallery
                                            ? (!RequiresGalleryReadScopeEvaluation
                                                ? CanReadGalleries
                                                : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadGalleries, CanReadGalleriesByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Gallery, application.HostId))
                                            : application.HostType == AffinityHostType.Group
                                                ? (!RequiresGroupReadScopeEvaluation
                                                    ? CanReadGroups
                                                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadGroups, CanReadGroupsByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Group, application.HostId))
                                                : application.HostType == AffinityHostType.Audio
                                                    ? (!RequiresAudioReadScopeEvaluation
                                                        ? CanReadAudios
                                                        : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadAudios, CanReadAudiosByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Audio, application.HostId))
                                                    : application.HostType == AffinityHostType.Text
                                                        ? (!RequiresTextReadScopeEvaluation
                                                            ? CanReadTexts
                                                            : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadTexts, CanReadTextsByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Text, application.HostId))
                                                        : false)
                && (!RequiresTagReadScopeEvaluation
                    ? CanReadTags
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadTags, CanReadTagsByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Tag, application.TagId))
                && (application.ContextType == null
                    ? application.ContextId == null
                    : application.ContextId != null
                        && (application.ContextType == "performer"
                            ? (!RequiresPerformerReadScopeEvaluation
                                ? CanReadPerformers
                                : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadPerformers, CanReadPerformersByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Performer, application.ContextId.Value))
                            : application.ContextType == "face"
                                ? CanReadFaces
                                : application.ContextType == "segment" || application.ContextType == "detection"
                                    ? (!RequiresSegmentReadScopeEvaluation
                                        ? CanReadSegments
                                        : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadSegments, CanReadSegmentsByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Segment, application.ContextId.Value))
                                    : false)));

        modelBuilder.Entity<PerformerTag>().HasQueryFilter(link =>
            AuthorizationFiltersBypassed
                ? true
                : (!RequiresPerformerReadScopeEvaluation
                    ? CanReadPerformers
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadPerformers, CanReadPerformersByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Performer, link.PerformerId))
                && (!RequiresTagReadScopeEvaluation
                    ? CanReadTags
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadTags, CanReadTagsByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Tag, link.TagId)));

        modelBuilder.Entity<ImageTag>().HasQueryFilter(link =>
            AuthorizationFiltersBypassed
                ? true
                : (!RequiresImageReadScopeEvaluation
                    ? CanReadImages
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadImages, CanReadImagesByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Image, link.ImageId))
                && (!RequiresTagReadScopeEvaluation
                    ? CanReadTags
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadTags, CanReadTagsByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Tag, link.TagId)));

        modelBuilder.Entity<ImagePerformer>().HasQueryFilter(link =>
            AuthorizationFiltersBypassed
                ? true
                : (!RequiresImageReadScopeEvaluation
                    ? CanReadImages
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadImages, CanReadImagesByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Image, link.ImageId))
                && (!RequiresPerformerReadScopeEvaluation
                    ? CanReadPerformers
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadPerformers, CanReadPerformersByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Performer, link.PerformerId)));

        modelBuilder.Entity<ImageGallery>().HasQueryFilter(link =>
            AuthorizationFiltersBypassed
                ? true
                : (!RequiresImageReadScopeEvaluation
                    ? CanReadImages
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadImages, CanReadImagesByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Image, link.ImageId))
                && (!RequiresGalleryReadScopeEvaluation
                    ? CanReadGalleries
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadGalleries, CanReadGalleriesByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Gallery, link.GalleryId)));

        modelBuilder.Entity<GalleryTag>().HasQueryFilter(link =>
            AuthorizationFiltersBypassed
                ? true
                : (!RequiresGalleryReadScopeEvaluation
                    ? CanReadGalleries
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadGalleries, CanReadGalleriesByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Gallery, link.GalleryId))
                && (!RequiresTagReadScopeEvaluation
                    ? CanReadTags
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadTags, CanReadTagsByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Tag, link.TagId)));

        modelBuilder.Entity<GalleryPerformer>().HasQueryFilter(link =>
            AuthorizationFiltersBypassed
                ? true
                : (!RequiresGalleryReadScopeEvaluation
                    ? CanReadGalleries
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadGalleries, CanReadGalleriesByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Gallery, link.GalleryId))
                && (!RequiresPerformerReadScopeEvaluation
                    ? CanReadPerformers
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadPerformers, CanReadPerformersByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Performer, link.PerformerId)));

        modelBuilder.Entity<StudioTag>().HasQueryFilter(link =>
            AuthorizationFiltersBypassed
                ? true
                : (!RequiresStudioReadScopeEvaluation
                    ? CanReadStudios
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadStudios, CanReadStudiosByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Studio, link.StudioId))
                && (!RequiresTagReadScopeEvaluation
                    ? CanReadTags
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadTags, CanReadTagsByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Tag, link.TagId)));

        modelBuilder.Entity<GroupTag>().HasQueryFilter(link =>
            AuthorizationFiltersBypassed
                ? true
                : (!RequiresGroupReadScopeEvaluation
                    ? CanReadGroups
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadGroups, CanReadGroupsByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Group, link.GroupId))
                && (!RequiresTagReadScopeEvaluation
                    ? CanReadTags
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadTags, CanReadTagsByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Tag, link.TagId)));

        modelBuilder.Entity<GroupRelation>().HasQueryFilter(link =>
            AuthorizationFiltersBypassed
                ? true
                : (!RequiresGroupReadScopeEvaluation
                    ? CanReadGroups
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadGroups, CanReadGroupsByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Group, link.ContainingGroupId))
                && (!RequiresGroupReadScopeEvaluation
                    ? CanReadGroups
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadGroups, CanReadGroupsByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Group, link.SubGroupId)));

        modelBuilder.Entity<TagParent>().HasQueryFilter(link =>
            AuthorizationFiltersBypassed
                ? true
                : (!RequiresTagReadScopeEvaluation
                    ? CanReadTags
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadTags, CanReadTagsByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Tag, link.ParentId))
                && (!RequiresTagReadScopeEvaluation
                    ? CanReadTags
                    : CanReadEntitySql(AuthorizationFiltersBypassed, CanReadTags, CanReadTagsByRule, CurrentRoleNames, CurrentShareLinkId, EntityKinds.Tag, link.ChildId)));
    }
}
