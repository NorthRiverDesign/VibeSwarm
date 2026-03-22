using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using VibeSwarm.Shared.VersionControl.Models;
using VibeSwarm.Shared;

namespace VibeSwarm.Shared.VersionControl;

/// <summary>
/// Service for interacting with Git version control.
/// </summary>
public sealed partial class VersionControlService : IVersionControlService
{
	private const int DefaultTimeoutSeconds = 30;
	private const int MaxDiffSizeBytes = 1024 * 1024; // 1 MB max diff size

	private readonly IGitCommandExecutor _commandExecutor;
	private readonly ILogger<VersionControlService> _logger;

	public VersionControlService(IGitCommandExecutor commandExecutor, ILogger<VersionControlService> logger)
	{
		_commandExecutor = commandExecutor;
		_logger = logger;
	}

	/// <inheritdoc />
	public async Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default)
	{
		try
		{
			var result = await _commandExecutor.ExecuteAsync(
				"--version",
				Directory.GetCurrentDirectory(),
				cancellationToken,
				timeoutSeconds: 5);

			return result.Success && result.Output.Contains("git version");
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to check git availability");
			return false;
		}
	}

	/// <inheritdoc />
	public async Task<bool> IsGitRepositoryAsync(string workingDirectory, CancellationToken cancellationToken = default)
	{
		try
		{
			var result = await _commandExecutor.ExecuteAsync(
				"rev-parse --is-inside-work-tree",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 5);

			return result.Success && result.Output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to check if {Directory} is a git repository", workingDirectory);
			return false;
		}
	}

	/// <inheritdoc />
	public async Task<string?> GetCurrentCommitHashAsync(string workingDirectory, CancellationToken cancellationToken = default)
	{
		try
		{
			var result = await _commandExecutor.ExecuteAsync(
				"rev-parse HEAD",
				workingDirectory,
				cancellationToken);

			if (result.Success && !string.IsNullOrWhiteSpace(result.Output))
			{
				return result.Output.Trim();
			}
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to get current commit hash for {Directory}", workingDirectory);
		}

		return null;
	}

	/// <inheritdoc />
	public async Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken cancellationToken = default)
	{
		try
		{
			var result = await _commandExecutor.ExecuteAsync(
				"rev-parse --abbrev-ref HEAD",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 10);

			if (result.Success && !string.IsNullOrWhiteSpace(result.Output))
			{
				return result.Output.Trim();
			}
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to get current branch for {Directory}", workingDirectory);
		}

		return null;
	}

	/// <inheritdoc />
	public async Task<string?> GetRemoteUrlAsync(string workingDirectory, string remoteName = "origin", CancellationToken cancellationToken = default)
	{
		try
		{
			var result = await _commandExecutor.ExecuteAsync(
				$"remote get-url {remoteName}",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 10);

			if (result.Success && !string.IsNullOrWhiteSpace(result.Output))
			{
				return result.Output.Trim();
			}
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to get remote URL for {Directory}", workingDirectory);
		}

		return null;
	}

	/// <inheritdoc />
	public async Task<bool> HasUncommittedChangesAsync(string workingDirectory, CancellationToken cancellationToken = default)
	{
		try
		{
			var status = await GetWorkingTreeStatusAsync(workingDirectory, cancellationToken);
			return status.HasUncommittedChanges;
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to check uncommitted changes for {Directory}", workingDirectory);
			return false;
		}
	}

	/// <inheritdoc />
	public async Task<GitWorkingTreeStatus> GetWorkingTreeStatusAsync(string workingDirectory, CancellationToken cancellationToken = default)
	{
		try
		{
			var result = await _commandExecutor.ExecuteAsync(
				"status --porcelain=v1 --untracked-files=all",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 10);

			if (!result.Success)
			{
				return new GitWorkingTreeStatus();
			}

			var changedFiles = ParseWorkingTreeStatus(result.Output);
			if (changedFiles.Count == 0 && !string.IsNullOrWhiteSpace(result.Output))
			{
				changedFiles = (await GetChangedFilesAsync(workingDirectory, cancellationToken: cancellationToken)).ToList();
			}

			return new GitWorkingTreeStatus
			{
				HasUncommittedChanges = !string.IsNullOrWhiteSpace(result.Output),
				ChangedFiles = changedFiles,
				ChangedFilesCount = changedFiles.Count
			};
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to get working tree status for {Directory}", workingDirectory);
			return new GitWorkingTreeStatus();
		}
	}
}
