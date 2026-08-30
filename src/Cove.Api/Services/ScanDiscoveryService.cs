using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Cove.Core.Common;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cove.Api.Services;

/// <summary>
/// Discovers scan candidates and maintains the directory checkpoints that make unchanged-directory
/// scans cheap. Media validation and persistence deliberately remain in <see cref="ScanService"/>.
/// </summary>
internal sealed class ScanDiscoveryService(
    IServiceScopeFactory scopeFactory,
    CoveConfiguration config,
    ILogger logger)
{
    private static readonly TimeSpan ScanCommandTimeout = TimeSpan.FromMinutes(5);
    private const int DirectoryScanSignatureVersion = 2;
    private static readonly string[] FolderIgnoreFileNames = [".coveignore", ".stashignore"];

    public async Task<ScanDiscoveryResult> DiscoverAsync(
        ScanOperationOptions options,
        IJobProgress progress,
        CancellationToken ct)
    {
        var extensions = ScanExtensionCatalog.From(config);
        var scanTargets = ResolveScanTargets(config, options.Paths);
        var scanStartedAt = DateTime.UtcNow;
        var directorySkippingEnabled = !RequiresFullFileDiscovery(options);
        var directoryScanSignature = ComputeDirectoryScanSignature(config, scanTargets);
        var directoryScanStates = new Dictionary<string, DirectoryScanState>(FilesystemPaths.PathComparer);

        if (directorySkippingEnabled && scanTargets.Count > 0)
        {
            await using var stateScope = scopeFactory.CreateAsyncScope();
            var stateDb = stateScope.ServiceProvider.GetRequiredService<CoveContext>();
            if (stateDb.Database.IsRelational())
                stateDb.Database.SetCommandTimeout(ScanCommandTimeout);
            directoryScanStates = await LoadDirectoryScanStatesAsync(stateDb, ct);
        }
        else if (scanTargets.Count > 0)
        {
            logger.LogInformation("Directory scan cache bypassed because this scan requested a forced rescan or generated assets.");
        }

        var directoryScanContext = new DirectoryScanContext(
            directorySkippingEnabled,
            scanStartedAt,
            directoryScanSignature,
            directoryScanStates);

        if (scanTargets.Count == 0)
        {
            logger.LogWarning("No cove paths configured. Nothing to scan.");
            return new ScanDiscoveryResult([], scanTargets, extensions, directoryScanContext);
        }

        var scanStopwatch = Stopwatch.StartNew();
        progress.Report(0, "Discovering files...");
        var files = new List<DiscoveredFile>();
        var discoveryProgress = new ScanDiscoveryProgress(progress, logger);
        var ignoreRuleCache = new Dictionary<string, List<IgnoreRule>>(FilesystemPaths.PathComparer);
        var configuredPatterns = new ConfiguredScanPatternMatcher(config);

        foreach (var scanTarget in scanTargets)
        {
            if (scanTarget.IsFile)
            {
                DiscoverFileTarget(scanTarget, extensions, configuredPatterns, ignoreRuleCache, discoveryProgress, files);
                continue;
            }

            if (!Directory.Exists(scanTarget.Path))
            {
                logger.LogWarning("Scan target does not exist: {Path}", scanTarget.Path);
                continue;
            }

            files.AddRange(DiscoverFilesSafely(
                scanTarget,
                extensions,
                configuredPatterns,
                ignoreRuleCache,
                discoveryProgress,
                directoryScanContext,
                ct));
        }

        discoveryProgress.Complete();

        logger.LogInformation(
            "Scan phase discovery completed in {ElapsedMs} ms. Discovered {FileCount} media files across {DirectoryCount} directories; skipped file enumeration in {UnchangedDirectoryCount} verified unchanged directories, {IgnoredPathCount} ignored paths, and {UnsupportedFileCount} unsupported files.",
            scanStopwatch.ElapsedMilliseconds,
            files.Count,
            discoveryProgress.DirectoryCount,
            discoveryProgress.UnchangedDirectoryCount,
            discoveryProgress.IgnoredPathCount,
            discoveryProgress.UnsupportedFileCount);

        // Overlapping roots may surface the same physical file more than once. Stable path ordering
        // also keeps parallel workers reading nearby directories together.
        if (files.Count > 0)
        {
            var beforeDedup = files.Count;
            files = files
                .GroupBy(file => file.StoredPath, FilesystemPaths.PathComparer)
                .Select(group => group.First())
                .OrderBy(file => file.StoredPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (files.Count != beforeDedup)
                logger.LogInformation("Scan de-duplicated {DuplicateCount} discovered file path(s).", beforeDedup - files.Count);
        }

        return new ScanDiscoveryResult(files, scanTargets, extensions, directoryScanContext);
    }

    public async Task PersistDirectoryScanStatesAsync(DirectoryScanContext context, CancellationToken ct)
    {
        var observations = context.Observations
            .Where(observation => observation.FullyEnumerated
                || (observation.Skipped && observation.RequiresConfirmation))
            .ToList();
        if (observations.Count == 0)
            return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
        if (db.Database.IsRelational())
            db.Database.SetCommandTimeout(ScanCommandTimeout);

        var foldersByPath = new Dictionary<string, Folder>(FilesystemPaths.PathComparer);
        var knownIds = observations
            .Select(observation => observation.FolderId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        foreach (var chunk in knownIds.Chunk(1000))
        {
            var folders = await db.Folders.Where(folder => chunk.Contains(folder.Id)).ToListAsync(ct);
            foreach (var folder in folders)
            {
                var canonicalPath = ScanPath.TryCanonicalizeStoredFolderPath(folder.Path);
                if (canonicalPath != null)
                    foldersByPath.TryAdd(canonicalPath, folder);
            }
        }

        var missingPaths = observations
            .Select(observation => observation.StoredPath)
            .Where(path => !foldersByPath.ContainsKey(path))
            .Distinct(FilesystemPaths.PathComparer)
            .ToList();
        foreach (var chunk in missingPaths.Chunk(1000))
        {
            var folders = await db.Folders.Where(folder => chunk.Contains(folder.Path)).ToListAsync(ct);
            foreach (var folder in folders)
            {
                var canonicalPath = ScanPath.TryCanonicalizeStoredFolderPath(folder.Path);
                if (canonicalPath != null)
                    foldersByPath.TryAdd(canonicalPath, folder);
            }
        }

        var verifiedCount = 0;
        var dirtyCount = 0;
        foreach (var observation in observations)
        {
            if (!foldersByPath.TryGetValue(observation.StoredPath, out var folder))
                continue;

            var currentMetadata = ScanDirectoryMetadata.TryRead(observation.FilesystemPath);
            var currentModTime = currentMetadata?.ModTime ?? observation.ObservedModTime;
            var canVerify = observation.FullyEnumerated
                && !observation.RequiresConfirmation
                && !observation.BlocksCache
                && observation.ObservedModTime.HasValue
                && currentMetadata != null
                && !currentMetadata.IsReparsePoint
                && currentMetadata.ModTime == observation.ObservedModTime.Value
                && currentMetadata.ModTime <= context.ScanStartedAt - ScanDiscoveryPolicy.DirectoryModTimeSafetyWindow;

            if (currentModTime.HasValue)
                folder.ModTime = currentModTime.Value;
            folder.ScanSignature = context.Signature;
            folder.ScanVerifiedAt = canVerify ? context.ScanStartedAt : null;
            if (canVerify)
                verifiedCount++;
            else
                dirtyCount++;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Directory scan state updated: {VerifiedCount} verified unchanged, {DirtyCount} left pending confirmation.",
            verifiedCount,
            dirtyCount);
    }

    internal static bool IsMediaTypeExcludedByScanTarget(
        string extension,
        bool excludeVideo,
        bool excludeImage,
        bool excludeAudio,
        bool excludeText,
        IReadOnlySet<string> videoExts,
        IReadOnlySet<string> imageExts,
        IReadOnlySet<string> galleryExts,
        IReadOnlySet<string> audioExts,
        IReadOnlySet<string> textExts)
    {
        return (excludeVideo && videoExts.Contains(extension))
            || (excludeImage && (imageExts.Contains(extension) || galleryExts.Contains(extension)))
            || (excludeAudio && audioExts.Contains(extension))
            || (excludeText && textExts.Contains(extension));
    }

    private void DiscoverFileTarget(
        ScanTarget scanTarget,
        ScanExtensionCatalog extensions,
        ConfiguredScanPatternMatcher configuredPatterns,
        Dictionary<string, List<IgnoreRule>> ignoreRuleCache,
        ScanDiscoveryProgress discoveryProgress,
        List<DiscoveredFile> files)
    {
        if (!File.Exists(scanTarget.Path))
        {
            logger.LogWarning("Scan target does not exist: {Path}", scanTarget.Path);
            return;
        }

        var relativePath = Path.GetRelativePath(scanTarget.PatternRoot, scanTarget.Path);
        if (configuredPatterns.IsGloballyExcluded(scanTarget.Path, relativePath))
            return;

        var extension = Path.GetExtension(scanTarget.Path);
        if (!extensions.All.Contains(extension)
            || IsMediaTypeExcludedByScanTarget(
                extension,
                scanTarget.ExcludeVideo,
                scanTarget.ExcludeImage,
                scanTarget.ExcludeAudio,
                scanTarget.ExcludeText,
                extensions.Video,
                extensions.Image,
                extensions.Gallery,
                extensions.Audio,
                extensions.Text)
            || configuredPatterns.IsMediaTypeExcluded(
                scanTarget.Path,
                relativePath,
                extension,
                extensions.Image,
                extensions.Gallery)
            || IsExcludedByFolderIgnore(
                scanTarget.Path,
                Path.GetDirectoryName(scanTarget.Path) ?? scanTarget.Path,
                ignoreRuleCache))
        {
            return;
        }

        if (TryCreateDiscoveredFile(scanTarget.Path, extension, out var discoveredFile))
        {
            files.Add(discoveredFile);
            discoveryProgress.RecordMediaFile(discoveredFile.Path);
        }
    }

    private bool TryCreateDiscoveredFile(string path, string extension, out DiscoveredFile discoveredFile)
    {
        try
        {
            var normalizedPath = ScanPath.Normalize(path);
            discoveredFile = new DiscoveredFile(
                normalizedPath,
                ScanPath.NormalizeStoredFilePath(normalizedPath),
                extension,
                ScanPath.GetFileStat(normalizedPath));
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or FileNotFoundException or DirectoryNotFoundException)
        {
            logger.LogWarning(ex, "Skipping unreadable scan file: {Path}", path);
            discoveredFile = null!;
            return false;
        }
    }

    private IEnumerable<DiscoveredFile> DiscoverFilesSafely(
        ScanTarget scanTarget,
        ScanExtensionCatalog extensions,
        ConfiguredScanPatternMatcher configuredPatterns,
        Dictionary<string, List<IgnoreRule>> ruleCache,
        ScanDiscoveryProgress discoveryProgress,
        DirectoryScanContext directoryScanContext,
        CancellationToken ct)
    {
        var pending = new Stack<DirectoryScanFrame>();
        pending.Push(CreateDirectoryScanFrame(scanTarget.Path, [], inheritedIgnoreFileInScope: false));

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var frame = pending.Pop();
            var directory = frame.Path;
            discoveryProgress.RecordDirectory(directory);
            var observation = directoryScanContext.ObserveDirectory(
                directory,
                frame.HasIgnoreFileInScope || frame.HasLocalGalleryControlFile);

            List<FileSystemInfo> entries;
            try
            {
                var directoryInfo = new DirectoryInfo(directory);
                var enumerationOptions = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false };
                if (directoryScanContext.CanSkipFileEnumeration(observation))
                {
                    entries = directoryInfo
                        .EnumerateDirectories("*", enumerationOptions)
                        .Cast<FileSystemInfo>()
                        .ToList();
                    observation.MarkSkipped();
                    discoveryProgress.RecordUnchangedDirectory();
                }
                else
                {
                    entries = directoryInfo
                        .EnumerateFileSystemInfos("*", enumerationOptions)
                        .ToList();
                    observation.MarkFullyEnumerated();
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
            {
                observation.MarkRequiresConfirmation();
                discoveryProgress.RecordUnreadablePath(directory);
                logger.LogWarning(ex, "Skipping unreadable scan directory: {Path}", directory);
                continue;
            }

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();

                var path = entry.FullName;
                FileAttributes attributes;
                try
                {
                    attributes = entry.Attributes;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
                {
                    observation.MarkRequiresConfirmation();
                    discoveryProgress.RecordUnreadablePath(path);
                    logger.LogWarning(ex, "Skipping unreadable scan path: {Path}", path);
                    continue;
                }

                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                if (isDirectory)
                {
                    if ((attributes & FileAttributes.ReparsePoint) != 0
                        || IsExcludedByActiveIgnoreRules(path, frame.IgnoreRuleSets, isDirectory: true))
                    {
                        discoveryProgress.RecordIgnoredPath(path);
                        continue;
                    }

                    pending.Push(CreateDirectoryScanFrame(path, frame.IgnoreRuleSets, frame.HasIgnoreFileInScope));
                    continue;
                }

                // A link target can change without changing this directory entry's timestamp.
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    observation.MarkBlocksCache();

                var relativePath = Path.GetRelativePath(scanTarget.PatternRoot, path);
                if (configuredPatterns.IsGloballyExcluded(path, relativePath))
                {
                    discoveryProgress.RecordIgnoredPath(path);
                    continue;
                }

                var extension = Path.GetExtension(path);
                if (!extensions.All.Contains(extension))
                {
                    discoveryProgress.RecordUnsupportedFile();
                    continue;
                }

                if (IsMediaTypeExcludedByScanTarget(
                    extension,
                    scanTarget.ExcludeVideo,
                    scanTarget.ExcludeImage,
                    scanTarget.ExcludeAudio,
                    scanTarget.ExcludeText,
                    extensions.Video,
                    extensions.Image,
                    extensions.Gallery,
                    extensions.Audio,
                    extensions.Text))
                {
                    discoveryProgress.RecordIgnoredPath(path);
                    continue;
                }

                if (configuredPatterns.IsMediaTypeExcluded(
                        path,
                        relativePath,
                        extension,
                        extensions.Image,
                        extensions.Gallery)
                    || IsExcludedByActiveIgnoreRules(path, frame.IgnoreRuleSets))
                {
                    discoveryProgress.RecordIgnoredPath(path);
                    continue;
                }

                if (entry is not FileInfo fileInfo)
                {
                    discoveryProgress.RecordUnsupportedFile();
                    continue;
                }

                DiscoveredFile discoveredFile;
                try
                {
                    var normalizedPath = ScanPath.Normalize(path);
                    discoveredFile = new DiscoveredFile(
                        normalizedPath,
                        ScanPath.NormalizeStoredFilePath(normalizedPath),
                        extension,
                        new FileStat(
                            fileInfo.Length,
                            ScanPath.NormalizeFileModTime(fileInfo.LastWriteTimeUtc),
                            fileInfo.LastWriteTimeUtc));
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or FileNotFoundException or DirectoryNotFoundException)
                {
                    observation.MarkRequiresConfirmation();
                    discoveryProgress.RecordUnreadablePath(path);
                    logger.LogWarning(ex, "Skipping unreadable scan file: {Path}", path);
                    continue;
                }

                discoveryProgress.RecordMediaFile(discoveredFile.Path);
                yield return discoveredFile;
            }
        }

        DirectoryScanFrame CreateDirectoryScanFrame(
            string directory,
            IReadOnlyList<ActiveIgnoreRuleSet> inheritedRuleSets,
            bool inheritedIgnoreFileInScope)
        {
            var normalizedDirectory = ScanPath.Normalize(directory);
            var hasLocalIgnoreFile = FolderIgnoreFileNames.Any(fileName => File.Exists(Path.Combine(normalizedDirectory, fileName)));
            var hasIgnoreFileInScope = inheritedIgnoreFileInScope || hasLocalIgnoreFile;
            var hasLocalGalleryControlFile = File.Exists(Path.Combine(normalizedDirectory, ".forcegallery"))
                || File.Exists(Path.Combine(normalizedDirectory, ".nogallery"));
            var rules = GetIgnoreRules(normalizedDirectory, ruleCache);
            if (rules.Count == 0)
                return new DirectoryScanFrame(directory, inheritedRuleSets, hasIgnoreFileInScope, hasLocalGalleryControlFile);

            var ruleSets = new List<ActiveIgnoreRuleSet>(inheritedRuleSets.Count + 1);
            ruleSets.AddRange(inheritedRuleSets);
            ruleSets.Add(new ActiveIgnoreRuleSet(normalizedDirectory, rules));
            return new DirectoryScanFrame(directory, ruleSets, hasIgnoreFileInScope, hasLocalGalleryControlFile);
        }
    }

    private static bool RequiresFullFileDiscovery(ScanOperationOptions options)
    {
        return options.Rescan
            || options.IncludeUnchangedFilesInAssetGeneration
            || options.GenerateCovers
            || options.GeneratePreviews
            || options.GenerateSprites
            || options.GeneratePhashes
            || options.GenerateMd5
            || options.GenerateImageThumbnails
            || options.GenerateImagePhashes
            || options.GenerateAudioPhashes
            || options.GenerateTextPhashes;
    }

    private static string ComputeDirectoryScanSignature(
        CoveConfiguration cfg,
        IReadOnlyCollection<ScanTarget> scanTargets)
    {
        var value = new StringBuilder();
        value.Append("version=").Append(DirectoryScanSignatureVersion).Append('\n');
        AppendValues("video", cfg.VideoExtensions);
        AppendValues("image", cfg.ImageExtensions);
        AppendValues("gallery", cfg.GalleryExtensions);
        AppendValues("audio", cfg.AudioExtensions);
        AppendValues("text", cfg.TextExtensions);
        AppendValues("exclude", cfg.ExcludePatterns);
        AppendValues("exclude-image", cfg.ExcludeImagePatterns);
        AppendValues("exclude-gallery", cfg.ExcludeGalleryPatterns);
        value.Append("create-folder-galleries=").Append(cfg.CreateGalleriesFromFolders).Append('\n');

        foreach (var path in cfg.CovePaths
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .ThenBy(item => item.ExcludeVideo)
            .ThenBy(item => item.ExcludeImage)
            .ThenBy(item => item.ExcludeAudio)
            .ThenBy(item => item.ExcludeText))
        {
            value.Append("root=")
                .Append(path.Path.Replace('\\', '/'))
                .Append('|').Append(path.ExcludeVideo)
                .Append('|').Append(path.ExcludeImage)
                .Append('|').Append(path.ExcludeAudio)
                .Append('|').Append(path.ExcludeText)
                .Append('\n');
        }

        // Selective checkpoints are scoped to the exact targets that established them so they can
        // never hide files from a later, broader scan.
        foreach (var target in scanTargets
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .ThenBy(item => item.ExcludeVideo)
            .ThenBy(item => item.ExcludeImage)
            .ThenBy(item => item.ExcludeAudio)
            .ThenBy(item => item.ExcludeText)
            .ThenBy(item => item.IsFile))
        {
            value.Append("target=")
                .Append(target.Path.Replace('\\', '/'))
                .Append('|').Append(target.ExcludeVideo)
                .Append('|').Append(target.ExcludeImage)
                .Append('|').Append(target.ExcludeAudio)
                .Append('|').Append(target.ExcludeText)
                .Append('|').Append(target.IsFile)
                .Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString()))).ToLowerInvariant();

        void AppendValues(string name, IEnumerable<string> values)
        {
            foreach (var item in values
                .Select(item => item.Trim().ToLowerInvariant())
                .Where(item => item.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal))
            {
                value.Append(name).Append('=').Append(item).Append('\n');
            }
        }
    }

    private static async Task<Dictionary<string, DirectoryScanState>> LoadDirectoryScanStatesAsync(
        CoveContext db,
        CancellationToken ct)
    {
        var persistedRows = await db.Folders
            .AsNoTracking()
            .Where(folder => folder.ZipFileId == null)
            .Select(folder => new
            {
                folder.Id,
                folder.Path,
                folder.ModTime,
                folder.ScanVerifiedAt,
                folder.ScanSignature,
            })
            .ToListAsync(ct);

        var result = new Dictionary<string, DirectoryScanState>(FilesystemPaths.PathComparer);
        foreach (var persisted in persistedRows)
        {
            var row = new DirectoryScanState(
                persisted.Id,
                persisted.Path,
                ScanDirectoryMetadata.NormalizeModTime(persisted.ModTime),
                persisted.ScanVerifiedAt,
                persisted.ScanSignature);
            var canonicalPath = ScanPath.TryCanonicalizeStoredFolderPath(row.StoredPath);
            if (canonicalPath == null)
                continue;

            if (!result.TryGetValue(canonicalPath, out var existing)
                || (row.ScanVerifiedAt ?? DateTime.MinValue) > (existing.ScanVerifiedAt ?? DateTime.MinValue))
            {
                result[canonicalPath] = row with { StoredPath = canonicalPath };
            }
        }

        return result;
    }

    private static bool IsExcludedByActiveIgnoreRules(
        string path,
        IReadOnlyList<ActiveIgnoreRuleSet> ruleSets,
        bool isDirectory = false)
    {
        if (ruleSets.Count == 0)
            return false;

        var fullPath = ScanPath.Normalize(path);
        var fileName = Path.GetFileName(fullPath);
        var ignored = false;

        foreach (var ruleSet in ruleSets)
        {
            var relativePath = Path.GetRelativePath(ruleSet.Directory, fullPath).Replace('\\', '/');
            if (isDirectory && !relativePath.EndsWith('/'))
                relativePath += "/";
            foreach (var rule in ruleSet.Rules)
            {
                if (IgnoreRuleMatches(rule.Pattern, relativePath, fileName))
                    ignored = !rule.Negated;
            }
        }

        return ignored;
    }

    private static bool IsExcludedByFolderIgnore(
        string path,
        string rootPath,
        Dictionary<string, List<IgnoreRule>> ruleCache)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            return false;

        var fullPath = ScanPath.Normalize(path);
        var root = ScanPath.Normalize(rootPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
            return false;

        var ancestors = new Stack<string>();
        for (var current = ScanPath.Normalize(directory);
            !string.IsNullOrWhiteSpace(current) && ScanPath.IsWithin(current, root);
            current = Path.GetDirectoryName(current))
        {
            ancestors.Push(current);
        }

        var ignored = false;
        while (ancestors.Count > 0)
        {
            var ruleDirectory = ancestors.Pop();
            foreach (var rule in GetIgnoreRules(ruleDirectory, ruleCache))
            {
                var relativePath = Path.GetRelativePath(ruleDirectory, fullPath).Replace('\\', '/');
                if (IgnoreRuleMatches(rule.Pattern, relativePath, Path.GetFileName(fullPath)))
                    ignored = !rule.Negated;
            }
        }

        return ignored;
    }

    private static List<IgnoreRule> GetIgnoreRules(
        string directory,
        Dictionary<string, List<IgnoreRule>> ruleCache)
    {
        if (ruleCache.TryGetValue(directory, out var cached))
            return cached;

        var rules = new List<IgnoreRule>();
        foreach (var fileName in FolderIgnoreFileNames)
        {
            var ignoreFile = Path.Combine(directory, fileName);
            if (!File.Exists(ignoreFile))
                continue;

            foreach (var line in File.ReadLines(ignoreFile))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                    continue;

                var negated = trimmed.StartsWith('!');
                var pattern = (negated ? trimmed[1..] : trimmed).Trim().Replace('\\', '/');
                if (pattern.Length > 0)
                    rules.Add(new IgnoreRule(pattern, negated));
            }
        }

        ruleCache[directory] = rules;
        return rules;
    }

    private static bool IgnoreRuleMatches(string pattern, string relativePath, string fileName)
    {
        var normalizedPattern = pattern.TrimStart('/');
        var directoryPattern = normalizedPattern.EndsWith('/');
        if (directoryPattern)
            normalizedPattern = normalizedPattern.TrimEnd('/');

        if (normalizedPattern.Length == 0)
            return false;

        if (directoryPattern)
        {
            return relativePath.StartsWith(normalizedPattern + '/', StringComparison.OrdinalIgnoreCase)
                || relativePath.Contains('/' + normalizedPattern + '/', StringComparison.OrdinalIgnoreCase);
        }

        if (normalizedPattern.Contains('/'))
            return FileSystemName.MatchesSimpleExpression(normalizedPattern, relativePath, ignoreCase: true);

        return FileSystemName.MatchesSimpleExpression(normalizedPattern, fileName, ignoreCase: true)
            || relativePath.Split('/').Any(segment => FileSystemName.MatchesSimpleExpression(normalizedPattern, segment, ignoreCase: true));
    }

    private static List<ScanTarget> ResolveScanTargets(CoveConfiguration cfg, List<string>? selectedPaths)
    {
        if (selectedPaths == null)
        {
            return cfg.CovePaths
                .Select(path => new ScanTarget(
                    ScanPath.Normalize(path.Path),
                    path.ExcludeVideo,
                    path.ExcludeImage,
                    path.ExcludeAudio,
                    path.ExcludeText,
                    false,
                    ScanPath.Normalize(path.Path)))
                .ToList();
        }

        var targets = new List<ScanTarget>();
        foreach (var selectedPath in selectedPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(ScanPath.Normalize)
            .Distinct(FilesystemPaths.PathComparer))
        {
            var matchingConfig = cfg.CovePaths
                .Select(path => new { Config = path, NormalizedPath = ScanPath.Normalize(path.Path) })
                .Where(item => ScanPath.IsWithin(selectedPath, item.NormalizedPath))
                .OrderByDescending(item => item.NormalizedPath.Length)
                .FirstOrDefault();

            if (matchingConfig == null)
                continue;

            var isFile = File.Exists(selectedPath);
            if (!isFile && !Directory.Exists(selectedPath))
                continue;

            targets.Add(new ScanTarget(
                selectedPath,
                matchingConfig.Config.ExcludeVideo,
                matchingConfig.Config.ExcludeImage,
                matchingConfig.Config.ExcludeAudio,
                matchingConfig.Config.ExcludeText,
                isFile,
                matchingConfig.NormalizedPath));
        }

        return targets;
    }

    private sealed class ScanDiscoveryProgress(
        IJobProgress progress,
        ILogger logger)
    {
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private DateTime _lastUiReport = DateTime.MinValue;
        private DateTime _lastLogReport = DateTime.MinValue;

        public int DirectoryCount { get; private set; }
        public int UnchangedDirectoryCount { get; private set; }
        public int MediaFileCount { get; private set; }
        public int UnsupportedFileCount { get; private set; }
        public int IgnoredPathCount { get; private set; }
        public int UnreadablePathCount { get; private set; }

        public void RecordDirectory(string path)
        {
            DirectoryCount++;
            ReportIfDue(path);
        }

        public void RecordUnchangedDirectory() => UnchangedDirectoryCount++;

        public void RecordMediaFile(string path)
        {
            MediaFileCount++;
            ReportIfDue(path);
        }

        public void RecordUnsupportedFile() => UnsupportedFileCount++;

        public void RecordIgnoredPath(string path)
        {
            IgnoredPathCount++;
            ReportIfDue(path);
        }

        public void RecordUnreadablePath(string path)
        {
            UnreadablePathCount++;
            ReportIfDue(path);
        }

        public void Complete()
        {
            var message = $"Discovered {MediaFileCount:N0} media files in {DirectoryCount:N0} folders.";
            if (UnchangedDirectoryCount > 0)
                message += $" {UnchangedDirectoryCount:N0} unchanged {(UnchangedDirectoryCount == 1 ? "folder" : "folders")} skipped.";
            progress.Report(0.10, message);
        }

        private void ReportIfDue(string? path)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastUiReport).TotalSeconds >= 1)
            {
                _lastUiReport = now;
                progress.Report(0.05, BuildMessage(path));
            }

            if ((now - _lastLogReport).TotalSeconds >= 10)
            {
                _lastLogReport = now;
                logger.LogDebug(
                    "Scan discovery progress after {ElapsedMs} ms: {MediaFileCount} media files, {DirectoryCount} directories, {UnchangedDirectoryCount} verified unchanged directories skipped, {IgnoredPathCount} ignored, {UnsupportedFileCount} unsupported, {UnreadablePathCount} unreadable. Current path: {Path}",
                    _elapsed.ElapsedMilliseconds,
                    MediaFileCount,
                    DirectoryCount,
                    UnchangedDirectoryCount,
                    IgnoredPathCount,
                    UnsupportedFileCount,
                    UnreadablePathCount,
                    path);
            }
        }

        private string BuildMessage(string? path)
        {
            var message = $"Discovering files: {MediaFileCount:N0} media files, {DirectoryCount:N0} folders";
            if (UnchangedDirectoryCount > 0)
                message += $", {UnchangedDirectoryCount:N0} unchanged skipped";
            if (IgnoredPathCount > 0)
                message += $", {IgnoredPathCount:N0} ignored";
            if (!string.IsNullOrWhiteSpace(path))
                message += $": {Path.GetFileName(path)}";
            return message;
        }
    }
}

internal sealed record ScanDiscoveryResult(
    List<DiscoveredFile> Files,
    IReadOnlyList<ScanTarget> Targets,
    ScanExtensionCatalog Extensions,
    DirectoryScanContext DirectoryScanContext)
{
    public bool HasForceGalleryHints => Files
        .Select(file => Path.GetDirectoryName(file.Path))
        .Where(directory => !string.IsNullOrWhiteSpace(directory))
        .Distinct(FilesystemPaths.PathComparer)
        .Any(directory => File.Exists(Path.Combine(directory!, ".forcegallery")));
}

internal sealed record ScanExtensionCatalog(
    HashSet<string> Video,
    HashSet<string> Image,
    HashSet<string> Gallery,
    HashSet<string> Audio,
    HashSet<string> Text,
    HashSet<string> All)
{
    public static ScanExtensionCatalog From(CoveConfiguration config)
    {
        var video = new HashSet<string>(config.VideoExtensions, StringComparer.OrdinalIgnoreCase);
        var image = new HashSet<string>(config.ImageExtensions, StringComparer.OrdinalIgnoreCase);
        var gallery = new HashSet<string>(config.GalleryExtensions, StringComparer.OrdinalIgnoreCase);
        var audio = new HashSet<string>(config.AudioExtensions, StringComparer.OrdinalIgnoreCase);
        var text = new HashSet<string>(config.TextExtensions, StringComparer.OrdinalIgnoreCase);
        var all = video.Union(image).Union(gallery).Union(audio).Union(text).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new ScanExtensionCatalog(video, image, gallery, audio, text, all);
    }
}

internal sealed class ConfiguredScanPatternMatcher
{
    private readonly ScanPatternSet _global;
    private readonly ScanPatternSet _images;
    private readonly ScanPatternSet _galleries;

    public ConfiguredScanPatternMatcher(CoveConfiguration config)
    {
        _global = new ScanPatternSet(config.ExcludePatterns);
        _images = new ScanPatternSet(config.ExcludeImagePatterns);
        _galleries = new ScanPatternSet(config.ExcludeGalleryPatterns);
    }

    public bool IsGloballyExcluded(string fullPath, string relativePath) => _global.IsMatch(fullPath, relativePath);

    public bool IsMediaTypeExcluded(
        string fullPath,
        string relativePath,
        string extension,
        IReadOnlySet<string> imageExts,
        IReadOnlySet<string> galleryExts)
    {
        return (imageExts.Contains(extension) && _images.IsMatch(fullPath, relativePath))
            || (galleryExts.Contains(extension) && _galleries.IsMatch(fullPath, relativePath));
    }

    private sealed class ScanPatternSet
    {
        private readonly string[] _literalFragments;
        private readonly Regex[] _globPatterns;

        public ScanPatternSet(IEnumerable<string> patterns)
        {
            var literals = new List<string>();
            var globs = new List<Regex>();

            foreach (var value in patterns)
            {
                var pattern = value.Trim().Replace('\\', '/');
                if (pattern.Length == 0)
                    continue;

                if (!pattern.Contains('*') && !pattern.Contains('?'))
                {
                    literals.Add(pattern);
                    continue;
                }

                var normalizedPattern = pattern.TrimStart('/');
                if (!normalizedPattern.Contains('/'))
                    normalizedPattern = $"**/{normalizedPattern}";
                globs.Add(CompileGlob(normalizedPattern));
            }

            _literalFragments = [.. literals];
            _globPatterns = [.. globs];
        }

        public bool IsMatch(string fullPath, string relativePath)
        {
            var normalizedFullPath = fullPath.Replace('\\', '/');
            if (_literalFragments.Any(pattern => normalizedFullPath.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
                return true;

            var normalizedRelativePath = relativePath.Replace('\\', '/').TrimStart('/');
            return _globPatterns.Any(pattern => pattern.IsMatch(normalizedRelativePath));
        }

        private static Regex CompileGlob(string pattern)
        {
            var expression = new StringBuilder("^");
            for (var index = 0; index < pattern.Length; index++)
            {
                var character = pattern[index];
                if (character == '*' && index + 1 < pattern.Length && pattern[index + 1] == '*')
                {
                    index++;
                    if (index + 1 < pattern.Length && pattern[index + 1] == '/')
                    {
                        expression.Append("(?:.*/)?");
                        index++;
                    }
                    else
                    {
                        expression.Append(".*");
                    }
                }
                else if (character == '*')
                {
                    expression.Append("[^/]*");
                }
                else if (character == '?')
                {
                    expression.Append("[^/]");
                }
                else
                {
                    expression.Append(Regex.Escape(character.ToString()));
                }
            }

            expression.Append('$');
            return new Regex(
                expression.ToString(),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        }
    }
}

internal static class ScanDiscoveryPolicy
{
    public static readonly TimeSpan DirectoryModTimeSafetyWindow = TimeSpan.FromSeconds(5);
}

internal static class ScanPath
{
    public static string Normalize(string path) => Path.GetFullPath(path);

    public static FileStat GetFileStat(string path)
    {
        var fileInfo = new FileInfo(path);
        return new FileStat(
            fileInfo.Length,
            NormalizeFileModTime(fileInfo.LastWriteTimeUtc),
            fileInfo.LastWriteTimeUtc);
    }

    public static DateTime NormalizeFileModTime(DateTime modTime)
    {
        var utc = modTime.Kind == DateTimeKind.Utc ? modTime : modTime.ToUniversalTime();
        return new DateTime(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
    }

    public static string NormalizeStoredFolderPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        var normalized = !string.IsNullOrEmpty(root) && string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return normalized.Replace('\\', '/');
    }

    public static string NormalizeStoredFilePath(string path)
    {
        var dirPath = Path.GetDirectoryName(path) ?? string.Empty;
        var basename = Path.GetFileName(path);
        return BaseFileEntity.ComputePath(NormalizeStoredFolderPath(dirPath), basename);
    }

    public static string? TryCanonicalizeStoredFolderPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            return NormalizeStoredFolderPath(path);
        }
        catch
        {
            return null;
        }
    }

    public static string? GetParentStoredFolderPath(string storedPath)
    {
        var nativePath = storedPath.Replace('/', Path.DirectorySeparatorChar);
        var parentPath = Path.GetDirectoryName(nativePath);
        return string.IsNullOrWhiteSpace(parentPath) ? null : NormalizeStoredFolderPath(parentPath);
    }

    public static bool IsWithin(string path, string root)
    {
        if (path.Equals(root, FilesystemPaths.PathComparison))
            return true;

        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, FilesystemPaths.PathComparison)
            || path.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, FilesystemPaths.PathComparison);
    }
}

internal sealed class DirectoryScanContext(
    bool enabled,
    DateTime scanStartedAt,
    string signature,
    IReadOnlyDictionary<string, DirectoryScanState> states)
{
    private readonly ConcurrentDictionary<string, DirectoryScanObservation> _observations =
        new(FilesystemPaths.PathComparer);

    public bool Enabled { get; } = enabled;
    public DateTime ScanStartedAt { get; } = scanStartedAt;
    public string Signature { get; } = signature;
    public IEnumerable<DirectoryScanObservation> Observations => _observations.Values;

    public DirectoryScanObservation ObserveDirectory(string filesystemPath, bool blocksCache)
    {
        var storedPath = ScanPath.NormalizeStoredFolderPath(filesystemPath);
        states.TryGetValue(storedPath, out var state);
        var metadata = ScanDirectoryMetadata.TryRead(filesystemPath);
        var observation = _observations.GetOrAdd(storedPath, _ => new DirectoryScanObservation(
            filesystemPath,
            storedPath,
            state?.FolderId,
            metadata?.ModTime,
            metadata?.IsReparsePoint == true));

        if (blocksCache)
            observation.MarkBlocksCache();
        if (metadata == null || (state != null && state.ModTime != metadata.ModTime))
            observation.MarkRequiresConfirmation();

        return observation;
    }

    public bool CanSkipFileEnumeration(DirectoryScanObservation observation)
    {
        if (!Enabled
            || observation.BlocksCache
            || observation.RequiresConfirmation
            || observation.IsReparsePoint
            || !observation.ObservedModTime.HasValue
            || !states.TryGetValue(observation.StoredPath, out var state)
            || !state.ScanVerifiedAt.HasValue
            || !string.Equals(state.ScanSignature, Signature, StringComparison.Ordinal)
            || state.ModTime != observation.ObservedModTime.Value)
        {
            return false;
        }

        return observation.ObservedModTime.Value
            <= state.ScanVerifiedAt.Value - ScanDiscoveryPolicy.DirectoryModTimeSafetyWindow;
    }

    public void MarkRequiresConfirmation(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(directory))
            return;

        var storedPath = ScanPath.NormalizeStoredFolderPath(directory);
        if (_observations.TryGetValue(storedPath, out var observation))
            observation.MarkRequiresConfirmation();
    }

}

internal static class ScanDirectoryMetadata
{
    public static DirectoryMetadata? TryRead(string directory)
    {
        try
        {
            var info = new DirectoryInfo(directory);
            info.Refresh();
            if (!info.Exists)
                return null;

            return new DirectoryMetadata(
                NormalizeModTime(info.LastWriteTimeUtc),
                (info.Attributes & FileAttributes.ReparsePoint) != 0);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public static DateTime NormalizeModTime(DateTime modTime)
    {
        var utc = modTime.Kind == DateTimeKind.Utc ? modTime : modTime.ToUniversalTime();
        // PostgreSQL timestamps have microsecond precision. Match it so a same-second directory
        // change still invalidates its checkpoint after round-tripping.
        return new DateTime(utc.Ticks - (utc.Ticks % 10), DateTimeKind.Utc);
    }
}

internal sealed class DirectoryScanObservation(
    string filesystemPath,
    string storedPath,
    int? folderId,
    DateTime? observedModTime,
    bool isReparsePoint)
{
    private int _blocksCache;
    private int _fullyEnumerated;
    private int _requiresConfirmation;
    private int _skipped;

    public string FilesystemPath { get; } = filesystemPath;
    public string StoredPath { get; } = storedPath;
    public int? FolderId { get; } = folderId;
    public DateTime? ObservedModTime { get; } = observedModTime;
    public bool IsReparsePoint { get; } = isReparsePoint;
    public bool BlocksCache => Volatile.Read(ref _blocksCache) != 0;
    public bool FullyEnumerated => Volatile.Read(ref _fullyEnumerated) != 0;
    public bool RequiresConfirmation => Volatile.Read(ref _requiresConfirmation) != 0;
    public bool Skipped => Volatile.Read(ref _skipped) != 0;

    public void MarkBlocksCache() => Interlocked.Exchange(ref _blocksCache, 1);
    public void MarkFullyEnumerated() => Interlocked.Exchange(ref _fullyEnumerated, 1);
    public void MarkRequiresConfirmation() => Interlocked.Exchange(ref _requiresConfirmation, 1);
    public void MarkSkipped() => Interlocked.Exchange(ref _skipped, 1);
}

internal sealed record DirectoryScanState(
    int FolderId,
    string StoredPath,
    DateTime ModTime,
    DateTime? ScanVerifiedAt,
    string? ScanSignature);

internal sealed record DirectoryMetadata(DateTime ModTime, bool IsReparsePoint);

internal sealed record ActiveIgnoreRuleSet(string Directory, IReadOnlyList<IgnoreRule> Rules);

internal sealed record DirectoryScanFrame(
    string Path,
    IReadOnlyList<ActiveIgnoreRuleSet> IgnoreRuleSets,
    bool HasIgnoreFileInScope,
    bool HasLocalGalleryControlFile);

internal sealed record DiscoveredFile(string Path, string StoredPath, string Extension, FileStat Stat)
{
    public long Size => Stat.Size;
    public DateTime ModTime => Stat.ModTime;
    public DateTime ObservedModTime => Stat.ObservedModTime;
}

internal readonly record struct FileStat(long Size, DateTime ModTime, DateTime ObservedModTime);

internal sealed record IgnoreRule(string Pattern, bool Negated);

internal sealed record ScanTarget(
    string Path,
    bool ExcludeVideo,
    bool ExcludeImage,
    bool ExcludeAudio,
    bool ExcludeText,
    bool IsFile,
    string PatternRoot);
