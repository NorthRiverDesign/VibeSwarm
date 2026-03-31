using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

public interface ICriticalErrorLogService
{
	Task<CriticalErrorLogEntry> LogAsync(CriticalErrorLogEntry entry, CancellationToken cancellationToken = default);
	Task<IReadOnlyList<CriticalErrorLogEntry>> GetRecentAsync(int limit = 25, CancellationToken cancellationToken = default);
	Task ApplyRetentionPolicyAsync(CancellationToken cancellationToken = default);
}
