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

	private sealed class StubJobService : IJobService
	{
		private readonly bool _isCancellationRequested;

		public StubJobService(bool isCancellationRequested)
		{
			_isCancellationRequested = isCancellationRequested;
		}

		public Task<bool> IsCancellationRequestedAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(_isCancellationRequested);
		public Task<IEnumerable<Job>> GetAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<JobsListResult> GetPagedAsync(Guid? projectId = null, string statusFilter = "all", int page = 1, int pageSize = 25, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<Job>> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<ProjectJobsListResult> GetPagedByProjectIdAsync(Guid projectId, int page = 1, int pageSize = 10, string? search = null, string statusFilter = "all", CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<Job>> GetPendingJobsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<JobSummary>> GetActiveJobsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Job?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Job?> GetByIdWithMessagesAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Job> CreateAsync(Job job, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Job> UpdateStatusAsync(Guid id, JobStatus status, string? output = null, string? errorMessage = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Job> UpdateJobResultAsync(Guid id, JobStatus status, string? sessionId, string? output, string? errorMessage, int? inputTokens, int? outputTokens, decimal? costUsd, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task AddMessageAsync(Guid jobId, JobMessage message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task AddMessagesAsync(Guid jobId, IEnumerable<JobMessage> messages, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> RequestCancellationAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> ForceCancelAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task UpdateProgressAsync(Guid id, string? currentActivity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> ResetJobAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> UpdateGitCommitHashAsync(Guid id, string commitHash, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> UpdateGitDiffAsync(Guid id, string? gitDiff, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> UpdateGitDeliveryAsync(Guid id, string? commitHash = null, int? pullRequestNumber = null, string? pullRequestUrl = null, DateTime? pullRequestCreatedAt = null, DateTime? mergedAt = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> PauseForInteractionAsync(Guid id, string interactionPrompt, string interactionType, string? choices = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<(string? Prompt, string? Type, string? Choices)?> GetPendingInteractionAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> ResumeJobAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> ContinueJobAsync(Guid id, string followUpPrompt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<Job>> GetPausedJobsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<string?> GetLastUsedModelAsync(Guid projectId, Guid providerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> ResetJobWithOptionsAsync(Guid id, Guid? providerId = null, string? modelId = null, string? reasoningEffort = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> UpdateJobPromptAsync(Guid id, string newPrompt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<int> CancelAllByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<int> DeleteCompletedByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<int> RetrySelectedByProjectIdAsync(Guid projectId, IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<int> CancelSelectedByProjectIdAsync(Guid projectId, IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<int> PrioritizeSelectedByProjectIdAsync(Guid projectId, IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> ForceFailJobAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task RefreshExecutionPlanAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<JobChangeSet>> GetChangeSetsAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.FromResult(Enumerable.Empty<JobChangeSet>());
	}

	private sealed class StubProviderService : IProviderService
	{
		public Task<IEnumerable<Provider>> GetAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Provider?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Provider?> GetDefaultAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public IProvider? CreateInstance(Provider config) => throw new NotSupportedException();
		public Task<Provider> CreateAsync(Provider provider, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Provider> UpdateAsync(Provider provider, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> TestConnectionAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<ConnectionTestResult> TestConnectionWithDetailsAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task SetEnabledAsync(Guid id, bool isEnabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task SetDefaultAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<SessionSummary> GetSessionSummaryAsync(Guid providerId, string? sessionId, string? workingDirectory = null, string? fallbackOutput = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<ProviderModel>> GetModelsAsync(Guid providerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<ProviderModel>> RefreshModelsAsync(Guid providerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task SetDefaultModelAsync(Guid providerId, Guid modelId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<CliUpdateResult> UpdateCliAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class NoOpVersionControlService : IVersionControlService
	{
		public Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> IsGitRepositoryAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<string?> GetCurrentCommitHashAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<string?> GetRemoteUrlAsync(string workingDirectory, string remoteName = "origin", CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> HasUncommittedChangesAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitWorkingTreeStatus> GetWorkingTreeStatusAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
		public Task<GitOperationResult> CreateBranchAsync(string workingDirectory, string branchName, bool switchToBranch = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> DiscardAllChangesAsync(string workingDirectory, bool includeUntracked = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> PreserveChangesAsync(string workingDirectory, string message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
}
