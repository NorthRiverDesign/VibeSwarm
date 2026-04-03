using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Web.Services;

public class CriticalErrorLogService : ICriticalErrorLogService
{
	private static readonly TimeSpan LogOperationTimeout = TimeSpan.FromSeconds(5);
	private static readonly TimeSpan RetentionInterval = TimeSpan.FromMinutes(1);
	private static long _lastRetentionRunTicks;
	private readonly VibeSwarmDbContext _dbContext;
	private readonly ILogger<CriticalErrorLogService> _logger;
	private readonly IServiceScopeFactory? _scopeFactory;

	public CriticalErrorLogService(
		VibeSwarmDbContext dbContext,
		ILogger<CriticalErrorLogService> logger,
		IServiceScopeFactory? scopeFactory = null)
	{
		_dbContext = dbContext;
		_logger = logger;
		_scopeFactory = scopeFactory;
	}

	public async Task<CriticalErrorLogEntry> LogAsync(CriticalErrorLogEntry entry, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(entry);

		var normalizedEntry = Normalize(entry);
		using var scope = CreateScope();
		var dbContext = GetDbContext(scope);
		dbContext.CriticalErrorLogs.Add(normalizedEntry);

		using var timeoutCts = CreateTimeoutCancellationTokenSource(cancellationToken);
		await dbContext.SaveChangesAsync(timeoutCts.Token);
		await TryApplyRetentionPolicyAsync(cancellationToken);

		_logger.LogDebug(
			"Stored critical error log {Category} from {Source} at {CreatedAt}",
			normalizedEntry.Category,
			normalizedEntry.Source,
			normalizedEntry.CreatedAt);

		return normalizedEntry;
	}

	public async Task<IReadOnlyList<CriticalErrorLogEntry>> GetRecentAsync(int limit = 25, CancellationToken cancellationToken = default)
	{
		var normalizedLimit = Math.Clamp(limit, 1, 200);
		using var scope = CreateScope();
		var dbContext = GetDbContext(scope);

		return await dbContext.CriticalErrorLogs
			.AsNoTracking()
			.OrderByDescending(entry => entry.CreatedAt)
			.Take(normalizedLimit)
			.ToListAsync(cancellationToken);
	}

	public async Task ApplyRetentionPolicyAsync(CancellationToken cancellationToken = default)
	{
		using var scope = CreateScope();
		var dbContext = GetDbContext(scope);
		await ApplyRetentionPolicyAsync(dbContext, cancellationToken);
	}

	private async Task ApplyRetentionPolicyAsync(VibeSwarmDbContext dbContext, CancellationToken cancellationToken)
	{
		using var timeoutCts = CreateTimeoutCancellationTokenSource(cancellationToken);
		var effectiveCancellationToken = timeoutCts.Token;

		var settings = await dbContext.AppSettings
			.AsNoTracking()
			.OrderBy(setting => setting.Id)
			.FirstOrDefaultAsync(effectiveCancellationToken);

		var retentionDays = NormalizeRetentionDays(settings?.CriticalErrorLogRetentionDays ?? AppSettings.DefaultCriticalErrorLogRetentionDays);
		var maxEntries = NormalizeMaxEntries(settings?.CriticalErrorLogMaxEntries ?? AppSettings.DefaultCriticalErrorLogMaxEntries);
		var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

		var expiredEntries = await dbContext.CriticalErrorLogs
			.Where(entry => entry.CreatedAt < cutoff)
			.ToListAsync(effectiveCancellationToken);

		if (expiredEntries.Count > 0)
		{
			dbContext.CriticalErrorLogs.RemoveRange(expiredEntries);
			await dbContext.SaveChangesAsync(effectiveCancellationToken);
		}

		var totalCount = await dbContext.CriticalErrorLogs.CountAsync(effectiveCancellationToken);
		var overflow = totalCount - maxEntries;

		if (overflow > 0)
		{
			var overflowEntries = await dbContext.CriticalErrorLogs
				.OrderBy(entry => entry.CreatedAt)
				.Take(overflow)
				.ToListAsync(effectiveCancellationToken);

			if (overflowEntries.Count > 0)
			{
				dbContext.CriticalErrorLogs.RemoveRange(overflowEntries);
			}
		}

		if (dbContext.ChangeTracker.HasChanges())
		{
			await dbContext.SaveChangesAsync(effectiveCancellationToken);
		}
	}

	private async Task TryApplyRetentionPolicyAsync(CancellationToken cancellationToken)
	{
		if (!ShouldRunRetention())
		{
			return;
		}

		try
		{
			await ApplyRetentionPolicyAsync(cancellationToken);
		}
		catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
		{
			_logger.LogWarning(ex, "Timed out while pruning critical error logs after writing a new entry.");
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to prune critical error logs after writing a new entry.");
		}
	}

	private IServiceScope? CreateScope()
	{
		return _scopeFactory?.CreateScope();
	}

	private VibeSwarmDbContext GetDbContext(IServiceScope? scope)
	{
		return scope?.ServiceProvider.GetRequiredService<VibeSwarmDbContext>() ?? _dbContext;
	}

	private static CancellationTokenSource CreateTimeoutCancellationTokenSource(CancellationToken cancellationToken)
	{
		var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutCts.CancelAfter(LogOperationTimeout);
		return timeoutCts;
	}

	private static bool ShouldRunRetention()
	{
		var nowTicks = DateTime.UtcNow.Ticks;
		var lastRunTicks = Interlocked.Read(ref _lastRetentionRunTicks);
		if (lastRunTicks != 0 && nowTicks - lastRunTicks < RetentionInterval.Ticks)
		{
			return false;
		}

		Interlocked.Exchange(ref _lastRetentionRunTicks, nowTicks);
		return true;
	}

	private static CriticalErrorLogEntry Normalize(CriticalErrorLogEntry entry)
	{
		var message = NormalizeRequired(entry.Message, "Critical error recorded.", ValidationLimits.CriticalErrorLogMessageMaxLength);
		var category = NormalizeRequired(entry.Category, "unhandled-exception", ValidationLimits.CriticalErrorLogFieldMaxLength);

		return new CriticalErrorLogEntry
		{
			Id = entry.Id == Guid.Empty ? Guid.NewGuid() : entry.Id,
			Source = NormalizeRequired(entry.Source, "server", ValidationLimits.CriticalErrorLogFieldMaxLength),
			Category = category,
			Severity = NormalizeRequired(entry.Severity, "critical", ValidationLimits.CriticalErrorLogFieldMaxLength),
			Message = message,
			Details = NormalizeOptional(entry.Details, ValidationLimits.CriticalErrorLogDetailsMaxLength),
			TraceId = NormalizeOptional(entry.TraceId, ValidationLimits.CriticalErrorLogTraceIdMaxLength),
			Url = NormalizeOptional(entry.Url, ValidationLimits.CriticalErrorLogUrlMaxLength),
			UserAgent = NormalizeOptional(entry.UserAgent, ValidationLimits.CriticalErrorLogUserAgentMaxLength),
			RefreshAction = NormalizeOptional(entry.RefreshAction, ValidationLimits.CriticalErrorLogFieldMaxLength),
			TriggeredRefresh = entry.TriggeredRefresh,
			AdditionalDataJson = NormalizeOptional(entry.AdditionalDataJson, ValidationLimits.CriticalErrorLogMetadataMaxLength),
			UserId = entry.UserId,
			CreatedAt = entry.CreatedAt == default ? DateTime.UtcNow : entry.CreatedAt
		};
	}

	private static string NormalizeRequired(string? value, string fallback, int maxLength)
	{
		var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
		return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
	}

	private static string? NormalizeOptional(string? value, int maxLength)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}

		var normalized = value.Trim();
		return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
	}

	private static int NormalizeRetentionDays(int retentionDays)
	{
		return Math.Clamp(
			retentionDays,
			AppSettings.MinCriticalErrorLogRetentionDays,
			AppSettings.MaxCriticalErrorLogRetentionDays);
	}

	private static int NormalizeMaxEntries(int maxEntries)
	{
		return Math.Clamp(
			maxEntries,
			AppSettings.MinCriticalErrorLogMaxEntries,
			AppSettings.MaxCriticalErrorLogMaxEntries);
	}
}
