using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.VersionControl;

namespace VibeSwarm.Web.Services;

/// <summary>
/// Runs Lead reviews in the background, one per project at a time, so a review that takes many
/// minutes neither holds a request open nor dies with the page that started it.
/// </summary>
public sealed class LeadReviewRunner
{
	private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();
	private readonly IServiceScopeFactory _scopeFactory;
	private readonly ILogger<LeadReviewRunner> _logger;

	public LeadReviewRunner(IServiceScopeFactory scopeFactory, ILogger<LeadReviewRunner> logger)
	{
		_scopeFactory = scopeFactory;
		_logger = logger;
	}

	public bool IsRunning(Guid projectId) => _running.ContainsKey(projectId);

	public bool TryStart(Guid projectId, Guid reviewId)
	{
		var cts = new CancellationTokenSource();
		if (!_running.TryAdd(projectId, cts))
		{
			cts.Dispose();
			return false;
		}

		_ = Task.Run(async () =>
		{
			try
			{
				using var scope = _scopeFactory.CreateScope();
				await scope.ServiceProvider.GetRequiredService<LeadService>().RunReviewAsync(reviewId, cts.Token);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Lead review {ReviewId} for project {ProjectId} crashed", reviewId, projectId);
			}
			finally
			{
				_running.TryRemove(projectId, out _);
				cts.Dispose();
			}
		});
		return true;
	}

	public void Cancel(Guid projectId)
	{
		if (_running.TryGetValue(projectId, out var cts))
		{
			cts.Cancel();
		}
	}
}

/// <summary>What auto-pilot asks of the Lead when it runs out of approved ideas.</summary>
public interface ILeadAutomation
{
	/// <summary>Starts a review for auto-pilot; false when the project has no Lead or one couldn't start.</summary>
	Task<bool> TryStartReviewForAutoPilotAsync(Guid projectId, CancellationToken cancellationToken);

	Task NudgeOwnerAsync(Project project, string title, string body, CancellationToken cancellationToken);
}

public sealed class LeadService : ILeadService, ILeadAutomation
{
	public const string ManualTrigger = "manual";
	public const string AutoPilotTrigger = "auto-pilot";

	internal const int MaxRecentJobs = 15;
	internal const int MaxDismissedListed = 30;
	internal static readonly TimeSpan ReviewTimeout = TimeSpan.FromMinutes(60);

	private readonly VibeSwarmDbContext _db;
	private readonly IReadOnlyConsultService _consult;
	private readonly IGitCommandExecutor _git;
	private readonly ISystemCommandRunner _commands;
	private readonly LeadReviewRunner _runner;
	private readonly IServiceProvider _services;
	private readonly ILogger<LeadService> _logger;

	public LeadService(
		VibeSwarmDbContext db,
		IReadOnlyConsultService consult,
		IGitCommandExecutor git,
		ISystemCommandRunner commands,
		LeadReviewRunner runner,
		IServiceProvider services,
		ILogger<LeadService> logger)
	{
		_db = db;
		_consult = consult;
		_git = git;
		_commands = commands;
		_runner = runner;
		_services = services;
		_logger = logger;
	}

	/// <summary>Where a review's copy of the repository goes while the Lead reads it.</summary>
	internal string SnapshotRoot { get; set; } = Path.Combine(Path.GetTempPath(), "vibeswarm-lead");

	public async Task<LeadStatus> GetStatusAsync(Guid projectId, CancellationToken cancellationToken = default)
	{
		var project = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
		if (project == null)
		{
			return new LeadStatus();
		}

		await MarkInterruptedReviewsAsync(projectId, cancellationToken);
		return new LeadStatus
		{
			IsConfigured = project.LeadProviderId.HasValue,
			IsRunning = _runner.IsRunning(projectId),
			LatestReview = await _db.LeadReviews.AsNoTracking()
				.Where(r => r.ProjectId == projectId)
				.OrderByDescending(r => r.CreatedAt)
				.FirstOrDefaultAsync(cancellationToken),
			Suggestions = await PendingSuggestions(projectId).AsNoTracking().ToListAsync(cancellationToken)
		};
	}

	public async Task<IReadOnlyList<LeadReview>> GetReviewsAsync(Guid projectId, CancellationToken cancellationToken = default)
		=> await _db.LeadReviews.AsNoTracking()
			.Where(r => r.ProjectId == projectId)
			.OrderByDescending(r => r.CreatedAt)
			.Take(20)
			.ToListAsync(cancellationToken);

	public Task<LeadReview> StartReviewAsync(Guid projectId, CancellationToken cancellationToken = default)
		=> StartReviewAsync(projectId, ManualTrigger, cancellationToken);

	public async Task<LeadReview> StartReviewAsync(Guid projectId, string trigger, CancellationToken cancellationToken)
	{
		var project = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken)
			?? throw new InvalidOperationException("Project not found.");
		if (!project.LeadProviderId.HasValue)
		{
			throw new InvalidOperationException("Choose a Lead in the project's settings first.");
		}

		await MarkInterruptedReviewsAsync(projectId, cancellationToken);
		var running = await _db.LeadReviews.AsNoTracking()
			.Where(r => r.ProjectId == projectId && r.Status == LeadReviewStatus.Running)
			.OrderByDescending(r => r.CreatedAt)
			.FirstOrDefaultAsync(cancellationToken);
		if (running != null)
		{
			return running;
		}

		var previous = await _db.LeadReviews.AsNoTracking()
			.Where(r => r.ProjectId == projectId && r.Status == LeadReviewStatus.Completed)
			.OrderByDescending(r => r.CreatedAt)
			.Select(r => (Guid?)r.Id)
			.FirstOrDefaultAsync(cancellationToken);
		var review = new LeadReview { ProjectId = projectId, Trigger = trigger, PreviousReviewId = previous };
		_db.LeadReviews.Add(review);
		await _db.SaveChangesAsync(cancellationToken);

		if (!_runner.TryStart(projectId, review.Id))
		{
			review.Status = LeadReviewStatus.Cancelled;
			review.Error = "Another review was already running.";
			review.CompletedAt = DateTime.UtcNow;
			await _db.SaveChangesAsync(cancellationToken);
		}

		return review;
	}

	public async Task<bool> TryStartReviewForAutoPilotAsync(Guid projectId, CancellationToken cancellationToken)
	{
		try
		{
			var review = await StartReviewAsync(projectId, AutoPilotTrigger, cancellationToken);
			return review.Status == LeadReviewStatus.Running;
		}
		catch (InvalidOperationException ex)
		{
			_logger.LogInformation("Auto-pilot couldn't start a Lead review for {ProjectId}: {Reason}", projectId, ex.Message);
			return false;
		}
	}

	public Task NudgeOwnerAsync(Project project, string title, string body, CancellationToken cancellationToken)
		=> PushAsync(project, title, body, cancellationToken);

	public Task CancelReviewAsync(Guid projectId, CancellationToken cancellationToken = default)
	{
		_runner.Cancel(projectId);
		return Task.CompletedTask;
	}

	public async Task<Idea?> ApproveSuggestionAsync(Guid ideaId, CancellationToken cancellationToken = default)
	{
		var idea = await _db.Ideas.IgnoreQueryFilters()
			.FirstOrDefaultAsync(i => i.Id == ideaId && i.NeedsApproval && i.DismissedAt == null, cancellationToken);
		if (idea == null)
		{
			return null;
		}

		var lastInQueue = await _db.Ideas
			.Where(i => i.ProjectId == idea.ProjectId)
			.Select(i => (int?)i.SortOrder)
			.MaxAsync(cancellationToken);
		idea.NeedsApproval = false;
		idea.SortOrder = (lastInQueue ?? -1) + 1;
		if (!string.IsNullOrWhiteSpace(idea.ExpandedDescription))
		{
			idea.ExpansionStatus = IdeaExpansionStatus.Approved;
			idea.ExpandedAt ??= DateTime.UtcNow;
		}

		// A loop waiting for approved ideas has one now; the next wait deserves a new nudge.
		await _db.IterationLoops
			.Where(l => l.ProjectId == idea.ProjectId && l.ApprovalNudgeSentAt != null)
			.ExecuteUpdateAsync(setters => setters.SetProperty(l => l.ApprovalNudgeSentAt, (DateTime?)null), cancellationToken);
		await _db.SaveChangesAsync(cancellationToken);
		await NotifyIdeaCreatedAsync(idea);
		return idea;
	}

	public async Task DismissSuggestionAsync(Guid ideaId, CancellationToken cancellationToken = default)
	{
		var idea = await _db.Ideas.IgnoreQueryFilters()
			.FirstOrDefaultAsync(i => i.Id == ideaId && i.NeedsApproval && i.DismissedAt == null, cancellationToken);
		if (idea == null)
		{
			return;
		}

		idea.DismissedAt = DateTime.UtcNow;
		await _db.SaveChangesAsync(cancellationToken);
	}

	/// <summary>
	/// The review itself: reads origin's latest code in a copy of its own, asks the Lead, and
	/// stores the report and the ideas. Every way it can end leaves the review marked finished.
	/// </summary>
	internal async Task RunReviewAsync(Guid reviewId, CancellationToken cancellationToken)
	{
		var review = await _db.LeadReviews.FirstOrDefaultAsync(r => r.Id == reviewId, cancellationToken);
		var project = review == null ? null : await _db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == review.ProjectId, cancellationToken);
		if (review == null || project == null)
		{
			return;
		}

		var stopwatch = Stopwatch.StartNew();
		var snapshot = Path.Combine(SnapshotRoot, review.Id.ToString("N"));
		try
		{
			var copyError = await CopyLatestCodeAsync(project, snapshot, cancellationToken);
			if (copyError != null)
			{
				await FinishAsync(review, LeadReviewStatus.Failed, copyError, stopwatch);
				return;
			}

			var prompt = LeadPrompts.BuildPrompt(await BuildContextAsync(project, review, cancellationToken));
			var result = await _consult.ConsultAsync(new ConsultRequest
			{
				Target = new ConsultTarget(project.LeadProviderId, null, project.LeadModelId, project.LeadReasoningEffort),
				Prompt = prompt,
				SystemPrompt = LeadPrompts.SystemPrompt,
				WorkingDirectory = snapshot,
				Title = $"Lead review of {project.Name}",
				TaskType = LeadPrompts.TaskType,
				Timeout = ReviewTimeout
			}, cancellationToken);

			review.ProviderName = result.ProviderName;
			review.ModelId = result.ModelUsed ?? project.LeadModelId;
			review.InputTokens = result.InputTokens;
			review.OutputTokens = result.OutputTokens;
			review.CostUsd = result.CostUsd;
			if (!result.Success)
			{
				await FinishAsync(review, LeadReviewStatus.Failed, result.Error ?? "The Lead gave no answer.", stopwatch);
				return;
			}

			var reply = LeadPrompts.Parse(result.Text);
			review.Content = Truncate(reply.Report.Length == 0 ? "The Lead wrote no report." : reply.Report, LeadReviewLimits.ContentMaxLength);
			review.IdeasCreated = await AddSuggestionsAsync(project.Id, review.Id, reply.Ideas, cancellationToken);
			await FinishAsync(review, LeadReviewStatus.Completed, null, stopwatch);
			_logger.LogInformation("Lead review of {Project} suggested {Count} ideas", project.Name, review.IdeasCreated);

			if (review.IdeasCreated > 0)
			{
				await PushAsync(project, $"The Lead suggested {review.IdeasCreated} idea{(review.IdeasCreated == 1 ? "" : "s")}", "Approve the ones you want built.", cancellationToken);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			await FinishAsync(review, LeadReviewStatus.Cancelled, "Cancelled.", stopwatch);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Lead review of {Project} failed", project.Name);
			await FinishAsync(review, LeadReviewStatus.Failed, ex.Message, stopwatch);
		}
		finally
		{
			TryDelete(snapshot);
		}
	}

	/// <summary>Sends the owner a push; the URL opens the project, where suggestions wait.</summary>
	internal async Task PushAsync(Project project, string title, string body, CancellationToken cancellationToken)
	{
		try
		{
			var push = _services.GetService<PushNotificationService>();
			if (push != null)
			{
				await push.SendAsync(
					JobPushEvent.InputRequired,
					new PushMessage($"{project.Name}: {title}", body, $"/projects/{project.Id}", $"lead-{project.Id}"),
					cancellationToken);
			}
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			_logger.LogDebug(ex, "Could not send the Lead's push for {Project}", project.Name);
		}
	}

	private IQueryable<Idea> PendingSuggestions(Guid projectId)
		=> _db.Ideas.IgnoreQueryFilters()
			.Where(i => i.ProjectId == projectId && i.NeedsApproval && i.DismissedAt == null)
			.OrderBy(i => i.SortOrder)
			.ThenBy(i => i.CreatedAt);

	/// <summary>
	/// Copies the branch jobs deliver to, as it is on origin, into a plain directory: no git
	/// worktree for a running job to sweep up, nothing a job's reset can change underneath.
	/// </summary>
	private async Task<string?> CopyLatestCodeAsync(Project project, string snapshot, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(project.WorkingPath) || !Directory.Exists(project.WorkingPath))
		{
			return "The project's folder doesn't exist.";
		}

		var branch = project.DevelopmentBranch?.Trim();
		if (string.IsNullOrEmpty(branch))
		{
			var head = await _git.ExecuteAsync("rev-parse --abbrev-ref HEAD", project.WorkingPath, cancellationToken);
			branch = head.Success ? head.Output.Trim() : null;
		}

		if (string.IsNullOrEmpty(branch) || branch == "HEAD" || !IsSafeRefName(branch))
		{
			return "Couldn't tell which branch to review.";
		}

		var gitRef = branch;
		var hasOrigin = (await _git.ExecuteAsync("remote get-url origin", project.WorkingPath, cancellationToken)).Success;
		if (hasOrigin)
		{
			var fetch = await _git.ExecuteAsync($"fetch -q origin {branch}", project.WorkingPath, cancellationToken, timeoutSeconds: 180);
			if (!fetch.Success)
			{
				return $"Couldn't reach origin to get the latest {branch}, so the review didn't run on stale code.";
			}

			gitRef = $"origin/{branch}";
		}

		TryDelete(snapshot);
		Directory.CreateDirectory(snapshot);
		var archive = await _commands.RunAsync($"git archive --format=tar '{gitRef}' | tar -x -C '{snapshot}'", project.WorkingPath, cancellationToken: cancellationToken);
		return archive.Success ? null : $"Couldn't copy {gitRef} for the review: {archive.ErrorMessage}";
	}

	private async Task<LeadContext> BuildContextAsync(Project project, LeadReview review, CancellationToken cancellationToken)
	{
		var section = await new ProjectContextBuilder(_db, _git, _logger).BuildProjectSectionAsync(
			project,
			new ProjectContextOptions { MaxRecentJobs = MaxRecentJobs, MaxSectionLength = 12_000, IncludeReviewNotes = true },
			cancellationToken);
		var waiting = await PendingSuggestions(project.Id).AsNoTracking().Select(i => i.Description).ToListAsync(cancellationToken);
		var dismissed = await _db.Ideas.IgnoreQueryFilters().AsNoTracking()
			.Where(i => i.ProjectId == project.Id && i.DismissedAt != null)
			.OrderByDescending(i => i.DismissedAt)
			.Take(MaxDismissedListed)
			.Select(i => i.Description)
			.ToListAsync(cancellationToken);
		var previous = review.PreviousReviewId is Guid previousId
			? await _db.LeadReviews.AsNoTracking().FirstOrDefaultAsync(r => r.Id == previousId, cancellationToken)
			: null;

		return new LeadContext(
			project.Name,
			project.LeadDirection,
			project.Description,
			project.PromptContext,
			project.Memory,
			project.RepoMap,
			section,
			waiting,
			dismissed,
			previous,
			DateTime.UtcNow);
	}

	/// <summary>Adds the Lead's ideas as suggestions, skipping any the project already has.</summary>
	internal async Task<int> AddSuggestionsAsync(Guid projectId, Guid reviewId, IReadOnlyList<LeadIdea> ideas, CancellationToken cancellationToken)
	{
		if (ideas.Count == 0)
		{
			return 0;
		}

		var known = (await _db.Ideas.IgnoreQueryFilters().AsNoTracking()
				.Where(i => i.ProjectId == projectId)
				.Select(i => i.Description)
				.ToListAsync(cancellationToken))
			.Select(LeadPrompts.NormalizeForComparison)
			.ToHashSet(StringComparer.Ordinal);
		var nextRank = await _db.Ideas.IgnoreQueryFilters()
			.Where(i => i.ProjectId == projectId && i.NeedsApproval && i.DismissedAt == null)
			.Select(i => (int?)i.SortOrder)
			.MaxAsync(cancellationToken) ?? -1;

		var added = new List<Idea>();
		foreach (var idea in ideas)
		{
			if (!known.Add(LeadPrompts.NormalizeForComparison(idea.Title)))
			{
				continue;
			}

			added.Add(new Idea
			{
				ProjectId = projectId,
				Description = idea.Title,
				ExpandedDescription = idea.Spec,
				ExpansionStatus = IdeaExpansionStatus.PendingReview,
				ExpandedAt = DateTime.UtcNow,
				Source = IdeaSource.Lead,
				NeedsApproval = true,
				LeadReviewId = reviewId,
				SortOrder = ++nextRank
			});
		}

		_db.Ideas.AddRange(added);
		await _db.SaveChangesAsync(cancellationToken);
		return added.Count;
	}

	/// <summary>A review left Running by a restart can never finish; say so instead of spinning forever.</summary>
	private async Task MarkInterruptedReviewsAsync(Guid projectId, CancellationToken cancellationToken)
	{
		if (_runner.IsRunning(projectId))
		{
			return;
		}

		await _db.LeadReviews
			.Where(r => r.ProjectId == projectId && r.Status == LeadReviewStatus.Running)
			.ExecuteUpdateAsync(setters => setters
				.SetProperty(r => r.Status, LeadReviewStatus.Failed)
				.SetProperty(r => r.Error, "Interrupted by a restart.")
				.SetProperty(r => r.CompletedAt, DateTime.UtcNow),
				cancellationToken);
	}

	private async Task FinishAsync(LeadReview review, LeadReviewStatus status, string? error, Stopwatch stopwatch)
	{
		review.Status = status;
		review.Error = error == null ? null : Truncate(error, 2000);
		review.CompletedAt = DateTime.UtcNow;
		review.DurationMs = stopwatch.ElapsedMilliseconds;
		await _db.SaveChangesAsync(CancellationToken.None);
	}

	private async Task NotifyIdeaCreatedAsync(Idea idea)
	{
		try
		{
			var updates = _services.GetService<IJobUpdateService>();
			if (updates != null)
			{
				await updates.NotifyIdeaCreated(idea.Id, idea.ProjectId);
			}
		}
		catch (Exception ex)
		{
			_logger.LogDebug(ex, "Could not announce approved idea {IdeaId}", idea.Id);
		}
	}

	private void TryDelete(string path)
	{
		try
		{
			if (Directory.Exists(path))
			{
				Directory.Delete(path, recursive: true);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			_logger.LogDebug(ex, "Could not remove the Lead's copy at {Path}", path);
		}
	}

	private static bool IsSafeRefName(string name)
		=> name.All(c => char.IsLetterOrDigit(c) || c is '/' or '-' or '_' or '.') && !name.Contains("..", StringComparison.Ordinal);

	private static string Truncate(string text, int maxLength)
		=> text.Length > maxLength ? text[..maxLength] : text;
}
