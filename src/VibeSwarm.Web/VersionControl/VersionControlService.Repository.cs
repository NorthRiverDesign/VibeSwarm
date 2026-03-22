using System.Text;
using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Shared.VersionControl;

public sealed partial class VersionControlService
{
	/// <inheritdoc />
	public async Task<GitOperationResult> CloneRepositoryAsync(
		string repositoryUrl,
		string targetDirectory,
		string? branch = null,
		Action<string>? progressCallback = null,
		CancellationToken cancellationToken = default)
	{
		try
		{
			// Validate inputs
			if (string.IsNullOrWhiteSpace(repositoryUrl))
			{
				return GitOperationResult.Failed("Repository URL cannot be empty.");
			}

			if (string.IsNullOrWhiteSpace(targetDirectory))
			{
				return GitOperationResult.Failed("Target directory cannot be empty.");
			}

			// Check if git is available
			var gitAvailable = await IsGitAvailableAsync(cancellationToken);
			if (!gitAvailable)
			{
				return GitOperationResult.Failed("Git is not available on this system.");
			}

			// Check if target directory exists and is not empty
			if (Directory.Exists(targetDirectory))
			{
				var entries = Directory.GetFileSystemEntries(targetDirectory);
				if (entries.Length > 0)
				{
					return GitOperationResult.Failed($"Target directory '{targetDirectory}' exists and is not empty.");
				}
			}
			else
			{
				// Create the parent directory if it doesn't exist
				var parentDir = Path.GetDirectoryName(targetDirectory);
				if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
				{
					Directory.CreateDirectory(parentDir);
				}
			}

			progressCallback?.Invoke($"Cloning repository from {repositoryUrl}...");

			// Build clone command
			var cloneArgs = new StringBuilder("clone");

			// Add branch if specified
			if (!string.IsNullOrWhiteSpace(branch))
			{
				cloneArgs.Append($" --branch {branch}");
			}

			// Add progress flag for better feedback
			cloneArgs.Append(" --progress");

			// Add the repository URL and target directory
			cloneArgs.Append($" \"{repositoryUrl}\" \"{targetDirectory}\"");

			// Use the parent directory as working directory since target doesn't exist yet
			var workingDir = Path.GetDirectoryName(targetDirectory) ?? Directory.GetCurrentDirectory();

			// Clone can take longer, increase timeout to 5 minutes
			var result = await _commandExecutor.ExecuteAsync(
				cloneArgs.ToString(),
				workingDir,
				cancellationToken,
				timeoutSeconds: 300);

			if (!result.Success)
			{
				// Clean up partially cloned directory if it exists
				if (Directory.Exists(targetDirectory))
				{
					try
					{
						Directory.Delete(targetDirectory, recursive: true);
					}
					catch
					{
						// Ignore cleanup errors
					}
				}

				var errorMessage = !string.IsNullOrWhiteSpace(result.Error)
					? result.Error.Trim()
					: "Clone operation failed with no error message.";

				return GitOperationResult.Failed($"Failed to clone repository: {errorMessage}");
			}

			progressCallback?.Invoke("Clone completed. Getting repository info...");

			// Get the commit hash and branch info from the cloned repository
			var commitHash = await GetCurrentCommitHashAsync(targetDirectory, cancellationToken);
			var currentBranch = await GetCurrentBranchAsync(targetDirectory, cancellationToken);

			return GitOperationResult.Succeeded(
				output: $"Successfully cloned repository to {targetDirectory}",
				branchName: currentBranch,
				commitHash: commitHash);
		}
		catch (OperationCanceledException)
		{
			// Clean up partially cloned directory
			if (Directory.Exists(targetDirectory))
			{
				try
				{
					Directory.Delete(targetDirectory, recursive: true);
				}
				catch
				{
					// Ignore cleanup errors
				}
			}

			return GitOperationResult.Failed("Clone operation was cancelled or timed out.");
		}
		catch (Exception ex)
		{
			return GitOperationResult.Failed($"Unexpected error during clone: {ex.Message}");
		}
	}

	/// <inheritdoc />
	public string GetGitHubCloneUrl(string ownerAndRepo, bool useSsh = true)
	{
		if (string.IsNullOrWhiteSpace(ownerAndRepo))
		{
			throw new ArgumentException("Owner and repo cannot be empty.", nameof(ownerAndRepo));
		}

		// Trim whitespace and remove any leading/trailing slashes
		var normalized = ownerAndRepo.Trim().Trim('/');

		// Validate format: should be "owner/repo"
		var parts = normalized.Split('/');
		if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
		{
			throw new ArgumentException("Invalid format. Expected 'owner/repo' format (e.g., 'microsoft/vscode').", nameof(ownerAndRepo));
		}

		// Return SSH or HTTPS URL based on preference
		// SSH is preferred for private repos as it uses SSH keys for authentication
		if (useSsh)
		{
			return $"git@github.com:{parts[0]}/{parts[1]}.git";
		}

		return $"https://github.com/{parts[0]}/{parts[1]}.git";
	}

	/// <inheritdoc />
	public string? ExtractGitHubRepository(string? remoteUrl)
	{
		if (string.IsNullOrWhiteSpace(remoteUrl))
		{
			return null;
		}

		var url = remoteUrl.Trim();

		// Handle SSH format: git@github.com:owner/repo.git
		if (url.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
		{
			var path = url.Substring("git@github.com:".Length);
			// Remove .git suffix if present
			if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
			{
				path = path.Substring(0, path.Length - 4);
			}
			return path;
		}

		// Handle HTTPS format: https://github.com/owner/repo.git or https://github.com/owner/repo
		if (url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
		{
			var path = url.Substring("https://github.com/".Length);
			// Remove .git suffix if present
			if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
			{
				path = path.Substring(0, path.Length - 4);
			}
			// Remove trailing slash if present
			path = path.TrimEnd('/');
			return path;
		}

		// Handle HTTP format (less common): http://github.com/owner/repo.git
		if (url.StartsWith("http://github.com/", StringComparison.OrdinalIgnoreCase))
		{
			var path = url.Substring("http://github.com/".Length);
			// Remove .git suffix if present
			if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
			{
				path = path.Substring(0, path.Length - 4);
			}
			// Remove trailing slash if present
			path = path.TrimEnd('/');
			return path;
		}

		// Not a recognized GitHub URL format
		return null;
	}

	/// <inheritdoc />
	public async Task<GitOperationResult> InitializeRepositoryAsync(
		string workingDirectory,
		CancellationToken cancellationToken = default)
	{
		try
		{
			if (!Directory.Exists(workingDirectory))
			{
				return GitOperationResult.Failed($"Directory does not exist: {workingDirectory}");
			}

			// Check if already a git repository
			var isRepo = await IsGitRepositoryAsync(workingDirectory, cancellationToken);
			if (isRepo)
			{
				return GitOperationResult.Failed("Directory is already a git repository.");
			}

			var result = await _commandExecutor.ExecuteAsync(
				"init",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: DefaultTimeoutSeconds);

			if (result.Success)
			{
				return GitOperationResult.Succeeded(output: result.Output?.Trim());
			}

			return GitOperationResult.Failed(result.Error ?? "Failed to initialize git repository.");
		}
		catch (OperationCanceledException)
		{
			return GitOperationResult.Failed("Git init operation was cancelled.");
		}
		catch (Exception ex)
		{
			return GitOperationResult.Failed($"Unexpected error initializing repository: {ex.Message}");
		}
	}

	/// <inheritdoc />
	public async Task<GitOperationResult> AddRemoteAsync(
		string workingDirectory,
		string remoteName,
		string remoteUrl,
		CancellationToken cancellationToken = default)
	{
		try
		{
			var isRepo = await IsGitRepositoryAsync(workingDirectory, cancellationToken);
			if (!isRepo)
			{
				return GitOperationResult.Failed("The specified directory is not a git repository.");
			}

			// Check if remote already exists
			var existingUrl = await GetRemoteUrlAsync(workingDirectory, remoteName, cancellationToken);
			if (!string.IsNullOrEmpty(existingUrl))
			{
				return GitOperationResult.Failed($"Remote '{remoteName}' already exists with URL: {existingUrl}");
			}

			var result = await _commandExecutor.ExecuteAsync(
				$"remote add {remoteName} \"{remoteUrl}\"",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: DefaultTimeoutSeconds);

			if (result.Success)
			{
				return GitOperationResult.Succeeded(
					output: $"Remote '{remoteName}' added successfully.",
					remoteName: remoteName);
			}

			return GitOperationResult.Failed(result.Error ?? "Failed to add remote.");
		}
		catch (OperationCanceledException)
		{
			return GitOperationResult.Failed("Add remote operation was cancelled.");
		}
		catch (Exception ex)
		{
			return GitOperationResult.Failed($"Unexpected error adding remote: {ex.Message}");
		}
	}

	/// <inheritdoc />
	public async Task<IReadOnlyDictionary<string, string>> GetRemotesAsync(
		string workingDirectory,
		CancellationToken cancellationToken = default)
	{
		var remotes = new Dictionary<string, string>();

		try
		{
			var isRepo = await IsGitRepositoryAsync(workingDirectory, cancellationToken);
			if (!isRepo)
			{
				return remotes;
			}

			var result = await _commandExecutor.ExecuteAsync(
				"remote -v",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: DefaultTimeoutSeconds);

			if (result.Success && !string.IsNullOrWhiteSpace(result.Output))
			{
				// Parse output: "origin  git@github.com:owner/repo.git (fetch)"
				foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
				{
					var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
					if (parts.Length >= 2)
					{
						var name = parts[0];
						var url = parts[1];

						// Only add if not already present (we get both fetch and push lines)
						if (!remotes.ContainsKey(name))
						{
							remotes[name] = url;
						}
					}
				}
			}
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to get remotes for {Directory}", workingDirectory);
		}

		return remotes;
	}

	private static string GetRepositoryDirectoryName(string repositoryName)
	{
		var segments = repositoryName
			.Replace('\\', '/')
			.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		return segments.LastOrDefault() ?? repositoryName;
	}

	private static bool PathsEqual(string left, string right)
	{
		var comparison = OperatingSystem.IsWindows()
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;
		return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
	}

}
