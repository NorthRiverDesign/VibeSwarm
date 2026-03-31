using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Web.Services;

public class CriticalErrorLogService : ICriticalErrorLogService
{
	private readonly VibeSwarmDbContext _dbContext;
	private readonly ILogger<CriticalErrorLogService> _logger;

	public CriticalErrorLogService(VibeSwarmDbContext dbContext, ILogger<CriticalErrorLogService> logger)
	{
		_dbContext = dbContext;
		_logger = logger;
	}

	public async Task<CriticalErrorLogEntry> LogAsync(CriticalErrorLogEntry entry, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(entry);

		var normalizedEntry = Normalize(entry);
		_dbContext.CriticalErrorLogs.Add(normalizedEntry);
		await _dbContext.SaveChangesAsync(cancellationToken);
		await ApplyRetentionPolicyAsync(cancellationToken);

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
		return await _dbContext.CriticalErrorLogs
			.AsNoTracking()
			.OrderByDescending(entry => entry.CreatedAt)
			.Take(normalizedLimit)
			.ToListAsync(cancellationToken);
	}

	public async Task ApplyRetentionPolicyAsync(CancellationToken cancellationToken = default)
	{
		var settings = await _dbContext.AppSettings
			.AsNoTracking()
			.OrderBy(setting => setting.Id)
			.FirstOrDefaultAsync(cancellationToken);

		var retentionDays = NormalizeRetentionDays(settings?.CriticalErrorLogRetentionDays ?? AppSettings.DefaultCriticalErrorLogRetentionDays);
		var maxEntries = NormalizeMaxEntries(settings?.CriticalErrorLogMaxEntries ?? AppSettings.DefaultCriticalErrorLogMaxEntries);
		var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

		var expiredEntries = await _dbContext.CriticalErrorLogs
			.Where(entry => entry.CreatedAt < cutoff)
			.ToListAsync(cancellationToken);

		if (expiredEntries.Count > 0)
		{
			_dbContext.CriticalErrorLogs.RemoveRange(expiredEntries);
			await _dbContext.SaveChangesAsync(cancellationToken);
		}

		var totalCount = await _dbContext.CriticalErrorLogs.CountAsync(cancellationToken);
		var overflow = totalCount - maxEntries;

		if (overflow > 0)
		{
			var overflowEntries = await _dbContext.CriticalErrorLogs
				.OrderBy(entry => entry.CreatedAt)
				.Take(overflow)
				.ToListAsync(cancellationToken);

			if (overflowEntries.Count > 0)
			{
				_dbContext.CriticalErrorLogs.RemoveRange(overflowEntries);
			}
		}

		if (_dbContext.ChangeTracker.HasChanges())
		{
			await _dbContext.SaveChangesAsync(cancellationToken);
		}
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
