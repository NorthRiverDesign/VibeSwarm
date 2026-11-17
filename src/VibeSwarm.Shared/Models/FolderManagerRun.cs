using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Models;

/// <summary>
/// What the user asks of a folder's manager run: optional notes on what to look at, and the
/// earlier report to build on (none starts fresh).
/// </summary>
public sealed class FolderManagerRunRequest
{
	public string? Notes { get; set; }

	public Guid? ContinueFromReportId { get; set; }
}

/// <summary>
/// The saved report a manager run wrote, or <see cref="Error"/> saying why there is none.
/// </summary>
public sealed class FolderManagerRunResult
{
	public FolderManagerReport? Report { get; set; }

	public string? Error { get; set; }
}
