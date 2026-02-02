using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

public class SkillService : ISkillService
{
	private readonly VibeSwarmDbContext _dbContext;
	private readonly IProviderService _providerService;
	private readonly ILogger<SkillService> _logger;

	public SkillService(VibeSwarmDbContext dbContext, IProviderService providerService, ILogger<SkillService> logger)
	{
		_dbContext = dbContext;
		_providerService = providerService;
		_logger = logger;
	}

	public async Task<IEnumerable<Skill>> GetAllAsync(CancellationToken cancellationToken = default)
	{
		return await _dbContext.Skills
			.OrderBy(s => s.Name)
			.ToListAsync(cancellationToken);
	}

	public async Task<IEnumerable<Skill>> GetEnabledAsync(CancellationToken cancellationToken = default)
	{
		return await _dbContext.Skills
			.Where(s => s.IsEnabled)
			.OrderBy(s => s.Name)
			.ToListAsync(cancellationToken);
	}

	public async Task<Skill?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
	{
		return await _dbContext.Skills
			.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
	}

	public async Task<Skill?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
	{
		return await _dbContext.Skills
			.FirstOrDefaultAsync(s => s.Name == name, cancellationToken);
	}

	public async Task<Skill> CreateAsync(Skill skill, CancellationToken cancellationToken = default)
	{
		skill.Id = Guid.NewGuid();
		skill.CreatedAt = DateTime.UtcNow;

		_dbContext.Skills.Add(skill);
		await _dbContext.SaveChangesAsync(cancellationToken);

		return skill;
	}

	public async Task<Skill> UpdateAsync(Skill skill, CancellationToken cancellationToken = default)
	{
		var existing = await _dbContext.Skills
			.FirstOrDefaultAsync(s => s.Id == skill.Id, cancellationToken);

		if (existing == null)
		{
			throw new InvalidOperationException($"Skill with ID {skill.Id} not found.");
		}

		existing.Name = skill.Name;
		existing.Description = skill.Description;
		existing.Content = skill.Content;
		existing.IsEnabled = skill.IsEnabled;
		existing.UpdatedAt = DateTime.UtcNow;

		await _dbContext.SaveChangesAsync(cancellationToken);

		return existing;
	}

	public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
	{
		var skill = await _dbContext.Skills
			.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

		if (skill != null)
		{
			_dbContext.Skills.Remove(skill);
			await _dbContext.SaveChangesAsync(cancellationToken);
		}
	}

	public async Task<bool> NameExistsAsync(string name, Guid? excludeId = null, CancellationToken cancellationToken = default)
	{
		var query = _dbContext.Skills.Where(s => s.Name == name);

		if (excludeId.HasValue)
		{
			query = query.Where(s => s.Id != excludeId.Value);
		}

		return await query.AnyAsync(cancellationToken);
	}

	public async Task<string?> ExpandSkillAsync(string description, Guid providerId, string? modelId = null, CancellationToken cancellationToken = default)
	{
		var provider = await _providerService.GetByIdAsync(providerId, cancellationToken);
		if (provider == null)
		{
			_logger.LogWarning("Provider {ProviderId} not found for skill expansion", providerId);
			return null;
		}

		var providerInstance = _providerService.CreateInstance(provider);
		if (providerInstance == null)
		{
			_logger.LogWarning("Could not create provider instance for {ProviderName}", provider.Name);
			return null;
		}

		var expansionPrompt = BuildSkillExpansionPrompt(description);

		try
		{
			var response = await providerInstance.GetPromptResponseAsync(
				expansionPrompt,
				workingDirectory: null, // Skills don't have a specific project context
				cancellationToken);

			if (response.Success && !string.IsNullOrWhiteSpace(response.Response))
			{
				_logger.LogInformation("Successfully expanded skill description");
				return response.Response.Trim();
			}

			_logger.LogWarning("Failed to expand skill: {Error}", response.ErrorMessage ?? "No response");
			return null;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error expanding skill description");
			return null;
		}
	}

	private static string BuildSkillExpansionPrompt(string description)
	{
		return $@"You are creating a detailed skill definition for an AI coding agent. A skill is a set of instructions that guides the AI agent on how to perform specific tasks.

## Skill Description
{description}

## Task
Generate comprehensive Markdown content for this skill that includes:

1. **Purpose**: A clear explanation of what this skill enables the AI to do
2. **When to Use**: Specific situations or triggers for applying this skill
3. **Instructions**: Step-by-step guidance for the AI to follow
4. **Best Practices**: Tips and recommendations for optimal results
5. **Examples**: If applicable, show sample inputs/outputs or usage scenarios
6. **Constraints**: Any limitations or things to avoid

Write the content in clear, instructional Markdown format that an AI agent can easily follow. Be specific and actionable.";
	}
}
