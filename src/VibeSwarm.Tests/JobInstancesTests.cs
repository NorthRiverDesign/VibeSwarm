using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

/// <summary>
/// A job created with more than one instance runs as copies side by side, sharing a swarm.
/// </summary>
public sealed class JobInstancesTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly ServiceProvider _rootProvider;

	public JobInstancesTests()
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
	public async Task CreateAsync_WithThreeInstances_SavesThreeNumberedCopiesInOneSwarm()
	{
		using var scope = _rootProvider.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
		var (project, provider) = await SeedAsync(dbContext);
		var service = new JobService(dbContext, scope.ServiceProvider);

		var created = await service.CreateAsync(new Job
		{
			ProjectId = project.Id,
			ProviderId = provider.Id,
			ModelUsed = "opus",
			ReasoningEffort = "high",
			Title = "Polish the settings pages",
			GoalPrompt = "- Fix the header\n- Fix the footer\n- Fix the sidebar",
			RequestedInstances = 3,
		});

		var jobs = await dbContext.Jobs.AsNoTracking().OrderBy(job => job.InstanceNumber).ToListAsync();
		Assert.Equal(3, jobs.Count);
		Assert.Equal(created.Id, jobs[0].Id);
		Assert.NotNull(created.SwarmId);
		Assert.All(jobs, job => Assert.Equal(created.SwarmId, job.SwarmId));
		Assert.Equal([1, 2, 3], jobs.Select(job => job.InstanceNumber!.Value));
		Assert.Equal(
			["Polish the settings pages (1/3)", "Polish the settings pages (2/3)", "Polish the settings pages (3/3)"],
			jobs.Select(job => job.Title));
		Assert.All(jobs, job =>
		{
			Assert.Equal(provider.Id, job.ProviderId);
			Assert.Equal("opus", job.ModelUsed);
			Assert.Equal("high", job.ReasoningEffort);
			Assert.Equal(JobStatus.New, job.Status);
			Assert.Equal(created.GoalPrompt, job.GoalPrompt);
		});
	}

	[Fact]
	public async Task CreateAsync_WithOneInstanceOrAnAgent_CreatesASingleJob()
	{
		using var scope = _rootProvider.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
		var (project, provider) = await SeedAsync(dbContext);
		var service = new JobService(dbContext, scope.ServiceProvider);

		var single = await service.CreateAsync(new Job { ProjectId = project.Id, ProviderId = provider.Id, GoalPrompt = "Just one." });
		Assert.Null(single.SwarmId);
		Assert.Null(single.InstanceNumber);

		var setup = LocalEnvironmentSetup.CreateJob(project.Id, provider.Id, null, null);
		setup.RequestedInstances = 4;
		var setupJob = await service.CreateAsync(setup);
		Assert.Null(setupJob.SwarmId);

		Assert.Equal(2, await dbContext.Jobs.CountAsync());
	}

	[Fact]
	public async Task CreateAsync_CapsTheInstanceCount()
	{
		using var scope = _rootProvider.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
		var (project, provider) = await SeedAsync(dbContext);
		var service = new JobService(dbContext, scope.ServiceProvider);

		var manyItems = string.Join("\n", Enumerable.Range(1, 8).Select(item => $"- Item {item}"));
		var many = await service.CreateAsync(new Job { ProjectId = project.Id, ProviderId = provider.Id, GoalPrompt = manyItems, RequestedInstances = 50 });
		Assert.Equal(VibeSwarm.Shared.Validation.ValidationLimits.JobInstancesMax, await dbContext.Jobs.CountAsync(job => job.SwarmId == many.SwarmId));

		// Never more copies than the goal has items, and prose isn't split at all.
		var two = await service.CreateAsync(new Job { ProjectId = project.Id, ProviderId = provider.Id, GoalPrompt = "- One\n- Two", RequestedInstances = 4 });
		Assert.Equal(2, await dbContext.Jobs.CountAsync(job => job.SwarmId == two.SwarmId));
		var prose = await service.CreateAsync(new Job { ProjectId = project.Id, ProviderId = provider.Id, GoalPrompt = "Make it faster.", RequestedInstances = 3 });
		Assert.Null(prose.SwarmId);
		Assert.Equal(1, await dbContext.Jobs.CountAsync(job => job.GoalPrompt == "Make it faster."));
	}

	[Fact]
	public void Splitter_GivesEachInstanceANeighbouringRunOfItems()
	{
		const string goal = """
			Tidy the app:
			1. Header
			2. Footer
			   with the copyright line
			3. Sidebar
			4. Settings
			5. Login

			Keep the tests green.
			""";

		Assert.Equal(["Header", "Footer with the copyright line", "Sidebar", "Settings", "Login"], JobInstanceSplitter.GetListItems(goal));
		Assert.Equal(["Header", "Footer with the copyright line"], JobInstanceSplitter.GetPart(goal, 1, 3));
		Assert.Equal(["Sidebar", "Settings"], JobInstanceSplitter.GetPart(goal, 2, 3));
		Assert.Equal(["Login"], JobInstanceSplitter.GetPart(goal, 3, 3));
		Assert.Null(JobInstanceSplitter.GetPart(goal, 1, 6));
		Assert.Null(JobInstanceSplitter.GetPart("Make it faster.", 1, 2));
	}

	[Fact]
	public void InstancePrompt_NamesThisInstancesPartOrAsksItToTakeAShare()
	{
		var listed = PromptBuilder.BuildInstanceSystemPromptContext("- One\n- Two", 2, 2);
		Assert.Contains("instance 2 of 2", listed);
		Assert.Contains("- Two", listed);
		Assert.DoesNotContain("- One", listed);

		var prose = PromptBuilder.BuildInstanceSystemPromptContext("Make it faster.", 1, 3);
		Assert.Contains("do only part 1", prose);

		var followUp = PromptBuilder.BuildInstanceSystemPromptContext("- One\n- Two", 1, 2, isFollowUp: true);
		Assert.DoesNotContain("Your part", followUp);
		Assert.DoesNotContain("do only part", followUp);
		Assert.Contains("don't switch branches", followUp);
	}

	private static async Task<(Project Project, Provider Provider)> SeedAsync(VibeSwarmDbContext dbContext)
	{
		var provider = new Provider { Id = Guid.NewGuid(), Name = "Claude", Type = ProviderType.Claude, ConnectionMode = ProviderConnectionMode.CLI, IsEnabled = true, IsDefault = true };
		dbContext.Providers.Add(provider);
		dbContext.ProviderModels.Add(new ProviderModel { ProviderId = provider.Id, ModelId = "opus", IsAvailable = true, IsDefault = true });
		var project = new Project { Id = Guid.NewGuid(), Name = $"Project {Guid.NewGuid():N}", WorkingPath = "/tmp/instances" };
		dbContext.Projects.Add(project);
		await dbContext.SaveChangesAsync();
		return (project, provider);
	}

	public void Dispose()
	{
		_rootProvider.Dispose();
		_connection.Dispose();
	}
}
