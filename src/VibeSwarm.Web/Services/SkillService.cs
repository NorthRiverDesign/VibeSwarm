using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

public class SkillService : ISkillService
{
	private readonly VibeSwarmDbContext _dbContext;

	public SkillService(VibeSwarmDbContext dbContext)
	{
		_dbContext = dbContext;
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
}
