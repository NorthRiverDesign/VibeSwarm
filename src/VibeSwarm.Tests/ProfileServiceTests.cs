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

	[Fact]
	public async Task InferenceProfile_TakesAListedModelAndNoReasoning_AndKeepsItsKind()
	{
		var (claude, _, _) = await SeedProvidersAsync();
		var grok = await SeedGrokAsync();
		await using var dbContext = CreateDbContext();
		var service = new ProfileService(dbContext);

		await Assert.ThrowsAsync<InvalidOperationException>(
			() => service.CreateAsync(new Profile { Name = "Both", ProviderId = claude.Id, InferenceProviderId = grok.Id }));
		await Assert.ThrowsAsync<InvalidOperationException>(
			() => service.CreateAsync(new Profile { Name = "Reasoning", InferenceProviderId = grok.Id, ReasoningEffort = "high" }));
		await Assert.ThrowsAsync<InvalidOperationException>(
			() => service.CreateAsync(new Profile { Name = "Unknown model", InferenceProviderId = grok.Id, ModelId = "grok-0" }));

		var research = await service.CreateAsync(new Profile { Name = "Research", InferenceProviderId = grok.Id, ModelId = "grok-4" });
		Assert.Equal("Grok", research.InferenceProvider?.Name);
		Assert.Null(research.ProviderId);

		var error = await Assert.ThrowsAsync<InvalidOperationException>(
			() => service.UpdateAsync(new Profile { Id = research.Id, Name = "Research", ProviderId = claude.Id }));
		Assert.Contains("inference", error.Message);
	}

	[Fact]
	public async Task EnsureProviderDefaults_GivesEveryProviderOneDefault_AndRunsAgainHarmlessly()
	{
		await SeedProvidersAsync();
		await SeedGrokAsync();
		await using (var dbContext = CreateDbContext())
		{
			// A profile already called "Claude" pushes the default to another name.
			dbContext.Profiles.Add(new Profile { Id = Guid.NewGuid(), Name = "Claude", ProviderId = (await dbContext.Providers.FirstAsync(p => p.Name == "Copilot")).Id });
			await dbContext.SaveChangesAsync();
			await ProfileService.EnsureProviderDefaultsAsync(dbContext);
			await ProfileService.EnsureProviderDefaultsAsync(dbContext);
		}

		await using (var dbContext = CreateDbContext())
		{
			var defaults = await dbContext.Profiles.Where(profile => profile.IsProviderDefault).ToListAsync();
			Assert.Equal(4, defaults.Count);
			Assert.Contains(defaults, profile => profile.Name == "Claude default");
			Assert.Contains(defaults, profile => profile.Name == "Grok" && profile.InferenceProviderId != null);
			Assert.All(defaults, profile => Assert.Null(profile.ModelId));

			var service = new ProfileService(dbContext);
			var claudeDefault = defaults.Single(profile => profile.Name == "Claude default");
			await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync(claudeDefault.Id));
			var copilotId = (await dbContext.Providers.FirstAsync(p => p.Name == "Copilot")).Id;
			await Assert.ThrowsAsync<InvalidOperationException>(
				() => service.UpdateAsync(new Profile { Id = claudeDefault.Id, Name = "Claude default", ProviderId = copilotId }));

			// Its name and model can change.
			var renamed = await service.UpdateAsync(new Profile { Id = claudeDefault.Id, Name = "Claude Opus", ProviderId = claudeDefault.ProviderId, ModelId = "opus" });
			Assert.True(renamed.IsProviderDefault);
			Assert.Equal("opus", renamed.ModelId);
		}
	}

	[Fact]
	public async Task AgentProfile_FillsItsDefaults_AndItsProjectsFollowAChange()
	{
		var (claude, copilot, _) = await SeedProvidersAsync();
		Guid opusId, gptId, agentId, followingId, customId;
		await using (var dbContext = CreateDbContext())
		{
			var profiles = new ProfileService(dbContext);
			opusId = (await profiles.CreateAsync(new Profile { Name = "Opus", ProviderId = claude.Id, ModelId = "opus", ReasoningEffort = "high" })).Id;
			gptId = (await profiles.CreateAsync(new Profile { Name = "GPT", ProviderId = copilot.Id, ModelId = "gpt-5" })).Id;

			var agent = await new AgentService(dbContext).CreateAsync(new Agent { Name = "Lead dev", ProfileId = opusId, DefaultProviderId = copilot.Id, DefaultModelId = "stale" });
			agentId = agent.Id;
			Assert.Equal((claude.Id, "opus", "high"), (agent.DefaultProviderId!.Value, agent.DefaultModelId, agent.DefaultReasoningEffort));

			var following = NewProject("Following");
			following.AgentAssignments.Add(new ProjectAgent { AgentId = agentId, ProviderId = claude.Id, PreferredModelId = "opus", ProfileId = opusId });
			var custom = NewProject("Custom");
			custom.AgentAssignments.Add(new ProjectAgent { AgentId = agentId, ProviderId = claude.Id, PreferredModelId = "sonnet" });
			dbContext.Projects.AddRange(following, custom);
			await dbContext.SaveChangesAsync();
			followingId = following.Id;
			customId = custom.Id;
		}

		await using (var dbContext = CreateDbContext())
		{
			var agent = await dbContext.Agents.AsNoTracking().SingleAsync(candidate => candidate.Id == agentId);
			agent.ProfileId = gptId;
			await new AgentService(dbContext).UpdateAsync(agent);
		}

		await using (var dbContext = CreateDbContext())
		{
			var following = await dbContext.ProjectAgents.SingleAsync(assignment => assignment.ProjectId == followingId);
			Assert.Equal((gptId, copilot.Id, "gpt-5"), (following.ProfileId!.Value, following.ProviderId, following.PreferredModelId));
			var custom = await dbContext.ProjectAgents.SingleAsync(assignment => assignment.ProjectId == customId);
			Assert.Equal((null, claude.Id, "sonnet"), (custom.ProfileId, custom.ProviderId, custom.PreferredModelId));

			// Editing the profile reaches the agent and its linked assignment.
			await new ProfileService(dbContext).UpdateAsync(new Profile { Id = gptId, Name = "GPT", ProviderId = copilot.Id, ModelId = null, ReasoningEffort = "low" });
		}

		await using (var dbContext = CreateDbContext())
		{
			var agent = await dbContext.Agents.SingleAsync(candidate => candidate.Id == agentId);
			Assert.Equal((copilot.Id, (string?)null, "low"), (agent.DefaultProviderId!.Value, agent.DefaultModelId, agent.DefaultReasoningEffort));
			var following = await dbContext.ProjectAgents.SingleAsync(assignment => assignment.ProjectId == followingId);
			Assert.Equal("low", following.PreferredReasoningEffort);
		}
	}

	[Fact]
	public async Task ProjectSave_TakesAnInferenceReviewer_AndKeepsOnlyHelpersThatCanAnswer()
	{
		var (claude, _, openCode) = await SeedProvidersAsync();
		var grok = await SeedGrokAsync();
		Guid researchId, decisionsId, openCodeProfileId;
		await using (var dbContext = CreateDbContext())
		{
			var service = new ProfileService(dbContext);
			researchId = (await service.CreateAsync(new Profile { Name = "Grok research", InferenceProviderId = grok.Id, ModelId = "grok-4" })).Id;
			decisionsId = (await service.CreateAsync(new Profile { Name = "Claude decisions", ProviderId = claude.Id })).Id;
			openCodeProfileId = (await service.CreateAsync(new Profile { Name = "OpenCode", ProviderId = openCode.Id })).Id;
		}

		Project saved;
		await using (var dbContext = CreateDbContext())
		{
			var project = NewProject("Helped");
			project.ReviewEnabled = true;
			project.ReviewProfileId = researchId;
			project.ReviewProviderId = claude.Id;
			project.DecisionsProfileId = openCodeProfileId;
			project.ResearchProfileId = researchId;
			saved = await CreateProjectService(dbContext).CreateAsync(project);
		}

		await using (var dbContext = CreateDbContext())
		{
			var project = await dbContext.Projects.SingleAsync(p => p.Id == saved.Id);
			Assert.Equal((null, grok.Id, "grok-4"), (project.ReviewProviderId, project.ReviewInferenceProviderId!.Value, project.ReviewModelId));
			// OpenCode can't answer read-only, so it can't help.
			Assert.Null(project.DecisionsProfileId);
			Assert.Equal(researchId, project.ResearchProfileId);

			project.DecisionsProfileId = decisionsId;
			await dbContext.SaveChangesAsync();
			await new ProfileService(dbContext).DeleteAsync(decisionsId);
			Assert.Null((await dbContext.Projects.AsNoTracking().SingleAsync(p => p.Id == saved.Id)).DecisionsProfileId);
		}
	}

	private async Task<InferenceProvider> SeedGrokAsync()
	{
		var grok = new InferenceProvider { Id = Guid.NewGuid(), Name = "Grok", ProviderType = VibeSwarm.Shared.Inference.InferenceProviderType.Grok, Endpoint = "https://api.x.ai", IsEnabled = true };
		await using var dbContext = CreateDbContext();
		dbContext.InferenceProviders.Add(grok);
		dbContext.InferenceModels.Add(new InferenceModel { InferenceProviderId = grok.Id, ModelId = "grok-4", IsAvailable = true });
		await dbContext.SaveChangesAsync();
		return grok;
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
