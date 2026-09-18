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

	/// <summary>
	/// True when this entry is a directory containing a .git folder. Detected on the
	/// server so the browser can flag existing repositories without extra round trips.
	/// </summary>
	public bool IsGitRepository { get; set; }
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

/// <summary>
/// Everything VibeSwarm can work out about a candidate working directory on its own,
/// so the new-project form can pre-fill answers instead of asking for them.
/// </summary>
public class WorkspaceInspection
{
	public string Path { get; set; } = string.Empty;

	/// <summary>True when the directory already exists on disk.</summary>
	public bool Exists { get; set; }

	/// <summary>True when the directory exists but contains no entries.</summary>
	public bool IsEmpty { get; set; }

	/// <summary>True when the directory is the root of a git working tree.</summary>
	public bool IsGitRepository { get; set; }

	/// <summary>The "origin" remote URL, when the directory is a git repository.</summary>
	public string? RemoteUrl { get; set; }

	/// <summary>The origin remote expressed as "owner/repo" when it points at GitHub.</summary>
	public string? GitHubRepository { get; set; }

	/// <summary>The currently checked out branch name, when it can be resolved.</summary>
	public string? CurrentBranch { get; set; }

	/// <summary>A project name derived from the directory (or repository) name.</summary>
	public string? SuggestedName { get; set; }

	/// <summary>Human readable stack detected from marker files (e.g. ".NET", "Node.js").</summary>
	public string? DetectedStack { get; set; }

	/// <summary>Build command inferred from the detected stack.</summary>
	public string? SuggestedBuildCommand { get; set; }

	/// <summary>Test command inferred from the detected stack.</summary>
	public string? SuggestedTestCommand { get; set; }

	public string? Error { get; set; }
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

	/// <summary>
	/// Inspects a candidate working directory and reports what can be determined
	/// automatically: whether it exists, whether it is already a git repository,
	/// its origin remote and branch, and the stack it appears to use.
	/// </summary>
	Task<WorkspaceInspection> InspectWorkspaceAsync(string path);

	/// <summary>
	/// Inspects every immediate subdirectory of <paramref name="rootPath"/>, so the UI can
	/// offer existing repositories as ready-made project candidates.
	/// </summary>
	Task<List<WorkspaceInspection>> ScanWorkspacesAsync(string rootPath);
}
