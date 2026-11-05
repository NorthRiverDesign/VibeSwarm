using System.Text;
using System.Text.RegularExpressions;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

/// <summary>What a reviewer decided: nothing must change, or these issues must be fixed.</summary>
public sealed record ReviewVerdict(bool Approved, string? Issues);

/// <summary>
/// Prompts for the second model that reviews a job's work before delivery, and for sending what
/// it found back to the job's agent.
/// </summary>
public static partial class ReviewPrompts
{
	public const string ApprovedMarker = "REVIEW_APPROVED";
	public const string IssuesMarker = "ISSUES:";
	public const string TaskType = "review";

	/// <summary>How much of the diff the reviewer is shown; beyond it, it opens the files itself.</summary>
	public const int MaxDiffLength = 60_000;

	public const int MaxPlanLength = 6_000;
	public const int MaxBuildOutputLength = 6_000;

	public const string SystemPrompt =
		"You review another coding agent's finished work before it is committed. You may read files in the "
		+ "repository but must not change anything. Judge only whether the change does what the goal asks, and "
		+ "does it correctly: bugs, requirements or acceptance criteria it missed, edge cases it breaks, missing "
		+ "or wrong tests for new behaviour, security problems, and work left half done. Ignore style, naming, "
		+ "formatting and anything the goal didn't ask for. Name the file and say what is wrong for each problem. "
		+ "End your reply with exactly one of these: a line containing only " + ApprovedMarker + " when nothing "
		+ "must change; or a line containing only " + IssuesMarker + " followed by a numbered list of the problems "
		+ "that must be fixed, most important first, at most 8.";

	public static string BuildReviewPrompt(string goal, string? plan, string diff, bool canOpenFiles)
	{
		var builder = new StringBuilder();
		builder.AppendLine("# The goal the agent was given");
		builder.AppendLine(goal.Trim());

		if (!string.IsNullOrWhiteSpace(plan))
		{
			builder.AppendLine();
			builder.AppendLine("# The plan it worked from, with its acceptance criteria");
			builder.AppendLine(Truncate(plan.Trim(), MaxPlanLength));
		}

		builder.AppendLine();
		builder.AppendLine("# The change, as a diff against the commit the job started from");
		if (diff.Length > MaxDiffLength)
		{
			builder.AppendLine(diff[..MaxDiffLength]);
			builder.AppendLine(canOpenFiles
				? "[The diff is cut off here. Open the files listed in its summary to see the rest.]"
				: "[The diff is cut off here.]");
		}
		else
		{
			builder.AppendLine(diff.TrimEnd());
		}

		builder.AppendLine();
		builder.Append("Review the change now. End with ").Append(ApprovedMarker).Append(" or ").Append(IssuesMarker).AppendLine(" and the list.");
		return builder.ToString();
	}

	/// <summary>What the agent is told when the reviewer found problems.</summary>
	public static string BuildFixPrompt(string issues, string? reviewerName, int round)
	{
		var reviewer = string.IsNullOrWhiteSpace(reviewerName) ? "An independent reviewer" : $"An independent reviewer ({reviewerName.Trim()})";
		return $"""
			[Review round {round} of {JobReview.MaxRounds}] {reviewer} checked your work and found these problems:

			{issues.Trim()}

			Fix the ones that are real. If you disagree with one, say why in a sentence and leave it. Check the build and tests again, then finish with a short summary of what you changed.
			""";
	}

	/// <summary>What the agent is told when the project's build or tests failed on its work.</summary>
	public static string BuildBuildFailurePrompt(string? buildOutput, int round)
	{
		var output = string.IsNullOrWhiteSpace(buildOutput) ? "(no output)" : TailOf(buildOutput.Trim(), MaxBuildOutputLength);
		return $"""
			[Review round {round} of {JobReview.MaxRounds}] VibeSwarm ran the project's build and tests on your work and they failed:

			```
			{output}
			```

			Fix the failure, run the build and tests again, then finish with a short summary of what you changed.
			""";
	}

	/// <summary>
	/// The reviewer's decision, or null when it gave none. Whichever marker comes last wins, so a
	/// reviewer that thinks aloud before deciding is read by its conclusion.
	/// </summary>
	public static ReviewVerdict? ParseVerdict(string? reply)
	{
		if (string.IsNullOrWhiteSpace(reply))
		{
			return null;
		}

		var text = ThinkBlockRegex().Replace(reply, string.Empty);
		var approvedMatches = ApprovedLineRegex().Matches(text);
		var approvedAt = approvedMatches.Count == 0 ? -1 : approvedMatches[^1].Index;
		var issuesAt = LastMarkerLine(text);
		if (approvedAt < 0 && issuesAt < 0)
		{
			return null;
		}

		if (approvedAt > issuesAt)
		{
			return new ReviewVerdict(true, null);
		}

		var issues = text[(issuesAt + IssuesMarker.Length)..].TrimStart('*', '_', ' ', '\t').Trim();
		return issues.Length == 0
			? new ReviewVerdict(true, null)
			: new ReviewVerdict(false, Truncate(issues, 6_000));
	}

	private static int LastMarkerLine(string text)
	{
		var matches = IssuesLineRegex().Matches(text);
		return matches.Count == 0 ? -1 : matches[^1].Index + matches[^1].Value.IndexOf(IssuesMarker, StringComparison.OrdinalIgnoreCase);
	}

	private static string Truncate(string text, int maxLength)
		=> text.Length > maxLength ? text[..maxLength].TrimEnd() + " …" : text;

	private static string TailOf(string text, int maxLength)
		=> text.Length > maxLength ? "… " + text[^maxLength..].TrimStart() : text;

	[GeneratedRegex(@"<think>.*?(</think>|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
	private static partial Regex ThinkBlockRegex();

	// The marker on a line of its own, so "this would be REVIEW_APPROVED once fixed" doesn't count.
	[GeneratedRegex(@"^[ \t>*#_`]*REVIEW_APPROVED[ \t*_`.]*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
	private static partial Regex ApprovedLineRegex();

	[GeneratedRegex(@"^[ \t>*#_]*ISSUES:", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
	private static partial Regex IssuesLineRegex();
}
