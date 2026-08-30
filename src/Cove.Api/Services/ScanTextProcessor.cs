using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Cove.Core.Common;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;

namespace Cove.Api.Services;

internal sealed class ScanTextProcessor(
    CoveConfiguration config,
    IFingerprintService fingerprintService,
    TextExtractionService textExtractionService,
    ScanFolderResolver folderResolver,
    ScanFileIdentityService fileIdentity,
    ILogger logger)
{
    internal async Task<(TextDocument Entity, bool Relinked, bool Moved)> ProcessAsync(
        CoveContext db,
        string path,
        int? textDocumentId,
        CancellationToken ct,
        FileStat? fileStat = null,
        Dictionary<string, Folder>? folderCache = null,
        bool knownNew = false,
        int? parentFolderId = null,
        bool contentChanged = false,
        ScanOperationOptions? scanOptions = null,
        MoveDetectionIndex? moveIndex = null)
    {
        var stat = fileStat ?? ScanPath.GetFileStat(path);
        var dirPath = ScanPath.NormalizeStoredFolderPath(Path.GetDirectoryName(path) ?? path);
        var folderId = parentFolderId ?? (await folderResolver.EnsureAsync(db, dirPath, ct, folderCache)).Id;

        var basename = Path.GetFileName(path);
        var existing = knownNew
            ? null
            : await db.TextFiles
                .Include(file => file.Fingerprints)
                .Include(file => file.TextDocument)
                .ThenInclude(text => text!.Files)
                .FirstOrDefaultAsync(file => file.ParentFolderId == folderId && file.Basename == basename, ct);

        if (existing != null)
        {
            existing.Size = stat.Size;
            existing.ModTime = stat.ModTime;
            existing.Path = BaseFileEntity.ComputePath(dirPath, basename);

            var existingDocument = existing.TextDocument ?? throw new InvalidOperationException($"Text file {path} is not attached to a text document");
            await EnrichTextFileAsync(existingDocument, existing, path, ct, moveIndex);
            // A content change invalidates the stored phash; blank it so the generation phase recomputes it.
            if (contentChanged && scanOptions?.GenerateTextPhashes == true)
                ScanFileIdentityService.BlankFingerprint(existing, "phash");
            RefreshTextSummary(existingDocument);
            return (existingDocument, false, false);
        }

        // Content already in the library: re-link a moved text file, or attach a duplicate to its entity.
        if (!textDocumentId.HasValue && moveIndex is { Enabled: true })
        {
            var (match, isMove) = await fileIdentity.MatchExistingAsync(db.TextFiles, path, folderId, basename, stat, moveIndex, ct);
            if (match?.TextDocumentId is int matchedTextId)
            {
                var parentDocument = await db.TextDocuments.Include(item => item.Files).FirstOrDefaultAsync(item => item.Id == matchedTextId, ct);
                if (parentDocument != null)
                {
                    if (isMove)
                    {
                        logger.LogTrace("Re-linked moved text file to {NewPath} (previously {OldPath})", path, match.Path);
                        RefreshTextSummary(parentDocument);
                        return (parentDocument, true, true);
                    }

                    var duplicateFile = new TextFile
                    {
                        Basename = basename,
                        ParentFolderId = folderId,
                        Path = BaseFileEntity.ComputePath(dirPath, basename),
                        Size = stat.Size,
                        ModTime = stat.ModTime,
                        Format = Path.GetExtension(path).TrimStart('.').ToLowerInvariant(),
                    };
                    parentDocument.Files.Add(duplicateFile);
                    await EnrichTextFileAsync(parentDocument, duplicateFile, path, ct, moveIndex);
                    RefreshTextSummary(parentDocument);
                    logger.LogTrace("Attached duplicate text file {NewPath} to existing text document {TextId}", path, matchedTextId);
                    return (parentDocument, true, false);
                }
            }
        }

        var textFile = new TextFile
        {
            Basename = basename,
            ParentFolderId = folderId,
            Path = BaseFileEntity.ComputePath(dirPath, basename),
            Size = stat.Size,
            ModTime = stat.ModTime,
            Format = Path.GetExtension(path).TrimStart('.').ToLowerInvariant(),
        };

        TextDocument textDocument;
        if (textDocumentId.HasValue)
        {
            textDocument = await db.TextDocuments
                .Include(item => item.Files)
                .FirstOrDefaultAsync(item => item.Id == textDocumentId.Value, ct)
                ?? throw new InvalidOperationException($"Text document {textDocumentId.Value} was not found for downloaded media import");

            textDocument.Files.Add(textFile);
        }
        else
        {
            textDocument = new TextDocument
            {
                Title = Path.GetFileNameWithoutExtension(path),
                Files = [textFile],
            };

            db.TextDocuments.Add(textDocument);
        }

        await EnrichTextFileAsync(textDocument, textFile, path, ct, moveIndex);
        RefreshTextSummary(textDocument);

        logger.LogTrace("Added text document for {Path}", path);
        return (textDocument, false, false);
    }


    private async Task EnrichTextFileAsync(
        TextDocument textDocument,
        TextFile textFile,
        string path,
        CancellationToken ct,
        MoveDetectionIndex? moveIndex = null)
    {
        try
        {
            var metadata = await textExtractionService.ExtractMetadataAsync(path, ct);
            var fallbackTitle = Path.GetFileNameWithoutExtension(path);
            textFile.PageCount = metadata.PageCount;
            textFile.WordCount = metadata.WordCount;
            textFile.ExcerptText = metadata.ExcerptText;

            if (string.IsNullOrWhiteSpace(textDocument.Title) || string.Equals(textDocument.Title, fallbackTitle, StringComparison.OrdinalIgnoreCase))
                textDocument.Title = metadata.Title ?? fallbackTitle;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to extract text metadata for {Path}", path);
        }

        // Always-on identity fingerprint so a later scan can recognise this file if it moves/renames.
        var oshash = await ScanFileIdentityService.ComputeOshashAsync(path, moveIndex, ct);
        if (oshash != null)
            ScanFileIdentityService.UpsertFingerprint(textFile, "oshash", oshash);

        if (config.CalculateMd5)
        {
            var md5 = await fingerprintService.ComputeMd5Async(path, ct);
            if (!string.IsNullOrWhiteSpace(md5))
            {
                ScanFileIdentityService.UpsertFingerprint(textFile, "md5", md5);
            }
        }
    }


    private static void RefreshTextSummary(TextDocument textDocument)
    {
        var files = textDocument.Files.ToList();
        textDocument.FileCount = files.Count;
        if (files.Count == 0)
        {
            textDocument.MaxWordCount = null;
            textDocument.MaxPageCount = null;
            textDocument.MaxFileSize = 0;
            textDocument.MaxFileModTime = null;
            textDocument.MinPath = null;
            textDocument.MaxPath = null;
            textDocument.FileSearchText = null;
            return;
        }

        var paths = files
            .Select(file => string.IsNullOrWhiteSpace(file.Path) ? BaseFileEntity.ComputePath(file.ParentFolder?.Path, file.Basename) : file.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        textDocument.MaxWordCount = files.Max(file => file.WordCount);
        textDocument.MaxPageCount = files.Max(file => file.PageCount);
        textDocument.MaxFileSize = files.Max(file => file.Size);
        textDocument.MaxFileModTime = files.Max(file => (DateTime?)file.ModTime);
        textDocument.MinPath = paths.FirstOrDefault();
        textDocument.MaxPath = paths.LastOrDefault();
        textDocument.FileSearchText = ScanMediaSummary.BuildFileSearchText(paths);
    }
}
