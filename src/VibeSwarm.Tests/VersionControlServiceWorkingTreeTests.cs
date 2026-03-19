using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.VersionControl;
using VibeSwarm.Shared.VersionControl.Models;
using VibeSwarm.Shared;

namespace VibeSwarm.Tests;

public sealed class VersionControlServiceWorkingTreeTests
{
	[Fact]
	public async Task GetWorkingTreeStatusAsync_ParsesChangedFilesFromStatusOutput()
	{
		var executor = new RecordingGitCommandExecutor();
		executor.AddGitResult(
			"status --porcelain=v1 --untracked-files=all",
			new GitCommandResult
			{
				ExitCode = 0,
				Output = " M src/App.cs\n?? src/NewFile.cs\nR  src/OldName.cs -> src/NewName.cs\n"
			});

		var service = new VersionControlService(executor, NullLogger<VersionControlService>.Instance);

		var status = await service.GetWorkingTreeStatusAsync("/repo");

		Assert.True(status.HasUncommittedChanges);
		Assert.Equal(
			new[] { "src/App.cs", "src/NewFile.cs", "src/NewName.cs" },
			status.ChangedFiles.OrderBy(path => path).ToArray());
	}

	[Fact]
	public async Task HardCheckoutBranchAsync_PreservesLocalChangesBeforeReset()
	{
		var executor = new RecordingGitCommandExecutor();
		executor.AddGitResult("fetch origin --prune", new GitCommandResult { ExitCode = 0, Output = "fetch ok" });
		executor.AddGitResult("rev-parse --is-inside-work-tree", new GitCommandResult { ExitCode = 0, Output = "true\n" });
		executor.AddGitResult("status --porcelain=v1 --untracked-files=all", new GitCommandResult { ExitCode = 0, Output = " M src/App.cs\n" });
		executor.AddGitResult("status --porcelain=v1 --untracked-files=all", new GitCommandResult { ExitCode = 0, Output = " M src/App.cs\n" });
		executor.AddGitResult($"stash push --include-untracked --message \"{AppConstants.AppName} auto-preserve before checkout to main\"", new GitCommandResult { ExitCode = 0, Output = "Saved working directory" });
		executor.AddGitResult("rev-parse --verify stash@{0}", new GitCommandResult { ExitCode = 0, Output = "stashref123\n" });
		executor.AddGitResult("clean -fd", new GitCommandResult { ExitCode = 0 });
		executor.AddGitResult("reset --hard HEAD", new GitCommandResult { ExitCode = 0 });
		executor.AddGitResult("rev-parse --verify refs/heads/main", new GitCommandResult { ExitCode = 0, Output = "main\n" });
		executor.AddGitResult("checkout main", new GitCommandResult { ExitCode = 0 });
		executor.AddGitResult("rev-parse --verify refs/remotes/origin/main", new GitCommandResult { ExitCode = 0, Output = "origin/main\n" });
		executor.AddGitResult("reset --hard origin/main", new GitCommandResult { ExitCode = 0 });
		executor.AddGitResult("rev-parse HEAD", new GitCommandResult { ExitCode = 0, Output = "abc123\n" });

		var service = new VersionControlService(executor, NullLogger<VersionControlService>.Instance);

		var result = await service.HardCheckoutBranchAsync("/repo", "main");

		Assert.True(result.Success);
		Assert.NotNull(result.Output);
		Assert.Contains("stashref123", result.Output);
		Assert.Contains(executor.GitCommands, command => command.StartsWith("stash push --include-untracked", StringComparison.Ordinal));
	}

	[Fact]
	public async Task SyncWithOriginAsync_PreservesLocalChangesBeforeReset()
	{
		var executor = new RecordingGitCommandExecutor();
		executor.AddGitResult("rev-parse --abbrev-ref HEAD", new GitCommandResult { ExitCode = 0, Output = "main\n" });
		executor.AddGitResult("fetch origin --prune", new GitCommandResult { ExitCode = 0, Output = "fetch ok" });
		executor.AddGitResult("rev-parse --verify refs/remotes/origin/main", new GitCommandResult { ExitCode = 0, Output = "origin/main\n" });
		executor.AddGitResult("rev-parse --is-inside-work-tree", new GitCommandResult { ExitCode = 0, Output = "true\n" });
		executor.AddGitResult("status --porcelain=v1 --untracked-files=all", new GitCommandResult { ExitCode = 0, Output = "?? src/NewFile.cs\n" });
		executor.AddGitResult("status --porcelain=v1 --untracked-files=all", new GitCommandResult { ExitCode = 0, Output = "?? src/NewFile.cs\n" });
		executor.AddGitResult($"stash push --include-untracked --message \"{AppConstants.AppName} auto-preserve before sync to origin/main\"", new GitCommandResult { ExitCode = 0, Output = "Saved working directory" });
		executor.AddGitResult("rev-parse --verify stash@{0}", new GitCommandResult { ExitCode = 0, Output = "stashref456\n" });
		executor.AddGitResult("clean -fd", new GitCommandResult { ExitCode = 0 });
		executor.AddGitResult("reset --hard origin/main", new GitCommandResult { ExitCode = 0 });
		executor.AddGitResult("rev-parse HEAD", new GitCommandResult { ExitCode = 0, Output = "def456\n" });

		var service = new VersionControlService(executor, NullLogger<VersionControlService>.Instance);

		var result = await service.SyncWithOriginAsync("/repo");

		Assert.True(result.Success);
		Assert.NotNull(result.Output);
		Assert.Contains("stashref456", result.Output);
		Assert.Contains(executor.GitCommands, command => command.StartsWith("stash push --include-untracked", StringComparison.Ordinal));
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
