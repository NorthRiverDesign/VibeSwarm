using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.VersionControl;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

/// <summary>
/// Runs the job pipeline's git steps against real repositories: a bare origin, the project
/// checkout, and a second clone standing in for anyone else who pushes while a job runs.
/// </summary>
public sealed class JobGitSyncTests : IDisposable
{
	private readonly GitCommandExecutor _git = new();
	private readonly VersionControlService _versionControl;
	private readonly string _root;
	private readonly string _repo;
	private readonly string _other;

	public JobGitSyncTests()
	{
		_versionControl = new VersionControlService(_git, NullLogger<VersionControlService>.Instance);
		_root = Path.Combine(Path.GetTempPath(), "vibeswarm-tests", Guid.NewGuid().ToString("N"));
		_repo = Path.Combine(_root, "work");
		_other = Path.Combine(_root, "other");
		Directory.CreateDirectory(_root);
	}

	[Fact]
	public async Task Sync_KeepsUnpushedCommitsOnTopOfOrigin()
	{
		await InitRepositoryAsync();
		await CommitAsync(_repo, "app.txt", "one\ntwo\nthree\nlocal\n", "local work");
		await PushFromOtherCloneAsync("other.txt", "theirs\n");

		var result = await _versionControl.SyncWithOriginAsync(_repo);

		Assert.True(result.Success, result.Error);
		Assert.Equal(1, result.KeptLocalCommits);
		Assert.Null(result.RecoveryBranch);
		Assert.Equal("local work", await GitAsync(_repo, "log -1 --format=%s"));
		Assert.True(File.Exists(Path.Combine(_repo, "other.txt")));
	}

	[Fact]
	public async Task Sync_SavesConflictingUnpushedCommitsBeforeResetting()
	{
		await InitRepositoryAsync();
		await CommitAsync(_repo, "app.txt", "mine\n", "local work");
		var localCommit = await GitAsync(_repo, "rev-parse HEAD");
		await PushFromOtherCloneAsync("app.txt", "theirs\n");

		var result = await _versionControl.SyncWithOriginAsync(_repo);

		Assert.True(result.Success, result.Error);
		Assert.StartsWith("vibeswarm/recovery/main-", result.RecoveryBranch);
		Assert.Equal(localCommit, await GitAsync(_repo, $"rev-parse {result.RecoveryBranch}"));
		Assert.Equal(await GitAsync(_repo, "rev-parse origin/main"), await GitAsync(_repo, "rev-parse HEAD"));
		Assert.Equal("", await GitAsync(_repo, "status --porcelain"));
	}

	[Fact]
	public async Task Checkpoint_LeavesTheCheckoutOnTheProjectBranch()
	{
		await InitRepositoryAsync();
		await WriteAsync(_repo, "app.txt", "left behind by an earlier run\n");

		var processor = CreateProcessor();
		await using var dbContext = await CreateDbContextAsync();
		var job = await AddJobAsync(dbContext);
		var preserve = GetMethod("PreserveWorkingTreeBeforeBranchPreparationAsync");
		var baseBranch = await (Task<string?>)preserve.Invoke(processor, [job, _repo, dbContext, false, "test", CancellationToken.None])!;

		Assert.Equal("main", baseBranch);
		Assert.Equal("main", await GitAsync(_repo, "rev-parse --abbrev-ref HEAD"));
		Assert.Equal("", await GitAsync(_repo, "status --porcelain"));
		Assert.Equal("left behind by an earlier run", await GitAsync(_repo, $"show {job.GitCheckpointBranch}:app.txt"));
	}

	[Fact]
	public async Task Prepare_PullsTheLatestOriginBeforeTheJob()
	{
		await InitRepositoryAsync();
		await PushFromOtherCloneAsync("other.txt", "theirs\n");

		await PrepareAsync(CreateProcessor(), new Job { Id = Guid.NewGuid(), GoalPrompt = "x" });

		Assert.Equal(await GitAsync(_repo, "rev-parse origin/main"), await GitAsync(_repo, "rev-parse HEAD"));
		Assert.True(File.Exists(Path.Combine(_repo, "other.txt")));
	}

	[Fact]
	public async Task Prepare_LeavesARecoveryBranchForTheBranchItCameFrom()
	{
		await InitRepositoryAsync();
		await GitAsync(_repo, "checkout -q -b vibeswarm/recovery/main-20261001-120000-abcdef12");

		await PrepareAsync(CreateProcessor(), new Job { Id = Guid.NewGuid(), GoalPrompt = "x" });

		Assert.Equal("main", await GitAsync(_repo, "rev-parse --abbrev-ref HEAD"));
	}

	[Fact]
	public async Task Prepare_WaitsWhenOriginCannotBeReached()
	{
		await InitRepositoryAsync();
		await GitAsync(_repo, $"remote set-url origin {Path.Combine(_root, "missing.git")}");

		var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(
			() => PrepareAsync(CreateProcessor(), new Job { Id = Guid.NewGuid(), GoalPrompt = "x" }));

		Assert.Equal("GitRemoteUnavailableException", error.GetType().Name);
		Assert.StartsWith("Couldn't pull the latest changes from origin", error.Message);
	}

	[Fact]
	public async Task Prepare_FailsInsteadOfRunningOnTheWrongBranch()
	{
		await InitRepositoryAsync();
		await GitAsync(_repo, "checkout -q -b vibeswarm/recovery/gone-20261001-120000-abcdef12");

		var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(
			() => PrepareAsync(CreateProcessor(), new Job { Id = Guid.NewGuid(), GoalPrompt = "x" }));

		Assert.Equal("GitPreparationException", error.GetType().Name);
	}

	[Fact]
	public async Task Push_ReplaysTheCommitWhenOriginMovedDuringTheJob()
	{
		await InitRepositoryAsync();
		await CommitAsync(_repo, "app.txt", "one\ntwo\nthree\njob\n", "job work");
		await PushFromOtherCloneAsync("other.txt", "theirs\n");
		var job = new Job { Id = Guid.NewGuid(), GoalPrompt = "x" };

		await PushAsync(CreateProcessor(), job);

		Assert.Null(job.ErrorMessage);
		Assert.Equal(await GitAsync(_repo, "rev-parse HEAD"), await GitAsync(_repo, "rev-parse origin/main"));
		Assert.Equal(job.GitCommitHash, await GitAsync(_repo, "rev-parse HEAD"));
		Assert.Equal("job work", await GitAsync(_repo, "log -1 --format=%s origin/main"));
		Assert.Equal("theirs", await GitAsync(_repo, "show origin/main:other.txt"));
	}

	[Fact]
	public async Task Push_KeepsAConflictingCommitAndSaysWhere()
	{
		await InitRepositoryAsync();
		await CommitAsync(_repo, "app.txt", "mine\n", "job work");
		var jobCommit = await GitAsync(_repo, "rev-parse HEAD");
		await PushFromOtherCloneAsync("app.txt", "theirs\n");
		var job = new Job { Id = Guid.NewGuid(), GoalPrompt = "x" };

		await PushAsync(CreateProcessor(), job);

		Assert.Contains("vibeswarm/recovery/main-", job.ErrorMessage);
		var recoveryBranch = (await GitAsync(_repo, "branch --list vibeswarm/recovery/* --format=%(refname:short)")).Trim();
		Assert.Equal(jobCommit, await GitAsync(_repo, $"rev-parse {recoveryBranch}"));
		Assert.Equal("theirs", await GitAsync(_repo, "show origin/main:app.txt"));
	}

	private JobProcessingService CreateProcessor() => new(
		new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
		NullLogger<JobProcessingService>.Instance,
		_versionControl,
		projectEnvironmentCredentialService: new NoOpProjectEnvironmentCredentialService())
	{
		FetchRetryDelays = []
	};

	private async Task PrepareAsync(JobProcessingService processor, Job job)
	{
		await (Task)GetMethod("PrepareWorkingBranchAsync").Invoke(processor, [job, _repo, null, CancellationToken.None])!;
	}

	private async Task PushAsync(JobProcessingService processor, Job job)
	{
		await (Task)GetMethod("PushJobCommitAsync").Invoke(processor, [job, _repo, CancellationToken.None])!;
	}

	private static MethodInfo GetMethod(string name)
	{
		var method = typeof(JobProcessingService).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);
		return method;
	}

	private static async Task<VibeSwarmDbContext> CreateDbContextAsync()
	{
		var connection = new SqliteConnection("Data Source=:memory:");
		connection.Open();
		var dbContext = new VibeSwarmDbContext(new DbContextOptionsBuilder<VibeSwarmDbContext>().UseSqlite(connection).Options);
		await dbContext.Database.EnsureCreatedAsync();
		return dbContext;
	}

	private async Task<Job> AddJobAsync(VibeSwarmDbContext dbContext)
	{
		var project = new Project { Id = Guid.NewGuid(), Name = "Sync Project", WorkingPath = _repo };
		var provider = new Provider { Id = Guid.NewGuid(), Name = "Claude", Type = ProviderType.Claude, IsEnabled = true };
		var job = new Job { Id = Guid.NewGuid(), ProjectId = project.Id, ProviderId = provider.Id, GoalPrompt = "Build it", Status = JobStatus.Processing };
		dbContext.AddRange(project, provider, job);
		await dbContext.SaveChangesAsync();
		return job;
	}

	private async Task InitRepositoryAsync()
	{
		await RunAsync(_root, "init -q --bare -b main origin.git");
		await RunAsync(_root, "clone -q origin.git work");
		await ConfigureAsync(_repo);
		await CommitAsync(_repo, "app.txt", "one\ntwo\nthree\n", "base");
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

	private async Task PushFromOtherCloneAsync(string path, string content)
	{
		await GitAsync(_other, "pull -q");
		await CommitAsync(_other, path, content, "pushed by someone else");
		await GitAsync(_other, "push -q origin main");
	}

	private async Task CommitAsync(string repo, string path, string content, string message)
	{
		await WriteAsync(repo, path, content);
		await GitAsync(repo, "add -A");
		await GitAsync(repo, $"commit -qm \"{message}\"");
	}

	private static Task WriteAsync(string repo, string path, string content) =>
		File.WriteAllTextAsync(Path.Combine(repo, path), content);

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

	private sealed class NoOpProjectEnvironmentCredentialService : IProjectEnvironmentCredentialService
	{
		public void PrepareForStorage(Project project, IReadOnlyCollection<ProjectEnvironment>? existingEnvironments = null) { }
		public void PopulateForEditing(Project? project) { }
		public void PopulateForExecution(Project? project) { }
		public Dictionary<string, string>? BuildJobEnvironmentVariables(Project? project) => null;
	}
}
