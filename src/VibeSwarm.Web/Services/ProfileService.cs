using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Web.Services;

public class ProfileService : IProfileService
{
	private readonly VibeSwarmDbContext _dbContext;

	public ProfileService(VibeSwarmDbContext dbContext)
	{
		_dbContext = dbContext;
	}

	public async Task<IEnumerable<Profile>> GetAllAsync(CancellationToken cancellationToken = default)
	{
		return await _dbContext.Profiles
			.AsNoTracking()
			.Include(profile => profile.Provider)
			.OrderBy(profile => profile.Name)
			.ToListAsync(cancellationToken);
	}

	public async Task<Profile?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
	{
		return await _dbContext.Profiles
			.AsNoTracking()
			.Include(profile => profile.Provider)
			.FirstOrDefaultAsync(profile => profile.Id == id, cancellationToken);
	}

	public async Task<Profile> CreateAsync(Profile profile, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(profile);
		Normalize(profile);
		ValidationHelper.ValidateObject(profile);
		await EnsureNameIsFreeAsync(profile.Name, null, cancellationToken);
		await ValidateProviderAsync(profile, cancellationToken);

		var persisted = new Profile
		{
			Id = Guid.NewGuid(),
			Name = profile.Name,
			Description = profile.Description,
			ProviderId = profile.ProviderId,
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
		await EnsureNameIsFreeAsync(profile.Name, profile.Id, cancellationToken);
		var provider = await ValidateProviderAsync(profile, cancellationToken);

		existing.Name = profile.Name;
		existing.Description = profile.Description;
		existing.ProviderId = profile.ProviderId;
		existing.ModelId = profile.ModelId;
		existing.ReasoningEffort = profile.ReasoningEffort;
		existing.UpdatedAt = DateTime.UtcNow;
		existing.Provider = provider;

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

		// Linked settings keep the values they last copied and become ordinary custom settings.
		var projects = await _dbContext.Projects
			.Where(project => project.PlanningProfileId == id || project.ReviewProfileId == id || project.LeadProfileId == id)
			.ToListAsync(cancellationToken);
		foreach (var project in projects)
		{
			project.PlanningProfileId = project.PlanningProfileId == id ? null : project.PlanningProfileId;
			project.ReviewProfileId = project.ReviewProfileId == id ? null : project.ReviewProfileId;
			project.LeadProfileId = project.LeadProfileId == id ? null : project.LeadProfileId;
		}

		var entries = await _dbContext.ProjectProviders
			.Where(entry => entry.ProfileId == id)
			.ToListAsync(cancellationToken);
		foreach (var entry in entries)
		{
			entry.ProfileId = null;
		}

		_dbContext.Profiles.Remove(profile);
		await _dbContext.SaveChangesAsync(cancellationToken);
	}

	/// <summary>
	/// Copies the profile's values into every project setting linked to it. A planning, review or
	/// Lead link whose provider can no longer fill the role is dropped instead, as is a priority
	/// entry whose new provider the project already lists: the setting keeps its old values.
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

		var entries = await _dbContext.ProjectProviders
			.Where(entry => entry.ProfileId == id)
			.ToListAsync(cancellationToken);
		foreach (var entry in entries)
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
		profile.ModelId = string.IsNullOrWhiteSpace(profile.ModelId) ? null : profile.ModelId.Trim();
		profile.ReasoningEffort = ProviderCapabilities.NormalizeReasoningEffort(profile.ReasoningEffort);
		profile.Provider = null;
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

	private async Task<Provider> ValidateProviderAsync(Profile profile, CancellationToken cancellationToken)
	{
		if (profile.ProviderId == Guid.Empty)
		{
			throw new InvalidOperationException("Choose a provider for the profile.");
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

		return provider;
	}
}
