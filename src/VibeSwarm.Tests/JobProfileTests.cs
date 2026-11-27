using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

/// <summary>
/// A job runs on a profile: the profile decides its provider, model and reasoning when the job
/// is created or retried, and the job remembers which profile that was.
/// </summary>
public sealed class JobProfileTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly ServiceProvider _rootProvider;
	private readonly Provider _claude = new() { Id = Guid.NewGuid(), Name = "Claude", Type = ProviderType.Claude, ConnectionMode = ProviderConnectionMode.CLI, IsEnabled = true, IsDefault = true, DefaultReasoningEffort = "medium" };
	private readonly Provider _copilot = new() { Id = Guid.NewGuid(), Name = "Copilot", Type = ProviderType.Copilot, ConnectionMode = ProviderConnectionMode.CLI, IsEnabled = true };
	private readonly Provider _openCode = new() { Id = Guid.NewGuid(), Name = "OpenCode", Type = ProviderType.OpenCode, ConnectionMode = ProviderConnectionMode.CLI, IsEnabled = false };
	private readonly Project _project = new() { Id = Guid.NewGuid(), Name = "Profiled", WorkingPath = "/tmp/profiled" };

	// The project builds on Claude alone, on a profile of its own.
	private readonly Profile _build;
	private readonly Profile _deep;
	private readonly Profile _plain;
	private readonly Profile _onCopilot;
	private readonly Profile _onOpenCode;

	public JobProfileTests()
	{
		_build = NewProfile("Build", _claude, "sonnet", "low");
		_deep = NewProfile("Deep", _claude, "opus", "high");
		_plain = NewProfile("Plain", _claude, null, null);
		_onCopilot = NewProfile("Second opinion", _copilot, "gpt-5", null);
		_onOpenCode = NewProfile("Switched off", _openCode, null, null);

		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();

		var services = new ServiceCollection();
		services.AddDbContext<VibeSwarmDbContext>(options => options.UseSqlite(_connection));
		_rootProvider = services.BuildServiceProvider();

		using var scope = _rootProvider.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
		dbContext.Database.EnsureCreated();
		dbContext.Providers.AddRange(_claude, _copilot, _openCode);
		dbContext.ProviderModels.AddRange(
			new ProviderModel { ProviderId = _claude.Id, ModelId = "opus", IsAvailable = true, IsDefault = true },
			new ProviderModel { ProviderId = _claude.Id, ModelId = "sonnet", IsAvailable = true },
			new ProviderModel { ProviderId = _copilot.Id, ModelId = "gpt-5", IsAvailable = true, IsDefault = true });
		dbContext.Profiles.AddRange(_build, _deep, _plain, _onCopilot, _onOpenCode);
		_project.ProviderSelections.Add(new ProjectProvider { ProviderId = _claude.Id, ProfileId = _build.Id, PreferredModelId = "sonnet", PreferredReasoningEffort = "low", Priority = 0 });
		dbContext.Projects.Add(_project);
		dbContext.SaveChanges();
	}

	[Fact]
	public async Task CreateAsync_OnAProfile_TakesItsValuesOverWhatWasSent_AndRemembersItsName()
	{
		var job = await CreateAsync(new Job { ProfileId = _deep.Id, ProviderId = _copilot.Id, ModelUsed = "gpt-5" });

		Assert.Equal((_claude.Id, "opus", "high"), (job.ProviderId, job.ModelUsed, job.ReasoningEffort));
		Assert.Equal(((Guid?)_deep.Id, "Deep"), (job.ProfileId, job.ProfileName));
	}

	[Fact]
	public async Task CreateAsync_OnAProfileThatLeavesModelAndReasoningOpen_UsesTheProvidersDefaults()
	{
		var job = await CreateAsync(new Job { ProfileId = _plain.Id });

		// Not the sonnet and low reasoning the project prefers for its own Build profile.
		Assert.Equal((_claude.Id, "opus", "medium"), (job.ProviderId, job.ModelUsed, job.ReasoningEffort));
		Assert.Equal("Plain", job.ProfileName);
		Assert.Equal("opus", ReadPlan(job)[0].ModelId);
	}

	[Fact]
	public async Task CreateAsync_NamingNeitherProfileNorProvider_RunsOnTheProjectsBuildProfile()
	{
		var job = await CreateAsync(new Job());

		Assert.Equal((_claude.Id, "sonnet", "low"), (job.ProviderId, job.ModelUsed, job.ReasoningEffort));
		Assert.Equal(((Guid?)_build.Id, "Build"), (job.ProfileId, job.ProfileName));
	}

	[Fact]
	public async Task CreateAsync_OnAProjectThatListsNothing_RunsOnTheDefaultProvidersOwnProfile()
	{
		Guid unlistedProjectId;
		Guid claudeDefaultId;
		using (var scope = _rootProvider.CreateScope())
		{
			var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
			await VibeSwarm.Web.Services.ProfileService.EnsureProviderDefaultsAsync(dbContext);
			claudeDefaultId = (await dbContext.Profiles.SingleAsync(profile => profile.ProviderId == _claude.Id && profile.IsProviderDefault)).Id;
			var unlisted = new Project { Id = Guid.NewGuid(), Name = "Unlisted", WorkingPath = "/tmp/unlisted" };
			dbContext.Projects.Add(unlisted);
			await dbContext.SaveChangesAsync();
			unlistedProjectId = unlisted.Id;
		}

		using var createScope = _rootProvider.CreateScope();
		var job = await new JobService(createScope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>(), createScope.ServiceProvider)
			.CreateAsync(new Job { ProjectId = unlistedProjectId, Title = "A job", GoalPrompt = "Do the thing" });

		// Claude is the default provider; its profile leaves the model and reasoning to it.
		Assert.Equal(((Guid?)claudeDefaultId, _claude.Id, "opus", "medium"), (job.ProfileId, job.ProviderId, job.ModelUsed, job.ReasoningEffort));
	}

	[Fact]
	public async Task CreateAsync_OnAProfileThatCannotRun_FallsThroughInsteadOfFailing()
	{
		// A profile that is gone, and one whose provider is turned off, both count as not named.
		var gone = await CreateAsync(new Job { ProfileId = Guid.NewGuid() });
		var off = await CreateAsync(new Job { ProfileId = _onOpenCode.Id });
		Assert.All(new[] { gone, off }, job => Assert.Equal(((Guid?)_build.Id, "sonnet"), (job.ProfileId, job.ModelUsed)));

		// An older client that names a provider instead is left as it was sent.
		var spelledOut = await CreateAsync(new Job { ProfileId = Guid.NewGuid(), ProviderId = _claude.Id, ModelUsed = "opus", ReasoningEffort = "high" });
		Assert.Equal((_claude.Id, "opus", "high"), (spelledOut.ProviderId, spelledOut.ModelUsed, spelledOut.ReasoningEffort));
		Assert.Null(spelledOut.ProfileId);
		Assert.Null(spelledOut.ProfileName);
	}

	[Fact]
	public async Task CreateAsync_OnAProfileOutsideTheProjectsList_RunsItFirstThenFollowsTheList()
	{
		var job = await CreateAsync(new Job { ProfileId = _onCopilot.Id });

		Assert.Equal((_copilot.Id, "gpt-5"), (job.ProviderId, job.ModelUsed));
		var plan = ReadPlan(job);
		Assert.Equal(_copilot.Id, plan[0].ProviderId);
		Assert.Contains(plan, target => target.ProviderId == _claude.Id);
		Assert.True(plan.FindIndex(target => target.ProviderId == _claude.Id) > plan.FindLastIndex(target => target.ProviderId == _copilot.Id));
	}

	[Fact]
	public async Task CreateAsync_AsInstances_GivesEveryCopyTheProfile()
	{
		await CreateAsync(new Job { ProfileId = _deep.Id, GoalPrompt = "- Fix the header\n- Fix the footer", RequestedInstances = 2 });

		using var scope = _rootProvider.CreateScope();
		var jobs = await scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>().Jobs.AsNoTracking().ToListAsync();
		Assert.Equal(2, jobs.Count);
		Assert.All(jobs, job => Assert.Equal(((Guid?)_deep.Id, "Deep", "opus", "high"), (job.ProfileId, job.ProfileName, job.ModelUsed, job.ReasoningEffort)));
	}

	[Fact]
	public async Task Retry_WithNothingNamed_RunsOnTheJobsProfileAgain()
	{
		var job = await CreateFailedAsync(_deep);

		Assert.True(await RetryAsync(job.Id));

		var retried = await ReadAsync(job.Id);
		Assert.Equal((JobStatus.New, _claude.Id, "opus", "high"), (retried.Status, retried.ProviderId, retried.ModelUsed, retried.ReasoningEffort));
		Assert.Equal(((Guid?)_deep.Id, "Deep"), (retried.ProfileId, retried.ProfileName));
	}

	[Fact]
	public async Task Retry_OnAnotherProfile_SwitchesToIt()
	{
		var job = await CreateFailedAsync(_deep);

		Assert.True(await RetryAsync(job.Id, profileId: _onCopilot.Id));

		var retried = await ReadAsync(job.Id);
		Assert.Equal((_copilot.Id, "gpt-5"), (retried.ProviderId, retried.ModelUsed));
		Assert.Equal(((Guid?)_onCopilot.Id, "Second opinion"), (retried.ProfileId, retried.ProfileName));
		Assert.Equal(_copilot.Id, ReadPlan(retried)[0].ProviderId);
	}

	[Fact]
	public async Task Retry_SpellingOutAProvider_NoLongerNamesAProfile()
	{
		var job = await CreateFailedAsync(_deep);

		Assert.True(await RetryAsync(job.Id, providerId: _claude.Id, modelId: "sonnet"));

		var retried = await ReadAsync(job.Id);
		Assert.Equal((_claude.Id, "sonnet"), (retried.ProviderId, retried.ModelUsed));
		Assert.Null(retried.ProfileId);
		Assert.Null(retried.ProfileName);
	}

	[Fact]
	public async Task SwitchingProvider_KeepsTheProfileTheJobWasAskedToRunOn()
	{
		// Copilot joins the project's list, so the job has somewhere to go.
		using (var scope = _rootProvider.CreateScope())
		{
			var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
			dbContext.ProjectProviders.Add(new ProjectProvider { ProjectId = _project.Id, ProviderId = _copilot.Id, ProfileId = _onCopilot.Id, PreferredModelId = "gpt-5", Priority = 1 });
			await dbContext.SaveChangesAsync();
		}

		var job = await CreateFailedAsync(_deep);
		using (var scope = _rootProvider.CreateScope())
		{
			var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
			var service = new JobService(dbContext, scope.ServiceProvider);
			var moved = false;
			for (var attempt = 0; attempt < 5 && (await ReadAsync(job.Id)).ProviderId == _claude.Id; attempt++)
			{
				moved = await service.TryFailOverToNextExecutionTargetAsync(job.Id, "Claude usage limit reached");
			}

			Assert.True(moved);
		}

		var switched = await ReadAsync(job.Id);
		Assert.Equal(_copilot.Id, switched.ProviderId);
		Assert.Equal(((Guid?)_deep.Id, "Deep"), (switched.ProfileId, switched.ProfileName));
	}

	[Fact]
	public void ResolveForJob_FillsWhatTheProfileLeavesOpenFromItsProvider()
	{
		var open = new Profile { ProviderId = _claude.Id, Provider = _claude };
		Assert.Equal((_claude.Id, "opus", "medium"), VibeSwarm.Web.Services.ProfileLinks.ResolveForJob(open, "opus"));

		var set = new Profile { ProviderId = _claude.Id, Provider = _claude, ModelId = "sonnet", ReasoningEffort = "high" };
		Assert.Equal((_claude.Id, "sonnet", "high"), VibeSwarm.Web.Services.ProfileLinks.ResolveForJob(set, "opus"));
	}

	private static Profile NewProfile(string name, Provider provider, string? modelId, string? reasoningEffort)
		=> new() { Id = Guid.NewGuid(), Name = name, ProviderId = provider.Id, ModelId = modelId, ReasoningEffort = reasoningEffort };

	private async Task<Job> CreateAsync(Job job)
	{
		job.ProjectId = _project.Id;
		job.Title ??= "A job";
		job.GoalPrompt = string.IsNullOrEmpty(job.GoalPrompt) ? "Do the thing" : job.GoalPrompt;
		using var scope = _rootProvider.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
		return await new JobService(dbContext, scope.ServiceProvider).CreateAsync(job);
	}

	private async Task<Job> CreateFailedAsync(Profile profile)
	{
		var job = await CreateAsync(new Job { ProfileId = profile.Id });
		using var scope = _rootProvider.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
		var saved = await dbContext.Jobs.SingleAsync(item => item.Id == job.Id);
		saved.Status = JobStatus.Failed;
		// The CLI reports the model it ran, which need not be the one that was asked for.
		saved.ModelUsed = "claude-opus-reported";
		await dbContext.SaveChangesAsync();
		return saved;
	}

	private async Task<bool> RetryAsync(Guid jobId, Guid? providerId = null, string? modelId = null, Guid? profileId = null)
	{
		using var scope = _rootProvider.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
		return await new JobService(dbContext, scope.ServiceProvider).ResetJobWithOptionsAsync(jobId, providerId, modelId, profileId: profileId);
	}

	private async Task<Job> ReadAsync(Guid jobId)
	{
		using var scope = _rootProvider.CreateScope();
		return await scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>().Jobs.AsNoTracking().SingleAsync(job => job.Id == jobId);
	}

	private static List<JobExecutionTarget> ReadPlan(Job job)
		=> JsonSerializer.Deserialize<List<JobExecutionTarget>>(job.ExecutionPlan!)!;

	public void Dispose()
	{
		_rootProvider.Dispose();
		_connection.Dispose();
	}
}
