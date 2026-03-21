using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Web.Services;

public class TeamRoleService : ITeamRoleService
{
	private readonly VibeSwarmDbContext _dbContext;

	public TeamRoleService(VibeSwarmDbContext dbContext)
	{
		_dbContext = dbContext;
	}

	public async Task<IEnumerable<TeamRole>> GetAllAsync(CancellationToken cancellationToken = default)
	{
		return await BuildTeamRoleQuery()
			.OrderBy(teamRole => teamRole.Name)
			.ToListAsync(cancellationToken);
	}

	public async Task<IEnumerable<TeamRole>> GetEnabledAsync(CancellationToken cancellationToken = default)
	{
		return await BuildTeamRoleQuery()
			.Where(teamRole => teamRole.IsEnabled)
			.OrderBy(teamRole => teamRole.Name)
			.ToListAsync(cancellationToken);
	}

	public async Task<TeamRole?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
	{
		return await BuildTeamRoleQuery()
			.FirstOrDefaultAsync(teamRole => teamRole.Id == id, cancellationToken);
	}

	public async Task<TeamRole> CreateAsync(TeamRole teamRole, CancellationToken cancellationToken = default)
	{
		NormalizeTeamRole(teamRole);
		ValidationHelper.ValidateObject(teamRole);

		if (await NameExistsAsync(teamRole.Name, cancellationToken: cancellationToken))
		{
			throw new InvalidOperationException($"A team role named '{teamRole.Name}' already exists.");
		}

		await ValidateSkillsAsync(teamRole.SkillLinks, cancellationToken);
		await ValidateDefaultProviderAsync(teamRole, cancellationToken);

		teamRole.Id = Guid.NewGuid();
		teamRole.CreatedAt = DateTime.UtcNow;
		teamRole.UpdatedAt = null;
		foreach (var link in teamRole.SkillLinks)
		{
			link.TeamRoleId = teamRole.Id;
			link.TeamRole = null;
			link.Skill = null;
		}

		_dbContext.TeamRoles.Add(teamRole);
		await _dbContext.SaveChangesAsync(cancellationToken);

		return await GetByIdAsync(teamRole.Id, cancellationToken) ?? teamRole;
	}

	public async Task<TeamRole> UpdateAsync(TeamRole teamRole, CancellationToken cancellationToken = default)
	{
		NormalizeTeamRole(teamRole);
		ValidationHelper.ValidateObject(teamRole);

		var existing = await _dbContext.TeamRoles
			.Include(role => role.SkillLinks)
			.FirstOrDefaultAsync(role => role.Id == teamRole.Id, cancellationToken);
		if (existing == null)
		{
			throw new InvalidOperationException($"Team role with ID {teamRole.Id} not found.");
		}

		if (await NameExistsAsync(teamRole.Name, teamRole.Id, cancellationToken))
		{
			throw new InvalidOperationException($"A team role named '{teamRole.Name}' already exists.");
		}

		await ValidateSkillsAsync(teamRole.SkillLinks, cancellationToken);
		await ValidateDefaultProviderAsync(teamRole, cancellationToken);

		existing.Name = teamRole.Name;
		existing.Description = teamRole.Description;
		existing.Responsibilities = teamRole.Responsibilities;
		existing.DefaultProviderId = teamRole.DefaultProviderId;
		existing.DefaultModelId = teamRole.DefaultModelId;
		existing.IsEnabled = teamRole.IsEnabled;
		existing.UpdatedAt = DateTime.UtcNow;

		SynchronizeSkills(existing, teamRole.SkillLinks);
		await _dbContext.SaveChangesAsync(cancellationToken);

		return await GetByIdAsync(existing.Id, cancellationToken) ?? existing;
	}

	public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
	{
		var teamRole = await _dbContext.TeamRoles.FirstOrDefaultAsync(role => role.Id == id, cancellationToken);
		if (teamRole != null)
		{
			_dbContext.TeamRoles.Remove(teamRole);
			await _dbContext.SaveChangesAsync(cancellationToken);
		}
	}

	public async Task<bool> NameExistsAsync(string name, Guid? excludeId = null, CancellationToken cancellationToken = default)
	{
		var normalizedName = name.Trim();
		var query = _dbContext.TeamRoles.Where(role => role.Name == normalizedName);
		if (excludeId.HasValue)
		{
			query = query.Where(role => role.Id != excludeId.Value);
		}

		return await query.AnyAsync(cancellationToken);
	}

	private IQueryable<TeamRole> BuildTeamRoleQuery()
	{
		return _dbContext.TeamRoles
			.Include(role => role.DefaultProvider)
			.Include(role => role.SkillLinks)
				.ThenInclude(link => link.Skill);
	}

	private static void NormalizeTeamRole(TeamRole teamRole)
	{
		teamRole.Name = teamRole.Name.Trim();
		teamRole.Description = string.IsNullOrWhiteSpace(teamRole.Description) ? null : teamRole.Description.Trim();
		teamRole.Responsibilities = string.IsNullOrWhiteSpace(teamRole.Responsibilities) ? null : teamRole.Responsibilities.Trim();
		teamRole.DefaultModelId = string.IsNullOrWhiteSpace(teamRole.DefaultModelId) ? null : teamRole.DefaultModelId.Trim();
		if (!teamRole.DefaultProviderId.HasValue)
		{
			teamRole.DefaultModelId = null;
		}
		teamRole.SkillLinks = (teamRole.SkillLinks ?? [])
			.GroupBy(link => link.SkillId)
			.Select(group => new TeamRoleSkill
			{
				TeamRoleId = teamRole.Id,
				SkillId = group.Key
			})
			.ToList();
	}

	private async Task ValidateSkillsAsync(ICollection<TeamRoleSkill> skillLinks, CancellationToken cancellationToken)
	{
		if (skillLinks == null || skillLinks.Count == 0)
		{
			return;
		}

		var skillIds = skillLinks.Select(link => link.SkillId).Distinct().ToList();
		var existingSkillIds = await _dbContext.Skills
			.AsNoTracking()
			.Where(skill => skillIds.Contains(skill.Id))
			.Select(skill => skill.Id)
			.ToListAsync(cancellationToken);
		var invalidSkillIds = skillIds.Except(existingSkillIds).ToList();
		if (invalidSkillIds.Any())
		{
			throw new InvalidOperationException($"One or more selected skills do not exist: {string.Join(", ", invalidSkillIds)}");
		}
	}

	private void SynchronizeSkills(TeamRole existing, ICollection<TeamRoleSkill> requestedSkillLinks)
	{
		var requestedSkillIds = requestedSkillLinks
			.Select(link => link.SkillId)
			.ToHashSet();

		var linksToRemove = existing.SkillLinks
			.Where(link => !requestedSkillIds.Contains(link.SkillId))
			.ToList();
		foreach (var link in linksToRemove)
		{
			_dbContext.Remove(link);
		}

		foreach (var skillId in requestedSkillIds)
		{
			if (existing.SkillLinks.Any(link => link.SkillId == skillId))
			{
				continue;
			}

			existing.SkillLinks.Add(new TeamRoleSkill
			{
				TeamRoleId = existing.Id,
				SkillId = skillId
			});
		}
	}

	private async Task ValidateDefaultProviderAsync(TeamRole teamRole, CancellationToken cancellationToken)
	{
		if (!teamRole.DefaultProviderId.HasValue)
		{
			if (!string.IsNullOrWhiteSpace(teamRole.DefaultModelId))
			{
				throw new InvalidOperationException("A default model requires selecting a default provider.");
			}

			return;
		}

		var providerExists = await _dbContext.Providers
			.AsNoTracking()
			.AnyAsync(provider => provider.Id == teamRole.DefaultProviderId.Value, cancellationToken);
		if (!providerExists)
		{
			throw new InvalidOperationException("The selected default provider does not exist.");
		}

		if (string.IsNullOrWhiteSpace(teamRole.DefaultModelId))
		{
			return;
		}

		var modelExists = await _dbContext.ProviderModels
			.AsNoTracking()
			.AnyAsync(model =>
				model.ProviderId == teamRole.DefaultProviderId.Value &&
				model.IsAvailable &&
				model.ModelId == teamRole.DefaultModelId,
				cancellationToken);
		if (!modelExists)
		{
			throw new InvalidOperationException("The selected default model is not available for the chosen provider.");
		}
	}
}
