using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// Coordinates job assignment and execution across multiple providers.
/// Implements load balancing, provider selection, and job routing.
/// </summary>
public class JobCoordinatorService : IJobCoordinatorService
{
	private readonly IServiceScopeFactory _scopeFactory;
	private readonly ILogger<JobCoordinatorService> _logger;
	private readonly IProviderHealthTracker _healthTracker;
	private readonly JobQueueManager _queueManager;
	private readonly SemaphoreSlim _coordinatorLock = new(1, 1);

	private sealed record ProviderCandidate(Provider Provider, ProjectProvider? ProjectSelection, ProviderHealth Health, double Score);

	private static DateTime? GetLaterCooldown(DateTime? firstCooldownUntil, DateTime? secondCooldownUntil)
	{
		if (!firstCooldownUntil.HasValue)
		{
			return secondCooldownUntil;
		}

		if (!secondCooldownUntil.HasValue)
		{
			return firstCooldownUntil;
		}

		return firstCooldownUntil.Value >= secondCooldownUntil.Value
			? firstCooldownUntil
			: secondCooldownUntil;
	}

	/// <summary>
	/// Maximum number of jobs that can be assigned to a single provider at once
	/// </summary>
	public int MaxJobsPerProvider { get; set; } = 1;

	/// <summary>
	/// Timeout for provider selection operations
	/// </summary>
	public TimeSpan ProviderSelectionTimeout { get; set; } = TimeSpan.FromSeconds(10);

	public JobCoordinatorService(
		IServiceScopeFactory scopeFactory,
		ILogger<JobCoordinatorService> logger,
		IProviderHealthTracker healthTracker,
		JobQueueManager queueManager)
	{
		_scopeFactory = scopeFactory;
		_logger = logger;
		_healthTracker = healthTracker;
		_queueManager = queueManager;
	}

	/// <summary>
	/// Selects the best provider for a job based on availability, health, and load
	/// </summary>
	public async Task<Provider?> SelectProviderForJobAsync(Job job, CancellationToken cancellationToken = default)
	{
		await _coordinatorLock.WaitAsync(cancellationToken);
		try
		{
			using var scope = _scopeFactory.CreateScope();
			var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
			var usageService = scope.ServiceProvider.GetService<IProviderUsageService>();

			var projectProviderSelections = await dbContext.ProjectProviders
				.Include(pp => pp.Provider)
				.Where(pp => pp.ProjectId == job.ProjectId && pp.IsEnabled)
				.OrderBy(pp => pp.Priority)
				.ToListAsync(cancellationToken);

			var allowedProviderIds = projectProviderSelections
				.Select(pp => pp.ProviderId)
				.ToHashSet();

			// If job has a specific provider assigned, validate and return it
			if (job.ProviderId != Guid.Empty)
			{
				var assignedProvider = await dbContext.Providers
					.FirstOrDefaultAsync(p => p.Id == job.ProviderId && p.IsEnabled, cancellationToken);

				if (assignedProvider != null)
				{
					var providerIsAllowed = allowedProviderIds.Count == 0 || allowedProviderIds.Contains(assignedProvider.Id);
					var health = _healthTracker.GetProviderHealth(assignedProvider.Id);
					var cooldownUntil = await GetProviderCooldownUntilAsync(assignedProvider.Id, health, dbContext, cancellationToken);
					var exhaustionWarning = usageService != null
						? await usageService.CheckExhaustionAsync(assignedProvider.Id, cancellationToken: cancellationToken)
						: null;
					if (providerIsAllowed && !cooldownUntil.HasValue && health.IsHealthy && health.CurrentLoad < MaxJobsPerProvider && exhaustionWarning?.IsExhausted != true)
					{
						_logger.LogDebug("Using assigned provider {ProviderId} for job {JobId}",
							assignedProvider.Id, job.Id);
						return assignedProvider;
					}

					_logger.LogWarning("Assigned provider {ProviderId} is not eligible for job {JobId}. Allowed={Allowed}, Healthy={Healthy}, Load={Load}, Exhausted={Exhausted}, CooldownUntil={CooldownUntil}",
						assignedProvider.Id,
						job.Id,
						providerIsAllowed,
						health.IsHealthy,
						health.CurrentLoad,
						exhaustionWarning?.IsExhausted == true,
						cooldownUntil);
				}
			}

			List<Provider> providers;
			if (projectProviderSelections.Count > 0)
			{
				providers = projectProviderSelections
					.Where(pp => pp.Provider is { IsEnabled: true })
					.Select(pp => pp.Provider!)
					.ToList();
			}
			else
			{
				providers = await dbContext.Providers
					.Where(p => p.IsEnabled)
					.ToListAsync(cancellationToken);
			}

			if (!providers.Any())
			{
				_logger.LogWarning("No enabled providers available for job {JobId}", job.Id);
				return null;
			}

			// Score and rank providers
			var scoredProviders = providers
				.Select(p => new
				{
					Provider = p,
					ProjectSelection = projectProviderSelections.FirstOrDefault(pp => pp.ProviderId == p.Id),
					Health = _healthTracker.GetProviderHealth(p.Id),
					Score = CalculateProviderScore(p, _healthTracker.GetProviderHealth(p.Id))
				})
				.ToList();

			var eligibleProviders = new List<ProviderCandidate>();
			foreach (var providerCandidate in scoredProviders)
			{
				var cooldownUntil = await GetProviderCooldownUntilAsync(providerCandidate.Provider.Id, providerCandidate.Health, dbContext, cancellationToken);
				var exhaustionWarning = usageService != null
					? await usageService.CheckExhaustionAsync(providerCandidate.Provider.Id, cancellationToken: cancellationToken)
					: null;

				if (cooldownUntil.HasValue || !providerCandidate.Health.IsHealthy || providerCandidate.Health.CurrentLoad >= MaxJobsPerProvider || exhaustionWarning?.IsExhausted == true)
				{
					continue;
				}

				eligibleProviders.Add(new ProviderCandidate(
					providerCandidate.Provider,
					providerCandidate.ProjectSelection,
					providerCandidate.Health,
					providerCandidate.Score));
			}

			var rankedProviders = eligibleProviders
				.OrderBy(x => x.ProjectSelection?.Priority ?? int.MaxValue)
				.ThenByDescending(x => x.Score)
				.ToList();

			if (!rankedProviders.Any())
			{
				_logger.LogWarning("No healthy providers with available capacity for job {JobId}", job.Id);
				return null;
			}

			var selected = rankedProviders.First();
			_logger.LogInformation("Selected provider {ProviderName} (priority: {Priority}, score: {Score:F2}) for job {JobId}",
				selected.Provider.Name, selected.ProjectSelection?.Priority ?? -1, selected.Score, job.Id);

			return selected.Provider;
		}
		finally
		{
			_coordinatorLock.Release();
		}
	}

	/// <summary>
	/// Assigns a job to a provider and updates tracking
	/// </summary>
	public async Task<bool> AssignJobToProviderAsync(Guid jobId, Guid providerId, CancellationToken cancellationToken = default)
	{
		await _coordinatorLock.WaitAsync(cancellationToken);
		try
		{
			using var scope = _scopeFactory.CreateScope();
			var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();

			var job = await dbContext.Jobs.FindAsync(new object[] { jobId }, cancellationToken);
			if (job == null)
			{
				_logger.LogError("Job {JobId} not found for assignment", jobId);
				return false;
			}

			var provider = await dbContext.Providers.FindAsync(new object[] { providerId }, cancellationToken);
			if (provider == null || !provider.IsEnabled)
			{
				_logger.LogError("Provider {ProviderId} not found or disabled for job assignment", providerId);
				return false;
			}

			// Verify provider health
			var health = _healthTracker.GetProviderHealth(providerId);
			var cooldownUntil = await GetProviderCooldownUntilAsync(providerId, health, dbContext, cancellationToken);
			if (cooldownUntil.HasValue)
			{
				_logger.LogWarning("Cannot assign job {JobId} to provider {ProviderId} while it is cooling down until {CooldownUntil:u}",
					jobId, providerId, cooldownUntil.Value);
				return false;
			}

			if (!health.IsHealthy)
			{
				_logger.LogWarning("Cannot assign job {JobId} to unhealthy provider {ProviderId}", jobId, providerId);
				return false;
			}

			if (health.CurrentLoad >= MaxJobsPerProvider)
			{
				_logger.LogWarning("Cannot assign job {JobId} to overloaded provider {ProviderId} (load: {Load}/{Max})",
					jobId, providerId, health.CurrentLoad, MaxJobsPerProvider);
				return false;
			}

			job.ProviderId = providerId;
			await dbContext.SaveChangesAsync(cancellationToken);

			// Update provider load tracking
			_healthTracker.IncrementProviderLoad(providerId);

			_logger.LogInformation("Assigned job {JobId} to provider {ProviderName}", jobId, provider.Name);
			return true;
		}
		finally
		{
			_coordinatorLock.Release();
		}
	}

	/// <summary>
	/// Releases a job from a provider (on completion or failure)
	/// </summary>
	public async Task ReleaseJobFromProviderAsync(Guid jobId, Guid providerId, bool success, CancellationToken cancellationToken = default)
	{
		_healthTracker.DecrementProviderLoad(providerId);

		if (!success)
		{
			_healthTracker.RecordFailure(providerId);
		}
		else
		{
			_healthTracker.RecordSuccess(providerId);
		}

		_logger.LogDebug("Released job {JobId} from provider {ProviderId} (success: {Success})",
			jobId, providerId, success);
	}

	/// <summary>
	/// Gets the next batch of jobs that can be executed
	/// </summary>
	public async Task<IReadOnlyList<JobAssignment>> GetNextJobAssignmentsAsync(
		int maxJobs,
		CancellationToken cancellationToken = default)
	{
		var assignments = new List<JobAssignment>();

		using var scope = _scopeFactory.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();

		// Get pending jobs ordered by priority and creation time
		var pendingJobs = await _queueManager.GetPendingJobsAsync(maxJobs * 2, cancellationToken);

		foreach (var job in pendingJobs)
		{
			if (assignments.Count >= maxJobs)
				break;

			var provider = await SelectProviderForJobAsync(job, cancellationToken);
			if (provider != null)
			{
				assignments.Add(new JobAssignment
				{
					Job = job,
					Provider = provider,
					AssignedAt = DateTime.UtcNow
				});

				// Pre-increment load to prevent over-assignment
				_healthTracker.IncrementProviderLoad(provider.Id);
			}
		}

		return assignments;
	}

	/// <summary>
	/// Rebalances jobs when provider health changes
	/// </summary>
	public async Task RebalanceJobsAsync(CancellationToken cancellationToken = default)
	{
		_logger.LogDebug("Checking for job rebalancing opportunities");

		using var scope = _scopeFactory.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();

		// Find jobs assigned to unhealthy providers that haven't started yet
		var jobsToReassign = await dbContext.Jobs
			.Include(j => j.Provider)
			.Where(j => j.Status == JobStatus.New || j.Status == JobStatus.Pending)
			.Where(j => j.Provider != null)
			.ToListAsync(cancellationToken);

		foreach (var job in jobsToReassign)
		{
			var health = _healthTracker.GetProviderHealth(job.ProviderId);
			if (!health.IsHealthy)
			{
				_logger.LogInformation("Rebalancing job {JobId} from unhealthy provider {ProviderId}",
					job.Id, job.ProviderId);

				var newProvider = await SelectProviderForJobAsync(job, cancellationToken);
				if (newProvider != null && newProvider.Id != job.ProviderId)
				{
					await AssignJobToProviderAsync(job.Id, newProvider.Id, cancellationToken);
				}
			}
		}
	}

	private async Task<DateTime?> GetProviderCooldownUntilAsync(
		Guid providerId,
		ProviderHealth health,
		VibeSwarmDbContext dbContext,
		CancellationToken cancellationToken)
	{
		var now = DateTime.UtcNow;
		var persistedCooldownUntil = await dbContext.ProviderUsageSummaries
			.AsNoTracking()
			.Where(summary => summary.ProviderId == providerId)
			.Select(summary => summary.NextExecutionAvailableAt)
			.FirstOrDefaultAsync(cancellationToken);

		if (persistedCooldownUntil.HasValue && persistedCooldownUntil.Value <= now)
		{
			persistedCooldownUntil = null;
		}

		var trackedCooldownUntil = health.IsRateLimited && health.RateLimitResetTime.HasValue && health.RateLimitResetTime.Value > now
			? health.RateLimitResetTime.Value
			: (DateTime?)null;

		return GetLaterCooldown(persistedCooldownUntil, trackedCooldownUntil);
	}

	/// <summary>
	/// Calculates a score for provider selection (higher = better)
	/// </summary>
	private double CalculateProviderScore(Provider provider, ProviderHealth health)
	{
		double score = 100.0;

		// Penalize based on current load (0-40 points penalty)
		score -= (health.CurrentLoad / (double)MaxJobsPerProvider) * 40;

		// Penalize based on recent failure rate (0-30 points penalty)
		score -= health.RecentFailureRate * 30;

		// Penalize based on average response time (0-20 points penalty)
		var avgResponseMs = health.AverageResponseTime.TotalMilliseconds;
		var responsePenalty = Math.Min(avgResponseMs / 10000.0, 1.0) * 20;
		score -= responsePenalty;

		// Bonus for time since last failure (0-10 points bonus)
		var lastFailure = health.LastFailure ?? DateTime.UtcNow.AddDays(-1);
		var hoursSinceLastFailure = (DateTime.UtcNow - lastFailure).TotalHours;
		score += Math.Min(hoursSinceLastFailure, 24) / 24 * 10;

		return Math.Max(0, score);
	}
}

/// <summary>
/// Represents a job assignment to a provider
/// </summary>
public class JobAssignment
{
	public Job Job { get; set; } = null!;
	public Provider Provider { get; set; } = null!;
	public DateTime AssignedAt { get; set; }
}

/// <summary>
/// Interface for the job coordinator service
/// </summary>
public interface IJobCoordinatorService
{
	int MaxJobsPerProvider { get; set; }
	Task<Provider?> SelectProviderForJobAsync(Job job, CancellationToken cancellationToken = default);
	Task<bool> AssignJobToProviderAsync(Guid jobId, Guid providerId, CancellationToken cancellationToken = default);
	Task ReleaseJobFromProviderAsync(Guid jobId, Guid providerId, bool success, CancellationToken cancellationToken = default);
	Task<IReadOnlyList<JobAssignment>> GetNextJobAssignmentsAsync(int maxJobs, CancellationToken cancellationToken = default);
	Task RebalanceJobsAsync(CancellationToken cancellationToken = default);
}
