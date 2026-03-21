using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.VersionControl;
using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Tests;

public sealed class VersionControlServiceGitHubBrowserTests
{
	[Fact]
	public async Task BrowseGitHubRepositoriesAsync_ParsesJsonWrappedInAdditionalCliOutput()
	{
		var executor = new RecordingGitCommandExecutor();
		executor.AddRawResult("gh", "--version", new GitCommandResult { ExitCode = 0, Output = "gh version 2.70.0" });
		executor.AddRawResult("gh", "auth status", new GitCommandResult { ExitCode = 0, Output = "Logged in to github.com" });
		executor.AddRawResult("gh", "api user", new GitCommandResult
		{
			ExitCode = 0,
			Output = "warning: using cached credentials\n{\"login\":\"octocat\"}\n"
		});
		executor.AddRawResult("gh", "repo list \"octocat\" --limit 100 --json nameWithOwner,description,isPrivate,updatedAt,url", new GitCommandResult
		{
			ExitCode = 0,
			Output = "note: refreshing repository cache\n[{\"nameWithOwner\":\"octocat/older\",\"updatedAt\":\"2024-01-01T00:00:00Z\"},{\"nameWithOwner\":\"octocat/newer\",\"updatedAt\":\"2025-01-01T00:00:00Z\"}]\n"
		});

		var service = new VersionControlService(executor, NullLogger<VersionControlService>.Instance);

		var result = await service.BrowseGitHubRepositoriesAsync();

		Assert.True(result.IsGitHubCliAvailable);
		Assert.True(result.IsAuthenticated);
		Assert.Null(result.ErrorMessage);
		Assert.Equal(new[] { "octocat/newer", "octocat/older" }, result.Repositories.Select(repository => repository.NameWithOwner).ToArray());
	}

	[Fact]
	public async Task BrowseGitHubRepositoriesAsync_ReturnsParseErrorWhenRepositoryJsonIsMissing()
	{
		var executor = new RecordingGitCommandExecutor();
		executor.AddRawResult("gh", "--version", new GitCommandResult { ExitCode = 0, Output = "gh version 2.70.0" });
		executor.AddRawResult("gh", "auth status", new GitCommandResult { ExitCode = 0, Output = "Logged in to github.com" });
		executor.AddRawResult("gh", "api user", new GitCommandResult
		{
			ExitCode = 0,
			Output = "{\"login\":\"octocat\"}"
		});
		executor.AddRawResult("gh", "repo list \"octocat\" --limit 100 --json nameWithOwner,description,isPrivate,updatedAt,url", new GitCommandResult
		{
			ExitCode = 0,
			Output = "repository listing unavailable"
		});

		var service = new VersionControlService(executor, NullLogger<VersionControlService>.Instance);

		var result = await service.BrowseGitHubRepositoriesAsync();

		Assert.Equal("Unable to parse the GitHub repository list returned by GitHub CLI.", result.ErrorMessage);
		Assert.Empty(result.Repositories);
	}

	private sealed class RecordingGitCommandExecutor : IGitCommandExecutor
	{
		private readonly Dictionary<string, Queue<GitCommandResult>> _rawResults = new(StringComparer.Ordinal);

		public void AddRawResult(string command, string arguments, GitCommandResult result)
		{
			var key = $"{command}::{arguments}";
			if (!_rawResults.TryGetValue(key, out var queue))
			{
				queue = new Queue<GitCommandResult>();
				_rawResults[key] = queue;
			}

			queue.Enqueue(result);
		}

		public Task<GitCommandResult> ExecuteAsync(
			string arguments,
			string workingDirectory,
			CancellationToken cancellationToken = default,
			int timeoutSeconds = 30)
			=> throw new NotSupportedException();

		public Task<GitCommandResult> ExecuteRawAsync(
			string command,
			string arguments,
			string workingDirectory,
			CancellationToken cancellationToken = default,
			int timeoutSeconds = 30)
		{
			var key = $"{command}::{arguments}";
			if (!_rawResults.TryGetValue(key, out var queue) || queue.Count == 0)
			{
				throw new InvalidOperationException($"No recorded result for command: {key}");
			}

			return Task.FromResult(queue.Dequeue());
		}
	}
}
