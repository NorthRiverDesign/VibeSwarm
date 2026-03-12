using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

public sealed class QueueAndIdeaServiceTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;

	public QueueAndIdeaServiceTests()
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
	public async Task CreateAsync_AllowsQueueingAnotherJobWhileOneIsRunning()
	{
		await using var dbContext = CreateDbContext();
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Queue Project",
			WorkingPath = "/tmp/project"
		};
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Copilot",
			Type = ProviderType.Copilot,
			IsEnabled = true,
			IsDefault = true
		};

		dbContext.Projects.Add(project);
		dbContext.Providers.Add(provider);
		dbContext.Jobs.Add(new Job
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			ProviderId = provider.Id,
			GoalPrompt = "Existing running job",
			Status = JobStatus.Processing,
			StartedAt = DateTime.UtcNow.AddMinutes(-2)
		});
		await dbContext.SaveChangesAsync();

		var serviceProvider = new ServiceCollection().BuildServiceProvider();
		var jobService = new JobService(dbContext, serviceProvider);

		var createdJob = await jobService.CreateAsync(new Job
		{
			ProjectId = project.Id,
			ProviderId = provider.Id,
			GoalPrompt = "Queue the next job"
		});

		Assert.Equal(JobStatus.New, createdJob.Status);
		Assert.Equal(2, await dbContext.Jobs.CountAsync(j => j.ProjectId == project.Id));
		Assert.Equal(1, await dbContext.Jobs.CountAsync(j => j.ProjectId == project.Id && j.Status == JobStatus.New));
	}

	[Fact]
	public async Task UpdateAsync_UpdatesExistingIdeaWithoutCreatingDuplicate()
	{
		await using var dbContext = CreateDbContext();
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Ideas Project",
			WorkingPath = "/tmp/ideas"
		};
		var idea = new Idea
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			Description = "Original idea",
			SortOrder = 0,
			CreatedAt = DateTime.UtcNow
		};

		dbContext.Projects.Add(project);
		dbContext.Ideas.Add(idea);
		await dbContext.SaveChangesAsync();

		var ideaService = new IdeaService(
			dbContext,
			null!,
			null!,
			null!,
			NullLogger<IdeaService>.Instance);

		var updatedIdea = await ideaService.UpdateAsync(new Idea
		{
			Id = idea.Id,
			Description = "Updated idea",
			SortOrder = 0
		});

		Assert.Equal("Updated idea", updatedIdea.Description);
		Assert.Equal(1, await dbContext.Ideas.CountAsync(i => i.ProjectId == project.Id));
		Assert.Equal("Updated idea", await dbContext.Ideas
			.Where(i => i.Id == idea.Id)
			.Select(i => i.Description)
			.SingleAsync());
	}

	[Fact]
	public async Task GetByProjectIdAsync_IncludesLinkedQueuedJobState()
	{
		await using var dbContext = CreateDbContext();
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Queued Ideas Project",
			WorkingPath = "/tmp/queued-ideas"
		};
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Claude",
			Type = ProviderType.Claude,
			IsEnabled = true
		};
		var queuedJob = new Job
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			ProviderId = provider.Id,
			GoalPrompt = "Queued work item",
			Status = JobStatus.New
		};
		var queuedIdea = new Idea
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			Description = "Queued idea",
			JobId = queuedJob.Id,
			IsProcessing = true,
			SortOrder = 0,
			CreatedAt = DateTime.UtcNow
		};

		dbContext.Projects.Add(project);
		dbContext.Providers.Add(provider);
		dbContext.Jobs.Add(queuedJob);
		dbContext.Ideas.Add(queuedIdea);
		await dbContext.SaveChangesAsync();

		var ideaService = new IdeaService(
			dbContext,
			null!,
			null!,
			null!,
			NullLogger<IdeaService>.Instance);

		var ideas = (await ideaService.GetByProjectIdAsync(project.Id)).ToList();

		var loadedIdea = Assert.Single(ideas);
		Assert.NotNull(loadedIdea.Job);
		Assert.Equal(JobStatus.New, loadedIdea.Job!.Status);
	}

	private VibeSwarmDbContext CreateDbContext()
	{
		return new VibeSwarmDbContext(_dbOptions);
	}

	public void Dispose()
	{
		_connection.Dispose();
	}
}
