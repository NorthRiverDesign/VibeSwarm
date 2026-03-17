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
	public async Task PreviewMergeBranchAsync_ReturnsSuccessWithoutMutatingRepository()
	{
		var executor = new RecordingGitCommandExecutor();
		executor.AddGitResult("rev-parse --is-inside-work-tree", new GitCommandResult { ExitCode = 0, Output = "true\n" });
		executor.AddGitResult("fetch origin --prune", new GitCommandResult { ExitCode = 0, Output = "fetch ok" });
		executor.AddGitResult("rev-parse --verify refs/heads/feature/test", new GitCommandResult { ExitCode = 0, Output = "feature/test\n" });
		executor.AddGitResult("rev-parse --verify refs/heads/main", new GitCommandResult { ExitCode = 0, Output = "main\n" });
		executor.AddGitResult(command => command.StartsWith("worktree add --force --detach ", StringComparison.Ordinal), new GitCommandResult { ExitCode = 0 });
		executor.AddGitResult("merge --no-commit --no-ff \"feature/test\"", new GitCommandResult { ExitCode = 0, Output = "Automatic merge went well." });
		executor.AddGitResult(command => command.StartsWith("worktree remove --force ", StringComparison.Ordinal), new GitCommandResult { ExitCode = 0 });
		executor.AddGitResult("worktree prune", new GitCommandResult { ExitCode = 0 });

		var service = new VersionControlService(executor, NullLogger<VersionControlService>.Instance);

		var result = await service.PreviewMergeBranchAsync("/repo", "feature/test", "main");

		Assert.True(result.Success);
		Assert.Equal("feature/test", result.BranchName);
		Assert.Equal("main", result.TargetBranch);
		Assert.Equal("origin", result.RemoteName);
		Assert.Contains("without conflicts", result.Output);
		Assert.DoesNotContain(executor.GitCommands, command => command.StartsWith("push ", StringComparison.Ordinal));
	}

	[Fact]
	public async Task PreviewMergeBranchAsync_ReturnsConflictAndStillCleansUpTemporaryWorktree()
	{
		var executor = new RecordingGitCommandExecutor();
		executor.AddGitResult("rev-parse --is-inside-work-tree", new GitCommandResult { ExitCode = 0, Output = "true\n" });
		executor.AddGitResult("fetch origin --prune", new GitCommandResult { ExitCode = 0, Output = "fetch ok" });
		executor.AddGitResult("rev-parse --verify refs/heads/feature/test", new GitCommandResult { ExitCode = 0, Output = "feature/test\n" });
		executor.AddGitResult("rev-parse --verify refs/heads/main", new GitCommandResult { ExitCode = 0, Output = "main\n" });
		executor.AddGitResult(command => command.StartsWith("worktree add --force --detach ", StringComparison.Ordinal), new GitCommandResult { ExitCode = 0 });
		executor.AddGitResult("merge --no-commit --no-ff \"feature/test\"", new GitCommandResult { ExitCode = 1, Error = "CONFLICT (content): Merge conflict in README.md" });
		executor.AddGitResult(command => command.StartsWith("worktree remove --force ", StringComparison.Ordinal), new GitCommandResult { ExitCode = 0 });
		executor.AddGitResult("worktree prune", new GitCommandResult { ExitCode = 0 });

		var service = new VersionControlService(executor, NullLogger<VersionControlService>.Instance);

		var result = await service.PreviewMergeBranchAsync("/repo", "feature/test", "main");

		Assert.False(result.Success);
		Assert.Contains("would create conflicts", result.Error);
		Assert.Contains(executor.GitCommands, command => command.StartsWith("worktree remove --force ", StringComparison.Ordinal));
	}

	[Fact]
	public async Task MergeBranchAsync_MergesIntoTargetWithoutPushWhenDisabled()
	{
		var executor = new RecordingGitCommandExecutor();
		executor.AddGitResult("rev-parse --is-inside-work-tree", new GitCommandResult { ExitCode = 0, Output = "true\n" });
		executor.AddGitResult("fetch origin --prune", new GitCommandResult { ExitCode = 0, Output = "fetch ok" });
		executor.AddGitResult("rev-parse --verify refs/heads/feature/test", new GitCommandResult { ExitCode = 0, Output = "feature/test\n" });
		executor.AddGitResult("rev-parse --verify refs/heads/main", new GitCommandResult { ExitCode = 0, Output = "main\n" });
		executor.AddGitResult(command => command.StartsWith("worktree add --force ", StringComparison.Ordinal) && !command.Contains("--detach", StringComparison.Ordinal), new GitCommandResult { ExitCode = 0 });
		executor.AddGitResult("merge --no-ff --no-edit \"feature/test\"", new GitCommandResult { ExitCode = 0, Output = "Merge made by the 'ort' strategy." });
		executor.AddGitResult("rev-parse HEAD", new GitCommandResult { ExitCode = 0, Output = "abc123def456\n" });
		executor.AddGitResult(command => command.StartsWith("worktree remove --force ", StringComparison.Ordinal), new GitCommandResult { ExitCode = 0 });
		executor.AddGitResult("worktree prune", new GitCommandResult { ExitCode = 0 });

		var service = new VersionControlService(executor, NullLogger<VersionControlService>.Instance);

		var result = await service.MergeBranchAsync("/repo", "feature/test", "main", pushAfterMerge: false);

		Assert.True(result.Success);
		Assert.Equal("main", result.BranchName);
		Assert.Equal("main", result.TargetBranch);
		Assert.Equal("origin", result.RemoteName);
		Assert.Equal("abc123def456", result.CommitHash);
		Assert.Contains("locally", result.Output);
		Assert.DoesNotContain(executor.GitCommands, command => command == "push origin main");
	}

	private sealed class RecordingGitCommandExecutor : IGitCommandExecutor
	{
		private readonly List<(Func<string, bool> Match, Queue<GitCommandResult> Results)> _gitResults = [];
		private readonly Dictionary<string, Queue<GitCommandResult>> _rawResults = new(StringComparer.Ordinal);

		public List<string> GitCommands { get; } = [];

		public void AddGitResult(string arguments, GitCommandResult result)
			=> AddGitResult(command => string.Equals(command, arguments, StringComparison.Ordinal), result);

		public void AddGitResult(Func<string, bool> match, GitCommandResult result)
		{
			var entry = _gitResults.FirstOrDefault(candidate => ReferenceEquals(candidate.Match, match));
			if (entry.Match == null)
			{
				entry = (match, new Queue<GitCommandResult>());
				_gitResults.Add(entry);
			}

			entry.Results.Enqueue(result);
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

			foreach (var (match, results) in _gitResults)
			{
				if (results.Count > 0 && match(arguments))
				{
					return Task.FromResult(results.Dequeue());
				}
			}

			throw new InvalidOperationException($"No recorded result for command: {arguments}");
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
