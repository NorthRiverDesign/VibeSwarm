using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class ProjectQueueHoldTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;

	public ProjectQueueHoldTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();

		_dbOptions = new DbContextOptionsBuilder<VibeSwarmDbContext>()
			.UseSqlite(_connection)
			.Options;

		using var dbContext = CreateDbContext();
		dbContext.Database.EnsureCreated();
	}

	[Fact]
	public void Hold_LapsesAfterHoldDuration_UnlessRenewed()
	{
		var clock = new ManualTimeProvider();
		var holds = new ProjectQueueHolds(clock);
		var projectId = Guid.NewGuid();

		holds.Hold(projectId);
		clock.Advance(ProjectQueueHolds.HoldDuration - TimeSpan.FromMinutes(1));
		Assert.True(holds.IsHeld(projectId));

		holds.Hold(projectId);
		clock.Advance(ProjectQueueHolds.HoldDuration - TimeSpan.FromMinutes(1));
		Assert.True(holds.IsHeld(projectId));

		clock.Advance(TimeSpan.FromMinutes(2));
		Assert.False(holds.IsHeld(projectId));
		Assert.Null(holds.GetHeldUntil(projectId));
	}

	[Fact]
	public void Release_EndsHoldImmediately()
	{
		var holds = new ProjectQueueHolds();
		var projectId = Guid.NewGuid();

		holds.Hold(projectId);

		Assert.True(holds.Release(projectId));
		Assert.False(holds.IsHeld(projectId));
		Assert.False(holds.Release(projectId));
	}

	[Fact]
	public async Task GetPendingJobsAsync_SkipsHeldProject_AndLetsItsProviderServeOthers()
	{
		await using var dbContext = CreateDbContext();
		var provider = AddProvider(dbContext);
		var heldProject = AddProject(dbContext, "Held");
		var otherProject = AddProject(dbContext, "Other");
		AddJob(dbContext, heldProject, provider, JobStatus.New, "Held job", createdMinutesAgo: 5);
		var otherJob = AddJob(dbContext, otherProject, provider, JobStatus.New, "Other job", createdMinutesAgo: 1);
		await dbContext.SaveChangesAsync();

		var holds = new ProjectQueueHolds();
		holds.Hold(heldProject.Id);
		var jobService = new JobService(dbContext, new ServiceCollection().AddSingleton(holds).BuildServiceProvider());

		var pendingJobs = (await jobService.GetPendingJobsAsync()).ToList();

		Assert.Equal([otherJob.Id], pendingJobs.Select(job => job.Id));

		holds.Release(heldProject.Id);
		pendingJobs = (await jobService.GetPendingJobsAsync()).ToList();

		Assert.Equal(heldProject.Id, Assert.Single(pendingJobs).ProjectId);
	}

	[Fact]
	public async Task ProjectState_CountsTheProjectsJobs_AndReflectsHold()
	{
		await using var dbContext = CreateDbContext();
		var provider = AddProvider(dbContext);
		var project = AddProject(dbContext, "Counted");
		var otherProject = AddProject(dbContext, "Not counted");
		AddJob(dbContext, project, provider, JobStatus.Processing, "Running");
		AddJob(dbContext, project, provider, JobStatus.New, "Queued one");
		AddJob(dbContext, project, provider, JobStatus.New, "Queued two");
		AddJob(dbContext, project, provider, JobStatus.Completed, "Done");
		AddJob(dbContext, otherProject, provider, JobStatus.New, "Elsewhere");
		await dbContext.SaveChangesAsync();

		var holds = new ProjectQueueHolds();
		var jobService = new JobService(dbContext, new ServiceCollection().BuildServiceProvider());
		var queueControl = new JobQueueControlService(
			dbContext,
			jobService,
			NullLogger<JobQueueControlService>.Instance,
			projectHolds: holds);

		var state = await queueControl.GetProjectStateAsync(project.Id);
		Assert.Equal(1, state.RunningJobs);
		Assert.Equal(2, state.QueuedJobs);
		Assert.False(state.IsProjectPaused);
		Assert.True(state.CanStartJobs);

		state = await queueControl.PauseProjectAsync(project.Id);
		Assert.True(state.IsProjectPaused);
		Assert.NotNull(state.ProjectPausedUntil);
		Assert.False(state.CanStartJobs);
		Assert.True(holds.IsHeld(project.Id));
		Assert.False(holds.IsHeld(otherProject.Id));

		state = await queueControl.ResumeProjectAsync(project.Id);
		Assert.False(state.IsProjectPaused);
		Assert.False(holds.IsHeld(project.Id));
	}

	[Fact]
	public async Task UpdateJobPromptAsync_RefusesAJobTheWorkerHasClaimed()
	{
		await using var dbContext = CreateDbContext();
		var provider = AddProvider(dbContext);
		var project = AddProject(dbContext, "Claimed");
		var job = AddJob(dbContext, project, provider, JobStatus.New, "Original prompt");
		await dbContext.SaveChangesAsync();

		// The worker claims with an atomic update, behind the tracked copy's back.
		await dbContext.Jobs
			.Where(j => j.Id == job.Id)
			.ExecuteUpdateAsync(setters => setters.SetProperty(j => j.WorkerInstanceId, "worker-1"));

		var jobService = new JobService(dbContext, new ServiceCollection().BuildServiceProvider());

		Assert.False(await jobService.UpdateJobPromptAsync(job.Id, "Edited too late"));

		await using var verifyContext = CreateDbContext();
		Assert.Equal("Original prompt", await verifyContext.Jobs.Where(j => j.Id == job.Id).Select(j => j.GoalPrompt).SingleAsync());
	}

	[Fact]
	public async Task UpdateJobPromptAsync_RefusesAStartedJob_AndSavesAQueuedOne()
	{
		await using var dbContext = CreateDbContext();
		var provider = AddProvider(dbContext);
		var project = AddProject(dbContext, "Editable");
		var startedJob = AddJob(dbContext, project, provider, JobStatus.Processing, "Running prompt");
		var queuedJob = AddJob(dbContext, project, provider, JobStatus.New, "Queued prompt");
		await dbContext.SaveChangesAsync();

		var jobService = new JobService(dbContext, new ServiceCollection().BuildServiceProvider());

		Assert.False(await jobService.UpdateJobPromptAsync(startedJob.Id, "Edited while running"));
		Assert.True(await jobService.UpdateJobPromptAsync(queuedJob.Id, "  Edited while queued  "));

		await using var verifyContext = CreateDbContext();
		Assert.Equal("Running prompt", await verifyContext.Jobs.Where(j => j.Id == startedJob.Id).Select(j => j.GoalPrompt).SingleAsync());
		Assert.Equal("Edited while queued", await verifyContext.Jobs.Where(j => j.Id == queuedJob.Id).Select(j => j.GoalPrompt).SingleAsync());
	}

	private VibeSwarmDbContext CreateDbContext() => new(_dbOptions);

	private static Provider AddProvider(VibeSwarmDbContext dbContext)
	{
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Claude",
			Type = ProviderType.Claude,
			IsEnabled = true,
			IsDefault = true
		};
		dbContext.Providers.Add(provider);
		return provider;
	}

	private static Project AddProject(VibeSwarmDbContext dbContext, string name)
	{
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = name,
			WorkingPath = $"/tmp/{name.ToLowerInvariant().Replace(' ', '-')}"
		};
		dbContext.Projects.Add(project);
		return project;
	}

	private static Job AddJob(
		VibeSwarmDbContext dbContext,
		Project project,
		Provider provider,
		JobStatus status,
		string prompt,
		int createdMinutesAgo = 1)
	{
		var job = new Job
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			ProviderId = provider.Id,
			GoalPrompt = prompt,
			Title = prompt,
			Status = status,
			CreatedAt = DateTime.UtcNow.AddMinutes(-createdMinutesAgo)
		};
		dbContext.Jobs.Add(job);
		return job;
	}

	public void Dispose()
	{
		_connection.Dispose();
	}

	private sealed class ManualTimeProvider : TimeProvider
	{
		private DateTimeOffset _now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

		public override DateTimeOffset GetUtcNow() => _now;

		public void Advance(TimeSpan by) => _now += by;
	}
}
