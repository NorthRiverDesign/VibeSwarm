namespace VibeSwarm.Shared.Models;

public static class DatabaseMaintenanceDefaults
{
	public const int HistoryRetentionDays = 30;
}

public class DatabaseStorageSummary
{
	public string Provider { get; set; } = "unknown";
	public string? Location { get; set; }
	public long? TotalSizeBytes { get; set; }
	public long? DataFileSizeBytes { get; set; }
	public long? WalFileSizeBytes { get; set; }
	public long? SharedMemoryFileSizeBytes { get; set; }
	public int JobsCount { get; set; }
	public int JobMessagesCount { get; set; }
	public int ProviderUsageRecordsCount { get; set; }
	public int CriticalErrorLogsCount { get; set; }
	public int CompletedJobsOlderThanRetentionCount { get; set; }
	public int ProviderUsageRecordsOlderThanRetentionCount { get; set; }
	public bool SupportsCompaction { get; set; }
	public DateTime MeasuredAtUtc { get; set; } = DateTime.UtcNow;
}

public enum DatabaseMaintenanceOperation
{
	ApplyCriticalErrorLogRetention,
	DeleteCompletedJobsOlderThanRetention,
	DeleteProviderUsageOlderThanRetention,
	CompactDatabase
}

public class DatabaseMaintenanceRequest
{
	public DatabaseMaintenanceOperation Operation { get; set; }
}

public class DatabaseMaintenanceResult
{
	public DatabaseMaintenanceOperation Operation { get; set; }
	public int AffectedRows { get; set; }
	public long? SizeBeforeBytes { get; set; }
	public long? SizeAfterBytes { get; set; }
	public string Message { get; set; } = string.Empty;
}
