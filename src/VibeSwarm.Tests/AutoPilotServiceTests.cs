using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class AutoPilotServiceTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly ServiceProvider _rootProvider;

	public AutoPilotServiceTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();

		var services = new ServiceCollection();
		services.AddDbContext<VibeSwarmDbContext>(options => options.UseSqlite(_connection));
		_rootProvider = services.BuildServiceProvider();

		using var scope = _rootProvider.CreateScope();
		scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>().Database.EnsureCreated();
	}

	[Fact]
	public async Task Tick_WhenCodingProviderIsOutOfUsage_WaitsForTheResetInsteadOfStopping()
	{
		using var harness = await CreateHarnessAsync();
		var resetAt = DateTime.UtcNow.AddHours(2);
		harness.Usage.Warnings[harness.Provider.Id] = new UsageExhaustionWarning { IsExhausted = true, ResetTime = resetAt };
		var loop = await harness.AddLoopAsync();

		await harness.Service.ProcessTickAsync(loop.Id, CancellationToken.None);

		var saved = await harness.ReloadAsync(loop.Id);
		Assert.Equal(IterationLoopStatus.Running, saved.Status);
		Assert.True(saved.NextIterationAt >= resetAt);
		Assert.Contains("usage", saved.StatusMessage, StringComparison.OrdinalIgnoreCase);
		Assert.Empty(harness.Ideas.SuggestRequests);
	}

	[Fact]
	public async Task Tick_WhenEverySuggestionRepeatsEarlierWork_RecordsAMissAndLooksAtTheNextFocusArea()
	{
		using var harness = await CreateHarnessAsync();
		harness.Ideas.SuggestResults.Enqueue(_ => new SuggestIdeasResult { Success = true, Stage = SuggestIdeasStage.Success });
		harness.Ideas.SuggestResults.Enqueue(_ => new SuggestIdeasResult { Success = true, Stage = SuggestIdeasStage.Success });
		var loop = await harness.AddLoopAsync();

		await harness.Service.ProcessTickAsync(loop.Id, CancellationToken.None);

		var saved = await harness.ReloadAsync(loop.Id);
		Assert.Equal(IterationLoopStatus.Running, saved.Status);
		Assert.Equal(0, saved.ConsecutiveFailures);
		Assert.Equal(1, saved.ConsecutiveIdeaMisses);
		Assert.False(string.IsNullOrWhiteSpace(saved.StatusMessage));

		await harness.ExpireCooldownAsync(loop.Id);
		await harness.Service.ProcessTickAsync(loop.Id, CancellationToken.None);

		Assert.Equal(2, harness.Ideas.SuggestRequests.Count);
		Assert.Contains(AutoPilotPrompts.GetFocusArea(0), harness.Ideas.SuggestRequests[0].AdditionalContext);
		Assert.Contains(AutoPilotPrompts.GetFocusArea(1), harness.Ideas.SuggestRequests[1].AdditionalContext);
	}

	[Fact]
	public async Task Tick_AfterAFullRoundWithoutNewIdeas_RestsBeforeTheNextRound()
	{
		using var harness = await CreateHarnessAsync();
		harness.Ideas.SuggestResults.Enqueue(_ => new SuggestIdeasResult { Success = true, Stage = SuggestIdeasStage.Success });
		var loop = await harness.AddLoopAsync(l => l.ConsecutiveIdeaMisses = AutoPilotPrompts.FocusAreas.Count - 1);

		await harness.Service.ProcessTickAsync(loop.Id, CancellationToken.None);

		var saved = await harness.ReloadAsync(loop.Id);
		Assert.Equal(IterationLoopStatus.Running, saved.Status);
		Assert.True(saved.NextIterationAt > DateTime.UtcNow.Add(AutoPilotService.NoIdeasRetryDelay).AddMinutes(-1));
	}

	[Fact]
	public async Task Tick_WhenTheInferenceProviderFails_AsksTheCodingProviderForTheIdea()
	{
		using var harness = await CreateHarnessAsync();
		var inferenceProvider = new InferenceProvider { Name = "Ollama", Endpoint = "http://localhost:11434" };
		harness.Db.InferenceProviders.Add(inferenceProvider);
		await harness.Db.SaveChangesAsync();
		harness.Ideas.SuggestResults.Enqueue(_ => new SuggestIdeasResult { Stage = SuggestIdeasStage.ProviderUnreachable, Message = "Ollama is down." });
		harness.Ideas.SuggestResults.Enqueue(harness.Ideas.CreateIdeaResult("Add CSV export"));
		var loop = await harness.AddLoopAsync(l => l.InferenceProviderId = inferenceProvider.Id);

		await harness.Service.ProcessTickAsync(loop.Id, CancellationToken.None);

		Assert.Equal(2, harness.Ideas.SuggestRequests.Count);
		Assert.True(harness.Ideas.SuggestRequests[0].UseInference);
		Assert.False(harness.Ideas.SuggestRequests[1].UseInference);
		Assert.Equal(harness.Provider.Id, harness.Ideas.SuggestRequests[1].ProviderId);
		Assert.NotNull((await harness.ReloadAsync(loop.Id)).CurrentJobId);
	}

	[Fact]
	public async Task Tick_CreatesTheJobLinkedToTheLoopWithItsProviderAndCommitMode()
	{
		using var harness = await CreateHarnessAsync();
		await harness.AddJobAsync("Add dark mode", JobStatus.Completed, iterationLoopId: Guid.NewGuid());
		harness.Ideas.SuggestResults.Enqueue(harness.Ideas.CreateIdeaResult("Add CSV export"));
		var loop = await harness.AddLoopAsync(l =>
		{
			l.ProviderId = harness.Provider.Id;
			l.ModelId = "gpt-5";
			l.AutoPush = true;
		});

		await harness.Service.ProcessTickAsync(loop.Id, CancellationToken.None);

		var options = Assert.Single(harness.Ideas.ConvertOptions);
		Assert.Equal(loop.Id, options.IterationLoopId);
		Assert.Equal(AutoCommitMode.CommitAndPush, options.CommitModeOverride);
		Assert.Equal(harness.Provider.Id, options.ProviderId);
		Assert.Equal("gpt-5", options.ModelId);
		Assert.Contains("Add dark mode", harness.Ideas.SuggestRequests[0].AdditionalContext);

		var saved = await harness.ReloadAsync(loop.Id);
		Assert.NotNull(saved.CurrentJobId);
		Assert.Null(saved.StatusMessage);
	}

	[Fact]
	public async Task Tick_WhenAPolishPassIsDue_QueuesOneOverTheRecentChanges()
	{
		using var harness = await CreateHarnessAsync();
		var loop = await harness.AddLoopAsync(l =>
		{
			l.PolishEveryIterations = 2;
			l.IterationsSinceLastPolish = 2;
		});
		await harness.AddJobAsync("Add CSV export", JobStatus.Completed, loop.Id);
		await harness.AddJobAsync("Add dark mode", JobStatus.Completed, loop.Id);

		await harness.Service.ProcessTickAsync(loop.Id, CancellationToken.None);

		Assert.Empty(harness.Ideas.SuggestRequests);
		var polish = Assert.Single(harness.Jobs.Created);
		Assert.True(AutoPilotPrompts.IsPolishJob(polish));
		Assert.Contains("Add CSV export", polish.GoalPrompt);
		Assert.Contains("Add dark mode", polish.GoalPrompt);
		Assert.Equal(loop.Id, polish.IterationLoopId);
		Assert.Equal(AutoCommitMode.CommitOnly, polish.CommitModeOverride);

		var saved = await harness.ReloadAsync(loop.Id);
		Assert.Equal(polish.Id, saved.CurrentJobId);
		Assert.Equal(0, saved.IterationsSinceLastPolish);
	}

	[Fact]
	public async Task Tick_WhenTheJobSucceeds_CountsItTowardTheNextPolishPass()
	{
		using var harness = await CreateHarnessAsync();
		var loop = await harness.AddLoopAsync();
		var job = await harness.AddJobAsync("Add CSV export", JobStatus.Completed, loop.Id);
		await harness.UpdateLoopAsync(loop.Id, l => l.CurrentJobId = job.Id);

		await harness.Service.ProcessTickAsync(loop.Id, CancellationToken.None);

		var saved = await harness.ReloadAsync(loop.Id);
		Assert.Null(saved.CurrentJobId);
		Assert.Equal(1, saved.CompletedIterations);
		Assert.Equal(1, saved.IterationsSinceLastPolish);
	}

	[Fact]
	public async Task Tick_WhenTheJobFailsOnAUsageLimit_DoesNotCountItAsAFailure()
	{
		using var harness = await CreateHarnessAsync();
		harness.Usage.Warnings[harness.Provider.Id] = new UsageExhaustionWarning { IsExhausted = true, ResetTime = DateTime.UtcNow.AddHours(1) };
		var loop = await harness.AddLoopAsync();
		var job = await harness.AddJobAsync("Add CSV export", JobStatus.Failed, loop.Id);
		await harness.UpdateLoopAsync(loop.Id, l => l.CurrentJobId = job.Id);

		await harness.Service.ProcessTickAsync(loop.Id, CancellationToken.None);

		var saved = await harness.ReloadAsync(loop.Id);
		Assert.Equal(0, saved.ConsecutiveFailures);
		Assert.Equal(1, saved.CompletedIterations);
	}

	[Fact]
	public async Task Tick_WhenTheJobFails_CountsTheFailureAndDropsItsIdeaFromTheBacklog()
	{
		using var harness = await CreateHarnessAsync();
		var loop = await harness.AddLoopAsync();
		var job = await harness.AddJobAsync("Add CSV export", JobStatus.Failed, loop.Id);
		var idea = new Idea { ProjectId = harness.Project.Id, Description = "Add CSV export" };
		harness.Db.Ideas.Add(idea);
		await harness.Db.SaveChangesAsync();
		await harness.UpdateLoopAsync(loop.Id, l =>
		{
			l.CurrentJobId = job.Id;
			l.CurrentIdeaId = idea.Id;
		});

		await harness.Service.ProcessTickAsync(loop.Id, CancellationToken.None);

		var saved = await harness.ReloadAsync(loop.Id);
		Assert.Equal(1, saved.ConsecutiveFailures);
		Assert.Contains(idea.Id, harness.Ideas.DeletedIds);
	}

	[Fact]
	public async Task Tick_PicksUpAJobCreatedForTheLoopButNeverRecorded()
	{
		using var harness = await CreateHarnessAsync();
		var loop = await harness.AddLoopAsync();
		var job = await harness.AddJobAsync("Add CSV export", JobStatus.New, loop.Id);

		await harness.Service.ProcessTickAsync(loop.Id, CancellationToken.None);

		Assert.Equal(job.Id, (await harness.ReloadAsync(loop.Id)).CurrentJobId);
		Assert.Empty(harness.Ideas.SuggestRequests);
	}

	[Fact]
	public async Task Start_RejectsACodingProviderThatIsDisabled()
	{
		using var harness = await CreateHarnessAsync();
		var disabled = new Provider { Id = Guid.NewGuid(), Name = "Off", Type = ProviderType.Claude, IsEnabled = false };
		harness.Db.Providers.Add(disabled);
		await harness.Db.SaveChangesAsync();

		var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			harness.Service.StartAsync(harness.Project.Id, new AutoPilotConfig { ProviderId = disabled.Id }));

		Assert.Contains("disabled", error.Message);
	}

	[Fact]
	public async Task Start_ClampsLimitsAndKeepsThePolishCadence()
	{
		using var harness = await CreateHarnessAsync();

		var loop = await harness.Service.StartAsync(harness.Project.Id, new AutoPilotConfig
		{
			CooldownSeconds = 1,
			MaxConsecutiveFailures = 0,
			PolishEveryIterations = 4,
			MaxTotalCostUsd = 0
		});

		Assert.Equal(AutoPilotConfig.MinCooldownSeconds, loop.CooldownSeconds);
		Assert.Equal(1, loop.MaxConsecutiveFailures);
		Assert.Equal(4, loop.PolishEveryIterations);
		Assert.Null(loop.MaxTotalCostUsd);
	}

	[Fact]
	public void PolishGoal_StaysWithinTheGoalPromptLimit()
	{
		var titles = Enumerable.Range(1, 50).Select(i => $"Change {i}: " + new string('x', 300)).ToList();

		var goal = AutoPilotPrompts.BuildPolishGoal(titles);

		Assert.True(goal.Length <= 2000);
		Assert.Contains("Change 1:", goal);
		Assert.Contains("Don't add features", goal);
	}

	[Theory]
	[InlineData(AutoCommitMode.Off, null, AutoCommitMode.CommitOnly)]
	[InlineData(AutoCommitMode.Off, AutoCommitMode.CommitAndPush, AutoCommitMode.CommitAndPush)]
	[InlineData(AutoCommitMode.CommitAndPush, AutoCommitMode.CommitOnly, AutoCommitMode.CommitOnly)]
	[InlineData(AutoCommitMode.CommitAndPush, null, AutoCommitMode.CommitAndPush)]
	public void EffectiveCommitMode_PrefersTheJobsOverrideToTheProjectSetting(
		AutoCommitMode projectMode, AutoCommitMode? jobOverride, AutoCommitMode expected)
	{
		var job = new Job { Project = new Project { AutoCommitMode = projectMode }, CommitModeOverride = jobOverride };

		Assert.Equal(expected, JobProcessingService.GetEffectiveCommitMode(job));
	}

	private async Task<Harness> CreateHarnessAsync()
	{
		var scope = _rootProvider.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();

		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Copilot",
			Type = ProviderType.Copilot,
			ConnectionMode = ProviderConnectionMode.CLI,
			IsEnabled = true,
			IsDefault = true
		};
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "VibeSwarm",
			WorkingPath = "/tmp/vibeswarm",
			ProviderSelections = [new ProjectProvider { ProviderId = provider.Id, Priority = 1, IsEnabled = true }]
		};
		db.Providers.Add(provider);
		db.Projects.Add(project);
		await db.SaveChangesAsync();

		var ideas = new FakeIdeaService(db, project.Id, provider.Id);
		var jobs = new FakeJobService(db);
		var usage = new FakeUsageService();
		var service = new AutoPilotService(
			db,
			ideas,
			jobs,
			usage,
			DispatchProxy.Create<IJobUpdateService, NoOpProxy>(),
			NullLogger<AutoPilotService>.Instance);

		return new Harness(scope, db, service, ideas, jobs, usage, provider, project);
	}

	public void Dispose()
	{
		_rootProvider.Dispose();
		_connection.Dispose();
	}

	private sealed record Harness(
		IServiceScope Scope,
		VibeSwarmDbContext Db,
		AutoPilotService Service,
		FakeIdeaService Ideas,
		FakeJobService Jobs,
		FakeUsageService Usage,
		Provider Provider,
		Project Project) : IDisposable
	{
		public async Task<IterationLoop> AddLoopAsync(Action<IterationLoop>? configure = null)
		{
			var loop = new IterationLoop
			{
				Id = Guid.NewGuid(),
				ProjectId = Project.Id,
				Status = IterationLoopStatus.Running,
				StartedAt = DateTime.UtcNow
			};
			configure?.Invoke(loop);
			Db.IterationLoops.Add(loop);
			await Db.SaveChangesAsync();
			return loop;
		}

		public async Task<Job> AddJobAsync(string title, JobStatus status, Guid? iterationLoopId)
		{
			var job = new Job
			{
				Id = Guid.NewGuid(),
				ProjectId = Project.Id,
				ProviderId = Provider.Id,
				Title = title,
				GoalPrompt = title,
				Status = status,
				IterationLoopId = iterationLoopId
			};
			Db.Jobs.Add(job);
			await Db.SaveChangesAsync();
			return job;
		}

		public async Task UpdateLoopAsync(Guid loopId, Action<IterationLoop> update)
		{
			update(await Db.IterationLoops.SingleAsync(l => l.Id == loopId));
			await Db.SaveChangesAsync();
		}

		public Task ExpireCooldownAsync(Guid loopId) =>
			UpdateLoopAsync(loopId, l => l.NextIterationAt = DateTime.UtcNow.AddSeconds(-1));

		public async Task<IterationLoop> ReloadAsync(Guid loopId)
		{
			var loop = await Db.IterationLoops.SingleAsync(l => l.Id == loopId);
			await Db.Entry(loop).ReloadAsync();
			return loop;
		}

		public void Dispose() => Scope.Dispose();
	}

	private sealed class FakeIdeaService(VibeSwarmDbContext db, Guid projectId, Guid providerId) : FakeIdeaServiceBase
	{
		public Queue<Func<SuggestIdeasRequest, SuggestIdeasResult>> SuggestResults { get; } = new();
		public List<SuggestIdeasRequest> SuggestRequests { get; } = [];
		public List<IdeaProcessingOptions> ConvertOptions { get; } = [];
		public List<Guid> DeletedIds { get; } = [];

		public Func<SuggestIdeasRequest, SuggestIdeasResult> CreateIdeaResult(string description) => _ =>
		{
			var idea = new Idea { ProjectId = projectId, Description = description };
			db.Ideas.Add(idea);
			db.SaveChanges();
			return new SuggestIdeasResult { Success = true, Stage = SuggestIdeasStage.Success, Ideas = [idea] };
		};

		public override Task<SuggestIdeasResult> SuggestIdeasFromCodebaseAsync(Guid projectId, SuggestIdeasRequest? request = null, CancellationToken cancellationToken = default)
		{
			SuggestRequests.Add(request!);
			return Task.FromResult(SuggestResults.Dequeue()(request!));
		}

		public override async Task<Job?> ConvertToJobAsync(Guid ideaId, IdeaProcessingOptions? options = null, CancellationToken cancellationToken = default)
		{
			ConvertOptions.Add(options!);
			var job = new Job
			{
				Id = Guid.NewGuid(),
				ProjectId = projectId,
				ProviderId = options?.ProviderId ?? providerId,
				Title = "Idea job",
				GoalPrompt = "Idea job",
				IterationLoopId = options?.IterationLoopId,
				CommitModeOverride = options?.CommitModeOverride
			};
			db.Jobs.Add(job);
			await db.SaveChangesAsync(cancellationToken);
			return job;
		}

		public override Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
		{
			DeletedIds.Add(id);
			return Task.CompletedTask;
		}
	}

	private sealed class FakeJobService(VibeSwarmDbContext db) : FakeJobServiceBase
	{
		public List<Job> Created { get; } = [];

		public override async Task<Job> CreateAsync(Job job, CancellationToken cancellationToken = default)
		{
			job.Id = Guid.NewGuid();
			if (job.ProviderId == Guid.Empty)
				job.ProviderId = await db.Providers.Select(p => p.Id).FirstAsync(cancellationToken);
			db.Jobs.Add(job);
			await db.SaveChangesAsync(cancellationToken);
			Created.Add(job);
			return job;
		}
	}

	private sealed class FakeUsageService : IProviderUsageService
	{
		public Dictionary<Guid, UsageExhaustionWarning> Warnings { get; } = [];

		public Task<UsageExhaustionWarning?> CheckExhaustionAsync(Guid providerId, int warningThreshold = 80, CancellationToken cancellationToken = default) =>
			Task.FromResult(Warnings.GetValueOrDefault(providerId));

		public Task RecordUsageAsync(Guid providerId, Guid? jobId, ExecutionResult executionResult, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<ProviderUsageSummary?> GetUsageSummaryAsync(Guid providerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Dictionary<Guid, ProviderUsageSummary>> GetAllUsageSummariesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<List<ProviderUsageRecord>> GetUsageHistoryAsync(Guid providerId, int limit = 100, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task UpdateVersionInfoAsync(Guid providerId, string version, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<ProviderUsageSummary> ApplyDetectedLimitsAsync(Guid providerId, UsageLimits limits, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task ResetPeriodAsync(Guid providerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	public class NoOpProxy : DispatchProxy
	{
		protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
			targetMethod?.ReturnType == typeof(Task) ? Task.CompletedTask : null;
	}
}
