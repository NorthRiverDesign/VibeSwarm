using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.VersionControl;
using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Tests;

public sealed class VersionControlServiceBranchDeliveryTests
{
	[Fact]
	public async Task CreatePullRequestAsync_PushesBranchAndParsesPullRequestMetadata()
	{
		var executor = new RecordingGitCommandExecutor();
		executor.AddGitResult("rev-parse --is-inside-work-tree", new GitCommandResult { ExitCode = 0, Output = "true\n" });
		executor.AddGitResult("push origin feature/test", new GitCommandResult { ExitCode = 0, Error = "Everything up-to-date" });
		executor.AddRawResult("gh", "--version", new GitCommandResult { ExitCode = 0, Output = "gh version 2.70.0" });
		executor.AddRawResult("gh", "auth status", new GitCommandResult { ExitCode = 0, Output = "Logged in to github.com" });
		executor.AddRawResult(
			"gh",
			"pr create --base \"main\" --head \"feature/test\" --title \"Add feature\" --body \"PR body\"",
			new GitCommandResult { ExitCode = 0, Output = "https://github.com/octocat/hello-world/pull/42\n" });

		var service = new VersionControlService(executor, NullLogger<VersionControlService>.Instance);

		var result = await service.CreatePullRequestAsync("/repo", "feature/test", "main", "Add feature", "PR body");

		Assert.True(result.Success);
		Assert.Equal("feature/test", result.BranchName);
		Assert.Equal("main", result.TargetBranch);
		Assert.Equal("https://github.com/octocat/hello-world/pull/42", result.PullRequestUrl);
		Assert.Equal(42, result.PullRequestNumber);
		Assert.Contains(executor.GitCommands, command => command == "push origin feature/test");
	}

	[Fact]
	public async Task MergeBranchAsync_MergesIntoTargetAndReturnsNewCommitHash()
	{
		var executor = new RecordingGitCommandExecutor();
		executor.AddGitResult("rev-parse --is-inside-work-tree", new GitCommandResult { ExitCode = 0, Output = "true\n" });
		executor.AddGitResult("fetch origin --prune", new GitCommandResult { ExitCode = 0, Output = "fetch ok" });
		executor.AddGitResult("fetch origin --prune", new GitCommandResult { ExitCode = 0, Output = "fetch ok" });
		executor.AddGitResult("clean -fd", new GitCommandResult { ExitCode = 0 });
		executor.AddGitResult("reset --hard HEAD", new GitCommandResult { ExitCode = 0 });
		executor.AddGitResult("rev-parse --verify refs/heads/main", new GitCommandResult { ExitCode = 0, Output = "main" });
		executor.AddGitResult("checkout main", new GitCommandResult { ExitCode = 0 });
		executor.AddGitResult("rev-parse --verify refs/remotes/origin/main", new GitCommandResult { ExitCode = 0, Output = "origin/main" });
		executor.AddGitResult("reset --hard origin/main", new GitCommandResult { ExitCode = 0 });
		executor.AddGitResult("rev-parse HEAD", new GitCommandResult { ExitCode = 0, Output = "base123\n" });
		executor.AddGitResult("rev-parse --verify refs/heads/feature/test", new GitCommandResult { ExitCode = 0, Output = "feature/test" });
		executor.AddGitResult("merge --no-ff --no-edit \"feature/test\"", new GitCommandResult { ExitCode = 0, Output = "Merge made by the 'ort' strategy." });
		executor.AddGitResult("push origin main", new GitCommandResult { ExitCode = 0, Error = "Everything up-to-date" });
		executor.AddGitResult("rev-parse HEAD", new GitCommandResult { ExitCode = 0, Output = "abc123def456\n" });

		var service = new VersionControlService(executor, NullLogger<VersionControlService>.Instance);

		var result = await service.MergeBranchAsync("/repo", "feature/test", "main");

		Assert.True(result.Success);
		Assert.Equal("main", result.BranchName);
		Assert.Equal("main", result.TargetBranch);
		Assert.Equal("origin", result.RemoteName);
		Assert.Equal("abc123def456", result.CommitHash);
		Assert.Contains(executor.GitCommands, command => command == "merge --no-ff --no-edit \"feature/test\"");
		Assert.Contains(executor.GitCommands, command => command == "push origin main");
	}

	private sealed class RecordingGitCommandExecutor : IGitCommandExecutor
	{
		private readonly Dictionary<string, Queue<GitCommandResult>> _gitResults = new(StringComparer.Ordinal);
		private readonly Dictionary<string, Queue<GitCommandResult>> _rawResults = new(StringComparer.Ordinal);

		public List<string> GitCommands { get; } = [];

		public void AddGitResult(string arguments, GitCommandResult result)
		{
			if (!_gitResults.TryGetValue(arguments, out var queue))
			{
				queue = new Queue<GitCommandResult>();
				_gitResults[arguments] = queue;
			}

			queue.Enqueue(result);
		}

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
		{
			GitCommands.Add(arguments);
			return Task.FromResult(Dequeue(_gitResults, arguments));
		}

		public Task<GitCommandResult> ExecuteRawAsync(
			string command,
			string arguments,
			string workingDirectory,
			CancellationToken cancellationToken = default,
			int timeoutSeconds = 30)
		{
			return Task.FromResult(Dequeue(_rawResults, $"{command}::{arguments}"));
		}

		private static GitCommandResult Dequeue(Dictionary<string, Queue<GitCommandResult>> lookup, string key)
		{
			if (!lookup.TryGetValue(key, out var queue) || queue.Count == 0)
			{
				throw new InvalidOperationException($"No recorded result for command: {key}");
			}

			return queue.Dequeue();
		}
	}
}
