namespace VibeSwarm.Shared.Services;

public class FileSystemService : IFileSystemService
{
	public Task<DirectoryListResult> ListDirectoryAsync(string? path, bool directoriesOnly = false)
	{
		var result = new DirectoryListResult();

		try
		{
			// If path is null or empty, show drives/root
			if (string.IsNullOrWhiteSpace(path))
			{
				result.IsRoot = true;
				result.CurrentPath = string.Empty;
				result.Drives = GetDrivesInternal();

				// On Unix systems, start at root
				if (!OperatingSystem.IsWindows())
				{
					path = "/";
					result.CurrentPath = "/";
					result.IsRoot = false;
				}
				else
				{
					return Task.FromResult(result);
				}
			}

			// Normalize the path
			path = Path.GetFullPath(path);
			result.CurrentPath = path;

			var parentDir = Directory.GetParent(path);
			if (parentDir != null)
			{
				result.ParentPath = parentDir.FullName;
			}
			else if (OperatingSystem.IsWindows())
			{
				// On Windows, if we're at a drive root, allow going back to drive list
				result.ParentPath = null;
				result.IsRoot = true;
			}
			else
			{
				// On Unix, "/" is the root
				result.IsRoot = path == "/";
				result.ParentPath = result.IsRoot ? null : Directory.GetParent(path)?.FullName;
			}

			// List directory contents
			var dirInfo = new DirectoryInfo(path);
			if (!dirInfo.Exists)
			{
				result.Error = "Directory does not exist.";
				return Task.FromResult(result);
			}

			foreach (var dir in dirInfo.GetDirectories().OrderBy(d => d.Name))
			{
				try
				{
					result.Entries.Add(new DirectoryEntry
					{
						Name = dir.Name,
						FullPath = dir.FullName,
						IsDirectory = true,
						LastModified = dir.LastWriteTime,
						IsGitRepository = Directory.Exists(Path.Combine(dir.FullName, ".git"))
					});
				}
				catch (UnauthorizedAccessException)
				{
					// Skip directories we can't access
				}
			}

			// Get files (if not directories only)
			if (!directoriesOnly)
			{
				foreach (var file in dirInfo.GetFiles().OrderBy(f => f.Name))
				{
					try
					{
						result.Entries.Add(new DirectoryEntry
						{
							Name = file.Name,
							FullPath = file.FullName,
							IsDirectory = false,
							LastModified = file.LastWriteTime
						});
					}
					catch (UnauthorizedAccessException)
					{
						// Skip files we can't access
					}
				}
			}

			// Always include drives on Windows
			if (OperatingSystem.IsWindows())
			{
				result.Drives = GetDrivesInternal();
			}
		}
		catch (UnauthorizedAccessException)
		{
			result.Error = "Access denied. You don't have permission to access this directory.";
		}
		catch (DirectoryNotFoundException)
		{
			result.Error = "Directory not found.";
		}
		catch (Exception ex)
		{
			result.Error = $"Error accessing directory: {ex.Message}";
		}

		return Task.FromResult(result);
	}

	public Task<bool> DirectoryExistsAsync(string path)
	{
		try
		{
			return Task.FromResult(Directory.Exists(path));
		}
		catch
		{
			return Task.FromResult(false);
		}
	}

	public Task<List<DriveEntry>> GetDrivesAsync()
	{
		return Task.FromResult(GetDrivesInternal());
	}

	public Task<WorkspaceInspection> InspectWorkspaceAsync(string path)
	{
		return Task.FromResult(Inspect(path));
	}

	public Task<List<WorkspaceInspection>> ScanWorkspacesAsync(string rootPath)
	{
		var results = new List<WorkspaceInspection>();

		try
		{
			if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
			{
				return Task.FromResult(results);
			}

			foreach (var dir in new DirectoryInfo(rootPath).GetDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
			{
				if (dir.Name.StartsWith('.'))
				{
					continue;
				}

				try
				{
					results.Add(Inspect(dir.FullName));
				}
				catch (UnauthorizedAccessException)
				{
					// Skip directories we can't read.
				}
			}
		}
		catch
		{
			// Return whatever we managed to collect.
		}

		return Task.FromResult(results);
	}

	private static WorkspaceInspection Inspect(string path)
	{
		var inspection = new WorkspaceInspection { Path = path ?? string.Empty };

		if (string.IsNullOrWhiteSpace(path))
		{
			return inspection;
		}

		try
		{
			inspection.Path = Path.GetFullPath(path);
			var directory = new DirectoryInfo(inspection.Path);
			inspection.SuggestedName = SuggestNameFromPath(inspection.Path);

			if (!directory.Exists)
			{
				return inspection;
			}

			inspection.Exists = true;
			inspection.IsEmpty = !directory.EnumerateFileSystemInfos().Any();

			var gitPath = Path.Combine(inspection.Path, ".git");
			inspection.IsGitRepository = Directory.Exists(gitPath) || File.Exists(gitPath);

			if (inspection.IsGitRepository)
			{
				inspection.RemoteUrl = ReadOriginRemoteUrl(gitPath);
				inspection.GitHubRepository = ParseGitHubRepository(inspection.RemoteUrl);
				inspection.CurrentBranch = ReadCurrentBranch(gitPath);
			}

			DetectStack(inspection, directory);
		}
		catch (UnauthorizedAccessException)
		{
			inspection.Error = "Access denied. VibeSwarm can't read this directory.";
		}
		catch (Exception ex)
		{
			inspection.Error = ex.Message;
		}

		return inspection;
	}

	private static string? SuggestNameFromPath(string fullPath)
	{
		var name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
		return string.IsNullOrWhiteSpace(name) ? null : name;
	}

	/// <summary>
	/// Reads the origin remote straight out of .git/config. Parsing the file avoids
	/// shelling out to git for something the UI asks for on every keystroke.
	/// </summary>
	private static string? ReadOriginRemoteUrl(string gitPath)
	{
		try
		{
			var configPath = Path.Combine(ResolveGitDirectory(gitPath), "config");
			if (!File.Exists(configPath))
			{
				return null;
			}

			var inOriginSection = false;
			foreach (var rawLine in File.ReadLines(configPath))
			{
				var line = rawLine.Trim();

				if (line.StartsWith('['))
				{
					inOriginSection = line.Replace(" ", string.Empty, StringComparison.Ordinal)
						.Equals("[remote\"origin\"]", StringComparison.OrdinalIgnoreCase);
					continue;
				}

				if (inOriginSection && line.StartsWith("url", StringComparison.OrdinalIgnoreCase))
				{
					var separator = line.IndexOf('=');
					if (separator >= 0)
					{
						var url = line[(separator + 1)..].Trim();
						return string.IsNullOrWhiteSpace(url) ? null : url;
					}
				}
			}
		}
		catch
		{
			// A malformed or unreadable config simply means we can't pre-fill anything.
		}

		return null;
	}

	private static string? ReadCurrentBranch(string gitPath)
	{
		try
		{
			var headPath = Path.Combine(ResolveGitDirectory(gitPath), "HEAD");
			if (!File.Exists(headPath))
			{
				return null;
			}

			var head = File.ReadAllText(headPath).Trim();
			const string refPrefix = "ref: refs/heads/";
			if (head.StartsWith(refPrefix, StringComparison.Ordinal))
			{
				return head[refPrefix.Length..].Trim();
			}

			// Detached HEAD — show the short commit instead of nothing.
			return head.Length >= 7 ? head[..7] : null;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// Resolves the real git directory, following the "gitdir:" pointer used by worktrees
	/// and submodules where .git is a file rather than a folder.
	/// </summary>
	private static string ResolveGitDirectory(string gitPath)
	{
		if (Directory.Exists(gitPath))
		{
			return gitPath;
		}

		if (File.Exists(gitPath))
		{
			var contents = File.ReadAllText(gitPath).Trim();
			const string prefix = "gitdir:";
			if (contents.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			{
				var target = contents[prefix.Length..].Trim();
				if (!Path.IsPathRooted(target))
				{
					target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(gitPath) ?? string.Empty, target));
				}

				return target;
			}
		}

		return gitPath;
	}

	private static string? ParseGitHubRepository(string? remoteUrl)
	{
		if (string.IsNullOrWhiteSpace(remoteUrl) ||
			remoteUrl.IndexOf("github.com", StringComparison.OrdinalIgnoreCase) < 0)
		{
			return null;
		}

		var value = remoteUrl.Trim();
		if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
		{
			value = value[..^4];
		}

		var marker = value.IndexOf("github.com", StringComparison.OrdinalIgnoreCase);
		value = value[(marker + "github.com".Length)..].TrimStart(':', '/');

		var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		return segments.Length >= 2 ? $"{segments[0]}/{segments[1]}" : null;
	}

	/// <summary>
	/// Detects the project stack from well-known marker files so build verification
	/// commands can be offered instead of typed.
	/// </summary>
	private static void DetectStack(WorkspaceInspection inspection, DirectoryInfo directory)
	{
		foreach (var (stack, build, test, matches) in StackSignatures)
		{
			if (!matches(directory))
			{
				continue;
			}

			inspection.DetectedStack = stack;
			inspection.SuggestedBuildCommand = build;
			inspection.SuggestedTestCommand = test;
			return;
		}
	}

	private static readonly (string Stack, string Build, string Test, Func<DirectoryInfo, bool> Matches)[] StackSignatures =
	[
		(".NET", "dotnet build", "dotnet test", d => HasFile(d, "*.sln") || HasFile(d, "*.slnx") || HasFile(d, "*.csproj")),
		("Node.js", "npm run build", "npm test", d => HasFile(d, "package.json")),
		("Rust", "cargo build", "cargo test", d => HasFile(d, "Cargo.toml")),
		("Go", "go build ./...", "go test ./...", d => HasFile(d, "go.mod")),
		("Python", "python -m build", "pytest", d => HasFile(d, "pyproject.toml") || HasFile(d, "setup.py") || HasFile(d, "requirements.txt")),
		("Java (Maven)", "mvn -B package", "mvn -B test", d => HasFile(d, "pom.xml")),
		("Java (Gradle)", "./gradlew build", "./gradlew test", d => HasFile(d, "build.gradle") || HasFile(d, "build.gradle.kts")),
		("Make", "make", "make test", d => HasFile(d, "Makefile"))
	];

	private static bool HasFile(DirectoryInfo directory, string pattern)
	{
		try
		{
			return directory.EnumerateFiles(pattern, SearchOption.TopDirectoryOnly).Any();
		}
		catch
		{
			return false;
		}
	}

	private List<DriveEntry> GetDrivesInternal()
	{
		var drives = new List<DriveEntry>();

		try
		{
			foreach (var drive in DriveInfo.GetDrives())
			{
				try
				{
					if (drive.IsReady)
					{
						drives.Add(new DriveEntry
						{
							Name = drive.Name,
							RootPath = drive.RootDirectory.FullName,
							Label = string.IsNullOrEmpty(drive.VolumeLabel) ? drive.DriveType.ToString() : drive.VolumeLabel,
							TotalSize = drive.TotalSize,
							FreeSpace = drive.AvailableFreeSpace
						});
					}
					else
					{
						drives.Add(new DriveEntry
						{
							Name = drive.Name,
							RootPath = drive.RootDirectory.FullName,
							Label = "Not Ready"
						});
					}
				}
				catch
				{
					// Skip drives we can't access
				}
			}
		}
		catch
		{
			// Failed to enumerate drives
		}

		return drives;
	}
}
