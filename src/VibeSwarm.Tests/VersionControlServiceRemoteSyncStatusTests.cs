using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.VersionControl;

namespace VibeSwarm.Tests;

/// <summary>
/// Compares a checkout with origin against real repositories: a bare origin, the project checkout,
/// and a second clone that pushes commits the checkout doesn't have.
/// </summary>
public sealed class VersionControlServiceRemoteSyncStatusTests : IDisposable
{
	private readonly GitCommandExecutor _git = new();
	private readonly VersionControlService _versionControl;
	private readonly string _root;
	private readonly string _repo;
	private readonly string _other;

	public VersionControlServiceRemoteSyncStatusTests()
	{
		_versionControl = new VersionControlService(_git, NullLogger<VersionControlService>.Instance);
		_root = Path.Combine(Path.GetTempPath(), "vibeswarm-tests", Guid.NewGuid().ToString("N"));
		_repo = Path.Combine(_root, "work");
		_other = Path.Combine(_root, "other");
		Directory.CreateDirectory(_root);
	}

	[Fact]
	public async Task UpToDate_IsNotBehind()
	{
		await InitRepositoryAsync();

		var status = await _versionControl.GetRemoteSyncStatusAsync(_repo);

		Assert.Equal("origin/main", status.RemoteBranch);
		Assert.Equal(0, status.CommitsBehind);
		Assert.True(status.IsRemoteReachable);
		Assert.False(status.IsBehind);
	}

	[Fact]
	public async Task FetchedCommits_AreCounted()
	{
		await InitRepositoryAsync();
		await PushFromOtherCloneAsync(2);
		await GitAsync(_repo, "fetch -q origin");

		var status = await _versionControl.GetRemoteSyncStatusAsync(_repo);

		Assert.Equal(2, status.CommitsBehind);
		Assert.True(status.IsBehind);
	}

	[Fact]
	public async Task UnfetchedCommits_AreBehindWithoutACount_AndNothingIsFetched()
	{
		await InitRepositoryAsync();
		var trackingBefore = await GitAsync(_repo, "rev-parse refs/remotes/origin/main");
		await PushFromOtherCloneAsync(1);

		var status = await _versionControl.GetRemoteSyncStatusAsync(_repo);

		Assert.Equal("origin/main", status.RemoteBranch);
		Assert.Null(status.CommitsBehind);
		Assert.True(status.IsBehind);
		Assert.Equal(trackingBefore, await GitAsync(_repo, "rev-parse refs/remotes/origin/main"));
	}

	[Fact]
	public async Task UnpushedLocalCommits_AreNotBehind()
	{
		await InitRepositoryAsync();
		await CommitAsync(_repo, "local.txt", "local\n", "local work");

		var status = await _versionControl.GetRemoteSyncStatusAsync(_repo);

		Assert.Equal(0, status.CommitsBehind);
		Assert.False(status.IsBehind);
	}

	[Fact]
	public async Task BranchMissingOnOrigin_HasNoRemoteBranch()
	{
		await InitRepositoryAsync();
		await GitAsync(_repo, "checkout -q -b feature/local-only");

		var status = await _versionControl.GetRemoteSyncStatusAsync(_repo);

		Assert.Null(status.RemoteBranch);
		Assert.False(status.IsBehind);
	}

	[Fact]
	public async Task UnreachableOrigin_FallsBackToLastFetch()
	{
		await InitRepositoryAsync();
		await PushFromOtherCloneAsync(3);
		await GitAsync(_repo, "fetch -q origin");
		await GitAsync(_repo, $"remote set-url origin \"{Path.Combine(_root, "missing.git")}\"");

		var status = await _versionControl.GetRemoteSyncStatusAsync(_repo);

		Assert.Equal("origin/main", status.RemoteBranch);
		Assert.Equal(3, status.CommitsBehind);
		Assert.False(status.IsRemoteReachable);
	}

	private async Task InitRepositoryAsync()
	{
		await RunAsync(_root, "init -q --bare -b main origin.git");
		await RunAsync(_root, "clone -q origin.git work");
		await ConfigureAsync(_repo);
		await CommitAsync(_repo, "app.txt", "one\n", "base");
		await GitAsync(_repo, "push -q -u origin main");
		await RunAsync(_root, "clone -q origin.git other");
		await ConfigureAsync(_other);
	}

	private async Task ConfigureAsync(string repo)
	{
		await GitAsync(repo, "config user.email tests@example.com");
		await GitAsync(repo, "config user.name Tests");
		await GitAsync(repo, "config commit.gpgsign false");
	}

	private async Task PushFromOtherCloneAsync(int commits)
	{
		for (var i = 0; i < commits; i++)
		{
			await CommitAsync(_other, $"other-{i}.txt", $"{i}\n", $"pushed by someone else {i}");
		}

		await GitAsync(_other, "push -q origin main");
	}

	private async Task CommitAsync(string repo, string path, string content, string message)
	{
		await File.WriteAllTextAsync(Path.Combine(repo, path), content);
		await GitAsync(repo, "add -A");
		await GitAsync(repo, $"commit -qm \"{message}\"");
	}

	private Task<string> GitAsync(string repo, string arguments) => RunAsync(repo, arguments);

	private async Task<string> RunAsync(string workingDirectory, string arguments)
	{
		var result = await _git.ExecuteAsync(arguments, workingDirectory);
		Assert.True(result.Success, $"git {arguments} failed: {result.Error}");
		return result.Output.Trim();
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
		}
	}
}
