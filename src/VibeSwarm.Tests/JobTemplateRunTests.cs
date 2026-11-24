using System.ComponentModel.DataAnnotations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Web.Data;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

/// <summary>
/// Running a template queues one job per project, and every setting the template leaves empty comes
/// from that project.
/// </summary>
public sealed class JobTemplateRunTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly ServiceProvider _rootProvider;

	public JobTemplateRunTests()
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
	public async Task RunAsync_QueuesOneJobPerProject_WithEachProjectsOwnSettings()
	{
		using var scope = _rootProvider.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
		var provider = SeedProvider(dbContext);
		var pullRequests = SeedProject(dbContext, provider.Id, "Alpha", project =>
		{
			project.DevelopmentBranch = "develop";
			project.GitChangeDeliveryMode = GitChangeDeliveryMode.PullRequest;
			project.DefaultTargetBranch = "main";
		});
		var commits = SeedProject(dbContext, provider.Id, "Beta");
		var template = SeedTemplate(dbContext, "Update dependencies");
		await dbContext.SaveChangesAsync();

		var service = CreateService(dbContext, scope, currentBranch: "feature/checked-out");
		var result = await service.RunAsync(template.Id, [pullRequests.Id, commits.Id]);

		Assert.Equal(2, result.QueuedCount);
		Assert.Equal(["Alpha", "Beta"], result.Projects.Select(outcome => outcome.ProjectName));

		var jobs = await dbContext.Jobs.AsNoTracking().ToDictionaryAsync(job => job.ProjectId);
		var alpha = jobs[pullRequests.Id];
		Assert.Equal(template.Id, alpha.JobTemplateId);
		Assert.Equal("Update dependencies", alpha.Title);
		Assert.Equal("Bring every dependency up to date.", alpha.GoalPrompt);
		Assert.Equal(provider.Id, alpha.ProviderId);
		Assert.Equal("develop", alpha.Branch);
		Assert.Equal(GitChangeDeliveryMode.PullRequest, alpha.GitChangeDeliveryMode);
		Assert.Equal("main", alpha.TargetBranch);

		var beta = jobs[commits.Id];
		Assert.Equal("feature/checked-out", beta.Branch);
		Assert.Equal(GitChangeDeliveryMode.CommitToBranch, beta.GitChangeDeliveryMode);
		Assert.Null(beta.TargetBranch);

		var stored = await dbContext.JobTemplates.AsNoTracking().SingleAsync();
		Assert.Equal(2, stored.UseCount);
	}

	[Fact]
	public async Task RunAsync_TemplateOverridesBeatTheProject()
	{
		using var scope = _rootProvider.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
		var provider = SeedProvider(dbContext);
		var project = SeedProject(dbContext, provider.Id, "Alpha", item =>
		{
			item.DevelopmentBranch = "develop";
			item.GitChangeDeliveryMode = GitChangeDeliveryMode.PullRequest;
			item.DefaultTargetBranch = "main";
		});
		var template = SeedTemplate(dbContext, "Compliance pass", item =>
		{
			item.Branch = "chore/compliance";
			item.GitChangeDeliveryMode = GitChangeDeliveryMode.CommitToBranch;
			item.CycleMode = CycleMode.FixedCount;
			item.MaxCycles = 3;
		});
		await dbContext.SaveChangesAsync();

		await CreateService(dbContext, scope).RunAsync(template.Id, [project.Id]);

		var job = await dbContext.Jobs.AsNoTracking().SingleAsync();
		Assert.Equal("chore/compliance", job.Branch);
		Assert.Equal(GitChangeDeliveryMode.CommitToBranch, job.GitChangeDeliveryMode);
		Assert.Equal(CycleMode.FixedCount, job.CycleMode);
		Assert.Equal(3, job.MaxCycles);
	}

	[Theory]
	[InlineData(JobStatus.New)]
	[InlineData(JobStatus.Processing)]
	[InlineData(JobStatus.Paused)]
	[InlineData(JobStatus.Stalled)]
	[InlineData(JobStatus.Planning)]
	public async Task RunAsync_SkipsProjectsAlreadyRunningTheTemplate(JobStatus unfinished)
	{
		using var scope = _rootProvider.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
		var provider = SeedProvider(dbContext);
		var busy = SeedProject(dbContext, provider.Id, "Busy");
		var finished = SeedProject(dbContext, provider.Id, "Finished");
		var template = SeedTemplate(dbContext, "Update dependencies");
		SeedJob(dbContext, template.Id, busy.Id, provider.Id, unfinished);
		SeedJob(dbContext, template.Id, finished.Id, provider.Id, JobStatus.Completed);
		await dbContext.SaveChangesAsync();

		var result = await CreateService(dbContext, scope).RunAsync(template.Id, [busy.Id, finished.Id]);

		Assert.Equal("Already running this template.", result.Projects.Single(outcome => outcome.ProjectId == busy.Id).SkipReason);
		Assert.NotNull(result.Projects.Single(outcome => outcome.ProjectId == finished.Id).JobId);
	}

	[Fact]
	public async Task RunAsync_SkipsInactiveAndMissingProjects_AndQueuesTheRest()
	{
		using var scope = _rootProvider.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
		var provider = SeedProvider(dbContext);
		var inactive = SeedProject(dbContext, provider.Id, "Inactive", project => project.IsActive = false);
		var active = SeedProject(dbContext, provider.Id, "Active");
		var template = SeedTemplate(dbContext, "Update dependencies");
		await dbContext.SaveChangesAsync();

		var missingId = Guid.NewGuid();
		var result = await CreateService(dbContext, scope).RunAsync(template.Id, [inactive.Id, missingId, active.Id]);

		Assert.Equal("Project is inactive.", result.Projects.Single(outcome => outcome.ProjectId == inactive.Id).SkipReason);
		Assert.Equal("Project not found.", result.Projects.Single(outcome => outcome.ProjectId == missingId).SkipReason);
		Assert.Equal(1, result.QueuedCount);
		Assert.Equal(active.Id, (await dbContext.Jobs.AsNoTracking().SingleAsync()).ProjectId);
	}

	[Fact]
	public async Task RunAsync_OneProjectFailingDoesNotStopTheOthers()
	{
		using var scope = _rootProvider.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
		var provider = SeedProvider(dbContext);
		var broken = SeedProject(dbContext, provider.Id, "Alpha");
		var healthy = SeedProject(dbContext, provider.Id, "Beta");
		var template = SeedTemplate(dbContext, "Update dependencies");
		await dbContext.SaveChangesAsync();

		var jobService = new FailingForProjectJobService(new JobService(dbContext, scope.ServiceProvider), broken.Id);
		var service = new JobTemplateService(dbContext, jobService, new FakeVersionControlService(null), NullLogger<JobTemplateService>.Instance);
		var result = await service.RunAsync(template.Id, [broken.Id, healthy.Id]);

		Assert.Equal("Disk full.", result.Projects.Single(outcome => outcome.ProjectId == broken.Id).SkipReason);
		Assert.NotNull(result.Projects.Single(outcome => outcome.ProjectId == healthy.Id).JobId);
	}

	[Fact]
	public async Task RunAsync_PinnedProviderTurnedOff_FailsOnceBeforeQueuingAnything()
	{
		using var scope = _rootProvider.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
		var provider = SeedProvider(dbContext);
		var project = SeedProject(dbContext, provider.Id, "Alpha");
		var template = SeedTemplate(dbContext, "Update dependencies", item => item.ProviderId = provider.Id);
		await dbContext.SaveChangesAsync();
		provider.IsEnabled = false;
		await dbContext.SaveChangesAsync();

		var error = await Assert.ThrowsAsync<ValidationException>(() => CreateService(dbContext, scope).RunAsync(template.Id, [project.Id]));

		Assert.Contains("turned off", error.Message);
		Assert.False(await dbContext.Jobs.AnyAsync());
	}

	[Fact]
	public async Task RunAsync_PinnedProvider_SkipsProjectsThatDontUseIt()
	{
		using var scope = _rootProvider.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
		var claude = SeedProvider(dbContext);
		var copilot = SeedProvider(dbContext);
		copilot.Name = "Copilot only";
		copilot.IsDefault = false;
		var usesClaude = SeedProject(dbContext, claude.Id, "Alpha");
		var copilotOnly = SeedProject(dbContext, copilot.Id, "Beta");
		var anyProvider = SeedProject(dbContext, claude.Id, "Gamma", project => project.ProviderSelections = []);
		var template = SeedTemplate(dbContext, "Update dependencies", item => item.ProviderId = claude.Id);
		await dbContext.SaveChangesAsync();

		var result = await CreateService(dbContext, scope).RunAsync(template.Id, [usesClaude.Id, copilotOnly.Id, anyProvider.Id]);

		Assert.NotNull(result.Projects.Single(outcome => outcome.ProjectId == usesClaude.Id).JobId);
		Assert.NotNull(result.Projects.Single(outcome => outcome.ProjectId == anyProvider.Id).JobId);
		Assert.Equal("Project doesn't use Copilot.", result.Projects.Single(outcome => outcome.ProjectId == copilotOnly.Id).SkipReason);
	}

	[Fact]
	public async Task GetRunsAsync_ReturnsTheLatestTopLevelJobPerProject()
	{
		using var scope = _rootProvider.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
		var provider = SeedProvider(dbContext);
		var alpha = SeedProject(dbContext, provider.Id, "Alpha");
		var beta = SeedProject(dbContext, provider.Id, "Beta");
		var template = SeedTemplate(dbContext, "Update dependencies");
		SeedJob(dbContext, template.Id, alpha.Id, provider.Id, JobStatus.Failed, createdAt: DateTime.UtcNow.AddDays(-3));
		var latestAlpha = SeedJob(dbContext, template.Id, alpha.Id, provider.Id, JobStatus.Completed, createdAt: DateTime.UtcNow.AddDays(-1));
		var child = SeedJob(dbContext, template.Id, alpha.Id, provider.Id, JobStatus.Processing, createdAt: DateTime.UtcNow);
		child.ParentJobId = latestAlpha.Id;
		var betaJob = SeedJob(dbContext, template.Id, beta.Id, provider.Id, JobStatus.Pending, createdAt: DateTime.UtcNow.AddDays(-2));
		await dbContext.SaveChangesAsync();

		var service = new JobTemplateService(dbContext);
		var runs = await service.GetRunsAsync(template.Id);

		Assert.Equal([latestAlpha.Id, betaJob.Id], runs.Select(run => run.JobId));
		Assert.Equal("Alpha", runs[0].ProjectName);
		Assert.True(runs[1].IsUnfinished);

		var listed = Assert.Single(await service.GetAllAsync());
		Assert.Equal(2, listed.ProjectRunCount);
		Assert.Equal(latestAlpha.CreatedAt, listed.LastRunAt!.Value, TimeSpan.FromSeconds(1));
	}

	[Fact]
	public void Migration_ClearsTheProjectSettingsOldTemplatesCopied()
	{
		var databasePath = Path.Combine(Path.GetTempPath(), $"vibeswarm-templates-{Guid.NewGuid():N}.db");
		try
		{
			using (var dbContext = DataServiceExtensions.CreateMigrationContext($"Data Source={databasePath};Pooling=False"))
			{
				var migrator = dbContext.GetService<IMigrator>();
				var migrations = dbContext.Database.GetMigrations().ToList();
				var index = migrations.FindIndex(id => id.EndsWith("_ClearProjectSettingsFromTemplates", StringComparison.Ordinal));
				migrator.Migrate(migrations[index - 1]);
				dbContext.JobTemplates.Add(new JobTemplate
				{
					Id = Guid.NewGuid(),
					Name = "Saved from a job",
					GoalPrompt = "Fix the bug",
					Branch = "develop",
					TargetBranch = "main",
					GitChangeDeliveryMode = GitChangeDeliveryMode.PullRequest
				});
				dbContext.SaveChanges();

				migrator.Migrate();
			}

			using var migrated = DataServiceExtensions.CreateMigrationContext($"Data Source={databasePath};Pooling=False");
			var template = migrated.JobTemplates.AsNoTracking().Single();
			Assert.Equal("Fix the bug", template.GoalPrompt);
			Assert.Null(template.Branch);
			Assert.Null(template.TargetBranch);
			Assert.Null(template.GitChangeDeliveryMode);
		}
		finally
		{
			SqliteConnection.ClearAllPools();
			File.Delete(databasePath);
		}
	}

	private static JobTemplateService CreateService(VibeSwarmDbContext dbContext, IServiceScope scope, string? currentBranch = null)
		=> new(dbContext, new JobService(dbContext, scope.ServiceProvider), new FakeVersionControlService(currentBranch), NullLogger<JobTemplateService>.Instance);

	private static Provider SeedProvider(VibeSwarmDbContext dbContext)
	{
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Copilot",
			Type = ProviderType.Copilot,
			ConnectionMode = ProviderConnectionMode.CLI,
			IsEnabled = true,
			IsDefault = true
		};
		dbContext.Providers.Add(provider);
		dbContext.ProviderModels.Add(new ProviderModel { ProviderId = provider.Id, ModelId = "gpt-5", IsAvailable = true, IsDefault = true });
		return provider;
	}

	private static Project SeedProject(VibeSwarmDbContext dbContext, Guid providerId, string name, Action<Project>? configure = null)
	{
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = name,
			WorkingPath = $"/tmp/{name.ToLowerInvariant()}",
			ProviderSelections = [new ProjectProvider { ProviderId = providerId, Priority = 1, IsEnabled = true }]
		};
		configure?.Invoke(project);
		dbContext.Projects.Add(project);
		return project;
	}

	private static JobTemplate SeedTemplate(VibeSwarmDbContext dbContext, string name, Action<JobTemplate>? configure = null)
	{
		var template = new JobTemplate { Id = Guid.NewGuid(), Name = name, GoalPrompt = "Bring every dependency up to date." };
		configure?.Invoke(template);
		dbContext.JobTemplates.Add(template);
		return template;
	}

	private static Job SeedJob(VibeSwarmDbContext dbContext, Guid templateId, Guid projectId, Guid providerId, JobStatus status, DateTime? createdAt = null)
	{
		var job = new Job
		{
			Id = Guid.NewGuid(),
			ProjectId = projectId,
			ProviderId = providerId,
			JobTemplateId = templateId,
			GoalPrompt = "Earlier run",
			Status = status,
			CreatedAt = createdAt ?? DateTime.UtcNow.AddHours(-1)
		};
		dbContext.Jobs.Add(job);
		return job;
	}

	public void Dispose()
	{
		_rootProvider.Dispose();
		_connection.Dispose();
	}

	private sealed class FakeVersionControlService(string? currentBranch) : FakeVersionControlServiceBase
	{
		public override Task<bool> IsGitRepositoryAsync(string workingDirectory, CancellationToken cancellationToken = default)
			=> Task.FromResult(currentBranch != null);

		public override Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken cancellationToken = default)
			=> Task.FromResult(currentBranch);
	}

	private sealed class FailingForProjectJobService(IJobService inner, Guid failingProjectId) : FakeJobServiceBase
	{
		public override Task<Job> CreateAsync(Job job, CancellationToken cancellationToken = default)
			=> job.ProjectId == failingProjectId
				? throw new InvalidOperationException("Disk full.")
				: inner.CreateAsync(job, cancellationToken);
	}
}
