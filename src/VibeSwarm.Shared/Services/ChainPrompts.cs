using System.Text;
using System.Text.RegularExpressions;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

/// <summary>One step as the Lead wrote it, before it becomes a <see cref="JobStep"/>.</summary>
public sealed record PlannedStep(string Title, string Instructions, string? DoneWhen);

/// <summary>The Lead's plan: a brief every step is given, and the steps in order.</summary>
public sealed record ChainPlan(string Brief, IReadOnlyList<PlannedStep> Steps);

/// <summary>
/// What a team job's chain says to its models: the Lead is asked for a brief and an ordered list
/// of steps, and each step's builder is given the brief, the list and its own step. Steps are
/// marked with plain text headings, as the Lead's suggestions and the reviewer's verdict are, so
/// a model that adds a sentence of its own doesn't break the parse.
/// </summary>
public static partial class ChainPrompts
{
	/// <summary>The most steps a plan may hold; a longer list keeps its first ones.</summary>
	public const int MaxPlannedSteps = 8;

	/// <summary>The line the Lead's check ends with when the job is done.</summary>
	public const string CheckPassedMarker = "CHECK_PASSED";

	private const string StepHeading = "## STEP:";
	private const string HandoffHeading = "## Handoff";
	private const int HandoffMaxLength = 1500;

	[GeneratedRegex(@"^\s*#{1,3}\s*STEP\s*(?:\d+\s*)?[:.\-]\s*(?<title>.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
	private static partial Regex StepHeadingPattern();

	[GeneratedRegex(@"^\s*#{1,3}\s*Handoff\b.*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
	private static partial Regex HandoffHeadingPattern();

	[GeneratedRegex(@"^\s*\**\s*Done when\s*\**\s*:\s*", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
	private static partial Regex DoneWhenPattern();

	[GeneratedRegex(@"^\s*\**\s*Do\s*\**\s*:\s*", RegexOptions.IgnoreCase)]
	private static partial Regex DoPattern();

	/// <summary>Asks the Lead to turn a goal into a brief and the steps to build it.</summary>
	public static string BuildPlanPrompt(string? goal)
	{
		var task = string.IsNullOrWhiteSpace(goal) ? "No task description was provided." : goal.Trim();
		return $"""
			You are the lead engineer on this repository. Read the code this task touches, then plan the work for builders who will carry it out without you.

			## Task
			{task}

			## What to write
			First a brief of under 300 words: the outcome wanted, the acceptance criteria as a short list, the files and patterns that matter, and anything the builders must not do. End the brief with one line naming the whole job as a commit subject, imperative and under 72 characters: <commit-summary>Add the thing</commit-summary>

			Then the steps, in the order they should be built. A small change is one step. Use more only when the work divides into parts that each stand alone, and never more than {MaxPlannedSteps}. Write each step exactly like this:

			{StepHeading} <short title>
			Do: <what to build in this step, concretely>
			Done when: <how to tell this step is finished>

			Rules for steps:
			- Each step is built by a fresh agent that has not seen the others work. It gets your brief, the list of steps and a short note from each earlier step.
			- Each step must leave the project building and its tests passing.
			- Every step changes the repository. Do not write a step for planning, reviewing or summarising.

			Return only the brief and the steps. Do not implement anything, and do not ask questions: choose the option that best fits the code and say so in the brief.
			""";
	}

	/// <summary>
	/// Reads the Lead's reply. Text before the first step heading is the brief. A reply with no
	/// step headings is a plan to carry out whole, as a single step.
	/// </summary>
	public static ChainPlan ParsePlan(string? reply, string? goal = null)
	{
		var text = (reply ?? string.Empty).Trim();
		var headings = StepHeadingPattern().Matches(text);
		if (headings.Count == 0)
		{
			return new ChainPlan(text, [new PlannedStep(TitleFromGoal(goal), "Carry out the plan in the brief.", null)]);
		}

		var steps = ReadSteps(text, headings, MaxPlannedSteps);
		if (steps.Count == 0)
		{
			return new ChainPlan(text, [new PlannedStep(TitleFromGoal(goal), "Carry out the plan in the brief.", null)]);
		}

		var brief = text[..headings[0].Index].Trim();
		return new ChainPlan(brief.Length == 0 ? text : brief, steps);
	}

	private static List<PlannedStep> ReadSteps(string text, MatchCollection headings, int max)
	{
		var steps = new List<PlannedStep>();
		for (var index = 0; index < headings.Count && steps.Count < max; index++)
		{
			var heading = headings[index];
			var bodyStart = heading.Index + heading.Length;
			var bodyEnd = index + 1 < headings.Count ? headings[index + 1].Index : text.Length;
			var body = text[bodyStart..bodyEnd].Trim();

			string instructions = body;
			string? doneWhen = null;
			var doneMatch = DoneWhenPattern().Match(body);
			if (doneMatch.Success)
			{
				instructions = body[..doneMatch.Index].Trim();
				doneWhen = body[(doneMatch.Index + doneMatch.Length)..].Trim();
			}

			instructions = DoPattern().Replace(instructions, string.Empty).Trim();
			var title = Truncate(heading.Groups["title"].Value.Trim().Trim('*', '`'), JobStep.TitleMaxLength);
			if (title.Length == 0)
			{
				continue;
			}

			steps.Add(new PlannedStep(
				title,
				Truncate(instructions.Length == 0 ? title : instructions, JobStep.TextMaxLength),
				string.IsNullOrWhiteSpace(doneWhen) ? null : Truncate(doneWhen, JobStep.SummaryMaxLength)));
		}

		return steps;
	}

	/// <summary>
	/// Asks the Lead, once the steps are built, whether the job is done: it passes the result or
	/// writes the steps still needed. It is given the notes and the list of changed files rather
	/// than a diff, and opens what it wants to read.
	/// </summary>
	public static string BuildCheckPrompt(string? goal, string? brief, IReadOnlyList<JobStep> steps, IReadOnlyList<string> changedFiles)
	{
		var sb = new StringBuilder();
		sb.AppendLine("You are the lead engineer who planned this job. Its steps have been built. Read the result in the repository and decide whether the task is done.");
		sb.AppendLine();
		sb.AppendLine("## Task");
		sb.AppendLine(string.IsNullOrWhiteSpace(goal) ? "No task description was provided." : goal.Trim());
		if (!string.IsNullOrWhiteSpace(brief))
		{
			sb.AppendLine();
			sb.AppendLine("## Your brief");
			sb.AppendLine(brief.Trim());
		}

		sb.AppendLine();
		sb.AppendLine("## What the builders did");
		var number = 0;
		foreach (var step in steps.Where(step => step.Status == JobStepStatus.Done).OrderBy(step => step.Order))
		{
			sb.AppendLine($"{++number}. {step.Title}");
			if (!string.IsNullOrWhiteSpace(step.Summary))
			{
				sb.AppendLine($"   Builder's note: {Truncate(step.Summary.Trim().ReplaceLineEndings(" "), 600)}");
			}

			if (step.ReviewStatus == JobReviewStatus.Unresolved && !string.IsNullOrWhiteSpace(step.ReviewNotes))
			{
				sb.AppendLine($"   Review findings left open: {Truncate(step.ReviewNotes.Trim().ReplaceLineEndings(" "), 800)}");
			}
		}

		if (changedFiles.Count > 0)
		{
			sb.AppendLine();
			sb.AppendLine("## Files changed");
			foreach (var file in changedFiles.Take(80))
			{
				sb.AppendLine($"- {file}");
			}
		}

		sb.AppendLine();
		sb.AppendLine("## What to write");
		sb.AppendLine("Open the files that matter and judge the result against the task and the brief's acceptance criteria. Polish is not a reason to ask for more: only something the task needs that is missing or wrong.");
		sb.AppendLine($"If the task is done, end with a line that says exactly: {CheckPassedMarker}");
		sb.AppendLine($"Otherwise write the steps still needed, at most {JobChain.MaxStepsPerCheck}, each exactly like this:");
		sb.AppendLine();
		sb.AppendLine($"{StepHeading} <short title>");
		sb.AppendLine("Do: <what to build in this step, concretely>");
		sb.AppendLine("Done when: <how to tell this step is finished>");
		sb.AppendLine();
		sb.Append("Do not change anything yourself, and do not ask questions.");
		return sb.ToString();
	}

	/// <summary>
	/// The steps the Lead's check asks for; none when it passed the job. Whichever comes last
	/// decides, so a reply that thinks aloud about a step and then passes the job is a pass.
	/// </summary>
	public static IReadOnlyList<PlannedStep> ParseCheck(string? reply)
	{
		var text = (reply ?? string.Empty).Trim();
		var headings = StepHeadingPattern().Matches(text);
		if (headings.Count == 0)
		{
			return [];
		}

		var passedAt = text.LastIndexOf(CheckPassedMarker, StringComparison.Ordinal);
		return passedAt > headings[^1].Index ? [] : ReadSteps(text, headings, JobChain.MaxStepsPerCheck);
	}

	/// <summary>
	/// What a step's builder is told beyond the job's own prompt: the brief, where its step sits
	/// among the others, what earlier steps left behind, and to do its own step only.
	/// </summary>
	public static string BuildStepSection(string? brief, IReadOnlyList<JobStep> steps, JobStep step)
	{
		var ordered = steps.Where(other => other.Status != JobStepStatus.Skipped || other.Id == step.Id)
			.OrderBy(other => other.Order)
			.ToList();
		var position = ordered.FindIndex(other => other.Id == step.Id) + 1;
		var sb = new StringBuilder();

		if (!string.IsNullOrWhiteSpace(brief))
		{
			sb.AppendLine("<brief>");
			sb.AppendLine(brief.Trim());
			sb.AppendLine("</brief>");
			sb.AppendLine();
		}

		sb.AppendLine("<steps>");
		sb.AppendLine("The lead engineer divided this task into steps. Each is built by a different agent, in order.");
		for (var index = 0; index < ordered.Count; index++)
		{
			var other = ordered[index];
			var state = other.Id == step.Id ? "yours"
				: other.Status == JobStepStatus.Done ? "done"
				: "later";
			sb.AppendLine($"{index + 1}. [{state}] {other.Title}");
			if (other.Status == JobStepStatus.Done && other.Id != step.Id && !string.IsNullOrWhiteSpace(other.Summary))
			{
				sb.AppendLine($"   Note from its builder: {Truncate(other.Summary.Trim().ReplaceLineEndings(" "), 600)}");
			}
		}

		sb.AppendLine("</steps>");
		sb.AppendLine();
		sb.AppendLine($"<your_step number=\"{position}\" of=\"{ordered.Count}\">");
		sb.AppendLine(step.Title);
		sb.AppendLine($"Do: {step.Instructions.Trim()}");
		if (!string.IsNullOrWhiteSpace(step.DoneWhen))
		{
			sb.AppendLine($"Done when: {step.DoneWhen.Trim()}");
		}

		sb.AppendLine("</your_step>");
		sb.AppendLine();
		sb.AppendLine("Do your step, and only your step. Earlier steps are already in the working tree: build on them and leave them as they are unless your step needs them changed. Later steps belong to other agents.");
		sb.AppendLine("Leave the project building and its tests passing.");
		sb.Append($"Finish with a section headed \"{HandoffHeading}\": two or three sentences on what you changed and what the next agent must know.");
		return sb.ToString();
	}

	/// <summary>
	/// The note a step's builder left for the next: what follows its Handoff heading, or the end
	/// of what it said when it wrote none.
	/// </summary>
	public static string? ExtractHandoff(string? reply)
	{
		if (string.IsNullOrWhiteSpace(reply))
		{
			return null;
		}

		var text = reply.Trim();
		var headings = HandoffHeadingPattern().Matches(text);
		if (headings.Count > 0)
		{
			var last = headings[^1];
			var note = text[(last.Index + last.Length)..].Trim();
			if (note.Length > 0)
			{
				return Truncate(note, HandoffMaxLength);
			}
		}

		// No heading: the closing lines of a reply are where an agent says what it did.
		return text.Length <= 600 ? text : "…" + text[^600..].TrimStart();
	}

	private static string TitleFromGoal(string? goal)
	{
		var firstLine = (goal ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
		return string.IsNullOrWhiteSpace(firstLine) ? "Build it" : Truncate(firstLine, 80);
	}

	private static string Truncate(string text, int maxLength)
		=> text.Length <= maxLength ? text : text[..(maxLength - 1)].TrimEnd() + "…";
}
