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
/// Team swarm members working side by side in worktrees of their own, against real repositories:
/// a bare origin and the project checkout.
/// </summary>
public sealed class SwarmWorktreeTests : IDisposable
{
	private readonly GitCommandExecutor _git = new();
	private readonly SwarmWorktrees _worktrees;
	private readonly string _root;
	private readonly string _repo;
	private readonly Guid _projectId = Guid.NewGuid();

	public SwarmWorktreeTests()
	{
		_root = Path.Combine(Path.GetTempPath(), "vibeswarm-tests", Guid.NewGuid().ToString("N"));
		_repo = Path.Combine(_root, "work");
		Directory.CreateDirectory(_root);
		_worktrees = new SwarmWorktrees(_git, NullLogger<SwarmWorktrees>.Instance, Path.Combine(_root, "worktrees"));
	}

	[Fact]
	public async Task Members_WorkApart_AndBothLandOnTheBranch()
	{
		await InitRepositoryAsync();
		var first = await _worktrees.OpenAsync(_repo, _projectId, Guid.NewGuid());
		var second = await _worktrees.OpenAsync(_repo, _projectId, Guid.NewGuid());

		Assert.False(first.Reused);
		Assert.StartsWith(SwarmWorktrees.BranchPrefix, first.Branch);
		Assert.True(_worktrees.IsSwarmWorktree(first.Path));
		Assert.False(_worktrees.IsSwarmWorktree(_repo));

		await CommitAsync(first.Path, "api.txt", "endpoint\n", "Add the endpoint");
		await CommitAsync(second.Path, "ui.txt", "page\n", "Add the page");
		Assert.False(File.Exists(Path.Combine(first.Path, "ui.txt")));

		var firstResult = await _worktrees.IntegrateAsync(_repo, first.Path);
		var secondResult = await _worktrees.IntegrateAsync(_repo, second.Path);

		Assert.Equal(SwarmIntegrationOutcome.Integrated, firstResult.Outcome);
		Assert.Equal(SwarmIntegrationOutcome.Integrated, secondResult.Outcome);
		Assert.Equal(secondResult.CommitHash, await GitAsync(_repo, "rev-parse HEAD"));
		Assert.Equal("Add the page\nAdd the endpoint\nbase", await GitAsync(_repo, "log --format=%s"));
		Assert.Equal("", await GitAsync(_repo, "status --porcelain"));

		Assert.Null(await _worktrees.ReleaseAsync(_repo, first.Path, "first"));
		Assert.Null(await _worktrees.ReleaseAsync(_repo, second.Path, "second"));
		Assert.False(Directory.Exists(first.Path));
		Assert.Equal("", await GitAsync(_repo, $"branch --list \"{SwarmWorktrees.BranchPrefix}*\""));
	}

	[Fact]
	public async Task OverlappingWork_IsLeftOnItsBranch_WithBothSidesUntouched()
	{
		await InitRepositoryAsync();
		var first = await _worktrees.OpenAsync(_repo, _projectId, Guid.NewGuid());
		var second = await _worktrees.OpenAsync(_repo, _projectId, Guid.NewGuid());
		await CommitAsync(first.Path, "app.txt", "one\nmine\nthree\n", "First take");
		await CommitAsync(second.Path, "app.txt", "one\ntheirs\nthree\n", "Second take");
		var secondHead = await GitAsync(second.Path, "rev-parse HEAD");

		Assert.Equal(SwarmIntegrationOutcome.Integrated, (await _worktrees.IntegrateAsync(_repo, first.Path)).Outcome);
		var checkoutHead = await GitAsync(_repo, "rev-parse HEAD");
		var conflict = await _worktrees.IntegrateAsync(_repo, second.Path);

		Assert.Equal(SwarmIntegrationOutcome.Conflicted, conflict.Outcome);
		Assert.Equal(["app.txt"], conflict.ConflictedFiles);
		Assert.Equal(checkoutHead, await GitAsync(_repo, "rev-parse HEAD"));
		Assert.Equal(secondHead, await GitAsync(second.Path, "rev-parse HEAD"));
		Assert.Equal("", await GitAsync(second.Path, "status --porcelain"));

		var kept = await _worktrees.ReleaseAsync(_repo, second.Path, "second");

		Assert.Equal(second.Branch, kept);
		Assert.Equal(secondHead, await GitAsync(_repo, $"rev-parse {kept}"));
		Assert.False(Directory.Exists(second.Path));
	}

	[Fact]
	public async Task Release_CommitsLeftoversToTheBranch()
	{
		await InitRepositoryAsync();
		var worktree = await _worktrees.OpenAsync(_repo, _projectId, Guid.NewGuid());
		await File.WriteAllTextAsync(Path.Combine(worktree.Path, "half-done.txt"), "work in progress\n");

		var kept = await _worktrees.ReleaseAsync(_repo, worktree.Path, "member");

		Assert.Equal(worktree.Branch, kept);
		Assert.Equal("work in progress", await GitAsync(_repo, $"show {kept}:half-done.txt"));
		Assert.Equal("", await GitAsync(_repo, "status --porcelain"));
	}

	[Fact]
	public async Task Open_ReusesTheWorktreeKeptFromAnInterruptedRun()
	{
		await InitRepositoryAsync();
		var jobId = Guid.NewGuid();
		var worktree = await _worktrees.OpenAsync(_repo, _projectId, jobId);
		await File.WriteAllTextAsync(Path.Combine(worktree.Path, "app.txt"), "changed before the restart\n");

		var reopened = await _worktrees.OpenAsync(_repo, _projectId, jobId);

		Assert.True(reopened.Reused);
		Assert.Equal(worktree.Path, reopened.Path);
		Assert.Equal(worktree.Branch, reopened.Branch);
		Assert.Equal("changed before the restart\n", await File.ReadAllTextAsync(Path.Combine(reopened.Path, "app.txt")));
	}

	[Fact]
	public async Task Sweep_ReleasesOnlyWorktreesNoLiveJobOwns()
	{
		await InitRepositoryAsync();
		var liveJob = Guid.NewGuid();
		var finishedJob = Guid.NewGuid();
		var live = await _worktrees.OpenAsync(_repo, _projectId, liveJob);
		var finished = await _worktrees.OpenAsync(_repo, _projectId, finishedJob);
		await File.WriteAllTextAsync(Path.Combine(finished.Path, "orphan.txt"), "left behind\n");

		var kept = await _worktrees.SweepAsync(_repo, _projectId, [liveJob]);

		Assert.Equal([finished.Branch], kept);
		Assert.True(Directory.Exists(live.Path));
		Assert.False(Directory.Exists(finished.Path));
	}

	[Fact]
	public async Task Integrate_ThenPush_DeliversEachMemberToOrigin()
	{
		await InitRepositoryAsync();
		await using var dbContext = await CreateDbContextAsync();
		var processor = CreateProcessor();
		var first = await AddSwarmJobAsync(dbContext);
		var second = await AddSwarmJobAsync(dbContext, first.ProjectId, first.SwarmId);
		var firstTree = await _worktrees.OpenAsync(_repo, first.ProjectId, first.Id);
		var secondTree = await _worktrees.OpenAsync(_repo, second.ProjectId, second.Id);
		await CommitAsync(firstTree.Path, "api.txt", "endpoint\n", "Add the endpoint");
		await CommitAsync(secondTree.Path, "ui.txt", "page\n", "Add the page");

		var integrate = typeof(JobProcessingService).GetMethod("IntegrateSwarmWorkAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
		var firstResult = await (Task<SwarmIntegration>)integrate.Invoke(processor, [first, _repo, firstTree.Path, dbContext, CancellationToken.None])!;
		var secondResult = await (Task<SwarmIntegration>)integrate.Invoke(processor, [second, _repo, secondTree.Path, dbContext, CancellationToken.None])!;

		Assert.Equal(SwarmIntegrationOutcome.Integrated, firstResult.Outcome);
		Assert.Equal(SwarmIntegrationOutcome.Integrated, secondResult.Outcome);
		Assert.Null(second.ErrorMessage);
		Assert.Equal(second.GitCommitHash, await GitAsync(_repo, "rev-parse origin/main"));
		Assert.Equal("Add the page\nAdd the endpoint\nbase", await GitAsync(_repo, "log --format=%s origin/main"));
	}

	[Fact]
	public async Task Integrate_OnTheDevelopmentBranch_KeepsTheLastMembersUnpushedCommit()
	{
		await InitRepositoryAsync();
		await using var dbContext = await CreateDbContextAsync();
		var processor = CreateProcessor();
		var first = await AddSwarmJobAsync(dbContext, commitMode: AutoCommitMode.CommitOnly);
		var second = await AddSwarmJobAsync(dbContext, first.ProjectId, first.SwarmId, AutoCommitMode.CommitOnly);
		first.Project!.DevelopmentBranch = "main";
		await dbContext.SaveChangesAsync();
		var firstTree = await _worktrees.OpenAsync(_repo, first.ProjectId, first.Id);
		var secondTree = await _worktrees.OpenAsync(_repo, second.ProjectId, second.Id);
		await CommitAsync(firstTree.Path, "api.txt", "endpoint\n", "Add the endpoint");
		await CommitAsync(secondTree.Path, "ui.txt", "page\n", "Add the page");

		var integrate = typeof(JobProcessingService).GetMethod("IntegrateSwarmWorkAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
		await (Task<SwarmIntegration>)integrate.Invoke(processor, [first, _repo, firstTree.Path, dbContext, CancellationToken.None])!;
		var result = await (Task<SwarmIntegration>)integrate.Invoke(processor, [second, _repo, secondTree.Path, dbContext, CancellationToken.None])!;

		Assert.Equal(SwarmIntegrationOutcome.Integrated, result.Outcome);
		Assert.Equal("Add the page\nAdd the endpoint\nbase", await GitAsync(_repo, "log --format=%s main"));
		Assert.Equal("base", await GitAsync(_repo, "log --format=%s origin/main"));
		Assert.Equal("", await GitAsync(_repo, "branch --list \"vibeswarm/recovery/*\""));
	}

	[Fact]
	public async Task FinishingMembers_CommitAndLand_AndOverlappingWorkStaysOnItsBranch()
	{
		await InitRepositoryAsync();
		await using var dbContext = await CreateDbContextAsync();
		var processor = CreateProcessor();
		var first = await AddSwarmJobAsync(dbContext, commitMode: AutoCommitMode.CommitOnly);
		var second = await AddSwarmJobAsync(dbContext, first.ProjectId, first.SwarmId, AutoCommitMode.CommitOnly);
		var firstTree = await _worktrees.OpenAsync(_repo, first.ProjectId, first.Id);
		var secondTree = await _worktrees.OpenAsync(_repo, second.ProjectId, second.Id);
		first.GitCommitBefore = second.GitCommitBefore = await GitAsync(_repo, "rev-parse HEAD");
		await File.WriteAllTextAsync(Path.Combine(firstTree.Path, "app.txt"), "one\nmine\nthree\n");
		await File.WriteAllTextAsync(Path.Combine(secondTree.Path, "app.txt"), "one\ntheirs\nthree\n");

		await FinishAsync(processor, first, firstTree.Path, dbContext);
		await FinishAsync(processor, second, secondTree.Path, dbContext);

		Assert.Equal("one\nmine\nthree", await GitAsync(_repo, "show HEAD:app.txt"));
		Assert.Equal(first.GitCommitHash, await GitAsync(_repo, "rev-parse HEAD"));
		Assert.Null(first.ErrorMessage);
		Assert.False(Directory.Exists(firstTree.Path));
		Assert.False(Directory.Exists(secondTree.Path));

		Assert.Contains($"kept on the branch {secondTree.Branch}", second.ErrorMessage);
		Assert.Equal("one\ntheirs\nthree", await GitAsync(_repo, $"show {secondTree.Branch}:app.txt"));
		Assert.Equal(secondTree.Branch, await GitAsync(_repo, $"branch --list --format=%(refname:short) \"{SwarmWorktrees.BranchPrefix}*\""));
	}

	private static async Task FinishAsync(JobProcessingService processor, Job job, string worktree, VibeSwarmDbContext dbContext)
	{
		var contextType = typeof(JobProcessingService).GetNestedType("JobExecutionContext", BindingFlags.NonPublic)!;
		var context = Activator.CreateInstance(contextType)!;
		contextType.GetProperty("GitCommitBefore")!.SetValue(context, job.GitCommitBefore);
		contextType.GetProperty("ProjectId")!.SetValue(context, job.ProjectId);
		var finish = typeof(JobProcessingService).GetMethod("FinishSwarmRunAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
		await (Task)finish.Invoke(processor, [job, JobStatus.Completed, job.Project!.WorkingPath, worktree, context, dbContext, CancellationToken.None])!;
	}

	[Fact]
	public void MergeGoalPrompt_NamesTheBranchAndFiles_AndStaysWithinTheGoalLimit()
	{
		var prompt = JobProcessingService.BuildSwarmMergeGoalPrompt(
			"Backend",
			new string('x', 5000),
			"vibeswarm/swarm/abcd1234-20261022120000",
			["src/Api.cs", "src/Ui.razor"]);

		Assert.True(prompt.Length <= 2000);
		Assert.Contains("The Backend agent's changes overlap", prompt);
		Assert.Contains("'vibeswarm/swarm/abcd1234-20261022120000'", prompt);
		Assert.Contains("- src/Ui.razor", prompt);
	}

	[Fact]
	public void RolePrompt_TellsEachAgentItWorksBesideItsTeammates()
	{
		var context = PromptBuilder.BuildRoleSystemPromptContext(
			new Agent { Name = "Backend", Responsibilities = "APIs and data." },
			totalSwarmSize: 3,
			teammates: [new Agent { Name = "Frontend", Description = "Pages and styles." }, new Agent { Name = "Tester" }]);

		Assert.Contains("one of 3 specialized agents working on this goal at the same time", context);
		Assert.Contains("Your teammates: Frontend (Pages and styles.); Tester.", context);
		Assert.Contains("leave files and areas a teammate owns to them", context);
		Assert.DoesNotContain("taking turns", context);
	}

	private JobProcessingService CreateProcessor() => new(
		new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
		NullLogger<JobProcessingService>.Instance,
		new VersionControlService(_git, NullLogger<VersionControlService>.Instance),
		projectEnvironmentCredentialService: new NoOpProjectEnvironmentCredentialService(),
		swarmWorktrees: _worktrees)
	{
		FetchRetryDelays = []
	};

	private static async Task<VibeSwarmDbContext> CreateDbContextAsync()
	{
		var connection = new SqliteConnection("Data Source=:memory:");
		connection.Open();
		var dbContext = new VibeSwarmDbContext(new DbContextOptionsBuilder<VibeSwarmDbContext>().UseSqlite(connection).Options);
		await dbContext.Database.EnsureCreatedAsync();
		return dbContext;
	}

	private async Task<Job> AddSwarmJobAsync(
		VibeSwarmDbContext dbContext,
		Guid? projectId = null,
		Guid? swarmId = null,
		AutoCommitMode commitMode = AutoCommitMode.CommitAndPush)
	{
		if (projectId == null)
		{
			var project = new Project { Id = _projectId, Name = "Swarm Project", WorkingPath = _repo, EnableTeamSwarm = true };
			dbContext.Add(project);
		}

		var provider = new Provider { Id = Guid.NewGuid(), Name = $"Provider {Guid.NewGuid():N}", Type = ProviderType.Claude, IsEnabled = true };
		var job = new Job
		{
			Id = Guid.NewGuid(),
			ProjectId = projectId ?? _projectId,
			ProviderId = provider.Id,
			SwarmId = swarmId ?? Guid.NewGuid(),
			GoalPrompt = "Build it",
			Status = JobStatus.Processing,
			CommitModeOverride = commitMode
		};
		dbContext.AddRange(provider, job);
		await dbContext.SaveChangesAsync();
		return job;
	}

	private async Task InitRepositoryAsync()
	{
		await RunAsync(_root, "init -q --bare -b main origin.git");
		await RunAsync(_root, "clone -q origin.git work");
		await GitAsync(_repo, "config user.email tests@example.com");
		await GitAsync(_repo, "config user.name Tests");
		await GitAsync(_repo, "config commit.gpgsign false");
		await CommitAsync(_repo, "app.txt", "one\ntwo\nthree\n", "base");
		await GitAsync(_repo, "push -q -u origin main");
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

	private sealed class NoOpProjectEnvironmentCredentialService : IProjectEnvironmentCredentialService
	{
		public void PrepareForStorage(Project project, IReadOnlyCollection<ProjectEnvironment>? existingEnvironments = null) { }
		public void PopulateForEditing(Project? project) { }
		public void PopulateForExecution(Project? project) { }
		public Dictionary<string, string>? BuildJobEnvironmentVariables(Project? project) => null;
	}
}
