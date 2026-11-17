using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

/// <summary>Run next, and why a queued job has not started yet.</summary>
public sealed class JobQueueOrderTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;
	private readonly Project _projectA = new() { Id = Guid.NewGuid(), Name = "Alpha", WorkingPath = "/tmp/alpha" };
	private readonly Project _projectB = new() { Id = Guid.NewGuid(), Name = "Beta", WorkingPath = "/tmp/beta" };
	private readonly Provider _claude = new() { Id = Guid.NewGuid(), Name = "Claude", Type = ProviderType.Claude, IsEnabled = true, IsDefault = true };
	private readonly Provider _copilot = new() { Id = Guid.NewGuid(), Name = "Copilot", Type = ProviderType.Copilot, IsEnabled = true };

	public JobQueueOrderTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		_dbOptions = new DbContextOptionsBuilder<VibeSwarmDbContext>().UseSqlite(_connection).Options;

		using var dbContext = CreateDbContext();
		dbContext.Database.EnsureCreated();
		dbContext.Projects.AddRange(_projectA, _projectB);
		dbContext.Providers.AddRange(_claude, _copilot);
		dbContext.SaveChanges();
	}

	public void Dispose() => _connection.Dispose();

	[Fact]
	public async Task RunNextAsync_StartsANewerJobBeforeAnotherProjectsOlderJobOnTheSameProvider()
	{
		await using var dbContext = CreateDbContext();
		var older = AddJob(dbContext, _projectB, _claude, "Older job in Beta", createdMinutesAgo: 30, priority: 3);
		var newer = AddJob(dbContext, _projectA, _claude, "Newer job in Alpha", createdMinutesAgo: 5);
		await dbContext.SaveChangesAsync();
		var jobService = CreateJobService(dbContext);

		Assert.Equal(older.Id, Assert.Single(await jobService.GetPendingJobsAsync()).Id);

		Assert.True(await jobService.RunNextAsync(newer.Id));

		Assert.Equal(newer.Id, Assert.Single(await jobService.GetPendingJobsAsync()).Id);
		Assert.Equal(4, await dbContext.Jobs.Where(j => j.Id == newer.Id).Select(j => j.Priority).SingleAsync());
	}

	[Fact]
	public async Task RunNextAsync_LeavesAJobAlreadyFirstInLineAlone()
	{
		await using var dbContext = CreateDbContext();
		var first = AddJob(dbContext, _projectA, _claude, "First", createdMinutesAgo: 10, priority: 7);
		AddJob(dbContext, _projectB, _claude, "Second", createdMinutesAgo: 20, priority: 2);
		await dbContext.SaveChangesAsync();

		Assert.True(await CreateJobService(dbContext).RunNextAsync(first.Id));

		Assert.Equal(7, await dbContext.Jobs.Where(j => j.Id == first.Id).Select(j => j.Priority).SingleAsync());
	}

	[Theory]
	[InlineData(JobStatus.Processing, false)]
	[InlineData(JobStatus.Completed, false)]
	[InlineData(JobStatus.New, true)]
	public async Task RunNextAsync_RefusesAJobThatIsNoLongerQueued(JobStatus status, bool cancellationRequested)
	{
		await using var dbContext = CreateDbContext();
		var job = AddJob(dbContext, _projectA, _claude, "Not queued", createdMinutesAgo: 1, status: status);
		job.CancellationRequested = cancellationRequested;
		AddJob(dbContext, _projectB, _claude, "Queued", createdMinutesAgo: 2, priority: 5);
		await dbContext.SaveChangesAsync();

		Assert.False(await CreateJobService(dbContext).RunNextAsync(job.Id));
		Assert.Equal(0, await dbContext.Jobs.Where(j => j.Id == job.Id).Select(j => j.Priority).SingleAsync());
	}

	[Fact]
	public async Task GetQueueStatusAsync_NamesTheOtherProjectsJobHoldingTheProvider()
	{
		await using var dbContext = CreateDbContext();
		var running = AddJob(dbContext, _projectB, _claude, "Refactor the billing module", createdMinutesAgo: 40, status: JobStatus.Processing);
		var queued = AddJob(dbContext, _projectA, _claude, "Add dark mode", createdMinutesAgo: 5);
		await dbContext.SaveChangesAsync();

		var status = await CreateJobService(dbContext).GetQueueStatusAsync(queued.Id);

		Assert.NotNull(status);
		Assert.Equal(JobQueueWaitReason.ProviderBusy, status.Reason);
		Assert.Equal("Claude", status.ProviderName);
		Assert.Equal(running.Id, status.BlockingJobId);
		Assert.Equal("Refactor the billing module", status.BlockingJobTitle);
		Assert.Equal("Beta", status.BlockingProjectName);
		Assert.Equal(JobStatus.Processing, status.BlockingJobStatus);
		Assert.Equal(0, status.JobsAhead);
	}

	[Fact]
	public async Task GetQueueStatusAsync_PrefersTheProjectsOwnRunningJob()
	{
		await using var dbContext = CreateDbContext();
		AddJob(dbContext, _projectB, _claude, "Other project", createdMinutesAgo: 40, status: JobStatus.Processing);
		var sameProject = AddJob(dbContext, _projectA, _copilot, "Same project", createdMinutesAgo: 30, status: JobStatus.Paused);
		var queued = AddJob(dbContext, _projectA, _claude, "Queued", createdMinutesAgo: 5);
		await dbContext.SaveChangesAsync();

		var status = await CreateJobService(dbContext).GetQueueStatusAsync(queued.Id);

		Assert.Equal(JobQueueWaitReason.ProjectBusy, status!.Reason);
		Assert.Equal(sameProject.Id, status.BlockingJobId);
		Assert.Equal(JobStatus.Paused, status.BlockingJobStatus);
	}

	[Fact]
	public async Task GetQueueStatusAsync_CountsOnlyQueuedJobsThatCompeteAndComeFirst()
	{
		await using var dbContext = CreateDbContext();
		var queued = AddJob(dbContext, _projectA, _claude, "Mine", createdMinutesAgo: 10);
		AddJob(dbContext, _projectB, _claude, "Older, same provider", createdMinutesAgo: 20);
		AddJob(dbContext, _projectA, _copilot, "Higher priority, same project", createdMinutesAgo: 1, priority: 2);
		AddJob(dbContext, _projectB, _copilot, "Other project and provider", createdMinutesAgo: 30, priority: 9);
		AddJob(dbContext, _projectB, _claude, "Newer, same provider", createdMinutesAgo: 2);
		var held = AddJob(dbContext, _projectB, _claude, "Held for a usage limit", createdMinutesAgo: 40);
		held.NotBeforeUtc = DateTime.UtcNow.AddHours(1);
		await dbContext.SaveChangesAsync();
		var jobService = CreateJobService(dbContext);

		var status = await jobService.GetQueueStatusAsync(queued.Id);
		Assert.Equal(JobQueueWaitReason.StartingSoon, status!.Reason);
		Assert.Equal(2, status.JobsAhead);

		await jobService.RunNextAsync(queued.Id);

		Assert.Equal(0, (await jobService.GetQueueStatusAsync(queued.Id))!.JobsAhead);
	}

	[Fact]
	public async Task GetQueueStatusAsync_ReportsATimedHold()
	{
		await using var dbContext = CreateDbContext();
		var queued = AddJob(dbContext, _projectA, _claude, "Waiting on limits", createdMinutesAgo: 5);
		var resetAt = DateTime.UtcNow.AddMinutes(45);
		queued.NotBeforeUtc = resetAt;
		await dbContext.SaveChangesAsync();

		var status = await CreateJobService(dbContext).GetQueueStatusAsync(queued.Id);

		Assert.Equal(JobQueueWaitReason.HeldUntil, status!.Reason);
		Assert.Equal(resetAt, status.WaitUntilUtc!.Value, TimeSpan.FromSeconds(1));
	}

	[Fact]
	public async Task GetQueueStatusAsync_ReportsAStoppedQueueFirst()
	{
		await using var dbContext = CreateDbContext();
		AddJob(dbContext, _projectB, _claude, "Running", createdMinutesAgo: 40, status: JobStatus.Processing);
		var queued = AddJob(dbContext, _projectA, _claude, "Queued", createdMinutesAgo: 5);
		dbContext.AppSettings.Add(new AppSettings { Id = Guid.NewGuid(), TimeZoneId = "UTC", JobQueuePaused = true });
		await dbContext.SaveChangesAsync();

		var status = await CreateJobService(dbContext).GetQueueStatusAsync(queued.Id);

		Assert.Equal(JobQueueWaitReason.QueuePaused, status!.Reason);
	}

	[Fact]
	public async Task GetQueueStatusAsync_ReportsAnUnfinishedDependency()
	{
		await using var dbContext = CreateDbContext();
		var dependency = AddJob(dbContext, _projectB, _copilot, "Must finish first", createdMinutesAgo: 40, status: JobStatus.Failed);
		var queued = AddJob(dbContext, _projectA, _claude, "Depends on it", createdMinutesAgo: 5);
		queued.DependsOnJobId = dependency.Id;
		await dbContext.SaveChangesAsync();

		var status = await CreateJobService(dbContext).GetQueueStatusAsync(queued.Id);

		Assert.Equal(JobQueueWaitReason.WaitingOnDependency, status!.Reason);
		Assert.Equal(dependency.Id, status.BlockingJobId);
		Assert.Equal(JobStatus.Failed, status.BlockingJobStatus);
	}

	[Fact]
	public async Task GetQueueStatusAsync_ReportsAnEditHoldOnTheProject()
	{
		await using var dbContext = CreateDbContext();
		var queued = AddJob(dbContext, _projectA, _claude, "Queued", createdMinutesAgo: 5);
		await dbContext.SaveChangesAsync();
		var holds = new ProjectQueueHolds();
		holds.Hold(_projectA.Id);
		var services = new ServiceCollection().AddSingleton(holds).BuildServiceProvider();

		var status = await new JobService(dbContext, services).GetQueueStatusAsync(queued.Id);

		Assert.Equal(JobQueueWaitReason.ProjectEditHold, status!.Reason);
		Assert.NotNull(status.WaitUntilUtc);
	}

	[Fact]
	public async Task GetQueueStatusAsync_ReturnsNullOnceTheJobHasStarted()
	{
		await using var dbContext = CreateDbContext();
		var started = AddJob(dbContext, _projectA, _claude, "Running", createdMinutesAgo: 5, status: JobStatus.Processing);
		await dbContext.SaveChangesAsync();

		Assert.Null(await CreateJobService(dbContext).GetQueueStatusAsync(started.Id));
	}

	private VibeSwarmDbContext CreateDbContext() => new(_dbOptions);

	private static JobService CreateJobService(VibeSwarmDbContext dbContext)
		=> new(dbContext, new ServiceCollection().BuildServiceProvider());

	private static Job AddJob(VibeSwarmDbContext dbContext, Project project, Provider provider, string title,
		int createdMinutesAgo, int priority = 0, JobStatus status = JobStatus.New)
	{
		var job = new Job
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			ProviderId = provider.Id,
			Title = title,
			GoalPrompt = title,
			Status = status,
			Priority = priority,
			CreatedAt = DateTime.UtcNow.AddMinutes(-createdMinutesAgo)
		};
		dbContext.Jobs.Add(job);
		return job;
	}
}
