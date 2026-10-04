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

public sealed class JobWorkSnapshotTests : IDisposable
{
	private readonly GitCommandExecutor _git = new();
	private readonly JobWorkSnapshotService _snapshots;
	private readonly string _root;
	private readonly string _origin;
	private readonly string _repo;

	public JobWorkSnapshotTests()
	{
		_snapshots = new JobWorkSnapshotService(_git, NullLogger<JobWorkSnapshotService>.Instance);
		_root = Path.Combine(Path.GetTempPath(), "vibeswarm-tests", Guid.NewGuid().ToString("N"));
		_origin = Path.Combine(_root, "origin.git");
		_repo = Path.Combine(_root, "work");
		Directory.CreateDirectory(_root);
	}

	[Fact]
	public async Task SaveAsync_KeepsUncommittedWorkUnderJobRefWithoutTouchingTheCheckout()
	{
		var baseCommit = await InitRepositoryAsync();
		var jobId = Guid.NewGuid();
		await WriteAsync("app.txt", "one\nTWO\nthree\n");
		await WriteAsync("src/New.cs", "class New {}\n");
		await WriteAsync("build.log", "ignored\n");
		var statusBefore = await GitAsync("status --porcelain");

		var snapshot = await _snapshots.SaveAsync(_repo, jobId, baseCommit);

		Assert.NotNull(snapshot);
		Assert.Equal(statusBefore, await GitAsync("status --porcelain"));
		Assert.Equal(baseCommit, await GitAsync("rev-parse HEAD"));
		Assert.Equal(baseCommit, await GitAsync($"rev-parse {snapshot}^"));
		Assert.Contains(snapshot, await GitAsync($"for-each-ref --format=%(objectname) {JobWorkSnapshotService.RefPrefix}/{jobId:N}/"));

		var savedFiles = await GitAsync($"ls-tree -r --name-only {snapshot}");
		Assert.Contains("src/New.cs", savedFiles);
		Assert.DoesNotContain("build.log", savedFiles);
		Assert.Equal("one\nTWO\nthree", await GitAsync($"show {snapshot}:app.txt"));
	}

	[Fact]
	public async Task SaveAsync_SkipsRunsThatChangedNothing()
	{
		var baseCommit = await InitRepositoryAsync();

		Assert.Null(await _snapshots.SaveAsync(_repo, Guid.NewGuid(), baseCommit));
	}

	[Fact]
	public async Task SaveAsync_SkipsWorkThatIsAlreadyPushed()
	{
		var baseCommit = await InitRepositoryAsync();
		await WriteAsync("app.txt", "pushed\n");
		await GitAsync("commit -qam pushed");
		await GitAsync("push -q origin main");

		Assert.Null(await _snapshots.SaveAsync(_repo, Guid.NewGuid(), baseCommit));
	}

	[Fact]
	public async Task SaveAsync_KeepsLocalCommitsThatWereNeverPushed()
	{
		var baseCommit = await InitRepositoryAsync();
		await WriteAsync("app.txt", "committed locally\n");
		await GitAsync("commit -qam local");

		var snapshot = await _snapshots.SaveAsync(_repo, Guid.NewGuid(), baseCommit);

		Assert.NotNull(snapshot);
		Assert.Equal(baseCommit, await GitAsync($"rev-parse {snapshot}^"));
		Assert.Equal("committed locally", await GitAsync($"show {snapshot}:app.txt"));
	}

	[Fact]
	public async Task FollowUp_RestoresEarlierWorkOnTopOfNewerBranchChanges()
	{
		var baseCommit = await InitRepositoryAsync();
		var jobId = Guid.NewGuid();

		// The first run leaves its work uncommitted (verification failed, or auto-commit is off).
		await WriteAsync("app.txt", "one\nTWO\nthree\n");
		await WriteAsync("src/New.cs", "class New {}\n");
		var snapshot = await _snapshots.SaveAsync(_repo, jobId, baseCommit);
		Assert.NotNull(snapshot);

		// The next job's pre-run reset wipes it, and that job pushes an unrelated change.
		await ResetToOriginAsync();
		await WriteAsync("other.txt", "from another job\n");
		await GitAsync("add -A");
		await GitAsync("commit -qm other");
		await GitAsync("push -q origin main");

		// The follow-up starts from the synced branch and restores the first run's work.
		await ResetToOriginAsync();
		var result = await _snapshots.RestoreAsync(_repo, snapshot);

		Assert.Equal(JobWorkRestoreOutcome.Restored, result.Outcome);
		Assert.Empty(result.ConflictedFiles);
		Assert.Contains("app.txt", result.Files);
		Assert.Contains("src/New.cs", result.Files);
		Assert.Equal("one\nTWO\nthree\n", await ReadAsync("app.txt"));
		Assert.True(File.Exists(Path.Combine(_repo, "src", "New.cs")));
		Assert.True(File.Exists(Path.Combine(_repo, "other.txt")));
		Assert.Empty(await GitAsync("diff --cached --name-only"));

		// Delivering both together leaves nothing at risk, so no new snapshot is needed.
		var followUpBase = await GitAsync("rev-parse HEAD");
		await WriteAsync("src/Follow.cs", "class Follow {}\n");
		await GitAsync("add -A");
		await GitAsync("commit -qm follow-up");
		await GitAsync("push -q origin main");
		Assert.Null(await _snapshots.SaveAsync(_repo, jobId, followUpBase));
		Assert.Contains("src/New.cs", await GitAsync("ls-tree -r --name-only origin/main"));
	}

	[Fact]
	public async Task RestoreAsync_LeavesConflictMarkersWhenTheBranchMovedTheSameLines()
	{
		var baseCommit = await InitRepositoryAsync();
		await WriteAsync("app.txt", "one\nJOB\nthree\n");
		var snapshot = await _snapshots.SaveAsync(_repo, Guid.NewGuid(), baseCommit);
		Assert.NotNull(snapshot);

		await ResetToOriginAsync();
		await WriteAsync("app.txt", "one\nOTHER\nthree\n");
		await GitAsync("commit -qam other");

		var result = await _snapshots.RestoreAsync(_repo, snapshot);

		Assert.Equal(JobWorkRestoreOutcome.Conflicted, result.Outcome);
		Assert.Equal(new[] { "app.txt" }, result.ConflictedFiles);
		Assert.Contains("<<<<<<< ", await ReadAsync("app.txt"));
		Assert.Empty(await GitAsync("diff --name-only --diff-filter=U"));
		Assert.Equal(new[] { "app.txt" }, await _snapshots.FindUnresolvedConflictsAsync(_repo, result.ConflictedFiles));

		await WriteAsync("app.txt", "one\nOTHER and JOB\nthree\n");
		Assert.Empty(await _snapshots.FindUnresolvedConflictsAsync(_repo, result.ConflictedFiles));
	}

	[Fact]
	public async Task RestoreAsync_ReportsWorkThatIsAlreadyPresent()
	{
		var baseCommit = await InitRepositoryAsync();
		await WriteAsync("app.txt", "done\n");
		var snapshot = await _snapshots.SaveAsync(_repo, Guid.NewGuid(), baseCommit);
		Assert.NotNull(snapshot);
		await GitAsync("commit -qam done");

		var result = await _snapshots.RestoreAsync(_repo, snapshot);

		Assert.Equal(JobWorkRestoreOutcome.AlreadyOnBranch, result.Outcome);
		Assert.Empty(await GitAsync("status --porcelain"));
	}

	[Fact]
	public async Task RestoreAsync_FailsForUnknownSnapshots()
	{
		await InitRepositoryAsync();

		var result = await _snapshots.RestoreAsync(_repo, "0123456789abcdef0123456789abcdef01234567");

		Assert.Equal(JobWorkRestoreOutcome.Failed, result.Outcome);
		Assert.Equal(JobWorkRestoreOutcome.Failed, (await _snapshots.RestoreAsync(_repo, "HEAD; rm -rf")).Outcome);
	}

	[Fact]
	public async Task RestorePriorRunWork_RestoresTheLatestSavedRunForAFollowUp()
	{
		var baseCommit = await InitRepositoryAsync();
		await WriteAsync("app.txt", "first run\n");
		var firstRun = await _snapshots.SaveAsync(_repo, Guid.NewGuid(), baseCommit);
		await WriteAsync("app.txt", "second run\n");
		var secondRun = await _snapshots.SaveAsync(_repo, Guid.NewGuid(), baseCommit);
		await ResetToOriginAsync();

		var (result, _) = await RestoreThroughProcessingServiceAsync(
			new JobChangeSet { FollowUpIndex = 0, WorkSnapshotCommit = firstRun },
			new JobChangeSet { FollowUpIndex = 1, WorkSnapshotCommit = secondRun },
			// The run that failed before it could save anything falls back to the one before it.
			new JobChangeSet { FollowUpIndex = 2 });

		Assert.NotNull(result);
		Assert.Equal(JobWorkRestoreOutcome.Restored, result.Outcome);
		Assert.Equal("second run\n", await ReadAsync("app.txt"));
	}

	[Fact]
	public async Task RestorePriorRunWork_StopsAtARunWhoseCommitIsAlreadyOnTheBranch()
	{
		var baseCommit = await InitRepositoryAsync();
		await WriteAsync("app.txt", "first run\n");
		var firstRun = await _snapshots.SaveAsync(_repo, Guid.NewGuid(), baseCommit);
		await WriteAsync("app.txt", "delivered\n");
		await GitAsync("commit -qam delivered");
		var delivered = await GitAsync("rev-parse HEAD");

		var (result, activity) = await RestoreThroughProcessingServiceAsync(
			new JobChangeSet { FollowUpIndex = 0, WorkSnapshotCommit = firstRun },
			new JobChangeSet { FollowUpIndex = 1, GitCommitHash = delivered });

		Assert.NotNull(result);
		Assert.Equal(JobWorkRestoreOutcome.AlreadyOnBranch, result.Outcome);
		Assert.Equal("delivered\n", await ReadAsync("app.txt"));
		Assert.Null(activity);
	}

	[Fact]
	public async Task RestorePriorRunWork_DoesNothingForAFirstRun()
	{
		await InitRepositoryAsync();

		var (result, _) = await RestoreThroughProcessingServiceAsync();

		Assert.Null(result);
	}

	[Fact]
	public void BuildPriorWorkRules_TellsTheAgentWhatWasRestored()
	{
		Assert.Null(PromptBuilder.BuildPriorWorkRules(null));

		var restored = PromptBuilder.BuildPriorWorkRules(new JobWorkRestoreResult
		{
			Outcome = JobWorkRestoreOutcome.Restored,
			Files = ["app.txt", "src/New.cs"]
		});
		Assert.NotNull(restored);
		Assert.StartsWith("EARLIER RUNS OF THIS JOB:", restored);
		Assert.Contains("(2 file(s))", restored);
		Assert.Contains("do not revert", restored);

		var conflicted = PromptBuilder.BuildPriorWorkRules(new JobWorkRestoreResult
		{
			Outcome = JobWorkRestoreOutcome.Conflicted,
			Files = ["app.txt"],
			ConflictedFiles = ["app.txt"]
		});
		Assert.Contains("  - app.txt", conflicted);
		Assert.Contains("will not deliver", conflicted);

		var failed = PromptBuilder.BuildPriorWorkRules(JobWorkRestoreResult.Failed("gone\nsecond line"));
		Assert.Contains("could not re-apply the changes from the earlier runs of this job: gone", failed);
		Assert.DoesNotContain("second line", failed);
	}

	private async Task<(JobWorkRestoreResult? Result, string? Activity)> RestoreThroughProcessingServiceAsync(params JobChangeSet[] priorRuns)
	{
		using var connection = new SqliteConnection("Data Source=:memory:");
		connection.Open();
		var options = new DbContextOptionsBuilder<VibeSwarmDbContext>().UseSqlite(connection).Options;
		await using var dbContext = new VibeSwarmDbContext(options);
		await dbContext.Database.EnsureCreatedAsync();

		var project = new Project { Id = Guid.NewGuid(), Name = "Snapshot Project", WorkingPath = _repo };
		var provider = new Provider { Id = Guid.NewGuid(), Name = "Copilot", Type = ProviderType.Copilot, IsEnabled = true };
		var job = new Job { Id = Guid.NewGuid(), ProjectId = project.Id, ProviderId = provider.Id, GoalPrompt = "Build it", Status = JobStatus.Processing };
		dbContext.AddRange(project, provider, job);
		foreach (var run in priorRuns)
		{
			run.Id = Guid.NewGuid();
			run.JobId = job.Id;
			dbContext.JobChangeSets.Add(run);
		}
		await dbContext.SaveChangesAsync();

		var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
		var processor = new JobProcessingService(
			scopeFactory,
			NullLogger<JobProcessingService>.Instance,
			new VersionControlService(_git, NullLogger<VersionControlService>.Instance),
			projectEnvironmentCredentialService: new NoOpProjectEnvironmentCredentialService(),
			workSnapshots: _snapshots);

		var method = typeof(JobProcessingService).GetMethod("RestorePriorRunWorkAsync", BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);
		var result = await (Task<JobWorkRestoreResult?>)method.Invoke(processor, [job, _repo, dbContext, CancellationToken.None])!;

		var activity = await dbContext.Jobs.AsNoTracking().Where(j => j.Id == job.Id).Select(j => j.CurrentActivity).SingleAsync();
		return (result, activity);
	}

	private async Task<string> InitRepositoryAsync()
	{
		await RunAsync(_root, "init -q --bare -b main origin.git");
		await RunAsync(_root, "clone -q origin.git work");
		await GitAsync("symbolic-ref HEAD refs/heads/main");
		await GitAsync("config user.email tests@example.com");
		await GitAsync("config user.name Tests");
		await GitAsync("config commit.gpgsign false");
		await WriteAsync(".gitignore", "*.log\n");
		await WriteAsync("app.txt", "one\ntwo\nthree\n");
		await GitAsync("add -A");
		await GitAsync("commit -qm base");
		await GitAsync("push -q -u origin main");
		return await GitAsync("rev-parse HEAD");
	}

	private async Task ResetToOriginAsync()
	{
		await GitAsync("fetch -q origin");
		await GitAsync("reset -q --hard origin/main");
		await GitAsync("clean -fdq");
	}

	private Task WriteAsync(string path, string content)
	{
		var fullPath = Path.Combine(_repo, path);
		Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
		return File.WriteAllTextAsync(fullPath, content);
	}

	private Task<string> ReadAsync(string path) => File.ReadAllTextAsync(Path.Combine(_repo, path));

	private Task<string> GitAsync(string arguments) => RunAsync(_repo, arguments);

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
