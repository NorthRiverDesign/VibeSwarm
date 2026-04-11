using VibeSwarm.Shared.Models;

namespace VibeSwarm.Shared.Services;

public interface IDatabaseService
{
	Task<DatabaseExportDto> ExportAsync(CancellationToken ct = default);
	Task<DatabaseImportResult> ImportAsync(DatabaseExportDto export, CancellationToken ct = default);
	Task<DatabaseConfigurationInfo> GetConfigurationAsync(CancellationToken ct = default);
	Task<DatabaseStorageSummary> GetStorageSummaryAsync(CancellationToken ct = default);
	Task<DatabaseMigrationResult> MigrateAsync(DatabaseMigrationRequest request, CancellationToken ct = default);
	Task<DatabaseMaintenanceResult> RunMaintenanceAsync(DatabaseMaintenanceRequest request, CancellationToken ct = default);
}
