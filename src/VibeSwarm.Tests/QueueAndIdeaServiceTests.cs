using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Inference;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Validation;
using VibeSwarm.Shared.VersionControl;
using VibeSwarm.Shared.VersionControl.Models;
using VibeSwarm.Web.Services;

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
	public async Task GetPendingJobsAsync_ReturnsOnlyOneQueuedJobPerProject_WhenProjectsUseDifferentProviders()
	{
		await using var dbContext = CreateDbContext();
		var firstProject = new Project
		{
			Id = Guid.NewGuid(),
			Name = "First Queue Project",
			WorkingPath = "/tmp/first-queue-project"
		};
		var secondProject = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Second Queue Project",
			WorkingPath = "/tmp/second-queue-project"
		};
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Copilot",
			Type = ProviderType.Copilot,
			IsEnabled = true,
			IsDefault = true
		};
		var secondProvider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Claude",
			Type = ProviderType.Claude,
			IsEnabled = true
		};

		dbContext.Projects.AddRange(firstProject, secondProject);
		dbContext.Providers.AddRange(provider, secondProvider);
		dbContext.Jobs.AddRange(
			new Job
			{
				Id = Guid.NewGuid(),
				ProjectId = firstProject.Id,
				ProviderId = provider.Id,
				GoalPrompt = "First project, first queued job",
				Title = "First project, first queued job",
				Status = JobStatus.New,
				Priority = 5,
				CreatedAt = DateTime.UtcNow.AddMinutes(-3)
			},
			new Job
			{
				Id = Guid.NewGuid(),
				ProjectId = firstProject.Id,
				ProviderId = provider.Id,
				GoalPrompt = "First project, second queued job",
				Title = "First project, second queued job",
				Status = JobStatus.New,
				Priority = 1,
				CreatedAt = DateTime.UtcNow.AddMinutes(-2)
			},
			new Job
			{
				Id = Guid.NewGuid(),
				ProjectId = secondProject.Id,
				ProviderId = secondProvider.Id,
				GoalPrompt = "Second project queued job",
				Title = "Second project queued job",
				Status = JobStatus.New,
				Priority = 3,
				CreatedAt = DateTime.UtcNow.AddMinutes(-1)
			});
		await dbContext.SaveChangesAsync();

		var serviceProvider = new ServiceCollection().BuildServiceProvider();
		var jobService = new JobService(dbContext, serviceProvider);

		var pendingJobs = (await jobService.GetPendingJobsAsync()).ToList();

		Assert.Equal(2, pendingJobs.Count);
		Assert.Equal(2, pendingJobs.Select(job => job.ProjectId).Distinct().Count());
		Assert.Contains(pendingJobs, job => job.ProjectId == firstProject.Id && job.Title == "First project, first queued job");
		Assert.DoesNotContain(pendingJobs, job => job.ProjectId == firstProject.Id && job.Title == "First project, second queued job");
	}

	[Fact]
	public async Task GetPendingJobsAsync_SkipsJobsWithFutureNotBeforeUtc_AndIncompleteDependencies()
	{
		await using var dbContext = CreateDbContext();
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Pending Filter Project",
			WorkingPath = "/tmp/pending-filter-project"
		};
		var dependencyProject = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Dependency Project",
			WorkingPath = "/tmp/dependency-project"
		};
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Copilot",
			Type = ProviderType.Copilot,
			IsEnabled = true,
			IsDefault = true
		};
		var dependencyProvider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Claude",
			Type = ProviderType.Claude,
			IsEnabled = true
		};
		var completedDependencyId = Guid.NewGuid();
		var pendingDependencyId = Guid.NewGuid();
		var readyJobId = Guid.NewGuid();
		var backedOffJobId = Guid.NewGuid();
		var blockedJobId = Guid.NewGuid();

		dbContext.Projects.AddRange(project, dependencyProject);
		dbContext.Providers.AddRange(provider, dependencyProvider);
		dbContext.Jobs.AddRange(
			new Job
			{
				Id = completedDependencyId,
				ProjectId = project.Id,
				ProviderId = provider.Id,
				GoalPrompt = "Completed dependency",
				Title = "Completed dependency",
				Status = JobStatus.Completed,
				CreatedAt = DateTime.UtcNow.AddMinutes(-10),
				CompletedAt = DateTime.UtcNow.AddMinutes(-9)
			},
			new Job
			{
				Id = pendingDependencyId,
				ProjectId = dependencyProject.Id,
				ProviderId = dependencyProvider.Id,
				GoalPrompt = "Pending dependency",
				Title = "Pending dependency",
				Status = JobStatus.New,
				CreatedAt = DateTime.UtcNow.AddMinutes(-8)
			},
			new Job
			{
				Id = readyJobId,
				ProjectId = project.Id,
				ProviderId = provider.Id,
				GoalPrompt = "Ready queued job",
				Title = "Ready queued job",
				Status = JobStatus.New,
				CreatedAt = DateTime.UtcNow.AddMinutes(-7),
				DependsOnJobId = completedDependencyId
			},
			new Job
			{
				Id = backedOffJobId,
				ProjectId = project.Id,
				ProviderId = provider.Id,
				GoalPrompt = "Backed off queued job",
				Title = "Backed off queued job",
				Status = JobStatus.New,
				CreatedAt = DateTime.UtcNow.AddMinutes(-6),
				NotBeforeUtc = DateTime.UtcNow.AddMinutes(30)
			},
			new Job
			{
				Id = blockedJobId,
				ProjectId = project.Id,
				ProviderId = provider.Id,
				GoalPrompt = "Blocked queued job",
				Title = "Blocked queued job",
				Status = JobStatus.New,
				CreatedAt = DateTime.UtcNow.AddMinutes(-5),
				DependsOnJobId = pendingDependencyId
			});
		await dbContext.SaveChangesAsync();

		var serviceProvider = new ServiceCollection().BuildServiceProvider();
		var jobService = new JobService(dbContext, serviceProvider);

		var pendingJobs = (await jobService.GetPendingJobsAsync()).ToList();

		Assert.Contains(pendingJobs, job => job.Id == readyJobId);
		Assert.DoesNotContain(pendingJobs, job => job.Id == backedOffJobId);
		Assert.DoesNotContain(pendingJobs, job => job.Id == blockedJobId);
	}

	[Fact]
	public async Task GetPendingJobsAsync_ReturnsAtMostOneQueuedJobPerProvider()
	{
		await using var dbContext = CreateDbContext();
		var firstProject = new Project
		{
			Id = Guid.NewGuid(),
			Name = "First Provider Queue Project",
			WorkingPath = "/tmp/first-provider-queue-project"
		};
		var secondProject = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Second Provider Queue Project",
			WorkingPath = "/tmp/second-provider-queue-project"
		};
		var thirdProject = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Third Provider Queue Project",
			WorkingPath = "/tmp/third-provider-queue-project"
		};
		var copilotProvider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Copilot",
			Type = ProviderType.Copilot,
			IsEnabled = true,
			IsDefault = true
		};
		var claudeProvider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Claude",
			Type = ProviderType.Claude,
			IsEnabled = true
		};

		dbContext.Projects.AddRange(firstProject, secondProject, thirdProject);
		dbContext.Providers.AddRange(copilotProvider, claudeProvider);
		dbContext.Jobs.AddRange(
			new Job
			{
				Id = Guid.NewGuid(),
				ProjectId = firstProject.Id,
				ProviderId = copilotProvider.Id,
				GoalPrompt = "First Copilot queued job",
				Title = "First Copilot queued job",
				Status = JobStatus.New,
				Priority = 5,
				CreatedAt = DateTime.UtcNow.AddMinutes(-3)
			},
			new Job
			{
				Id = Guid.NewGuid(),
				ProjectId = secondProject.Id,
				ProviderId = copilotProvider.Id,
				GoalPrompt = "Second Copilot queued job",
				Title = "Second Copilot queued job",
				Status = JobStatus.New,
				Priority = 1,
				CreatedAt = DateTime.UtcNow.AddMinutes(-2)
			},
			new Job
			{
				Id = Guid.NewGuid(),
				ProjectId = thirdProject.Id,
				ProviderId = claudeProvider.Id,
				GoalPrompt = "Claude queued job",
				Title = "Claude queued job",
				Status = JobStatus.New,
				Priority = 3,
				CreatedAt = DateTime.UtcNow.AddMinutes(-1)
			});
		await dbContext.SaveChangesAsync();

		var serviceProvider = new ServiceCollection().BuildServiceProvider();
		var jobService = new JobService(dbContext, serviceProvider);

		var pendingJobs = (await jobService.GetPendingJobsAsync()).ToList();

		Assert.Equal(2, pendingJobs.Count);
		Assert.Contains(pendingJobs, job => job.ProviderId == claudeProvider.Id);
		Assert.Contains(pendingJobs, job => job.ProviderId == copilotProvider.Id && job.Title == "First Copilot queued job");
		Assert.DoesNotContain(pendingJobs, job => job.ProviderId == copilotProvider.Id && job.Title == "Second Copilot queued job");
	}

	[Fact]
	public async Task GetPendingJobsAsync_SkipsQueuedJobsForProvidersThatAreAlreadyRunning()
	{
		await using var dbContext = CreateDbContext();
		var runningProject = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Running Provider Project",
			WorkingPath = "/tmp/running-provider-project"
		};
		var blockedProject = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Blocked Provider Project",
			WorkingPath = "/tmp/blocked-provider-project"
		};
		var readyProject = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Ready Provider Project",
			WorkingPath = "/tmp/ready-provider-project"
		};
		var copilotProvider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Copilot",
			Type = ProviderType.Copilot,
			IsEnabled = true,
			IsDefault = true
		};
		var claudeProvider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Claude",
			Type = ProviderType.Claude,
			IsEnabled = true
		};

		dbContext.Projects.AddRange(runningProject, blockedProject, readyProject);
		dbContext.Providers.AddRange(copilotProvider, claudeProvider);
		dbContext.Jobs.AddRange(
			new Job
			{
				Id = Guid.NewGuid(),
				ProjectId = runningProject.Id,
				ProviderId = copilotProvider.Id,
				GoalPrompt = "Running Copilot job",
				Title = "Running Copilot job",
				Status = JobStatus.Processing,
				StartedAt = DateTime.UtcNow.AddMinutes(-5)
			},
			new Job
			{
				Id = Guid.NewGuid(),
				ProjectId = blockedProject.Id,
				ProviderId = copilotProvider.Id,
				GoalPrompt = "Queued Copilot job",
				Title = "Queued Copilot job",
				Status = JobStatus.New,
				CreatedAt = DateTime.UtcNow.AddMinutes(-2)
			},
			new Job
			{
				Id = Guid.NewGuid(),
				ProjectId = readyProject.Id,
				ProviderId = claudeProvider.Id,
				GoalPrompt = "Queued Claude job",
				Title = "Queued Claude job",
				Status = JobStatus.New,
				CreatedAt = DateTime.UtcNow.AddMinutes(-1)
			});
		await dbContext.SaveChangesAsync();

		var serviceProvider = new ServiceCollection().BuildServiceProvider();
		var jobService = new JobService(dbContext, serviceProvider);

		var pendingJobs = (await jobService.GetPendingJobsAsync()).ToList();

		var readyJob = Assert.Single(pendingJobs);
		Assert.Equal(claudeProvider.Id, readyJob.ProviderId);
		Assert.Equal("Queued Claude job", readyJob.Title);
	}

	[Fact]
	public async Task PrioritizeSelectedByProjectIdAsync_OnlyUpdatesQueuedSelectedJobs()
	{
		await using var dbContext = CreateDbContext();
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Priority Project",
			WorkingPath = "/tmp/priority-project"
		};
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Copilot",
			Type = ProviderType.Copilot,
			IsEnabled = true,
			IsDefault = true
		};
		var selectedQueuedJobId = Guid.NewGuid();
		var otherQueuedJobId = Guid.NewGuid();
		var failedJobId = Guid.NewGuid();

		dbContext.Projects.Add(project);
		dbContext.Providers.Add(provider);
		dbContext.Jobs.AddRange(
			new Job
			{
				Id = selectedQueuedJobId,
				ProjectId = project.Id,
				ProviderId = provider.Id,
				GoalPrompt = "Selected queued job",
				Status = JobStatus.New,
				Priority = 1
			},
			new Job
			{
				Id = otherQueuedJobId,
				ProjectId = project.Id,
				ProviderId = provider.Id,
				GoalPrompt = "Other queued job",
				Status = JobStatus.New,
				Priority = 5
			},
			new Job
			{
				Id = failedJobId,
				ProjectId = project.Id,
				ProviderId = provider.Id,
				GoalPrompt = "Failed job",
				Status = JobStatus.Failed,
				Priority = 3
			});
		await dbContext.SaveChangesAsync();

		var serviceProvider = new ServiceCollection().BuildServiceProvider();
		var jobService = new JobService(dbContext, serviceProvider);

		var updatedCount = await jobService.PrioritizeSelectedByProjectIdAsync(project.Id, [selectedQueuedJobId, failedJobId]);

		Assert.Equal(1, updatedCount);
		Assert.Equal(6, await dbContext.Jobs.Where(j => j.Id == selectedQueuedJobId).Select(j => j.Priority).SingleAsync());
		Assert.Equal(5, await dbContext.Jobs.Where(j => j.Id == otherQueuedJobId).Select(j => j.Priority).SingleAsync());
		Assert.Equal(3, await dbContext.Jobs.Where(j => j.Id == failedJobId).Select(j => j.Priority).SingleAsync());
	}

	[Fact]
	public async Task UpdateJobPromptAsync_ClearsPersistedPlanningOutput()
	{
		await using var dbContext = CreateDbContext();
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Planning Project",
			WorkingPath = "/tmp/planning-project",
			PlanningEnabled = true,
			PlanningProviderId = Guid.NewGuid(),
			PlanningModelId = "claude-sonnet-4"
		};
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Claude",
			Type = ProviderType.Claude,
			IsEnabled = true,
			IsDefault = true
		};
		var job = new Job
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			ProviderId = provider.Id,
			GoalPrompt = "Original prompt",
			Status = JobStatus.Failed,
			PlanningOutput = "Existing saved plan",
			PlanningProviderId = provider.Id,
			PlanningModelUsed = "claude-sonnet-4",
			PlanningInputTokens = 123,
			PlanningOutputTokens = 456,
			PlanningCostUsd = 1.23m,
			PlanningGeneratedAt = DateTime.UtcNow.AddMinutes(-5)
		};

		dbContext.Projects.Add(project);
		dbContext.Providers.Add(provider);
		dbContext.Jobs.Add(job);
		await dbContext.SaveChangesAsync();

		var serviceProvider = new ServiceCollection().BuildServiceProvider();
		var jobService = new JobService(dbContext, serviceProvider);

		var updated = await jobService.UpdateJobPromptAsync(job.Id, "Updated prompt");

		Assert.True(updated);
		var savedJob = await dbContext.Jobs.SingleAsync(j => j.Id == job.Id);
		Assert.Equal("Updated prompt", savedJob.GoalPrompt);
		Assert.Null(savedJob.PlanningOutput);
		Assert.Null(savedJob.PlanningProviderId);
		Assert.Null(savedJob.PlanningModelUsed);
		Assert.Null(savedJob.PlanningReasoningEffortUsed);
		Assert.Null(savedJob.PlanningGeneratedAt);
		Assert.Null(savedJob.PlanningInputTokens);
		Assert.Null(savedJob.PlanningOutputTokens);
		Assert.Null(savedJob.PlanningCostUsd);
	}

	[Fact]
	public async Task ResetJobWithOptionsAsync_PersistsNormalizedReasoningEffortAndClearsPlanningReasoning()
	{
		await using var dbContext = CreateDbContext();
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "GitHub Copilot",
			Type = ProviderType.Copilot,
			ConnectionMode = ProviderConnectionMode.CLI,
			IsEnabled = true,
			IsDefault = true
		};
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Retry Project",
			WorkingPath = "/tmp/retry-project"
		};
		var job = new Job
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			ProviderId = provider.Id,
			GoalPrompt = "Retry me",
			Status = JobStatus.Failed,
			PlanningOutput = "Existing plan",
			PlanningProviderId = provider.Id,
			PlanningModelUsed = "gpt-5.4",
			PlanningReasoningEffortUsed = "medium",
			PlanningInputTokens = 100,
			PlanningOutputTokens = 50,
			PlanningCostUsd = 0.75m,
			PlanningGeneratedAt = DateTime.UtcNow.AddMinutes(-3)
		};

		dbContext.Projects.Add(project);
		dbContext.Providers.Add(provider);
		dbContext.Jobs.Add(job);
		await dbContext.SaveChangesAsync();

		var serviceProvider = new ServiceCollection().BuildServiceProvider();
		var jobService = new JobService(dbContext, serviceProvider);

		var reset = await jobService.ResetJobWithOptionsAsync(job.Id, reasoningEffort: " High ");

		Assert.True(reset);
		var savedJob = await dbContext.Jobs.SingleAsync(item => item.Id == job.Id);
		Assert.Equal(JobStatus.New, savedJob.Status);
		Assert.Equal("high", savedJob.ReasoningEffort);
		Assert.Null(savedJob.PlanningOutput);
		Assert.Null(savedJob.PlanningProviderId);
		Assert.Null(savedJob.PlanningModelUsed);
		Assert.Null(savedJob.PlanningReasoningEffortUsed);
		Assert.Null(savedJob.PlanningGeneratedAt);
		Assert.Null(savedJob.PlanningInputTokens);
		Assert.Null(savedJob.PlanningOutputTokens);
		Assert.Null(savedJob.PlanningCostUsd);
	}

	[Fact]
	public async Task UpdateJobPromptAsync_UpdatesPromptDerivedTitle()
	{
		await using var dbContext = CreateDbContext();
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Manual Job Project",
			WorkingPath = "/tmp/manual-job-project"
		};
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Copilot",
			Type = ProviderType.Copilot,
			IsEnabled = true,
			IsDefault = true
		};
		var originalPrompt = "Fix the prompt wording";
		var job = new Job
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			ProviderId = provider.Id,
			Title = originalPrompt,
			GoalPrompt = originalPrompt,
			Status = JobStatus.New
		};

		dbContext.Projects.Add(project);
		dbContext.Providers.Add(provider);
		dbContext.Jobs.Add(job);
		await dbContext.SaveChangesAsync();

		var serviceProvider = new ServiceCollection().BuildServiceProvider();
		var jobService = new JobService(dbContext, serviceProvider);

		var updated = await jobService.UpdateJobPromptAsync(job.Id, "Fix the goal prompt wording");

		Assert.True(updated);
		var savedJob = await dbContext.Jobs.SingleAsync(j => j.Id == job.Id);
		Assert.Equal("Fix the goal prompt wording", savedJob.GoalPrompt);
		Assert.Equal("Fix the goal prompt wording", savedJob.Title);
	}

	[Fact]
	public async Task UpdateJobPromptAsync_PreservesIdeaTitle()
	{
		await using var dbContext = CreateDbContext();
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Idea Job Project",
			WorkingPath = "/tmp/idea-job-project"
		};
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Claude",
			Type = ProviderType.Claude,
			IsEnabled = true,
			IsDefault = true
		};
		var job = new Job
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			ProviderId = provider.Id,
			Title = "Use the original idea text",
			GoalPrompt = "Expanded implementation prompt with more detail",
			Status = JobStatus.Failed
		};

		dbContext.Projects.Add(project);
		dbContext.Providers.Add(provider);
		dbContext.Jobs.Add(job);
		await dbContext.SaveChangesAsync();

		var serviceProvider = new ServiceCollection().BuildServiceProvider();
		var jobService = new JobService(dbContext, serviceProvider);

		var updated = await jobService.UpdateJobPromptAsync(job.Id, "Updated expanded implementation prompt");

		Assert.True(updated);
		var savedJob = await dbContext.Jobs.SingleAsync(j => j.Id == job.Id);
		Assert.Equal("Updated expanded implementation prompt", savedJob.GoalPrompt);
		Assert.Equal("Use the original idea text", savedJob.Title);
	}

	[Fact]
	public async Task ResumeJobAsync_ReturnsPlanningStatus_WhenPlanIsStillPending()
	{
		await using var dbContext = CreateDbContext();
		var planningProviderId = Guid.NewGuid();
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Planning Resume Project",
			WorkingPath = "/tmp/planning-resume-project",
			PlanningEnabled = true,
			PlanningProviderId = planningProviderId,
			PlanningModelId = "claude-sonnet-4"
		};
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Claude",
			Type = ProviderType.Claude,
			IsEnabled = true,
			IsDefault = true
		};
		var job = new Job
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			Project = project,
			ProviderId = provider.Id,
			GoalPrompt = "Implement the feature",
			Status = JobStatus.Paused,
			PendingInteractionPrompt = "Continue?",
			InteractionType = "confirmation"
		};

		dbContext.Projects.Add(project);
		dbContext.Providers.Add(provider);
		dbContext.Jobs.Add(job);
		await dbContext.SaveChangesAsync();

		var serviceProvider = new ServiceCollection().BuildServiceProvider();
		var jobService = new JobService(dbContext, serviceProvider);

		var resumed = await jobService.ResumeJobAsync(job.Id);

		Assert.True(resumed);
		var savedJob = await dbContext.Jobs.SingleAsync(j => j.Id == job.Id);
		Assert.Equal(JobStatus.Planning, savedJob.Status);
		Assert.Null(savedJob.PendingInteractionPrompt);
		Assert.Null(savedJob.InteractionType);
	}

	[Fact]
	public async Task ContinueJobAsync_PersistsUserFollowUpAndResetsCompletedJob()
	{
		await using var dbContext = CreateDbContext();
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Follow-Up Project",
			WorkingPath = "/tmp/follow-up-project"
		};
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Copilot",
			Type = ProviderType.Copilot,
			IsEnabled = true,
			IsDefault = true
		};
		var job = new Job
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			ProviderId = provider.Id,
			GoalPrompt = "Implement the initial feature",
			Status = JobStatus.Completed,
			SessionId = "session-123",
			CompletedAt = DateTime.UtcNow,
			Output = "Done",
			ConsoleOutput = "console output",
			CommandUsed = "copilot run",
			GitDiff = "diff --git",
			BuildVerified = true,
			BuildOutput = "build ok",
			SessionSummary = "Previous summary",
			CurrentCycle = 3,
			InputTokens = 120,
			OutputTokens = 80,
			TotalCostUsd = 1.23m
		};

		dbContext.Projects.Add(project);
		dbContext.Providers.Add(provider);
		dbContext.Jobs.Add(job);
		dbContext.JobProviderAttempts.Add(new JobProviderAttempt
		{
			Id = Guid.NewGuid(),
			JobId = job.Id,
			ProviderId = provider.Id,
			ProviderName = provider.Name,
			AttemptOrder = 0,
			Reason = "initial-execution",
			AttemptedAt = DateTime.UtcNow.AddMinutes(-5)
		});
		await dbContext.SaveChangesAsync();

		var serviceProvider = new ServiceCollection().BuildServiceProvider();
		var jobService = new JobService(dbContext, serviceProvider);

		var continued = await jobService.ContinueJobAsync(job.Id, "Address the remaining failing tests.");

		Assert.True(continued);

		var savedJob = await dbContext.Jobs.SingleAsync(j => j.Id == job.Id);
		Assert.Equal(JobStatus.New, savedJob.Status);
		Assert.Equal("session-123", savedJob.SessionId);
		Assert.Contains("Previous goal: Implement the initial feature", savedJob.GoalPrompt);
		Assert.Contains("Follow-up instructions:\nAddress the remaining failing tests.", savedJob.GoalPrompt);
		Assert.Null(savedJob.CompletedAt);
		Assert.Null(savedJob.Output);
		Assert.Null(savedJob.ConsoleOutput);
		Assert.Null(savedJob.CommandUsed);
		Assert.Null(savedJob.GitDiff);
		Assert.Null(savedJob.BuildVerified);
		Assert.Null(savedJob.BuildOutput);
		Assert.Null(savedJob.SessionSummary);
		Assert.Equal(1, savedJob.CurrentCycle);
		Assert.Null(savedJob.InputTokens);
		Assert.Null(savedJob.OutputTokens);
		Assert.Null(savedJob.TotalCostUsd);

		var savedMessages = await dbContext.JobMessages
			.Where(message => message.JobId == job.Id)
			.OrderBy(message => message.CreatedAt)
			.ToListAsync();
		var followUpMessage = Assert.Single(savedMessages);
		Assert.Equal(MessageRole.User, followUpMessage.Role);
		Assert.Equal("Address the remaining failing tests.", followUpMessage.Content);
		Assert.Empty(await dbContext.JobProviderAttempts.Where(attempt => attempt.JobId == job.Id).ToListAsync());
	}

	[Fact]
	public async Task ContinueJobAsync_RejectsJobsThatAreNotCompleted()
	{
		await using var dbContext = CreateDbContext();
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Processing Project",
			WorkingPath = "/tmp/processing-project"
		};
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Claude",
			Type = ProviderType.Claude,
			IsEnabled = true,
			IsDefault = true
		};
		var job = new Job
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			ProviderId = provider.Id,
			GoalPrompt = "Still running",
			Status = JobStatus.Processing
		};

		dbContext.Projects.Add(project);
		dbContext.Providers.Add(provider);
		dbContext.Jobs.Add(job);
		await dbContext.SaveChangesAsync();

		var serviceProvider = new ServiceCollection().BuildServiceProvider();
		var jobService = new JobService(dbContext, serviceProvider);

		var continued = await jobService.ContinueJobAsync(job.Id, "Keep going.");

		Assert.False(continued);
		Assert.Empty(await dbContext.JobMessages.Where(message => message.JobId == job.Id).ToListAsync());
	}

	[Fact]
	public async Task SuggestIdeasFromCodebaseAsync_UsesRequestedProviderModelAndIdeaCount()
	{
		await using var dbContext = CreateDbContext();
		var workingPath = Path.Combine(Path.GetTempPath(), $"vibeswarm-suggestion-{Guid.NewGuid():N}");
		Directory.CreateDirectory(workingPath);
		File.WriteAllText(Path.Combine(workingPath, "Program.cs"), "Console.WriteLine(\"Hello from VibeSwarm\");");
		try
		{
			var project = new Project
			{
				Id = Guid.NewGuid(),
				Name = "Suggestion Project",
				WorkingPath = workingPath
			};
			var selectedProvider = new InferenceProvider
			{
				Id = Guid.NewGuid(),
				Name = "Preferred Ollama",
				Endpoint = "http://preferred-ollama:11434",
				IsEnabled = true,
				Models =
				[
					new InferenceModel
					{
						Id = Guid.NewGuid(),
						ModelId = "qwen2.5-coder:7b",
						TaskType = "suggest",
						IsDefault = true,
						IsAvailable = true
					},
					new InferenceModel
					{
						Id = Guid.NewGuid(),
						ModelId = "deepseek-coder:14b",
						TaskType = "default",
						IsDefault = false,
						IsAvailable = true
					}
				]
			};
			var otherProvider = new InferenceProvider
			{
				Id = Guid.NewGuid(),
				Name = "Fallback Ollama",
				Endpoint = "http://fallback-ollama:11434",
				IsEnabled = true,
				Models =
				[
					new InferenceModel
					{
						Id = Guid.NewGuid(),
						ModelId = "llama3.2",
						TaskType = "default",
						IsDefault = true,
						IsAvailable = true
					}
				]
			};

			foreach (var model in selectedProvider.Models)
			{
				model.InferenceProviderId = selectedProvider.Id;
			}

			foreach (var model in otherProvider.Models)
			{
				model.InferenceProviderId = otherProvider.Id;
			}

			dbContext.Projects.Add(project);
			dbContext.InferenceProviders.AddRange(selectedProvider, otherProvider);
			await dbContext.SaveChangesAsync();

			var inferenceService = new FakeInferenceService
			{
				Response = new InferenceResponse
				{
					Success = true,
					ModelUsed = "deepseek-coder:14b",
					Response = """
					- Add a project summary card to the detail page
					- Add a recent inference activity feed
					- Add idea filtering by status and age
					- Add smarter empty states across project pages
					""",
					DurationMs = 1500
				}
			};

			var ideaService = CreateIdeaService(dbContext, new Provider(), inferenceService: inferenceService);
			var result = await ideaService.SuggestIdeasFromCodebaseAsync(project.Id, new SuggestIdeasRequest
			{
				ProviderId = selectedProvider.Id,
				ModelId = "deepseek-coder:14b",
				IdeaCount = 2
			});

			Assert.True(result.Success);
			Assert.Equal(2, result.Ideas.Count);
			Assert.Equal(selectedProvider.Endpoint, inferenceService.LastRequest?.Endpoint);
			Assert.Equal("deepseek-coder:14b", inferenceService.LastRequest?.Model);
			Assert.Contains("Return exactly 2 concrete, actionable ideas.", inferenceService.LastRequest?.Prompt);
			Assert.Contains("De-prioritize development-only work such as adding tests", inferenceService.LastRequest?.Prompt);
			Assert.Contains("Do not repeat, restate, or lightly rename existing project ideas.", inferenceService.LastRequest?.Prompt);
			Assert.Equal(2, await dbContext.Ideas.CountAsync(idea => idea.ProjectId == project.Id));
		}
		finally
		{
			if (Directory.Exists(workingPath))
			{
				Directory.Delete(workingPath, recursive: true);
			}
		}
	}

	[Fact]
	public async Task SuggestIdeasFromCodebaseAsync_IncludesExistingIdeasAndAdditionalContextInPrompt()
	{
		await using var dbContext = CreateDbContext();
		var workingPath = Path.Combine(Path.GetTempPath(), $"vibeswarm-scheduled-suggestion-{Guid.NewGuid():N}");
		Directory.CreateDirectory(workingPath);
		File.WriteAllText(Path.Combine(workingPath, "Program.cs"), "Console.WriteLine(\"Hello from VibeSwarm\");");
		try
		{
			var project = new Project
			{
				Id = Guid.NewGuid(),
				Name = "Scheduled Suggestion Project",
				WorkingPath = workingPath
			};
			var provider = new InferenceProvider
			{
				Id = Guid.NewGuid(),
				Name = "Preferred Ollama",
				Endpoint = "http://preferred-ollama:11434",
				IsEnabled = true,
				Models =
				[
					new InferenceModel
					{
						Id = Guid.NewGuid(),
						ModelId = "qwen2.5-coder:7b",
						TaskType = "suggest",
						IsDefault = true,
						IsAvailable = true
					}
				]
			};

			provider.Models.First().InferenceProviderId = provider.Id;
			dbContext.Projects.Add(project);
			dbContext.InferenceProviders.Add(provider);
			dbContext.Ideas.Add(new Idea
			{
				Id = Guid.NewGuid(),
				ProjectId = project.Id,
				Description = "Add a project summary card to the detail page",
				SortOrder = 0,
				CreatedAt = DateTime.UtcNow.AddMinutes(-3)
			});
			await dbContext.SaveChangesAsync();

			var inferenceService = new FakeInferenceService
			{
				Response = new InferenceResponse
				{
					Success = true,
					ModelUsed = "qwen2.5-coder:7b",
					Response = "- Add a recent inference activity feed",
					DurationMs = 700
				}
			};

			var ideaService = CreateIdeaService(dbContext, new Provider(), inferenceService: inferenceService);
			var result = await ideaService.SuggestIdeasFromCodebaseAsync(project.Id, new SuggestIdeasRequest
			{
				ProviderId = provider.Id,
				IdeaCount = 1,
				AdditionalContext = "This idea-generation request comes from the scheduler."
			});

			Assert.True(result.Success);
			Assert.NotNull(inferenceService.LastRequest?.Prompt);
			Assert.Contains("<existing_ideas>", inferenceService.LastRequest!.Prompt);
			Assert.Contains("Add a project summary card to the detail page", inferenceService.LastRequest.Prompt);
			Assert.Contains("<run_context>", inferenceService.LastRequest.Prompt);
			Assert.Contains("comes from the scheduler", inferenceService.LastRequest.Prompt, StringComparison.OrdinalIgnoreCase);
		}
		finally
		{
			if (Directory.Exists(workingPath))
			{
				Directory.Delete(workingPath, recursive: true);
			}
		}
	}

	[Fact]
	public async Task SuggestIdeasFromCodebaseAsync_PrioritizesUserImpactIdeasOverDevelopmentOnlySuggestions()
	{
		await using var dbContext = CreateDbContext();
		var workingPath = Path.Combine(Path.GetTempPath(), $"vibeswarm-ranked-suggestion-{Guid.NewGuid():N}");
		Directory.CreateDirectory(workingPath);
		File.WriteAllText(Path.Combine(workingPath, "Program.cs"), "Console.WriteLine(\"Hello from VibeSwarm\");");
		try
		{
			var project = new Project
			{
				Id = Guid.NewGuid(),
				Name = "Ranked Suggestion Project",
				WorkingPath = workingPath
			};
			var provider = new InferenceProvider
			{
				Id = Guid.NewGuid(),
				Name = "Preferred Ollama",
				Endpoint = "http://preferred-ollama:11434",
				IsEnabled = true,
				Models =
				[
					new InferenceModel
					{
						Id = Guid.NewGuid(),
						ModelId = "qwen2.5-coder:7b",
						TaskType = "suggest",
						IsDefault = true,
						IsAvailable = true
					}
				]
			};

			provider.Models.First().InferenceProviderId = provider.Id;

			dbContext.Projects.Add(project);
			dbContext.InferenceProviders.Add(provider);
			await dbContext.SaveChangesAsync();

			var inferenceService = new FakeInferenceService
			{
				Response = new InferenceResponse
				{
					Success = true,
					ModelUsed = "qwen2.5-coder:7b",
					Response = """
					- Add tests for project creation and provider setup flows
					- Add a mobile-friendly active job summary so users can check progress from their phones
					- Show inline validation errors when users create a project with missing fields
					""",
					DurationMs = 1100
				}
			};

			var ideaService = CreateIdeaService(dbContext, new Provider(), inferenceService: inferenceService);
			var result = await ideaService.SuggestIdeasFromCodebaseAsync(project.Id, new SuggestIdeasRequest
			{
				ProviderId = provider.Id,
				IdeaCount = 2
			});

			Assert.True(result.Success);
			Assert.Equal(
				[
					"Add a mobile-friendly active job summary so users can check progress from their phones",
					"Show inline validation errors when users create a project with missing fields"
				],
				result.Ideas.Select(idea => idea.Description).ToArray());
			Assert.DoesNotContain(result.Ideas, idea => idea.Description.Contains("Add tests", StringComparison.OrdinalIgnoreCase));
		}
		finally
		{
			if (Directory.Exists(workingPath))
			{
				Directory.Delete(workingPath, recursive: true);
			}
		}
	}

	[Fact]
	public async Task SuggestIdeasFromCodebaseAsync_SkipsExistingIdeasUsingNormalizedDescriptions()
	{
		await using var dbContext = CreateDbContext();
		var workingPath = Path.Combine(Path.GetTempPath(), $"vibeswarm-duplicate-suggestion-{Guid.NewGuid():N}");
		Directory.CreateDirectory(workingPath);
		File.WriteAllText(Path.Combine(workingPath, "Program.cs"), "Console.WriteLine(\"Hello from VibeSwarm\");");
		try
		{
			var project = new Project
			{
				Id = Guid.NewGuid(),
				Name = "Duplicate Suggestion Project",
				WorkingPath = workingPath
			};
			var provider = new InferenceProvider
			{
				Id = Guid.NewGuid(),
				Name = "Preferred Ollama",
				Endpoint = "http://preferred-ollama:11434",
				IsEnabled = true,
				Models =
				[
					new InferenceModel
					{
						Id = Guid.NewGuid(),
						ModelId = "qwen2.5-coder:7b",
						TaskType = "suggest",
						IsDefault = true,
						IsAvailable = true
					}
				]
			};
			var existingIdea = new Idea
			{
				Id = Guid.NewGuid(),
				ProjectId = project.Id,
				Description = "Add a project summary card to the detail page",
				SortOrder = 0,
				CreatedAt = DateTime.UtcNow.AddMinutes(-10)
			};

			provider.Models.First().InferenceProviderId = provider.Id;

			dbContext.Projects.Add(project);
			dbContext.InferenceProviders.Add(provider);
			dbContext.Ideas.Add(existingIdea);
			await dbContext.SaveChangesAsync();

			var inferenceService = new FakeInferenceService
			{
				Response = new InferenceResponse
				{
					Success = true,
					ModelUsed = "qwen2.5-coder:7b",
					Response = """
					-   add a project summary card to the detail page
					- Add a recent inference activity feed
					""",
					DurationMs = 900
				}
			};

			var ideaService = CreateIdeaService(dbContext, new Provider(), inferenceService: inferenceService);
			var result = await ideaService.SuggestIdeasFromCodebaseAsync(project.Id, new SuggestIdeasRequest
			{
				ProviderId = provider.Id,
				IdeaCount = 2
			});

			Assert.True(result.Success);
			Assert.Single(result.Ideas);
			Assert.Equal("Add a recent inference activity feed", result.Ideas[0].Description);
			Assert.Contains("Skipped 1 duplicate existing idea.", result.Message, StringComparison.Ordinal);
			Assert.Equal(2, await dbContext.Ideas.CountAsync(idea => idea.ProjectId == project.Id));
		}
		finally
		{
			if (Directory.Exists(workingPath))
			{
				Directory.Delete(workingPath, recursive: true);
			}
		}
	}

	[Fact]
	public async Task SuggestIdeasFromCodebaseAsync_ReturnsSuccessWhenAllSuggestionsAlreadyExist()
	{
		await using var dbContext = CreateDbContext();
		var workingPath = Path.Combine(Path.GetTempPath(), $"vibeswarm-all-duplicates-{Guid.NewGuid():N}");
		Directory.CreateDirectory(workingPath);
		File.WriteAllText(Path.Combine(workingPath, "Program.cs"), "Console.WriteLine(\"Hello from VibeSwarm\");");
		try
		{
			var project = new Project
			{
				Id = Guid.NewGuid(),
				Name = "All Duplicate Suggestions Project",
				WorkingPath = workingPath
			};
			var provider = new InferenceProvider
			{
				Id = Guid.NewGuid(),
				Name = "Preferred Ollama",
				Endpoint = "http://preferred-ollama:11434",
				IsEnabled = true,
				Models =
				[
					new InferenceModel
					{
						Id = Guid.NewGuid(),
						ModelId = "qwen2.5-coder:7b",
						TaskType = "suggest",
						IsDefault = true,
						IsAvailable = true
					}
				]
			};

			provider.Models.First().InferenceProviderId = provider.Id;

			dbContext.Projects.Add(project);
			dbContext.InferenceProviders.Add(provider);
			dbContext.Ideas.AddRange(
				new Idea
				{
					Id = Guid.NewGuid(),
					ProjectId = project.Id,
					Description = "Add a project summary card to the detail page",
					SortOrder = 0,
					CreatedAt = DateTime.UtcNow.AddMinutes(-10)
				},
				new Idea
				{
					Id = Guid.NewGuid(),
					ProjectId = project.Id,
					Description = "Add a recent inference activity feed",
					SortOrder = 1,
					CreatedAt = DateTime.UtcNow.AddMinutes(-9)
				});
			await dbContext.SaveChangesAsync();

			var inferenceService = new FakeInferenceService
			{
				Response = new InferenceResponse
				{
					Success = true,
					ModelUsed = "qwen2.5-coder:7b",
					Response = """
					- Add a project summary card to the detail page
					- add a recent   inference activity feed
					""",
					DurationMs = 950
				}
			};

			var ideaService = CreateIdeaService(dbContext, new Provider(), inferenceService: inferenceService);
			var result = await ideaService.SuggestIdeasFromCodebaseAsync(project.Id, new SuggestIdeasRequest
			{
				ProviderId = provider.Id,
				IdeaCount = 2
			});

			Assert.True(result.Success);
			Assert.Equal(SuggestIdeasStage.Success, result.Stage);
			Assert.Empty(result.Ideas);
			Assert.Contains("already exist for this project", result.Message, StringComparison.Ordinal);
			Assert.Equal(2, await dbContext.Ideas.CountAsync(idea => idea.ProjectId == project.Id));
		}
		finally
		{
			if (Directory.Exists(workingPath))
			{
				Directory.Delete(workingPath, recursive: true);
			}
		}
	}

	[Fact]
	public async Task SuggestIdeasFromCodebaseAsync_ReturnsModelNotFound_WhenRequestedModelIsUnavailable()
	{
		await using var dbContext = CreateDbContext();
		var workingPath = Path.Combine(Path.GetTempPath(), $"vibeswarm-suggestion-{Guid.NewGuid():N}");
		Directory.CreateDirectory(workingPath);
		File.WriteAllText(Path.Combine(workingPath, "Program.cs"), "Console.WriteLine(\"Hello from VibeSwarm\");");
		try
		{
			var project = new Project
			{
				Id = Guid.NewGuid(),
				Name = "Suggestion Project",
				WorkingPath = workingPath
			};
			var provider = new InferenceProvider
			{
				Id = Guid.NewGuid(),
				Name = "Preferred Ollama",
				Endpoint = "http://preferred-ollama:11434",
				IsEnabled = true,
				Models =
				[
					new InferenceModel
					{
						Id = Guid.NewGuid(),
						ModelId = "qwen2.5-coder:7b",
						TaskType = "suggest",
						IsDefault = true,
						IsAvailable = true
					}
				]
			};

			provider.Models.First().InferenceProviderId = provider.Id;
			dbContext.Projects.Add(project);
			dbContext.InferenceProviders.Add(provider);
			await dbContext.SaveChangesAsync();

			var inferenceService = new FakeInferenceService();
			var ideaService = CreateIdeaService(dbContext, new Provider(), inferenceService: inferenceService);
			var result = await ideaService.SuggestIdeasFromCodebaseAsync(project.Id, new SuggestIdeasRequest
			{
				ProviderId = provider.Id,
				ModelId = "missing-model",
				IdeaCount = 2
			});

			Assert.False(result.Success);
			Assert.Equal(SuggestIdeasStage.ModelNotFound, result.Stage);
			Assert.Contains("selected model", result.Message, StringComparison.OrdinalIgnoreCase);
			Assert.Null(inferenceService.LastRequest);
		}
		finally
		{
			if (Directory.Exists(workingPath))
			{
				Directory.Delete(workingPath, recursive: true);
			}
		}
	}

	[Fact]
	public async Task SuggestIdeasFromCodebaseAsync_UsesConfiguredProviderWhenInferenceIsNotSelected()
	{
		await using var dbContext = CreateDbContext();
		var workingPath = Path.Combine(Path.GetTempPath(), $"vibeswarm-provider-suggestion-{Guid.NewGuid():N}");
		Directory.CreateDirectory(workingPath);
		File.WriteAllText(Path.Combine(workingPath, "Program.cs"), "Console.WriteLine(\"Hello from VibeSwarm\");");
		try
		{
			var project = new Project
			{
				Id = Guid.NewGuid(),
				Name = "Provider Suggestion Project",
				WorkingPath = workingPath
			};
			var provider = new Provider
			{
				Id = Guid.NewGuid(),
				Name = "Claude",
				Type = ProviderType.Claude,
				IsEnabled = true,
				IsDefault = true
			};
			var providerModel = new ProviderModel
			{
				Id = Guid.NewGuid(),
				ProviderId = provider.Id,
				ModelId = "claude-sonnet-4.6",
				DisplayName = "Claude Sonnet 4.6",
				IsAvailable = true,
				IsDefault = true
			};
			var providerInstance = new FakeProviderInstance
			{
				ExecutionResult = new ExecutionResult
				{
					Success = true,
					ModelUsed = "claude-sonnet-4.6",
					Output = """
					- Add a provider-backed suggestion source selector to the ideas modal
					- Show cached provider models when suggesting ideas from configured providers
					- Add tests that cover provider-based idea suggestions
					"""
				}
			};

			dbContext.Projects.Add(project);
			dbContext.Providers.Add(provider);
			dbContext.ProviderModels.Add(providerModel);
			await dbContext.SaveChangesAsync();

			var ideaService = CreateIdeaService(dbContext, provider, providerInstance);
			var result = await ideaService.SuggestIdeasFromCodebaseAsync(project.Id, new SuggestIdeasRequest
			{
				UseInference = false,
				ProviderId = provider.Id,
				ModelId = providerModel.ModelId,
				IdeaCount = 2
			});

			Assert.True(result.Success);
			Assert.Equal(2, result.Ideas.Count);
			Assert.NotNull(providerInstance.LastExecutePrompt);
			Assert.Contains("Return exactly 2 concrete, actionable ideas.", providerInstance.LastExecutePrompt);
			Assert.Equal(providerModel.ModelId, providerInstance.LastExecutionOptions?.Model);
			Assert.Equal(2, await dbContext.Ideas.CountAsync(idea => idea.ProjectId == project.Id));
		}
		finally
		{
			if (Directory.Exists(workingPath))
			{
				Directory.Delete(workingPath, recursive: true);
			}
		}
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
			new FakeProjectMemoryService(),
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
	public async Task CreateAsync_RejectsIdeaDescriptionThatExceedsLimit()
	{
		await using var dbContext = CreateDbContext();
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Long Idea Project",
			WorkingPath = "/tmp/long-idea-project"
		};

		dbContext.Projects.Add(project);
		await dbContext.SaveChangesAsync();

		var ideaService = new IdeaService(
			dbContext,
			null!,
			null!,
			null!,
			new FakeProjectMemoryService(),
			NullLogger<IdeaService>.Instance);

		var error = await Assert.ThrowsAsync<System.ComponentModel.DataAnnotations.ValidationException>(() => ideaService.CreateAsync(new Idea
		{
			ProjectId = project.Id,
			Description = new string('x', ValidationLimits.IdeaDescriptionMaxLength + 1)
		}));

		Assert.Contains(nameof(Idea.Description), error.Message);
	}

	[Fact]
	public async Task CreateAsync_WithAttachments_PersistsMetadataAndFiles()
	{
		await using var dbContext = CreateDbContext();
		var workingPath = Path.Combine(Path.GetTempPath(), $"vibeswarm-idea-attachments-{Guid.NewGuid():N}");
		Directory.CreateDirectory(workingPath);

		try
		{
			var project = new Project
			{
				Id = Guid.NewGuid(),
				Name = "Attachment Project",
				WorkingPath = workingPath
			};

			dbContext.Projects.Add(project);
			await dbContext.SaveChangesAsync();

			var ideaService = new IdeaService(
				dbContext,
				null!,
				null!,
				null!,
				new FakeProjectMemoryService(),
				NullLogger<IdeaService>.Instance);

			var created = await ideaService.CreateAsync(new CreateIdeaRequest
			{
				ProjectId = project.Id,
				Description = "Add image context support",
				Attachments =
				[
					new IdeaAttachmentUpload
					{
						FileName = "screenshot.png",
						ContentType = "image/png",
						Content = [1, 2, 3, 4]
					}
				]
			});

			var savedIdea = await dbContext.Ideas.Include(idea => idea.Attachments).SingleAsync(idea => idea.Id == created.Id);
			var attachment = Assert.Single(savedIdea.Attachments);
			Assert.Equal("screenshot.png", attachment.FileName);
			Assert.Equal("image/png", attachment.ContentType);
			Assert.Equal(4, attachment.SizeBytes);
			Assert.Contains(Path.Combine(".vibeswarm", "idea-attachments"), attachment.RelativePath, StringComparison.Ordinal);

			var storedPath = Path.Combine(workingPath, attachment.RelativePath);
			Assert.True(File.Exists(storedPath));
			Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(storedPath));
		}
		finally
		{
			if (Directory.Exists(workingPath))
			{
				Directory.Delete(workingPath, recursive: true);
			}
		}
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
			new FakeProjectMemoryService(),
			NullLogger<IdeaService>.Instance);

		var ideas = (await ideaService.GetByProjectIdAsync(project.Id)).ToList();

		var loadedIdea = Assert.Single(ideas);
		Assert.NotNull(loadedIdea.Job);
		Assert.Equal(JobStatus.New, loadedIdea.Job!.Status);
	}

	[Fact]
	public async Task GetPagedByProjectIdAsync_ReturnsRequestedIdeaPageWithAggregateCounts()
	{
		await using var dbContext = CreateDbContext();
		var projectId = Guid.NewGuid();

		dbContext.Projects.Add(new Project
		{
			Id = projectId,
			Name = "Paged Ideas",
			WorkingPath = "/tmp/paged-ideas"
		});

		for (var index = 0; index < 6; index++)
		{
			dbContext.Ideas.Add(new Idea
			{
				Id = Guid.NewGuid(),
				ProjectId = projectId,
				Description = $"Idea {index + 1}",
				SortOrder = index,
				CreatedAt = DateTime.UtcNow.AddMinutes(index),
				IsProcessing = index == 5
			});
		}

		await dbContext.SaveChangesAsync();

		var ideaService = new IdeaService(
			dbContext,
			null!,
			null!,
			null!,
			new FakeProjectMemoryService(),
			NullLogger<IdeaService>.Instance);

		var page = await ideaService.GetPagedByProjectIdAsync(projectId, page: 2, pageSize: 2);

		Assert.Equal(2, page.PageNumber);
		Assert.Equal(2, page.PageSize);
		Assert.Equal(6, page.TotalCount);
		Assert.Equal(5, page.UnprocessedCount);
		Assert.Equal(new[] { "Idea 3", "Idea 4" }, page.Items.Select(idea => idea.Description).ToArray());
	}

	[Fact]
	public async Task ConvertToJobAsync_UsesDirectImplementationPrompt_WhenNoApprovedExpansion()
	{
		await using var dbContext = CreateDbContext();
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Direct Ideas Project",
			WorkingPath = "/tmp/direct-ideas"
		};
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Copilot",
			Type = ProviderType.Copilot,
			IsEnabled = true,
			IsDefault = true
		};
		var idea = new Idea
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			Description = "Implement a compact dashboard widget",
			SortOrder = 0
		};

		dbContext.Projects.Add(project);
		dbContext.Providers.Add(provider);
		dbContext.Ideas.Add(idea);
		await dbContext.SaveChangesAsync();

		var ideaService = CreateIdeaService(dbContext, provider);
		var job = await ideaService.ConvertToJobAsync(idea.Id);

		Assert.NotNull(job);
		Assert.Contains("You are a staff-level software engineer implementing a feature directly from a product idea.", job!.GoalPrompt);
		Assert.Contains("Operate like an autonomous CI coding job", job.GoalPrompt);
		Assert.Contains("Do not mention or attribute the work to any provider, model, or CLI tool.", job.GoalPrompt);
		Assert.DoesNotContain("Begin by expanding this idea into a detailed specification, then implement it.", job.GoalPrompt);
	}

	[Fact]
	public async Task ConvertToJobAsync_IncludesAttachmentManifestAndAttachedFilesJson()
	{
		await using var dbContext = CreateDbContext();
		var workingPath = Path.Combine(Path.GetTempPath(), $"vibeswarm-job-attachments-{Guid.NewGuid():N}");
		var attachmentDirectory = Path.Combine(workingPath, ".vibeswarm", "idea-attachments");
		Directory.CreateDirectory(attachmentDirectory);

		try
		{
			var project = new Project
			{
				Id = Guid.NewGuid(),
				Name = "Idea Attachment Conversion",
				WorkingPath = workingPath
			};
			var provider = new Provider
			{
				Id = Guid.NewGuid(),
				Name = "OpenCode",
				Type = ProviderType.OpenCode,
				IsEnabled = true,
				IsDefault = true
			};
			var idea = new Idea
			{
				Id = Guid.NewGuid(),
				ProjectId = project.Id,
				Description = "Use the attached screenshot while implementing this idea",
				SortOrder = 0,
				Attachments =
				[
					new IdeaAttachment
					{
						Id = Guid.NewGuid(),
						FileName = "screenshot.png",
						ContentType = "image/png",
						RelativePath = Path.Combine(".vibeswarm", "idea-attachments", "screenshot.png"),
						SizeBytes = 4
					}
				]
			};

			dbContext.Projects.Add(project);
			dbContext.Providers.Add(provider);
			dbContext.Ideas.Add(idea);
			await dbContext.SaveChangesAsync();

			var absoluteAttachmentPath = Path.Combine(workingPath, idea.Attachments.Single().RelativePath);
			Directory.CreateDirectory(Path.GetDirectoryName(absoluteAttachmentPath)!);
			await File.WriteAllBytesAsync(absoluteAttachmentPath, [1, 2, 3, 4]);

			var ideaService = CreateIdeaService(dbContext, provider);
			var job = await ideaService.ConvertToJobAsync(idea.Id);

			Assert.NotNull(job);
			Assert.Contains("## Attached Context Files", job!.GoalPrompt);
			Assert.Contains("screenshot.png", job.GoalPrompt);
			Assert.Contains(absoluteAttachmentPath, job.GoalPrompt);
			Assert.NotNull(job.AttachedFilesJson);
			Assert.Contains(absoluteAttachmentPath, job.AttachedFilesJson, StringComparison.Ordinal);
		}
		finally
		{
			if (Directory.Exists(workingPath))
			{
				Directory.Delete(workingPath, recursive: true);
			}
		}
	}

	[Fact]
	public async Task ConvertToJobAsync_PrefersApprovedExpansion_OverDirectPrompt()
	{
		await using var dbContext = CreateDbContext();
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Approved Expansion Project",
			WorkingPath = "/tmp/approved-expansion"
		};
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "OpenCode",
			Type = ProviderType.OpenCode,
			IsEnabled = true,
			IsDefault = true
		};
		var idea = new Idea
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			Description = "Improve mobile navigation",
			ExpandedDescription = "Create a bottom navigation bar with clear project and job shortcuts.",
			ExpansionStatus = IdeaExpansionStatus.Approved,
			SortOrder = 0
		};

		dbContext.Projects.Add(project);
		dbContext.Providers.Add(provider);
		dbContext.Ideas.Add(idea);
		await dbContext.SaveChangesAsync();

		var ideaService = CreateIdeaService(dbContext, provider);
		var job = await ideaService.ConvertToJobAsync(idea.Id);

		Assert.NotNull(job);
		Assert.Contains("## Detailed Specification", job!.GoalPrompt);
		Assert.Contains("Operate like an autonomous CI coding job", job.GoalPrompt);
		Assert.Contains(idea.ExpandedDescription, job.GoalPrompt);
		Assert.DoesNotContain("Work directly from the idea below", job.GoalPrompt);
	}

	[Fact]
	public async Task ConvertToJobAsync_NotifiesIdeaStartedWithoutIdeaUpdated()
	{
		await using var dbContext = CreateDbContext();
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Idea Notification Project",
			WorkingPath = "/tmp/idea-notification-project"
		};
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Copilot",
			Type = ProviderType.Copilot,
			IsEnabled = true,
			IsDefault = true
		};
		var idea = new Idea
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			Description = "Start this idea once",
			SortOrder = 0
		};

		dbContext.Projects.Add(project);
		dbContext.Providers.Add(provider);
		dbContext.Ideas.Add(idea);
		await dbContext.SaveChangesAsync();

		var jobUpdateService = new FakeJobUpdateService();
		var ideaService = CreateIdeaService(dbContext, provider, jobUpdateService: jobUpdateService);
		var job = await ideaService.ConvertToJobAsync(idea.Id);

		Assert.NotNull(job);
		Assert.Empty(jobUpdateService.IdeaUpdatedNotifications);

		var startedNotification = Assert.Single(jobUpdateService.IdeaStartedNotifications);
		Assert.Equal(idea.Id, startedNotification.IdeaId);
		Assert.Equal(project.Id, startedNotification.ProjectId);
		Assert.Equal(job!.Id, startedNotification.JobId);
	}

	[Fact]
	public async Task ConvertToJobAsync_UsesProjectProviderPriorityAndPreferredModelDefaults()
	{
		await using var dbContext = CreateDbContext();
		var globalDefaultProvider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Global Default",
			Type = ProviderType.Copilot,
			IsEnabled = true,
			IsDefault = true
		};
		var projectProvider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Project Preferred",
			Type = ProviderType.Claude,
			IsEnabled = true,
			IsDefault = false
		};
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Project Priority Defaults",
			WorkingPath = "/tmp/project-priority-defaults",
			ProviderSelections =
			[
				new ProjectProvider
				{
					ProviderId = projectProvider.Id,
					Priority = 0,
					IsEnabled = true,
					PreferredModelId = "claude-opus-4.6",
					PreferredReasoningEffort = "high"
				}
			]
		};
		var idea = new Idea
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			Description = "Use project defaults when converting ideas",
			SortOrder = 0
		};

		dbContext.Projects.Add(project);
		dbContext.Providers.AddRange(globalDefaultProvider, projectProvider);
		dbContext.ProviderModels.AddRange(
			new ProviderModel
			{
				Id = Guid.NewGuid(),
				ProviderId = projectProvider.Id,
				ModelId = "claude-sonnet-4.6",
				DisplayName = "Claude Sonnet 4.6",
				IsAvailable = true,
				IsDefault = true
			},
			new ProviderModel
			{
				Id = Guid.NewGuid(),
				ProviderId = projectProvider.Id,
				ModelId = "claude-opus-4.6",
				DisplayName = "Claude Opus 4.6",
				IsAvailable = true,
				IsDefault = false
			});
		dbContext.Ideas.Add(idea);
		await dbContext.SaveChangesAsync();

		var ideaService = CreateIdeaService(dbContext, globalDefaultProvider);
		var job = await ideaService.ConvertToJobAsync(idea.Id);

		Assert.NotNull(job);
		Assert.Equal(projectProvider.Id, job!.ProviderId);
		Assert.Equal("claude-opus-4.6", job.ModelUsed);
		Assert.Equal("high", job.ReasoningEffort);
	}


	[Fact]
	public async Task StartProcessingAsync_PersistsQueueProviderAndModelOverrides()
	{
		await using var dbContext = CreateDbContext();
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Persist Queue Overrides",
			WorkingPath = "/tmp/persist-queue-overrides"
		};
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Claude",
			Type = ProviderType.Claude,
			IsEnabled = true,
			IsDefault = true
		};

		dbContext.Projects.Add(project);
		dbContext.Providers.Add(provider);
		dbContext.ProviderModels.Add(new ProviderModel
		{
			Id = Guid.NewGuid(),
			ProviderId = provider.Id,
			ModelId = "claude-sonnet-4.6",
			DisplayName = "Claude Sonnet 4.6",
			IsAvailable = true,
			IsDefault = true
		});
		await dbContext.SaveChangesAsync();

		var ideaService = CreateIdeaService(dbContext, provider);
		await ideaService.StartProcessingAsync(project.Id, new IdeaProcessingOptions
		{
			AutoCommitMode = AutoCommitMode.CommitOnly,
			ProviderId = provider.Id,
			ModelId = "claude-sonnet-4.6"
		});

		var updatedProject = await dbContext.Projects.SingleAsync(item => item.Id == project.Id);
		Assert.True(updatedProject.IdeasProcessingActive);
		Assert.True(updatedProject.IdeasAutoCommit);
		Assert.Equal(provider.Id, updatedProject.IdeasProcessingProviderId);
		Assert.Equal("claude-sonnet-4.6", updatedProject.IdeasProcessingModelId);
	}

	[Fact]
	public async Task ProcessNextIdeaIfReadyAsync_UsesQueuedProviderAndModelOverrides()
	{
		await using var dbContext = CreateDbContext();
		var defaultProvider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Default Provider",
			Type = ProviderType.Copilot,
			IsEnabled = true,
			IsDefault = true
		};
		var overrideProvider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Override Provider",
			Type = ProviderType.Claude,
			IsEnabled = true
		};
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Queued Override Project",
			WorkingPath = "/tmp/queued-override-project",
			IdeasProcessingActive = true,
			IdeasProcessingProviderId = overrideProvider.Id,
			IdeasProcessingModelId = "claude-opus-4.6"
		};
		var idea = new Idea
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			Description = "Use queued overrides",
			SortOrder = 0
		};

		dbContext.Projects.Add(project);
		dbContext.Providers.AddRange(defaultProvider, overrideProvider);
		dbContext.ProviderModels.Add(new ProviderModel
		{
			Id = Guid.NewGuid(),
			ProviderId = overrideProvider.Id,
			ModelId = "claude-opus-4.6",
			DisplayName = "Claude Opus 4.6",
			IsAvailable = true
		});
		dbContext.Ideas.Add(idea);
		await dbContext.SaveChangesAsync();

		var ideaService = CreateIdeaService(dbContext, defaultProvider);
		var processed = await ideaService.ProcessNextIdeaIfReadyAsync(project.Id);

		Assert.True(processed);
		var job = await dbContext.Jobs.SingleAsync(item => item.ProjectId == project.Id);
		Assert.Equal(overrideProvider.Id, job.ProviderId);
		Assert.Equal("claude-opus-4.6", job.ModelUsed);
	}
	[Fact]
	public async Task ProcessNextIdeaIfReadyAsync_WaitsForExistingProjectJobToFinish()
	{
		await using var dbContext = CreateDbContext();
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Ideas Queue Project",
			WorkingPath = "/tmp/ideas-queue-project",
			IdeasProcessingActive = true
		};
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Claude",
			Type = ProviderType.Claude,
			IsEnabled = true,
			IsDefault = true
		};
		var blockingJob = new Job
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			ProviderId = provider.Id,
			GoalPrompt = "Finish current project job first",
			Status = JobStatus.New,
			CreatedAt = DateTime.UtcNow.AddMinutes(-5)
		};
		var firstIdea = new Idea
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			Description = "First queued idea",
			SortOrder = 0,
			CreatedAt = DateTime.UtcNow.AddMinutes(-4)
		};
		var secondIdea = new Idea
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			Description = "Second queued idea",
			SortOrder = 1,
			CreatedAt = DateTime.UtcNow.AddMinutes(-3)
		};

		dbContext.Projects.Add(project);
		dbContext.Providers.Add(provider);
		dbContext.Jobs.Add(blockingJob);
		dbContext.Ideas.AddRange(firstIdea, secondIdea);
		await dbContext.SaveChangesAsync();

		var ideaService = CreateIdeaService(dbContext, provider);

		var processedWhileBlocked = await ideaService.ProcessNextIdeaIfReadyAsync(project.Id);
		Assert.False(processedWhileBlocked);
		Assert.Equal(1, await dbContext.Jobs.CountAsync(job => job.ProjectId == project.Id));
		Assert.Equal(0, await dbContext.Ideas.CountAsync(idea => idea.ProjectId == project.Id && idea.JobId != null));

		blockingJob.Status = JobStatus.Completed;
		blockingJob.CompletedAt = DateTime.UtcNow;
		await dbContext.SaveChangesAsync();

		var processedAfterCompletion = await ideaService.ProcessNextIdeaIfReadyAsync(project.Id);
		Assert.True(processedAfterCompletion);

		var linkedIdea = await dbContext.Ideas.FirstAsync(idea => idea.Id == firstIdea.Id);
		Assert.True(linkedIdea.IsProcessing);
		Assert.NotNull(linkedIdea.JobId);
		Assert.Equal(2, await dbContext.Jobs.CountAsync(job => job.ProjectId == project.Id));
	}

	[Fact]
	public async Task GetGlobalQueueSnapshotAsync_PrioritizesRecentlyUpdatedQueues_AndSupportsLargerIdeaPreviews()
	{
		await using var dbContext = CreateDbContext();
		var now = DateTime.UtcNow;
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Claude",
			Type = ProviderType.Claude,
			IsEnabled = true,
			IsDefault = true
		};
		var recentlyStartedProject = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Recent Queue",
			WorkingPath = "/tmp/recent-queue",
			IsActive = true,
			IdeasProcessingActive = true,
			UpdatedAt = now
		};
		var earlierStartedProject = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Earlier Queue",
			WorkingPath = "/tmp/earlier-queue",
			IsActive = true,
			IdeasProcessingActive = true,
			UpdatedAt = now.AddMinutes(-10)
		};
		var pendingProject = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Pending Queue",
			WorkingPath = "/tmp/pending-queue",
			IsActive = true,
			IdeasProcessingActive = false,
			UpdatedAt = now.AddMinutes(-20)
		};

		dbContext.Providers.Add(provider);
		dbContext.Projects.AddRange(recentlyStartedProject, earlierStartedProject, pendingProject);
		dbContext.Ideas.AddRange(
			CreateIdeaRange(recentlyStartedProject.Id, "Recent", 8, now.AddMinutes(-1))
				.Concat(CreateIdeaRange(earlierStartedProject.Id, "Earlier", 8, now.AddMinutes(-2)))
				.Concat(CreateIdeaRange(pendingProject.Id, "Pending", 8, now.AddMinutes(-3))));
		await dbContext.SaveChangesAsync();

		var ideaService = CreateIdeaService(dbContext, provider);
		var snapshot = await ideaService.GetGlobalQueueSnapshotAsync();

		Assert.Equal(24, snapshot.UpcomingIdeasCount);
		Assert.Equal(24, snapshot.UpcomingIdeas.Count);
		Assert.All(snapshot.UpcomingIdeas.Take(8), idea => Assert.Equal(recentlyStartedProject.Id, idea.ProjectId));
		Assert.All(snapshot.UpcomingIdeas.Skip(8).Take(8), idea => Assert.Equal(earlierStartedProject.Id, idea.ProjectId));
		Assert.All(snapshot.UpcomingIdeas.Skip(16).Take(8), idea => Assert.Equal(pendingProject.Id, idea.ProjectId));
		Assert.Equal(Enumerable.Range(0, 8), snapshot.UpcomingIdeas.Take(8).Select(idea => idea.SortOrder));
	}

	[Fact]
	public async Task JobQueueManager_GetPendingJobsAsync_ReturnsSingleJobPerProject_WhenProjectsUseDifferentProviders()
	{
		await using var dbContext = CreateDbContext();
		var firstProject = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Queue Manager Project One",
			WorkingPath = "/tmp/queue-manager-one"
		};
		var secondProject = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Queue Manager Project Two",
			WorkingPath = "/tmp/queue-manager-two"
		};
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "OpenCode",
			Type = ProviderType.OpenCode,
			IsEnabled = true,
			IsDefault = true
		};
		var secondProvider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Claude",
			Type = ProviderType.Claude,
			IsEnabled = true
		};

		dbContext.Projects.AddRange(firstProject, secondProject);
		dbContext.Providers.AddRange(provider, secondProvider);
		dbContext.Jobs.AddRange(
			new Job
			{
				Id = Guid.NewGuid(),
				ProjectId = firstProject.Id,
				ProviderId = provider.Id,
				GoalPrompt = "First project first queued job",
				Status = JobStatus.New,
				Priority = 10,
				CreatedAt = DateTime.UtcNow.AddMinutes(-4)
			},
			new Job
			{
				Id = Guid.NewGuid(),
				ProjectId = firstProject.Id,
				ProviderId = provider.Id,
				GoalPrompt = "First project second queued job",
				Status = JobStatus.New,
				Priority = 5,
				CreatedAt = DateTime.UtcNow.AddMinutes(-3)
			},
			new Job
			{
				Id = Guid.NewGuid(),
				ProjectId = secondProject.Id,
				ProviderId = secondProvider.Id,
				GoalPrompt = "Second project queued job",
				Status = JobStatus.New,
				Priority = 1,
				CreatedAt = DateTime.UtcNow.AddMinutes(-2)
			});
		await dbContext.SaveChangesAsync();

		using var serviceProvider = CreateScopedServiceProvider();
		var queueManager = new JobQueueManager(
			serviceProvider.GetRequiredService<IServiceScopeFactory>(),
			NullLogger<JobQueueManager>.Instance);

		var pendingJobs = await queueManager.GetPendingJobsAsync(10);

		Assert.Equal(2, pendingJobs.Count);
		Assert.Equal(2, pendingJobs.Select(job => job.ProjectId).Distinct().Count());
		Assert.Contains(pendingJobs, job => job.ProjectId == firstProject.Id && job.GoalPrompt == "First project first queued job");
		Assert.DoesNotContain(pendingJobs, job => job.ProjectId == firstProject.Id && job.GoalPrompt == "First project second queued job");
	}

	[Fact]
	public async Task HandleJobCompletionAsync_Success_DeletesIdeaAndAttachmentFiles()
	{
		await using var dbContext = CreateDbContext();
		var workingPath = Path.Combine(Path.GetTempPath(), $"vibeswarm-job-cleanup-{Guid.NewGuid():N}");
		Directory.CreateDirectory(workingPath);

		try
		{
			var project = new Project
			{
				Id = Guid.NewGuid(),
				Name = "Cleanup Project",
				WorkingPath = workingPath
			};
			var provider = new Provider
			{
				Id = Guid.NewGuid(),
				Name = "Copilot",
				Type = ProviderType.Copilot,
				IsEnabled = true,
				IsDefault = true
			};
			var job = new Job
			{
				Id = Guid.NewGuid(),
				ProjectId = project.Id,
				ProviderId = provider.Id,
				GoalPrompt = "Complete job with idea attachment cleanup",
				Status = JobStatus.Completed
			};
			var relativeAttachmentPath = Path.Combine(".vibeswarm", "idea-attachments", "cleanup", "context.txt");
			var idea = new Idea
			{
				Id = Guid.NewGuid(),
				ProjectId = project.Id,
				Description = "Cleanup attachments on success",
				JobId = job.Id,
				IsProcessing = true,
				SortOrder = 0,
				Attachments =
				[
					new IdeaAttachment
					{
						Id = Guid.NewGuid(),
						FileName = "context.txt",
						ContentType = "text/plain",
						RelativePath = relativeAttachmentPath,
						SizeBytes = 5
					}
				]
			};

			dbContext.Projects.Add(project);
			dbContext.Providers.Add(provider);
			dbContext.Jobs.Add(job);
			dbContext.Ideas.Add(idea);
			await dbContext.SaveChangesAsync();

			var attachmentPath = Path.Combine(workingPath, relativeAttachmentPath);
			Directory.CreateDirectory(Path.GetDirectoryName(attachmentPath)!);
			await File.WriteAllTextAsync(attachmentPath, "hello");

			var ideaService = CreateIdeaService(dbContext, provider);
			var handled = await ideaService.HandleJobCompletionAsync(job.Id, success: true);

			Assert.True(handled);
			Assert.False(File.Exists(attachmentPath));
			Assert.False(await dbContext.Ideas.AnyAsync(item => item.Id == idea.Id));
			Assert.False(await dbContext.IdeaAttachments.AnyAsync(item => item.IdeaId == idea.Id));
		}
		finally
		{
			if (Directory.Exists(workingPath))
			{
				Directory.Delete(workingPath, recursive: true);
			}
		}
	}

	[Fact]
	public async Task HandleJobCompletionAsync_Success_StopsIdeasProcessingWhenLastIdeaCompletes()
	{
		await using var dbContext = CreateDbContext();
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Stop Processing Project",
			WorkingPath = "/tmp/stop-processing-project",
			IdeasProcessingActive = true,
			IdeasAutoCommit = true
		};
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Copilot",
			Type = ProviderType.Copilot,
			IsEnabled = true,
			IsDefault = true
		};
		var job = new Job
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			ProviderId = provider.Id,
			GoalPrompt = "Complete the final queued idea",
			Status = JobStatus.Completed
		};
		var idea = new Idea
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			Description = "Final queued idea",
			JobId = job.Id,
			IsProcessing = true,
			SortOrder = 0
		};

		dbContext.Projects.Add(project);
		dbContext.Providers.Add(provider);
		dbContext.Jobs.Add(job);
		dbContext.Ideas.Add(idea);
		await dbContext.SaveChangesAsync();

		var jobUpdateService = new FakeJobUpdateService();
		var ideaService = CreateIdeaService(dbContext, provider, jobUpdateService: jobUpdateService);
		var handled = await ideaService.HandleJobCompletionAsync(job.Id, success: true);

		Assert.True(handled);
		var refreshedProject = await dbContext.Projects.SingleAsync(item => item.Id == project.Id);
		Assert.False(refreshedProject.IdeasProcessingActive);
		Assert.True(refreshedProject.IdeasAutoCommit);
		Assert.Null(refreshedProject.IdeasProcessingProviderId);
		Assert.Null(refreshedProject.IdeasProcessingModelId);
		Assert.Contains(jobUpdateService.IdeasProcessingStateChanges, change => change.ProjectId == project.Id && !change.IsActive);
	}

	[Fact]
	public async Task HandleJobCompletionAsync_Failure_KeepsIdeaAndAttachmentFiles()
	{
		await using var dbContext = CreateDbContext();
		var workingPath = Path.Combine(Path.GetTempPath(), $"vibeswarm-job-retain-{Guid.NewGuid():N}");
		Directory.CreateDirectory(workingPath);

		try
		{
			var project = new Project
			{
				Id = Guid.NewGuid(),
				Name = "Retention Project",
				WorkingPath = workingPath
			};
			var provider = new Provider
			{
				Id = Guid.NewGuid(),
				Name = "Copilot",
				Type = ProviderType.Copilot,
				IsEnabled = true,
				IsDefault = true
			};
			var job = new Job
			{
				Id = Guid.NewGuid(),
				ProjectId = project.Id,
				ProviderId = provider.Id,
				GoalPrompt = "Fail job but keep idea attachment",
				Status = JobStatus.Failed
			};
			var relativeAttachmentPath = Path.Combine(".vibeswarm", "idea-attachments", "retain", "context.txt");
			var idea = new Idea
			{
				Id = Guid.NewGuid(),
				ProjectId = project.Id,
				Description = "Keep attachments after a failed job",
				JobId = job.Id,
				IsProcessing = true,
				SortOrder = 0,
				Attachments =
				[
					new IdeaAttachment
					{
						Id = Guid.NewGuid(),
						FileName = "context.txt",
						ContentType = "text/plain",
						RelativePath = relativeAttachmentPath,
						SizeBytes = 5
					}
				]
			};

			dbContext.Projects.Add(project);
			dbContext.Providers.Add(provider);
			dbContext.Jobs.Add(job);
			dbContext.Ideas.Add(idea);
			await dbContext.SaveChangesAsync();

			var attachmentPath = Path.Combine(workingPath, relativeAttachmentPath);
			Directory.CreateDirectory(Path.GetDirectoryName(attachmentPath)!);
			await File.WriteAllTextAsync(attachmentPath, "hello");

			var ideaService = CreateIdeaService(dbContext, provider);
			var handled = await ideaService.HandleJobCompletionAsync(job.Id, success: false);

			Assert.True(handled);
			Assert.True(File.Exists(attachmentPath));

			var retainedIdea = await dbContext.Ideas.Include(item => item.Attachments).SingleAsync(item => item.Id == idea.Id);
			Assert.False(retainedIdea.IsProcessing);
			Assert.Null(retainedIdea.JobId);
			Assert.Single(retainedIdea.Attachments);
		}
		finally
		{
			if (Directory.Exists(workingPath))
			{
				Directory.Delete(workingPath, recursive: true);
			}
		}
	}

	[Fact]
	public async Task JobQueueManager_GetPendingJobsAsync_SkipsJobsWithFutureNotBeforeUtc()
	{
		await using var dbContext = CreateDbContext();
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Backoff Project",
			WorkingPath = "/tmp/backoff-project"
		};
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Claude",
			Type = ProviderType.Claude,
			IsEnabled = true,
			IsDefault = true
		};
		var secondProvider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Copilot",
			Type = ProviderType.Copilot,
			IsEnabled = true
		};

		var readyJobId = Guid.NewGuid();
		var backedOffJobId = Guid.NewGuid();
		var expiredBackoffJobId = Guid.NewGuid();

		dbContext.Projects.Add(project);
		dbContext.Providers.AddRange(provider, secondProvider);
		dbContext.Jobs.AddRange(
			new Job
			{
				Id = readyJobId,
				ProjectId = project.Id,
				ProviderId = provider.Id,
				GoalPrompt = "Ready job",
				Status = JobStatus.New,
				CreatedAt = DateTime.UtcNow.AddMinutes(-3),
				NotBeforeUtc = null
			},
			new Job
			{
				Id = backedOffJobId,
				ProjectId = project.Id,
				ProviderId = secondProvider.Id,
				GoalPrompt = "Backed off job",
				Status = JobStatus.New,
				CreatedAt = DateTime.UtcNow.AddMinutes(-2),
				NotBeforeUtc = DateTime.UtcNow.AddHours(1) // still in backoff
			},
			new Job
			{
				Id = expiredBackoffJobId,
				ProjectId = project.Id,
				ProviderId = secondProvider.Id,
				GoalPrompt = "Expired backoff job",
				Status = JobStatus.New,
				CreatedAt = DateTime.UtcNow.AddMinutes(-1),
				NotBeforeUtc = DateTime.UtcNow.AddMinutes(-5) // backoff expired
			});
		await dbContext.SaveChangesAsync();

		using var serviceProvider = CreateScopedServiceProvider();
		var queueManager = new JobQueueManager(
			serviceProvider.GetRequiredService<IServiceScopeFactory>(),
			NullLogger<JobQueueManager>.Instance);

		// Both readyJob and expiredBackoffJob are eligible because they do not share the same
		// provider slot; backedOffJob is still excluded by its future NotBeforeUtc value.
		queueManager.MaxJobsPerProject = 10;
		var pendingJobs = await queueManager.GetPendingJobsAsync(10);

		Assert.DoesNotContain(pendingJobs, j => j.Id == backedOffJobId);
		Assert.Contains(pendingJobs, j => j.Id == readyJobId);
		Assert.Contains(pendingJobs, j => j.Id == expiredBackoffJobId);
	}

	[Fact]
	public async Task GetPagedByProjectIdAsync_ReturnsRequestedJobPageAndActiveCount()
	{
		await using var dbContext = CreateDbContext();
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Paged Jobs",
			WorkingPath = "/tmp/paged-jobs"
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

		var statuses = new[]
		{
			JobStatus.Completed,
			JobStatus.Failed,
			JobStatus.Paused,
			JobStatus.Processing,
			JobStatus.New
		};

		for (var index = 0; index < statuses.Length; index++)
		{
			dbContext.Jobs.Add(new Job
			{
				Id = Guid.NewGuid(),
				ProjectId = project.Id,
				ProviderId = provider.Id,
				GoalPrompt = $"Job {index + 1}",
				Title = $"Job {index + 1}",
				Status = statuses[index],
				CreatedAt = DateTime.UtcNow.AddMinutes(index)
			});
		}

		await dbContext.SaveChangesAsync();

		var serviceProvider = new ServiceCollection().BuildServiceProvider();
		var jobService = new JobService(dbContext, serviceProvider);

		var page = await jobService.GetPagedByProjectIdAsync(project.Id, page: 2, pageSize: 2);

		Assert.Equal(2, page.PageNumber);
		Assert.Equal(5, page.TotalCount);
		Assert.Equal(3, page.ActiveCount);
		Assert.Equal(1, page.CompletedCount);
		Assert.Equal(new[] { "Job 3", "Job 2" }, page.Items.Select(job => job.Title).ToArray());
	}

	[Fact]
	public async Task CreateAsync_UsesProviderDefaultModel_WhenProjectHasNoPreferredModel()
	{
		await using var dbContext = CreateDbContext();
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Provider Default Models",
			WorkingPath = "/tmp/provider-default-models"
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
		dbContext.ProviderModels.AddRange(
			new ProviderModel
			{
				Id = Guid.NewGuid(),
				ProviderId = provider.Id,
				ModelId = "gpt-5.4",
				DisplayName = "GPT-5.4",
				IsAvailable = true,
				IsDefault = true
			},
			new ProviderModel
			{
				Id = Guid.NewGuid(),
				ProviderId = provider.Id,
				ModelId = "gpt-5-mini",
				DisplayName = "GPT-5 mini",
				IsAvailable = true,
				IsDefault = false
			});
		await dbContext.SaveChangesAsync();

		var serviceProvider = new ServiceCollection().BuildServiceProvider();
		var jobService = new JobService(dbContext, serviceProvider);
		var job = await jobService.CreateAsync(new Job
		{
			ProjectId = project.Id,
			ProviderId = provider.Id,
			GoalPrompt = "Use the provider default model"
		});

		Assert.Equal("gpt-5.4", job.ModelUsed);
	}

	[Fact]
	public async Task DeleteCompletedByProjectIdAsync_RemovesOnlyCompletedJobsForProject()
	{
		await using var dbContext = CreateDbContext();
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Cleanup Jobs",
			WorkingPath = "/tmp/cleanup-jobs"
		};
		var otherProject = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Other Project",
			WorkingPath = "/tmp/other-project"
		};
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Copilot",
			Type = ProviderType.Copilot,
			IsEnabled = true,
			IsDefault = true
		};

		dbContext.Projects.AddRange(project, otherProject);
		dbContext.Providers.Add(provider);
		dbContext.Jobs.AddRange(
			new Job
			{
				Id = Guid.NewGuid(),
				ProjectId = project.Id,
				ProviderId = provider.Id,
				GoalPrompt = "Completed job 1",
				Status = JobStatus.Completed,
				CreatedAt = DateTime.UtcNow.AddMinutes(-3)
			},
			new Job
			{
				Id = Guid.NewGuid(),
				ProjectId = project.Id,
				ProviderId = provider.Id,
				GoalPrompt = "Completed job 2",
				Status = JobStatus.Completed,
				CreatedAt = DateTime.UtcNow.AddMinutes(-2)
			},
			new Job
			{
				Id = Guid.NewGuid(),
				ProjectId = project.Id,
				ProviderId = provider.Id,
				GoalPrompt = "Processing job",
				Status = JobStatus.Processing,
				CreatedAt = DateTime.UtcNow.AddMinutes(-1)
			},
			new Job
			{
				Id = Guid.NewGuid(),
				ProjectId = otherProject.Id,
				ProviderId = provider.Id,
				GoalPrompt = "Other project completed job",
				Status = JobStatus.Completed,
				CreatedAt = DateTime.UtcNow
			});
		await dbContext.SaveChangesAsync();

		var serviceProvider = new ServiceCollection().BuildServiceProvider();
		var jobService = new JobService(dbContext, serviceProvider);

		var deletedCount = await jobService.DeleteCompletedByProjectIdAsync(project.Id);

		Assert.Equal(2, deletedCount);
		Assert.Equal(2, await dbContext.Jobs.CountAsync());
		Assert.DoesNotContain(await dbContext.Jobs.Where(j => j.ProjectId == project.Id).ToListAsync(), job => job.Status == JobStatus.Completed);
		Assert.Single(await dbContext.Jobs.Where(j => j.ProjectId == otherProject.Id && j.Status == JobStatus.Completed).ToListAsync());
	}

	[Fact]
	public async Task GetPagedAsync_FiltersJobsServerSideAndReturnsProjectSummaries()
	{
		await using var dbContext = CreateDbContext();
		var firstProject = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Project One",
			WorkingPath = "/tmp/project-one"
		};
		var secondProject = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Project Two",
			WorkingPath = "/tmp/project-two"
		};
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Claude",
			Type = ProviderType.Claude,
			IsEnabled = true,
			IsDefault = true
		};

		dbContext.Projects.AddRange(firstProject, secondProject);
		dbContext.Providers.Add(provider);
		dbContext.Jobs.AddRange(
			new Job
			{
				Id = Guid.NewGuid(),
				ProjectId = firstProject.Id,
				ProviderId = provider.Id,
				GoalPrompt = "Completed job",
				Title = "Completed job",
				Status = JobStatus.Completed,
				CreatedAt = DateTime.UtcNow.AddMinutes(-3),
				InputTokens = 100,
				OutputTokens = 50,
				TotalCostUsd = 1.25m
			},
			new Job
			{
				Id = Guid.NewGuid(),
				ProjectId = firstProject.Id,
				ProviderId = provider.Id,
				GoalPrompt = "Queued job",
				Title = "Queued job",
				Status = JobStatus.New,
				CreatedAt = DateTime.UtcNow.AddMinutes(-2)
			},
			new Job
			{
				Id = Guid.NewGuid(),
				ProjectId = secondProject.Id,
				ProviderId = provider.Id,
				GoalPrompt = "Failed job",
				Title = "Failed job",
				Status = JobStatus.Failed,
				CreatedAt = DateTime.UtcNow.AddMinutes(-1)
			});

		await dbContext.SaveChangesAsync();

		var serviceProvider = new ServiceCollection().BuildServiceProvider();
		var jobService = new JobService(dbContext, serviceProvider);

		var result = await jobService.GetPagedAsync(statusFilter: "completed", page: 1, pageSize: 10);

		var completedJob = Assert.Single(result.Items);
		Assert.Equal("Completed job", completedJob.Title);
		Assert.Equal(1, result.TotalCount);
		Assert.Equal(100, result.TotalInputTokens);
		Assert.Equal(50, result.TotalOutputTokens);
		Assert.Equal(1.25m, result.TotalCostUsd);
		Assert.Equal(2, result.ProjectCounts.Count);
		Assert.Equal(1, result.ProjectCounts.Single(summary => summary.ProjectId == firstProject.Id).ActiveCount);
	}

	[Fact]
	public async Task CreateAsync_TruncatesDerivedTitleToEntityLimit()
	{
		await using var dbContext = CreateDbContext();
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Manual Job Project",
			WorkingPath = "/tmp/manual-job"
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
		await dbContext.SaveChangesAsync();

		var jobService = new JobService(dbContext, new ServiceCollection().BuildServiceProvider());
		var longPrompt = new string('A', 240);

		var createdJob = await jobService.CreateAsync(new Job
		{
			ProjectId = project.Id,
			ProviderId = provider.Id,
			GoalPrompt = longPrompt,
			Title = longPrompt
		});

		Assert.NotNull(createdJob.Title);
		Assert.Equal(200, createdJob.Title!.Length);
		Assert.EndsWith("...", createdJob.Title);
	}

	[Fact]
	public async Task UpdateGitDiffAsync_UpdatesChangedFilesCountFromDiff()
	{
		await using var dbContext = CreateDbContext();
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Git Diff Project",
			WorkingPath = "/tmp/git-diff-project"
		};
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "OpenCode",
			Type = ProviderType.OpenCode,
			IsEnabled = true,
			IsDefault = true
		};
		var job = new Job
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			ProviderId = provider.Id,
			GoalPrompt = "Check git diff state",
			Status = JobStatus.Completed
		};

		dbContext.Projects.Add(project);
		dbContext.Providers.Add(provider);
		dbContext.Jobs.Add(job);
		await dbContext.SaveChangesAsync();

		var jobService = new JobService(dbContext, new ServiceCollection().BuildServiceProvider());
		var gitDiff = """
			diff --git a/src/FileOne.cs b/src/FileOne.cs
			--- a/src/FileOne.cs
			+++ b/src/FileOne.cs
			@@ -1 +1 @@
			-old
			+new
			diff --git a/src/FileTwo.cs b/src/FileTwo.cs
			--- a/src/FileTwo.cs
			+++ b/src/FileTwo.cs
			@@ -1 +1 @@
			-old
			+new
			""";

		var updated = await jobService.UpdateGitDiffAsync(job.Id, gitDiff);

		Assert.True(updated);
		var storedJob = await dbContext.Jobs.SingleAsync(j => j.Id == job.Id);
		Assert.Equal(2, storedJob.ChangedFilesCount);
		Assert.Equal(gitDiff, storedJob.GitDiff);
	}

	[Fact]
	public async Task ProjectService_UpdateAsync_PersistsPlanningSettings()
	{
		await using var dbContext = CreateDbContext();
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Claude Planner",
			Type = ProviderType.Claude,
			IsEnabled = true
		};
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Planning Project",
			WorkingPath = "/tmp/planning-project"
		};
		var model = new ProviderModel
		{
			Id = Guid.NewGuid(),
			ProviderId = provider.Id,
			ModelId = "claude-sonnet-4.6",
			DisplayName = "Claude Sonnet 4.6",
			IsAvailable = true,
			IsDefault = true
		};

		dbContext.Providers.Add(provider);
		dbContext.ProviderModels.Add(model);
		dbContext.Projects.Add(project);
		await dbContext.SaveChangesAsync();

		var projectService = new ProjectService(dbContext, new NoOpProjectEnvironmentCredentialService(), new FakeVersionControlService());
		var updated = await projectService.UpdateAsync(new Project
		{
			Id = project.Id,
			Name = project.Name,
			WorkingPath = project.WorkingPath,
			PlanningEnabled = true,
			PlanningProviderId = provider.Id,
			PlanningModelId = model.ModelId
		});

		Assert.True(updated.PlanningEnabled);
		Assert.Equal(provider.Id, updated.PlanningProviderId);
		Assert.Equal(model.ModelId, updated.PlanningModelId);

		var stored = await dbContext.Projects.AsNoTracking().SingleAsync(p => p.Id == project.Id);
		Assert.True(stored.PlanningEnabled);
		Assert.Equal(provider.Id, stored.PlanningProviderId);
		Assert.Equal(model.ModelId, stored.PlanningModelId);
	}

	[Fact]
	public async Task ProjectService_CreateAsync_RejectsUnsupportedPlanningProvider()
	{
		await using var dbContext = CreateDbContext();
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "OpenCode Planner",
			Type = ProviderType.OpenCode,
			IsEnabled = true
		};

		dbContext.Providers.Add(provider);
		await dbContext.SaveChangesAsync();

		var projectService = new ProjectService(dbContext, new NoOpProjectEnvironmentCredentialService(), new FakeVersionControlService());

		var error = await Assert.ThrowsAsync<InvalidOperationException>(() => projectService.CreateAsync(new Project
		{
			Name = "Unsupported Planning",
			WorkingPath = "/tmp/unsupported-planning",
			PlanningEnabled = true,
			PlanningProviderId = provider.Id
		}));

		Assert.Contains("supports only Claude and GitHub Copilot", error.Message);
	}

	[Fact]
	public async Task ExpandIdeaAsync_UsesProjectPlanningProviderAndModel()
	{
		await using var dbContext = CreateDbContext();
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Claude Planner",
			Type = ProviderType.Claude,
			IsEnabled = true,
			IsDefault = true
		};
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Planned Ideas Project",
			WorkingPath = "/tmp/planned-ideas",
			PlanningEnabled = true,
			PlanningProviderId = provider.Id,
			PlanningModelId = "claude-sonnet-4.6",
			PlanningReasoningEffort = "high"
		};
		var idea = new Idea
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			Description = "Add a planning settings card to the project page",
			SortOrder = 0
		};
		var providerInstance = new FakeProviderInstance
		{
			ExecutionResult = new ExecutionResult
			{
				Success = true,
				ModelUsed = "claude-sonnet-4.6",
				Messages =
				[
					new ExecutionMessage
					{
						Role = "plan",
						Content = "Overview: Add project planning controls.\nAcceptance Criteria: Users can configure planning.",
					}
				]
			}
		};

		dbContext.Projects.Add(project);
		dbContext.Providers.Add(provider);
		dbContext.Ideas.Add(idea);
		await dbContext.SaveChangesAsync();

		var ideaService = CreateIdeaService(dbContext, provider, providerInstance);
		var result = await ideaService.ExpandIdeaAsync(idea.Id);

		Assert.NotNull(result);
		Assert.Equal(IdeaExpansionStatus.PendingReview, result!.ExpansionStatus);
		Assert.Contains("Overview: Add project planning controls.", result.ExpandedDescription);
		Assert.NotNull(providerInstance.LastExecutePrompt);
		Assert.DoesNotContain("/plan", providerInstance.LastExecutePrompt!, StringComparison.Ordinal);
		Assert.StartsWith("Explore the codebase and create an implementation-ready plan", providerInstance.LastExecutePrompt!, StringComparison.Ordinal);
		Assert.Equal(project.PlanningModelId, providerInstance.LastExecutionOptions?.Model);
		Assert.Equal(project.PlanningReasoningEffort, providerInstance.LastExecutionOptions?.ReasoningEffort);
		Assert.NotNull(providerInstance.LastExecutionOptions?.DisallowedTools);
		Assert.Contains("Bash", providerInstance.LastExecutionOptions!.DisallowedTools!);
		Assert.Contains("Edit", providerInstance.LastExecutionOptions.DisallowedTools!);
		Assert.Contains("Write", providerInstance.LastExecutionOptions.DisallowedTools!);
	}

	[Fact]
	public async Task ExpandIdeaAsync_UsesConfiguredIdeaExpansionPromptTemplate()
	{
		await using var dbContext = CreateDbContext();
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Copilot",
			Type = ProviderType.Copilot,
			IsEnabled = true,
			IsDefault = true
		};
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Idea Prompt Project",
			WorkingPath = "/tmp/idea-prompt-project"
		};
		var idea = new Idea
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			Description = "Ship a settings editor for prompt templates",
			SortOrder = 0
		};
		var inferenceService = new FakeInferenceService
		{
			Response = new InferenceResponse
			{
				Success = true,
				Response = "Overview: Add settings editor.\nAcceptance Criteria: Users can save templates."
			}
		};

		dbContext.Providers.Add(provider);
		dbContext.Projects.Add(project);
		dbContext.Ideas.Add(idea);
		dbContext.AppSettings.Add(new AppSettings
		{
			Id = Guid.NewGuid(),
			TimeZoneId = "UTC",
			IdeaExpansionPromptTemplate = "Explore first:\n{{idea}}\nThen produce a spec."
		});
		await dbContext.SaveChangesAsync();

		var ideaService = CreateIdeaService(dbContext, provider, inferenceService: inferenceService);
		var result = await ideaService.ExpandIdeaAsync(idea.Id, new IdeaExpansionRequest { UseInference = true });

		Assert.NotNull(result);
		Assert.NotNull(inferenceService.LastRequest);
		Assert.Contains("Explore first:", inferenceService.LastRequest!.Prompt);
		Assert.Contains(idea.Description, inferenceService.LastRequest.Prompt);
		Assert.DoesNotContain("implementation-ready engineering specification", inferenceService.LastRequest.Prompt);
	}

	[Fact]
	public async Task ConvertToJobAsync_UsesConfiguredIdeaImplementationPromptTemplate()
	{
		await using var dbContext = CreateDbContext();
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Copilot",
			Type = ProviderType.Copilot,
			IsEnabled = true,
			IsDefault = true
		};
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Idea Prompt Project",
			WorkingPath = "/tmp/idea-job-project"
		};
		var idea = new Idea
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			Description = "Implement settings-driven idea prompts",
			SortOrder = 0
		};

		dbContext.Providers.Add(provider);
		dbContext.Projects.Add(project);
		dbContext.Ideas.Add(idea);
		dbContext.AppSettings.Add(new AppSettings
		{
			Id = Guid.NewGuid(),
			TimeZoneId = "UTC",
			IdeaImplementationPromptTemplate = "Inspect the repo before coding.\nIdea:\n{{idea}}"
		});
		await dbContext.SaveChangesAsync();

		var ideaService = CreateIdeaService(dbContext, provider);
		var job = await ideaService.ConvertToJobAsync(idea.Id);

		Assert.NotNull(job);
		Assert.Contains("Inspect the repo before coding.", job!.GoalPrompt);
		Assert.Contains(idea.Description, job.GoalPrompt);
		Assert.DoesNotContain("Work directly from the idea below", job.GoalPrompt);
	}

	[Fact]
	public async Task ConvertToJobAsync_UsesConfiguredApprovedIdeaImplementationPromptTemplate()
	{
		await using var dbContext = CreateDbContext();
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Copilot",
			Type = ProviderType.Copilot,
			IsEnabled = true,
			IsDefault = true
		};
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Approved Idea Prompt Project",
			WorkingPath = "/tmp/approved-idea-job-project"
		};
		var idea = new Idea
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			Description = "Add prompt template settings",
			ExpandedDescription = "Persist app-level templates and render them in Settings.",
			ExpansionStatus = IdeaExpansionStatus.Approved,
			ExpandedAt = DateTime.UtcNow,
			SortOrder = 0
		};

		dbContext.Providers.Add(provider);
		dbContext.Projects.Add(project);
		dbContext.Ideas.Add(idea);
		dbContext.AppSettings.Add(new AppSettings
		{
			Id = Guid.NewGuid(),
			TimeZoneId = "UTC",
			ApprovedIdeaImplementationPromptTemplate = "Original:\n{{idea}}\nSpecification:\n{{specification}}\nShip it."
		});
		await dbContext.SaveChangesAsync();

		var ideaService = CreateIdeaService(dbContext, provider);
		var job = await ideaService.ConvertToJobAsync(idea.Id);

		Assert.NotNull(job);
		Assert.Contains("Original:", job!.GoalPrompt);
		Assert.Contains(idea.Description, job.GoalPrompt);
		Assert.Contains(idea.ExpandedDescription, job.GoalPrompt);
		Assert.Contains("Ship it.", job.GoalPrompt);
		Assert.DoesNotContain("This specification was reviewed and approved", job.GoalPrompt);
	}

	private VibeSwarmDbContext CreateDbContext()
	{
		return new VibeSwarmDbContext(_dbOptions);
	}

	private ServiceProvider CreateScopedServiceProvider()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddDbContext<VibeSwarmDbContext>(options => options.UseSqlite(_connection));
		return services.BuildServiceProvider();
	}

	private static IEnumerable<Idea> CreateIdeaRange(Guid projectId, string prefix, int count, DateTime createdAt)
	{
		return Enumerable.Range(0, count).Select(index => new Idea
		{
			Id = Guid.NewGuid(),
			ProjectId = projectId,
			Description = $"{prefix} idea {index + 1}",
			SortOrder = index,
			CreatedAt = createdAt.AddSeconds(index)
		});
	}

	private static IdeaService CreateIdeaService(
		VibeSwarmDbContext dbContext,
		Provider provider,
		IProvider? providerInstance = null,
		IEnumerable<ProviderModel>? models = null,
		IInferenceService? inferenceService = null,
		IJobUpdateService? jobUpdateService = null)
	{
		var serviceProvider = new ServiceCollection().BuildServiceProvider();
		var jobService = new JobService(dbContext, serviceProvider);
		var providerService = new FakeProviderService(provider, providerInstance, models);
		var versionControlService = new FakeVersionControlService();
		var projectMemoryService = new FakeProjectMemoryService();

		return new IdeaService(
			dbContext,
			jobService,
			providerService,
			versionControlService,
			projectMemoryService,
			NullLogger<IdeaService>.Instance,
			inferenceService,
			jobUpdateService);
	}

	public void Dispose()
	{
		_connection.Dispose();
	}

	private sealed class FakeProviderService(
		Provider provider,
		IProvider? providerInstance = null,
		IEnumerable<ProviderModel>? models = null) : IProviderService
	{
		private readonly Provider _provider = provider;
		private readonly IProvider? _providerInstance = providerInstance;
		private readonly IReadOnlyList<ProviderModel> _models = models?.ToList() ?? [];

		public Task<IEnumerable<Provider>> GetAllAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult<IEnumerable<Provider>>([_provider]);

		public Task<Provider?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
			=> Task.FromResult(id == _provider.Id ? _provider : null);

		public Task<Provider?> GetDefaultAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult<Provider?>(_provider);

		public IProvider? CreateInstance(Provider config) => config.Id == _provider.Id ? _providerInstance : null;
		public Task<Provider> CreateAsync(Provider provider, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Provider> UpdateAsync(Provider provider, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> TestConnectionAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<ConnectionTestResult> TestConnectionWithDetailsAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task SetEnabledAsync(Guid id, bool isEnabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task SetDefaultAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<SessionSummary> GetSessionSummaryAsync(Guid providerId, string? sessionId, string? workingDirectory = null, string? fallbackOutput = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<ProviderModel>> GetModelsAsync(Guid providerId, CancellationToken cancellationToken = default)
			=> Task.FromResult<IEnumerable<ProviderModel>>(providerId == _provider.Id ? _models : []);
		public Task<IEnumerable<ProviderModel>> RefreshModelsAsync(Guid providerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task SetDefaultModelAsync(Guid providerId, Guid modelId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<CliUpdateResult> UpdateCliAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class FakeProviderInstance : IProvider
	{
		public Guid Id { get; init; } = Guid.NewGuid();
		public string Name { get; init; } = "Fake Planner";
		public ProviderType Type { get; init; } = ProviderType.Claude;
		public ProviderConnectionMode ConnectionMode { get; init; } = ProviderConnectionMode.CLI;
		public bool IsConnected { get; } = true;
		public string? LastConnectionError { get; }
		public ExecutionResult ExecutionResult { get; set; } = new() { Success = true };
		public PromptResponse PromptResponse { get; set; } = PromptResponse.Ok("Default fake response");
		public string? LastExecutePrompt { get; private set; }
		public ExecutionOptions? LastExecutionOptions { get; private set; }

		public Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
		public Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);

		public Task<ExecutionResult> ExecuteWithSessionAsync(
			string prompt,
			string? sessionId = null,
			string? workingDirectory = null,
			IProgress<ExecutionProgress>? progress = null,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public Task<ExecutionResult> ExecuteWithOptionsAsync(
			string prompt,
			ExecutionOptions options,
			IProgress<ExecutionProgress>? progress = null,
			CancellationToken cancellationToken = default)
		{
			LastExecutePrompt = prompt;
			LastExecutionOptions = options;
			return Task.FromResult(ExecutionResult);
		}

		public Task<ProviderInfo> GetProviderInfoAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<UsageLimits> GetUsageLimitsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<SessionSummary> GetSessionSummaryAsync(string? sessionId, string? workingDirectory = null, string? fallbackOutput = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<PromptResponse> GetPromptResponseAsync(string prompt, string? workingDirectory = null, CancellationToken cancellationToken = default) => Task.FromResult(PromptResponse);
		public Task<CliUpdateResult> UpdateCliAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class FakeVersionControlService : IVersionControlService
	{
		public Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> IsGitRepositoryAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<string?> GetCurrentCommitHashAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<string?> GetRemoteUrlAsync(string workingDirectory, string remoteName = "origin", CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> HasUncommittedChangesAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IReadOnlyList<string>> GetChangedFilesAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<string?> GetWorkingDirectoryDiffAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<string?> GetCommitRangeDiffAsync(string workingDirectory, string fromCommit, string? toCommit = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitDiffSummary?> GetDiffSummaryAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> CommitAllChangesAsync(string workingDirectory, string commitMessage, CancellationToken cancellationToken = default, GitCommitOptions? commitOptions = null) => throw new NotSupportedException();
		public Task<GitOperationResult> PushAsync(string workingDirectory, string remoteName = "origin", string? branchName = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> CommitAndPushAsync(string workingDirectory, string commitMessage, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> CreatePullRequestAsync(string workingDirectory, string sourceBranch, string targetBranch, string title, string? body = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> PreviewMergeBranchAsync(string workingDirectory, string sourceBranch, string targetBranch, string remoteName = "origin", CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> MergeBranchAsync(string workingDirectory, string sourceBranch, string targetBranch, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default, bool pushAfterMerge = true, IReadOnlyList<MergeConflictResolution>? conflictResolutions = null) => throw new NotSupportedException();
		public Task<IReadOnlyList<GitBranchInfo>> GetBranchesAsync(string workingDirectory, bool includeRemote = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> FetchAsync(string workingDirectory, string remoteName = "origin", bool prune = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> HardCheckoutBranchAsync(string workingDirectory, string branchName, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> SyncWithOriginAsync(string workingDirectory, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> CloneRepositoryAsync(string repositoryUrl, string targetDirectory, string? branch = null, Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public string GetGitHubCloneUrl(string ownerAndRepo, bool useSsh = true) => throw new NotSupportedException();
		public string? ExtractGitHubRepository(string? remoteUrl) => throw new NotSupportedException();
		public Task<GitWorkingTreeStatus> GetWorkingTreeStatusAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> PreserveChangesAsync(string workingDirectory, string message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> CreateBranchAsync(string workingDirectory, string branchName, bool switchToBranch = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> DiscardAllChangesAsync(string workingDirectory, bool includeUntracked = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IReadOnlyList<string>> GetCommitLogAsync(string workingDirectory, string fromCommit, string? toCommit = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> InitializeRepositoryAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> IsGitHubCliAvailableAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> IsGitHubCliAuthenticatedAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> CreateGitHubRepositoryAsync(string workingDirectory, string repositoryName, string? description = null, bool isPrivate = false, Action<string>? progressCallback = null, CancellationToken cancellationToken = default, string? gitignoreTemplate = null, string? licenseTemplate = null, bool initializeReadme = false) => throw new NotSupportedException();
		public Task<GitOperationResult> AddRemoteAsync(string workingDirectory, string remoteName, string remoteUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IReadOnlyDictionary<string, string>> GetRemotesAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> CloneWithGitHubCliAsync(string ownerRepo, string targetDirectory, Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> PruneRemoteBranchesAsync(string workingDirectory, string remoteName = "origin", CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class NoOpProjectEnvironmentCredentialService : IProjectEnvironmentCredentialService
	{
		public void PrepareForStorage(Project project, IReadOnlyCollection<ProjectEnvironment>? existingEnvironments = null) { }
		public void PopulateForEditing(Project? project) { }
		public void PopulateForExecution(Project? project) { }
		public Dictionary<string, string>? BuildJobEnvironmentVariables(Project? project) => null;
	}

	private sealed class FakeProjectMemoryService : IProjectMemoryService
	{
		public Task<string?> PrepareMemoryFileAsync(Project? project, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public Task SyncMemoryFromFileAsync(Guid projectId, string? memoryFilePath, CancellationToken cancellationToken = default) => Task.CompletedTask;
		public Task EnsureGitExcludeAsync(string workingPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
	}

	private sealed class FakeInferenceService : IInferenceService
	{
		public InferenceHealthResult Health { get; set; } = new() { IsAvailable = true };
		public InferenceResponse Response { get; set; } = new() { Success = true };
		public InferenceRequest? LastRequest { get; private set; }

		public Task<InferenceHealthResult> CheckHealthAsync(string? endpoint = null, InferenceProviderType? providerType = null, CancellationToken ct = default)
		{
			return Task.FromResult(new InferenceHealthResult
			{
				IsAvailable = Health.IsAvailable,
				Version = Health.Version,
				Error = Health.Error,
				DiscoveredModels = Health.DiscoveredModels
			});
		}

		public Task<List<DiscoveredModel>> GetAvailableModelsAsync(string? endpoint = null, InferenceProviderType? providerType = null, CancellationToken ct = default)
			=> Task.FromResult(new List<DiscoveredModel>());

		public Task<InferenceResponse> GenerateAsync(InferenceRequest request, CancellationToken ct = default)
		{
			LastRequest = request;
			return Task.FromResult(Response);
		}

		public Task<InferenceResponse> GenerateForTaskAsync(string taskType, string prompt, string? systemPrompt = null, CancellationToken ct = default)
		{
			LastRequest = new InferenceRequest
			{
				TaskType = taskType,
				Prompt = prompt,
				SystemPrompt = systemPrompt
			};
			return Task.FromResult(Response);
		}
	}

	private sealed class FakeJobUpdateService : IJobUpdateService
	{
		public List<(Guid IdeaId, Guid ProjectId)> IdeaUpdatedNotifications { get; } = [];
		public List<(Guid IdeaId, Guid ProjectId, Guid JobId)> IdeaStartedNotifications { get; } = [];
		public List<(Guid ProjectId, bool IsActive)> IdeasProcessingStateChanges { get; } = [];

		public Task NotifyJobStatusChanged(Guid jobId, string status) => Task.CompletedTask;
		public Task NotifyJobActivity(Guid jobId, string activity, DateTime timestamp) => Task.CompletedTask;
		public Task NotifyJobMessageAdded(Guid jobId) => Task.CompletedTask;
		public Task NotifyJobCompleted(Guid jobId, bool success, string? errorMessage = null) => Task.CompletedTask;
		public Task NotifyJobListChanged() => Task.CompletedTask;
		public Task NotifyJobCreated(Guid jobId, Guid projectId) => Task.CompletedTask;
		public Task NotifyJobDeleted(Guid jobId, Guid projectId) => Task.CompletedTask;
		public Task NotifyJobHeartbeat(Guid jobId, DateTime timestamp) => Task.CompletedTask;
		public Task NotifyJobOutput(Guid jobId, string line, bool isError, DateTime timestamp) => Task.CompletedTask;
		public Task NotifyProcessStarted(Guid jobId, int processId, string command) => Task.CompletedTask;
		public Task NotifyProcessExited(Guid jobId, int processId, int exitCode, TimeSpan duration) => Task.CompletedTask;
		public Task NotifyJobGitDiffUpdated(Guid jobId, bool hasChanges) => Task.CompletedTask;
		public Task NotifyJobInteractionRequired(Guid jobId, string prompt, string interactionType, List<string>? choices = null, string? defaultResponse = null) => Task.CompletedTask;
		public Task NotifyJobResumed(Guid jobId) => Task.CompletedTask;
		public Task NotifyJobCycleProgress(Guid jobId, int currentCycle, int maxCycles) => Task.CompletedTask;
		public Task NotifyIdeasProcessingStateChanged(Guid projectId, bool isActive)
		{
			IdeasProcessingStateChanges.Add((projectId, isActive));
			return Task.CompletedTask;
		}
		public Task NotifyIdeaCreated(Guid ideaId, Guid projectId) => Task.CompletedTask;
		public Task NotifyIdeaDeleted(Guid ideaId, Guid projectId) => Task.CompletedTask;
		public Task NotifyProviderUsageWarning(Guid providerId, string providerName, int percentUsed, string message, bool isExhausted, DateTime? resetTime) => Task.CompletedTask;
		public Task NotifyProviderRateLimited(Guid providerId, string providerName, string message, DateTime? resetTime) => Task.CompletedTask;
		public Task NotifyAutoPilotStateChanged(Guid projectId, VibeSwarm.Shared.Data.IterationLoop loop) => Task.CompletedTask;
		public Task NotifyDeveloperUpdateStatusChanged(VibeSwarm.Shared.Models.DeveloperModeStatus status) => Task.CompletedTask;
		public Task NotifyDeveloperUpdateOutputAdded(VibeSwarm.Shared.Models.DeveloperUpdateOutputLine line) => Task.CompletedTask;

		public Task NotifyIdeaStarted(Guid ideaId, Guid projectId, Guid jobId)
		{
			IdeaStartedNotifications.Add((ideaId, projectId, jobId));
			return Task.CompletedTask;
		}

		public Task NotifyIdeaUpdated(Guid ideaId, Guid projectId)
		{
			IdeaUpdatedNotifications.Add((ideaId, projectId));
			return Task.CompletedTask;
		}
	}
}
