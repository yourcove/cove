using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Cove.Core.Auth;
using Cove.Core.Common;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Data;
using Cove.Api.Services;
using System.Runtime.InteropServices;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/files")]
[RequiresPermission(Permissions.FilesRead)]
public class FileOpsController(
    CoveContext db,
    IEventBus eventBus,
    ILogger<FileOpsController> logger,
    IFileManagerLauncher? fileManagerLauncher = null,
    PhysicalFileAccessCoordinator? physicalFileCoordinator = null,
    PhysicalFileDeletionRecoverySignal? physicalFileDeletionRecoverySignal = null) : ControllerBase
{
    private static readonly IFileManagerLauncher DefaultFileManagerLauncher = new FileManagerLauncher();
    private readonly PhysicalFileAccessCoordinator _physicalFileCoordinator = physicalFileCoordinator ?? PhysicalFileAccessCoordinator.Shared;

    [HttpPost("move")]
    [RequiresPermission(Permissions.FilesWrite)]
    [RequiresUnscopedEntityAccess("read")]
    [RequiresEntityAccess(EntityKinds.File, Permissions.FilesWrite, ActionArgumentName = "dto", PropertyName = "FileIds")]
    public async Task<IActionResult> MoveFiles([FromBody] MoveFilesDto dto, CancellationToken ct)
    {
        var normalizedDestination = TryNormalizeMoveDestination(dto.DestinationPath);
        if (normalizedDestination == null)
            return BadRequest("Destination directory does not exist");

        var (destinationPath, storedDestinationPath) = normalizedDestination.Value;
        if (!Directory.Exists(destinationPath))
            return BadRequest("Destination directory does not exist");

        using var moveLease = await _physicalFileCoordinator.AcquireReadAsync(ct);
        var files = await db.Set<BaseFileEntity>()
            .Include(f => f.ParentFolder)
            .Where(f => dto.FileIds.Contains(f.Id))
            .ToListAsync(ct);

        var movedCount = 0;
        var movedFiles = new List<BaseFileEntity>();
        foreach (var file in files)
        {
            var oldPath = FilesystemPaths.ToNativePath(!string.IsNullOrWhiteSpace(file.Path)
                ? file.Path
                : BaseFileEntity.ComputePath(file.ParentFolder?.Path, file.Basename));
            var newPath = Path.Combine(destinationPath, file.Basename);

            if (!System.IO.File.Exists(oldPath))
            {
                logger.LogWarning("Source file does not exist: {Path}", oldPath);
                continue;
            }

            if (System.IO.File.Exists(newPath))
            {
                logger.LogWarning("Destination file already exists: {Path}", newPath);
                continue;
            }

            System.IO.File.Move(oldPath, newPath);

            // Update folder reference
            var newFolder = await db.Folders.FirstOrDefaultAsync(f => f.Path == storedDestinationPath, ct);
            if (newFolder == null)
            {
                newFolder = new Folder { Path = storedDestinationPath, ModTime = DateTime.UtcNow };
                db.Folders.Add(newFolder);
                await db.SaveChangesAsync(ct);
            }
            file.ParentFolderId = newFolder.Id;
            movedCount++;
            movedFiles.Add(file);
        }

        await db.SaveChangesAsync(ct);
        PublishOwnerUpdates(movedFiles);
        return Ok(new { moved = movedCount, total = files.Count });
    }

    internal static (string NativePath, string StoredPath)? TryNormalizeMoveDestination(string path)
    {
        try
        {
            var nativePath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(nativePath);
            var normalizedNativePath = !string.IsNullOrEmpty(root) && string.Equals(nativePath, root, StringComparison.OrdinalIgnoreCase)
                ? nativePath
                : nativePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return (normalizedNativePath, FilesystemPaths.ToStoredPath(normalizedNativePath));
        }
        catch
        {
            return null;
        }
    }

    private void PublishOwnerUpdates(IEnumerable<BaseFileEntity> files)
    {
        var owners = files
            .Select(file => file switch
            {
                VideoFile { VideoId: int id } => (EventType.VideoUpdated, EntityType: "Video", Id: id),
                ImageFile { ImageId: int id } => (EventType.ImageUpdated, EntityType: "Image", Id: id),
                GalleryFile { GalleryId: int id } => (EventType.GalleryUpdated, EntityType: "Gallery", Id: id),
                AudioFile { AudioId: int id } => (EventType.AudioUpdated, EntityType: "Audio", Id: id),
                TextFile { TextDocumentId: int id } => (EventType.TextUpdated, EntityType: "Text", Id: id),
                _ => ((EventType Type, string EntityType, int Id)?)null,
            })
            .Where(owner => owner.HasValue)
            .Select(owner => owner!.Value)
            .Distinct();

        foreach (var (type, entityType, id) in owners)
            eventBus.Publish(new EntityEvent(type, entityType, id));
    }

    [HttpPost("delete")]
    [RequiresPermission(Permissions.FilesDelete)]
    [RequiresEntityAccess(EntityKinds.File, Permissions.FilesDelete, ActionArgumentName = "dto", PropertyName = "FileIds")]
    public async Task<IActionResult> DeleteFiles([FromBody] DeleteFilesDto dto, CancellationToken ct)
    {
        var files = await db.Set<BaseFileEntity>()
            .Include(f => f.ParentFolder)
            .Where(f => dto.FileIds.Contains(f.Id))
            .ToListAsync(ct);

        var deletedCount = 0;
        var physicalPaths = new List<string>();
        foreach (var file in files)
        {
            if (dto.DeleteFromDisk)
            {
                var storedPath = !string.IsNullOrWhiteSpace(file.Path)
                    ? file.Path
                    : BaseFileEntity.ComputePath(file.ParentFolder?.Path, file.Basename);
                physicalPaths.Add(storedPath);
            }

            db.Set<BaseFileEntity>().Remove(file);
            deletedCount++;
        }

        var deletionContext = new BulkDeletionExecutionContext();
        deletionContext.StagePhysicalFiles(db, physicalPaths);
        await db.SaveChangesAsync(ct);
        if (dto.DeleteFromDisk && physicalPaths.Count > 0)
            physicalFileDeletionRecoverySignal?.Notify();
        PublishOwnerUpdates(files);
        logger.LogInformation(
            "Deleted {Count} file record(s); {DiskCount} physical deletion(s) were staged",
            deletedCount,
            physicalPaths.Count);
        return Ok(new { deleted = deletedCount });
    }

    [HttpGet("browse")]
    [RequiresUnscopedEntityAccess("read")]
    public ActionResult<List<DirectoryEntryDto>> Browse([FromQuery] string? path)
    {
        var targetPath = path ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!Directory.Exists(targetPath))
            return NotFound("Directory does not exist");

        var entries = new List<DirectoryEntryDto>();
        try
        {
            foreach (var dir in Directory.GetDirectories(targetPath))
                entries.Add(new DirectoryEntryDto(dir, true));
            foreach (var file in Directory.GetFiles(targetPath))
                entries.Add(new DirectoryEntryDto(file, false));
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }

        return Ok(entries.OrderBy(e => !e.IsDirectory).ThenBy(e => e.Path).ToList());
    }

    [HttpPost("{id:int}/reveal")]
    [RequiresPermission(Permissions.FilesRead)]
    [RequiresUnscopedEntityAccess("read")]
    [RequiresEntityAccess(EntityKinds.File, Permissions.FilesRead)]
    public async Task<IActionResult> RevealInFileManager(int id, CancellationToken ct)
    {
        var file = await db.Set<BaseFileEntity>()
            .Include(f => f.ParentFolder)
            .FirstOrDefaultAsync(f => f.Id == id, ct);
        if (file == null) return NotFound();

        var storedPath = !string.IsNullOrWhiteSpace(file.Path)
            ? file.Path
            : BaseFileEntity.ComputePath(file.ParentFolder?.Path, file.Basename);
        var filePath = NormalizeLocalPath(FilesystemPaths.ToNativePath(storedPath));
        if (!System.IO.File.Exists(filePath))
            return NotFound("File does not exist on disk");

        try
        {
            (fileManagerLauncher ?? DefaultFileManagerLauncher).RevealFile(filePath);
            return Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reveal file {FileId} in file manager", id);
            return StatusCode(500, "Failed to open file manager");
        }
    }

    [HttpPost("folders/{id:int}/reveal")]
    [RequiresPermission(Permissions.FilesRead)]
    [RequiresUnscopedEntityAccess("read")]
    public async Task<IActionResult> RevealFolderInFileManager(int id, CancellationToken ct)
    {
        var folder = await db.Folders.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (folder == null) return NotFound();

        var folderPath = NormalizeLocalPath(folder.Path);
        if (!Directory.Exists(folderPath))
            return NotFound("Folder does not exist on disk");

        try
        {
            (fileManagerLauncher ?? DefaultFileManagerLauncher).RevealFolder(folderPath);
            return Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reveal folder {FolderId} in file manager", id);
            return StatusCode(500, "Failed to open file manager");
        }
    }

    [HttpPost("fingerprints")]
    [RequiresPermission(Permissions.FilesWrite)]
    [RequiresEntityAccess(EntityKinds.File, Permissions.FilesWrite, ActionArgumentName = "dto", PropertyName = "FileId")]
    public async Task<IActionResult> SetFingerprints([FromBody] FileSetFingerprintsDto dto, CancellationToken ct)
    {
        var file = await db.Set<BaseFileEntity>()
            .Include(f => f.Fingerprints)
            .FirstOrDefaultAsync(f => f.Id == dto.FileId, ct);
        if (file == null) return NotFound();

        foreach (var fp in dto.Fingerprints)
        {
            var existing = file.Fingerprints.FirstOrDefault(f =>
                string.Equals(f.Type, fp.Type, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                existing.Value = fp.Value;
            else
                file.Fingerprints.Add(new FileFingerprint { Type = fp.Type, Value = fp.Value });
        }

        await db.SaveChangesAsync(ct);
        return Ok(new { updated = dto.Fingerprints.Count });
    }

    private static string NormalizeLocalPath(string path)
    {
        var normalized = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? path.Replace('/', Path.DirectorySeparatorChar)
            : path;
        if (OperatingSystem.IsWindows())
        {
            if (normalized.Length > 2 && normalized[1] == ':' && normalized[2] != '\\')
            {
                var tail = normalized[2..].TrimStart('\\');
                var separatorIndex = tail.IndexOf(Path.DirectorySeparatorChar);
                if (separatorIndex >= 0)
                {
                    var root = Path.GetPathRoot(Environment.CurrentDirectory) ?? string.Concat(normalized[0], ':', Path.DirectorySeparatorChar);
                    normalized = Path.Combine(root, tail[(separatorIndex + 1)..]);
                }
                else
                {
                    normalized = normalized[..2] + Path.DirectorySeparatorChar + tail;
                }
            }
        }
        return Path.GetFullPath(normalized);
    }
}
