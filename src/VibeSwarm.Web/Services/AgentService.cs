using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Web.Services;

public class AgentService : IAgentService
{
	private readonly VibeSwarmDbContext _dbContext;

	public AgentService(VibeSwarmDbContext dbContext)
	{
		_dbContext = dbContext;
	}

	public async Task<IEnumerable<Agent>> GetAllAsync(CancellationToken cancellationToken = default)
	{
		return await BuildAgentQuery()
			.OrderBy(agent => agent.Name)
			.ToListAsync(cancellationToken);
	}

	public async Task<IEnumerable<Agent>> GetEnabledAsync(CancellationToken cancellationToken = default)
	{
		return await BuildAgentQuery()
			.Where(agent => agent.IsEnabled)
			.OrderBy(agent => agent.Name)
			.ToListAsync(cancellationToken);
	}

	public async Task<Agent?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
	{
		return await BuildAgentQuery()
			.FirstOrDefaultAsync(agent => agent.Id == id, cancellationToken);
	}

	public async Task<Agent> CreateAsync(Agent agent, CancellationToken cancellationToken = default)
	{
		NormalizeAgent(agent);
		ValidationHelper.ValidateObject(agent);

		if (await NameExistsAsync(agent.Name, cancellationToken: cancellationToken))
		{
			throw new InvalidOperationException($"An agent named '{agent.Name}' already exists.");
		}

		await ValidateSkillsAsync(agent.SkillLinks, cancellationToken);
		await ValidateDefaultProviderAsync(agent, cancellationToken);

		var persistedAgent = new Agent
		{
			Id = Guid.NewGuid(),
			Name = agent.Name,
			Description = agent.Description,
			Responsibilities = agent.Responsibilities,
			DefaultProviderId = agent.DefaultProviderId,
			DefaultModelId = agent.DefaultModelId,
			DefaultReasoningEffort = agent.DefaultReasoningEffort,
			DefaultCycleMode = agent.DefaultCycleMode,
			DefaultCycleSessionMode = agent.DefaultCycleSessionMode,
			DefaultMaxCycles = agent.DefaultMaxCycles,
			DefaultCycleReviewPrompt = agent.DefaultCycleReviewPrompt,
			IsEnabled = agent.IsEnabled,
			CreatedAt = DateTime.UtcNow,
			UpdatedAt = null,
			SkillLinks = agent.SkillLinks
				.Select(link => new AgentSkill
				{
					SkillId = link.SkillId
				})
				.ToList()
		};
		foreach (var link in persistedAgent.SkillLinks)
		{
			link.AgentId = persistedAgent.Id;
		}

		_dbContext.Agents.Add(persistedAgent);
		await _dbContext.SaveChangesAsync(cancellationToken);

		return await GetByIdAsync(persistedAgent.Id, cancellationToken) ?? persistedAgent;
	}

	public async Task<Agent> UpdateAsync(Agent agent, CancellationToken cancellationToken = default)
	{
		NormalizeAgent(agent);
		ValidationHelper.ValidateObject(agent);

		var existing = await _dbContext.Agents
			.Include(role => role.SkillLinks)
			.FirstOrDefaultAsync(role => role.Id == agent.Id, cancellationToken);
		if (existing == null)
		{
			throw new InvalidOperationException($"Agent with ID {agent.Id} not found.");
		}

		if (await NameExistsAsync(agent.Name, agent.Id, cancellationToken))
		{
			throw new InvalidOperationException($"An agent named '{agent.Name}' already exists.");
		}

		await ValidateSkillsAsync(agent.SkillLinks, cancellationToken);
		await ValidateDefaultProviderAsync(agent, cancellationToken);

		existing.Name = agent.Name;
		existing.Description = agent.Description;
		existing.Responsibilities = agent.Responsibilities;
		existing.DefaultProviderId = agent.DefaultProviderId;
		existing.DefaultModelId = agent.DefaultModelId;
		existing.DefaultReasoningEffort = agent.DefaultReasoningEffort;
		existing.DefaultCycleMode = agent.DefaultCycleMode;
		existing.DefaultCycleSessionMode = agent.DefaultCycleSessionMode;
		existing.DefaultMaxCycles = agent.DefaultMaxCycles;
		existing.DefaultCycleReviewPrompt = agent.DefaultCycleReviewPrompt;
		existing.IsEnabled = agent.IsEnabled;
		existing.UpdatedAt = DateTime.UtcNow;

		SynchronizeSkills(existing, agent.SkillLinks);
		await _dbContext.SaveChangesAsync(cancellationToken);

		return await GetByIdAsync(existing.Id, cancellationToken) ?? existing;
	}

	public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
	{
		var agent = await _dbContext.Agents.FirstOrDefaultAsync(role => role.Id == id, cancellationToken);
		if (agent != null)
		{
			_dbContext.Agents.Remove(agent);
			await _dbContext.SaveChangesAsync(cancellationToken);
		}
	}

	public async Task<bool> NameExistsAsync(string name, Guid? excludeId = null, CancellationToken cancellationToken = default)
	{
		var normalizedName = name.Trim();
		var query = _dbContext.Agents.Where(role => role.Name == normalizedName);
		if (excludeId.HasValue)
		{
			query = query.Where(role => role.Id != excludeId.Value);
		}

		return await query.AnyAsync(cancellationToken);
	}

	private IQueryable<Agent> BuildAgentQuery()
	{
		return _dbContext.Agents
			.Include(role => role.DefaultProvider)
			.Include(role => role.SkillLinks)
				.ThenInclude(link => link.Skill);
	}

	private static void NormalizeAgent(Agent agent)
	{
		agent.Name = agent.Name.Trim();
		agent.Description = string.IsNullOrWhiteSpace(agent.Description) ? null : agent.Description.Trim();
		agent.Responsibilities = string.IsNullOrWhiteSpace(agent.Responsibilities) ? null : agent.Responsibilities.Trim();
		agent.DefaultModelId = string.IsNullOrWhiteSpace(agent.DefaultModelId) ? null : agent.DefaultModelId.Trim();
		agent.DefaultReasoningEffort = ProviderCapabilities.NormalizeReasoningEffort(agent.DefaultReasoningEffort);
		agent.DefaultCycleReviewPrompt = string.IsNullOrWhiteSpace(agent.DefaultCycleReviewPrompt)
			? null
			: agent.DefaultCycleReviewPrompt.Trim();
		agent.DefaultMaxCycles = Math.Clamp(agent.DefaultMaxCycles, 1, 100);
		if (!agent.DefaultProviderId.HasValue)
		{
			agent.DefaultModelId = null;
			agent.DefaultReasoningEffort = null;
		}
		if (agent.DefaultCycleMode == CycleMode.SingleCycle)
		{
			agent.DefaultCycleSessionMode = CycleSessionMode.ContinueSession;
			agent.DefaultMaxCycles = 1;
			agent.DefaultCycleReviewPrompt = null;
		}
		agent.SkillLinks = (agent.SkillLinks ?? [])
			.GroupBy(link => link.SkillId)
			.Select(group => new AgentSkill
			{
				AgentId = agent.Id,
				SkillId = group.Key
			})
			.ToList();
	}

	private async Task ValidateSkillsAsync(ICollection<AgentSkill> skillLinks, CancellationToken cancellationToken)
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

	private void SynchronizeSkills(Agent existing, ICollection<AgentSkill> requestedSkillLinks)
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

			existing.SkillLinks.Add(new AgentSkill
			{
				AgentId = existing.Id,
				SkillId = skillId
			});
		}
	}

	private async Task ValidateDefaultProviderAsync(Agent agent, CancellationToken cancellationToken)
	{
		if (!agent.DefaultProviderId.HasValue)
		{
			if (!string.IsNullOrWhiteSpace(agent.DefaultModelId))
			{
				throw new InvalidOperationException("A default model requires selecting a default provider.");
			}

			if (!string.IsNullOrWhiteSpace(agent.DefaultReasoningEffort))
			{
				throw new InvalidOperationException("A default reasoning level requires selecting a default provider.");
			}

			return;
		}

		var provider = await _dbContext.Providers
			.AsNoTracking()
			.FirstOrDefaultAsync(provider => provider.Id == agent.DefaultProviderId.Value, cancellationToken);
		if (provider == null)
		{
			throw new InvalidOperationException("The selected default provider does not exist.");
		}

		if (!ProviderCapabilities.SupportsReasoningEffort(provider, agent.DefaultReasoningEffort))
		{
			throw new InvalidOperationException("The selected default reasoning level is not supported by the chosen provider.");
		}

		if (string.IsNullOrWhiteSpace(agent.DefaultModelId))
		{
			return;
		}

		var modelExists = await _dbContext.ProviderModels
			.AsNoTracking()
			.AnyAsync(model =>
				model.ProviderId == agent.DefaultProviderId.Value &&
				model.IsAvailable &&
				model.ModelId == agent.DefaultModelId,
				cancellationToken);
		if (!modelExists)
		{
			throw new InvalidOperationException("The selected default model is not available for the chosen provider.");
		}
	}
}
