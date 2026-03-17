using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Models;

/// <summary>
/// Stages at which a codebase suggestion attempt can stop.
/// Sent back to the client so the UI can give precise feedback.
/// </summary>
public static class SuggestIdeasStage
{
	/// <summary>No local inference service is registered in DI.</summary>
	public const string NotConfigured = "not_configured";

	/// <summary>The inference provider endpoint could not be reached.</summary>
	public const string ProviderUnreachable = "provider_unreachable";

	/// <summary>The selected inference provider no longer exists or is disabled.</summary>
	public const string ProviderNotFound = "provider_not_found";

	/// <summary>The selected model is unavailable for the chosen inference provider.</summary>
	public const string ModelNotFound = "model_not_found";

	/// <summary>The inference provider is reachable but has no model assigned to the "suggest" or "default" task.</summary>
	public const string NoModel = "no_model";

	/// <summary>The project working directory could not be scanned to build a repo map.</summary>
	public const string RepoMapFailed = "repo_map_failed";

	/// <summary>The inference request was sent but the model returned an error or empty response.</summary>
	public const string GenerateFailed = "generate_failed";

	/// <summary>The model responded but no parseable idea lines were found in the output.</summary>
	public const string ParseFailed = "parse_failed";

	/// <summary>At least one idea was created successfully.</summary>
	public const string Success = "success";
}

/// <summary>
/// Result of a codebase suggestion attempt, including diagnostic information
/// for every possible failure point so the UI can surface precise feedback.
/// </summary>
public class SuggestIdeasResult
{
	public bool Success { get; set; }

	/// <summary>
	/// Ideas that were created. Empty on failure.
	/// </summary>
	public List<Idea> Ideas { get; set; } = [];

	/// <summary>
	/// Which stage the operation reached. See <see cref="SuggestIdeasStage"/>.
	/// </summary>
	public string Stage { get; set; } = SuggestIdeasStage.NotConfigured;

	/// <summary>
	/// Human-readable explanation of the outcome.
	/// </summary>
	public string Message { get; set; } = string.Empty;

	/// <summary>
	/// The inference model that was used, if the request reached the provider.
	/// </summary>
	public string? ModelUsed { get; set; }

	/// <summary>
	/// Total time spent on the inference call in milliseconds, if available.
	/// </summary>
	public long? InferenceDurationMs { get; set; }

	/// <summary>
	/// Raw inference error detail, if the provider returned one.
	/// </summary>
	public string? InferenceError { get; set; }
}
