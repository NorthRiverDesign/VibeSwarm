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
/// Deploys against real repositories: a bare origin with main (release) and develop
/// (development), the project checkout on develop, and a second clone that pushes hotfixes.
/// </summary>
public sealed class ProjectReleaseTests : IDisposable
{
	private readonly GitCommandExecutor _git = new();
	private readonly VersionControlService _versionControl;
	private readonly ReleaseBranchGit _releaseGit;
	private readonly RecordingJobService _jobService = new();
	private readonly string _root;
	private readonly string _repo;
	private readonly string _other;
	private SqliteConnection? _connection;

	public ProjectReleaseTests()
	{
		_versionControl = new VersionControlService(_git, NullLogger<VersionControlService>.Instance);
		_releaseGit = new ReleaseBranchGit(_git);
		_root = Path.Combine(Path.GetTempPath(), "vibeswarm-tests", Guid.NewGuid().ToString("N"));
		_repo = Path.Combine(_root, "work");
		_other = Path.Combine(_root, "other");
		Directory.CreateDirectory(_root);
	}

	[Fact]
	public async Task Deploy_MergesDevelopmentIntoReleaseAndPushes()
	{
		await InitRepositoryAsync();
		await CommitAndPushAsync(_repo, "develop", "feature.txt", "new feature\n", "Add a feature");
		var (service, project) = await CreateServiceAsync();

		var result = await service.DeployAsync(project.Id);

		Assert.Equal(DeployOutcome.Deployed, result.Outcome);
		await GitAsync(_other, "fetch -q origin");
		Assert.True(await IsAncestorAsync(_other, "origin/develop", "origin/main"));
		Assert.Equal("Merge branch 'develop' into main", await GitAsync(_other, "log -1 --format=%s origin/main"));
		var status = await service.GetStatusAsync(project.Id);
		Assert.Equal(0, status.PendingCount);
	}

	[Fact]
	public async Task Deploy_WithNothingWaiting_ReportsNothingToDeploy()
	{
		await InitRepositoryAsync();
		var (service, project) = await CreateServiceAsync();

		var result = await service.DeployAsync(project.Id);

		Assert.Equal(DeployOutcome.NothingToDeploy, result.Outcome);
	}

	[Fact]
	public async Task Deploy_LeavesOutDevelopmentCommitsThatWereNeverPushed()
	{
		await InitRepositoryAsync();
		await CommitAsync(_repo, "local.txt", "not pushed\n", "Local only");
		var (service, project) = await CreateServiceAsync();

		var status = await service.GetStatusAsync(project.Id);
		var result = await service.DeployAsync(project.Id);

		Assert.Equal(0, status.PendingCount);
		Assert.Equal(DeployOutcome.NothingToDeploy, result.Outcome);
		await GitAsync(_other, "fetch -q origin");
		Assert.Equal("base", await GitAsync(_other, "log -1 --format=%s origin/main"));
	}

	[Fact]
	public async Task Deploy_WhenReleaseConflicts_QueuesAProviderJobAndPushesNothing()
	{
		await InitRepositoryAsync();
		await CreateConflictAsync();
		await GitAsync(_other, "fetch -q origin");
		var releaseBefore = await GitAsync(_other, "rev-parse origin/main");
		var (service, project) = await CreateServiceAsync();

		var result = await service.DeployAsync(project.Id);

		Assert.Equal(DeployOutcome.ResolvingConflicts, result.Outcome);
		var job = Assert.Single(_jobService.Created);
		Assert.Equal(result.JobId, job.Id);
		Assert.Equal("develop", job.Branch);
		Assert.Equal("main", job.MergeFromBranch);
		Assert.Equal(AutoCommitMode.CommitAndPush, job.CommitModeOverride);
		Assert.Equal(GitChangeDeliveryMode.CommitToBranch, job.GitChangeDeliveryMode);
		Assert.True(job.Priority > 0);
		Assert.Contains("app.txt", job.GoalPrompt);
		await GitAsync(_other, "fetch -q origin");
		Assert.Equal(releaseBefore, await GitAsync(_other, "rev-parse origin/main"));
	}

	[Fact]
	public async Task Deploy_WhileAConflictJobIsActive_WaitsForIt()
	{
		await InitRepositoryAsync();
		await CreateConflictAsync();
		var (service, project) = await CreateServiceAsync();
		var provider = new Provider { Id = Guid.NewGuid(), Name = "Claude", Type = ProviderType.Claude, IsEnabled = true };
		var resolving = new Job { Id = Guid.NewGuid(), ProjectId = project.Id, ProviderId = provider.Id, GoalPrompt = "Resolve", MergeFromBranch = "main", Status = JobStatus.Processing };
		await using (var dbContext = CreateDbContext())
		{
			dbContext.AddRange(provider, resolving);
			await dbContext.SaveChangesAsync();
		}

		var result = await service.DeployAsync(project.Id);
		var status = await service.GetStatusAsync(project.Id);

		Assert.Equal(DeployOutcome.ResolvingConflicts, result.Outcome);
		Assert.Equal(resolving.Id, result.JobId);
		Assert.Equal(resolving.Id, status.ResolvingJobId);
		Assert.Empty(_jobService.Created);
	}

	[Fact]
	public async Task ResolvedMerge_IsCommittedAsAMergeAndTheNextDeployGoesThrough()
	{
		await InitRepositoryAsync();
		await CreateConflictAsync();
		await GitAsync(_repo, "fetch -q origin");

		var merge = await _releaseGit.StartMergeAsync(_repo, "main");

		Assert.Equal(BranchMergeOutcome.Conflicted, merge.Outcome);
		Assert.Equal(["app.txt"], merge.ConflictedFiles);
		Assert.Contains("<<<<<<< ", await File.ReadAllTextAsync(Path.Combine(_repo, "app.txt")));

		await WriteAsync(_repo, "app.txt", "one\nhotfix and feature\nthree\n");
		var commit = await _versionControl.CommitAllChangesAsync(_repo, "Resolve conflicts with main");
		Assert.True(commit.Success, commit.Error);
		Assert.Equal(2, await CountParentsAsync(_repo, "HEAD"));
		await GitAsync(_repo, "push -q origin develop");

		var (service, project) = await CreateServiceAsync();
		var result = await service.DeployAsync(project.Id);

		Assert.Equal(DeployOutcome.Deployed, result.Outcome);
		await GitAsync(_other, "fetch -q origin");
		Assert.Equal("one\nhotfix and feature\nthree", await GitAsync(_other, "show origin/main:app.txt"));
	}

	[Fact]
	public async Task StartMerge_AroundRestoredWork_RecordsTheMergeWithoutRedoingIt()
	{
		await InitRepositoryAsync();
		await CreateConflictAsync();
		await GitAsync(_repo, "fetch -q origin");
		await WriteAsync(_repo, "app.txt", "one\n<<<<<<< HEAD\nhalf done\n=======\nhotfix\n>>>>>>> origin/main\nthree\n");

		var merge = await _releaseGit.StartMergeAsync(_repo, "main");

		Assert.Equal(BranchMergeOutcome.Conflicted, merge.Outcome);
		Assert.Equal(["app.txt"], merge.ConflictedFiles);
		await WriteAsync(_repo, "app.txt", "one\nhotfix and feature\nthree\n");
		var commit = await _versionControl.CommitAllChangesAsync(_repo, "Resolve conflicts with main");
		Assert.True(commit.Success, commit.Error);
		Assert.Equal(2, await CountParentsAsync(_repo, "HEAD"));
		Assert.True(await IsAncestorAsync(_repo, "origin/main", "HEAD"));
	}

	[Fact]
	public async Task StartMerge_WhenReleaseIsAlreadyMerged_ReportsNothingToDo()
	{
		await InitRepositoryAsync();

		var merge = await _releaseGit.StartMergeAsync(_repo, "main");

		Assert.Equal(BranchMergeOutcome.AlreadyMerged, merge.Outcome);
		Assert.Empty(await GitAsync(_repo, "status --porcelain"));
	}

	[Fact]
	public async Task GetStatus_ListsWhatIsWaitingNewestFirst()
	{
		await InitRepositoryAsync();
		await CommitAndPushAsync(_repo, "develop", "a.txt", "a\n", "First change");
		await CommitAndPushAsync(_repo, "develop", "b.txt", "b\n", "Second change");
		var (service, project) = await CreateServiceAsync();

		var status = await service.GetStatusAsync(project.Id);

		Assert.Null(status.Error);
		Assert.Equal(2, status.PendingCount);
		Assert.Equal(["Second change", "First change"], status.PendingChanges.Select(change => change.Subject));
		Assert.Equal("base", status.ReleaseHead?.Subject);
		Assert.All(status.PendingChanges, change => Assert.NotEqual(default, change.CommittedAt));
	}

	[Fact]
	public async Task GetStatus_ReportsABranchThatDoesNotExist()
	{
		await InitRepositoryAsync();
		var (service, project) = await CreateServiceAsync(releaseBranch: "release");

		var status = await service.GetStatusAsync(project.Id);

		Assert.Contains("'release'", status.Error);
	}

	[Fact]
	public async Task PrepareWorkingBranch_UsesTheProjectsDevelopmentBranch()
	{
		await InitRepositoryAsync();
		await GitAsync(_repo, "checkout -q main");
		var job = new Job
		{
			Id = Guid.NewGuid(),
			GoalPrompt = "Build it",
			Project = new Project { Name = "Release", WorkingPath = _repo, DevelopmentBranch = "develop" }
		};

		await InvokeAsync(CreateProcessor(), "PrepareWorkingBranchAsync", job, _repo, null, CancellationToken.None);

		Assert.Equal("develop", await GitAsync(_repo, "rev-parse --abbrev-ref HEAD"));
	}

	[Fact]
	public async Task Delivery_RefusesAMergeThatStillHasConflictMarkers()
	{
		await InitRepositoryAsync();
		await CreateConflictAsync();
		await GitAsync(_repo, "fetch -q origin");
		var merge = await _releaseGit.StartMergeAsync(_repo, "main");
		var job = new Job { Id = Guid.NewGuid(), GoalPrompt = "Resolve", MergeFromBranch = "main" };
		var processor = CreateProcessor();

		var blocked = await InvokeAsync<bool>(processor, "VerifyBranchMergeResolvedAsync", job, _repo, merge, CancellationToken.None);
		await WriteAsync(_repo, "app.txt", "one\nresolved\nthree\n");
		var allowed = await InvokeAsync<bool>(processor, "VerifyBranchMergeResolvedAsync", job, _repo, merge, CancellationToken.None);

		Assert.False(blocked);
		Assert.Contains("- app.txt", job.BuildOutput);
		Assert.True(allowed);
	}

	[Fact]
	public void MergeRules_ListTheConflictsAndKeepTheAgentAwayFromGit()
	{
		var rules = PromptBuilder.BuildBranchMergeRules("main", BranchMergeOutcome.Conflicted, ["app.txt"]);

		Assert.Contains("  - app.txt", rules);
		Assert.Contains("Do not run git commit", rules);
		Assert.Null(PromptBuilder.BuildBranchMergeRules(null, BranchMergeOutcome.Conflicted, ["app.txt"]));
	}

	private async Task CreateConflictAsync()
	{
		await CommitAndPushAsync(_other, "main", "app.txt", "one\nhotfix\nthree\n", "Hotfix on main");
		await CommitAndPushAsync(_repo, "develop", "app.txt", "one\nfeature\nthree\n", "Feature on develop");
	}

	private async Task<(ProjectReleaseService Service, Project Project)> CreateServiceAsync(string releaseBranch = "main")
	{
		_connection ??= new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		var project = new Project { Id = Guid.NewGuid(), Name = "Release", WorkingPath = _repo, DevelopmentBranch = "develop", ReleaseBranch = releaseBranch };
		await using (var setup = CreateDbContext())
		{
			await setup.Database.EnsureCreatedAsync();
			setup.Projects.Add(project);
			await setup.SaveChangesAsync();
		}

		var service = new ProjectReleaseService(CreateDbContext(), _versionControl, _releaseGit, _jobService, NullLogger<ProjectReleaseService>.Instance);
		return (service, project);
	}

	private VibeSwarmDbContext CreateDbContext() =>
		new(new DbContextOptionsBuilder<VibeSwarmDbContext>().UseSqlite(_connection!).Options);

	private JobProcessingService CreateProcessor() => new(
		new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
		NullLogger<JobProcessingService>.Instance,
		_versionControl,
		projectEnvironmentCredentialService: new NoOpProjectEnvironmentCredentialService(),
		workSnapshots: new JobWorkSnapshotService(_git, NullLogger<JobWorkSnapshotService>.Instance),
		releaseGit: _releaseGit)
	{
		FetchRetryDelays = []
	};

	private static async Task InvokeAsync(JobProcessingService processor, string name, params object?[] arguments) =>
		await (Task)GetMethod(name).Invoke(processor, arguments)!;

	private static async Task<T> InvokeAsync<T>(JobProcessingService processor, string name, params object?[] arguments) =>
		await (Task<T>)GetMethod(name).Invoke(processor, arguments)!;

	private static MethodInfo GetMethod(string name)
	{
		var method = typeof(JobProcessingService).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);
		return method;
	}

	private async Task InitRepositoryAsync()
	{
		await RunAsync(_root, "init -q --bare -b main origin.git");
		await RunAsync(_root, "clone -q origin.git work");
		await ConfigureAsync(_repo);
		await CommitAsync(_repo, "app.txt", "one\ntwo\nthree\n", "base");
		await GitAsync(_repo, "push -q -u origin main");
		await GitAsync(_repo, "checkout -q -b develop");
		await GitAsync(_repo, "push -q -u origin develop");
		await RunAsync(_root, "clone -q origin.git other");
		await ConfigureAsync(_other);
	}

	private async Task ConfigureAsync(string repo)
	{
		await GitAsync(repo, "config user.email tests@example.com");
		await GitAsync(repo, "config user.name Tests");
		await GitAsync(repo, "config commit.gpgsign false");
	}

	private async Task CommitAndPushAsync(string repo, string branch, string path, string content, string message)
	{
		await GitAsync(repo, "fetch -q origin");
		await GitAsync(repo, $"checkout -q -B {branch} origin/{branch}");
		await CommitAsync(repo, path, content, message);
		await GitAsync(repo, $"push -q origin {branch}");
	}

	private async Task CommitAsync(string repo, string path, string content, string message)
	{
		await WriteAsync(repo, path, content);
		await GitAsync(repo, "add -A");
		await GitAsync(repo, $"commit -qm \"{message}\"");
	}

	private static Task WriteAsync(string repo, string path, string content) =>
		File.WriteAllTextAsync(Path.Combine(repo, path), content);

	private async Task<int> CountParentsAsync(string repo, string revision) =>
		(await GitAsync(repo, $"rev-list --parents -n 1 {revision}")).Split(' ').Length - 1;

	private async Task<bool> IsAncestorAsync(string repo, string ancestor, string descendant) =>
		(await _git.ExecuteAsync($"merge-base --is-ancestor {ancestor} {descendant}", repo)).Success;

	private Task<string> GitAsync(string repo, string arguments) => RunAsync(repo, arguments);

	private async Task<string> RunAsync(string workingDirectory, string arguments)
	{
		var result = await _git.ExecuteAsync(arguments, workingDirectory);
		Assert.True(result.Success, $"git {arguments} failed: {result.Error}");
		return result.Output.Trim();
	}

	public void Dispose()
	{
		_connection?.Dispose();
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
		}
	}

	private sealed class RecordingJobService : FakeJobServiceBase
	{
		public List<Job> Created { get; } = [];

		public override Task<Job> CreateAsync(Job job, CancellationToken cancellationToken = default)
		{
			job.Id = Guid.NewGuid();
			Created.Add(job);
			return Task.FromResult(job);
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
