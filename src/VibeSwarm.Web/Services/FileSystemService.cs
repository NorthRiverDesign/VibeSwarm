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

			// Get parent directory
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

			// Get directories
			foreach (var dir in dirInfo.GetDirectories().OrderBy(d => d.Name))
			{
				try
				{
					result.Entries.Add(new DirectoryEntry
					{
						Name = dir.Name,
						FullPath = dir.FullName,
						IsDirectory = true,
						LastModified = dir.LastWriteTime
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
