using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Web.Services;

public class JobTemplateService : IJobTemplateService
{
	private readonly VibeSwarmDbContext _dbContext;

	public JobTemplateService(VibeSwarmDbContext dbContext)
	{
		_dbContext = dbContext;
	}

	public async Task<IEnumerable<JobTemplate>> GetAllAsync(CancellationToken cancellationToken = default)
	{
		return await BuildQuery()
			.OrderByDescending(template => template.UseCount)
			.ThenBy(template => template.Name)
			.ToListAsync(cancellationToken);
	}

	public async Task<JobTemplate?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
	{
		return await BuildQuery()
			.FirstOrDefaultAsync(template => template.Id == id, cancellationToken);
	}

	public async Task<JobTemplate> CreateAsync(JobTemplate template, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(template);

		NormalizeTemplate(template);
		ValidationHelper.ValidateObject(template);
		await ValidateReferencesAsync(template, cancellationToken);
		await EnsureNameIsUniqueAsync(template.Name, null, cancellationToken);

		template.Id = Guid.NewGuid();
		template.CreatedAt = DateTime.UtcNow;
		template.UpdatedAt = null;
		template.UseCount = 0;

		_dbContext.JobTemplates.Add(template);
		await _dbContext.SaveChangesAsync(cancellationToken);

		return await GetByIdAsync(template.Id, cancellationToken) ?? template;
	}

	public async Task<JobTemplate> UpdateAsync(JobTemplate template, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(template);

		var existing = await _dbContext.JobTemplates.FirstOrDefaultAsync(item => item.Id == template.Id, cancellationToken);
		if (existing == null)
		{
			throw new InvalidOperationException($"Template with ID {template.Id} not found.");
		}

		existing.Name = template.Name;
		existing.Description = template.Description;
		existing.GoalPrompt = template.GoalPrompt;
		existing.ProviderId = template.ProviderId;
		existing.ModelId = template.ModelId;
		existing.ReasoningEffort = template.ReasoningEffort;
		existing.Branch = template.Branch;
		existing.GitChangeDeliveryMode = template.GitChangeDeliveryMode;
		existing.TargetBranch = template.TargetBranch;
		existing.CycleMode = template.CycleMode;
		existing.CycleSessionMode = template.CycleSessionMode;
		existing.MaxCycles = template.MaxCycles;
		existing.CycleReviewPrompt = template.CycleReviewPrompt;
		existing.UpdatedAt = DateTime.UtcNow;

		NormalizeTemplate(existing);
		ValidationHelper.ValidateObject(existing);
		await ValidateReferencesAsync(existing, cancellationToken);
		await EnsureNameIsUniqueAsync(existing.Name, existing.Id, cancellationToken);

		await _dbContext.SaveChangesAsync(cancellationToken);

		return await GetByIdAsync(existing.Id, cancellationToken) ?? existing;
	}

	public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
	{
		var template = await _dbContext.JobTemplates.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
		if (template == null)
		{
			return;
		}

		_dbContext.JobTemplates.Remove(template);
		await _dbContext.SaveChangesAsync(cancellationToken);
	}

	public async Task<JobTemplate> IncrementUseCountAsync(Guid id, CancellationToken cancellationToken = default)
	{
		var template = await _dbContext.JobTemplates.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
		if (template == null)
		{
			throw new InvalidOperationException($"Template with ID {id} not found.");
		}

		template.UseCount++;
		template.UpdatedAt = DateTime.UtcNow;
		await _dbContext.SaveChangesAsync(cancellationToken);

		return await GetByIdAsync(template.Id, cancellationToken) ?? template;
	}

	private IQueryable<JobTemplate> BuildQuery()
	{
		return _dbContext.JobTemplates
			.Include(template => template.Provider);
	}

	private async Task EnsureNameIsUniqueAsync(string name, Guid? currentId, CancellationToken cancellationToken)
	{
		var exists = await _dbContext.JobTemplates.AnyAsync(template =>
			template.Name == name && (!currentId.HasValue || template.Id != currentId.Value), cancellationToken);
		if (exists)
		{
			throw new ValidationException($"A job template named '{name}' already exists.");
		}
	}

	private async Task ValidateReferencesAsync(JobTemplate template, CancellationToken cancellationToken)
	{
		if (template.ProviderId == null)
		{
			if (!string.IsNullOrWhiteSpace(template.ModelId))
			{
				throw new ValidationException("Selecting a model requires selecting a provider.");
			}

			if (!string.IsNullOrWhiteSpace(template.ReasoningEffort))
			{
				throw new ValidationException("Selecting a reasoning level requires selecting a provider.");
			}

			return;
		}

		var provider = await _dbContext.Providers
			.AsNoTracking()
			.FirstOrDefaultAsync(item => item.Id == template.ProviderId.Value && item.IsEnabled, cancellationToken);
		if (provider == null)
		{
			throw new ValidationException("The selected provider is not enabled.");
		}

		if (!ProviderCapabilities.SupportsReasoningEffort(provider, template.ReasoningEffort))
		{
			throw new ValidationException("The selected reasoning level is not supported by the chosen provider.");
		}

		if (string.IsNullOrWhiteSpace(template.ModelId))
		{
			return;
		}

		var modelExists = await _dbContext.ProviderModels
			.AnyAsync(model =>
				model.ProviderId == template.ProviderId.Value &&
				model.IsAvailable &&
				model.ModelId == template.ModelId,
				cancellationToken);
		if (!modelExists)
		{
			throw new ValidationException("The selected model is not available for the chosen provider.");
		}
	}

	private static void NormalizeTemplate(JobTemplate template)
	{
		template.Name = template.Name?.Trim() ?? string.Empty;
		template.Description = string.IsNullOrWhiteSpace(template.Description) ? null : template.Description.Trim();
		template.GoalPrompt = template.GoalPrompt?.Trim() ?? string.Empty;
		template.ProviderId = template.ProviderId == Guid.Empty ? null : template.ProviderId;
		template.ModelId = string.IsNullOrWhiteSpace(template.ModelId) ? null : template.ModelId.Trim();
		template.ReasoningEffort = ProviderCapabilities.NormalizeReasoningEffort(template.ReasoningEffort);
		template.Branch = string.IsNullOrWhiteSpace(template.Branch) ? null : template.Branch.Trim();
		template.TargetBranch = string.IsNullOrWhiteSpace(template.TargetBranch) ? null : template.TargetBranch.Trim();
		template.CycleReviewPrompt = string.IsNullOrWhiteSpace(template.CycleReviewPrompt) ? null : template.CycleReviewPrompt.Trim();
		template.MaxCycles = Math.Clamp(template.MaxCycles, 1, 100);
	}
}
