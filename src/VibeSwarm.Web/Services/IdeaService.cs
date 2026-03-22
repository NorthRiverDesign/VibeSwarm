using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Inference;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Validation;
using VibeSwarm.Shared.VersionControl;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Shared.Services;

public partial class IdeaService : IIdeaService
{
	private const int DefaultIdeaPageSize = 10;
	private const int MaxPageSize = 100;

	private readonly VibeSwarmDbContext _dbContext;
	private readonly IJobService _jobService;
	private readonly IProviderService _providerService;
	private readonly IVersionControlService _versionControlService;
	private readonly IInferenceService? _inferenceService;
	private readonly IJobUpdateService? _jobUpdateService;
	private readonly ILogger<IdeaService> _logger;

	/// <summary>
	/// Timeout for AI expansion operations (5 minutes)
	/// </summary>
	private static readonly TimeSpan ExpansionTimeout = TimeSpan.FromMinutes(5);

	/// <summary>
	/// Global lock to prevent race conditions when converting ideas to jobs
	/// </summary>
	private static readonly SemaphoreSlim _ideaConversionLock = new(1, 1);

	/// <summary>
	/// Lock for toggling ideas processing state
	/// </summary>
	private static readonly SemaphoreSlim _processingStateLock = new(1, 1);
	private static readonly string[] UserImpactSuggestionKeywords =
	[
		"user",
		"users",
		"customer",
		"customers",
		"ux",
		"page",
		"screen",
		"dashboard",
		"form",
		"workflow",
		"onboarding",
		"search",
		"filter",
		"notification",
		"mobile",
		"accessibility",
		"empty state",
		"validation",
		"security",
		"performance",
		"reliability",
		"error handling",
		"retry",
		"auth",
		"permission",
		"export",
		"import"
	];
	private static readonly string[] DevelopmentOnlySuggestionKeywords =
	[
		"test",
		"testing",
		"coverage",
		"developer experience",
		"documentation",
		"docs",
		"comment",
		"comments",
		"refactor",
		"code cleanup",
		"lint",
		"formatting",
		"ci/cd",
		"continuous integration",
		"pipeline",
		"build script"
	];
	private static readonly string[] DevelopmentOnlySuggestionPrefixes =
	[
		"add tests",
		"write tests",
		"expand test coverage",
		"increase test coverage",
		"improve test coverage",
		"add unit tests",
		"add integration tests",
		"refactor",
		"improve developer experience",
		"update documentation",
		"add documentation"
	];

	public IdeaService(
		VibeSwarmDbContext dbContext,
		IJobService jobService,
		IProviderService providerService,
		IVersionControlService versionControlService,
		ILogger<IdeaService> logger,
		IInferenceService? inferenceService = null,
		IJobUpdateService? jobUpdateService = null)
	{
		_dbContext = dbContext;
		_jobService = jobService;
		_providerService = providerService;
		_versionControlService = versionControlService;
		_logger = logger;
		_inferenceService = inferenceService;
		_jobUpdateService = jobUpdateService;
	}

	public async Task<IEnumerable<Idea>> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default)
	{
		return await _dbContext.Ideas
			.Include(i => i.Job)
			.Where(i => i.ProjectId == projectId)
			.OrderBy(i => i.SortOrder)
			.ThenBy(i => i.CreatedAt)
			.ToListAsync(cancellationToken);
	}

	public async Task<ProjectIdeasListResult> GetPagedByProjectIdAsync(Guid projectId, int page = 1, int pageSize = DefaultIdeaPageSize, CancellationToken cancellationToken = default)
	{
		var normalizedPageSize = NormalizePageSize(pageSize, DefaultIdeaPageSize);
		var baseQuery = _dbContext.Ideas
			.Where(i => i.ProjectId == projectId);

		var totalCount = await baseQuery.CountAsync(cancellationToken);
		var normalizedPage = NormalizePageNumber(page, normalizedPageSize, totalCount);

		return new ProjectIdeasListResult
		{
			PageNumber = normalizedPage,
			PageSize = normalizedPageSize,
			TotalCount = totalCount,
			UnprocessedCount = await baseQuery.CountAsync(i => !i.IsProcessing && i.JobId == null, cancellationToken),
			Items = await baseQuery
				.Include(i => i.Job)
				.OrderBy(i => i.SortOrder)
				.ThenBy(i => i.CreatedAt)
				.Skip((normalizedPage - 1) * normalizedPageSize)
				.Take(normalizedPageSize)
				.ToListAsync(cancellationToken)
		};
	}

	public async Task<Idea?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
	{
		return await _dbContext.Ideas
			.Include(i => i.Project)
			.FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
	}

	public async Task<Idea> CreateAsync(Idea idea, CancellationToken cancellationToken = default)
	{
		ValidateIdea(idea);

		// Check for duplicate: same project + description within the last 10 seconds
		var duplicateCutoff = DateTime.UtcNow.AddSeconds(-10);
		var existingDuplicate = await _dbContext.Ideas
			.FirstOrDefaultAsync(i => i.ProjectId == idea.ProjectId
				&& i.Description == idea.Description
				&& i.CreatedAt >= duplicateCutoff, cancellationToken);

		if (existingDuplicate != null)
		{
			_logger.LogWarning("Duplicate idea rejected for project {ProjectId}: \"{Description}\" (existing idea {IdeaId} created at {CreatedAt})",
				idea.ProjectId, idea.Description?.Length > 80 ? idea.Description[..80] + "..." : idea.Description,
				existingDuplicate.Id, existingDuplicate.CreatedAt);
			return existingDuplicate;
		}

		idea.Id = Guid.NewGuid();
		idea.CreatedAt = DateTime.UtcNow;

		// Set sort order to the next available value
		var maxSortOrder = await _dbContext.Ideas
			.Where(i => i.ProjectId == idea.ProjectId)
			.MaxAsync(i => (int?)i.SortOrder, cancellationToken) ?? -1;
		idea.SortOrder = maxSortOrder + 1;

		_dbContext.Ideas.Add(idea);
		await _dbContext.SaveChangesAsync(cancellationToken);

		// Notify all clients about the new idea
		if (_jobUpdateService != null)
		{
			try
			{
				await _jobUpdateService.NotifyIdeaCreated(idea.Id, idea.ProjectId);
			}
			catch { /* Don't fail creation if notification fails */ }
		}

		return idea;
	}

	public async Task<Idea> UpdateAsync(Idea idea, CancellationToken cancellationToken = default)
	{
		var existing = await _dbContext.Ideas.FindAsync(new object[] { idea.Id }, cancellationToken);
		if (existing == null)
		{
			throw new InvalidOperationException($"Idea with ID {idea.Id} not found.");
		}

		existing.Description = idea.Description;
		existing.SortOrder = idea.SortOrder;
		ValidateIdea(existing);

		await _dbContext.SaveChangesAsync(cancellationToken);

		// Notify all clients about the update
		if (_jobUpdateService != null)
		{
			try
			{
				await _jobUpdateService.NotifyIdeaUpdated(existing.Id, existing.ProjectId);
			}
			catch { /* Don't fail update if notification fails */ }
		}

		return existing;
	}

	public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
	{
		var idea = await _dbContext.Ideas.FindAsync(new object[] { id }, cancellationToken);
		if (idea != null)
		{
			var projectId = idea.ProjectId;

			// Cancel the associated job if it is still queued (not yet running)
			if (idea.JobId.HasValue)
			{
				try
				{
					await _jobService.RequestCancellationAsync(idea.JobId.Value, cancellationToken);
				}
				catch { /* Don't fail deletion if job cancellation fails */ }
			}

			_dbContext.Ideas.Remove(idea);
			await _dbContext.SaveChangesAsync(cancellationToken);

			// Notify all clients about the deletion
			if (_jobUpdateService != null)
			{
				try
				{
					await _jobUpdateService.NotifyIdeaDeleted(id, projectId);
				}
				catch { /* Don't fail deletion if notification fails */ }
			}
		}
	}

	public async Task<Idea?> GetNextUnprocessedAsync(Guid projectId, CancellationToken cancellationToken = default)
	{
		return await _dbContext.Ideas
			.Where(i => i.ProjectId == projectId && !i.IsProcessing && i.JobId == null)
			.OrderBy(i => i.SortOrder)
			.ThenBy(i => i.CreatedAt)
			.FirstOrDefaultAsync(cancellationToken);
	}


	public async Task<Idea?> GetByJobIdAsync(Guid jobId, CancellationToken cancellationToken = default)
	{
		return await _dbContext.Ideas
			.FirstOrDefaultAsync(i => i.JobId == jobId, cancellationToken);
	}

	private static void EnsureLengthWithinLimit(string fieldName, string? value, int maxLength)
	{
		if (!string.IsNullOrEmpty(value) && value.Length > maxLength)
		{
			throw new ValidationException($"{fieldName} must be {maxLength:N0} characters or fewer.");
		}
	}

	private static int NormalizePageSize(int pageSize, int defaultPageSize)
	{
		if (pageSize <= 0)
		{
			return defaultPageSize;
		}

		return Math.Min(pageSize, MaxPageSize);
	}

	private static int NormalizePageNumber(int pageNumber, int pageSize, int totalCount)
	{
		if (totalCount <= 0)
		{
			return 1;
		}

		var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
		return Math.Min(Math.Max(pageNumber, 1), totalPages);
	}
}
