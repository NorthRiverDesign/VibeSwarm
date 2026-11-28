using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Inference;
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
			new string('x', 100) + "\n" + new string('y', JobRecoveryHelper.MaxRecoveryConsoleOutputLength - 1) + "\n");

		Assert.Equal(JobStatus.Processing, job.ResumeFromStatus);
		Assert.Equal("Continue where you left off", job.RecoveryPrompt);
		Assert.NotNull(job.RecoveryCheckpointAt);
		Assert.NotNull(job.ConsoleOutput);
		Assert.Equal(JobRecoveryHelper.MaxRecoveryConsoleOutputLength, job.ConsoleOutput!.Length);
		Assert.StartsWith("y", job.ConsoleOutput);

		JobRecoveryHelper.ClearRecoveryState(job);

		Assert.Null(job.ResumeFromStatus);
		Assert.Null(job.RecoveryPrompt);
		Assert.Null(job.RecoveryCheckpointAt);
		Assert.False(job.ForceFreshSession);
		Assert.Equal("session-123", job.SessionId);
	}

	[Fact]
	public void JobRecoveryHelper_CheckpointNeverStoresHalfAStreamJsonLine()
	{
		var olderLine = "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"" + new string('a', 40) + "\"}]}}";
		var longLine = "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"content\":\"" + new string('b', JobRecoveryHelper.MaxRecoveryConsoleOutputLength) + "\"}]}}";
		var recentLines = string.Concat(Enumerable.Repeat(olderLine + "\n", 300));
		var job = new Job { GoalPrompt = "Checkpoint output" };

		JobRecoveryHelper.CaptureRecoveryState(job, JobStatus.Processing, null, null, longLine + "\n" + recentLines);

		Assert.NotNull(job.ConsoleOutput);
		Assert.True(job.ConsoleOutput!.Length <= JobRecoveryHelper.MaxRecoveryConsoleOutputLength);
		Assert.All(
			job.ConsoleOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries),
			line => Assert.Equal(olderLine, line));

		var previousOutput = job.ConsoleOutput;
		JobRecoveryHelper.CaptureRecoveryState(job, JobStatus.Processing, null, null, recentLines + longLine + "\n");

		Assert.Equal(previousOutput, job.ConsoleOutput);
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

	[Fact]
	public async Task ProcessJobAsync_WaitsForTheSessionToResetWhenUsageIsOverThreshold()
	{
		var projectId = Guid.NewGuid();
		var providerId = Guid.NewGuid();
		var jobId = Guid.NewGuid();
		var sessionReset = DateTime.UtcNow.AddHours(2);

		await using (var setupContext = CreateDbContext())
		{
			setupContext.Projects.Add(new Project
			{
				Id = projectId,
				Name = "Session Project",
				WorkingPath = "/tmp/session-project"
			});
			setupContext.Providers.Add(new Provider
			{
				Id = providerId,
				Name = "Claude",
				Type = ProviderType.Claude,
				IsEnabled = true,
				ExecutablePath = "missing-claude"
			});
			setupContext.ProviderUsageSummaries.Add(new ProviderUsageSummary
			{
				ProviderId = providerId,
				LimitsRefreshedAt = DateTime.UtcNow,
				LimitWindows = [SessionWindow(93, sessionReset)]
			});
			setupContext.Jobs.Add(new Job
			{
				Id = jobId,
				ProjectId = projectId,
				ProviderId = providerId,
				GoalPrompt = "Wait for the session to reset",
				Status = JobStatus.New
			});

			await setupContext.SaveChangesAsync();
		}

		var services = new ServiceCollection();
		services.AddDbContext<VibeSwarmDbContext>(options => options.UseSqlite(_connection));
		var serviceProvider = services.BuildServiceProvider();

		var processingService = new JobProcessingService(
			serviceProvider.GetRequiredService<IServiceScopeFactory>(),
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
		Assert.Equal(sessionReset, persistedJob.NotBeforeUtc!.Value, TimeSpan.FromSeconds(1));
		Assert.Contains("session is 93% used", persistedJob.ErrorMessage ?? string.Empty);
	}

	[Fact]
	public async Task ProcessJobAsync_WaitsForTheWeekToResetWhenWeeklyUsageIsOverItsThreshold()
	{
		var projectId = Guid.NewGuid();
		var providerId = Guid.NewGuid();
		var jobId = Guid.NewGuid();
		var sessionReset = DateTime.UtcNow.AddHours(2);
		var weekReset = DateTime.UtcNow.AddDays(3);

		await using (var setupContext = CreateDbContext())
		{
			setupContext.Projects.Add(new Project
			{
				Id = projectId,
				Name = "Session Project",
				WorkingPath = "/tmp/session-project"
			});
			setupContext.Providers.Add(new Provider
			{
				Id = providerId,
				Name = "Claude",
				Type = ProviderType.Claude,
				IsEnabled = true,
				ExecutablePath = "missing-claude",
				WeeklyLimitPauseThresholdPercent = 80
			});
			setupContext.ProviderUsageSummaries.Add(new ProviderUsageSummary
			{
				ProviderId = providerId,
				LimitsRefreshedAt = DateTime.UtcNow,
				LimitWindows =
				[
					SessionWindow(10, sessionReset),
					new UsageLimitWindow { Scope = UsageLimitWindowScope.Weekly, LimitType = UsageLimitType.RateLimit, Label = "Weekly", CurrentUsage = 84, MaxUsage = 100, ResetTime = weekReset }
				]
			});
			setupContext.Jobs.Add(new Job
			{
				Id = jobId,
				ProjectId = projectId,
				ProviderId = providerId,
				GoalPrompt = "Wait for the week to reset",
				Status = JobStatus.New
			});

			await setupContext.SaveChangesAsync();
		}

		var services = new ServiceCollection();
		services.AddDbContext<VibeSwarmDbContext>(options => options.UseSqlite(_connection));
		var serviceProvider = services.BuildServiceProvider();

		var processingService = new JobProcessingService(
			serviceProvider.GetRequiredService<IServiceScopeFactory>(),
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
		Assert.Equal(weekReset, persistedJob.NotBeforeUtc!.Value, TimeSpan.FromSeconds(1));
		Assert.Contains("weekly limit is 84% used", persistedJob.ErrorMessage ?? string.Empty);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task ProcessJobAsync_StopsTheAgentAtItsTimeLimitAndDoesNotRequeueIt(bool throwsWhenCancelled)
	{
		var workingPath = Directory.CreateTempSubdirectory("vibeswarm-time-limit-").FullName;
		var jobId = Guid.NewGuid();
		Provider providerConfig;
		await using (var setupContext = CreateDbContext())
		{
			var project = new Project { Id = Guid.NewGuid(), Name = "Time Limit Project", WorkingPath = workingPath };
			providerConfig = new Provider { Id = Guid.NewGuid(), Name = "Claude", Type = ProviderType.Claude, IsEnabled = true };
			setupContext.AddRange(project, providerConfig, new Job
			{
				Id = jobId,
				ProjectId = project.Id,
				ProviderId = providerConfig.Id,
				GoalPrompt = "Work forever",
				Status = JobStatus.New
			});
			await setupContext.SaveChangesAsync();
		}

		var agent = new NeverFinishingProvider(providerConfig, throwsWhenCancelled);
		var services = new ServiceCollection();
		services.AddDbContext<VibeSwarmDbContext>(options => options.UseSqlite(_connection));
		services.AddSingleton<IProjectMemoryService, NoOpProjectMemoryService>();
		services.AddSingleton<IJobService>(new StubJobService(isCancellationRequested: false));
		services.AddSingleton<IProviderService>(new StubProviderService());
		var processingService = new JobProcessingService(
			services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
			NullLogger<JobProcessingService>.Instance,
			new NotARepositoryVersionControlService(),
			projectEnvironmentCredentialService: new NoOpProjectEnvironmentCredentialService())
		{
			TimeLimitOverride = TimeSpan.FromSeconds(3),
			ProviderFactoryOverride = _ => agent
		};

		try
		{
			await using var executionContext = CreateDbContext();
			var job = await executionContext.Jobs.Include(j => j.Project).Include(j => j.Provider).SingleAsync(j => j.Id == jobId);
			await InvokeProcessJobAsync(processingService, job, new StubJobService(isCancellationRequested: false), new StubProviderService(), executionContext, withJobCancellation: true)
				.WaitAsync(TimeSpan.FromSeconds(60));
		}
		finally
		{
			Directory.Delete(workingPath, recursive: true);
		}

		await using var verificationContext = CreateDbContext();
		var persistedJob = await verificationContext.Jobs.SingleAsync(j => j.Id == jobId);
		Assert.True(agent.WasStopped, $"{persistedJob.Status}: {persistedJob.ErrorMessage}");
		Assert.Equal(JobStatus.Failed, persistedJob.Status);
		Assert.True(JobTimeLimit.IsStopMessage(persistedJob.ErrorMessage), persistedJob.ErrorMessage);
		Assert.Null(persistedJob.ResumeFromStatus);
	}

	[Fact]
	public async Task ProcessJobAsync_InferenceAnswersTheAgentsQuestion_AndTheJobCarriesOn()
	{
		var workingPath = Directory.CreateTempSubdirectory("vibeswarm-question-").FullName;
		var jobId = Guid.NewGuid();
		Provider providerConfig;
		await using (var setupContext = CreateDbContext())
		{
			var project = new Project { Id = Guid.NewGuid(), Name = "Question Project", WorkingPath = workingPath };
			providerConfig = new Provider { Id = Guid.NewGuid(), Name = "Claude", Type = ProviderType.Claude, IsEnabled = true };
			var inferenceProvider = new InferenceProvider { Name = "Desktop", Endpoint = "http://desktop:11434" };
			inferenceProvider.Models.Add(new InferenceModel { ModelId = "qwen3", TaskType = "default", IsDefault = true });
			setupContext.AddRange(project, providerConfig, inferenceProvider, new Job
			{
				Id = jobId,
				ProjectId = project.Id,
				ProviderId = providerConfig.Id,
				GoalPrompt = "Add a settings page",
				Status = JobStatus.New
			});
			await setupContext.SaveChangesAsync();
		}

		var agent = new QuestionAskingProvider(providerConfig, "Would you like me to also update the docs?");
		var jobService = new InteractionJobService();
		var inference = new ReplyingInferenceService("Yes, update the docs too.");
		var services = new ServiceCollection();
		services.AddDbContext<VibeSwarmDbContext>(options => options.UseSqlite(_connection));
		services.AddSingleton<IProjectMemoryService, NoOpProjectMemoryService>();
		services.AddSingleton<IJobService>(jobService);
		services.AddSingleton<IProviderService>(new StubProviderService());
		services.AddSingleton<IInferenceService>(inference);
		services.AddLogging();
		services.AddScoped<AgentQuestionAnswerer>();
		var processingService = new JobProcessingService(
			services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
			NullLogger<JobProcessingService>.Instance,
			new NotARepositoryVersionControlService(),
			interactionResponseService: new InMemoryInteractionResponseService(),
			projectEnvironmentCredentialService: new NoOpProjectEnvironmentCredentialService())
		{
			ProviderFactoryOverride = _ => agent
		};

		try
		{
			await using var executionContext = CreateDbContext();
			var job = await executionContext.Jobs.Include(j => j.Project).Include(j => j.Provider).SingleAsync(j => j.Id == jobId);
			await InvokeProcessJobAsync(processingService, job, jobService, new StubProviderService(), executionContext, withJobCancellation: true)
				.WaitAsync(TimeSpan.FromSeconds(60));
		}
		finally
		{
			Directory.Delete(workingPath, recursive: true);
		}

		Assert.Equal(2, agent.Prompts.Count);
		Assert.Contains("Yes, update the docs too.", agent.Prompts[1]);
		Assert.Contains("without asking again", agent.Prompts[1]);
		Assert.True(jobService.Paused);
		Assert.True(jobService.Resumed);
		var reply = Assert.Single(jobService.Messages, m => m.Role == MessageRole.User);
		Assert.StartsWith("Yes, update the docs too.", reply.Content);
		Assert.Contains("Would you like me to also update the docs?", inference.LastRequest?.Prompt);
	}

	[Theory]
	[InlineData("Yes, update the docs too.", 2)]
	[InlineData("NO_FOLLOW_UP", 1)]
	public async Task ProcessJobAsync_InferenceAnswersTheQuestionAnAgentEndsWith_AndTheSessionContinues(string reply, int expectedRuns)
	{
		var workingPath = Directory.CreateTempSubdirectory("vibeswarm-closing-question-").FullName;
		var jobId = Guid.NewGuid();
		Provider providerConfig;
		await using (var setupContext = CreateDbContext())
		{
			var project = new Project { Id = Guid.NewGuid(), Name = "Closing Question Project", WorkingPath = workingPath };
			providerConfig = new Provider { Id = Guid.NewGuid(), Name = "Claude", Type = ProviderType.Claude, IsEnabled = true };
			var inferenceProvider = new InferenceProvider { Name = "Desktop", Endpoint = "http://desktop:11434" };
			inferenceProvider.Models.Add(new InferenceModel { ModelId = "qwen3", TaskType = "default", IsDefault = true });
			setupContext.AddRange(project, providerConfig, inferenceProvider, new Job
			{
				Id = jobId,
				ProjectId = project.Id,
				ProviderId = providerConfig.Id,
				GoalPrompt = "Add a settings page",
				Status = JobStatus.New
			});
			await setupContext.SaveChangesAsync();
		}

		var agent = new ClosingQuestionProvider(providerConfig, "Built the page.\n\nShould I also update the docs?");
		var jobService = new InteractionJobService();
		var services = new ServiceCollection();
		services.AddDbContext<VibeSwarmDbContext>(options => options.UseSqlite(_connection));
		services.AddSingleton<IProjectMemoryService, NoOpProjectMemoryService>();
		services.AddSingleton<IJobService>(jobService);
		services.AddSingleton<IProviderService>(new StubProviderService());
		services.AddSingleton<IInferenceService>(new ReplyingInferenceService(reply));
		services.AddLogging();
		services.AddScoped<AgentQuestionAnswerer>();
		var processingService = new JobProcessingService(
			services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
			NullLogger<JobProcessingService>.Instance,
			new NotARepositoryVersionControlService(),
			interactionResponseService: new InMemoryInteractionResponseService(),
			projectEnvironmentCredentialService: new NoOpProjectEnvironmentCredentialService())
		{
			ProviderFactoryOverride = _ => agent
		};

		try
		{
			await using var executionContext = CreateDbContext();
			var job = await executionContext.Jobs.Include(j => j.Project).Include(j => j.Provider).SingleAsync(j => j.Id == jobId);
			await InvokeProcessJobAsync(processingService, job, jobService, new StubProviderService(), executionContext, withJobCancellation: true)
				.WaitAsync(TimeSpan.FromSeconds(60));
		}
		finally
		{
			Directory.Delete(workingPath, recursive: true);
		}

		Assert.Equal(expectedRuns, agent.Runs.Count);
		if (expectedRuns == 2)
		{
			Assert.StartsWith("Yes, update the docs too.", agent.Runs[1].Prompt);
			Assert.Equal("session-1", agent.Runs[1].SessionId);
			Assert.Contains(jobService.Messages, m => m.Role == MessageRole.Assistant && m.Content.Contains("Should I also update the docs?"));
			Assert.Contains(jobService.Messages, m => m.Role == MessageRole.User && m.Content.StartsWith("Yes, update the docs too."));
		}
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task ProcessJobAsync_AMessageFromTheUserStopsTheTurn_AndTheSessionCarriesOnWithIt(bool throwsWhenCancelled)
	{
		var turnStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var agent = new ScriptedProvider(async (run, cancellationToken) =>
		{
			if (run > 1)
			{
				return new ExecutionResult { Success = true, SessionId = "session-1", Output = "Switched to tabs." };
			}

			turnStarted.TrySetResult();
			try
			{
				await Task.Delay(Timeout.Infinite, cancellationToken);
				return new ExecutionResult { Success = true };
			}
			catch (OperationCanceledException) when (!throwsWhenCancelled)
			{
				return new ExecutionResult { Success = false, ErrorMessage = "Execution was cancelled." };
			}
		});

		var (jobId, jobService, processingService) = await RunScriptedJobAsync(agent, async processing =>
		{
			Assert.False(processing.TrySendUserMessage(Guid.NewGuid(), "Not a running job"));
			await turnStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
			Assert.True(processing.TrySendUserMessage(processing.RunningJobId, "Use tabs, not spaces."));
		});

		Assert.Equal(2, agent.Runs.Count);
		Assert.Equal("session-1", agent.Runs[1].SessionId);
		Assert.StartsWith("<user_messages>", agent.Runs[1].Prompt);
		Assert.Contains("Your last step was stopped", agent.Runs[1].Prompt);
		Assert.Contains("Use tabs, not spaces.", agent.Runs[1].Prompt);
		Assert.Contains(jobService.Messages, m => m.Role == MessageRole.User && m.Content == "Use tabs, not spaces.");

		await using var verifyContext = CreateDbContext();
		Assert.Equal(JobStatus.Completed, (await verifyContext.Jobs.SingleAsync(j => j.Id == jobId)).Status);
		Assert.False(processingService.TrySendUserMessage(jobId, "Too late"));
		var finishedJobService = new JobService(verifyContext, new ServiceCollection().BuildServiceProvider(), jobProcessingService: processingService);
		Assert.False(await finishedJobService.SendMessageToAgentAsync(jobId, "Too late"));
		Assert.False(await finishedJobService.SendMessageToAgentAsync(jobId, "   "));
	}

	[Fact]
	public async Task ProcessJobAsync_GivesTheAgentTheProjectsEnvironments()
	{
		var workingPath = Directory.CreateTempSubdirectory("vibeswarm-environments-").FullName;
		var dataProtectionPath = Directory.CreateTempSubdirectory("vibeswarm-environments-keys-").FullName;
		var credentialService = new ProjectEnvironmentCredentialService(
			Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create(new DirectoryInfo(dataProtectionPath)));
		var jobId = Guid.NewGuid();
		await using (var setupContext = CreateDbContext())
		{
			var project = new Project
			{
				Id = Guid.NewGuid(),
				Name = "Shop",
				WorkingPath = workingPath,
				Environments =
				[
					new ProjectEnvironment
					{
						Id = Guid.NewGuid(),
						Name = "Prod DB",
						Type = EnvironmentType.Database,
						Stage = EnvironmentStage.Production,
						Url = "mysql://db.example.com:3306/shop",
						IsPrimary = true,
						Username = "readonly",
						Password = "DbSecret!"
					}
				]
			};
			credentialService.PrepareForStorage(project);
			var providerConfig = new Provider { Id = Guid.NewGuid(), Name = "Claude", Type = ProviderType.Claude, IsEnabled = true };
			setupContext.AddRange(project, providerConfig, new Job
			{
				Id = jobId,
				ProjectId = project.Id,
				ProviderId = providerConfig.Id,
				GoalPrompt = "Pull last week's orders",
				Status = JobStatus.New,
				SessionId = "session-1"
			});
			await setupContext.SaveChangesAsync();
		}

		var agent = new ScriptedProvider((_, _) => Task.FromResult(new ExecutionResult { Success = true, SessionId = "session-1", Output = "Done." }));
		var jobService = new InteractionJobService();
		var services = new ServiceCollection();
		services.AddDbContext<VibeSwarmDbContext>(options => options.UseSqlite(_connection));
		services.AddSingleton<IProjectMemoryService, NoOpProjectMemoryService>();
		services.AddSingleton<IJobService>(jobService);
		services.AddSingleton<IProviderService>(new StubProviderService());
		var processingService = new JobProcessingService(
			services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
			NullLogger<JobProcessingService>.Instance,
			new NotARepositoryVersionControlService(),
			interactionResponseService: new InMemoryInteractionResponseService(),
			projectEnvironmentCredentialService: credentialService)
		{
			ProviderFactoryOverride = _ => agent
		};

		try
		{
			// The queue loads the job, environments and all, in its own context; the run re-reads it in another.
			Job queuedJob;
			await using (var queueContext = CreateDbContext())
			{
				queuedJob = await queueContext.Jobs
					.Include(j => j.Project).ThenInclude(project => project!.Environments)
					.Include(j => j.Provider)
					.SingleAsync(j => j.Id == jobId);
			}

			await using var executionContext = CreateDbContext();
			await InvokeProcessJobAsync(processingService, queuedJob, jobService, new StubProviderService(), executionContext, withJobCancellation: true)
				.WaitAsync(TimeSpan.FromSeconds(60));
		}
		finally
		{
			Directory.Delete(workingPath, recursive: true);
			Directory.Delete(dataProtectionPath, recursive: true);
		}

		var (prompt, _) = Assert.Single(agent.Runs);
		Assert.Contains("<environments>", prompt);
		Assert.Contains("Database [Production]: Prod DB | URL: mysql://db.example.com:3306/shop | Env: APP_PROD_DB_URL, APP_PROD_DB_USERNAME, APP_PROD_DB_PASSWORD", prompt);
		Assert.DoesNotContain("DbSecret!", prompt);

		var variables = Assert.Single(agent.EnvironmentVariables);
		Assert.NotNull(variables);
		Assert.Equal("mysql://db.example.com:3306/shop", variables["APP_PROD_DB_URL"]);
		Assert.Equal("readonly", variables["APP_PROD_DB_USERNAME"]);
		Assert.Equal("DbSecret!", variables["APP_PROD_DB_PASSWORD"]);

		await using var verificationContext = CreateDbContext();
		var persistedJob = await verificationContext.Jobs.SingleAsync(j => j.Id == jobId);
		Assert.Equal(1, persistedJob.EnvironmentCount);
	}

	[Fact]
	public async Task ProcessJobAsync_AMessageSentAsTheAgentFinishes_KeepsTheSessionGoing()
	{
		JobProcessingService? processingService = null;
		Guid jobId = default;
		var agent = new ScriptedProvider((run, _) =>
		{
			if (run == 1)
			{
				// Arrives after the agent's last word, before the run wraps up.
				Assert.True(processingService!.TrySendUserMessage(jobId, "Also update the README."));
			}

			var text = run == 1 ? "Built the page." : "Updated the README.";
			return Task.FromResult(new ExecutionResult
			{
				Success = true,
				SessionId = "session-1",
				Output = text,
				Messages = [new ExecutionMessage { Role = "assistant", Content = text }]
			});
		});

		var (_, jobService, _) = await RunScriptedJobAsync(agent, processing =>
		{
			processingService = processing.Service;
			jobId = processing.RunningJobId;
			return Task.CompletedTask;
		}, startBeforeRunning: true);

		Assert.Equal(2, agent.Runs.Count);
		Assert.Equal("session-1", agent.Runs[1].SessionId);
		Assert.DoesNotContain("Your last step was stopped", agent.Runs[1].Prompt);
		Assert.Contains("Also update the README.", agent.Runs[1].Prompt);
		Assert.Contains(jobService.Messages, m => m.Role == MessageRole.Assistant && m.Content == "Built the page.");
		Assert.Contains(jobService.Messages, m => m.Role == MessageRole.User && m.Content == "Also update the README.");
	}

	// The three tests below pin what a plain job run does from its first turn to its last, so the
	// loop that carries them can be reshaped without changing it.
	[Fact]
	public async Task ProcessJobAsync_OneTurnThatSucceeds_CompletesTheJob()
	{
		var agent = new ScriptedProvider((_, _) => Task.FromResult(Reply("Built the page.")));

		var (jobId, jobService, _) = await RunScriptedJobAsync(agent, _ => Task.CompletedTask);

		var run = Assert.Single(agent.Runs);
		Assert.Contains("Add a settings page", run.Prompt);
		Assert.Equal("session-1", run.SessionId);
		Assert.Contains(jobService.Messages, m => m.Role == MessageRole.Assistant && m.Content == "Built the page.");
		await using var db = CreateDbContext();
		var job = await db.Jobs.SingleAsync(j => j.Id == jobId);
		Assert.Equal(JobStatus.Completed, job.Status);
		Assert.Equal(JobReviewStatus.None, job.ReviewStatus);
	}

	[Fact]
	public async Task ProcessJobAsync_ReviewFindings_GoBackIntoTheSameSession_ThenTheJobCompletes()
	{
		var agent = new ScriptedProvider((run, _) => Task.FromResult(Reply(run == 1 ? "Built the page." : "Put the null check back.")));
		// The first look finds a problem; the look after the fix finds none.
		var consult = new ScriptedConsultService(_ => new ConsultResult
		{
			Success = true,
			ProviderName = "Reviewer",
			Text = agent.Runs.Count == 1
				? "Read the change.\nISSUES:\n1. src/app.cs drops the null check."
				: "Read the change.\nREVIEW_APPROVED"
		});

		var (jobId, _, _) = await RunScriptedJobAsync(
			agent,
			_ => Task.CompletedTask,
			configureProject: (project, provider) =>
			{
				project.ReviewEnabled = true;
				project.ReviewProviderId = provider.Id;
			},
			configureServices: services => services.AddSingleton<IReadOnlyConsultService>(consult),
			versionControl: new DiffWithoutARepositoryVersionControlService(" src/app.cs | 2 +-\n-old\n+new\n"),
			configureJob: job => job.TeamRun = true);

		Assert.Equal(2, agent.Runs.Count);
		Assert.Equal("session-1", agent.Runs[1].SessionId);
		Assert.Contains("drops the null check", agent.Runs[1].Prompt);
		Assert.Equal(2, consult.Requests.Count);
		await using var db = CreateDbContext();
		var job = await db.Jobs.SingleAsync(j => j.Id == jobId);
		Assert.Equal(JobStatus.Completed, job.Status);
		Assert.Equal(JobReviewStatus.Approved, job.ReviewStatus);
		Assert.Equal(1, job.ReviewRounds);
	}

	[Fact]
	public async Task ProcessJobAsync_ATurnThatFails_FailsTheJobWithItsError()
	{
		var agent = new ScriptedProvider((_, _) => Task.FromResult(new ExecutionResult
		{
			Success = false,
			SessionId = "session-1",
			ErrorMessage = "The model refused the request."
		}));

		var (jobId, _, _) = await RunScriptedJobAsync(agent, _ => Task.CompletedTask);

		Assert.Single(agent.Runs);
		await using var db = CreateDbContext();
		var job = await db.Jobs.SingleAsync(j => j.Id == jobId);
		Assert.Equal(JobStatus.Failed, job.Status);
		Assert.Contains("The model refused the request.", job.ErrorMessage);
	}

	[Fact]
	public async Task ProcessJobAsync_ATeamJob_IsPlannedByTheLead_AndBuiltFromThePlan()
	{
		var agent = new ScriptedProvider((_, _) => Task.FromResult(Reply("Built the page.")));
		var lead = new ScriptedConsultService(_ => new ConsultResult
		{
			Success = true,
			Text = "Outcome: a settings page.\n1. Add SettingsPage.razor under Pages.",
			ProviderName = "Claude",
			ModelUsed = "opus",
			InputTokens = 120,
			OutputTokens = 40
		});

		var (jobId, _, _) = await RunScriptedJobAsync(
			agent,
			_ => Task.CompletedTask,
			configureProject: (project, provider) =>
			{
				project.TeamEnabled = true;
				project.LeadProviderId = provider.Id;
				project.LeadModelId = "opus";
			},
			configureServices: services => services.AddSingleton<IReadOnlyConsultService>(lead),
			configureJob: job => job.TeamRun = true);

		// The Lead is asked once, read-only, for a plan of the job's goal.
		var asked = Assert.Single(lead.Requests);
		Assert.Contains("Add a settings page", asked.Prompt);
		Assert.Equal("plan", asked.TaskType);
		Assert.Equal("opus", asked.Target.ModelId);

		// The builder is handed the plan with the goal.
		var run = Assert.Single(agent.Runs);
		Assert.Contains("Add a settings page", run.Prompt);
		Assert.Contains("Add SettingsPage.razor under Pages.", run.Prompt);

		await using var db = CreateDbContext();
		var job = await db.Jobs.SingleAsync(j => j.Id == jobId);
		Assert.Equal(JobStatus.Completed, job.Status);
		Assert.Contains("Add SettingsPage.razor", job.PlanningOutput);
		Assert.Null(job.PlanSkippedReason);
		Assert.Equal(("opus", 120, 40), (job.PlanningModelUsed, job.PlanningInputTokens, job.PlanningOutputTokens));
	}

	[Fact]
	public async Task ProcessJobAsync_WhenTheLeadCannotPlan_TheJobIsBuiltFromItsGoal_AndSaysWhy()
	{
		var agent = new ScriptedProvider((_, _) => Task.FromResult(Reply("Built the page.")));
		var lead = new ScriptedConsultService(_ => ConsultResult.Skip("Claude is near its usage limit", "Claude"));

		var (jobId, _, _) = await RunScriptedJobAsync(
			agent,
			_ => Task.CompletedTask,
			configureProject: (project, provider) =>
			{
				project.TeamEnabled = true;
				project.LeadProviderId = provider.Id;
			},
			configureServices: services => services.AddSingleton<IReadOnlyConsultService>(lead),
			configureJob: job => job.TeamRun = true);

		Assert.Single(lead.Requests);
		var run = Assert.Single(agent.Runs);
		Assert.Contains("Add a settings page", run.Prompt);
		Assert.DoesNotContain("implementation_plan", run.Prompt);

		await using var db = CreateDbContext();
		var job = await db.Jobs.SingleAsync(j => j.Id == jobId);
		Assert.Equal(JobStatus.Completed, job.Status);
		Assert.Null(job.PlanningOutput);
		Assert.Equal("Claude is near its usage limit", job.PlanSkippedReason);
	}

	[Fact]
	public async Task ProcessJobAsync_AJobItsBuilderWorksAlone_IsNeitherPlannedNorReviewed()
	{
		var agent = new ScriptedProvider((_, _) => Task.FromResult(Reply("Built the page.")));
		var team = new ScriptedConsultService(_ => new ConsultResult { Success = true, Text = "A plan.\nISSUES:\n1. Something." });

		// The project works as a team, but this job was created to run on its builder alone.
		var (jobId, _, _) = await RunScriptedJobAsync(
			agent,
			_ => Task.CompletedTask,
			configureProject: (project, provider) =>
			{
				project.TeamEnabled = true;
				project.LeadProviderId = provider.Id;
				project.ReviewEnabled = true;
				project.ReviewProviderId = provider.Id;
			},
			configureServices: services => services.AddSingleton<IReadOnlyConsultService>(team),
			versionControl: new DiffWithoutARepositoryVersionControlService(" src/app.cs | 2 +-\n-old\n+new\n"));

		Assert.Empty(team.Requests);
		Assert.Single(agent.Runs);
		await using var db = CreateDbContext();
		var job = await db.Jobs.SingleAsync(j => j.Id == jobId);
		Assert.Equal((JobStatus.Completed, JobReviewStatus.None), (job.Status, job.ReviewStatus));
		Assert.Null(job.PlanningOutput);
	}

	private const string TwoStepPlan = """
		Outcome: calc.py gains divide and power.

		## STEP: Add divide
		Do: Add divide(a, b) to calc.py.
		Done when: divide(6, 3) is 2.

		## STEP: Add power
		Do: Add power(base, exponent) to calc.py.
		Done when: power(2, 3) is 8.
		""";

	[Fact]
	public async Task ProcessJobAsync_ATeamJobPlannedIntoSteps_BuildsEachInItsOwnSession_AndPassesNotesOn()
	{
		var agent = new ScriptedProvider((run, _) => Task.FromResult(new ExecutionResult
		{
			Success = true,
			SessionId = $"step-session-{run}",
			Output = run == 1 ? "Added it.\n\n## Handoff\ndivide is in calc.py; zero returns None." : "Added it.\n\n## Handoff\npower is in calc.py.",
			Messages = [new ExecutionMessage { Role = "assistant", Content = run == 1 ? "Added it.\n\n## Handoff\ndivide is in calc.py; zero returns None." : "Added it.\n\n## Handoff\npower is in calc.py." }]
		}));
		var lead = new ScriptedConsultService(request => new ConsultResult
		{
			Success = true,
			ProviderName = "Claude",
			Text = request.TaskType == "plan" ? TwoStepPlan : $"Both functions are there.\n{ChainPrompts.CheckPassedMarker}"
		});

		var (jobId, jobService, _) = await RunScriptedJobAsync(
			agent,
			_ => Task.CompletedTask,
			configureProject: (project, provider) =>
			{
				project.TeamEnabled = true;
				project.LeadProviderId = provider.Id;
			},
			configureServices: services => services.AddSingleton<IReadOnlyConsultService>(lead),
			configureJob: job => job.TeamRun = true);

		// One plan, one run per step, each starting a session of its own, then the Lead's check.
		Assert.Equal(["plan", "check"], lead.Requests.Select(request => request.TaskType));
		Assert.Contains("## STEP: <short title>", lead.Requests[0].Prompt);
		Assert.Contains("Builder's note: power is in calc.py.", lead.Requests[1].Prompt);
		Assert.Equal(2, agent.Runs.Count);
		Assert.All(agent.Runs, run => Assert.Null(run.SessionId));

		// The first builder is given its step among the others.
		Assert.Contains("Add a settings page", agent.Runs[0].Prompt);
		Assert.Contains("Outcome: calc.py gains divide and power.", agent.Runs[0].Prompt);
		Assert.Contains("<your_step number=\"1\" of=\"2\">", agent.Runs[0].Prompt);
		Assert.Contains("1. [yours] Add divide", agent.Runs[0].Prompt);
		Assert.Contains("2. [later] Add power", agent.Runs[0].Prompt);

		// The second is told what the first left behind.
		Assert.Contains("<your_step number=\"2\" of=\"2\">", agent.Runs[1].Prompt);
		Assert.Contains("1. [done] Add divide", agent.Runs[1].Prompt);
		Assert.Contains("Note from its builder: divide is in calc.py; zero returns None.", agent.Runs[1].Prompt);

		await using var db = CreateDbContext();
		var job = await db.Jobs.SingleAsync(j => j.Id == jobId);
		Assert.Equal(JobStatus.Completed, job.Status);
		Assert.StartsWith("Outcome: calc.py gains divide and power.", job.PlanningOutput);
		Assert.DoesNotContain("STEP", job.PlanningOutput);
		var steps = await db.JobSteps.Where(step => step.JobId == jobId).OrderBy(step => step.Order).ToListAsync();
		Assert.Equal(["Add divide", "Add power"], steps.Select(step => step.Title));
		Assert.All(steps, step => Assert.Equal(JobStepStatus.Done, step.Status));
		Assert.Equal(["step-session-1", "step-session-2"], steps.Select(step => step.SessionId));
		Assert.Equal("divide is in calc.py; zero returns None.", steps[0].Summary);

		// Both steps' conversations are kept, not just the last one's.
		Assert.Contains(jobService.Messages, m => m.Role == MessageRole.Assistant && m.Content.Contains("divide is in calc.py"));
		Assert.Contains(jobService.Messages, m => m.Role == MessageRole.Assistant && m.Content.Contains("power is in calc.py"));
	}

	[Fact]
	public async Task ProcessJobAsync_EachStepIsReviewedOnItsOwn_AndItsFixesStayInItsSession()
	{
		var agent = new ScriptedProvider((run, _) => Task.FromResult(new ExecutionResult
		{
			Success = true,
			SessionId = run <= 2 ? "step-session-1" : "step-session-2",
			Output = $"Turn {run} done.",
			Messages = [new ExecutionMessage { Role = "assistant", Content = $"Turn {run} done." }]
		}));
		var reviews = 0;
		var team = new ScriptedConsultService(request => request.TaskType switch
		{
			"plan" => new ConsultResult { Success = true, Text = TwoStepPlan },
			"check" => new ConsultResult { Success = true, Text = ChainPrompts.CheckPassedMarker },
			// The first look at step one finds a problem; every look after that approves.
			_ => new ConsultResult { Success = true, ProviderName = "Reviewer", CostUsd = 0.01m, Text = ++reviews == 1 ? "ISSUES:\n1. divide has no zero check." : "REVIEW_APPROVED" }
		});

		var (jobId, _, _) = await RunScriptedJobAsync(
			agent,
			_ => Task.CompletedTask,
			configureProject: (project, provider) =>
			{
				project.TeamEnabled = true;
				project.LeadProviderId = provider.Id;
				project.ReviewEnabled = true;
				project.ReviewProviderId = provider.Id;
			},
			configureServices: services => services.AddSingleton<IReadOnlyConsultService>(team),
			versionControl: new DiffWithoutARepositoryVersionControlService(" calc.py | 4 ++++\n+def divide\n"),
			configureJob: job => job.TeamRun = true);

		// Step one, its fix in the same session, then step two in a session of its own.
		Assert.Equal(3, agent.Runs.Count);
		Assert.Equal([null, "step-session-1", null], agent.Runs.Select(run => run.SessionId));
		Assert.Contains("divide has no zero check", agent.Runs[1].Prompt);

		// The reviewer is shown one step at a time, and told the rest is not missing.
		var reviewRequests = team.Requests.Where(request => request.TaskType is not ("plan" or "check")).ToList();
		Assert.Equal(3, reviewRequests.Count);
		Assert.Contains("# The step the agent was given", reviewRequests[0].Prompt);
		Assert.Contains("Add divide", reviewRequests[0].Prompt);
		Assert.Contains("step 1 of 2", reviewRequests[0].Prompt);
		Assert.Contains("Add power", reviewRequests[2].Prompt);
		Assert.Contains("step 2 of 2", reviewRequests[2].Prompt);

		await using var db = CreateDbContext();
		var steps = await db.JobSteps.Where(step => step.JobId == jobId).OrderBy(step => step.Order).ToListAsync();
		Assert.Equal([(JobReviewStatus.Approved, 1), (JobReviewStatus.Approved, 0)], steps.Select(step => (step.ReviewStatus, step.ReviewRounds)));

		// The job's totals speak for every step: all three reviews are paid for, not just the last.
		var job = await db.Jobs.SingleAsync(j => j.Id == jobId);
		Assert.Equal((JobStatus.Completed, JobReviewStatus.Approved, 1), (job.Status, job.ReviewStatus, job.ReviewRounds));
		Assert.Equal(0.03m, job.ReviewCostUsd);
	}

	[Theory]
	[InlineData(1, 3, 1)] // The first check asks for one more step; the second passes.
	[InlineData(9, 4, JobChain.MaxChecks)] // A Lead never satisfied is heard twice, then the work is delivered.
	public async Task ProcessJobAsync_TheLeadsCheck_CanAddSteps_ButOnlySoManyTimes(int checksThatAskForMore, int expectedRuns, int expectedChecks)
	{
		var agent = new ScriptedProvider((run, _) => Task.FromResult(Reply($"Turn {run} done.\n\n## Handoff\nNote {run}.")));
		var checks = 0;
		var lead = new ScriptedConsultService(request => new ConsultResult
		{
			Success = true,
			InputTokens = 5,
			OutputTokens = 7,
			Text = request.TaskType == "plan" ? TwoStepPlan
				: ++checks <= checksThatAskForMore ? $"The README is missing.\n\n## STEP: Document round {checks}\nDo: Add it to the README.\nDone when: The README lists it."
				: ChainPrompts.CheckPassedMarker
		});

		var (jobId, _, _) = await RunScriptedJobAsync(
			agent,
			_ => Task.CompletedTask,
			configureProject: (project, provider) =>
			{
				project.TeamEnabled = true;
				project.LeadProviderId = provider.Id;
			},
			configureServices: services => services.AddSingleton<IReadOnlyConsultService>(lead),
			configureJob: job => job.TeamRun = true);

		Assert.Equal(expectedRuns, agent.Runs.Count);
		Assert.Contains("Document round 1", agent.Runs[2].Prompt);
		await using var db = CreateDbContext();
		var job = await db.Jobs.SingleAsync(j => j.Id == jobId);
		Assert.Equal((JobStatus.Completed, expectedChecks), (job.Status, job.LeadChecks));
		var steps = await db.JobSteps.Where(step => step.JobId == jobId).OrderBy(step => step.Order).ToListAsync();
		Assert.Equal(expectedRuns, steps.Count);
		Assert.All(steps, step => Assert.Equal(JobStepStatus.Done, step.Status));
		Assert.Equal(JobStepSource.Check, steps[2].Source);
		// The Lead's checks are counted with its plan.
		Assert.True(job.PlanningOutputTokens > 7);
	}

	[Fact]
	public async Task ProcessJobAsync_AResumedChain_CarriesOnAtTheStepItWasOn_InThatStepsSession()
	{
		var agent = new ScriptedProvider((run, _) => Task.FromResult(Reply($"Turn {run} done.\n\n## Handoff\npower is in.")));

		// Interrupted during step two: step one is done, and the job remembers where it was.
		var (jobId, _, _) = await RunScriptedJobAsync(
			agent,
			_ => Task.CompletedTask,
			configureProject: (project, provider) =>
			{
				project.TeamEnabled = true;
				project.LeadProviderId = provider.Id;
			},
			configureJob: job =>
			{
				job.TeamRun = true;
				job.PlanningOutput = "Outcome: two functions.";
				job.ResumeFromStatus = JobStatus.Processing;
				job.RecoveryPrompt = "The step two prompt, as it was running.";
				job.Steps.Add(new JobStep { Order = 1, Title = "Add divide", Instructions = "Add divide.", Status = JobStepStatus.Done, Summary = "divide is in." });
				job.Steps.Add(new JobStep { Order = 2, Title = "Add power", Instructions = "Add power.", Status = JobStepStatus.Running });
			});

		// One run only: step one is not built again, and step two resumes its own session.
		var run = Assert.Single(agent.Runs);
		Assert.Equal("session-1", run.SessionId);
		await using var db = CreateDbContext();
		var steps = await db.JobSteps.Where(step => step.JobId == jobId).OrderBy(step => step.Order).ToListAsync();
		Assert.Equal([JobStepStatus.Done, JobStepStatus.Done], steps.Select(step => step.Status));
		Assert.Equal("divide is in.", steps[0].Summary);
		Assert.Equal("power is in.", steps[1].Summary);
		Assert.Equal(JobStatus.Completed, (await db.Jobs.SingleAsync(j => j.Id == jobId)).Status);
	}

	[Fact]
	public async Task ProcessJobAsync_StepsAlreadyDone_AreBuiltAgain_WhenTheirWorkIsNotInTheTree()
	{
		var agent = new ScriptedProvider((run, _) => Task.FromResult(Reply($"Turn {run} done.")));

		// A new run of a job whose first step was done before, and saved its work. Nothing put
		// that work back (the restore didn't happen), so skipping the step would deliver half the job.
		var (jobId, _, _) = await RunScriptedJobAsync(
			agent,
			_ => Task.CompletedTask,
			configureProject: (project, provider) =>
			{
				project.TeamEnabled = true;
				project.LeadProviderId = provider.Id;
			},
			configureJob: job =>
			{
				job.TeamRun = true;
				job.PlanningOutput = "Outcome: two functions.";
				job.Steps.Add(new JobStep { Order = 1, Title = "Add divide", Instructions = "Add divide.", Status = JobStepStatus.Done, Summary = "Done before.", SnapshotCommit = "abc123" });
				job.Steps.Add(new JobStep { Order = 2, Title = "Add power", Instructions = "Add power." });
			});

		Assert.Equal(2, agent.Runs.Count);
		Assert.Contains("1. [yours] Add divide", agent.Runs[0].Prompt);
		await using var db = CreateDbContext();
		Assert.All(await db.JobSteps.Where(step => step.JobId == jobId).ToListAsync(), step => Assert.Equal(JobStepStatus.Done, step.Status));
	}

	[Fact]
	public async Task ProcessJobAsync_ARetriedChain_CarriesOnAtTheFirstStepNotFinished_InAFreshSession()
	{
		var agent = new ScriptedProvider((run, _) =>
		{
			var reply = Reply($"Turn {run} done.\n\n## Handoff\npower is in.");
			(reply.InputTokens, reply.OutputTokens, reply.CostUsd) = (40, 8, 0.2m);
			return Task.FromResult(reply);
		});
		var usage = new RecordingUsageService();

		// A retry: step one was finished by the run that failed, which was then on step two.
		var (jobId, _, _) = await RunScriptedJobAsync(
			agent,
			_ => Task.CompletedTask,
			configureProject: (project, provider) =>
			{
				project.TeamEnabled = true;
				project.LeadProviderId = provider.Id;
			},
			configureServices: services => services.AddSingleton<IProviderUsageService>(usage),
			configureJob: job =>
			{
				job.TeamRun = true;
				job.PlanningOutput = "Outcome: two functions.";
				job.Steps.Add(new JobStep { Order = 1, Title = "Add divide", Instructions = "Add divide.", Status = JobStepStatus.Done, Summary = "divide is in.", InputTokens = 100, OutputTokens = 20, CostUsd = 0.5m });
				job.Steps.Add(new JobStep { Order = 2, Title = "Add power", Instructions = "Add power.", Status = JobStepStatus.Running, SessionId = "the-run-that-failed" });
			});

		// Step one is not built again, and step two starts over in a session of its own.
		var run = Assert.Single(agent.Runs);
		Assert.Null(run.SessionId);
		Assert.Contains("<your_step number=\"2\" of=\"2\">", run.Prompt);
		Assert.Contains("1. [done] Add divide", run.Prompt);
		await using var db = CreateDbContext();
		var steps = await db.JobSteps.Where(step => step.JobId == jobId).OrderBy(step => step.Order).ToListAsync();
		Assert.Equal([JobStepStatus.Done, JobStepStatus.Done], steps.Select(step => step.Status));
		Assert.Equal(["divide is in.", "power is in."], steps.Select(step => step.Summary));

		// The job's totals are the whole chain's; the step and the usage record hold this run's share.
		var job = await db.Jobs.SingleAsync(j => j.Id == jobId);
		Assert.Equal((JobStatus.Completed, 140, 28, 0.7m), (job.Status, job.ExecutionInputTokens, job.ExecutionOutputTokens, job.ExecutionCostUsd));
		Assert.Equal((40, 8, 0.2m), (steps[1].InputTokens, steps[1].OutputTokens, steps[1].CostUsd));
		Assert.Equal([(40, 8, 0.2m)], usage.Usage);
	}

	[Fact]
	public async Task ProcessJobAsync_AFollowUp_IsPlannedAndBuiltAfterTheStepsOfTheEarlierRun()
	{
		var agent = new ScriptedProvider((run, _) => Task.FromResult(Reply($"Turn {run} done.\n\n## Handoff\nNote {run}.")));
		var lead = new ScriptedConsultService(request => new ConsultResult
		{
			Success = true,
			Text = request.TaskType == "plan" ? TwoStepPlan : ChainPrompts.CheckPassedMarker
		});

		// A follow-up on a job whose first run built one step: that step is the job's record.
		var (jobId, _, _) = await RunScriptedJobAsync(
			agent,
			_ => Task.CompletedTask,
			configureProject: (project, provider) =>
			{
				project.TeamEnabled = true;
				project.LeadProviderId = provider.Id;
			},
			configureServices: services => services.AddSingleton<IReadOnlyConsultService>(lead),
			configureJob: job =>
			{
				job.TeamRun = true;
				job.Steps.Add(new JobStep { Order = 1, Title = "Add subtract", Instructions = "Add subtract.", Status = JobStepStatus.Earlier, Summary = "subtract is in.", InputTokens = 900, CostUsd = 2m });
			});

		// The Lead plans the follow-up, and its builders see that plan only.
		Assert.Equal(["plan", "check"], lead.Requests.Select(request => request.TaskType));
		Assert.Equal(2, agent.Runs.Count);
		Assert.Contains("<your_step number=\"1\" of=\"2\">", agent.Runs[0].Prompt);
		Assert.DoesNotContain("Add subtract", agent.Runs[0].Prompt);

		await using var db = CreateDbContext();
		var steps = await db.JobSteps.Where(step => step.JobId == jobId).OrderBy(step => step.Order).ToListAsync();
		Assert.Equal(
			[(1, "Add subtract", JobStepStatus.Earlier), (2, "Add divide", JobStepStatus.Done), (3, "Add power", JobStepStatus.Done)],
			steps.Select(step => (step.Order, step.Title, step.Status)));
		Assert.Equal("subtract is in.", steps[0].Summary);

		// The earlier run's usage is not counted into this one.
		var job = await db.Jobs.SingleAsync(j => j.Id == jobId);
		Assert.Equal(JobStatus.Completed, job.Status);
		Assert.NotEqual(2m, job.ExecutionCostUsd);
	}

	[Fact]
	public async Task ProcessJobAsync_AChainStoppedBetweenTwoSteps_StartsTheNextStepAfresh_NotAsARecovery()
	{
		var agent = new ScriptedProvider((run, _) => Task.FromResult(Reply($"Turn {run} done.\n\n## Handoff\npower is in.")));

		// Interrupted after step one was saved and before step two began: what the job remembers
		// is still step one's prompt and session.
		await RunScriptedJobAsync(
			agent,
			_ => Task.CompletedTask,
			configureProject: (project, provider) =>
			{
				project.TeamEnabled = true;
				project.LeadProviderId = provider.Id;
			},
			configureJob: job =>
			{
				job.TeamRun = true;
				job.PlanningOutput = "Outcome: two functions.";
				job.ResumeFromStatus = JobStatus.Processing;
				job.RecoveryPrompt = "The step one prompt, as it was running.";
				job.Steps.Add(new JobStep { Order = 1, Title = "Add divide", Instructions = "Add divide.", Status = JobStepStatus.Done, Summary = "divide is in." });
				job.Steps.Add(new JobStep { Order = 2, Title = "Add power", Instructions = "Add power." });
			});

		var run = Assert.Single(agent.Runs);
		Assert.Null(run.SessionId);
		Assert.Contains("<your_step number=\"2\" of=\"2\">", run.Prompt);
		Assert.DoesNotContain("The step one prompt", run.Prompt);
	}

	[Theory]
	[InlineData(false, 0)] // The Lead passes it: nothing is built, the work is delivered.
	[InlineData(true, 1)] // The Lead asks for one more step: only that step is built.
	public async Task ProcessJobAsync_AChainStoppedDuringTheLeadsCheck_IsCheckedAndDelivered_NotBuiltAgain(bool leadAsksForMore, int expectedRuns)
	{
		var agent = new ScriptedProvider((run, _) => Task.FromResult(Reply($"Turn {run} done.\n\n## Handoff\nThe README lists both.")));
		var checks = 0;
		var lead = new ScriptedConsultService(_ => new ConsultResult
		{
			Success = true,
			Text = leadAsksForMore && ++checks == 1
				? "The README is missing.\n\n## STEP: Document both\nDo: Add them to the README.\nDone when: The README lists both."
				: ChainPrompts.CheckPassedMarker
		});

		// Both steps were done when the service stopped; the job remembers step two's prompt.
		var (jobId, _, _) = await RunScriptedJobAsync(
			agent,
			_ => Task.CompletedTask,
			configureProject: (project, provider) =>
			{
				project.TeamEnabled = true;
				project.LeadProviderId = provider.Id;
			},
			configureServices: services => services.AddSingleton<IReadOnlyConsultService>(lead),
			configureJob: job =>
			{
				job.TeamRun = true;
				job.PlanningOutput = "Outcome: two functions.";
				job.ResumeFromStatus = JobStatus.Processing;
				job.RecoveryPrompt = "The step two prompt, as it was running.";
				job.Steps.Add(new JobStep { Order = 1, Title = "Add divide", Instructions = "Add divide.", Status = JobStepStatus.Done, Summary = "divide is in.", ReviewStatus = JobReviewStatus.Approved, ReviewRounds = 1 });
				job.Steps.Add(new JobStep { Order = 2, Title = "Add power", Instructions = "Add power.", Status = JobStepStatus.Done, Summary = "power is in.", ReviewStatus = JobReviewStatus.Approved });
			});

		Assert.All(lead.Requests, request => Assert.Equal("check", request.TaskType));
		Assert.Equal(expectedRuns + 1, lead.Requests.Count);
		Assert.Equal(expectedRuns, agent.Runs.Count);
		if (leadAsksForMore)
		{
			Assert.Null(agent.Runs[0].SessionId);
			Assert.Contains("Document both", agent.Runs[0].Prompt);
			Assert.DoesNotContain("The step two prompt", agent.Runs[0].Prompt);
		}

		await using var db = CreateDbContext();
		var job = await db.Jobs.SingleAsync(j => j.Id == jobId);
		Assert.Equal((JobStatus.Completed, JobReviewStatus.Approved, 1), (job.Status, job.ReviewStatus, job.ReviewRounds));
		Assert.All(await db.JobSteps.Where(step => step.JobId == jobId).ToListAsync(), step => Assert.Equal(JobStepStatus.Done, step.Status));
		if (!leadAsksForMore)
		{
			// No agent ran, so no usage is made up for the run.
			Assert.False(job.IsTokenEstimate);
			Assert.Null(job.ExecutionInputTokens);
		}
	}

	[Fact]
	public async Task ProcessJobAsync_WhenUsageRunsShortBetweenTwoSteps_TheChainStopsThere_AndIsQueuedAgain()
	{
		var providerId = Guid.Empty;
		var sessionReset = DateTime.UtcNow.AddHours(2);
		var agent = new ScriptedProvider(async (run, _) =>
		{
			// Step one uses the session up: by its end the provider is over its pause threshold.
			await using var usageDb = CreateDbContext();
			var summary = await usageDb.ProviderUsageSummaries.SingleOrDefaultAsync(usage => usage.ProviderId == providerId);
			if (summary == null)
			{
				summary = new ProviderUsageSummary { ProviderId = providerId };
				usageDb.ProviderUsageSummaries.Add(summary);
			}

			summary.LimitsRefreshedAt = DateTime.UtcNow;
			summary.LimitWindows = [SessionWindow(93, sessionReset)];
			await usageDb.SaveChangesAsync();
			return Reply($"Turn {run} done.\n\n## Handoff\ndivide is in.");
		});

		var (jobId, _, _) = await RunScriptedJobAsync(
			agent,
			_ => Task.CompletedTask,
			configureProject: (project, provider) =>
			{
				providerId = provider.Id;
				project.TeamEnabled = true;
				project.LeadProviderId = provider.Id;
			},
			configureJob: job =>
			{
				job.TeamRun = true;
				job.PlanningOutput = "Outcome: two functions.";
				job.Steps.Add(new JobStep { Order = 1, Title = "Add divide", Instructions = "Add divide." });
				job.Steps.Add(new JobStep { Order = 2, Title = "Add power", Instructions = "Add power." });
			});

		// Step two is not started on a provider a new job would be made to wait for. The job goes
		// back in the queue with step one kept, and is no recovery: nothing was cut off.
		Assert.Single(agent.Runs);
		await using var db = CreateDbContext();
		var job = await db.Jobs.SingleAsync(j => j.Id == jobId);
		Assert.Equal((JobStatus.New, null, null), (job.Status, job.ResumeFromStatus, job.WorkerInstanceId));
		Assert.StartsWith("Stopped between two steps.", job.ErrorMessage);
		Assert.Contains("session is 93% used", job.ErrorMessage);
		var steps = await db.JobSteps.Where(step => step.JobId == jobId).OrderBy(step => step.Order).ToListAsync();
		Assert.Equal([JobStepStatus.Done, JobStepStatus.Waiting], steps.Select(step => step.Status));
		Assert.Equal("divide is in.", steps[0].Summary);
	}

	[Fact]
	public void JobTimeLimit_TellsAChainThatRetryCarriesOn()
	{
		var message = JobTimeLimit.BuildStopMessage(TimeSpan.FromHours(12), "vibeswarm/recovery/job", hasSteps: true);

		Assert.True(JobTimeLimit.IsStopMessage(message));
		Assert.Contains("Retry carries on at the first step not finished", message);
		Assert.DoesNotContain("split the task", message);
	}

	private static ExecutionResult Reply(string text) => new()
	{
		Success = true,
		SessionId = "session-1",
		Output = text,
		Messages = [new ExecutionMessage { Role = "assistant", Content = text }]
	};

	[Fact]
	public async Task ProcessJobAsync_PausesAJobInterruptedByAServiceStop()
	{
		var workingPath = Directory.CreateTempSubdirectory("vibeswarm-interrupted-").FullName;
		var jobId = Guid.NewGuid();
		Provider providerConfig;
		await using (var setupContext = CreateDbContext())
		{
			var project = new Project { Id = Guid.NewGuid(), Name = "Interrupted Project", WorkingPath = workingPath };
			providerConfig = new Provider { Id = Guid.NewGuid(), Name = "Claude", Type = ProviderType.Claude, IsEnabled = true };
			setupContext.AddRange(project, providerConfig, new Job
			{
				Id = jobId,
				ProjectId = project.Id,
				ProviderId = providerConfig.Id,
				GoalPrompt = "Work for hours",
				Status = JobStatus.New
			});
			await setupContext.SaveChangesAsync();
		}

		var agent = new NeverFinishingProvider(providerConfig, throwsWhenCancelled: true);
		var services = new ServiceCollection();
		services.AddDbContext<VibeSwarmDbContext>(options => options.UseSqlite(_connection));
		services.AddSingleton<IProjectMemoryService, NoOpProjectMemoryService>();
		services.AddSingleton<IJobService>(new StubJobService(isCancellationRequested: false));
		services.AddSingleton<IProviderService>(new StubProviderService());
		var processingService = new JobProcessingService(
			services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
			NullLogger<JobProcessingService>.Instance,
			new NotARepositoryVersionControlService(),
			projectEnvironmentCredentialService: new NoOpProjectEnvironmentCredentialService())
		{
			ProviderFactoryOverride = _ => agent
		};

		using var jobCts = new CancellationTokenSource();
		try
		{
			await using var executionContext = CreateDbContext();
			var job = await executionContext.Jobs.Include(j => j.Project).Include(j => j.Provider).SingleAsync(j => j.Id == jobId);
			var run = InvokeProcessJobAsync(processingService, job, new StubJobService(isCancellationRequested: false), new StubProviderService(), executionContext, externalJobCts: jobCts);

			// The service stopping cancels every running job's source.
			await agent.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
			jobCts.Cancel();
			await run.WaitAsync(TimeSpan.FromSeconds(30));
		}
		finally
		{
			Directory.Delete(workingPath, recursive: true);
		}

		await using var verificationContext = CreateDbContext();
		var persistedJob = await verificationContext.Jobs.SingleAsync(j => j.Id == jobId);
		Assert.True(agent.WasStopped);
		Assert.Equal(JobStatus.Stalled, persistedJob.Status);
		Assert.StartsWith(JobProcessingService.InterruptedByShutdown, persistedJob.ErrorMessage);
		Assert.Equal(JobStatus.Processing, persistedJob.ResumeFromStatus);
		Assert.Contains("Work for hours", persistedJob.RecoveryPrompt);
	}

	[Fact]
	public async Task JobTimeLimit_DoesNotCountTimeSpentWaitingForTheUser()
	{
		var reached = new TaskCompletionSource();
		using var limit = new JobTimeLimit(TimeSpan.FromMilliseconds(300), () => reached.TrySetResult());

		limit.Pause();
		await Task.Delay(600);
		Assert.False(limit.Reached);

		limit.Resume();
		await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.True(limit.Reached);
	}

	[Fact]
	public async Task JobTimeLimit_StopsCountingOnceTheAgentIsDone()
	{
		using var limit = new JobTimeLimit(TimeSpan.FromMilliseconds(200), () => { });

		limit.Stop();
		await Task.Delay(500);

		Assert.False(limit.Reached);
	}

	[Fact]
	public async Task ResolveProviderForExecutionAsync_LetsJobsStartWhileTheSessionHasRoom()
	{
		var projectId = Guid.NewGuid();
		var providerId = Guid.NewGuid();
		var jobId = Guid.NewGuid();

		await using (var setupContext = CreateDbContext())
		{
			setupContext.Projects.Add(new Project { Id = projectId, Name = "Session Project", WorkingPath = "/tmp/session-project" });
			setupContext.Providers.Add(new Provider { Id = providerId, Name = "Claude", Type = ProviderType.Claude, IsEnabled = true });
			setupContext.ProviderUsageSummaries.Add(new ProviderUsageSummary
			{
				ProviderId = providerId,
				LimitsRefreshedAt = DateTime.UtcNow,
				LimitWindows = [SessionWindow(60, DateTime.UtcNow.AddHours(2))]
			});
			setupContext.Jobs.Add(new Job { Id = jobId, ProjectId = projectId, ProviderId = providerId, GoalPrompt = "Run now", Status = JobStatus.New });
			await setupContext.SaveChangesAsync();
		}

		var services = new ServiceCollection();
		services.AddDbContext<VibeSwarmDbContext>(options => options.UseSqlite(_connection));
		var processingService = new JobProcessingService(
			services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
			NullLogger<JobProcessingService>.Instance,
			new NoOpVersionControlService(),
			projectEnvironmentCredentialService: new NoOpProjectEnvironmentCredentialService());

		await using var executionContext = CreateDbContext();
		var job = await executionContext.Jobs.Include(j => j.Provider).SingleAsync(j => j.Id == jobId);

		var (resolvedProvider, cooldownUntil) = await InvokeResolveProviderForExecutionAsync(processingService, job, executionContext);

		Assert.Equal(providerId, resolvedProvider?.Id);
		Assert.Null(cooldownUntil);
	}

	[Fact]
	public async Task ResolveProviderForExecutionAsync_SwitchesAwayFromAProviderWaitingForItsSession()
	{
		var projectId = Guid.NewGuid();
		var heldProviderId = Guid.NewGuid();
		var fallbackProviderId = Guid.NewGuid();
		var jobId = Guid.NewGuid();

		await using (var setupContext = CreateDbContext())
		{
			setupContext.Projects.Add(new Project { Id = projectId, Name = "Fallback Project", WorkingPath = "/tmp/fallback-project" });
			setupContext.Providers.AddRange(
				new Provider { Id = heldProviderId, Name = "Claude", Type = ProviderType.Claude, IsEnabled = true },
				new Provider { Id = fallbackProviderId, Name = "Copilot", Type = ProviderType.Copilot, IsEnabled = true });
			setupContext.ProjectProviders.AddRange(
				new ProjectProvider { ProjectId = projectId, ProviderId = heldProviderId, Priority = 1, IsEnabled = true },
				new ProjectProvider { ProjectId = projectId, ProviderId = fallbackProviderId, Priority = 2, IsEnabled = true });
			setupContext.ProviderUsageSummaries.Add(new ProviderUsageSummary
			{
				ProviderId = heldProviderId,
				LimitsRefreshedAt = DateTime.UtcNow,
				LimitWindows = [SessionWindow(97, DateTime.UtcNow.AddHours(1))]
			});
			setupContext.Jobs.Add(new Job { Id = jobId, ProjectId = projectId, ProviderId = heldProviderId, GoalPrompt = "Use the other provider", Status = JobStatus.New });
			await setupContext.SaveChangesAsync();
		}

		var services = new ServiceCollection();
		services.AddDbContext<VibeSwarmDbContext>(options => options.UseSqlite(_connection));
		var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
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
		var job = await executionContext.Jobs.Include(j => j.Provider).SingleAsync(j => j.Id == jobId);

		var (resolvedProvider, cooldownUntil) = await InvokeResolveProviderForExecutionAsync(processingService, job, executionContext);

		Assert.Equal(fallbackProviderId, resolvedProvider?.Id);
		Assert.Null(cooldownUntil);
	}

	private static UsageLimitWindow SessionWindow(int percentUsed, DateTime resetTime) => new()
	{
		Scope = UsageLimitWindowScope.Session,
		LimitType = UsageLimitType.SessionLimit,
		Label = "Session (5 hours)",
		CurrentUsage = percentUsed,
		MaxUsage = 100,
		ResetTime = resetTime
	};

	[Fact]
	public async Task SaveRunMessagesAsync_KeepsTheConversationButNotTheEchoedPrompt()
	{
		var serviceProvider = new ServiceCollection().BuildServiceProvider();
		var processingService = new JobProcessingService(
			serviceProvider.GetRequiredService<IServiceScopeFactory>(),
			NullLogger<JobProcessingService>.Instance,
			new NoOpVersionControlService(),
			projectEnvironmentCredentialService: new NoOpProjectEnvironmentCredentialService());
		var jobService = new RecordingMessagesJobService();
		var jobId = Guid.NewGuid();
		var startedAt = DateTime.UtcNow;
		var result = new ExecutionResult
		{
			Success = false,
			Messages =
			[
				new ExecutionMessage { Role = "user", Content = "Continue the previous job for this project.", Timestamp = startedAt },
				new ExecutionMessage { Role = "assistant", Content = "Nothing needed changing.", Timestamp = startedAt.AddSeconds(1) },
				new ExecutionMessage { Role = "tool_use", Content = "bash", ToolName = "bash", ToolInput = "{\"command\":\"ls\"}", Timestamp = startedAt.AddSeconds(2) },
				new ExecutionMessage { Role = "tool_error", Content = "exit code 1", ToolName = "bash", ToolOutput = "exit code 1", Timestamp = startedAt.AddSeconds(3) }
			]
		};

		var method = typeof(JobProcessingService).GetMethod("SaveRunMessagesAsync", BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);
		await (Task)method.Invoke(processingService, [jobService, jobId, result])!;

		Assert.Equal(jobId, jobService.SavedJobId);
		Assert.Equal(
			[MessageRole.Assistant, MessageRole.ToolUse, MessageRole.ToolResult],
			jobService.SavedMessages.Select(message => message.Role));
		Assert.Equal("Nothing needed changing.", jobService.SavedMessages[0].Content);
		Assert.Equal(startedAt.AddSeconds(1), jobService.SavedMessages[0].CreatedAt);
		Assert.Equal("bash", jobService.SavedMessages[2].ToolName);
	}

	[Fact]
	public async Task SaveRunMessagesAsync_SavesNothingWhenTheRunOnlyEchoedItsPrompt()
	{
		var serviceProvider = new ServiceCollection().BuildServiceProvider();
		var processingService = new JobProcessingService(
			serviceProvider.GetRequiredService<IServiceScopeFactory>(),
			NullLogger<JobProcessingService>.Instance,
			new NoOpVersionControlService(),
			projectEnvironmentCredentialService: new NoOpProjectEnvironmentCredentialService());
		var jobService = new RecordingMessagesJobService();
		var result = new ExecutionResult
		{
			Messages = [new ExecutionMessage { Role = "user", Content = "Implement the feature." }]
		};

		var method = typeof(JobProcessingService).GetMethod("SaveRunMessagesAsync", BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);
		await (Task)method.Invoke(processingService, [jobService, Guid.NewGuid(), result])!;

		Assert.Null(jobService.SavedJobId);
	}

	private VibeSwarmDbContext CreateDbContext() => new(_dbOptions);

	private sealed record ScriptedJobRun(JobProcessingService Service, Guid RunningJobId)
	{
		public bool TrySendUserMessage(Guid jobId, string message) => Service.TrySendUserMessage(jobId, message);
	}

	/// <summary>
	/// Runs a job on <paramref name="agent"/>. <paramref name="whileRunning"/> runs alongside the job,
	/// or before it starts when <paramref name="startBeforeRunning"/> is set.
	/// </summary>
	private async Task<(Guid JobId, InteractionJobService JobService, JobProcessingService ProcessingService)> RunScriptedJobAsync(
		ScriptedProvider agent,
		Func<ScriptedJobRun, Task> whileRunning,
		bool startBeforeRunning = false,
		Action<Project, Provider>? configureProject = null,
		Action<IServiceCollection>? configureServices = null,
		IVersionControlService? versionControl = null,
		Action<Job>? configureJob = null)
	{
		var workingPath = Directory.CreateTempSubdirectory("vibeswarm-user-message-").FullName;
		var jobId = Guid.NewGuid();
		await using (var setupContext = CreateDbContext())
		{
			var project = new Project { Id = Guid.NewGuid(), Name = "Steered Project", WorkingPath = workingPath };
			var providerConfig = new Provider { Id = Guid.NewGuid(), Name = "Claude", Type = ProviderType.Claude, IsEnabled = true };
			configureProject?.Invoke(project, providerConfig);
			var seededJob = new Job
			{
				Id = jobId,
				ProjectId = project.Id,
				ProviderId = providerConfig.Id,
				GoalPrompt = "Add a settings page",
				Status = JobStatus.New,
				// Known before the run: reporting it through progress would also write a heartbeat
				// on another thread, and the tests' one SQLite connection can't take both.
				SessionId = "session-1"
			};
			configureJob?.Invoke(seededJob);
			setupContext.AddRange(project, providerConfig, seededJob);
			await setupContext.SaveChangesAsync();
		}

		var jobService = new InteractionJobService();
		var services = new ServiceCollection();
		services.AddDbContext<VibeSwarmDbContext>(options => options.UseSqlite(_connection));
		services.AddSingleton<IProjectMemoryService, NoOpProjectMemoryService>();
		services.AddSingleton<IJobService>(jobService);
		services.AddSingleton<IProviderService>(new StubProviderService());
		configureServices?.Invoke(services);
		var processingService = new JobProcessingService(
			services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
			NullLogger<JobProcessingService>.Instance,
			versionControl ?? new NotARepositoryVersionControlService(),
			interactionResponseService: new InMemoryInteractionResponseService(),
			projectEnvironmentCredentialService: new NoOpProjectEnvironmentCredentialService())
		{
			ProviderFactoryOverride = _ => agent
		};

		try
		{
			var run = new ScriptedJobRun(processingService, jobId);
			if (startBeforeRunning)
			{
				await whileRunning(run);
			}

			await using var executionContext = CreateDbContext();
			var job = await executionContext.Jobs.Include(j => j.Project).Include(j => j.Provider).SingleAsync(j => j.Id == jobId);
			var processing = InvokeProcessJobAsync(processingService, job, jobService, new StubProviderService(), executionContext, withJobCancellation: true);
			if (!startBeforeRunning)
			{
				await whileRunning(run);
			}

			await processing.WaitAsync(TimeSpan.FromSeconds(60));
		}
		finally
		{
			Directory.Delete(workingPath, recursive: true);
		}

		return (jobId, jobService, processingService);
	}

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
		VibeSwarmDbContext dbContext,
		bool withJobCancellation = false,
		CancellationTokenSource? externalJobCts = null)
	{
		var contextType = typeof(JobProcessingService).GetNestedType("JobExecutionContext", BindingFlags.NonPublic);
		Assert.NotNull(contextType);

		var executionContext = Activator.CreateInstance(contextType!);
		Assert.NotNull(executionContext);

		// The worker gives every job its own cancellation source; stopping a job cancels it.
		using var ownJobCts = new CancellationTokenSource();
		var jobCts = externalJobCts ?? ownJobCts;
		withJobCancellation |= externalJobCts != null;
		if (withJobCancellation)
		{
			contextType!.GetProperty("CancellationTokenSource")!.SetValue(executionContext, jobCts);
		}

		var method = typeof(JobProcessingService).GetMethod("ProcessJobAsync", BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);

		var skillStorage = new SkillStorageService(dbContext, NullLogger<SkillStorageService>.Instance);
		var task = (Task)method.Invoke(service, [job, jobService, providerService, dbContext, skillStorage, executionContext!, withJobCancellation ? jobCts.Token : CancellationToken.None])!;
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

	private sealed class RecordingMessagesJobService : FakeJobServiceBase
	{
		public Guid? SavedJobId { get; private set; }
		public List<JobMessage> SavedMessages { get; } = [];

		public override Task AddMessagesAsync(Guid jobId, IEnumerable<JobMessage> messages, CancellationToken cancellationToken = default)
		{
			SavedJobId = jobId;
			SavedMessages.AddRange(messages);
			return Task.CompletedTask;
		}
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

	private sealed class NoOpProjectMemoryService : IProjectMemoryService
	{
		public Task<string?> PrepareMemoryFileAsync(Project? project, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public Task SyncMemoryFromFileAsync(Guid projectId, string? memoryFilePath, CancellationToken cancellationToken = default) => Task.CompletedTask;
		public Task EnsureGitExcludeAsync(string workingPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
	}

	private sealed class NotARepositoryVersionControlService : FakeVersionControlServiceBase
	{
		public override Task<bool> IsGitRepositoryAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(false);
	}

	/// <summary>No repository to sync or commit to, but a diff of the agent's work for a reviewer to read.</summary>
	private sealed class DiffWithoutARepositoryVersionControlService(string diff) : FakeVersionControlServiceBase
	{
		public override Task<bool> IsGitRepositoryAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(false);

		public override Task<string?> GetWorkingDirectoryDiffAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default)
			=> Task.FromResult<string?>(diff);
	}

	/// <summary>
	/// An agent that stops to ask a question on its first run, and finishes when run again.
	/// </summary>
	private sealed class QuestionAskingProvider(Provider config, string question) : SdkProviderBase(config)
	{
		public List<string> Prompts { get; } = [];

		public override ProviderType Type => ProviderType.Claude;
		public override Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
		public override Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);

		public override async Task<ExecutionResult> ExecuteWithSessionAsync(
			string prompt,
			string? sessionId = null,
			string? workingDirectory = null,
			IProgress<ExecutionProgress>? progress = null,
			CancellationToken cancellationToken = default)
		{
			Prompts.Add(prompt);
			if (Prompts.Count > 1)
			{
				return new ExecutionResult { Success = true, Output = "Done." };
			}

			progress?.Report(new ExecutionProgress { OutputLine = question });
			await Task.Delay(Timeout.Infinite, cancellationToken);
			return new ExecutionResult { Success = true };
		}

		public override Task<ProviderInfo> GetProviderInfoAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ProviderInfo());
		public override Task<UsageLimits> GetUsageLimitsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new UsageLimits());
		public override Task<SessionSummary> GetSessionSummaryAsync(string? sessionId, string? workingDirectory = null, string? fallbackOutput = null, CancellationToken cancellationToken = default) => Task.FromResult(new SessionSummary());
		public override Task<PromptResponse> GetPromptResponseAsync(string prompt, string? workingDirectory = null, CancellationToken cancellationToken = default) => Task.FromResult(PromptResponse.Fail("not used"));
		public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	/// <summary>
	/// An agent whose first run ends by asking a question, and whose later runs just finish.
	/// </summary>
	private sealed class ClosingQuestionProvider(Provider config, string finalMessage) : SdkProviderBase(config)
	{
		public List<(string Prompt, string? SessionId)> Runs { get; } = [];

		public override ProviderType Type => ProviderType.Claude;
		public override Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
		public override Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);

		public override Task<ExecutionResult> ExecuteWithSessionAsync(
			string prompt,
			string? sessionId = null,
			string? workingDirectory = null,
			IProgress<ExecutionProgress>? progress = null,
			CancellationToken cancellationToken = default)
		{
			Runs.Add((prompt, sessionId));
			var text = Runs.Count == 1 ? finalMessage : "Updated the docs.";
			return Task.FromResult(new ExecutionResult
			{
				Success = true,
				SessionId = "session-1",
				Output = text,
				Messages = [new ExecutionMessage { Role = "assistant", Content = text }]
			});
		}

		public override Task<ProviderInfo> GetProviderInfoAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ProviderInfo());
		public override Task<UsageLimits> GetUsageLimitsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new UsageLimits());
		public override Task<SessionSummary> GetSessionSummaryAsync(string? sessionId, string? workingDirectory = null, string? fallbackOutput = null, CancellationToken cancellationToken = default) => Task.FromResult(new SessionSummary());
		public override Task<PromptResponse> GetPromptResponseAsync(string prompt, string? workingDirectory = null, CancellationToken cancellationToken = default) => Task.FromResult(PromptResponse.Fail("not used"));
		public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	private sealed class InteractionJobService : FakeJobServiceBase
	{
		public bool Paused { get; private set; }
		public bool Resumed { get; private set; }
		public List<JobMessage> Messages { get; } = [];

		public override Task<bool> IsCancellationRequestedAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(false);
		public override Task<IEnumerable<JobChangeSet>> GetChangeSetsAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.FromResult(Enumerable.Empty<JobChangeSet>());

		public override Task<bool> PauseForInteractionAsync(Guid id, string interactionPrompt, string interactionType, string? choices = null, CancellationToken cancellationToken = default)
		{
			Paused = true;
			return Task.FromResult(true);
		}

		public override Task AddMessageAsync(Guid jobId, JobMessage message, CancellationToken cancellationToken = default)
		{
			lock (Messages)
			{
				Messages.Add(message);
			}
			return Task.CompletedTask;
		}

		public override Task AddMessagesAsync(Guid jobId, IEnumerable<JobMessage> messages, CancellationToken cancellationToken = default)
		{
			lock (Messages)
			{
				Messages.AddRange(messages);
			}
			return Task.CompletedTask;
		}

		public override Task<bool> ResumeJobAsync(Guid id, CancellationToken cancellationToken = default)
		{
			Resumed = true;
			return Task.FromResult(true);
		}
	}

	private sealed class ReplyingInferenceService(string reply) : IInferenceService
	{
		public InferenceRequest? LastRequest { get; private set; }

		public Task<InferenceResponse> GenerateAsync(InferenceRequest request, CancellationToken ct = default)
		{
			LastRequest = request;
			return Task.FromResult(new InferenceResponse { Success = true, Response = reply });
		}

		public Task<InferenceHealthResult> ProbeAsync(InferenceProbeRequest request, CancellationToken ct = default) => throw new NotSupportedException();
		public Task<InferenceHealthResult> CheckHealthAsync(string? endpoint = null, InferenceProviderType? providerType = null, CancellationToken ct = default) => throw new NotSupportedException();
		public Task<List<DiscoveredModel>> GetAvailableModelsAsync(string? endpoint = null, InferenceProviderType? providerType = null, CancellationToken ct = default) => throw new NotSupportedException();
		public Task<InferenceResponse> GenerateForTaskAsync(string taskType, string prompt, string? systemPrompt = null, CancellationToken ct = default) => throw new NotSupportedException();
	}

	/// <summary>
	/// An agent whose turns follow a script: given the turn number (from 1) and the turn's
	/// cancellation token, the script returns how the turn ended.
	/// </summary>
	private sealed class ScriptedProvider(Func<int, CancellationToken, Task<ExecutionResult>> runTurn)
		: SdkProviderBase(new Provider { Name = "Claude", Type = ProviderType.Claude })
	{
		public List<(string Prompt, string? SessionId)> Runs { get; } = [];
		public List<Dictionary<string, string>?> EnvironmentVariables { get; } = [];

		public override ProviderType Type => ProviderType.Claude;
		public override Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
		public override Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);

		public override Task<ExecutionResult> ExecuteWithSessionAsync(
			string prompt,
			string? sessionId = null,
			string? workingDirectory = null,
			IProgress<ExecutionProgress>? progress = null,
			CancellationToken cancellationToken = default)
		{
			Runs.Add((prompt, sessionId));
			EnvironmentVariables.Add(CurrentEnvironmentVariables);
			return runTurn(Runs.Count, cancellationToken);
		}

		public override Task<ProviderInfo> GetProviderInfoAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ProviderInfo());
		public override Task<UsageLimits> GetUsageLimitsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new UsageLimits());
		public override Task<SessionSummary> GetSessionSummaryAsync(string? sessionId, string? workingDirectory = null, string? fallbackOutput = null, CancellationToken cancellationToken = default) => Task.FromResult(new SessionSummary());
		public override Task<PromptResponse> GetPromptResponseAsync(string prompt, string? workingDirectory = null, CancellationToken cancellationToken = default) => Task.FromResult(PromptResponse.Fail("not used"));
		public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	/// <summary>
	/// An agent that works until it is stopped: a CLI provider throws when cancelled, an SDK
	/// provider returns a failed result.
	/// </summary>
	private sealed class NeverFinishingProvider(Provider config, bool throwsWhenCancelled) : SdkProviderBase(config)
	{
		public bool WasStopped { get; private set; }
		public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public override ProviderType Type => ProviderType.Claude;
		public override Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
		public override Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);

		public override async Task<ExecutionResult> ExecuteWithSessionAsync(
			string prompt,
			string? sessionId = null,
			string? workingDirectory = null,
			IProgress<ExecutionProgress>? progress = null,
			CancellationToken cancellationToken = default)
		{
			try
			{
				Started.TrySetResult();
				await Task.Delay(Timeout.Infinite, cancellationToken);
				return new ExecutionResult { Success = true };
			}
			catch (OperationCanceledException)
			{
				WasStopped = true;
				if (throwsWhenCancelled)
				{
					throw;
				}

				return new ExecutionResult { Success = false, ErrorMessage = "Execution was cancelled." };
			}
		}

		public override Task<ProviderInfo> GetProviderInfoAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ProviderInfo());
		public override Task<UsageLimits> GetUsageLimitsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new UsageLimits());
		public override Task<SessionSummary> GetSessionSummaryAsync(string? sessionId, string? workingDirectory = null, string? fallbackOutput = null, CancellationToken cancellationToken = default) => Task.FromResult(new SessionSummary());
		public override Task<PromptResponse> GetPromptResponseAsync(string prompt, string? workingDirectory = null, CancellationToken cancellationToken = default) => Task.FromResult(PromptResponse.Fail("not used"));
		public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}
}
