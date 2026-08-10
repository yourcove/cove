namespace Cove.Core.Auth;

/// <summary>
/// Canonical permission key catalog for the core application. Extension permissions
/// live in their own keys with an extension namespace prefix.
/// </summary>
public static class Permissions
{
    // Wildcard / meta
    public const string All = "*";

    // Videos
    public const string VideosRead = "videos.read";
    public const string VideosWrite = "videos.write";
    public const string VideosDelete = "videos.delete";
    public const string VideosDeleteFile = "videos.delete.file";
    public const string VideosScrape = "videos.scrape";

    // Audios
    public const string AudiosRead = "audios.read";
    public const string AudiosWrite = "audios.write";
    public const string AudiosDelete = "audios.delete";

    // Texts
    public const string TextsRead = "texts.read";
    public const string TextsWrite = "texts.write";
    public const string TextsDelete = "texts.delete";

    // Performers
    public const string PerformersRead = "performers.read";
    public const string PerformersWrite = "performers.write";
    public const string PerformersDelete = "performers.delete";
    public const string PerformersScrape = "performers.scrape";

    // Faces
    public const string FacesRead = "faces.read";
    public const string FacesWrite = "faces.write";
    public const string FacesDelete = "faces.delete";

    // Tags
    public const string TagsRead = "tags.read";
    public const string TagsWrite = "tags.write";
    public const string TagsDelete = "tags.delete";
    public const string TagNameConflictsManage = "tags.name-conflicts.manage";
    public const string TagGroupsRead = "taggroups.read";
    public const string TagGroupsWrite = "taggroups.write";
    public const string TagGroupsDelete = "taggroups.delete";

    // Studios
    public const string StudiosRead = "studios.read";
    public const string StudiosWrite = "studios.write";
    public const string StudiosDelete = "studios.delete";

    // Galleries
    public const string GalleriesRead = "galleries.read";
    public const string GalleriesWrite = "galleries.write";
    public const string GalleriesDelete = "galleries.delete";

    // Images
    public const string ImagesRead = "images.read";
    public const string ImagesWrite = "images.write";
    public const string ImagesDelete = "images.delete";
    public const string ImagesDeleteFile = "images.delete.file";

    // Groups
    public const string GroupsRead = "groups.read";
    public const string GroupsWrite = "groups.write";
    public const string GroupsDelete = "groups.delete";

    // Segments
    public const string SegmentsRead = "segments.read";
    public const string SegmentsWrite = "segments.write";
    public const string SegmentsDelete = "segments.delete";

    // Files
    public const string FilesRead = "files.read";
    public const string FilesWrite = "files.write";
    public const string FilesDelete = "files.delete";

    // Library / scan / cleanup
    public const string LibraryScan = "library.scan";
    public const string LibraryIdentify = "library.identify";
    public const string LibraryClean = "library.clean";

    // Saved filters
    public const string SavedFiltersRead = "savedfilters.read";
    public const string SavedFiltersWrite = "savedfilters.write";
    public const string SavedFiltersDelete = "savedfilters.delete";

    // Jobs
    public const string JobsRead = "jobs.read";
    public const string JobsRun = "jobs.run";
    public const string JobsCancel = "jobs.cancel";

    // AI core
    public const string EmbeddingsRead = "embeddings.read";
    public const string AiRunsRead = "airuns.read";
    public const string AiDataRead = "aidata.read";
    public const string AiDataClear = "aidata.clear";

    // Extensions
    public const string ExtensionsRead = "extensions.read";
    public const string ExtensionsInstall = "extensions.install";
    public const string ExtensionsConfigure = "extensions.configure";
    public const string ExtensionsUninstall = "extensions.uninstall";

    // Users / roles
    public const string UsersRead = "users.read";
    public const string UsersWrite = "users.write";
    public const string UsersInvite = "users.invite";
    public const string UsersDelete = "users.delete";
    public const string RolesRead = "roles.read";
    public const string RolesWrite = "roles.write";
    public const string RolesDelete = "roles.delete";
    public const string ApiTokensWrite = "apitokens.write";
    public const string ShareLinksWrite = "sharelinks.write";

    // System
    public const string SystemRead = "system.read";
    public const string SystemSettingsWrite = "system.settings.write";
    public const string SystemBackup = "system.backup";
    public const string SystemRestore = "system.restore";
    public const string SystemWipe = "system.wipe";
    public const string SystemShutdown = "system.shutdown";

    // Audit
    public const string AuditRead = "audit.read";

    // Migrations / import
    public const string ImportStash = "import.stash";

    // Streaming / file ops
    public const string StreamRead = "stream.read";

    /// <summary>Authoritative full list, used by PermissionRegistry to seed the catalog.</summary>
    public static readonly PermissionDefinition[] CorePermissions =
    [
        new(All, "System", "Superuser; bypasses every check. Only assignable to the Owner role.", Dangerous: true),

        new(VideosRead, "Videos", "View videos in lists, detail pages, search."),
        new(VideosWrite, "Videos", "Create or edit video metadata.", Implies: [VideosRead]),
        new(VideosDelete, "Videos", "Delete video rows (metadata).", Dangerous: true, Implies: [VideosRead]),
        new(VideosDeleteFile, "Videos", "Delete underlying video files from disk.", Dangerous: true, Implies: [VideosDelete]),
        new(VideosScrape, "Videos", "Run scrapers against videos.", Implies: [VideosRead]),

        new(AudiosRead, "Audios", "View audio items."),
        new(AudiosWrite, "Audios", "Create or edit audio metadata.", Implies: [AudiosRead]),
        new(AudiosDelete, "Audios", "Delete audio rows.", Dangerous: true, Implies: [AudiosRead]),

        new(TextsRead, "Texts", "View text items."),
        new(TextsWrite, "Texts", "Create or edit text metadata.", Implies: [TextsRead]),
        new(TextsDelete, "Texts", "Delete text rows.", Dangerous: true, Implies: [TextsRead]),

        new(PerformersRead, "Performers", "View performers."),
        new(PerformersWrite, "Performers", "Create or edit performer metadata.", Implies: [PerformersRead]),
        new(PerformersDelete, "Performers", "Delete performer rows.", Dangerous: true, Implies: [PerformersRead]),
        new(PerformersScrape, "Performers", "Run scrapers against performers.", Implies: [PerformersRead]),

        new(FacesRead, "Faces", "View detected and curated faces."),
        new(FacesWrite, "Faces", "Create or edit faces, merge them, and link them to performers.", Implies: [FacesRead]),
        new(FacesDelete, "Faces", "Delete faces.", Dangerous: true, Implies: [FacesRead]),

        new(TagsRead, "Tags", "View tags."),
        new(TagsWrite, "Tags", "Create or edit tags.", Implies: [TagsRead]),
        new(TagsDelete, "Tags", "Delete tags.", Dangerous: true, Implies: [TagsRead]),
        new(TagNameConflictsManage, "Tags", "Review and resolve tag name and alias conflicts before namespace enforcement.", Dangerous: true, Implies: [TagsRead, TagsWrite, TagsDelete], GrantToAdminsByDefault: true),
        new(TagGroupsRead, "Tag Groups", "View tag groups."),
        new(TagGroupsWrite, "Tag Groups", "Create or edit tag groups.", Implies: [TagGroupsRead, TagsRead]),
        new(TagGroupsDelete, "Tag Groups", "Delete tag groups.", Dangerous: true, Implies: [TagGroupsRead, TagsRead]),

        new(StudiosRead, "Studios", "View studios."),
        new(StudiosWrite, "Studios", "Create or edit studios.", Implies: [StudiosRead]),
        new(StudiosDelete, "Studios", "Delete studios.", Dangerous: true, Implies: [StudiosRead]),

        new(GalleriesRead, "Galleries", "View galleries."),
        new(GalleriesWrite, "Galleries", "Create or edit galleries.", Implies: [GalleriesRead]),
        new(GalleriesDelete, "Galleries", "Delete galleries.", Dangerous: true, Implies: [GalleriesRead]),

        new(ImagesRead, "Images", "View images."),
        new(ImagesWrite, "Images", "Create or edit images.", Implies: [ImagesRead]),
        new(ImagesDelete, "Images", "Delete image rows.", Dangerous: true, Implies: [ImagesRead]),
        new(ImagesDeleteFile, "Images", "Delete underlying image files.", Dangerous: true, Implies: [ImagesDelete]),

        new(GroupsRead, "Groups", "View groups."),
        new(GroupsWrite, "Groups", "Create or edit groups.", Implies: [GroupsRead]),
        new(GroupsDelete, "Groups", "Delete groups.", Dangerous: true, Implies: [GroupsRead]),

        new(SegmentsRead, "Segments", "View segments and detections."),
        new(SegmentsWrite, "Segments", "Create or edit segments and detections.", Implies: [SegmentsRead]),
        new(SegmentsDelete, "Segments", "Delete segments and detections.", Implies: [SegmentsRead]),

        new(FilesRead, "Files", "List orphan/raw files in the library."),
        new(FilesWrite, "Files", "Move files or edit raw-file metadata/fingerprints.", Dangerous: true, Implies: [FilesRead]),
        new(FilesDelete, "Files", "Delete raw files from disk.", Dangerous: true, Implies: [FilesWrite]),

        new(LibraryScan, "Library", "Trigger library scans."),
        new(LibraryIdentify, "Library", "Run identify (scraper-based metadata matching) jobs."),
        new(LibraryClean, "Library", "Trigger cleanup of missing files.", Dangerous: true),

        new(SavedFiltersRead, "Saved Filters", "View saved filters."),
        new(SavedFiltersWrite, "Saved Filters", "Create or edit saved filters.", Implies: [SavedFiltersRead]),
        new(SavedFiltersDelete, "Saved Filters", "Delete saved filters.", Implies: [SavedFiltersRead]),

        new(JobsRead, "Jobs", "View job queue and history."),
        new(JobsRun, "Jobs", "Submit new jobs.", Implies: [JobsRead]),
        new(JobsCancel, "Jobs", "Cancel running jobs.", Implies: [JobsRead]),

        new(EmbeddingsRead, "AI", "View embeddings and similarity-search results."),
        new(AiRunsRead, "AI", "View AI run provenance and summaries."),
        new(AiDataRead, "AI", "View AI-managed artifact summaries."),
        new(AiDataClear, "AI", "Preview and clear AI-managed artifacts.", Dangerous: true, Implies: [AiDataRead]),

        new(ExtensionsRead, "Extensions", "View installed extensions and registry."),
        new(ExtensionsInstall, "Extensions", "Install or update extensions.", Dangerous: true, Implies: [ExtensionsRead]),
        new(ExtensionsConfigure, "Extensions", "Change extension configuration.", Implies: [ExtensionsRead]),
        new(ExtensionsUninstall, "Extensions", "Uninstall extensions.", Dangerous: true, Implies: [ExtensionsRead]),

        new(UsersRead, "Users", "View user accounts."),
        new(UsersWrite, "Users", "Create or edit user accounts.", Dangerous: true, Implies: [UsersRead]),
        new(UsersInvite, "Users", "Create one-time user password invite links.", Dangerous: true, Implies: [UsersRead]),
        new(UsersDelete, "Users", "Delete user accounts.", Dangerous: true, Implies: [UsersRead]),
        new(RolesRead, "Roles", "View roles and permissions."),
        new(RolesWrite, "Roles", "Create or edit roles and permission assignments.", Dangerous: true, Implies: [RolesRead]),
        new(RolesDelete, "Roles", "Delete roles.", Dangerous: true, Implies: [RolesRead]),
        new(ApiTokensWrite, "Access", "Create, list, and revoke API tokens for user accounts.", Dangerous: true),
        new(ShareLinksWrite, "Access", "Create, list, and revoke share links for accessible content.", Dangerous: true),

        new(SystemRead, "System", "View system status and configuration."),
        new(SystemSettingsWrite, "System", "Change application settings.", Dangerous: true, Implies: [SystemRead]),
        new(SystemBackup, "System", "Trigger backup operations.", Implies: [SystemRead]),
        new(SystemRestore, "System", "Restore from a backup.", Dangerous: true, Implies: [SystemRead]),
        new(SystemWipe, "System", "Wipe the entire library. Cannot be undone.", Dangerous: true),
        new(SystemShutdown, "System", "Shut down the Cove server process.", Dangerous: true, Implies: [SystemRead]),

        new(AuditRead, "Audit", "View the security audit log."),

        new(ImportStash, "Import", "Run a Stash database import.", Dangerous: true),

        new(StreamRead, "Streaming", "Access the streaming endpoints (raw bytes / HLS).", Implies: [VideosRead]),
    ];

    /// <summary>Default permission set for the Member role.</summary>
    public static readonly string[] MemberDefaults =
    [
        VideosWrite, VideosScrape,
        AudiosWrite, TextsWrite,
        PerformersWrite, PerformersScrape,
        FacesWrite, FacesDelete,
        TagsWrite, TagGroupsWrite, StudiosWrite,
        GalleriesWrite, ImagesWrite, GroupsWrite,
        SegmentsWrite, SegmentsDelete,
        FilesRead, FilesWrite,
        LibraryScan, LibraryIdentify,
        SavedFiltersWrite, SavedFiltersDelete,
        JobsRun, JobsCancel,
        ExtensionsRead,
        SystemRead,
        StreamRead,
    ];

    /// <summary>Default permission set for the Viewer role (read-only).</summary>
    public static readonly string[] ViewerDefaults =
    [
        VideosRead, AudiosRead, TextsRead, PerformersRead, TagsRead, StudiosRead,
        TagGroupsRead,
        GalleriesRead, ImagesRead, GroupsRead, SegmentsRead,
        FacesRead, EmbeddingsRead, AiRunsRead,
        SavedFiltersRead, JobsRead, ExtensionsRead, SystemRead,
        StreamRead,
    ];

    /// <summary>Default permission set for the Guest role (share-link target).</summary>
    public static readonly string[] GuestDefaults =
    [
        VideosRead, AudiosRead, TextsRead, PerformersRead, TagsRead, StudiosRead,
        TagGroupsRead,
        GalleriesRead, ImagesRead, GroupsRead,
        StreamRead,
    ];

    /// <summary>Default permission set for the Admin role: everything except wipe and audit-protected admin actions on the Owner.</summary>
    public static IEnumerable<string> AdminDefaults()
    {
        foreach (var p in CorePermissions)
        {
            if (p.Key == All) continue;
            if (p.Key == SystemWipe) continue;
            if (p.Key == SystemShutdown) continue;
            yield return p.Key;
        }
    }
}

/// <summary>
/// In-memory permission definition contributed by core or an extension.
/// </summary>
public sealed record PermissionDefinition(
    string Key,
    string Category,
    string Description,
    bool Dangerous = false,
    string[]? Implies = null,
    string Source = "core",
    bool GrantToAdminsByDefault = false);
