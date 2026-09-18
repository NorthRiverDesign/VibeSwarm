using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.VersionControl;
using VibeSwarm.Shared.VersionControl.Models;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class JobExecutionSafetyTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;

	public JobExecutionSafetyTests()
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
	public void GetCompletionCriteria_UsesFifteenMinuteDefaultAndProviderOverride()
	{
		var provider = new Provider
		{
			StallTimeoutSeconds = 900
		};

		var defaultJob = new Job
		{
			GoalPrompt = "Default timeout",
			Provider = new Provider()
		};

		var providerOverrideJob = new Job
		{
			GoalPrompt = "Provider timeout",
			Provider = provider
		};

		var jobOverride = new Job
		{
			GoalPrompt = "Job timeout",
			Provider = provider,
			StallTimeoutSeconds = 120
		};

		Assert.Equal(TimeSpan.FromMinutes(15), defaultJob.GetCompletionCriteria().StallTimeout);
		Assert.Equal(TimeSpan.FromMinutes(15), providerOverrideJob.GetCompletionCriteria().StallTimeout);
		Assert.Equal(TimeSpan.FromMinutes(2), jobOverride.GetCompletionCriteria().StallTimeout);
	}

	[Fact]
	public async Task ClaimJobAsync_AllowsOnlyTheFirstClaim()
	{
		var projectId = Guid.NewGuid();
		var providerId = Guid.NewGuid();
		var jobId = Guid.NewGuid();

		await using (var setupContext = CreateDbContext())
		{
			setupContext.Projects.Add(new Project
			{
				Id = projectId,
				Name = "Execution Safety Project",
				WorkingPath = "/tmp/execution-safety-project"
			});
			setupContext.Providers.Add(new Provider
			{
				Id = providerId,
				Name = "Copilot",
				Type = ProviderType.Copilot,
				IsEnabled = true
			});
			setupContext.Jobs.Add(new Job
			{
				Id = jobId,
				ProjectId = projectId,
				ProviderId = providerId,
				GoalPrompt = "Run only once",
				Status = JobStatus.New
			});

			await setupContext.SaveChangesAsync();
		}

		var serviceProvider = new ServiceCollection().BuildServiceProvider();
		var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
		var processingService = new JobProcessingService(
			scopeFactory,
			NullLogger<JobProcessingService>.Instance,
			new NoOpVersionControlService(),
			projectEnvironmentCredentialService: new NoOpProjectEnvironmentCredentialService());

		await using var firstContext = CreateDbContext();
		await using var secondContext = CreateDbContext();

		var firstClaim = await InvokeClaimJobAsync(processingService, jobId, firstContext);
		var secondClaim = await InvokeClaimJobAsync(processingService, jobId, secondContext);

		Assert.True(firstClaim);
		Assert.False(secondClaim);

		await using var verificationContext = CreateDbContext();
		var job = await verificationContext.Jobs.SingleAsync(j => j.Id == jobId);

		Assert.Equal(JobStatus.Pending, job.Status);
		Assert.False(string.IsNullOrWhiteSpace(job.WorkerInstanceId));
		Assert.NotNull(job.StartedAt);
		Assert.NotNull(job.LastHeartbeatAt);
	}

	[Fact]
	public async Task ProcessJobAsync_PersistsCancelledStatus_WhenJobIsCancelledBeforeClaim()
	{
		var projectId = Guid.NewGuid();
		var providerId = Guid.NewGuid();
		var jobId = Guid.NewGuid();

		await using (var setupContext = CreateDbContext())
		{
			setupContext.Projects.Add(new Project
			{
				Id = projectId,
				Name = "Cancelled Before Start Project",
				WorkingPath = "/tmp/cancelled-before-start-project"
			});
			setupContext.Providers.Add(new Provider
			{
				Id = providerId,
				Name = "Copilot",
				Type = ProviderType.Copilot,
				IsEnabled = true
			});
			setupContext.Jobs.Add(new Job
			{
				Id = jobId,
				ProjectId = projectId,
				ProviderId = providerId,
				GoalPrompt = "Do not start",
				Status = JobStatus.New,
				CancellationRequested = true
			});

			await setupContext.SaveChangesAsync();
		}

		var services = new ServiceCollection();
		services.AddDbContext<VibeSwarmDbContext>(options => options.UseSqlite(_connection));
		var serviceProvider = services.BuildServiceProvider();
		var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

		var processingService = new JobProcessingService(
			scopeFactory,
			NullLogger<JobProcessingService>.Instance,
			new NoOpVersionControlService(),
			projectEnvironmentCredentialService: new NoOpProjectEnvironmentCredentialService());

		await using var executionContext = CreateDbContext();
		var job = await executionContext.Jobs
			.Include(j => j.Project)
			.Include(j => j.Provider)
			.SingleAsync(j => j.Id == jobId);

		await InvokeProcessJobAsync(
			processingService,
			job,
			new StubJobService(isCancellationRequested: true),
			new StubProviderService(),
			executionContext);

		await using var verificationContext = CreateDbContext();
		var persistedJob = await verificationContext.Jobs.SingleAsync(j => j.Id == jobId);

		Assert.Equal(JobStatus.Cancelled, persistedJob.Status);
		Assert.NotNull(persistedJob.CompletedAt);
		Assert.Equal("Cancelled before start", persistedJob.ErrorMessage);
		Assert.Null(persistedJob.WorkerInstanceId);
	}

	[Fact]
	public void ClaudeToolExecution_UsesExtendedWatchdogThreshold()
	{
		var serviceProvider = new ServiceCollection().BuildServiceProvider();
		var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
		var watchdogService = new JobWatchdogService(
			scopeFactory,
			NullLogger<JobWatchdogService>.Instance,
			new NoOpVersionControlService());

		var standardClaudeJob = new Job
		{
			GoalPrompt = "Standard Claude job",
			Provider = new Provider
			{
				Type = ProviderType.Claude,
				ConnectionMode = ProviderConnectionMode.CLI
			}
		};

		var longRunningToolJob = new Job
		{
			GoalPrompt = "Install dependencies",
			CurrentActivity = "Running tool: bash",
			Provider = new Provider
			{
				Type = ProviderType.Claude,
				ConnectionMode = ProviderConnectionMode.CLI
			}
		};

		Assert.Equal(TimeSpan.FromMinutes(15), InvokeEffectiveStallThreshold(watchdogService, standardClaudeJob));
		Assert.Equal(TimeSpan.FromMinutes(30), InvokeEffectiveStallThreshold(watchdogService, longRunningToolJob));
	}

	[Fact]
	public void JobRecoveryHelper_CapturesAndClearsRecoveryState()
	{
		var job = new Job
		{
			GoalPrompt = "Implement recovery",
			SessionId = "session-123"
		};

		JobRecoveryHelper.CaptureRecoveryState(
			job,
			JobStatus.Processing,
			"Continue where you left off",
			job.SessionId,
			new string('x', JobRecoveryHelper.MaxRecoveryConsoleOutputLength + 100));

		Assert.Equal(JobStatus.Processing, job.ResumeFromStatus);
		Assert.Equal("Continue where you left off", job.RecoveryPrompt);
		Assert.NotNull(job.RecoveryCheckpointAt);
		Assert.NotNull(job.ConsoleOutput);
		Assert.Equal(JobRecoveryHelper.MaxRecoveryConsoleOutputLength, job.ConsoleOutput!.Length);

		JobRecoveryHelper.ClearRecoveryState(job);

		Assert.Null(job.ResumeFromStatus);
		Assert.Null(job.RecoveryPrompt);
		Assert.Null(job.RecoveryCheckpointAt);
		Assert.False(job.ForceFreshSession);
		Assert.Equal("session-123", job.SessionId);
	}

	[Fact]
	public async Task ResolveProviderForExecutionAsync_RequeuesCoolingProviderWithoutTouchingPreflight()
	{
		var projectId = Guid.NewGuid();
		var providerId = Guid.NewGuid();
		var jobId = Guid.NewGuid();

		await using (var setupContext = CreateDbContext())
		{
			setupContext.Projects.Add(new Project
			{
				Id = projectId,
				Name = "Cooldown Project",
				WorkingPath = "/tmp/cooldown-project"
			});
			setupContext.Providers.Add(new Provider
			{
				Id = providerId,
				Name = "Cooling Copilot",
				Type = ProviderType.Copilot,
				IsEnabled = true,
				ExecutablePath = "missing-copilot"
			});
			setupContext.ProviderUsageSummaries.Add(new ProviderUsageSummary
			{
				ProviderId = providerId,
				NextExecutionAvailableAt = DateTime.UtcNow.AddMinutes(2),
				LastUpdatedAt = DateTime.UtcNow
			});
			setupContext.Jobs.Add(new Job
			{
				Id = jobId,
				ProjectId = projectId,
				ProviderId = providerId,
				GoalPrompt = "Wait for provider cooldown",
				Status = JobStatus.New
			});

			await setupContext.SaveChangesAsync();
		}

		var services = new ServiceCollection();
		services.AddDbContext<VibeSwarmDbContext>(options => options.UseSqlite(_connection));
		var serviceProvider = services.BuildServiceProvider();
		var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

		var processingService = new JobProcessingService(
			scopeFactory,
			NullLogger<JobProcessingService>.Instance,
			new NoOpVersionControlService(),
			projectEnvironmentCredentialService: new NoOpProjectEnvironmentCredentialService());

		await using var executionContext = CreateDbContext();
		var job = await executionContext.Jobs
			.Include(j => j.Project)
			.Include(j => j.Provider)
			.SingleAsync(j => j.Id == jobId);

		await InvokeProcessJobAsync(
			processingService,
			job,
			new StubJobService(isCancellationRequested: false),
			new StubProviderService(),
			executionContext);

		await using var verificationContext = CreateDbContext();
		var persistedJob = await verificationContext.Jobs.SingleAsync(j => j.Id == jobId);

		Assert.Equal(JobStatus.New, persistedJob.Status);
		Assert.NotNull(persistedJob.NotBeforeUtc);
		Assert.Contains("cooldown active", persistedJob.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ResolveProviderForExecutionAsync_SwitchesToHealthyFallbackProvider()
	{
		var projectId = Guid.NewGuid();
		var coolingProviderId = Guid.NewGuid();
		var fallbackProviderId = Guid.NewGuid();
		var jobId = Guid.NewGuid();

		await using (var setupContext = CreateDbContext())
		{
			setupContext.Projects.Add(new Project
			{
				Id = projectId,
				Name = "Fallback Project",
				WorkingPath = "/tmp/fallback-project"
			});
			setupContext.Providers.AddRange(
				new Provider
				{
					Id = coolingProviderId,
					Name = "Cooling Copilot",
					Type = ProviderType.Copilot,
					IsEnabled = true
				},
				new Provider
				{
					Id = fallbackProviderId,
					Name = "Healthy Claude",
					Type = ProviderType.Claude,
					IsEnabled = true
				});
			setupContext.ProjectProviders.AddRange(
				new ProjectProvider
				{
					ProjectId = projectId,
					ProviderId = coolingProviderId,
					Priority = 1,
					IsEnabled = true
				},
				new ProjectProvider
				{
					ProjectId = projectId,
					ProviderId = fallbackProviderId,
					Priority = 2,
					IsEnabled = true
				});
			setupContext.ProviderUsageSummaries.Add(new ProviderUsageSummary
			{
				ProviderId = coolingProviderId,
				NextExecutionAvailableAt = DateTime.UtcNow.AddMinutes(2),
				LastUpdatedAt = DateTime.UtcNow
			});
			setupContext.Jobs.Add(new Job
			{
				Id = jobId,
				ProjectId = projectId,
				ProviderId = coolingProviderId,
				GoalPrompt = "Use the healthy provider",
				Status = JobStatus.New
			});

			await setupContext.SaveChangesAsync();
		}

		var services = new ServiceCollection();
		services.AddDbContext<VibeSwarmDbContext>(options => options.UseSqlite(_connection));
		var serviceProvider = services.BuildServiceProvider();
		var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
		var healthTracker = new ProviderHealthTracker();
		var queueManager = new JobQueueManager(scopeFactory, NullLogger<JobQueueManager>.Instance);
		var jobCoordinator = new JobCoordinatorService(scopeFactory, NullLogger<JobCoordinatorService>.Instance, healthTracker, queueManager);

		var processingService = new JobProcessingService(
			scopeFactory,
			NullLogger<JobProcessingService>.Instance,
			new NoOpVersionControlService(),
			jobCoordinator: jobCoordinator,
			healthTracker: healthTracker,
			projectEnvironmentCredentialService: new NoOpProjectEnvironmentCredentialService());

		await using var executionContext = CreateDbContext();
		var job = await executionContext.Jobs
			.Include(j => j.Project)
			.Include(j => j.Provider)
			.SingleAsync(j => j.Id == jobId);

		var (resolvedProvider, cooldownUntil) = await InvokeResolveProviderForExecutionAsync(processingService, job, executionContext);

		Assert.NotNull(resolvedProvider);
		Assert.Null(cooldownUntil);
		Assert.Equal(fallbackProviderId, resolvedProvider!.Id);
		Assert.Equal(fallbackProviderId, job.ProviderId);

		await using var verificationContext = CreateDbContext();
		var persistedJob = await verificationContext.Jobs.SingleAsync(j => j.Id == jobId);
		Assert.Equal(fallbackProviderId, persistedJob.ProviderId);
	}

	private VibeSwarmDbContext CreateDbContext() => new(_dbOptions);

	private static async Task<bool> InvokeClaimJobAsync(JobProcessingService service, Guid jobId, VibeSwarmDbContext dbContext)
	{
		var method = typeof(JobProcessingService).GetMethod("ClaimJobAsync", BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);

		var task = (Task<bool>)method.Invoke(service, [jobId, dbContext, CancellationToken.None])!;
		return await task;
	}

	private static async Task InvokeProcessJobAsync(
		JobProcessingService service,
		Job job,
		IJobService jobService,
		IProviderService providerService,
		VibeSwarmDbContext dbContext)
	{
		var contextType = typeof(JobProcessingService).GetNestedType("JobExecutionContext", BindingFlags.NonPublic);
		Assert.NotNull(contextType);

		var executionContext = Activator.CreateInstance(contextType!);
		Assert.NotNull(executionContext);

		var method = typeof(JobProcessingService).GetMethod("ProcessJobAsync", BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);

		var skillStorage = new SkillStorageService(dbContext, NullLogger<SkillStorageService>.Instance);
		var task = (Task)method.Invoke(service, [job, jobService, providerService, dbContext, skillStorage, executionContext!, CancellationToken.None])!;
		await task;
	}

	private static async Task<(Provider? Provider, DateTime? CooldownUntil)> InvokeResolveProviderForExecutionAsync(
		JobProcessingService service,
		Job job,
		VibeSwarmDbContext dbContext)
	{
		var method = typeof(JobProcessingService).GetMethod("ResolveProviderForExecutionAsync", BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);

		var task = (Task<(Provider? Provider, DateTime? CooldownUntil)>)method.Invoke(service, [job, dbContext, CancellationToken.None])!;
		return await task;
	}

	private static TimeSpan InvokeEffectiveStallThreshold(JobWatchdogService service, Job job)
	{
		var method = typeof(JobWatchdogService).GetMethod("GetEffectiveStallThreshold", BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);

		return (TimeSpan)method.Invoke(service, [job])!;
	}

	public void Dispose()
	{
		_connection.Dispose();
	}

	private sealed class NoOpProjectEnvironmentCredentialService : IProjectEnvironmentCredentialService
	{
		public void PrepareForStorage(Project project, IReadOnlyCollection<ProjectEnvironment>? existingEnvironments = null) { }
		public void PopulateForEditing(Project? project) { }
		public void PopulateForExecution(Project? project) { }
		public Dictionary<string, string>? BuildJobEnvironmentVariables(Project? project) => null;
	}

	private sealed class StubJobService : FakeJobServiceBase
	{
		private readonly bool _isCancellationRequested;

		public StubJobService(bool isCancellationRequested)
		{
			_isCancellationRequested = isCancellationRequested;
		}

		public override Task<bool> IsCancellationRequestedAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(_isCancellationRequested);
		public override Task<IEnumerable<JobChangeSet>> GetChangeSetsAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.FromResult(Enumerable.Empty<JobChangeSet>());
	}

	private sealed class StubProviderService : FakeProviderServiceBase
	{
	}

	private sealed class NoOpVersionControlService : FakeVersionControlServiceBase
	{
	}
}
