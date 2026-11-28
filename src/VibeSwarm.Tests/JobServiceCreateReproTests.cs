using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

/// <summary>
/// Tries to reproduce the user-reported tracking error during CreateAsync.
/// Uses sensitive-data logging so any tracking collision reports the offending key.
/// </summary>
public sealed class JobServiceCreateReproTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly ServiceProvider _rootProvider;

	public JobServiceCreateReproTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();

		var services = new ServiceCollection();
		services.AddDbContext<VibeSwarmDbContext>(options =>
		{
			options.UseSqlite(_connection);
			options.EnableSensitiveDataLogging();
			options.EnableDetailedErrors();
		});
		_rootProvider = services.BuildServiceProvider();

		using var scope = _rootProvider.CreateScope();
		var seed = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
		seed.Database.EnsureCreated();
	}

	[Fact]
	public async Task CreateAsync_PostedJobWithNullNavProps_SucceedsAfterValidationLoadedTrackedData()
	{
		using var scope = _rootProvider.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();

		var provider = SeedProvider(dbContext);
		var project = SeedProject(dbContext, provider.Id);
		await dbContext.SaveChangesAsync();

		var service = new JobService(dbContext, scope.ServiceProvider);

		// Simulate the controller deserialising a JSON body into a fresh Job POCO.
		// Id is Guid.Empty, nav props null, collections empty — same as FromBody yields.
		var incomingJob = new Job
		{
			ProjectId = project.Id,
			ProviderId = provider.Id,
			GoalPrompt = "Do the thing."
		};

		var created = await service.CreateAsync(incomingJob);

		Assert.NotEqual(Guid.Empty, created.Id);
	}

	[Fact]
	public async Task CreateAsync_CalledTwice_InSameScope_DoesNotThrowDuplicateTracking()
	{
		using var scope = _rootProvider.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();

		var provider = SeedProvider(dbContext);
		var project = SeedProject(dbContext, provider.Id);
		await dbContext.SaveChangesAsync();

		var service = new JobService(dbContext, scope.ServiceProvider);

		var first = await service.CreateAsync(new Job
		{
			ProjectId = project.Id,
			ProviderId = provider.Id,
			GoalPrompt = "First."
		});

		var second = await service.CreateAsync(new Job
		{
			ProjectId = project.Id,
			ProviderId = provider.Id,
			GoalPrompt = "Second."
		});

		Assert.NotEqual(first.Id, second.Id);
	}

	[Fact]
	public async Task CreateAsync_JobWithAttachedFilesJson_DoesNotCollide()
	{
		using var scope = _rootProvider.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();

		var provider = SeedProvider(dbContext);
		var project = SeedProject(dbContext, provider.Id);
		await dbContext.SaveChangesAsync();

		var service = new JobService(dbContext, scope.ServiceProvider);

		var incomingJob = new Job
		{
			ProjectId = project.Id,
			ProviderId = provider.Id,
			GoalPrompt = "Job with attachments.",
			AttachedFilesJson = "[\"/tmp/file.txt\"]"
		};

		var created = await service.CreateAsync(incomingJob);
		Assert.NotEqual(Guid.Empty, created.Id);
	}

	[Fact]
	public async Task CreateAsync_DecidesOnceWhetherTheTeamWorksTheJob()
	{
		using var scope = _rootProvider.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
		var provider = SeedProvider(dbContext);
		var project = SeedProject(dbContext, provider.Id);
		project.TeamEnabled = true;
		var soloProject = SeedProject(dbContext, provider.Id);
		soloProject.Name = "Solo";
		soloProject.WorkingPath = "/tmp/solo";
		await dbContext.SaveChangesAsync();
		var service = new JobService(dbContext, scope.ServiceProvider);

		// A project working as a team has its jobs worked by the team...
		Assert.True((await service.CreateAsync(new Job { ProjectId = project.Id, GoalPrompt = "Add a page." })).TeamRun);
		// ...unless the job was created for its builder alone,
		Assert.False((await service.CreateAsync(new Job { ProjectId = project.Id, GoalPrompt = "Fix a typo.", RunSolo = true })).TeamRun);
		// is a merge, which has one resolution,
		Assert.False((await service.CreateAsync(new Job { ProjectId = project.Id, GoalPrompt = "Merge it.", MergeFromBranch = "feature" })).TeamRun);
		// or runs as several copies side by side.
		var copies = await service.CreateAsync(new Job { ProjectId = project.Id, GoalPrompt = "- Fix the header\n- Fix the footer", RequestedInstances = 2 });
		Assert.False(copies.TeamRun);
		Assert.False((await service.CreateAsync(new Job { ProjectId = soloProject.Id, GoalPrompt = "Add a page." })).TeamRun);
	}

	[Fact]
	public async Task Steps_CanBeAddedAndSkipped_OnlyWhileTheChainIsStillBeingWorked()
	{
		using var scope = _rootProvider.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
		var provider = SeedProvider(dbContext);
		var project = SeedProject(dbContext, provider.Id);
		var running = new JobStep { Order = 1, Title = "Add divide", Instructions = "Add divide.", Status = JobStepStatus.Running };
		var waiting = new JobStep { Order = 2, Title = "Add power", Instructions = "Add power." };
		var job = new Job { Id = Guid.NewGuid(), ProjectId = project.Id, ProviderId = provider.Id, GoalPrompt = "Two functions.", TeamRun = true, Status = JobStatus.Processing, Steps = [running, waiting] };
		var finished = new Job { Id = Guid.NewGuid(), ProjectId = project.Id, ProviderId = provider.Id, GoalPrompt = "Done.", TeamRun = true, Status = JobStatus.Completed, Steps = [new JobStep { Order = 1, Title = "Did it", Instructions = "Did it.", Status = JobStepStatus.Done }] };
		var plain = new Job { Id = Guid.NewGuid(), ProjectId = project.Id, ProviderId = provider.Id, GoalPrompt = "Plain.", Status = JobStatus.Processing };
		dbContext.Jobs.AddRange(job, finished, plain);
		await dbContext.SaveChangesAsync();
		var service = new JobService(dbContext, scope.ServiceProvider);

		var added = await service.AddStepAsync(job.Id, "  Also update the README.\nList every function.  ");
		Assert.NotNull(added);
		Assert.Equal((3, "Also update the README.", JobStepSource.User, JobStepStatus.Waiting), (added.Order, added.Title, added.Source, added.Status));
		Assert.Equal("Also update the README.\nList every function.", added.Instructions);
		Assert.Equal(["Add divide", "Add power", "Also update the README."], (await service.GetStepsAsync(job.Id)).Select(step => step.Title));

		// Nothing can join a job that is over, one with no chain, or a blank step.
		Assert.Null(await service.AddStepAsync(finished.Id, "One more."));
		Assert.Null(await service.AddStepAsync(plain.Id, "One more."));
		Assert.Null(await service.AddStepAsync(job.Id, "   "));

		// A waiting step can be dropped; one that has started cannot, nor can another job's.
		Assert.True(await service.SkipStepAsync(job.Id, waiting.Id));
		Assert.False(await service.SkipStepAsync(job.Id, waiting.Id));
		Assert.False(await service.SkipStepAsync(job.Id, running.Id));
		Assert.False(await service.SkipStepAsync(plain.Id, added.Id));
		dbContext.ChangeTracker.Clear();
		Assert.Equal(JobStepStatus.Skipped, (await dbContext.JobSteps.SingleAsync(step => step.Id == waiting.Id)).Status);
	}

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
		dbContext.ProviderModels.Add(new ProviderModel
		{
			ProviderId = provider.Id,
			ModelId = "gpt-5",
			IsAvailable = true,
			IsDefault = true
		});
		return provider;
	}

	private static Project SeedProject(VibeSwarmDbContext dbContext, Guid providerId)
	{
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "VibeSwarm",
			WorkingPath = "/tmp/vibeswarm",
			ProviderSelections =
			[
				new ProjectProvider { ProviderId = providerId, Priority = 1, IsEnabled = true }
			]
		};
		dbContext.Projects.Add(project);
		return project;
	}

	public void Dispose()
	{
		_rootProvider.Dispose();
		_connection.Dispose();
	}
}
