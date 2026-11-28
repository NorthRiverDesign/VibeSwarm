using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.VersionControl;
using VibeSwarm.Shared.VersionControl.Models;
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
	public async Task Sync_PullsOriginIntoACheckoutClonedWhileItWasEmpty()
	{
		await InitEmptyCloneAsync();
		await PushFirstCommitFromOtherCloneAsync();

		Assert.Equal("main", await _versionControl.GetCurrentBranchAsync(_repo));

		var result = await _versionControl.SyncWithOriginAsync(_repo);

		Assert.True(result.Success, result.Error);
		Assert.Equal("main", result.BranchName);
		Assert.Equal(await GitAsync(_repo, "rev-parse origin/main"), await GitAsync(_repo, "rev-parse HEAD"));
		Assert.True(File.Exists(Path.Combine(_repo, "README.md")));
	}

	[Fact]
	public async Task Sync_SaysWhyWhenNeitherSideHasCommitsYet()
	{
		await InitEmptyCloneAsync();

		var result = await _versionControl.SyncWithOriginAsync(_repo);

		Assert.False(result.Success);
		Assert.Equal("'main' has no commits yet, and origin has no 'main' branch to sync from. Push a first commit to origin, then sync again.", result.Error);
	}

	[Fact]
	public async Task CurrentBranch_IsStillHeadWhenDetached()
	{
		await InitRepositoryAsync();
		await GitAsync(_repo, "checkout -q --detach");

		Assert.Equal("HEAD", await _versionControl.GetCurrentBranchAsync(_repo));
	}

	[Fact]
	public async Task Prepare_PullsOriginIntoACheckoutClonedWhileItWasEmpty()
	{
		await InitEmptyCloneAsync();
		await PushFirstCommitFromOtherCloneAsync();

		await PrepareAsync(CreateProcessor(), new Job { Id = Guid.NewGuid(), GoalPrompt = "x" });

		Assert.Equal("main", await GitAsync(_repo, "rev-parse --abbrev-ref HEAD"));
		Assert.Equal(await GitAsync(_repo, "rev-parse origin/main"), await GitAsync(_repo, "rev-parse HEAD"));
	}

	[Theory]
	[InlineData("main")]
	[InlineData(null)]
	public async Task Prepare_StartsTheFirstJobOnARepositoryWithNoCommits(string? branch)
	{
		await InitEmptyCloneAsync();

		await PrepareAsync(CreateProcessor(), new Job { Id = Guid.NewGuid(), GoalPrompt = "x", Branch = branch });

		Assert.Equal("main", await GitAsync(_repo, "symbolic-ref --short HEAD"));
		Assert.Null(await _versionControl.GetCurrentCommitHashAsync(_repo));
	}

	[Fact]
	public async Task FirstJob_MakesTheFirstCommitAndTheNextJobStartsFromIt()
	{
		await InitEmptyCloneAsync();
		var processor = CreateProcessor();
		var job = new Job { Id = Guid.NewGuid(), GoalPrompt = "x", Branch = "main" };

		await PrepareAsync(processor, job);
		await WriteAsync(_repo, "README.md", "# New project\n");
		var commit = await _versionControl.CommitAllChangesAsync(_repo, "Start the project");
		Assert.True(commit.Success, commit.Error);
		job.GitCommitHash = commit.CommitHash;
		await PushAsync(processor, job);

		Assert.Null(job.ErrorMessage);
		Assert.Equal(commit.CommitHash, await GitAsync(_repo, "rev-parse origin/main"));

		await PrepareAsync(processor, new Job { Id = Guid.NewGuid(), GoalPrompt = "x", Branch = "main" });

		Assert.Equal(commit.CommitHash, await GitAsync(_repo, "rev-parse HEAD"));
	}

	[Fact]
	public async Task Checkpoint_LeavesFilesInPlaceWhenNothingIsCommittedYet()
	{
		await InitEmptyCloneAsync();
		await WriteAsync(_repo, "app.txt", "written before the first commit\n");

		var processor = CreateProcessor();
		await using var dbContext = await CreateDbContextAsync();
		var job = await AddJobAsync(dbContext);
		var preserve = GetMethod("PreserveWorkingTreeBeforeBranchPreparationAsync");
		var baseBranch = await (Task<string?>)preserve.Invoke(processor, [job, _repo, dbContext, false, "test", CancellationToken.None])!;
		await PrepareAsync(processor, job);

		Assert.Null(baseBranch);
		Assert.Null(job.GitCheckpointBranch);
		Assert.Equal("main", await GitAsync(_repo, "symbolic-ref --short HEAD"));
		Assert.Equal("?? app.txt", await GitAsync(_repo, "status --porcelain"));
		Assert.Equal("", await GitAsync(_repo, "branch --list"));
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

	[Fact]
	public async Task Worktree_WorkComesBackIntoAnUntouchedCheckout()
	{
		await InitRepositoryAsync();
		// Claude Code puts its worktrees inside the repository.
		var worktree = await AddWorktreeAsync("-b agent-wt", Path.Combine(_repo, ".claude", "worktrees", "agent"));
		await CommitAsync(worktree, "app.txt", "one\ntwo\nthree\ncommitted in worktree\n", "worktree commit");
		await WriteAsync(worktree, "new.txt", "left uncommitted\n");
		var snapshots = new JobWorkSnapshotService(_git, NullLogger<JobWorkSnapshotService>.Instance);

		Assert.Equal([worktree], await snapshots.ListWorktreesAsync(_repo));
		var keptOn = await snapshots.CollectWorktreeAsync(_repo, worktree, Guid.NewGuid());

		Assert.NotNull(keptOn);
		Assert.Equal("agent-wt", keptOn);
		Assert.False(Directory.Exists(worktree));
		Assert.Equal("", await GitAsync(_repo, "status --porcelain"));
		Assert.True(await snapshots.MergeIntoCleanCheckoutAsync(_repo, keptOn));
		Assert.Empty(await snapshots.ListWorktreesAsync(_repo));
		Assert.Equal("", await GitAsync(_repo, "branch --list agent-wt"));
		Assert.Contains("committed in worktree", await File.ReadAllTextAsync(Path.Combine(_repo, "app.txt")));
		Assert.Equal("left uncommitted\n", await File.ReadAllTextAsync(Path.Combine(_repo, "new.txt")));
	}

	[Fact]
	public async Task Worktree_DetachedWorkIsKeptOnARecoveryBranch()
	{
		await InitRepositoryAsync();
		var worktree = await AddWorktreeAsync("--detach", Path.Combine(_root, "agent-worktree"));
		await WriteAsync(worktree, "app.txt", "worktree version\n");
		var snapshots = new JobWorkSnapshotService(_git, NullLogger<JobWorkSnapshotService>.Instance);

		var keptOn = await snapshots.CollectWorktreeAsync(_repo, worktree, Guid.NewGuid());

		Assert.StartsWith("vibeswarm/recovery/worktree-", keptOn);
		Assert.False(Directory.Exists(worktree));
		Assert.Equal("worktree version", await GitAsync(_repo, $"show {keptOn}:app.txt"));
		Assert.Equal("", await GitAsync(_repo, "status --porcelain"));
	}

	private async Task<string> AddWorktreeAsync(string options, string path)
	{
		await GitAsync(_repo, $"worktree add -q {options} \"{path}\"");
		await ConfigureAsync(path);
		return path;
	}

	[Fact]
	public async Task PowerLoss_PausesTheJobAndResumeGivesItsWorkBack()
	{
		await InitRepositoryAsync();
		var baseCommit = await GitAsync(_repo, "rev-parse HEAD");
		await WriteAsync(_repo, "app.txt", "one\ntwo\nthree\nhalf done\n");
		await WriteAsync(_repo, "new.txt", "started before the power went out\n");

		var services = new ServiceCollection();
		var connection = new SqliteConnection("Data Source=:memory:");
		connection.Open();
		services.AddDbContext<VibeSwarmDbContext>(options => options.UseSqlite(connection));
		await using var provider = services.BuildServiceProvider();
		using (var scope = provider.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
			await db.Database.EnsureCreatedAsync();
		}

		var jobId = Guid.NewGuid();
		using (var scope = provider.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
			var project = new Project { Id = Guid.NewGuid(), Name = "Power Project", WorkingPath = _repo };
			var agent = new Provider { Id = Guid.NewGuid(), Name = "Claude", Type = ProviderType.Claude, IsEnabled = true };
			db.AddRange(project, agent, new Job
			{
				Id = jobId,
				ProjectId = project.Id,
				ProviderId = agent.Id,
				GoalPrompt = "Build it",
				Status = JobStatus.Processing,
				SessionId = "session-1",
				GitCommitBefore = baseCommit,
				StartedAt = DateTime.UtcNow.AddHours(-3),
				WorkerInstanceId = "worker-before-the-power-cut",
				LastHeartbeatAt = DateTime.UtcNow.AddMinutes(-5)
			});
			await db.SaveChangesAsync();
		}

		var processor = new JobProcessingService(
			provider.GetRequiredService<IServiceScopeFactory>(),
			NullLogger<JobProcessingService>.Instance,
			_versionControl,
			projectEnvironmentCredentialService: new NoOpProjectEnvironmentCredentialService(),
			workSnapshots: new JobWorkSnapshotService(_git, NullLogger<JobWorkSnapshotService>.Instance));

		await (Task)typeof(JobProcessingService).GetMethod("RecoverOrphanedJobsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(processor, [CancellationToken.None])!;

		using (var scope = provider.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
			var job = await db.Jobs.SingleAsync(j => j.Id == jobId);
			Assert.Equal(JobStatus.Stalled, job.Status);
			Assert.StartsWith(JobProcessingService.InterruptedUnexpectedly, job.ErrorMessage);
			Assert.Equal(JobStatus.Processing, job.ResumeFromStatus);
			Assert.Equal("session-1", job.SessionId);
			Assert.NotNull(job.WorkSnapshotCommit);
			Assert.Null(job.WorkerInstanceId);
			Assert.Equal("", await GitAsync(_repo, "status --porcelain"));
			Assert.Equal("main", await GitAsync(_repo, "rev-parse --abbrev-ref HEAD"));

			// Resuming restores the work on top of the branch before the agent carries on.
			var restore = GetMethod("RestorePriorRunWorkAsync");
			var result = await (Task<JobWorkRestoreResult?>)restore.Invoke(processor, [job, _repo, db, CancellationToken.None])!;
			Assert.Equal(JobWorkRestoreOutcome.Restored, result!.Outcome);
		}

		Assert.Contains("half done", await File.ReadAllTextAsync(Path.Combine(_repo, "app.txt")));
		Assert.Equal("started before the power went out\n", await File.ReadAllTextAsync(Path.Combine(_repo, "new.txt")));
	}

	[Fact]
	public async Task ARetriedChain_GetsItsFinishedStepsBack_ButNotTheStepThatWasUnderWay()
	{
		await InitRepositoryAsync();
		var baseCommit = await GitAsync(_repo, "rev-parse HEAD");
		var snapshots = new JobWorkSnapshotService(_git, NullLogger<JobWorkSnapshotService>.Instance);
		await using var db = await CreateDbContextAsync();
		var job = await AddJobAsync(db);
		job.TeamRun = true;
		job.Status = JobStatus.New;

		// Step one finished and was saved. Step two was half way when the run failed, and the
		// job's own snapshot holds that half as well.
		await WriteAsync(_repo, "divide.txt", "step one\n");
		var stepOne = await snapshots.SaveAsync(_repo, job.Id, baseCommit);
		await WriteAsync(_repo, "power.txt", "half of step two\n");
		job.WorkSnapshotCommit = await snapshots.SaveAsync(_repo, job.Id, baseCommit);
		db.JobSteps.AddRange(
			new JobStep { JobId = job.Id, Order = 1, Title = "Add divide", Instructions = "Add divide.", Status = JobStepStatus.Done, SnapshotCommit = stepOne },
			new JobStep { JobId = job.Id, Order = 2, Title = "Add power", Instructions = "Add power." });
		await db.SaveChangesAsync();
		Assert.NotNull(stepOne);
		Assert.NotEqual(stepOne, job.WorkSnapshotCommit);

		// The retry starts from a clean checkout of an origin that has moved on meanwhile.
		await PushFromOtherCloneAsync("other.txt", "someone else's work\n");
		await GitAsync(_repo, "clean -fdq");
		await GitAsync(_repo, "pull -q");
		var newBase = await GitAsync(_repo, "rev-parse HEAD");
		Assert.NotEqual(baseCommit, newBase);

		var processor = new JobProcessingService(
			new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
			NullLogger<JobProcessingService>.Instance,
			_versionControl,
			projectEnvironmentCredentialService: new NoOpProjectEnvironmentCredentialService(),
			workSnapshots: snapshots);
		var result = await (Task<JobWorkRestoreResult?>)GetMethod("RestorePriorRunWorkAsync").Invoke(processor, [job, _repo, db, CancellationToken.None])!;

		Assert.Equal(JobWorkRestoreOutcome.Restored, result!.Outcome);
		Assert.Equal("step one\n", await File.ReadAllTextAsync(Path.Combine(_repo, "divide.txt")));
		Assert.False(File.Exists(Path.Combine(_repo, "power.txt")));

		// The step's work is saved again on the branch as it now stands, so what the next step
		// changes is measured from there: neither step one nor origin's commit shows up in it.
		var step = await db.JobSteps.SingleAsync(saved => saved.Order == 1);
		Assert.NotEqual(stepOne, step.SnapshotCommit);
		Assert.Equal(step.SnapshotCommit, job.WorkSnapshotCommit);
		Assert.Equal(newBase, await GitAsync(_repo, $"rev-parse {step.SnapshotCommit}^"));
		await WriteAsync(_repo, "power.txt", "step two\n");
		var stepTwoChange = await snapshots.DiffWorkingTreeAgainstAsync(_repo, step.SnapshotCommit!);
		Assert.Contains("power.txt", stepTwoChange);
		Assert.DoesNotContain("divide.txt", stepTwoChange);
		Assert.DoesNotContain("other.txt", stepTwoChange);
	}

	[Fact]
	public void StaleIndexLock_IsRemovedOnlyWhenOlderThanTheBoot()
	{
		var gitDir = Path.Combine(_root, "locked", ".git");
		Directory.CreateDirectory(gitDir);
		var lockFile = Path.Combine(gitDir, "index.lock");
		File.WriteAllText(lockFile, "");
		File.SetLastWriteTimeUtc(lockFile, DateTime.UtcNow.AddHours(-1));

		Assert.False(JobProcessingService.RemoveStaleGitIndexLock(Path.Combine(_root, "locked"), DateTime.UtcNow.AddHours(-2)));
		Assert.True(File.Exists(lockFile));
		Assert.True(JobProcessingService.RemoveStaleGitIndexLock(Path.Combine(_root, "locked"), DateTime.UtcNow.AddMinutes(-5)));
		Assert.False(File.Exists(lockFile));
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

	// The state `gh repo create --clone` leaves when the new repository has no README: a checkout
	// on a branch with no commits yet, tracking a branch origin doesn't have.
	private async Task InitEmptyCloneAsync()
	{
		await RunAsync(_root, "init -q --bare -b main origin.git");
		foreach (var clone in new[] { _repo, _other })
		{
			await RunAsync(_root, $"clone -q origin.git {Path.GetFileName(clone)}");
			await GitAsync(clone, "symbolic-ref HEAD refs/heads/main");
			await ConfigureAsync(clone);
		}
	}

	private async Task PushFirstCommitFromOtherCloneAsync()
	{
		await CommitAsync(_other, "README.md", "# New project\n", "Initial commit");
		await GitAsync(_other, "push -q origin main");
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
