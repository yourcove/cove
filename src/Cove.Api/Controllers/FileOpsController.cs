using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Data;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/files")]
[RequiresPermission(Permissions.FilesRead)]
public class FileOpsController(CoveContext db, IEventBus eventBus, ILogger<FileOpsController> logger) : ControllerBase
{
    [HttpPost("move")]
    [RequiresPermission(Permissions.FilesWrite)]
    [RequiresEntityAccess(EntityKinds.File, Permissions.FilesWrite, ActionArgumentName = "dto", PropertyName = "FileIds")]
    public async Task<IActionResult> MoveFiles([FromBody] MoveFilesDto dto, CancellationToken ct)
    {
        if (!Directory.Exists(dto.DestinationPath))
            return BadRequest("Destination directory does not exist");

        var files = await db.Set<BaseFileEntity>()
            .Include(f => f.ParentFolder)
            .Where(f => dto.FileIds.Contains(f.Id))
            .ToListAsync(ct);

        var movedCount = 0;
        var movedFiles = new List<BaseFileEntity>();
        foreach (var file in files)
        {
            var oldPath = Path.Combine(file.ParentFolder?.Path ?? "", file.Basename);
            var newPath = Path.Combine(dto.DestinationPath, file.Basename);

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
            var newFolder = await db.Folders.FirstOrDefaultAsync(f => f.Path == dto.DestinationPath, ct);
            if (newFolder == null)
            {
                newFolder = new Folder { Path = dto.DestinationPath, ModTime = DateTime.UtcNow };
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
        var deletedFromDisk = 0;
        foreach (var file in files)
        {
            if (dto.DeleteFromDisk)
            {
                var path = Path.Combine(file.ParentFolder?.Path ?? "", file.Basename);
                if (System.IO.File.Exists(path))
                {
                    System.IO.File.Delete(path);
                    deletedFromDisk++;
                    logger.LogDebug("Deleted file from disk: {Path}", path);
                }
            }

            db.Set<BaseFileEntity>().Remove(file);
            deletedCount++;
        }

        await db.SaveChangesAsync(ct);
        PublishOwnerUpdates(files);
        logger.LogInformation("Deleted {Count} file record(s) ({DiskCount} also removed from disk)", deletedCount, deletedFromDisk);
        return Ok(new { deleted = deletedCount });
    }

    [HttpGet("browse")]
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
    [RequiresEntityAccess(EntityKinds.File, Permissions.FilesRead)]
    public async Task<IActionResult> RevealInFileManager(int id, CancellationToken ct)
    {
        var file = await db.Set<BaseFileEntity>()
            .Include(f => f.ParentFolder)
            .FirstOrDefaultAsync(f => f.Id == id, ct);
        if (file == null) return NotFound();

        var filePath = NormalizeLocalPath(!string.IsNullOrWhiteSpace(file.Path)
            ? file.Path
            : Path.Combine(file.ParentFolder?.Path ?? "", file.Basename));
        if (!System.IO.File.Exists(filePath))
            return NotFound("File does not exist on disk");

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var startInfo = new ProcessStartInfo("explorer.exe");
                startInfo.ArgumentList.Add("/select,");
                startInfo.ArgumentList.Add(filePath);
                Process.Start(startInfo);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var startInfo = new ProcessStartInfo("open");
                startInfo.ArgumentList.Add("-R");
                startInfo.ArgumentList.Add(filePath);
                Process.Start(startInfo);
            }
            else
            {
                Process.Start("xdg-open", Path.GetDirectoryName(filePath) ?? filePath);
            }

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
    public async Task<IActionResult> RevealFolderInFileManager(int id, CancellationToken ct)
    {
        var folder = await db.Folders.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (folder == null) return NotFound();

        var folderPath = NormalizeLocalPath(folder.Path);
        if (!Directory.Exists(folderPath))
            return NotFound("Folder does not exist on disk");

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var startInfo = new ProcessStartInfo("explorer.exe");
                startInfo.ArgumentList.Add(folderPath);
                Process.Start(startInfo);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var startInfo = new ProcessStartInfo("open");
                startInfo.ArgumentList.Add(folderPath);
                Process.Start(startInfo);
            }
            else
            {
                Process.Start("xdg-open", folderPath);
            }

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
