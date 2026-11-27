using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Web.Services;

public class ProfileService : IProfileService
{
	/// <summary>Serializes <see cref="EnsureProviderDefaultsAsync"/>, so two callers can't both add a provider's default.</summary>
	private static readonly SemaphoreSlim DefaultsGate = new(1, 1);

	private readonly VibeSwarmDbContext _dbContext;

	public ProfileService(VibeSwarmDbContext dbContext)
	{
		_dbContext = dbContext;
	}

	public async Task<IEnumerable<Profile>> GetAllAsync(CancellationToken cancellationToken = default)
	{
		var profiles = await _dbContext.Profiles
			.AsNoTracking()
			.Include(profile => profile.Provider)
			.Include(profile => profile.InferenceProvider)
			.OrderBy(profile => profile.Name)
			.ToListAsync(cancellationToken);
		profiles.ForEach(WithSummaryProviders);
		return profiles;
	}

	public async Task<Profile?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
	{
		var profile = await _dbContext.Profiles
			.AsNoTracking()
			.Include(candidate => candidate.Provider)
			.Include(candidate => candidate.InferenceProvider)
			.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
		if (profile != null)
		{
			WithSummaryProviders(profile);
		}

		return profile;
	}

	/// <summary>
	/// Swaps the loaded providers for copies holding only what a profile list shows, so keys
	/// and settings stay with the provider pages.
	/// </summary>
	private static void WithSummaryProviders(Profile profile)
	{
		if (profile.Provider is { } provider)
		{
			profile.Provider = new Provider
			{
				Id = provider.Id,
				Name = provider.Name,
				Type = provider.Type,
				ConnectionMode = provider.ConnectionMode,
				IsEnabled = provider.IsEnabled,
				IsDefault = provider.IsDefault,
			};
		}

		if (profile.InferenceProvider is { } inference)
		{
			profile.InferenceProvider = new InferenceProvider
			{
				Id = inference.Id,
				Name = inference.Name,
				ProviderType = inference.ProviderType,
				Endpoint = string.Empty,
				IsEnabled = inference.IsEnabled,
			};
		}
	}

	public async Task<Profile> CreateAsync(Profile profile, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(profile);
		Normalize(profile);
		ValidationHelper.ValidateObject(profile);
		await EnsureNameIsFreeAsync(profile.Name, null, cancellationToken);
		await ValidateTargetAsync(profile, cancellationToken);

		var persisted = new Profile
		{
			Id = Guid.NewGuid(),
			Name = profile.Name,
			Description = profile.Description,
			ProviderId = profile.ProviderId,
			InferenceProviderId = profile.InferenceProviderId,
			ModelId = profile.ModelId,
			ReasoningEffort = profile.ReasoningEffort,
			CreatedAt = DateTime.UtcNow,
		};

		_dbContext.Profiles.Add(persisted);
		await _dbContext.SaveChangesAsync(cancellationToken);
		return await GetByIdAsync(persisted.Id, cancellationToken) ?? persisted;
	}

	public async Task<Profile> UpdateAsync(Profile profile, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(profile);
		var existing = await _dbContext.Profiles
			.FirstOrDefaultAsync(candidate => candidate.Id == profile.Id, cancellationToken)
			?? throw new InvalidOperationException($"Profile with ID {profile.Id} not found.");

		Normalize(profile);
		ValidationHelper.ValidateObject(profile);
		if (existing.IsProviderDefault
			&& (profile.ProviderId != existing.ProviderId || profile.InferenceProviderId != existing.InferenceProviderId))
		{
			throw new InvalidOperationException("A provider's default profile stays on that provider. Add a new profile for another one.");
		}

		// Settings linked to a coding profile can't take an inference one, or the other way round.
		if (profile.InferenceProviderId.HasValue != existing.InferenceProviderId.HasValue)
		{
			throw new InvalidOperationException(existing.InferenceProviderId.HasValue
				? "An inference profile stays on an inference provider. Add a new profile for a coding provider."
				: "A coding profile stays on a coding provider. Add a new profile for an inference provider.");
		}

		await EnsureNameIsFreeAsync(profile.Name, profile.Id, cancellationToken);
		var (provider, inferenceProvider) = await ValidateTargetAsync(profile, cancellationToken);
		if (profile.ProviderId != existing.ProviderId || profile.InferenceProviderId != existing.InferenceProviderId)
		{
			await EnsureLinksCanFollowAsync(existing.Id, provider, inferenceProvider, cancellationToken);
		}

		existing.Name = profile.Name;
		existing.Description = profile.Description;
		existing.ProviderId = profile.ProviderId;
		existing.InferenceProviderId = profile.InferenceProviderId;
		existing.ModelId = profile.ModelId;
		existing.ReasoningEffort = profile.ReasoningEffort;
		existing.UpdatedAt = DateTime.UtcNow;
		existing.Provider = provider;
		existing.InferenceProvider = inferenceProvider;

		await WriteThroughAsync(existing, cancellationToken);

		// One save, so the profile and every setting copied from it change together.
		await _dbContext.SaveChangesAsync(cancellationToken);
		return await GetByIdAsync(existing.Id, cancellationToken) ?? existing;
	}

	public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
	{
		var profile = await _dbContext.Profiles.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
		if (profile == null)
		{
			return;
		}

		if (profile.IsProviderDefault)
		{
			throw new InvalidOperationException("A provider's default profile is removed with its provider.");
		}

		// Nothing is left without a profile: every setting that used this one moves to its
		// provider's default profile and takes that profile's values.
		var fallback = await _dbContext.Profiles
			.Include(candidate => candidate.Provider)
			.Include(candidate => candidate.InferenceProvider)
			.FirstOrDefaultAsync(candidate => candidate.IsProviderDefault && candidate.Id != id
				&& ((profile.ProviderId != null && candidate.ProviderId == profile.ProviderId)
					|| (profile.InferenceProviderId != null && candidate.InferenceProviderId == profile.InferenceProviderId)),
				cancellationToken);
		var fallbackId = fallback?.Id;
		var lookup = new Dictionary<Guid, Profile>();
		if (fallback != null)
		{
			lookup[fallback.Id] = fallback;
		}

		var projects = await _dbContext.Projects
			.Where(project => project.PlanningProfileId == id || project.ReviewProfileId == id || project.LeadProfileId == id
				|| project.DecisionsProfileId == id || project.ResearchProfileId == id)
			.ToListAsync(cancellationToken);
		foreach (var project in projects)
		{
			project.PlanningProfileId = project.PlanningProfileId == id ? fallbackId : project.PlanningProfileId;
			project.ReviewProfileId = project.ReviewProfileId == id ? fallbackId : project.ReviewProfileId;
			project.LeadProfileId = project.LeadProfileId == id ? fallbackId : project.LeadProfileId;
			project.DecisionsProfileId = project.DecisionsProfileId == id ? fallbackId : project.DecisionsProfileId;
			project.ResearchProfileId = project.ResearchProfileId == id ? fallbackId : project.ResearchProfileId;
			if (fallback != null)
			{
				ApplyRoleLinksFor(project, fallback);
			}

			project.UpdatedAt = DateTime.UtcNow;
		}

		foreach (var entry in await _dbContext.ProjectProviders.Where(entry => entry.ProfileId == id).ToListAsync(cancellationToken))
		{
			entry.ProfileId = fallbackId;
			ProfileLinks.ApplyEntryLink(entry, lookup);
		}

		foreach (var agent in await _dbContext.Agents.Where(agent => agent.ProfileId == id).ToListAsync(cancellationToken))
		{
			agent.ProfileId = fallbackId;
			ProfileLinks.ApplyAgentLink(agent, lookup);
		}

		foreach (var assignment in await _dbContext.ProjectAgents.Where(assignment => assignment.ProfileId == id).ToListAsync(cancellationToken))
		{
			assignment.ProfileId = fallbackId;
			ProfileLinks.ApplyAssignmentLink(assignment, lookup);
		}

		foreach (var template in await _dbContext.JobTemplates.Where(template => template.ProfileId == id).ToListAsync(cancellationToken))
		{
			template.ProfileId = fallbackId;
			ProfileLinks.ApplyTemplateLink(template, lookup);
		}

		_dbContext.Profiles.Remove(profile);
		await _dbContext.SaveChangesAsync(cancellationToken);
	}

	/// <summary>
	/// Gives every coding and inference provider without one its default profile: named after the
	/// provider, on its default model and reasoning. Safe to call any number of times.
	/// </summary>
	public static async Task EnsureProviderDefaultsAsync(VibeSwarmDbContext dbContext, CancellationToken cancellationToken = default)
	{
		await DefaultsGate.WaitAsync(cancellationToken);
		try
		{
			var missingProviders = await dbContext.Providers
				.Where(provider => !dbContext.Profiles.Any(profile => profile.ProviderId == provider.Id && profile.IsProviderDefault))
				.Select(provider => new { provider.Id, provider.Name })
				.ToListAsync(cancellationToken);
			var missingInference = await dbContext.InferenceProviders
				.Where(provider => !dbContext.Profiles.Any(profile => profile.InferenceProviderId == provider.Id && profile.IsProviderDefault))
				.Select(provider => new { provider.Id, provider.Name })
				.ToListAsync(cancellationToken);
			if (missingProviders.Count == 0 && missingInference.Count == 0)
			{
				return;
			}

			var takenNames = (await dbContext.Profiles.Select(profile => profile.Name).ToListAsync(cancellationToken))
				.ToHashSet(StringComparer.OrdinalIgnoreCase);
			var added = new List<Profile>();
			foreach (var provider in missingProviders)
			{
				added.Add(NewDefault(PickDefaultName(provider.Name, takenNames), providerId: provider.Id, inferenceProviderId: null));
			}

			foreach (var provider in missingInference)
			{
				added.Add(NewDefault(PickDefaultName(provider.Name, takenNames), providerId: null, inferenceProviderId: provider.Id));
			}

			dbContext.Profiles.AddRange(added);
			try
			{
				await dbContext.SaveChangesAsync(cancellationToken);
			}
			catch (DbUpdateException)
			{
				// Another process got there first; its defaults stand.
				foreach (var profile in added)
				{
					dbContext.Entry(profile).State = EntityState.Detached;
				}
			}
		}
		finally
		{
			DefaultsGate.Release();
		}

		static Profile NewDefault(string name, Guid? providerId, Guid? inferenceProviderId) => new()
		{
			Id = Guid.NewGuid(),
			Name = name,
			ProviderId = providerId,
			InferenceProviderId = inferenceProviderId,
			IsProviderDefault = true,
			CreatedAt = DateTime.UtcNow,
		};
	}

	/// <summary>
	/// Copies the profile's values into every setting linked to it. Every link can take them:
	/// <see cref="EnsureLinksCanFollowAsync"/> has already refused a change one couldn't follow.
	/// </summary>
	private async Task WriteThroughAsync(Profile profile, CancellationToken cancellationToken)
	{
		var id = profile.Id;
		var lookup = new Dictionary<Guid, Profile> { [id] = profile };
		var projects = await _dbContext.Projects
			.Where(project => project.PlanningProfileId == id || project.ReviewProfileId == id || project.LeadProfileId == id)
			.ToListAsync(cancellationToken);
		foreach (var project in projects)
		{
			ApplyRoleLinksFor(project, profile);
			project.UpdatedAt = DateTime.UtcNow;
		}

		foreach (var entry in await _dbContext.ProjectProviders.Where(entry => entry.ProfileId == id).ToListAsync(cancellationToken))
		{
			ProfileLinks.ApplyEntryLink(entry, lookup);
			entry.UpdatedAt = DateTime.UtcNow;
		}

		foreach (var agent in await _dbContext.Agents.Where(agent => agent.ProfileId == id).ToListAsync(cancellationToken))
		{
			ProfileLinks.ApplyAgentLink(agent, lookup);
			agent.UpdatedAt = DateTime.UtcNow;
		}

		foreach (var assignment in await _dbContext.ProjectAgents.Where(assignment => assignment.ProfileId == id).ToListAsync(cancellationToken))
		{
			ProfileLinks.ApplyAssignmentLink(assignment, lookup);
			assignment.UpdatedAt = DateTime.UtcNow;
		}

		foreach (var template in await _dbContext.JobTemplates.Where(template => template.ProfileId == id).ToListAsync(cancellationToken))
		{
			ProfileLinks.ApplyTemplateLink(template, lookup);
			template.UpdatedAt = DateTime.UtcNow;
		}
	}

	/// <summary>
	/// Refuses to move a profile onto a provider that a setting using it can't run on, naming the
	/// first setting in the way. A link is never dropped quietly: that would leave the setting
	/// running on values no profile describes.
	/// </summary>
	private async Task EnsureLinksCanFollowAsync(Guid id, Provider? provider, InferenceProvider? inferenceProvider, CancellationToken cancellationToken)
	{
		var moved = new Profile
		{
			ProviderId = provider?.Id,
			Provider = provider,
			InferenceProviderId = inferenceProvider?.Id,
			InferenceProvider = inferenceProvider,
		};
		var providerName = provider?.Name ?? inferenceProvider?.Name ?? "that provider";
		var projects = await _dbContext.Projects
			.AsNoTracking()
			.Where(project => project.PlanningProfileId == id || project.ReviewProfileId == id || project.LeadProfileId == id
				|| project.DecisionsProfileId == id || project.ResearchProfileId == id)
			.Select(project => new { project.Name, project.PlanningEnabled, project.ReviewEnabled, project.PlanningProfileId, project.ReviewProfileId, project.LeadProfileId, project.DecisionsProfileId, project.ResearchProfileId })
			.ToListAsync(cancellationToken);
		foreach (var project in projects)
		{
			// A planner or reviewer that is switched off keeps its last profile without using
			// it, so it never stands in the way.
			var role = !ProfileRoles.CanRunReadOnly(moved) && project.PlanningEnabled && project.PlanningProfileId == id ? "Plan first"
				: !ProfileRoles.CanRunReadOnly(moved) && project.LeadProfileId == id ? "Lead"
				: !ProfileRoles.CanHelpJobs(moved) && project.ReviewEnabled && project.ReviewProfileId == id ? "Review"
				: !ProfileRoles.CanHelpJobs(moved) && project.DecisionsProfileId == id ? "Decisions"
				: !ProfileRoles.CanHelpJobs(moved) && project.ResearchProfileId == id ? "Research"
				: null;
			if (role != null)
			{
				throw new InvalidOperationException(
					$"{role} in {project.Name} uses this profile and can't run on {providerName}. Pick another profile there first, or add a new profile for {providerName}.");
			}
		}

		if (provider == null)
		{
			return;
		}

		// A project lists a provider once, so its entry can't move onto one it already lists.
		var crowded = await _dbContext.ProjectProviders
			.AsNoTracking()
			.Where(entry => entry.ProfileId == id && entry.ProviderId != provider.Id
				&& _dbContext.ProjectProviders.Any(other => other.ProjectId == entry.ProjectId && other.ProviderId == provider.Id && other.Id != entry.Id))
			.Select(entry => entry.Project!.Name)
			.FirstOrDefaultAsync(cancellationToken);
		if (crowded != null)
		{
			throw new InvalidOperationException(
				$"{crowded} builds with this profile and already lists {providerName}. Remove one of the two there first, or add a new profile for {providerName}.");
		}
	}

	/// <summary>
	/// Links every setting that holds a provider, model and reasoning but no profile to the
	/// profile with exactly those values, adding one where none exists, and clears links to
	/// profiles that are gone. Settings from before profiles, an import, or a deleted provider
	/// all end up described by a profile, which is the only way the app shows them. A planner or
	/// reviewer left switched on with nothing to run on is switched off, since it never ran.
	/// Safe to call any number of times.
	/// </summary>
	public static async Task EnsureSettingsHaveProfilesAsync(VibeSwarmDbContext dbContext, CancellationToken cancellationToken = default)
	{
		await DefaultsGate.WaitAsync(cancellationToken);
		try
		{
			var catalog = await ProfileCatalog.LoadAsync(dbContext, cancellationToken);
			var projects = await dbContext.Projects
				.Include(project => project.ProviderSelections)
				.Include(project => project.AgentAssignments)
				.ToListAsync(cancellationToken);
			foreach (var project in projects)
			{
				catalog.LinkProject(project, switchOffRolesWithNothingToRunOn: true);
			}

			foreach (var agent in await dbContext.Agents.ToListAsync(cancellationToken))
			{
				catalog.LinkAgent(agent);
			}

			foreach (var template in await dbContext.JobTemplates.ToListAsync(cancellationToken))
			{
				catalog.LinkTemplate(template);
			}

			await dbContext.SaveChangesAsync(cancellationToken);
		}
		finally
		{
			DefaultsGate.Release();
		}
	}

	/// <summary>
	/// Gives each of the project's settings that came without a profile the one matching its
	/// values, before the project is saved. The caller saves; a profile added here is saved with it.
	/// </summary>
	public static async Task LinkProjectAsync(VibeSwarmDbContext dbContext, Project project, CancellationToken cancellationToken = default)
		=> (await ProfileCatalog.LoadAsync(dbContext, cancellationToken)).LinkProject(project, switchOffRolesWithNothingToRunOn: false);

	/// <summary>As <see cref="LinkProjectAsync"/>, for an agent's defaults.</summary>
	public static async Task LinkAgentAsync(VibeSwarmDbContext dbContext, Agent agent, CancellationToken cancellationToken = default)
		=> (await ProfileCatalog.LoadAsync(dbContext, cancellationToken)).LinkAgent(agent);

	/// <summary>As <see cref="LinkProjectAsync"/>, for a job template that pins a provider.</summary>
	public static async Task LinkTemplateAsync(VibeSwarmDbContext dbContext, JobTemplate template, CancellationToken cancellationToken = default)
		=> (await ProfileCatalog.LoadAsync(dbContext, cancellationToken)).LinkTemplate(template);

	/// <summary>
	/// Every profile, read once, so a pass over many settings finds or adds profiles without a
	/// query each. Added profiles join the context and are saved by whoever saves it next.
	/// </summary>
	private sealed class ProfileCatalog
	{
		private readonly VibeSwarmDbContext _dbContext;
		private readonly List<Profile> _profiles;
		private readonly HashSet<string> _names;
		private readonly Dictionary<Guid, string> _providerNames;
		private readonly Dictionary<Guid, string> _inferenceProviderNames;

		private ProfileCatalog(VibeSwarmDbContext dbContext, List<Profile> profiles, Dictionary<Guid, string> providerNames, Dictionary<Guid, string> inferenceProviderNames)
		{
			_dbContext = dbContext;
			_profiles = profiles;
			_names = profiles.Select(profile => profile.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
			_providerNames = providerNames;
			_inferenceProviderNames = inferenceProviderNames;
		}

		public static async Task<ProfileCatalog> LoadAsync(VibeSwarmDbContext dbContext, CancellationToken cancellationToken)
			=> new(
				dbContext,
				await dbContext.Profiles.AsNoTracking().ToListAsync(cancellationToken),
				await dbContext.Providers.AsNoTracking().ToDictionaryAsync(provider => provider.Id, provider => provider.Name, cancellationToken),
				await dbContext.InferenceProviders.AsNoTracking().ToDictionaryAsync(provider => provider.Id, provider => provider.Name, cancellationToken));

		public void LinkProject(Project project, bool switchOffRolesWithNothingToRunOn)
		{
			project.PlanningProfileId = Existing(project.PlanningProfileId);
			project.ReviewProfileId = Existing(project.ReviewProfileId);
			project.LeadProfileId = Existing(project.LeadProfileId);
			project.DecisionsProfileId = Existing(project.DecisionsProfileId);
			project.ResearchProfileId = Existing(project.ResearchProfileId);

			if (switchOffRolesWithNothingToRunOn)
			{
				project.PlanningEnabled = project.PlanningEnabled && project.PlanningProviderId.HasValue;
				project.ReviewEnabled = project.ReviewEnabled && (project.ReviewProviderId.HasValue || project.ReviewInferenceProviderId.HasValue);
			}

			// Only a role that is on needs a profile; values left behind a switched-off one are
			// just what it last ran on.
			if (project.PlanningEnabled)
			{
				project.PlanningProfileId ??= FindOrAdd(project.PlanningProviderId, null, project.PlanningModelId, project.PlanningReasoningEffort);
			}

			if (project.ReviewEnabled)
			{
				project.ReviewProfileId ??= project.ReviewProviderId.HasValue
					? FindOrAdd(project.ReviewProviderId, null, project.ReviewModelId, project.ReviewReasoningEffort)
					: FindOrAdd(null, project.ReviewInferenceProviderId, project.ReviewModelId, null);
			}

			// The Lead has no switch: it runs whenever it has a provider.
			project.LeadProfileId ??= FindOrAdd(project.LeadProviderId, null, project.LeadModelId, project.LeadReasoningEffort);

			foreach (var entry in project.ProviderSelections ?? [])
			{
				entry.ProfileId = Existing(entry.ProfileId)
					?? FindOrAdd(entry.ProviderId, null, entry.PreferredModelId, entry.PreferredReasoningEffort);
			}

			foreach (var assignment in project.AgentAssignments ?? [])
			{
				assignment.ProfileId = Existing(assignment.ProfileId)
					?? FindOrAdd(assignment.ProviderId, null, assignment.PreferredModelId, assignment.PreferredReasoningEffort);
			}
		}

		public void LinkAgent(Agent agent)
			=> agent.ProfileId = Existing(agent.ProfileId)
				?? FindOrAdd(agent.DefaultProviderId, null, agent.DefaultModelId, agent.DefaultReasoningEffort);

		public void LinkTemplate(JobTemplate template)
			=> template.ProfileId = Existing(template.ProfileId)
				?? FindOrAdd(template.ProviderId, null, template.ModelId, template.ReasoningEffort);

		private Guid? Existing(Guid? profileId)
			=> profileId is Guid id && _profiles.Any(profile => profile.Id == id) ? id : null;

		/// <summary>
		/// The profile holding exactly these values, the provider's default first, or a new one
		/// named after them. Null when there is no such provider to run on.
		/// </summary>
		private Guid? FindOrAdd(Guid? providerId, Guid? inferenceProviderId, string? modelId, string? reasoningEffort)
		{
			providerId = providerId == Guid.Empty ? null : providerId;
			var providerName = providerId is Guid coding ? _providerNames.GetValueOrDefault(coding)
				: inferenceProviderId is Guid inference ? _inferenceProviderNames.GetValueOrDefault(inference)
				: null;
			if (providerName == null)
			{
				return null;
			}

			inferenceProviderId = providerId.HasValue ? null : inferenceProviderId;
			modelId = string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim();
			reasoningEffort = providerId.HasValue ? ProviderCapabilities.NormalizeReasoningEffort(reasoningEffort) : null;

			var match = _profiles
				.Where(profile => profile.ProviderId == providerId && profile.InferenceProviderId == inferenceProviderId
					&& string.Equals(profile.ModelId, modelId, StringComparison.Ordinal)
					&& string.Equals(profile.ReasoningEffort, reasoningEffort, StringComparison.Ordinal))
				.OrderByDescending(profile => profile.IsProviderDefault)
				.ThenBy(profile => profile.CreatedAt)
				.FirstOrDefault();
			if (match != null)
			{
				return match.Id;
			}

			var added = new Profile
			{
				Id = Guid.NewGuid(),
				Name = PickName(providerName, modelId, reasoningEffort),
				ProviderId = providerId,
				InferenceProviderId = inferenceProviderId,
				ModelId = modelId,
				ReasoningEffort = reasoningEffort,
				CreatedAt = DateTime.UtcNow,
			};
			_dbContext.Profiles.Add(added);
			_profiles.Add(added);
			return added.Id;
		}

		/// <summary>Names a profile after what it runs on, such as "Claude Code · opus · xhigh".</summary>
		private string PickName(string providerName, string? modelId, string? reasoningEffort)
		{
			var baseName = modelId == null && reasoningEffort == null
				? $"{providerName.Trim()} (provider defaults)"
				: string.Join(" · ", new[] { providerName.Trim(), modelId, reasoningEffort }.Where(part => !string.IsNullOrWhiteSpace(part)));
			const int roomForNumber = 4;
			if (baseName.Length > ValidationLimits.ProfileNameMaxLength - roomForNumber)
			{
				baseName = baseName[..(ValidationLimits.ProfileNameMaxLength - roomForNumber)].TrimEnd();
			}

			var candidate = baseName;
			for (var number = 2; _names.Contains(candidate); number++)
			{
				candidate = $"{baseName} {number}";
			}

			_names.Add(candidate);
			return candidate;
		}
	}

	private static string PickDefaultName(string providerName, HashSet<string> takenNames)
	{
		var baseName = string.IsNullOrWhiteSpace(providerName) ? "Provider" : providerName.Trim();
		if (baseName.Length > ValidationLimits.ProfileNameMaxLength - 12)
		{
			baseName = baseName[..(ValidationLimits.ProfileNameMaxLength - 12)].TrimEnd();
		}

		var candidate = baseName;
		if (takenNames.Contains(candidate))
		{
			candidate = $"{baseName} default";
			for (var number = 2; takenNames.Contains(candidate); number++)
			{
				candidate = $"{baseName} default {number}";
			}
		}

		takenNames.Add(candidate);
		return candidate;
	}

	/// <summary>
	/// Applies only the project's links to this profile; links to other profiles are set aside
	/// first, so ApplyRoleLinks doesn't drop them for missing from its lookup.
	/// </summary>
	private static void ApplyRoleLinksFor(Project project, Profile profile)
	{
		var original = (project.PlanningProfileId, project.ReviewProfileId, project.LeadProfileId);
		project.PlanningProfileId = original.PlanningProfileId == profile.Id ? profile.Id : null;
		project.ReviewProfileId = original.ReviewProfileId == profile.Id ? profile.Id : null;
		project.LeadProfileId = original.LeadProfileId == profile.Id ? profile.Id : null;

		ProfileLinks.ApplyRoleLinks(project, new Dictionary<Guid, Profile> { [profile.Id] = profile });

		if (original.PlanningProfileId != profile.Id)
		{
			project.PlanningProfileId = original.PlanningProfileId;
		}

		if (original.ReviewProfileId != profile.Id)
		{
			project.ReviewProfileId = original.ReviewProfileId;
		}

		if (original.LeadProfileId != profile.Id)
		{
			project.LeadProfileId = original.LeadProfileId;
		}
	}

	private static void Normalize(Profile profile)
	{
		profile.Name = profile.Name?.Trim() ?? string.Empty;
		profile.Description = string.IsNullOrWhiteSpace(profile.Description) ? null : profile.Description.Trim();
		profile.ProviderId = profile.ProviderId == Guid.Empty ? null : profile.ProviderId;
		profile.InferenceProviderId = profile.InferenceProviderId == Guid.Empty ? null : profile.InferenceProviderId;
		profile.ModelId = string.IsNullOrWhiteSpace(profile.ModelId) ? null : profile.ModelId.Trim();
		profile.ReasoningEffort = ProviderCapabilities.NormalizeReasoningEffort(profile.ReasoningEffort);
		profile.Provider = null;
		profile.InferenceProvider = null;
	}

	private async Task EnsureNameIsFreeAsync(string name, Guid? excludeId, CancellationToken cancellationToken)
	{
		var lowered = name.ToLower();
		var taken = await _dbContext.Profiles.AnyAsync(
			profile => profile.Name.ToLower() == lowered && (!excludeId.HasValue || profile.Id != excludeId.Value),
			cancellationToken);
		if (taken)
		{
			throw new InvalidOperationException($"A profile named '{name}' already exists.");
		}
	}

	/// <summary>Checks the profile runs on exactly one existing provider, with a model and reasoning it offers.</summary>
	private async Task<(Provider? Provider, InferenceProvider? InferenceProvider)> ValidateTargetAsync(Profile profile, CancellationToken cancellationToken)
	{
		if (profile.ProviderId.HasValue == profile.InferenceProviderId.HasValue)
		{
			throw new InvalidOperationException("Choose one provider for the profile.");
		}

		if (profile.InferenceProviderId is Guid inferenceProviderId)
		{
			var inferenceProvider = await _dbContext.InferenceProviders.FirstOrDefaultAsync(candidate => candidate.Id == inferenceProviderId, cancellationToken)
				?? throw new InvalidOperationException("The selected inference provider does not exist.");
			if (profile.ReasoningEffort != null)
			{
				throw new InvalidOperationException("Inference providers have no reasoning levels.");
			}

			if (profile.ModelId != null
				&& !await _dbContext.InferenceModels.AnyAsync(
					model => model.InferenceProviderId == inferenceProviderId && model.IsAvailable && model.ModelId == profile.ModelId,
					cancellationToken))
			{
				throw new InvalidOperationException("The selected model is not available for the chosen provider.");
			}

			return (null, inferenceProvider);
		}

		var provider = await _dbContext.Providers.FirstOrDefaultAsync(candidate => candidate.Id == profile.ProviderId, cancellationToken)
			?? throw new InvalidOperationException("The selected provider does not exist.");

		if (!ProviderCapabilities.SupportsReasoningEffort(provider, profile.ReasoningEffort))
		{
			throw new InvalidOperationException("The reasoning level is not supported by the selected provider.");
		}

		// Project settings only take a model the provider lists, and a profile writes into them.
		if (profile.ModelId != null
			&& !await _dbContext.ProviderModels.AnyAsync(
				model => model.ProviderId == provider.Id && model.IsAvailable && model.ModelId == profile.ModelId,
				cancellationToken))
		{
			throw new InvalidOperationException("The selected model is not available for the chosen provider.");
		}

		return (provider, null);
	}
}
