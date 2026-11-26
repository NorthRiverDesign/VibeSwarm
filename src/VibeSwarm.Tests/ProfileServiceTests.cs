using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class ProfileServiceTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;
	private readonly string _dataProtectionDirectory = Path.Combine(Path.GetTempPath(), $"vibeswarm-profile-tests-{Guid.NewGuid():N}");

	public ProfileServiceTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		_dbOptions = new DbContextOptionsBuilder<VibeSwarmDbContext>().UseSqlite(_connection).Options;
		using var dbContext = CreateDbContext();
		dbContext.Database.EnsureCreated();
	}

	[Fact]
	public async Task CreateAsync_RejectsADuplicateNameWhateverItsCase()
	{
		var (claude, _, _) = await SeedProvidersAsync();
		await using var dbContext = CreateDbContext();
		var service = new ProfileService(dbContext);
		await service.CreateAsync(new Profile { Name = "Deep thinker", ProviderId = claude.Id });

		var error = await Assert.ThrowsAsync<InvalidOperationException>(
			() => service.CreateAsync(new Profile { Name = "deep THINKER", ProviderId = claude.Id }));
		Assert.Contains("already exists", error.Message);
	}

	[Fact]
	public async Task CreateAsync_NeedsAProviderAndAReasoningLevelItSupports()
	{
		var (claude, _, _) = await SeedProvidersAsync();
		await using var dbContext = CreateDbContext();
		var service = new ProfileService(dbContext);

		await Assert.ThrowsAsync<InvalidOperationException>(
			() => service.CreateAsync(new Profile { Name = "No provider" }));
		await Assert.ThrowsAsync<InvalidOperationException>(
			() => service.CreateAsync(new Profile { Name = "Bad reasoning", ProviderId = claude.Id, ReasoningEffort = "ludicrous" }));
		await Assert.ThrowsAsync<InvalidOperationException>(
			() => service.CreateAsync(new Profile { Name = "Unknown model", ProviderId = claude.Id, ModelId = "gpt-5" }));

		var created = await service.CreateAsync(new Profile { Name = "  Careful  ", ProviderId = claude.Id, ModelId = " opus ", ReasoningEffort = "high" });
		Assert.Equal("Careful", created.Name);
		Assert.Equal("opus", created.ModelId);
		Assert.Equal("high", created.ReasoningEffort);
		Assert.Equal("Claude", created.Provider?.Name);
	}

	[Fact]
	public async Task UpdateAsync_WritesTheNewValuesThroughToEveryLinkedSetting()
	{
		var (claude, copilot, _) = await SeedProvidersAsync();
		Guid profileId;
		Guid projectId;
		await using (var dbContext = CreateDbContext())
		{
			var service = new ProfileService(dbContext);
			profileId = (await service.CreateAsync(new Profile { Name = "Thinker", ProviderId = claude.Id, ModelId = "sonnet", ReasoningEffort = "medium" })).Id;
			var project = NewProject("Linked");
			project.PlanningProfileId = profileId;
			project.ReviewProfileId = profileId;
			project.LeadProfileId = profileId;
			project.ProviderSelections.Add(new ProjectProvider { ProviderId = claude.Id, ProfileId = profileId, PreferredModelId = "sonnet", Priority = 0 });
			dbContext.Projects.Add(project);
			await dbContext.SaveChangesAsync();
			projectId = project.Id;
		}

		await using (var dbContext = CreateDbContext())
		{
			await new ProfileService(dbContext).UpdateAsync(new Profile { Id = profileId, Name = "Thinker", ProviderId = copilot.Id, ModelId = "gpt-5", ReasoningEffort = "high" });
		}

		await using (var dbContext = CreateDbContext())
		{
			var project = await dbContext.Projects.Include(p => p.ProviderSelections).SingleAsync(p => p.Id == projectId);
			Assert.Equal((copilot.Id, "gpt-5", "high"), (project.PlanningProviderId!.Value, project.PlanningModelId, project.PlanningReasoningEffort));
			Assert.Equal((copilot.Id, "gpt-5", "high"), (project.ReviewProviderId!.Value, project.ReviewModelId, project.ReviewReasoningEffort));
			Assert.Equal((copilot.Id, "gpt-5", "high"), (project.LeadProviderId!.Value, project.LeadModelId, project.LeadReasoningEffort));
			var entry = Assert.Single(project.ProviderSelections);
			Assert.Equal((copilot.Id, "gpt-5", "high", (Guid?)profileId), (entry.ProviderId, entry.PreferredModelId, entry.PreferredReasoningEffort, entry.ProfileId));
		}
	}

	[Fact]
	public async Task UpdateAsync_UnlinksSettingsTheNewProviderCannotFill()
	{
		var (claude, copilot, openCode) = await SeedProvidersAsync();
		Guid profileId;
		Guid projectId;
		await using (var dbContext = CreateDbContext())
		{
			var service = new ProfileService(dbContext);
			profileId = (await service.CreateAsync(new Profile { Name = "Planner", ProviderId = claude.Id, ModelId = "opus" })).Id;
			var project = NewProject("Mixed");
			project.PlanningProfileId = profileId;
			project.PlanningProviderId = claude.Id;
			project.PlanningModelId = "opus";
			project.ProviderSelections.Add(new ProjectProvider { ProviderId = claude.Id, ProfileId = profileId, PreferredModelId = "opus", Priority = 0 });
			project.ProviderSelections.Add(new ProjectProvider { ProviderId = openCode.Id, PreferredModelId = "other", Priority = 1 });
			dbContext.Projects.Add(project);
			await dbContext.SaveChangesAsync();
			projectId = project.Id;
		}

		await using (var dbContext = CreateDbContext())
		{
			await new ProfileService(dbContext).UpdateAsync(new Profile { Id = profileId, Name = "Planner", ProviderId = openCode.Id, ModelId = "big" });
		}

		await using (var dbContext = CreateDbContext())
		{
			var project = await dbContext.Projects.Include(p => p.ProviderSelections).SingleAsync(p => p.Id == projectId);

			// OpenCode can't plan, so planning keeps Claude and drops the link.
			Assert.Null(project.PlanningProfileId);
			Assert.Equal((claude.Id, "opus"), (project.PlanningProviderId!.Value, project.PlanningModelId));

			// The project already lists OpenCode, so the linked Claude entry stays as it was, unlinked.
			var claudeEntry = project.ProviderSelections.Single(entry => entry.ProviderId == claude.Id);
			Assert.Null(claudeEntry.ProfileId);
			Assert.Equal("opus", claudeEntry.PreferredModelId);
			Assert.Equal("other", project.ProviderSelections.Single(entry => entry.ProviderId == openCode.Id).PreferredModelId);
		}

		Assert.NotEqual(Guid.Empty, copilot.Id);
	}

	[Fact]
	public async Task UpdateAsync_LeavesLinksToOtherProfilesAlone()
	{
		var (claude, copilot, _) = await SeedProvidersAsync();
		Guid editedId;
		Guid otherId;
		Guid projectId;
		await using (var dbContext = CreateDbContext())
		{
			var service = new ProfileService(dbContext);
			editedId = (await service.CreateAsync(new Profile { Name = "Reviewer", ProviderId = claude.Id })).Id;
			otherId = (await service.CreateAsync(new Profile { Name = "Planner", ProviderId = copilot.Id, ModelId = "gpt-5" })).Id;
			var project = NewProject("Two profiles");
			project.ReviewProfileId = editedId;
			project.PlanningProfileId = otherId;
			project.PlanningProviderId = copilot.Id;
			project.PlanningModelId = "gpt-5";
			dbContext.Projects.Add(project);
			await dbContext.SaveChangesAsync();
			projectId = project.Id;
		}

		await using (var dbContext = CreateDbContext())
		{
			await new ProfileService(dbContext).UpdateAsync(new Profile { Id = editedId, Name = "Reviewer", ProviderId = claude.Id, ModelId = "opus" });
		}

		await using (var dbContext = CreateDbContext())
		{
			var project = await dbContext.Projects.SingleAsync(p => p.Id == projectId);
			Assert.Equal(otherId, project.PlanningProfileId);
			Assert.Equal((copilot.Id, "gpt-5"), (project.PlanningProviderId!.Value, project.PlanningModelId));
			Assert.Equal(editedId, project.ReviewProfileId);
			Assert.Equal((claude.Id, "opus"), (project.ReviewProviderId!.Value, project.ReviewModelId));
		}
	}

	[Fact]
	public async Task DeleteAsync_UnlinksSettingsAndKeepsTheirValues()
	{
		var (claude, _, _) = await SeedProvidersAsync();
		Guid profileId;
		Guid projectId;
		await using (var dbContext = CreateDbContext())
		{
			var service = new ProfileService(dbContext);
			profileId = (await service.CreateAsync(new Profile { Name = "Lead", ProviderId = claude.Id, ModelId = "opus" })).Id;
			var project = NewProject("Deleting");
			project.LeadProfileId = profileId;
			project.LeadProviderId = claude.Id;
			project.LeadModelId = "opus";
			project.ProviderSelections.Add(new ProjectProvider { ProviderId = claude.Id, ProfileId = profileId, PreferredModelId = "opus", Priority = 0 });
			dbContext.Projects.Add(project);
			await dbContext.SaveChangesAsync();
			projectId = project.Id;
		}

		await using (var dbContext = CreateDbContext())
		{
			await new ProfileService(dbContext).DeleteAsync(profileId);
		}

		await using (var dbContext = CreateDbContext())
		{
			Assert.False(await dbContext.Profiles.AnyAsync());
			var project = await dbContext.Projects.Include(p => p.ProviderSelections).SingleAsync(p => p.Id == projectId);
			Assert.Null(project.LeadProfileId);
			Assert.Equal((claude.Id, "opus"), (project.LeadProviderId!.Value, project.LeadModelId));
			var entry = Assert.Single(project.ProviderSelections);
			Assert.Null(entry.ProfileId);
			Assert.Equal("opus", entry.PreferredModelId);
		}
	}

	[Fact]
	public async Task ProjectSave_TakesLinkedValuesFromTheProfileAndDropsMissingLinks()
	{
		var (claude, copilot, _) = await SeedProvidersAsync();
		Guid profileId;
		await using (var dbContext = CreateDbContext())
		{
			profileId = (await new ProfileService(dbContext).CreateAsync(
				new Profile { Name = "Strong", ProviderId = claude.Id, ModelId = "opus", ReasoningEffort = "high" })).Id;
		}

		Project saved;
		await using (var dbContext = CreateDbContext())
		{
			var project = NewProject("Saved");
			project.PlanningEnabled = true;
			project.PlanningProfileId = profileId;
			// Stale values from the client lose to the profile's.
			project.PlanningProviderId = copilot.Id;
			project.PlanningModelId = "stale";
			project.ReviewEnabled = true;
			project.ReviewProfileId = profileId;
			project.LeadProfileId = Guid.NewGuid();
			project.LeadProviderId = copilot.Id;
			project.ProviderSelections.Add(new ProjectProvider { ProviderId = copilot.Id, ProfileId = profileId, Priority = 0 });
			saved = await CreateProjectService(dbContext).CreateAsync(project);
		}

		await using (var dbContext = CreateDbContext())
		{
			var project = await dbContext.Projects.Include(p => p.ProviderSelections).SingleAsync(p => p.Id == saved.Id);
			Assert.Equal((claude.Id, "opus", "high"), (project.PlanningProviderId!.Value, project.PlanningModelId, project.PlanningReasoningEffort));
			Assert.Equal((claude.Id, "opus", "high"), (project.ReviewProviderId!.Value, project.ReviewModelId, project.ReviewReasoningEffort));
			Assert.Null(project.ReviewInferenceProviderId);
			Assert.Null(project.LeadProfileId);
			Assert.Equal(copilot.Id, project.LeadProviderId);
			var entry = Assert.Single(project.ProviderSelections);
			Assert.Equal((claude.Id, "opus", "high", (Guid?)profileId), (entry.ProviderId, entry.PreferredModelId, entry.PreferredReasoningEffort, entry.ProfileId));
		}
	}

	private async Task<(Provider Claude, Provider Copilot, Provider OpenCode)> SeedProvidersAsync()
	{
		var claude = new Provider { Id = Guid.NewGuid(), Name = "Claude", Type = ProviderType.Claude, ConnectionMode = ProviderConnectionMode.CLI, IsEnabled = true };
		var copilot = new Provider { Id = Guid.NewGuid(), Name = "Copilot", Type = ProviderType.Copilot, ConnectionMode = ProviderConnectionMode.CLI, IsEnabled = true };
		var openCode = new Provider { Id = Guid.NewGuid(), Name = "OpenCode", Type = ProviderType.OpenCode, ConnectionMode = ProviderConnectionMode.CLI, IsEnabled = true };
		await using var dbContext = CreateDbContext();
		dbContext.Providers.AddRange(claude, copilot, openCode);
		foreach (var (provider, model) in new[] { (claude, "opus"), (claude, "sonnet"), (copilot, "gpt-5"), (openCode, "big"), (openCode, "other") })
		{
			dbContext.ProviderModels.Add(new ProviderModel { ProviderId = provider.Id, ModelId = model, IsAvailable = true });
		}
		await dbContext.SaveChangesAsync();
		return (claude, copilot, openCode);
	}

	private static Project NewProject(string name) => new()
	{
		Id = Guid.NewGuid(),
		Name = name,
		WorkingPath = $"/tmp/{Guid.NewGuid():N}",
	};

	private ProjectService CreateProjectService(VibeSwarmDbContext dbContext)
	{
		var credentials = new ProjectEnvironmentCredentialService(DataProtectionProvider.Create(new DirectoryInfo(_dataProtectionDirectory)));
		return new ProjectService(dbContext, credentials, new NoOpVersionControlService());
	}

	private VibeSwarmDbContext CreateDbContext() => new(_dbOptions);

	public void Dispose()
	{
		_connection.Dispose();
		if (Directory.Exists(_dataProtectionDirectory))
		{
			Directory.Delete(_dataProtectionDirectory, recursive: true);
		}
	}

	private sealed class NoOpVersionControlService : FakeVersionControlServiceBase
	{
	}
}
