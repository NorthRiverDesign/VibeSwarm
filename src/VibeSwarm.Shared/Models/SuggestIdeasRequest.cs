using System.ComponentModel.DataAnnotations;

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
	/// Optional inference provider to use for suggestion generation.
	/// When omitted, the default enabled provider/model resolution is used.
	/// </summary>
	public Guid? ProviderId { get; set; }

	/// <summary>
	/// How many ideas to ask the model to generate.
	/// </summary>
	[Range(MinIdeaCount, MaxIdeaCount)]
	public int IdeaCount { get; set; } = DefaultIdeaCount;
}
