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

		// Linked settings keep the values they last copied and become ordinary custom settings.
		var projects = await _dbContext.Projects
			.Where(project => project.PlanningProfileId == id || project.ReviewProfileId == id || project.LeadProfileId == id
				|| project.DecisionsProfileId == id || project.ResearchProfileId == id)
			.ToListAsync(cancellationToken);
		foreach (var project in projects)
		{
			project.PlanningProfileId = project.PlanningProfileId == id ? null : project.PlanningProfileId;
			project.ReviewProfileId = project.ReviewProfileId == id ? null : project.ReviewProfileId;
			project.LeadProfileId = project.LeadProfileId == id ? null : project.LeadProfileId;
			project.DecisionsProfileId = project.DecisionsProfileId == id ? null : project.DecisionsProfileId;
			project.ResearchProfileId = project.ResearchProfileId == id ? null : project.ResearchProfileId;
		}

		foreach (var entry in await _dbContext.ProjectProviders.Where(entry => entry.ProfileId == id).ToListAsync(cancellationToken))
		{
			entry.ProfileId = null;
		}

		foreach (var agent in await _dbContext.Agents.Where(agent => agent.ProfileId == id).ToListAsync(cancellationToken))
		{
			agent.ProfileId = null;
		}

		foreach (var assignment in await _dbContext.ProjectAgents.Where(assignment => assignment.ProfileId == id).ToListAsync(cancellationToken))
		{
			assignment.ProfileId = null;
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
	/// Copies the profile's values into every setting linked to it. A link whose slot the profile
	/// can no longer fill is dropped instead, as is a priority entry whose new provider the
	/// project already lists: the setting keeps its old values.
	/// </summary>
	private async Task WriteThroughAsync(Profile profile, CancellationToken cancellationToken)
	{
		var id = profile.Id;
		var lookup = new Dictionary<Guid, Profile> { [id] = profile };
		var projects = await _dbContext.Projects
			.Where(project => project.PlanningProfileId == id || project.ReviewProfileId == id || project.LeadProfileId == id
				|| project.DecisionsProfileId == id || project.ResearchProfileId == id)
			.ToListAsync(cancellationToken);
		foreach (var project in projects)
		{
			ApplyRoleLinksFor(project, profile);
			if (!ProfileLinks.CanHelpJobs(profile))
			{
				project.DecisionsProfileId = project.DecisionsProfileId == id ? null : project.DecisionsProfileId;
				project.ResearchProfileId = project.ResearchProfileId == id ? null : project.ResearchProfileId;
			}

			project.UpdatedAt = DateTime.UtcNow;
		}

		foreach (var entry in await _dbContext.ProjectProviders.Where(entry => entry.ProfileId == id).ToListAsync(cancellationToken))
		{
			if (entry.ProviderId != profile.ProviderId
				&& await _dbContext.ProjectProviders.AnyAsync(
					other => other.ProjectId == entry.ProjectId && other.ProviderId == profile.ProviderId && other.Id != entry.Id,
					cancellationToken))
			{
				entry.ProfileId = null;
				continue;
			}

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
