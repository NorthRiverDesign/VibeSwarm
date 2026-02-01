using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Controllers;

[ApiController]
[Route("api/filesystem")]
[Authorize]
public class FileSystemController : ControllerBase
{
    private readonly IFileSystemService _fileSystemService;

    public FileSystemController(IFileSystemService fileSystemService) => _fileSystemService = fileSystemService;

    [HttpGet("list")]
    public async Task<IActionResult> List([FromQuery] string? path, [FromQuery] bool directoriesOnly = false)
        => Ok(await _fileSystemService.ListDirectoryAsync(path, directoriesOnly));

    [HttpGet("exists")]
    public async Task<IActionResult> Exists([FromQuery] string path)
        => Ok(await _fileSystemService.DirectoryExistsAsync(path));

    [HttpGet("drives")]
    public async Task<IActionResult> GetDrives() => Ok(await _fileSystemService.GetDrivesAsync());
}
