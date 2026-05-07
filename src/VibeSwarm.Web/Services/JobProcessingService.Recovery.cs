using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Services;

public partial class JobProcessingService
{
	private static readonly TimeSpan[] DurableRateLimitBackoffSteps =
	[
		TimeSpan.FromMinutes(1),
		TimeSpan.FromMinutes(2),
		TimeSpan.FromMinutes(3)
	];

	private static readonly TimeSpan ProviderStartSpacing = TimeSpan.FromSeconds(10);
	private static readonly TimeSpan RecoveryCheckpointInterval = TimeSpan.FromSeconds(5);

	private async Task<ProviderUsageSummary> GetOrCreateProviderUsageSummaryAsync(
		Guid providerId,
		VibeSwarmDbContext dbContext,
		CancellationToken cancellationToken)
	{
		var summary = await dbContext.ProviderUsageSummaries
			.FirstOrDefaultAsync(s => s.ProviderId == providerId, cancellationToken);

		if (summary != null)
		{
			return summary;
		}

		summary = new ProviderUsageSummary
		{
			ProviderId = providerId,
			LastUpdatedAt = DateTime.UtcNow
		};

		dbContext.ProviderUsageSummaries.Add(summary);
		await dbContext.SaveChangesAsync(cancellationToken);
		return summary;
	}

	private async Task<DateTime?> GetProviderCooldownUntilAsync(
		Guid providerId,
		VibeSwarmDbContext dbContext,
		CancellationToken cancellationToken)
	{
		var now = DateTime.UtcNow;
		var summary = await dbContext.ProviderUsageSummaries
			.AsNoTracking()
			.FirstOrDefaultAsync(s => s.ProviderId == providerId, cancellationToken);

		var persistedCooldownUntil = summary?.NextExecutionAvailableAt is DateTime nextExecutionAvailableAt && nextExecutionAvailableAt > now
			? nextExecutionAvailableAt
			: (DateTime?)null;

		var trackedCooldownUntil = _healthTracker?.GetProviderHealth(providerId) is { IsRateLimited: true, RateLimitResetTime: DateTime rateLimitResetTime }
			&& rateLimitResetTime > now
				? rateLimitResetTime
				: (DateTime?)null;

		return GetLaterCooldown(persistedCooldownUntil, trackedCooldownUntil);
	}

	private async Task<(Provider? Provider, DateTime? CooldownUntil)> ResolveProviderForExecutionAsync(
		Job job,
		VibeSwarmDbContext dbContext,
		CancellationToken cancellationToken)
	{
		if (job.Provider == null)
		{
			return (null, null);
		}

		var cooldownUntil = await GetProviderCooldownUntilAsync(job.ProviderId, dbContext, cancellationToken);
		if (!cooldownUntil.HasValue)
		{
			return (job.Provider, null);
		}

		if (_jobCoordinator != null)
		{
			var originalProviderId = job.ProviderId;
			var fallbackProvider = await _jobCoordinator.SelectProviderForJobAsync(job, cancellationToken);
			if (fallbackProvider != null && fallbackProvider.Id != originalProviderId)
			{
				var trackedProvider = await dbContext.Providers
					.FirstOrDefaultAsync(provider => provider.Id == fallbackProvider.Id, cancellationToken);
				if (trackedProvider != null)
				{
					job.ProviderId = trackedProvider.Id;
					job.Provider = trackedProvider;
					await dbContext.SaveChangesAsync(cancellationToken);

					_logger.LogInformation(
						"Switched job {JobId} from cooling-down provider {OriginalProviderId} to provider {ProviderId}",
						job.Id,
						originalProviderId,
						trackedProvider.Id);

					return (trackedProvider, null);
				}
			}
		}

		return (job.Provider, cooldownUntil);
	}

	private async Task ReserveProviderExecutionSlotAsync(
		Guid providerId,
		VibeSwarmDbContext dbContext,
		CancellationToken cancellationToken)
	{
		var summary = await GetOrCreateProviderUsageSummaryAsync(providerId, dbContext, cancellationToken);

		while (!cancellationToken.IsCancellationRequested)
		{
			var now = DateTime.UtcNow;
			var nextAllowedStart = summary.LastJobStartedAt.HasValue
				? summary.LastJobStartedAt.Value + ProviderStartSpacing
				: (DateTime?)null;

			if (!nextAllowedStart.HasValue || nextAllowedStart.Value <= now)
			{
				summary.LastJobStartedAt = now;
				summary.LastUpdatedAt = now;
				await dbContext.SaveChangesAsync(cancellationToken);
				return;
			}

			var delay = nextAllowedStart.Value - now;
			_logger.LogInformation(
				"Delaying provider {ProviderId} job start by {DelaySeconds:F1} seconds to preserve provider spacing",
				providerId,
				delay.TotalSeconds);

			await Task.Delay(delay, cancellationToken);
			await dbContext.Entry(summary).ReloadAsync(cancellationToken);
		}
	}

	private async Task<DateTime> RecordProviderRateLimitAsync(
		Guid providerId,
		DateTime? providerResetTime,
		string? message,
		VibeSwarmDbContext dbContext,
		CancellationToken cancellationToken,
		DateTime? cooldownUntil = null)
	{
		var summary = await GetOrCreateProviderUsageSummaryAsync(providerId, dbContext, cancellationToken);
		var now = DateTime.UtcNow;
		var previousRateLimitWasRecent = summary.LastRateLimitAt.HasValue
			&& summary.LastRateLimitAt.Value >= now.AddHours(-1);

		var consecutiveRateLimitCount = previousRateLimitWasRecent
			? Math.Min(summary.ConsecutiveRateLimitCount + 1, DurableRateLimitBackoffSteps.Length)
			: 1;

		var backoff = DurableRateLimitBackoffSteps[consecutiveRateLimitCount - 1];
		var backoffUntil = cooldownUntil ?? now + backoff;
		if (!cooldownUntil.HasValue && providerResetTime.HasValue && providerResetTime.Value > backoffUntil)
		{
			backoffUntil = providerResetTime.Value;
		}

		summary.ConsecutiveRateLimitCount = consecutiveRateLimitCount;
		summary.LastRateLimitAt = now;
		summary.LastRateLimitMessage = JobRecoveryHelper.TrimTail(message, 500);
		summary.NextExecutionAvailableAt = backoffUntil;
		summary.LastUpdatedAt = now;

		await dbContext.SaveChangesAsync(cancellationToken);
		return backoffUntil;
	}

	private async Task ClearProviderRateLimitAsync(
		Guid providerId,
		VibeSwarmDbContext dbContext,
		CancellationToken cancellationToken)
	{
		var summary = await dbContext.ProviderUsageSummaries
			.FirstOrDefaultAsync(s => s.ProviderId == providerId, cancellationToken);
		if (summary == null)
		{
			return;
		}

		summary.ConsecutiveRateLimitCount = 0;
		summary.LastRateLimitAt = null;
		summary.LastRateLimitMessage = null;
		summary.NextExecutionAvailableAt = null;
		summary.LastUpdatedAt = DateTime.UtcNow;
		await dbContext.SaveChangesAsync(cancellationToken);
	}

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

	private async Task PersistExecutionCheckpointAsync(
		Guid jobId,
		JobStatus resumeFromStatus,
		JobExecutionContext executionContext,
		CancellationToken cancellationToken)
	{
		using var scope = _scopeFactory.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
		var job = await dbContext.Jobs.FindAsync(new object[] { jobId }, cancellationToken);
		if (job == null)
		{
			return;
		}

		var now = DateTime.UtcNow;
		job.LastHeartbeatAt = now;
		job.LastActivityAt = now;
		job.CurrentActivity = executionContext.LatestActivity ?? job.CurrentActivity;
		job.ProcessId = executionContext.ProcessId;
		job.CommandUsed = executionContext.CommandUsed ?? job.CommandUsed;

		if (resumeFromStatus == JobStatus.Planning)
		{
			job.PlanningCommandUsed = executionContext.CommandUsed ?? job.PlanningCommandUsed;
		}
		else
		{
			job.ExecutionCommandUsed = executionContext.CommandUsed ?? job.ExecutionCommandUsed;
		}

		JobRecoveryHelper.CaptureRecoveryState(
			job,
			resumeFromStatus,
			executionContext.ActivePrompt,
			executionContext.SessionId,
			executionContext.GetConsoleOutput());

		await dbContext.SaveChangesAsync(cancellationToken);
		executionContext.LastCheckpointPersistedAt = now;
	}

	private static string BuildRecoveryPrompt(Job job, JobExecutionContext executionContext, string currentPrompt)
	{
		var recentConsoleOutput = executionContext.GetConsoleOutput();
		if (string.IsNullOrWhiteSpace(recentConsoleOutput))
		{
			recentConsoleOutput = job.ConsoleOutput;
		}

		recentConsoleOutput = JobRecoveryHelper.TrimTail(recentConsoleOutput, JobRecoveryHelper.MaxRecoveryConsoleOutputLength);

		return PromptBuilder.BuildRecoveryPrompt(
			currentPrompt,
			job.RecoveryPrompt,
			recentConsoleOutput,
			job.LastResumeFailureReason,
			job.ForceFreshSession);
	}

	private static bool IsSessionResumeFailure(ExecutionResult result)
	{
		if (result.Success)
		{
			return false;
		}

		var errorText = string.Join(
			"\n",
			new[]
			{
				result.ErrorMessage,
				result.Output,
				string.Join("\n", result.Messages.Select(message => message.Content))
			}.Where(value => !string.IsNullOrWhiteSpace(value)));

		if (string.IsNullOrWhiteSpace(errorText))
		{
			return false;
		}

		return errorText.Contains("session not found", StringComparison.OrdinalIgnoreCase)
			|| errorText.Contains("invalid session", StringComparison.OrdinalIgnoreCase)
			|| errorText.Contains("unknown session", StringComparison.OrdinalIgnoreCase)
			|| errorText.Contains("session expired", StringComparison.OrdinalIgnoreCase)
			|| errorText.Contains("could not resume", StringComparison.OrdinalIgnoreCase)
			|| errorText.Contains("failed to resume", StringComparison.OrdinalIgnoreCase)
			|| errorText.Contains("resume session", StringComparison.OrdinalIgnoreCase)
			|| errorText.Contains("conversation not found", StringComparison.OrdinalIgnoreCase)
			|| errorText.Contains("no session", StringComparison.OrdinalIgnoreCase);
	}
}