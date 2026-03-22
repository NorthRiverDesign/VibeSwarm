using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Shared.VersionControl;

public sealed partial class VersionControlService
{
	public async Task<bool> IsGitHubCliAvailableAsync(CancellationToken cancellationToken = default)
	{
		try
		{
			var result = await _commandExecutor.ExecuteRawAsync(
				"gh",
				"--version",
				Directory.GetCurrentDirectory(),
				cancellationToken,
				timeoutSeconds: 5);

			return result.Success && result.Output.Contains("gh version");
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to check GitHub CLI availability");
			return false;
		}
	}

	/// <inheritdoc />
	public async Task<bool> IsGitHubCliAuthenticatedAsync(CancellationToken cancellationToken = default)
	{
		try
		{
			var result = await _commandExecutor.ExecuteRawAsync(
				"gh",
				"auth status",
				Directory.GetCurrentDirectory(),
				cancellationToken,
				timeoutSeconds: 10);

			// gh auth status returns exit code 0 if logged in
			return result.Success;
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to check GitHub CLI authentication");
			return false;
		}
	}

	/// <inheritdoc />
	public async Task<GitHubRepositoryBrowserResult> BrowseGitHubRepositoriesAsync(CancellationToken cancellationToken = default)
	{
		var browserResult = new GitHubRepositoryBrowserResult();

		try
		{
			browserResult.IsGitHubCliAvailable = await IsGitHubCliAvailableAsync(cancellationToken);
			if (!browserResult.IsGitHubCliAvailable)
			{
				browserResult.ErrorMessage = "GitHub CLI (gh) is not installed. Install it on the host to browse repositories.";
				return browserResult;
			}

			browserResult.IsAuthenticated = await IsGitHubCliAuthenticatedAsync(cancellationToken);
			if (!browserResult.IsAuthenticated)
			{
				browserResult.ErrorMessage = "Sign in with 'gh auth login' on the host to browse repositories.";
				return browserResult;
			}

			var userResult = await _commandExecutor.ExecuteRawAsync(
				"gh",
				"api user",
				Directory.GetCurrentDirectory(),
				cancellationToken,
				timeoutSeconds: 15);

			if (!userResult.Success)
			{
				browserResult.ErrorMessage = BuildCommandError(userResult, "Unable to determine the authenticated GitHub account.");
				return browserResult;
			}

			var user = DeserializeCommandJson<GitHubViewerResponse>(userResult.Output, '{', '}');
			if (string.IsNullOrWhiteSpace(user?.Login))
			{
				browserResult.ErrorMessage = "Unable to determine the authenticated GitHub account.";
				return browserResult;
			}

			var listResult = await _commandExecutor.ExecuteRawAsync(
				"gh",
				$"repo list \"{EscapeCommandArgument(user.Login)}\" --limit 100 --json nameWithOwner,description,isPrivate,updatedAt,url",
				Directory.GetCurrentDirectory(),
				cancellationToken,
				timeoutSeconds: 30);

			if (!listResult.Success)
			{
				browserResult.ErrorMessage = BuildCommandError(listResult, "Unable to load GitHub repositories.");
				return browserResult;
			}

			browserResult.Repositories = DeserializeCommandJson<List<GitHubRepositoryBrowserItem>>(listResult.Output, '[', ']')?
				.OrderByDescending(repository => repository.UpdatedAt ?? DateTimeOffset.MinValue)
				.ThenBy(repository => repository.NameWithOwner, StringComparer.OrdinalIgnoreCase)
				.ToList()
				?? [];

			if (browserResult.Repositories.Count == 0 && !string.IsNullOrWhiteSpace(listResult.Output) && listResult.Output.IndexOf('[', StringComparison.Ordinal) < 0)
			{
				browserResult.ErrorMessage = "Unable to parse the GitHub repository list returned by GitHub CLI.";
			}

			return browserResult;
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to browse GitHub repositories");
			browserResult.ErrorMessage = $"Unable to load GitHub repositories: {ex.Message}";
			return browserResult;
		}
	}

	/// <inheritdoc />
	public async Task<GitOperationResult> CreateGitHubRepositoryAsync(
		string workingDirectory,
		string repositoryName,
		string? description = null,
		bool isPrivate = false,
		Action<string>? progressCallback = null,
		CancellationToken cancellationToken = default,
		string? gitignoreTemplate = null,
		string? licenseTemplate = null,
		bool initializeReadme = false)
	{
		try
		{
			// Check if gh CLI is available
			var ghAvailable = await IsGitHubCliAvailableAsync(cancellationToken);
			if (!ghAvailable)
			{
				return GitOperationResult.Failed("GitHub CLI (gh) is not installed. Please install it from https://cli.github.com/");
			}

			// Check authentication
			var ghAuthenticated = await IsGitHubCliAuthenticatedAsync(cancellationToken);
			if (!ghAuthenticated)
			{
				return GitOperationResult.Failed("Not authenticated with GitHub CLI. Please run 'gh auth login' to authenticate.");
			}

			var fullWorkingDirectory = Path.GetFullPath(workingDirectory);
			if (!Directory.Exists(fullWorkingDirectory) || !Directory.EnumerateFileSystemEntries(fullWorkingDirectory).Any())
			{
				return await CreateAndCloneGitHubRepositoryAsync(
					fullWorkingDirectory,
					repositoryName,
					description,
					isPrivate,
					progressCallback,
					cancellationToken,
					gitignoreTemplate,
					licenseTemplate,
					initializeReadme);
			}

			progressCallback?.Invoke("Checking git repository status...");

			// Check if it's a git repository, if not initialize it
			var isRepo = await IsGitRepositoryAsync(fullWorkingDirectory, cancellationToken);
			if (!isRepo)
			{
				progressCallback?.Invoke("Initializing git repository...");
				var initResult = await InitializeRepositoryAsync(fullWorkingDirectory, cancellationToken);
				if (!initResult.Success)
				{
					return GitOperationResult.Failed($"Failed to initialize git repository: {initResult.Error}");
				}
			}

			// Check if remote already exists
			var existingRemote = await GetRemoteUrlAsync(fullWorkingDirectory, "origin", cancellationToken);
			if (!string.IsNullOrEmpty(existingRemote))
			{
				return GitOperationResult.Failed($"Remote 'origin' already exists: {existingRemote}");
			}

			progressCallback?.Invoke("Creating GitHub repository...");

			// Build the gh repo create command
			var visibility = isPrivate ? "--private" : "--public";
			var descArg = !string.IsNullOrWhiteSpace(description)
				? $"--description \"{description.Replace("\"", "\\\"")}\""
				: "";

			var gitignoreArg = !string.IsNullOrEmpty(gitignoreTemplate)
				? $"--gitignore \"{gitignoreTemplate}\""
				: "";
			var licenseArg = !string.IsNullOrEmpty(licenseTemplate)
				? $"--license \"{licenseTemplate}\""
				: "";
			var readmeArg = initializeReadme ? "--add-readme" : "";

			// Use gh repo create with --source flag to link to existing directory
			var ghArgs = $"repo create \"{repositoryName}\" {visibility} {descArg} {gitignoreArg} {licenseArg} {readmeArg} --source \"{workingDirectory}\" --remote origin --push".Trim();

			var result = await _commandExecutor.ExecuteRawAsync(
				"gh",
				ghArgs,
				fullWorkingDirectory,
				cancellationToken,
				timeoutSeconds: 120);

			if (result.Success)
			{
				// Try to get the new remote URL
				var newRemoteUrl = await GetRemoteUrlAsync(fullWorkingDirectory, "origin", cancellationToken);

				progressCallback?.Invoke("Repository created successfully!");

				return GitOperationResult.Succeeded(
					output: result.Output?.Trim(),
					remoteName: "origin");
			}

			// Parse error message
			var errorMessage = result.Error ?? result.Output ?? "Failed to create GitHub repository.";

			// Check for common errors
			if (errorMessage.Contains("already exists"))
			{
				return GitOperationResult.Failed($"Repository '{repositoryName}' already exists on GitHub. Please choose a different name.");
			}

			return GitOperationResult.Failed(errorMessage.Trim());
		}
		catch (OperationCanceledException)
		{
			return GitOperationResult.Failed("Create GitHub repository operation was cancelled.");
		}
		catch (Exception ex)
		{
			return GitOperationResult.Failed($"Unexpected error creating GitHub repository: {ex.Message}");
		}
	}

	private async Task<GitOperationResult> CreateAndCloneGitHubRepositoryAsync(
		string workingDirectory,
		string repositoryName,
		string? description,
		bool isPrivate,
		Action<string>? progressCallback,
		CancellationToken cancellationToken,
		string? gitignoreTemplate,
		string? licenseTemplate,
		bool initializeReadme)
	{
		var parentDirectory = Path.GetDirectoryName(workingDirectory);
		if (string.IsNullOrWhiteSpace(parentDirectory))
		{
			return GitOperationResult.Failed($"Unable to determine a parent directory for '{workingDirectory}'.");
		}

		Directory.CreateDirectory(parentDirectory);

		var targetDirectoryExists = Directory.Exists(workingDirectory);
		if (targetDirectoryExists && Directory.EnumerateFileSystemEntries(workingDirectory).Any())
		{
			return GitOperationResult.Failed($"Target directory must be empty before creating and cloning a new repository: {workingDirectory}");
		}

		if (targetDirectoryExists)
		{
			Directory.Delete(workingDirectory);
		}

		var cloneDirectoryName = GetRepositoryDirectoryName(repositoryName);
		var cloneDirectory = Path.Combine(parentDirectory, cloneDirectoryName);
		if (!PathsEqual(cloneDirectory, workingDirectory) && Directory.Exists(cloneDirectory))
		{
			return GitOperationResult.Failed($"A directory already exists for the new repository clone: {cloneDirectory}");
		}

		progressCallback?.Invoke("Creating GitHub repository and cloning workspace...");

		var visibility = isPrivate ? "--private" : "--public";
		var descArg = !string.IsNullOrWhiteSpace(description)
			? $"--description \"{description.Replace("\"", "\\\"")}\""
			: "";
		var gitignoreArg = !string.IsNullOrEmpty(gitignoreTemplate)
			? $"--gitignore \"{gitignoreTemplate}\""
			: "";
		var licenseArg = !string.IsNullOrEmpty(licenseTemplate)
			? $"--license \"{licenseTemplate}\""
			: "";
		var readmeArg = initializeReadme ? "--add-readme" : "";
		var ghArgs = $"repo create \"{repositoryName}\" {visibility} {descArg} {gitignoreArg} {licenseArg} {readmeArg} --clone".Trim();

		var result = await _commandExecutor.ExecuteRawAsync(
			"gh",
			ghArgs,
			parentDirectory,
			cancellationToken,
			timeoutSeconds: 120);

		if (!result.Success)
		{
			if (targetDirectoryExists)
			{
				Directory.CreateDirectory(workingDirectory);
			}

			return ParseCreateRepositoryFailure(result, repositoryName);
		}

		if (!Directory.Exists(cloneDirectory))
		{
			return GitOperationResult.Failed($"GitHub repository was created, but the local clone directory was not found: {cloneDirectory}");
		}

		if (!PathsEqual(cloneDirectory, workingDirectory))
		{
			Directory.Move(cloneDirectory, workingDirectory);
		}

		progressCallback?.Invoke("Repository created successfully!");
		return GitOperationResult.Succeeded(output: result.Output?.Trim(), remoteName: "origin");
	}

	/// <inheritdoc />
	/// <inheritdoc />
	public async Task<GitOperationResult> CloneWithGitHubCliAsync(
		string ownerRepo,
		string targetDirectory,
		Action<string>? progressCallback = null,
		CancellationToken cancellationToken = default)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(ownerRepo))
			{
				return GitOperationResult.Failed("Owner/repo cannot be empty.");
			}

			if (string.IsNullOrWhiteSpace(targetDirectory))
			{
				return GitOperationResult.Failed("Target directory cannot be empty.");
			}

			var ghAvailable = await IsGitHubCliAvailableAsync(cancellationToken);
			if (!ghAvailable)
			{
				return GitOperationResult.Failed("GitHub CLI (gh) is not installed.");
			}

			var ghAuthenticated = await IsGitHubCliAuthenticatedAsync(cancellationToken);
			if (!ghAuthenticated)
			{
				return GitOperationResult.Failed("Not authenticated with GitHub CLI. Please run 'gh auth login'.");
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
				var parentDir = Path.GetDirectoryName(targetDirectory);
				if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
				{
					Directory.CreateDirectory(parentDir);
				}
			}

			progressCallback?.Invoke($"Cloning {ownerRepo} using GitHub CLI...");

			var workingDir = Path.GetDirectoryName(targetDirectory) ?? Directory.GetCurrentDirectory();

			var result = await _commandExecutor.ExecuteRawAsync(
				"gh",
				$"repo clone {ownerRepo} \"{targetDirectory}\"",
				workingDir,
				cancellationToken,
				timeoutSeconds: 300);

			if (!result.Success)
			{
				// Clean up partially cloned directory
				if (Directory.Exists(targetDirectory))
				{
					try { Directory.Delete(targetDirectory, true); } catch { }
				}
				return GitOperationResult.Failed($"gh clone failed: {result.Error}");
			}

			progressCallback?.Invoke("Clone complete.");

			return GitOperationResult.Succeeded(output: $"Successfully cloned {ownerRepo}");
		}
		catch (OperationCanceledException)
		{
			return GitOperationResult.Failed("Clone operation was cancelled or timed out.");
		}
		catch (Exception ex)
		{
			return GitOperationResult.Failed($"Unexpected error: {ex.Message}");
		}
	}

	private static GitOperationResult ParseCreateRepositoryFailure(GitCommandResult result, string repositoryName)
	{
		var errorMessage = result.Error ?? result.Output ?? "Failed to create GitHub repository.";
		if (errorMessage.Contains("already exists", StringComparison.OrdinalIgnoreCase))
		{
			return GitOperationResult.Failed($"Repository '{repositoryName}' already exists on GitHub. Please choose a different name.");
		}

		return GitOperationResult.Failed(errorMessage.Trim());
	}

}
