using VibeSwarm.Shared.Inference;

namespace VibeSwarm.Shared.Models;

/// <summary>
/// A follow-up message inference wrote for a job whose run has ended, which the user confirms
/// before it is sent, with what the request used. <see cref="Error"/> says why there is no draft.
/// </summary>
public sealed class FollowUpDraft
{
	public string? Text { get; set; }

	public string? ProviderName { get; set; }

	public InferenceProviderType? ProviderType { get; set; }

	public string? ModelId { get; set; }

	public int? PromptTokens { get; set; }

	public int? CompletionTokens { get; set; }

	public long? DurationMs { get; set; }

	/// <summary>What the provider billed, when it reports a cost.</summary>
	public decimal? CostUsd { get; set; }

	public string? Error { get; set; }
}
