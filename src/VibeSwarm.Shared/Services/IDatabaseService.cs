using VibeSwarm.Shared.Models;

namespace VibeSwarm.Shared.Services;

public interface IDatabaseService
{
	Task<DatabaseExportDto> ExportAsync(CancellationToken ct = default);
	Task<DatabaseImportResult> ImportAsync(DatabaseExportDto export, CancellationToken ct = default);
}
