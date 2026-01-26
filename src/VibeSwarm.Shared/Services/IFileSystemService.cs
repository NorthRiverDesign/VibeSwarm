namespace VibeSwarm.Shared.Services;

/// <summary>
/// Represents a directory entry for the directory browser.
/// </summary>
public class DirectoryEntry
{
	public string Name { get; set; } = string.Empty;
	public string FullPath { get; set; } = string.Empty;
	public bool IsDirectory { get; set; }
	public DateTime? LastModified { get; set; }
}

/// <summary>
/// Represents the result of a directory listing operation.
/// </summary>
public class DirectoryListResult
{
	public string CurrentPath { get; set; } = string.Empty;
	public string? ParentPath { get; set; }
	public List<DirectoryEntry> Entries { get; set; } = new();
	public List<DriveEntry> Drives { get; set; } = new();
	public bool IsRoot { get; set; }
	public string? Error { get; set; }
}

/// <summary>
/// Represents a drive entry for the directory browser.
/// </summary>
public class DriveEntry
{
	public string Name { get; set; } = string.Empty;
	public string RootPath { get; set; } = string.Empty;
	public string? Label { get; set; }
	public long? TotalSize { get; set; }
	public long? FreeSpace { get; set; }
}

public interface IFileSystemService
{
	/// <summary>
	/// Lists the contents of a directory.
	/// </summary>
	/// <param name="path">The directory path to list. If null, lists drives/root.</param>
	/// <param name="directoriesOnly">If true, only returns directories (not files).</param>
	Task<DirectoryListResult> ListDirectoryAsync(string? path, bool directoriesOnly = false);

	/// <summary>
	/// Checks if a directory exists.
	/// </summary>
	Task<bool> DirectoryExistsAsync(string path);

	/// <summary>
	/// Gets the available drives on the system.
	/// </summary>
	Task<List<DriveEntry>> GetDrivesAsync();
}
