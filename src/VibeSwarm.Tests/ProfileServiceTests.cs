using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
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
			project.LeadProfileId = plannerId;
			project.LeadProviderId = claude.Id;
			project.LeadModelId = "opus";
			project.ProviderSelections.Add(new ProjectProvider { ProviderId = claude.Id, ProfileId = builderId, PreferredModelId = "sonnet", Priority = 0 });
			project.ProviderSelections.Add(new ProjectProvider { ProviderId = openCode.Id, PreferredModelId = "other", Priority = 1 });
			dbContext.Projects.Add(project);
			await dbContext.SaveChangesAsync();
			projectId = project.Id;
		}

		await using (var dbContext = CreateDbContext())
		{
			var service = new ProfileService(dbContext);

			// OpenCode can't lead: the Lead reads the code read-only.
			var cannotPlan = await Assert.ThrowsAsync<InvalidOperationException>(
				() => service.UpdateAsync(new Profile { Id = plannerId, Name = "Planner", ProviderId = openCode.Id, ModelId = "big" }));
			Assert.Contains("Lead in Mixed", cannotPlan.Message);

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
			Assert.Equal((plannerId, claude.Id, "opus"), (project.LeadProfileId!.Value, project.LeadProviderId!.Value, project.LeadModelId));
			var claudeEntry = project.ProviderSelections.Single(entry => entry.ProviderId == claude.Id);
			Assert.Equal((builderId, "sonnet"), (claudeEntry.ProfileId!.Value, claudeEntry.PreferredModelId));
			Assert.Equal(claude.Id, (await dbContext.Profiles.SingleAsync(profile => profile.Id == plannerId)).ProviderId);

			// Model and reasoning alone can always change.
			await new ProfileService(dbContext).UpdateAsync(new Profile { Id = plannerId, Name = "Planner", ProviderId = claude.Id, ModelId = "sonnet", ReasoningEffort = "high" });
			var replanned = await dbContext.Projects.AsNoTracking().SingleAsync(p => p.Id == projectId);
			Assert.Equal(("sonnet", "high"), (replanned.LeadModelId, replanned.LeadReasoningEffort));
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
			// Review is off; it only remembers the profile it last used.
			var project = NewProject("Quiet");
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
			project.LeadProfileId = otherId;
			project.LeadProviderId = copilot.Id;
			project.LeadModelId = "gpt-5";
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
			Assert.Equal(otherId, project.LeadProfileId);
			Assert.Equal((copilot.Id, "gpt-5"), (project.LeadProviderId!.Value, project.LeadModelId));
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
			// A project working as a team that picked its own reviewer and Lead.
			var project = NewProject("Saved");
			project.TeamEnabled = true;
			project.TeamOverrides = TeamRoles.Lead | TeamRoles.Review;
			project.ReviewEnabled = true;
			project.ReviewProfileId = profileId;
			// Stale values from the client lose to the profile's.
			project.ReviewProviderId = copilot.Id;
			project.ReviewModelId = "stale";
			project.LeadProfileId = Guid.NewGuid();
			project.LeadProviderId = copilot.Id;
			project.ProviderSelections.Add(new ProjectProvider { ProviderId = copilot.Id, ProfileId = profileId, Priority = 0 });
			saved = await CreateProjectService(dbContext).CreateAsync(project);
		}

		await using (var dbContext = CreateDbContext())
		{
			var project = await dbContext.Projects.Include(p => p.ProviderSelections).SingleAsync(p => p.Id == saved.Id);
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
	public async Task ProjectSave_TakesAnInferenceReviewer_AndClearsTheHelperRolesNoLongerOffered()
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
			// OpenCode can't read a change read-only, so it can't review.
			var refused = NewProject("Refused");
			refused.TeamEnabled = true;
			refused.TeamOverrides = TeamRoles.Review;
			refused.ReviewProfileId = openCodeProfileId;
			var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateProjectService(dbContext).CreateAsync(refused));
			Assert.Contains("Review needs", error.Message);
		}

		Project saved;
		await using (var dbContext = CreateDbContext())
		{
			var project = NewProject("Helped");
			project.TeamEnabled = true;
			project.TeamOverrides = TeamRoles.Review;
			project.ReviewProfileId = researchId;
			project.ReviewProviderId = claude.Id;
			project.ResearchProfileId = researchId;
			saved = await CreateProjectService(dbContext).CreateAsync(project);
		}

		await using (var dbContext = CreateDbContext())
		{
			var project = await dbContext.Projects.SingleAsync(p => p.Id == saved.Id);
			Assert.Equal((null, grok.Id, "grok-4"), (project.ReviewProviderId, project.ReviewInferenceProviderId!.Value, project.ReviewModelId));
			Assert.True(project.ReviewEnabled);
			// Research and Decisions are no longer offered, so a save never keeps one running unseen.
			Assert.Null(project.ResearchProfileId);

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
			project.ReviewEnabled = true;
			project.ReviewInferenceProviderId = grok.Id;
			project.ReviewModelId = "grok-4";
			project.LeadProviderId = copilot.Id;
			project.LeadModelId = "gpt-5";
			project.LeadReasoningEffort = "high";
			project.DecisionsProfileId = Guid.NewGuid();
			dbContext.Projects.Add(project);

			var off = NewProject("Switched off");
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
			var reviewer = ById(project.ReviewProfileId);
			Assert.Equal(("Grok · grok-4", (Guid?)grok.Id, "grok-4"), (reviewer.Name, reviewer.InferenceProviderId, reviewer.ModelId));

			// One profile serves every setting holding the same values.
			Assert.Equal(copilotEntry.ProfileId, project.LeadProfileId);
			Assert.Equal(copilotEntry.ProfileId, (await dbContext.JobTemplates.SingleAsync(template => template.Id == pinnedTemplateId)).ProfileId);

			// A template that leaves the choice to each project stays that way, and a link to a
			// profile that is gone is cleared.
			Assert.Null((await dbContext.JobTemplates.SingleAsync(template => template.Id == openTemplateId)).ProfileId);
			Assert.Null(project.DecisionsProfileId);

			// A reviewer switched on with nothing to run on is switched off.
			var off = await dbContext.Projects.SingleAsync(p => p.Id == offProjectId);
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

	[Fact]
	public void TeamLinks_SwitchedOff_LeavesTheBuilderAlone()
	{
		var project = NewProject("Solo");
		project.TeamOverrides = TeamRoles.Lead | TeamRoles.Review;
		project.LeadProfileId = Guid.NewGuid();
		project.LeadProviderId = Guid.NewGuid();
		project.LeadModelId = "opus";
		project.ReviewEnabled = true;
		project.ReviewProfileId = Guid.NewGuid();

		TeamLinks.Apply(project, new TeamLinks.Team(Guid.NewGuid(), Guid.NewGuid()));

		Assert.Equal(TeamRoles.None, project.TeamOverrides);
		Assert.Equal((null, null, null), (project.LeadProfileId, project.LeadProviderId, project.LeadModelId));
		Assert.False(project.ReviewEnabled);
	}

	[Fact]
	public void TeamLinks_SwitchedOn_TakesTheTeamsProfiles_UnlessTheProjectPickedItsOwn()
	{
		var team = new TeamLinks.Team(Guid.NewGuid(), Guid.NewGuid());
		var following = NewProject("Following");
		following.TeamEnabled = true;
		TeamLinks.Apply(following, team);
		Assert.Equal((team.LeadProfileId, team.ReviewProfileId, true), (following.LeadProfileId, following.ReviewProfileId, following.ReviewEnabled));

		// Its own reviewer stays; the Lead still follows the team.
		var ownReviewer = Guid.NewGuid();
		var overriding = NewProject("Overriding");
		overriding.TeamEnabled = true;
		overriding.TeamOverrides = TeamRoles.Review;
		overriding.ReviewProfileId = ownReviewer;
		TeamLinks.Apply(overriding, team);
		Assert.Equal((team.LeadProfileId, (Guid?)ownReviewer, true), (overriding.LeadProfileId, overriding.ReviewProfileId, overriding.ReviewEnabled));

		// A team without a role leaves the project without it; an override that names nobody is none.
		var bare = NewProject("Bare");
		bare.TeamEnabled = true;
		bare.TeamOverrides = TeamRoles.Lead;
		bare.LeadProviderId = Guid.NewGuid();
		bare.ReviewProfileId = Guid.NewGuid();
		TeamLinks.Apply(bare, new TeamLinks.Team(null, null));
		Assert.Equal(TeamRoles.None, bare.TeamOverrides);
		Assert.Equal((null, null, false), (bare.LeadProviderId, bare.ReviewProfileId, bare.ReviewEnabled));
	}

	[Fact]
	public async Task UpdateTeam_ReachesEveryTeamProject_ExceptWhereTheProjectPickedItsOwn()
	{
		var (claude, copilot, _) = await SeedProvidersAsync();
		Guid strongId, cheapId, ownId, followingId, overridingId, soloId;
		await using (var dbContext = CreateDbContext())
		{
			var profiles = new ProfileService(dbContext);
			strongId = (await profiles.CreateAsync(new Profile { Name = "Strong", ProviderId = claude.Id, ModelId = "opus", ReasoningEffort = "high" })).Id;
			cheapId = (await profiles.CreateAsync(new Profile { Name = "Cheap", ProviderId = claude.Id, ModelId = "sonnet" })).Id;
			ownId = (await profiles.CreateAsync(new Profile { Name = "Own", ProviderId = copilot.Id, ModelId = "gpt-5" })).Id;

			// Saved before the team has anyone on it.
			var projects = CreateProjectService(dbContext);
			var following = NewProject("Following");
			following.TeamEnabled = true;
			var overriding = NewProject("Overriding");
			overriding.TeamEnabled = true;
			overriding.TeamOverrides = TeamRoles.Review;
			overriding.ReviewProfileId = ownId;
			followingId = (await projects.CreateAsync(following)).Id;
			overridingId = (await projects.CreateAsync(overriding)).Id;
			soloId = (await projects.CreateAsync(NewProject("Solo"))).Id;
		}

		await using (var dbContext = CreateDbContext())
		{
			var saved = await new SettingsService(dbContext).UpdateTeamAsync(strongId, cheapId);
			Assert.Equal((strongId, cheapId), (saved.TeamLeadProfileId!.Value, saved.TeamReviewProfileId!.Value));
		}

		await using (var dbContext = CreateDbContext())
		{
			var following = await dbContext.Projects.SingleAsync(p => p.Id == followingId);
			Assert.Equal((strongId, claude.Id, "opus", "high"), (following.LeadProfileId!.Value, following.LeadProviderId!.Value, following.LeadModelId, following.LeadReasoningEffort));
			Assert.Equal((true, cheapId, claude.Id, "sonnet"), (following.ReviewEnabled, following.ReviewProfileId!.Value, following.ReviewProviderId!.Value, following.ReviewModelId));

			var overriding = await dbContext.Projects.SingleAsync(p => p.Id == overridingId);
			Assert.Equal(strongId, overriding.LeadProfileId);
			Assert.Equal((true, ownId, copilot.Id, "gpt-5"), (overriding.ReviewEnabled, overriding.ReviewProfileId!.Value, overriding.ReviewProviderId!.Value, overriding.ReviewModelId));

			var solo = await dbContext.Projects.SingleAsync(p => p.Id == soloId);
			Assert.Equal((null, false), (solo.LeadProviderId, solo.ReviewEnabled));

			// A project saved from now on takes the team as it stands.
			var later = NewProject("Later");
			later.TeamEnabled = true;
			var created = await CreateProjectService(dbContext).CreateAsync(later);
			Assert.Equal((strongId, cheapId, true), (created.LeadProfileId!.Value, created.ReviewProfileId!.Value, created.ReviewEnabled));
		}

		// Taking the reviewer off the team switches review off where it followed the team.
		await using (var dbContext = CreateDbContext())
		{
			await new SettingsService(dbContext).UpdateTeamAsync(strongId, null);
			Assert.False((await dbContext.Projects.AsNoTracking().SingleAsync(p => p.Id == followingId)).ReviewEnabled);
			Assert.True((await dbContext.Projects.AsNoTracking().SingleAsync(p => p.Id == overridingId)).ReviewEnabled);
		}
	}

	[Fact]
	public async Task UpdateTeam_RefusesAProfileThatCannotFillTheRole()
	{
		var (claude, _, openCode) = await SeedProvidersAsync();
		var grok = await SeedGrokAsync();
		await using var dbContext = CreateDbContext();
		var profiles = new ProfileService(dbContext);
		var claudeId = (await profiles.CreateAsync(new Profile { Name = "Claude profile", ProviderId = claude.Id })).Id;
		var openCodeId = (await profiles.CreateAsync(new Profile { Name = "OpenCode profile", ProviderId = openCode.Id })).Id;
		var grokId = (await profiles.CreateAsync(new Profile { Name = "Grok profile", InferenceProviderId = grok.Id, ModelId = "grok-4" })).Id;
		var settings = new SettingsService(dbContext);

		// The Lead reads the code, which only Claude and Copilot can do read-only.
		Assert.Contains("The Lead needs", (await Assert.ThrowsAsync<InvalidOperationException>(() => settings.UpdateTeamAsync(openCodeId, null))).Message);
		Assert.Contains("The Lead needs", (await Assert.ThrowsAsync<InvalidOperationException>(() => settings.UpdateTeamAsync(grokId, null))).Message);
		Assert.Contains("Review needs", (await Assert.ThrowsAsync<InvalidOperationException>(() => settings.UpdateTeamAsync(null, openCodeId))).Message);
		Assert.Contains("no longer exists", (await Assert.ThrowsAsync<InvalidOperationException>(() => settings.UpdateTeamAsync(Guid.NewGuid(), null))).Message);

		// An inference profile can review, on the change alone.
		var saved = await settings.UpdateTeamAsync(claudeId, grokId);
		Assert.Equal((claudeId, grokId), (saved.TeamLeadProfileId!.Value, saved.TeamReviewProfileId!.Value));
	}

	[Fact]
	public async Task TheTeamsProfiles_MoveToTheProvidersDefaultWhenDeleted_AndCannotMoveWhereTheRoleCannotRun()
	{
		var (claude, _, openCode) = await SeedProvidersAsync();
		Guid strongId;
		await using (var dbContext = CreateDbContext())
		{
			await ProfileService.EnsureProviderDefaultsAsync(dbContext);
			strongId = (await new ProfileService(dbContext).CreateAsync(new Profile { Name = "Strong", ProviderId = claude.Id, ModelId = "opus" })).Id;
			await new SettingsService(dbContext).UpdateTeamAsync(strongId, strongId);
		}

		await using (var dbContext = CreateDbContext())
		{
			// No project uses the team yet, and it still can't be led from OpenCode.
			var error = await Assert.ThrowsAsync<InvalidOperationException>(
				() => new ProfileService(dbContext).UpdateAsync(new Profile { Id = strongId, Name = "Strong", ProviderId = openCode.Id, ModelId = "big" }));
			Assert.Contains("The team's Lead", error.Message);
		}

		await using (var dbContext = CreateDbContext())
		{
			await new ProfileService(dbContext).DeleteAsync(strongId);
			var claudeDefaultId = (await dbContext.Profiles.SingleAsync(profile => profile.ProviderId == claude.Id && profile.IsProviderDefault)).Id;
			var settings = await dbContext.AppSettings.AsNoTracking().SingleAsync();
			Assert.Equal((claudeDefaultId, claudeDefaultId), (settings.TeamLeadProfileId!.Value, settings.TeamReviewProfileId!.Value));
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
