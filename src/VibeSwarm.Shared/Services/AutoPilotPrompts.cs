using System.Text;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

/// <summary>A job an auto-pilot loop ran, as the idea source and polish passes see it.</summary>
public sealed record AutoPilotWorkItem(string Title, bool Succeeded);

/// <summary>
/// What auto-pilot tells its idea source each round, and the goal of a polish pass.
/// </summary>
public static class AutoPilotPrompts
{
	public const string PolishJobTag = "autopilot-polish";

	/// <summary>
	/// What each idea round looks for, in turn. The loop moves on after every iteration and
	/// every round that comes back empty, so a long run keeps finding new ground instead of
	/// circling the same few suggestions. Features come up most often.
	/// </summary>
	public static readonly IReadOnlyList<string> FocusAreas =
	[
		"a missing feature users would value",
		"usability: confusing flows, missing feedback, empty and error states",
		"reliability: error handling, edge cases and data integrity",
		"a missing feature that extends a workflow the project already has",
		"performance: slow paths and unnecessary work",
		"security and input validation",
		"accessibility and small-screen layout",
		"test coverage for important behaviour that has none"
	];

	/// <summary>Keeps the idea context inside the 4,000-character run-context limit.</summary>
	private const int MaxRecentWorkItems = 20;
	private const int MaxTitleLength = 140;

	/// <summary>Job.GoalPrompt is capped at 2,000 characters.</summary>
	private const int MaxPolishGoalLength = 2000;

	public static string GetFocusArea(int round) =>
		FocusAreas[(round % FocusAreas.Count + FocusAreas.Count) % FocusAreas.Count];

	public static string BuildIdeaContext(string focusArea, IReadOnlyList<AutoPilotWorkItem> recentWork)
	{
		var sb = new StringBuilder();
		sb.AppendLine("This is an unattended auto-pilot run: a coding agent builds each idea and commits it, then the next idea follows. Suggest the most valuable next change, small enough for one session.");
		sb.AppendLine($"This round, look for {focusArea}. If there is nothing worthwhile there, suggest the best change elsewhere.");

		if (recentWork.Count > 0)
		{
			sb.AppendLine("Auto-pilot already worked on these, newest first. Don't suggest them again or reword them:");
			foreach (var item in recentWork.Take(MaxRecentWorkItems))
			{
				sb.AppendLine($"- {(item.Succeeded ? "done" : "failed")}: {Shorten(item.Title)}");
			}
		}

		return sb.ToString().TrimEnd();
	}

	public static string BuildPolishTitle(int changeCount) =>
		$"Polish the last {changeCount} auto-pilot change{(changeCount == 1 ? "" : "s")}";

	public static string BuildPolishGoal(IReadOnlyList<string> recentChangeTitles)
	{
		const string header =
			"Polish pass over recent auto-pilot work. Don't add features or change what the software does.\n\n" +
			"Unattended agents made these changes one after another, newest first:\n";
		const string footer =
			"\nFind the code they touched with git log and git diff, then:\n" +
			"1. Fix bugs, unhandled edge cases and inconsistencies between the changes.\n" +
			"2. Refactor for clarity: remove duplication, dead code and leftovers, and match the surrounding code's patterns and naming.\n" +
			"3. Add or fix tests for the behaviour the changes introduced.\n" +
			"4. Make UI wording and layout consistent where the changes touched them.\n" +
			"If it is all clean already, make the most worthwhile small improvement you can find in that code.";

		var sb = new StringBuilder(header);
		foreach (var title in recentChangeTitles)
		{
			var line = $"- {Shorten(title)}\n";
			if (sb.Length + line.Length + footer.Length > MaxPolishGoalLength)
			{
				break;
			}

			sb.Append(line);
		}

		return sb.Append(footer).ToString();
	}

	public static bool IsPolishJob(Job? job) =>
		!string.IsNullOrWhiteSpace(job?.Tags) &&
		job.Tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
			.Contains(PolishJobTag, StringComparer.OrdinalIgnoreCase);

	private static string Shorten(string? text)
	{
		var singleLine = string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
		return singleLine.Length <= MaxTitleLength ? singleLine : singleLine[..(MaxTitleLength - 1)] + "…";
	}
}
