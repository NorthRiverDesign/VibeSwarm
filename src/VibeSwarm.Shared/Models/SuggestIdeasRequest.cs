using System.ComponentModel.DataAnnotations;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Shared.Models;

/// <summary>
/// Options for generating idea suggestions from a project's codebase.
/// </summary>
public class SuggestIdeasRequest
{
	public const int MinIdeaCount = 1;
	public const int DefaultIdeaCount = 3;
	public const int MaxIdeaCount = 7;

	/// <summary>
	/// When true, uses a inference provider (for example, Ollama) for suggestion generation.
	/// When false, uses a configured coding provider (for example, Claude, Copilot, or OpenCode).
	/// </summary>
	public bool UseInference { get; set; } = true;

	/// <summary>
	/// Optional provider to use for suggestion generation.
	/// For inference, this is an inference provider ID.
	/// For configured providers, this is a coding provider ID.
	/// When omitted, the server resolves the default source/provider for the selected mode.
	/// </summary>
	public Guid? ProviderId { get; set; }

	/// <summary>
	/// Optional explicit model override for the selected provider.
	/// Leave empty to use the provider's configured default model or built-in fallback behavior.
	/// </summary>
	[StringLength(ValidationLimits.ProjectPlanningModelIdMaxLength)]
	public string? ModelId { get; set; }

	/// <summary>
	 /// How many ideas to ask the model to generate.
	 /// </summary>
	[Range(MinIdeaCount, MaxIdeaCount)]
	public int IdeaCount { get; set; } = DefaultIdeaCount;

	/// <summary>
	/// Optional extra scheduler context so idea generation can avoid already-queued plans and recent repeats.
	/// </summary>
	[StringLength(ValidationLimits.ProjectPromptContextMaxLength)]
	public string? AdditionalContext { get; set; }
}
