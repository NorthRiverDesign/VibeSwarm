using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// Runs an inference model as project manager for a folder of projects: it reads each project's
/// queued ideas, jobs and recent commits and writes a report on where things stand. Reports are
/// saved, and a run can continue from an earlier one.
/// </summary>
public interface IFolderManagerService
{
	/// <summary>The folder's saved reports, newest first.</summary>
	Task<IReadOnlyList<FolderManagerReport>> GetReportsAsync(Guid folderId, CancellationToken cancellationToken = default);

	Task<FolderManagerRunResult> RunAsync(Guid folderId, FolderManagerRunRequest request, CancellationToken cancellationToken = default);

	Task DeleteReportAsync(Guid reportId, CancellationToken cancellationToken = default);
}
