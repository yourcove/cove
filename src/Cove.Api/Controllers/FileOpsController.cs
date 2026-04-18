using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Data;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/files")]
public class FileOpsController(CoveContext db, ILogger<FileOpsController> logger) : ControllerBase
{
    [HttpPost("move")]
    public async Task<IActionResult> MoveFiles([FromBody] MoveFilesDto dto, CancellationToken ct)
    {
        if (!Directory.Exists(dto.DestinationPath))
            return BadRequest("Destination directory does not exist");

        var files = await db.Set<BaseFileEntity>()
            .Include(f => f.ParentFolder)
            .Where(f => dto.FileIds.Contains(f.Id))
            .ToListAsync(ct);

        var movedCount = 0;
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
        }

        await db.SaveChangesAsync(ct);
        return Ok(new { moved = movedCount, total = files.Count });
    }

    [HttpPost("delete")]
    public async Task<IActionResult> DeleteFiles([FromBody] DeleteFilesDto dto, CancellationToken ct)
    {
        var files = await db.Set<BaseFileEntity>()
            .Include(f => f.ParentFolder)
            .Where(f => dto.FileIds.Contains(f.Id))
            .ToListAsync(ct);

        var deletedCount = 0;
        foreach (var file in files)
        {
            if (dto.DeleteFromDisk)
            {
                var path = Path.Combine(file.ParentFolder?.Path ?? "", file.Basename);
                if (System.IO.File.Exists(path))
                {
                    System.IO.File.Delete(path);
                    logger.LogInformation("Deleted file from disk: {Path}", path);
                }
            }

            db.Set<BaseFileEntity>().Remove(file);
            deletedCount++;
        }

        await db.SaveChangesAsync(ct);
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
    public async Task<IActionResult> RevealInFileManager(int id, CancellationToken ct)
    {
        var file = await db.Set<BaseFileEntity>()
            .Include(f => f.ParentFolder)
            .FirstOrDefaultAsync(f => f.Id == id, ct);
        if (file == null) return NotFound();

        var filePath = Path.Combine(file.ParentFolder?.Path ?? "", file.Basename);
        if (!System.IO.File.Exists(filePath))
            return NotFound("File does not exist on disk");

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start("explorer.exe", $"/select,\"{filePath}\"");
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start("open", $"-R \"{filePath}\"");
            else
                Process.Start("xdg-open", Path.GetDirectoryName(filePath) ?? filePath);

            return Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reveal file {FileId} in file manager", id);
            return StatusCode(500, "Failed to open file manager");
        }
    }

    [HttpPost("folders/{id:int}/reveal")]
    public async Task<IActionResult> RevealFolderInFileManager(int id, CancellationToken ct)
    {
        var folder = await db.Folders.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (folder == null) return NotFound();

        if (!Directory.Exists(folder.Path))
            return NotFound("Folder does not exist on disk");

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start("explorer.exe", $"\"{folder.Path}\"");
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start("open", $"\"{folder.Path}\"");
            else
                Process.Start("xdg-open", folder.Path);

            return Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reveal folder {FolderId} in file manager", id);
            return StatusCode(500, "Failed to open file manager");
        }
    }

    [HttpPost("fingerprints")]
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
}
