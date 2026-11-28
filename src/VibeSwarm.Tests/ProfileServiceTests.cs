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
	public async Task UpdateAsync_RefusesAMoveASettingUsingTheProfileCannotFollow()
	{
		var (claude, _, openCode) = await SeedProvidersAsync();
		Guid plannerId;
		Guid builderId;
		Guid projectId;
		await using (var dbContext = CreateDbContext())
		{
			var service = new ProfileService(dbContext);
			plannerId = (await service.CreateAsync(new Profile { Name = "Planner", ProviderId = claude.Id, ModelId = "opus" })).Id;
			builderId = (await service.CreateAsync(new Profile { Name = "Builder", ProviderId = claude.Id, ModelId = "sonnet" })).Id;
			var project = NewProject("Mixed");
			project.PlanningEnabled = true;
			project.PlanningProfileId = plannerId;
			project.PlanningProviderId = claude.Id;
			project.PlanningModelId = "opus";
			project.ProviderSelections.Add(new ProjectProvider { ProviderId = claude.Id, ProfileId = builderId, PreferredModelId = "sonnet", Priority = 0 });
			project.ProviderSelections.Add(new ProjectProvider { ProviderId = openCode.Id, PreferredModelId = "other", Priority = 1 });
			dbContext.Projects.Add(project);
			await dbContext.SaveChangesAsync();
			projectId = project.Id;
		}

		await using (var dbContext = CreateDbContext())
		{
			var service = new ProfileService(dbContext);

			// OpenCode can't plan.
			var cannotPlan = await Assert.ThrowsAsync<InvalidOperationException>(
				() => service.UpdateAsync(new Profile { Id = plannerId, Name = "Planner", ProviderId = openCode.Id, ModelId = "big" }));
			Assert.Contains("Plan first in Mixed", cannotPlan.Message);

			// The project already lists OpenCode, and it lists a provider once.
			var alreadyListed = await Assert.ThrowsAsync<InvalidOperationException>(
				() => service.UpdateAsync(new Profile { Id = builderId, Name = "Builder", ProviderId = openCode.Id, ModelId = "big" }));
			Assert.Contains("Mixed", alreadyListed.Message);
			Assert.Contains("already lists OpenCode", alreadyListed.Message);
		}

		await using (var dbContext = CreateDbContext())
		{
			// Nothing moved, and nothing was unlinked.
			var project = await dbContext.Projects.Include(p => p.ProviderSelections).SingleAsync(p => p.Id == projectId);
			Assert.Equal((plannerId, claude.Id, "opus"), (project.PlanningProfileId!.Value, project.PlanningProviderId!.Value, project.PlanningModelId));
			var claudeEntry = project.ProviderSelections.Single(entry => entry.ProviderId == claude.Id);
			Assert.Equal((builderId, "sonnet"), (claudeEntry.ProfileId!.Value, claudeEntry.PreferredModelId));
			Assert.Equal(claude.Id, (await dbContext.Profiles.SingleAsync(profile => profile.Id == plannerId)).ProviderId);

			// Model and reasoning alone can always change.
			await new ProfileService(dbContext).UpdateAsync(new Profile { Id = plannerId, Name = "Planner", ProviderId = claude.Id, ModelId = "sonnet", ReasoningEffort = "high" });
			var replanned = await dbContext.Projects.AsNoTracking().SingleAsync(p => p.Id == projectId);
			Assert.Equal(("sonnet", "high"), (replanned.PlanningModelId, replanned.PlanningReasoningEffort));
		}
	}

	[Fact]
	public async Task UpdateAsync_IsNotHeldBackByARoleThatIsSwitchedOff()
	{
		var (claude, _, openCode) = await SeedProvidersAsync();
		Guid profileId;
		await using (var dbContext = CreateDbContext())
		{
			profileId = (await new ProfileService(dbContext).CreateAsync(new Profile { Name = "Was the planner", ProviderId = claude.Id, ModelId = "opus" })).Id;
			// Planning and review are off; they only remember the profile they last used.
			var project = NewProject("Quiet");
			project.PlanningProfileId = profileId;
			project.PlanningProviderId = claude.Id;
			project.ReviewProfileId = profileId;
			project.ReviewProviderId = claude.Id;
			dbContext.Projects.Add(project);
			await dbContext.SaveChangesAsync();
		}

		await using (var dbContext = CreateDbContext())
		{
			var moved = await new ProfileService(dbContext).UpdateAsync(new Profile { Id = profileId, Name = "Was the planner", ProviderId = openCode.Id, ModelId = "big" });
			Assert.Equal(openCode.Id, moved.ProviderId);

			// Saving that project again isn't refused either.
			var project = await dbContext.Projects.AsNoTracking().SingleAsync();
			project.Description = "Edited";
			await CreateProjectService(dbContext).UpdateAsync(project);
		}
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
	public async Task DeleteAsync_MovesEverySettingToTheProvidersDefaultProfile()
	{
		var (claude, _, _) = await SeedProvidersAsync();
		Guid profileId;
		Guid defaultId;
		Guid projectId;
		Guid templateId;
		await using (var dbContext = CreateDbContext())
		{
			await ProfileService.EnsureProviderDefaultsAsync(dbContext);
			defaultId = (await dbContext.Profiles.SingleAsync(profile => profile.ProviderId == claude.Id && profile.IsProviderDefault)).Id;
			var service = new ProfileService(dbContext);
			profileId = (await service.CreateAsync(new Profile { Name = "Lead", ProviderId = claude.Id, ModelId = "opus", ReasoningEffort = "high" })).Id;
			var project = NewProject("Deleting");
			project.LeadProfileId = profileId;
			project.LeadProviderId = claude.Id;
			project.LeadModelId = "opus";
			project.LeadReasoningEffort = "high";
			project.ResearchProfileId = profileId;
			project.ProviderSelections.Add(new ProjectProvider { ProviderId = claude.Id, ProfileId = profileId, PreferredModelId = "opus", PreferredReasoningEffort = "high", Priority = 0 });
			dbContext.Projects.Add(project);
			var template = new JobTemplate { Id = Guid.NewGuid(), Name = "Tidy", GoalPrompt = "Tidy up", ProviderId = claude.Id, ModelId = "opus", ReasoningEffort = "high", ProfileId = profileId };
			dbContext.JobTemplates.Add(template);
			await dbContext.SaveChangesAsync();
			projectId = project.Id;
			templateId = template.Id;
		}

		await using (var dbContext = CreateDbContext())
		{
			await new ProfileService(dbContext).DeleteAsync(profileId);
		}

		await using (var dbContext = CreateDbContext())
		{
			Assert.False(await dbContext.Profiles.AnyAsync(profile => profile.Id == profileId));

			// Each setting now follows the default profile, which leaves model and reasoning open.
			var project = await dbContext.Projects.Include(p => p.ProviderSelections).SingleAsync(p => p.Id == projectId);
			Assert.Equal((defaultId, claude.Id, null, null), (project.LeadProfileId!.Value, project.LeadProviderId!.Value, project.LeadModelId, project.LeadReasoningEffort));
			Assert.Equal(defaultId, project.ResearchProfileId);
			var entry = Assert.Single(project.ProviderSelections);
			Assert.Equal(((Guid?)defaultId, claude.Id, null, null), (entry.ProfileId, entry.ProviderId, entry.PreferredModelId, entry.PreferredReasoningEffort));
			var template = await dbContext.JobTemplates.SingleAsync(item => item.Id == templateId);
			Assert.Equal(((Guid?)defaultId, (Guid?)claude.Id, null, null), (template.ProfileId, template.ProviderId, template.ModelId, template.ReasoningEffort));
		}
	}

	[Fact]
	public async Task DeleteAsync_WithNoDefaultProfileToMoveTo_UnlinksAndKeepsTheValues()
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
	public async Task ProjectSave_TakesLinkedValuesFromTheProfile_AndGivesAnUnlinkedSettingItsMatchingProfile()
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
			var entry = Assert.Single(project.ProviderSelections);
			Assert.Equal((claude.Id, "opus", "high", (Guid?)profileId), (entry.ProviderId, entry.PreferredModelId, entry.PreferredReasoningEffort, entry.ProfileId));

			// The Lead came pointing at a profile that is gone. It keeps what it was sent, under a
			// profile made to hold exactly that.
			Assert.Equal(copilot.Id, project.LeadProviderId);
			var leadProfile = await dbContext.Profiles.SingleAsync(profile => profile.Id == project.LeadProfileId);
			Assert.Equal((copilot.Id, null, null), (leadProfile.ProviderId!.Value, leadProfile.ModelId, leadProfile.ReasoningEffort));
			Assert.Equal("Copilot (provider defaults)", leadProfile.Name);
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
	public async Task ProjectSave_TakesAnInferenceReviewer_AndRefusesAHelperThatCannotAnswer()
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

		await using (var dbContext = CreateDbContext())
		{
			// OpenCode can't answer read-only, so it can't help.
			var refused = NewProject("Refused");
			refused.DecisionsProfileId = openCodeProfileId;
			var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateProjectService(dbContext).CreateAsync(refused));
			Assert.Contains("Decisions", error.Message);
		}

		Project saved;
		await using (var dbContext = CreateDbContext())
		{
			var project = NewProject("Helped");
			project.ReviewEnabled = true;
			project.ReviewProfileId = researchId;
			project.ReviewProviderId = claude.Id;
			project.ResearchProfileId = researchId;
			saved = await CreateProjectService(dbContext).CreateAsync(project);
		}

		await using (var dbContext = CreateDbContext())
		{
			var project = await dbContext.Projects.SingleAsync(p => p.Id == saved.Id);
			Assert.Equal((null, grok.Id, "grok-4"), (project.ReviewProviderId, project.ReviewInferenceProviderId!.Value, project.ReviewModelId));
			Assert.Equal(researchId, project.ResearchProfileId);

			project.DecisionsProfileId = decisionsId;
			await dbContext.SaveChangesAsync();
			await new ProfileService(dbContext).DeleteAsync(decisionsId);
			Assert.Null((await dbContext.Projects.AsNoTracking().SingleAsync(p => p.Id == saved.Id)).DecisionsProfileId);
		}
	}

	[Fact]
	public async Task EnsureSettingsHaveProfiles_LinksEachSettingToTheProfileHoldingItsValues()
	{
		var (claude, copilot, _) = await SeedProvidersAsync();
		var grok = await SeedGrokAsync();
		Guid projectId, offProjectId, pinnedTemplateId, openTemplateId;
		await using (var dbContext = CreateDbContext())
		{
			await ProfileService.EnsureProviderDefaultsAsync(dbContext);

			var project = NewProject("Older");
			project.ProviderSelections.Add(new ProjectProvider { ProviderId = claude.Id, Priority = 0 });
			project.ProviderSelections.Add(new ProjectProvider { ProviderId = copilot.Id, PreferredModelId = "gpt-5", PreferredReasoningEffort = "high", Priority = 1 });
			project.PlanningEnabled = true;
			project.PlanningProviderId = claude.Id;
			project.PlanningModelId = "opus";
			project.ReviewEnabled = true;
			project.ReviewInferenceProviderId = grok.Id;
			project.ReviewModelId = "grok-4";
			project.LeadProviderId = copilot.Id;
			project.LeadModelId = "gpt-5";
			project.LeadReasoningEffort = "high";
			project.DecisionsProfileId = Guid.NewGuid();
			dbContext.Projects.Add(project);

			var off = NewProject("Switched off");
			off.PlanningProviderId = claude.Id;
			off.PlanningModelId = "sonnet";
			off.ReviewEnabled = true;
			dbContext.Projects.Add(off);

			var pinned = new JobTemplate { Id = Guid.NewGuid(), Name = "Pinned", GoalPrompt = "Tidy up", ProviderId = copilot.Id, ModelId = "gpt-5", ReasoningEffort = "high" };
			var open = new JobTemplate { Id = Guid.NewGuid(), Name = "Open", GoalPrompt = "Tidy up" };
			dbContext.JobTemplates.AddRange(pinned, open);
			await dbContext.SaveChangesAsync();
			(projectId, offProjectId, pinnedTemplateId, openTemplateId) = (project.Id, off.Id, pinned.Id, open.Id);
		}

		await using (var dbContext = CreateDbContext())
		{
			await ProfileService.EnsureSettingsHaveProfilesAsync(dbContext);
		}

		int profileCount;
		await using (var dbContext = CreateDbContext())
		{
			var profiles = await dbContext.Profiles.AsNoTracking().ToListAsync();
			profileCount = profiles.Count;
			Profile ById(Guid? id) => profiles.Single(profile => profile.Id == id);

			var project = await dbContext.Projects.Include(p => p.ProviderSelections).SingleAsync(p => p.Id == projectId);

			// A setting that leaves model and reasoning open is what the default profile says.
			var claudeEntry = project.ProviderSelections.Single(entry => entry.ProviderId == claude.Id);
			Assert.True(ById(claudeEntry.ProfileId).IsProviderDefault);

			// Values no profile holds get one named after them.
			var copilotEntry = project.ProviderSelections.Single(entry => entry.ProviderId == copilot.Id);
			Assert.Equal("Copilot · gpt-5 · high", ById(copilotEntry.ProfileId).Name);
			Assert.Equal("Claude · opus", ById(project.PlanningProfileId).Name);
			var reviewer = ById(project.ReviewProfileId);
			Assert.Equal(("Grok · grok-4", (Guid?)grok.Id, "grok-4"), (reviewer.Name, reviewer.InferenceProviderId, reviewer.ModelId));

			// One profile serves every setting holding the same values.
			Assert.Equal(copilotEntry.ProfileId, project.LeadProfileId);
			Assert.Equal(copilotEntry.ProfileId, (await dbContext.JobTemplates.SingleAsync(template => template.Id == pinnedTemplateId)).ProfileId);

			// A template that leaves the choice to each project stays that way, and a link to a
			// profile that is gone is cleared.
			Assert.Null((await dbContext.JobTemplates.SingleAsync(template => template.Id == openTemplateId)).ProfileId);
			Assert.Null(project.DecisionsProfileId);

			// Planning that is off keeps its old values without a profile made for them, and a
			// reviewer switched on with nothing to run on is switched off.
			var off = await dbContext.Projects.SingleAsync(p => p.Id == offProjectId);
			Assert.Null(off.PlanningProfileId);
			Assert.DoesNotContain(profiles, profile => profile.ModelId == "sonnet");
			Assert.False(off.ReviewEnabled);
		}

		await using (var dbContext = CreateDbContext())
		{
			await ProfileService.EnsureSettingsHaveProfilesAsync(dbContext);
			Assert.Equal(profileCount, await dbContext.Profiles.CountAsync());
		}
	}

	[Fact]
	public async Task EnsureSettingsHaveProfiles_TurnsAnOlderProviderPickIntoWhatItRan()
	{
		var (claude, copilot, _) = await SeedProvidersAsync();
		Guid projectId, sameAsProject, withModel, loopWithModel, loopOnFallback, finishedLoop;
		await using (var dbContext = CreateDbContext())
		{
			await ProfileService.EnsureProviderDefaultsAsync(dbContext);
			// The project builds on Claude with opus at high reasoning, then falls back to Copilot.
			var project = NewProject("Older picks");
			project.ProviderSelections.Add(new ProjectProvider { ProviderId = claude.Id, PreferredModelId = "opus", PreferredReasoningEffort = "high", Priority = 0 });
			project.ProviderSelections.Add(new ProjectProvider { ProviderId = copilot.Id, Priority = 1 });
			project.IdeasProcessingActive = true;
			project.IdeasProcessingProviderId = claude.Id;
			dbContext.Projects.Add(project);
			JobSchedule Schedule(string? modelId) => new() { Id = Guid.NewGuid(), ProjectId = project.Id, Prompt = "Tidy", ProviderId = claude.Id, ModelId = modelId };
			IterationLoop Loop(Guid providerId, string? modelId, IterationLoopStatus status) => new() { Id = Guid.NewGuid(), ProjectId = project.Id, ProviderId = providerId, ModelId = modelId, Status = status };
			var schedules = new[] { Schedule(null), Schedule("sonnet") };
			var loops = new[] { Loop(claude.Id, "opus", IterationLoopStatus.Running), Loop(copilot.Id, null, IterationLoopStatus.Paused), Loop(claude.Id, "sonnet", IterationLoopStatus.Stopped) };
			dbContext.JobSchedules.AddRange(schedules);
			dbContext.IterationLoops.AddRange(loops);
			await dbContext.SaveChangesAsync();
			(projectId, sameAsProject, withModel) = (project.Id, schedules[0].Id, schedules[1].Id);
			(loopWithModel, loopOnFallback, finishedLoop) = (loops[0].Id, loops[1].Id, loops[2].Id);
		}

		await using (var dbContext = CreateDbContext())
		{
			await ProfileService.EnsureSettingsHaveProfilesAsync(dbContext);
		}

		await using (var dbContext = CreateDbContext())
		{
			var profiles = await dbContext.Profiles.AsNoTracking().ToListAsync();
			string? Name(Guid? id) => profiles.SingleOrDefault(profile => profile.Id == id)?.Name;
			var project = await dbContext.Projects.Include(p => p.ProviderSelections).SingleAsync(p => p.Id == projectId);
			var buildProfileId = project.ProviderSelections.Single(entry => entry.ProviderId == claude.Id).ProfileId;
			Assert.Equal("Claude · opus · high", Name(buildProfileId));

			// Naming the provider the project builds with, and no model, meant "as the project
			// builds": that is no profile at all, not the provider's bare default.
			var same = await dbContext.JobSchedules.SingleAsync(schedule => schedule.Id == sameAsProject);
			Assert.Equal((null, null, null), (same.ProfileId, same.ProviderId, same.ModelId));
			Assert.Equal((null, null), (project.IdeasProcessingProfileId, project.IdeasProcessingProviderId));

			// A schedule never took the project's reasoning; auto-pilot did.
			Assert.Equal("Claude · sonnet", Name((await dbContext.JobSchedules.SingleAsync(schedule => schedule.Id == withModel)).ProfileId));
			Assert.Equal(buildProfileId, (await dbContext.IterationLoops.SingleAsync(loop => loop.Id == loopWithModel)).ProfileId);

			// Naming a fallback's provider meant that fallback.
			var fallbackProfileId = project.ProviderSelections.Single(entry => entry.ProviderId == copilot.Id).ProfileId;
			Assert.Equal(fallbackProfileId, (await dbContext.IterationLoops.SingleAsync(loop => loop.Id == loopOnFallback)).ProfileId);

			// A finished run is history and stays as it was.
			var finished = await dbContext.IterationLoops.SingleAsync(loop => loop.Id == finishedLoop);
			Assert.Equal((null, (Guid?)claude.Id, "sonnet"), (finished.ProfileId, finished.ProviderId, finished.ModelId));
		}
	}

	[Fact]
	public async Task DeleteAsync_MovesSchedulesAutoPilotAndTheIdeasQueueToo()
	{
		var (claude, _, _) = await SeedProvidersAsync();
		Guid profileId, defaultId;
		await using (var dbContext = CreateDbContext())
		{
			await ProfileService.EnsureProviderDefaultsAsync(dbContext);
			defaultId = (await dbContext.Profiles.SingleAsync(profile => profile.ProviderId == claude.Id && profile.IsProviderDefault)).Id;
			profileId = (await new ProfileService(dbContext).CreateAsync(new Profile { Name = "Nightly", ProviderId = claude.Id, ModelId = "sonnet" })).Id;
			var project = NewProject("Runners");
			project.IdeasProcessingActive = true;
			project.IdeasProcessingProfileId = profileId;
			project.IdeasProcessingProviderId = claude.Id;
			project.IdeasProcessingModelId = "sonnet";
			dbContext.Projects.Add(project);
			dbContext.JobSchedules.Add(new JobSchedule { Id = Guid.NewGuid(), ProjectId = project.Id, Prompt = "Tidy", ProfileId = profileId, ProviderId = claude.Id, ModelId = "sonnet" });
			dbContext.IterationLoops.Add(new IterationLoop { Id = Guid.NewGuid(), ProjectId = project.Id, Status = IterationLoopStatus.Running, ProfileId = profileId, ProviderId = claude.Id, ModelId = "sonnet" });
			await dbContext.SaveChangesAsync();
		}

		await using (var dbContext = CreateDbContext())
		{
			await new ProfileService(dbContext).DeleteAsync(profileId);
		}

		await using (var dbContext = CreateDbContext())
		{
			var schedule = await dbContext.JobSchedules.SingleAsync();
			var loop = await dbContext.IterationLoops.SingleAsync();
			var project = await dbContext.Projects.SingleAsync();
			Assert.Equal(((Guid?)defaultId, (Guid?)claude.Id, null), (schedule.ProfileId, schedule.ProviderId, schedule.ModelId));
			Assert.Equal(((Guid?)defaultId, (Guid?)claude.Id, null), (loop.ProfileId, loop.ProviderId, loop.ModelId));
			Assert.Equal(((Guid?)defaultId, (Guid?)claude.Id, null), (project.IdeasProcessingProfileId, project.IdeasProcessingProviderId, project.IdeasProcessingModelId));
		}
	}

	[Fact]
	public async Task InferenceRoles_LinkAProfile_FollowItsEdits_AndMoveWhenItIsDeleted()
	{
		await SeedProvidersAsync();
		var grok = await SeedGrokAsync();
		Guid projectId, loopId, scheduleId;
		await using (var dbContext = CreateDbContext())
		{
			await ProfileService.EnsureProviderDefaultsAsync(dbContext);
			// As saved before these settings took a profile: a provider and a model.
			var project = NewProject("Inference roles");
			project.IdeaInferenceProviderId = grok.Id;
			project.IdeaInferenceModelId = "grok-4";
			project.CommitSummaryInferenceProviderId = grok.Id;
			dbContext.Projects.Add(project);
			var loop = new IterationLoop { Id = Guid.NewGuid(), ProjectId = project.Id, Status = IterationLoopStatus.Running, InferenceProviderId = grok.Id, InferenceModelId = "grok-4" };
			var schedule = new JobSchedule { Id = Guid.NewGuid(), ProjectId = project.Id, ScheduleType = JobScheduleType.GenerateIdeas, InferenceProviderId = grok.Id, ModelId = "grok-4" };
			dbContext.IterationLoops.Add(loop);
			dbContext.JobSchedules.Add(schedule);
			await dbContext.SaveChangesAsync();
			(projectId, loopId, scheduleId) = (project.Id, loop.Id, schedule.Id);
		}

		Guid ideasProfileId, grokDefaultId;
		await using (var dbContext = CreateDbContext())
		{
			await ProfileService.EnsureSettingsHaveProfilesAsync(dbContext);
			var project = await dbContext.Projects.SingleAsync(p => p.Id == projectId);
			var profiles = await dbContext.Profiles.AsNoTracking().ToListAsync();
			grokDefaultId = profiles.Single(profile => profile.InferenceProviderId == grok.Id && profile.IsProviderDefault).Id;
			ideasProfileId = project.IdeasProfileId!.Value;

			// One profile holds "Grok on grok-4" for the project, the run and the schedule alike;
			// the commit writer left its model open, which is the provider's own profile.
			Assert.Equal("Grok · grok-4", profiles.Single(profile => profile.Id == ideasProfileId).Name);
			Assert.Equal(grokDefaultId, project.CommitSummaryProfileId);
			Assert.Equal(ideasProfileId, (await dbContext.IterationLoops.SingleAsync(loop => loop.Id == loopId)).IdeasProfileId);
			Assert.Equal(ideasProfileId, (await dbContext.JobSchedules.SingleAsync(schedule => schedule.Id == scheduleId)).ProfileId);
		}

		await using (var dbContext = CreateDbContext())
		{
			// Deleting it moves each of them to Grok's own profile, which names no model.
			await new ProfileService(dbContext).DeleteAsync(ideasProfileId);
			var project = await dbContext.Projects.AsNoTracking().SingleAsync(p => p.Id == projectId);
			var loop = await dbContext.IterationLoops.AsNoTracking().SingleAsync(item => item.Id == loopId);
			var schedule = await dbContext.JobSchedules.AsNoTracking().SingleAsync(item => item.Id == scheduleId);
			Assert.Equal(((Guid?)grokDefaultId, (Guid?)grok.Id, null), (project.IdeasProfileId, project.IdeaInferenceProviderId, project.IdeaInferenceModelId));
			Assert.Equal(((Guid?)grokDefaultId, (Guid?)grok.Id, null), (loop.IdeasProfileId, loop.InferenceProviderId, loop.InferenceModelId));
			Assert.Equal(((Guid?)grokDefaultId, (Guid?)grok.Id, null), (schedule.ProfileId, schedule.InferenceProviderId, schedule.ModelId));
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
