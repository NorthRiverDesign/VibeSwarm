using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// Configuration for starting or updating an auto-pilot loop.
/// </summary>
public class AutoPilotConfig
{
	/// <summary>
	/// Provider to use. Null = use the project's default provider selection.
	/// </summary>
	public Guid? ProviderId { get; set; }

	/// <summary>
	/// Optional model override.
	/// </summary>
	public string? ModelId { get; set; }

	/// <summary>
	/// Maximum iterations before the loop stops. 0 = unlimited.
	/// </summary>
	public int MaxIterations { get; set; } = 50;

	/// <summary>
	/// Maximum total cost in USD. Null = no cost limit.
	/// </summary>
	public decimal? MaxTotalCostUsd { get; set; }

	/// <summary>
	/// Number of consecutive failures before the loop stops.
	/// </summary>
	public int MaxConsecutiveFailures { get; set; } = 3;

	/// <summary>
	/// Seconds to wait between iterations.
	/// </summary>
	public int CooldownSeconds { get; set; } = 60;

	/// <summary>
	/// Whether to auto-commit changes after each successful job.
	/// </summary>
	public bool AutoCommit { get; set; } = true;

	/// <summary>
	/// Whether to auto-push after committing.
	/// </summary>
	public bool AutoPush { get; set; }
}

/// <summary>
/// Service for managing auto-pilot iteration loops on projects.
/// </summary>
public interface IAutoPilotService
{
	/// <summary>
	/// Starts an auto-pilot loop for a project.
	/// </summary>
	Task<IterationLoop> StartAsync(Guid projectId, AutoPilotConfig config, CancellationToken cancellationToken = default);

	/// <summary>
	/// Requests a graceful stop for the project's active loop.
	/// The current job finishes before the loop transitions to Stopped.
	/// </summary>
	Task StopAsync(Guid projectId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Pauses the project's active loop. Can be resumed later.
	/// </summary>
	Task PauseAsync(Guid projectId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Resumes a paused loop.
	/// </summary>
	Task ResumeAsync(Guid projectId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets the active (non-terminal) loop for a project, or null.
	/// </summary>
	Task<IterationLoop?> GetStatusAsync(Guid projectId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets all loops for a project (current and past), newest first.
	/// </summary>
	Task<List<IterationLoop>> GetHistoryAsync(Guid projectId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Updates configuration on a paused loop.
	/// </summary>
	Task<IterationLoop> UpdateConfigAsync(Guid projectId, AutoPilotConfig config, CancellationToken cancellationToken = default);
}
