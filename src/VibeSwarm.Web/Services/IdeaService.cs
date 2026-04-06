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
	private readonly IProjectMemoryService _projectMemoryService;
	private readonly IInferenceService? _inferenceService;
	private readonly IJobUpdateService? _jobUpdateService;
	private readonly ILogger<IdeaService> _logger;

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
		IProjectMemoryService projectMemoryService,
		ILogger<IdeaService> logger,
		IInferenceService? inferenceService = null,
		IJobUpdateService? jobUpdateService = null)
	{
		_dbContext = dbContext;
		_jobService = jobService;
		_providerService = providerService;
		_versionControlService = versionControlService;
		_projectMemoryService = projectMemoryService;
		_logger = logger;
		_inferenceService = inferenceService;
		_jobUpdateService = jobUpdateService;
	}

	public async Task<IEnumerable<Idea>> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default)
	{
		return await _dbContext.Ideas
			.Include(i => i.Attachments)
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
				.Include(i => i.Attachments)
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
			.Include(i => i.Attachments)
			.FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
	}

	public async Task<Idea> CreateAsync(Idea idea, CancellationToken cancellationToken = default)
	{
		return await CreateAsync(new CreateIdeaRequest
		{
			ProjectId = idea.ProjectId,
			Description = idea.Description
		}, cancellationToken);
	}

	public async Task<Idea> CreateAsync(CreateIdeaRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		var trimmedDescription = request.Description?.Trim() ?? string.Empty;
		var idea = new Idea
		{
			ProjectId = request.ProjectId,
			Description = trimmedDescription
		};

		ValidateIdea(idea);
		ValidateAttachmentUploads(request.Attachments);

		var project = await _dbContext.Projects
			.FirstOrDefaultAsync(project => project.Id == request.ProjectId, cancellationToken)
			?? throw new InvalidOperationException($"Project with ID {request.ProjectId} not found.");

		var duplicateCutoff = DateTime.UtcNow.AddSeconds(-10);
		var existingDuplicate = await _dbContext.Ideas
			.Include(existing => existing.Attachments)
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
		project.UpdatedAt = DateTime.UtcNow;

		var maxSortOrder = await _dbContext.Ideas
			.Where(i => i.ProjectId == idea.ProjectId)
			.MaxAsync(i => (int?)i.SortOrder, cancellationToken) ?? -1;
		idea.SortOrder = maxSortOrder + 1;

		var persistedAttachments = await PersistIdeaAttachmentsAsync(project, idea.Id, request.Attachments, cancellationToken);
		foreach (var attachment in persistedAttachments)
		{
			idea.Attachments.Add(attachment);
		}

		_dbContext.Ideas.Add(idea);
		await _dbContext.SaveChangesAsync(cancellationToken);

		if (_jobUpdateService != null)
		{
			try
			{
				await _jobUpdateService.NotifyIdeaCreated(idea.Id, idea.ProjectId);
			}
			catch { }
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
		var project = await _dbContext.Projects.FindAsync(new object[] { existing.ProjectId }, cancellationToken);
		if (project != null)
		{
			project.UpdatedAt = DateTime.UtcNow;
		}

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
		var idea = await _dbContext.Ideas
			.Include(existing => existing.Project)
			.Include(existing => existing.Attachments)
			.FirstOrDefaultAsync(existing => existing.Id == id, cancellationToken);
		if (idea != null)
		{
			var projectId = idea.ProjectId;
			if (idea.Project != null)
			{
				idea.Project.UpdatedAt = DateTime.UtcNow;
			}
			await DeleteAttachmentFilesAsync(idea.Attachments, idea.Project?.WorkingPath);

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
			.Include(i => i.Project)
			.Include(i => i.Attachments)
			.FirstOrDefaultAsync(i => i.JobId == jobId, cancellationToken);
	}

	public async Task<IdeaAttachment?> GetAttachmentAsync(Guid attachmentId, CancellationToken cancellationToken = default)
	{
		return await _dbContext.IdeaAttachments
			.Include(attachment => attachment.Idea)
				.ThenInclude(idea => idea!.Project)
			.FirstOrDefaultAsync(attachment => attachment.Id == attachmentId, cancellationToken);
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

	private static void ValidateAttachmentUploads(IReadOnlyCollection<IdeaAttachmentUpload>? attachments)
	{
		if (attachments == null || attachments.Count == 0)
		{
			return;
		}

		if (attachments.Count > ValidationLimits.IdeaAttachmentMaxCount)
		{
			throw new ValidationException($"Ideas can include at most {ValidationLimits.IdeaAttachmentMaxCount} attachments.");
		}

		long totalBytes = 0;
		foreach (var attachment in attachments)
		{
			if (attachment == null)
			{
				throw new ValidationException("Attachment payload is required.");
			}

			var fileName = Path.GetFileName(attachment.FileName?.Trim() ?? string.Empty);
			if (string.IsNullOrWhiteSpace(fileName))
			{
				throw new ValidationException("Attachment file name is required.");
			}

			if (fileName.Length > ValidationLimits.IdeaAttachmentFileNameMaxLength)
			{
				throw new ValidationException($"Attachment file names must be {ValidationLimits.IdeaAttachmentFileNameMaxLength} characters or fewer.");
			}

			if (attachment.Content == null || attachment.Content.Length == 0)
			{
				throw new ValidationException($"Attachment '{fileName}' is empty.");
			}

			if (attachment.Content.LongLength > ValidationLimits.IdeaAttachmentMaxFileBytes)
			{
				throw new ValidationException($"Attachment '{fileName}' exceeds the {ValidationLimits.IdeaAttachmentMaxFileBytes / (1024 * 1024)} MB limit.");
			}

			totalBytes += attachment.Content.LongLength;
		}

		if (totalBytes > ValidationLimits.IdeaAttachmentMaxTotalBytes)
		{
			throw new ValidationException($"Idea attachments exceed the total limit of {ValidationLimits.IdeaAttachmentMaxTotalBytes / (1024 * 1024)} MB.");
		}
	}

	private async Task<List<IdeaAttachment>> PersistIdeaAttachmentsAsync(Project project, Guid ideaId, IReadOnlyCollection<IdeaAttachmentUpload>? attachments, CancellationToken cancellationToken)
	{
		var persisted = new List<IdeaAttachment>();
		if (attachments == null || attachments.Count == 0)
		{
			return persisted;
		}

		var workingPath = project.WorkingPath?.Trim();
		if (string.IsNullOrWhiteSpace(workingPath) || !Directory.Exists(workingPath))
		{
			throw new ValidationException("Project working directory was not found, so idea attachments cannot be stored.");
		}

		await _projectMemoryService.EnsureGitExcludeAsync(workingPath, cancellationToken);

		var rootDirectory = Path.Combine(workingPath, ".vibeswarm", "idea-attachments", ideaId.ToString("N"));
		Directory.CreateDirectory(rootDirectory);

		var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var upload in attachments)
		{
			var safeFileName = Path.GetFileName(upload.FileName.Trim());
			var storedFileName = BuildUniqueAttachmentFileName(rootDirectory, safeFileName, usedNames);
			var absolutePath = Path.Combine(rootDirectory, storedFileName);
			await File.WriteAllBytesAsync(absolutePath, upload.Content, cancellationToken);

			persisted.Add(new IdeaAttachment
			{
				Id = Guid.NewGuid(),
				IdeaId = ideaId,
				FileName = safeFileName,
				ContentType = string.IsNullOrWhiteSpace(upload.ContentType) ? null : upload.ContentType.Trim(),
				RelativePath = Path.GetRelativePath(workingPath, absolutePath),
				SizeBytes = upload.Content.LongLength,
				CreatedAt = DateTime.UtcNow
			});
		}

		return persisted;
	}

	private static string BuildUniqueAttachmentFileName(string directoryPath, string originalFileName, HashSet<string> usedNames)
	{
		var baseName = Path.GetFileNameWithoutExtension(originalFileName);
		var extension = Path.GetExtension(originalFileName);
		var candidate = originalFileName;
		var suffix = 1;

		while (File.Exists(Path.Combine(directoryPath, candidate)) || !usedNames.Add(candidate))
		{
			candidate = $"{baseName}-{suffix++}{extension}";
		}

		return candidate;
	}

	private Task DeleteAttachmentFilesAsync(IEnumerable<IdeaAttachment>? attachments, string? workingPath)
	{
		if (attachments == null || string.IsNullOrWhiteSpace(workingPath))
		{
			return Task.CompletedTask;
		}

		var normalizedRoot = Path.GetFullPath(workingPath);
		foreach (var attachment in attachments)
		{
			if (string.IsNullOrWhiteSpace(attachment.RelativePath))
			{
				continue;
			}

			try
			{
				var fullPath = Path.GetFullPath(Path.Combine(normalizedRoot, attachment.RelativePath));
				if (!fullPath.StartsWith(normalizedRoot, StringComparison.Ordinal))
				{
					continue;
				}

				if (File.Exists(fullPath))
				{
					File.Delete(fullPath);
				}

				var directory = Path.GetDirectoryName(fullPath);
				while (!string.IsNullOrWhiteSpace(directory) && !string.Equals(directory, normalizedRoot, StringComparison.Ordinal))
				{
					if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
					{
						Directory.Delete(directory);
						directory = Path.GetDirectoryName(directory);
						continue;
					}

					break;
				}
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Failed to delete idea attachment file {AttachmentRelativePath}", attachment.RelativePath);
			}
		}

		return Task.CompletedTask;
	}
}
