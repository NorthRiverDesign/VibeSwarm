using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Utilities;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class JobScheduleServiceTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;

	public JobScheduleServiceTests()
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
	public async Task CreateAsync_ComputesNextRunForDailySchedule()
	{
		await using var dbContext = CreateDbContext();
		var project = CreateProject();
		var provider = CreateProvider();
		dbContext.Projects.Add(project);
		dbContext.Providers.Add(provider);
		await dbContext.SaveChangesAsync();

		var service = new JobScheduleService(dbContext);
		var beforeCreate = DateTime.UtcNow;

		var created = await service.CreateAsync(new JobSchedule
		{
			ProjectId = project.Id,
			ProviderId = provider.Id,
			Prompt = "update dependencies",
			Frequency = JobScheduleFrequency.Daily,
			HourUtc = 9,
			MinuteUtc = 30
		});

		var expected = JobScheduleCalculator.CalculateNextRunUtc(created, beforeCreate);
		Assert.Equal(expected, created.NextRunAtUtc);
		Assert.True(created.IsEnabled);
	}

	[Fact]
	public async Task SetEnabledAsync_RecomputesFutureRunWhenResumingPausedSchedule()
	{
		await using var dbContext = CreateDbContext();
		var project = CreateProject();
		var provider = CreateProvider();
		dbContext.Projects.Add(project);
		dbContext.Providers.Add(provider);

		var schedule = new JobSchedule
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			ProviderId = provider.Id,
			Prompt = "run maintenance",
			Frequency = JobScheduleFrequency.Hourly,
			MinuteUtc = 15,
			IsEnabled = false,
			NextRunAtUtc = DateTime.UtcNow.AddHours(-4)
		};
		dbContext.JobSchedules.Add(schedule);
		await dbContext.SaveChangesAsync();

		var service = new JobScheduleService(dbContext);
		var resumed = await service.SetEnabledAsync(schedule.Id, true);

		Assert.True(resumed.IsEnabled);
		Assert.True(resumed.NextRunAtUtc > DateTime.UtcNow.AddMinutes(-1));
		Assert.Null(resumed.LastError);
	}

	[Fact]
	public async Task CreateAsync_UsesConfiguredTimezoneForDailySchedule()
	{
		await using var dbContext = CreateDbContext();
		var project = CreateProject();
		var provider = CreateProvider();
		var timeZoneId = DateTimeHelper.ResolveTimeZone("America/New_York").Id;
		dbContext.Projects.Add(project);
		dbContext.Providers.Add(provider);
		dbContext.AppSettings.Add(new AppSettings
		{
			Id = Guid.NewGuid(),
			TimeZoneId = timeZoneId,
			UpdatedAt = DateTime.UtcNow
		});
		await dbContext.SaveChangesAsync();

		var service = new JobScheduleService(dbContext);
		var beforeCreate = DateTime.UtcNow;
		var timeZone = DateTimeHelper.ResolveTimeZone(timeZoneId);

		var created = await service.CreateAsync(new JobSchedule
		{
			ProjectId = project.Id,
			ProviderId = provider.Id,
			Prompt = "run daily review",
			Frequency = JobScheduleFrequency.Daily,
			HourUtc = 9,
			MinuteUtc = 15
		});

		var expected = JobScheduleCalculator.CalculateNextRunUtc(created, beforeCreate, timeZone);
		Assert.Equal(expected, created.NextRunAtUtc);
	}

	[Fact]
	public async Task ProcessDueSchedulesAsync_CreatesOnlyOneJobPerScheduledRun()
	{
		await using var dbContext = CreateDbContext();
		var project = CreateProject();
		var provider = CreateProvider();
		dbContext.Projects.Add(project);
		dbContext.Providers.Add(provider);

		var dueTime = DateTime.UtcNow.AddMinutes(-2);
		var schedule = new JobSchedule
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			ProviderId = provider.Id,
			Prompt = "check dependencies",
			Frequency = JobScheduleFrequency.Hourly,
			MinuteUtc = dueTime.Minute,
			NextRunAtUtc = dueTime,
			IsEnabled = true
		};
		dbContext.JobSchedules.Add(schedule);
		await dbContext.SaveChangesAsync();

		var serviceProvider = new ServiceCollection().BuildServiceProvider();
		var jobService = new JobService(dbContext, serviceProvider);
		var processor = new JobScheduleProcessor(dbContext, jobService, new FakeIdeaService(), NullLogger<JobScheduleProcessor>.Instance);

		var firstCount = await processor.ProcessDueSchedulesAsync();
		var secondCount = await processor.ProcessDueSchedulesAsync();

		Assert.Equal(1, firstCount);
		Assert.Equal(0, secondCount);
		Assert.Equal(1, await dbContext.Jobs.CountAsync(job => job.JobScheduleId == schedule.Id));
		var savedJob = await dbContext.Jobs.SingleAsync(job => job.JobScheduleId == schedule.Id);
		Assert.True(savedJob.IsScheduled);
		Assert.Equal(dueTime, savedJob.ScheduledForUtc);
	}

	[Fact]
	public async Task CreateAsync_RejectsTeamRoleThatIsNotAssignedToProject()
	{
		await using var dbContext = CreateDbContext();
		var project = CreateProject();
		var provider = CreateProvider();
		var teamRole = CreateTeamRole();
		dbContext.Projects.Add(project);
		dbContext.Providers.Add(provider);
		dbContext.TeamRoles.Add(teamRole);
		await dbContext.SaveChangesAsync();

		var service = new JobScheduleService(dbContext);

		var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(new JobSchedule
		{
			ProjectId = project.Id,
			ExecutionTarget = JobScheduleExecutionTarget.TeamRole,
			TeamRoleId = teamRole.Id,
			Prompt = "review security findings",
			Frequency = JobScheduleFrequency.Daily,
			HourUtc = 9,
			MinuteUtc = 0
		}));

		Assert.Equal("The selected team member is not assigned to this project.", exception.Message);
	}

	[Fact]
	public async Task ProcessDueSchedulesAsync_CreatesTeamRoleJobFromProjectAssignmentWithoutSwarmFanout()
	{
		await using var dbContext = CreateDbContext();
		var project = CreateProject();
		project.EnableTeamSwarm = true;
		var primaryProvider = CreateProvider();
		var secondaryProvider = CreateProvider();
		var reviewerRole = CreateTeamRole("Security Reviewer");
		var backupRole = CreateTeamRole("Dependency Reviewer");
		dbContext.Projects.Add(project);
		dbContext.Providers.AddRange(primaryProvider, secondaryProvider);
		dbContext.TeamRoles.AddRange(reviewerRole, backupRole);
		dbContext.ProjectTeamRoles.AddRange(
			new ProjectTeamRole
			{
				ProjectId = project.Id,
				TeamRoleId = reviewerRole.Id,
				ProviderId = primaryProvider.Id,
				PreferredModelId = "copilot-reviewer",
				PreferredReasoningEffort = "high",
				IsEnabled = true
			},
			new ProjectTeamRole
			{
				ProjectId = project.Id,
				TeamRoleId = backupRole.Id,
				ProviderId = secondaryProvider.Id,
				PreferredModelId = "copilot-backup",
				PreferredReasoningEffort = "medium",
				IsEnabled = true
			});

		dbContext.ProviderModels.AddRange(
			new ProviderModel
			{
				Id = Guid.NewGuid(),
				ProviderId = primaryProvider.Id,
				ModelId = "copilot-reviewer",
				DisplayName = "Copilot Reviewer",
				IsAvailable = true,
				IsDefault = true,
				UpdatedAt = DateTime.UtcNow
			},
			new ProviderModel
			{
				Id = Guid.NewGuid(),
				ProviderId = secondaryProvider.Id,
				ModelId = "copilot-backup",
				DisplayName = "Copilot Backup",
				IsAvailable = true,
				IsDefault = true,
				UpdatedAt = DateTime.UtcNow
			});

		var dueTime = DateTime.UtcNow.AddMinutes(-2);
		dbContext.JobSchedules.Add(new JobSchedule
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			ExecutionTarget = JobScheduleExecutionTarget.TeamRole,
			TeamRoleId = reviewerRole.Id,
			Prompt = "review for security issues",
			Frequency = JobScheduleFrequency.Hourly,
			MinuteUtc = dueTime.Minute,
			NextRunAtUtc = dueTime,
			IsEnabled = true
		});
		await dbContext.SaveChangesAsync();

		var serviceProvider = new ServiceCollection().BuildServiceProvider();
		var jobService = new JobService(dbContext, serviceProvider);
		var processor = new JobScheduleProcessor(dbContext, jobService, new FakeIdeaService(), NullLogger<JobScheduleProcessor>.Instance);

		var createdCount = await processor.ProcessDueSchedulesAsync();

		Assert.Equal(1, createdCount);
		var jobs = await dbContext.Jobs.ToListAsync();
		Assert.Single(jobs);
		var job = jobs[0];
		Assert.True(job.IsScheduled);
		Assert.Equal(reviewerRole.Id, job.TeamRoleId);
		Assert.Equal(primaryProvider.Id, job.ProviderId);
		Assert.Equal("copilot-reviewer", job.ModelUsed);
		Assert.Equal("high", job.ReasoningEffort);
		Assert.Null(job.SwarmId);
	}

	[Fact]
	public async Task ProcessDueSchedulesAsync_GeneratesIdeasWithInferenceProvider()
	{
		await using var dbContext = CreateDbContext();
		var project = CreateProject();
		var inferenceProvider = new InferenceProvider
		{
			Id = Guid.NewGuid(),
			Name = "Local Ollama",
			Endpoint = "http://ollama:11434",
			IsEnabled = true
		};
		dbContext.Projects.Add(project);
		dbContext.InferenceProviders.Add(inferenceProvider);

		var dueTime = DateTime.UtcNow.AddMinutes(-2);
		var schedule = new JobSchedule
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			ScheduleType = JobScheduleType.GenerateIdeas,
			InferenceProviderId = inferenceProvider.Id,
			ModelId = "qwen2.5-coder:7b",
			IdeaCount = 2,
			Frequency = JobScheduleFrequency.Hourly,
			MinuteUtc = dueTime.Minute,
			NextRunAtUtc = dueTime,
			IsEnabled = true
		};
		dbContext.JobSchedules.Add(schedule);
		await dbContext.SaveChangesAsync();

		var serviceProvider = new ServiceCollection().BuildServiceProvider();
		var jobService = new JobService(dbContext, serviceProvider);
		var ideaService = new CapturingIdeaService();
		var processor = new JobScheduleProcessor(dbContext, jobService, ideaService, NullLogger<JobScheduleProcessor>.Instance);

		var createdCount = await processor.ProcessDueSchedulesAsync();

		Assert.Equal(1, createdCount);
		Assert.NotNull(ideaService.LastRequest);
		Assert.Equal(project.Id, ideaService.LastProjectId);
		Assert.True(ideaService.LastRequest!.UseInference);
		Assert.Equal(inferenceProvider.Id, ideaService.LastRequest.ProviderId);
		Assert.Equal("qwen2.5-coder:7b", ideaService.LastRequest.ModelId);
		Assert.Equal(2, ideaService.LastRequest.IdeaCount);
		Assert.Contains("scheduler", ideaService.LastRequest.AdditionalContext, StringComparison.OrdinalIgnoreCase);
		Assert.Empty(await dbContext.Jobs.ToListAsync());
		var refreshed = await dbContext.JobSchedules.SingleAsync(item => item.Id == schedule.Id);
		Assert.Equal(dueTime, refreshed.LastRunAtUtc);
		Assert.Null(refreshed.LastError);
	}

	[Fact]
	public async Task ProcessDueSchedulesAsync_StoresSuggestionFailureMessageForIdeaGeneration()
	{
		await using var dbContext = CreateDbContext();
		var project = CreateProject();
		var inferenceProvider = new InferenceProvider
		{
			Id = Guid.NewGuid(),
			Name = "Local Ollama",
			Endpoint = "http://ollama:11434",
			IsEnabled = true
		};
		dbContext.Projects.Add(project);
		dbContext.InferenceProviders.Add(inferenceProvider);

		var dueTime = DateTime.UtcNow.AddMinutes(-2);
		var schedule = new JobSchedule
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			ScheduleType = JobScheduleType.GenerateIdeas,
			InferenceProviderId = inferenceProvider.Id,
			IdeaCount = 1,
			Frequency = JobScheduleFrequency.Hourly,
			MinuteUtc = dueTime.Minute,
			NextRunAtUtc = dueTime,
			IsEnabled = true
		};
		dbContext.JobSchedules.Add(schedule);
		await dbContext.SaveChangesAsync();

		var serviceProvider = new ServiceCollection().BuildServiceProvider();
		var jobService = new JobService(dbContext, serviceProvider);
		var ideaService = new CapturingIdeaService
		{
			Result = new SuggestIdeasResult
			{
				Success = false,
				Stage = SuggestIdeasStage.GenerateFailed,
				Message = "Inference request failed."
			}
		};
		var processor = new JobScheduleProcessor(dbContext, jobService, ideaService, NullLogger<JobScheduleProcessor>.Instance);

		var createdCount = await processor.ProcessDueSchedulesAsync();

		Assert.Equal(0, createdCount);
		var refreshed = await dbContext.JobSchedules.SingleAsync(item => item.Id == schedule.Id);
		Assert.Equal("Inference request failed.", refreshed.LastError);
		Assert.Null(refreshed.LastRunAtUtc);
	}

	private VibeSwarmDbContext CreateDbContext() => new(_dbOptions);

	private static Project CreateProject() => new()
	{
		Id = Guid.NewGuid(),
		Name = $"Project-{Guid.NewGuid():N}",
		WorkingPath = "/tmp/scheduler-project"
	};

	private static Provider CreateProvider() => new()
	{
		Id = Guid.NewGuid(),
		Name = $"Provider-{Guid.NewGuid():N}",
		Type = ProviderType.Copilot,
		IsEnabled = true,
		IsDefault = true
	};

	private static TeamRole CreateTeamRole(string? name = null) => new()
	{
		Id = Guid.NewGuid(),
		Name = name ?? $"Role-{Guid.NewGuid():N}",
		IsEnabled = true
	};

	private class FakeIdeaService : IIdeaService
	{
		public Task<IEnumerable<Idea>> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<ProjectIdeasListResult> GetPagedByProjectIdAsync(Guid projectId, int page = 1, int pageSize = 10, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea> CreateAsync(Idea idea, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea> CreateAsync(CreateIdeaRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea> UpdateAsync(Idea idea, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea?> GetNextUnprocessedAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Job?> ConvertToJobAsync(Guid ideaId, IdeaProcessingOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> CompleteIdeaFromJobAsync(Guid jobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> HandleJobCompletionAsync(Guid jobId, bool success, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea?> GetByJobIdAsync(Guid jobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IdeaAttachment?> GetAttachmentAsync(Guid attachmentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task StartProcessingAsync(Guid projectId, IdeaProcessingOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task StopProcessingAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> IsProcessingActiveAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> ProcessNextIdeaIfReadyAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<Guid>> GetActiveProcessingProjectsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task RecoverStuckIdeasAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task ReorderIdeasAsync(Guid projectId, IEnumerable<Guid> ideaIdsInOrder, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea> CopyToProjectAsync(Guid ideaId, Guid targetProjectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea> MoveToProjectAsync(Guid ideaId, Guid targetProjectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea?> ExpandIdeaAsync(Guid ideaId, IdeaExpansionRequest? request = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea?> CancelExpansionAsync(Guid ideaId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea?> ApproveExpansionAsync(Guid ideaId, string? editedDescription = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea?> RejectExpansionAsync(Guid ideaId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GlobalIdeasProcessingStatus> GetGlobalProcessingStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task StartAllProcessingAsync(IdeaProcessingOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task StopAllProcessingAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public virtual Task<SuggestIdeasResult> SuggestIdeasFromCodebaseAsync(Guid projectId, SuggestIdeasRequest? request = null, CancellationToken cancellationToken = default)
			=> Task.FromResult(new SuggestIdeasResult { Success = true, Stage = SuggestIdeasStage.Success });
	}

	private sealed class CapturingIdeaService : FakeIdeaService
	{
		public Guid LastProjectId { get; private set; }
		public SuggestIdeasRequest? LastRequest { get; private set; }
		public SuggestIdeasResult Result { get; set; } = new() { Success = true, Stage = SuggestIdeasStage.Success };

		public override Task<SuggestIdeasResult> SuggestIdeasFromCodebaseAsync(Guid projectId, SuggestIdeasRequest? request = null, CancellationToken cancellationToken = default)
		{
			LastProjectId = projectId;
			LastRequest = request;
			return Task.FromResult(Result);
		}
	}

	public void Dispose()
	{
		_connection.Dispose();
	}
}
