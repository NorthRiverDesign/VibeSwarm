using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Validation;
using VibeSwarm.Shared.VersionControl;

namespace VibeSwarm.Web.Services;

public class JobTemplateService : IJobTemplateService
{
	private readonly VibeSwarmDbContext _dbContext;
	private readonly IJobService? _jobService;
	private readonly IVersionControlService? _versionControlService;
	private readonly ILogger<JobTemplateService> _logger;

	/// <summary>Library management only; <see cref="RunAsync"/> needs the full constructor.</summary>
	public JobTemplateService(VibeSwarmDbContext dbContext)
	{
		_dbContext = dbContext;
		_logger = NullLogger<JobTemplateService>.Instance;
	}

	public JobTemplateService(
		VibeSwarmDbContext dbContext,
		IJobService jobService,
		IVersionControlService versionControlService,
		ILogger<JobTemplateService> logger)
	{
		_dbContext = dbContext;
		_jobService = jobService;
		_versionControlService = versionControlService;
		_logger = logger;
	}

	public async Task<IEnumerable<JobTemplate>> GetAllAsync(CancellationToken cancellationToken = default)
	{
		var templates = await BuildQuery()
			.OrderByDescending(template => template.UseCount)
			.ThenBy(template => template.Name)
			.ToListAsync(cancellationToken);

		// One query for every template's run summary; grouped in memory so it reads the same on
		// SQLite and MySQL.
		var runs = await _dbContext.Jobs
			.AsNoTracking()
			.Where(job => job.JobTemplateId != null && job.ParentJobId == null)
			.Select(job => new { TemplateId = job.JobTemplateId!.Value, job.ProjectId, job.CreatedAt })
			.ToListAsync(cancellationToken);
		var runsByTemplate = runs.ToLookup(run => run.TemplateId);
		foreach (var template in templates)
		{
			var templateRuns = runsByTemplate[template.Id].ToList();
			template.ProjectRunCount = templateRuns.Select(run => run.ProjectId).Distinct().Count();
			template.LastRunAt = templateRuns.Count > 0 ? templateRuns.Max(run => run.CreatedAt) : null;
		}

		await NameProfilesAsync(templates, cancellationToken);
		return templates;
	}

	/// <summary>Fills each template's profile name, which the list shows instead of a provider and model.</summary>
	private async Task NameProfilesAsync(IReadOnlyCollection<JobTemplate> templates, CancellationToken cancellationToken)
	{
		var profileIds = templates.Where(template => template.ProfileId.HasValue).Select(template => template.ProfileId!.Value).Distinct().ToList();
		if (profileIds.Count == 0)
		{
			return;
		}

		var names = await _dbContext.Profiles
			.AsNoTracking()
			.Where(profile => profileIds.Contains(profile.Id))
			.ToDictionaryAsync(profile => profile.Id, profile => profile.Name, cancellationToken);
		foreach (var template in templates)
		{
			template.ProfileName = template.ProfileId is Guid id ? names.GetValueOrDefault(id) : null;
		}
	}

	public async Task<IReadOnlyList<JobTemplateProjectRun>> GetRunsAsync(Guid templateId, CancellationToken cancellationToken = default)
	{
		var jobs = await _dbContext.Jobs
			.AsNoTracking()
			.Where(job => job.JobTemplateId == templateId && job.ParentJobId == null)
			.OrderByDescending(job => job.CreatedAt)
			.Select(job => new JobTemplateProjectRun
			{
				ProjectId = job.ProjectId,
				ProjectName = job.Project != null ? job.Project.Name : string.Empty,
				JobId = job.Id,
				Status = job.Status,
				CreatedAt = job.CreatedAt,
				CompletedAt = job.CompletedAt
			})
			.ToListAsync(cancellationToken);

		return jobs
			.GroupBy(run => run.ProjectId)
			.Select(group => group.First())
			.OrderByDescending(run => run.CreatedAt)
			.ToList();
	}

	public async Task<JobTemplateRunResult> RunAsync(Guid templateId, IReadOnlyCollection<Guid> projectIds, CancellationToken cancellationToken = default)
	{
		if (_jobService == null || _versionControlService == null)
		{
			throw new InvalidOperationException("Running a template needs the job and version control services.");
		}

		var template = await _dbContext.JobTemplates
			.AsNoTracking()
			.FirstOrDefaultAsync(item => item.Id == templateId, cancellationToken)
			?? throw new InvalidOperationException($"Template with ID {templateId} not found.");

		var requestedIds = (projectIds ?? []).Where(id => id != Guid.Empty).Distinct().ToList();
		if (requestedIds.Count == 0)
		{
			throw new ValidationException("Choose at least one project.");
		}

		// A pinned provider that has since been turned off would fail every project the same way.
		if (template.ProviderId is Guid pinnedProviderId &&
			!await _dbContext.Providers.AnyAsync(provider => provider.Id == pinnedProviderId && provider.IsEnabled, cancellationToken))
		{
			throw new ValidationException($"'{template.Name}' uses a provider that is turned off. Edit the template to choose another, or leave the provider to each project.");
		}

		var projects = await _dbContext.Projects
			.AsNoTracking()
			.Where(project => requestedIds.Contains(project.Id))
			.ToDictionaryAsync(project => project.Id, cancellationToken);

		// Like the job form, a pinned provider only runs where the project allows it: a project that
		// lists providers allows just those, one that lists none allows any enabled provider.
		var allowedProvidersByProject = (await _dbContext.ProjectProviders
			.AsNoTracking()
			.Where(selection => requestedIds.Contains(selection.ProjectId) && selection.IsEnabled)
			.Select(selection => new { selection.ProjectId, selection.ProviderId })
			.ToListAsync(cancellationToken))
			.ToLookup(selection => selection.ProjectId, selection => selection.ProviderId);
		var pinnedProviderName = template.ProviderId is Guid providerId
			? await _dbContext.Providers.Where(provider => provider.Id == providerId).Select(provider => provider.Name).FirstOrDefaultAsync(cancellationToken)
			: null;

				// A second tap from the phone must not queue the same template twice on one project.
		var busyProjectIds = (await _dbContext.Jobs
			.AsNoTracking()
			.Where(job => job.JobTemplateId == templateId &&
				job.ParentJobId == null &&
				requestedIds.Contains(job.ProjectId) &&
				job.Status != JobStatus.Completed &&
				job.Status != JobStatus.Failed &&
				job.Status != JobStatus.Cancelled)
			.Select(job => job.ProjectId)
			.Distinct()
			.ToListAsync(cancellationToken))
			.ToHashSet();

		var result = new JobTemplateRunResult();
		foreach (var projectId in requestedIds)
		{
			if (!projects.TryGetValue(projectId, out var project))
			{
				result.Projects.Add(new JobTemplateRunOutcome { ProjectId = projectId, SkipReason = "Project not found." });
				continue;
			}

			var outcome = new JobTemplateRunOutcome { ProjectId = project.Id, ProjectName = project.Name };
			result.Projects.Add(outcome);

			if (!project.IsActive)
			{
				outcome.SkipReason = "Project is inactive.";
				continue;
			}

			// A template on a profile runs wherever it is sent, like a job given that profile.
			// Only one still pinned to a bare provider is held to the project's list, since
			// such a job is planned from that list alone.
			if (template.ProfileId == null &&
				template.ProviderId is Guid pinned &&
				allowedProvidersByProject[project.Id].Any() &&
				!allowedProvidersByProject[project.Id].Contains(pinned))
			{
				outcome.SkipReason = $"Project doesn't use {pinnedProviderName ?? "this template's provider"}.";
				continue;
			}

			if (busyProjectIds.Contains(project.Id))
			{
				outcome.SkipReason = "Already running this template.";
				continue;
			}

			try
			{
				var job = await BuildJobAsync(template, project, cancellationToken);
				var created = await _jobService.CreateAsync(job, cancellationToken);
				outcome.JobId = created.Id;
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				// One project's failure never stops the rest of the run.
				_logger.LogWarning(ex, "Template {TemplateId} could not queue a job on project {ProjectId}", templateId, project.Id);
				outcome.SkipReason = ex.Message;
			}
		}

		result.Projects = result.Projects
			.OrderBy(outcome => outcome.ProjectName, StringComparer.OrdinalIgnoreCase)
			.ToList();
		return result;
	}

	/// <summary>
	/// The job a template queues on one project. Anything the template leaves empty comes from the
	/// project: provider priority (resolved by JobService), development branch, delivery mode and
	/// pull-request target.
	/// </summary>
	private async Task<Job> BuildJobAsync(JobTemplate template, Project project, CancellationToken cancellationToken)
	{
		var pinsProvider = template.ProviderId.HasValue;
		return new Job
		{
			ProjectId = project.Id,
			JobTemplateId = template.Id,
			Title = template.Name,
			GoalPrompt = template.GoalPrompt,
			ProfileId = template.ProfileId,
			ProviderId = template.ProviderId ?? Guid.Empty,
			ModelUsed = pinsProvider ? template.ModelId : null,
			ReasoningEffort = pinsProvider ? template.ReasoningEffort : null,
			Branch = template.Branch
				?? await ProjectJobBranch.ResolveDefaultAsync(project, _versionControlService!, _logger, cancellationToken),
			GitChangeDeliveryMode = template.GitChangeDeliveryMode ?? project.GitChangeDeliveryMode,
			TargetBranch = template.TargetBranch ?? project.DefaultTargetBranch,
			CycleMode = template.CycleMode,
			CycleSessionMode = template.CycleSessionMode,
			MaxCycles = template.MaxCycles,
			CycleReviewPrompt = template.CycleReviewPrompt,
			Status = JobStatus.New
		};
	}

	public async Task<JobTemplate?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
	{
		var template = await BuildQuery()
			.FirstOrDefaultAsync(template => template.Id == id, cancellationToken);
		if (template != null)
		{
			await NameProfilesAsync([template], cancellationToken);
		}

		return template;
	}

	public async Task<JobTemplate> CreateAsync(JobTemplate template, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(template);

		NormalizeTemplate(template);
		await ApplyProfileLinkAsync(template, cancellationToken);
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
		existing.ProfileId = template.ProfileId;
		existing.Branch = template.Branch;
		existing.GitChangeDeliveryMode = template.GitChangeDeliveryMode;
		existing.TargetBranch = template.TargetBranch;
		existing.CycleMode = template.CycleMode;
		existing.CycleSessionMode = template.CycleSessionMode;
		existing.MaxCycles = template.MaxCycles;
		existing.CycleReviewPrompt = template.CycleReviewPrompt;
		existing.UpdatedAt = DateTime.UtcNow;

		NormalizeTemplate(existing);
		await ApplyProfileLinkAsync(existing, cancellationToken);
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

	/// <summary>
	/// Fills the template's provider, model and reasoning from its linked profile, whatever the
	/// client sent, and gives a template that pins a provider without a profile the matching one.
	/// </summary>
	private async Task ApplyProfileLinkAsync(JobTemplate template, CancellationToken cancellationToken)
	{
		if (template.ProfileId is Guid profileId)
		{
			var profiles = await _dbContext.Profiles
				.AsNoTracking()
				.Where(profile => profile.Id == profileId)
				.ToDictionaryAsync(profile => profile.Id, cancellationToken);
			ProfileLinks.ApplyTemplateLink(template, profiles);
		}

		await ProfileService.LinkTemplateAsync(_dbContext, template, cancellationToken);
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
