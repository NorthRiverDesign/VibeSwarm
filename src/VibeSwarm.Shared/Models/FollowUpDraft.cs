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

/// <summary>
/// What the user asked inference to build a follow-up from: what they typed in the follow-up box,
/// and the <see cref="FollowUpDraftActions"/> they picked for the agent.
/// </summary>
public sealed class FollowUpDraftRequest
{
	public string? Notes { get; set; }

	/// <summary>Keys of the picked <see cref="FollowUpDraftActions.All"/>; unknown keys are ignored.</summary>
	public List<string> Actions { get; set; } = [];
}

/// <summary>
/// Something the user can ask the agent to do next when inference writes a follow-up.
/// <paramref name="Instruction"/> is what the inference model is told the owner wants.
/// </summary>
public sealed record FollowUpDraftAction(string Key, string Label, string Instruction);

public static class FollowUpDraftActions
{
	public const string KeepGoing = "keep-going";
	public const string CodeReview = "code-review";
	public const string TestAndVerify = "test-and-verify";
	public const string AddTests = "add-tests";

	public static IReadOnlyList<FollowUpDraftAction> All { get; } =
	[
		new(KeepGoing, "Keep going", "Keep going and finish whatever is left of the goal."),
		new(CodeReview, "Review the code", "Review the changes made so far for bugs, missed edge cases and code quality, and fix what turns up."),
		new(TestAndVerify, "Test and verify", "Run the app and test the changes by hand to verify they work, and fix any bugs found."),
		new(AddTests, "Add tests", "Add or update automated tests that cover the changes, and make sure they pass.")
	];

	/// <summary>The known actions among <paramref name="keys"/>, in the order of <see cref="All"/>.</summary>
	public static IReadOnlyList<FollowUpDraftAction> Resolve(IEnumerable<string>? keys)
	{
		if (keys == null)
		{
			return [];
		}

		var picked = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
		return All.Where(action => picked.Contains(action.Key)).ToList();
	}
}
