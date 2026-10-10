using System.ComponentModel.DataAnnotations;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// Configuration for starting or updating an auto-pilot loop.
/// </summary>
public class AutoPilotConfig
{
	/// <summary>
	/// Inference provider for idea generation (e.g., Grok, Ollama).
	/// Null = fall back to coding provider or project default.
	/// </summary>
	public Guid? InferenceProviderId { get; set; }

	public string? InferenceModelId { get; set; }

	/// <summary>
	/// CLI coding provider for job execution (e.g., Claude, Copilot).
	/// Null = use the project's default provider selection.
	/// </summary>
	public Guid? ProviderId { get; set; }

	/// <summary>
	/// Optional model override for the coding provider.
	/// </summary>
	public string? ModelId { get; set; }

	public const int MaxIterationsLimit = 1000;
	public const int MaxConsecutiveFailuresLimit = 20;
	public const int MinCooldownSeconds = 10;
	public const int MaxCooldownSeconds = 3600;
	public const int MaxPolishEveryIterations = 100;

	/// <summary>
	/// Maximum iterations before the loop stops. 0 = unlimited.
	/// </summary>
	[Range(0, MaxIterationsLimit)]
	public int MaxIterations { get; set; } = 50;

	/// <summary>
	/// Maximum total cost in USD. Null = no cost limit.
	/// </summary>
	[Range(0, 1_000_000)]
	public decimal? MaxTotalCostUsd { get; set; }

	/// <summary>
	/// Number of consecutive failures before the loop stops.
	/// </summary>
	[Range(1, MaxConsecutiveFailuresLimit)]
	public int MaxConsecutiveFailures { get; set; } = 3;

	[Range(MinCooldownSeconds, MaxCooldownSeconds)]
	public int CooldownSeconds { get; set; } = 60;

	/// <summary>
	/// After this many successful changes, run a polish pass over them. 0 = never.
	/// </summary>
	[Range(0, MaxPolishEveryIterations)]
	public int PolishEveryIterations { get; set; } = 5;

	/// <summary>
	/// Whether to push each change after it is committed.
	/// </summary>
	public bool AutoPush { get; set; }
}

public interface IAutoPilotService
{
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
