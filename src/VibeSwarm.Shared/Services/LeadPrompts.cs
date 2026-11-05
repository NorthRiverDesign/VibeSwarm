using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Shared.Services;

/// <summary>An idea the Lead proposed: a title for the list and a spec an agent can start from.</summary>
public sealed record LeadIdea(string Title, string Spec, string? Size);

public sealed record LeadReply(string Report, IReadOnlyList<LeadIdea> Ideas);

/// <summary>Everything the Lead is told about a project before it looks at the code.</summary>
public sealed record LeadContext(
	string ProjectName,
	string? Direction,
	string? Description,
	string? Instructions,
	string? Memory,
	string? RepoMap,
	string ProjectSection,
	IReadOnlyList<string> WaitingSuggestions,
	IReadOnlyList<string> DismissedSuggestions,
	LeadReview? PreviousReview,
	DateTime NowUtc);

/// <summary>
/// Prompts for a project's Lead, the read-only pass that looks over a repository and its history
/// and proposes what to build next, and the parser for what it writes back.
/// </summary>
public static partial class LeadPrompts
{
	public const string IdeaHeading = "## IDEA:";
	public const string TaskType = "lead";

	private const int MaxDirectionLength = LeadReviewLimits.DirectionMaxLength;
	private const int MaxTextLength = 6_000;
	private const int MaxPreviousReportLength = 4_000;

	public static readonly string SystemPrompt =
		"You are the Lead of a software project that coding agents build through VibeSwarm. The owner queues "
		+ "ideas from a phone; each idea the owner approves becomes a job that an agent runs on its own, and "
		+ "the agent's work is committed. Your part is to look over the repository and what was done "
		+ "recently, notice what the owner may have missed, and propose the next pieces of work. You may "
		+ "read any file but must not change anything. Read the code that matters instead of guessing. "
		+ "Prefer work that moves the project toward the owner's direction, fixes real bugs or risks you can "
		+ "point to in the code, finishes what earlier jobs left undone (failed jobs, unresolved review "
		+ "findings), and tests important behaviour that has no tests. Skip busywork, cosmetic refactors, and "
		+ "anything already queued, waiting, done or dismissed. Each idea must be one job an agent can finish "
		+ "and verify by itself in one run.";

	public static string BuildPrompt(LeadContext context)
	{
		var builder = new StringBuilder();
		builder.Append("# Project: ").AppendLine(context.ProjectName.Trim());
		builder.Append("Today: ").AppendLine(context.NowUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
		builder.AppendLine();

		builder.AppendLine("# The owner's direction");
		builder.AppendLine(string.IsNullOrWhiteSpace(context.Direction)
			? "The owner hasn't written one. Infer it from the description, the instructions and the recent work."
			: Truncate(context.Direction.Trim(), MaxDirectionLength));

		AppendSection(builder, "# What the project is", context.Description);
		AppendSection(builder, "# Instructions every agent on this project gets", context.Instructions);
		AppendSection(builder, "# Lessons agents recorded in the project memory", context.Memory);
		AppendSection(builder, "# Repository map", context.RepoMap);

		builder.AppendLine();
		builder.AppendLine("# Where the project stands");
		builder.AppendLine(context.ProjectSection.Trim());

		AppendList(builder, "# Already suggested and waiting for the owner (don't repeat these)", context.WaitingSuggestions);
		AppendList(builder, "# Turned down by the owner (never suggest these again)", context.DismissedSuggestions);

		if (context.PreviousReview?.Content is { Length: > 0 } previous)
		{
			builder.AppendLine();
			builder.Append("# Your previous report, from ")
				.Append(context.PreviousReview.CreatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
				.AppendLine(". Say what changed since.");
			builder.AppendLine(Truncate(previous.Trim(), MaxPreviousReportLength));
		}

		builder.AppendLine();
		builder.AppendLine("# What to write");
		builder.AppendLine("Look at the code first. Then start your reply with a short report for the owner, at most 200 words, in plain text: where the project stands and what you noticed.");
		builder.Append("Then write up to ").Append(LeadReviewLimits.MaxIdeasPerReview).AppendLine(" ideas, most valuable first, each in exactly this form:");
		builder.AppendLine();
		builder.Append(IdeaHeading).AppendLine(" <short imperative title, under 80 characters>");
		builder.AppendLine("Why: <one or two sentences on the problem or opportunity, naming the files>");
		builder.AppendLine("Size: <S, M or L>");
		builder.AppendLine("Goal: <what the agent must do, specific enough to start without asking anything>");
		builder.AppendLine("Acceptance criteria:");
		builder.AppendLine("- <an outcome the agent can check>");
		builder.AppendLine("- <another>");
		builder.AppendLine();
		builder.AppendLine("Write fewer ideas rather than weak ones. When nothing is worth doing, write the report and no ideas.");
		return builder.ToString();
	}

	/// <summary>The report and the ideas in the Lead's reply. Ideas without a title are dropped.</summary>
	public static LeadReply Parse(string? reply)
	{
		if (string.IsNullOrWhiteSpace(reply))
		{
			return new LeadReply(string.Empty, []);
		}

		var text = ThinkBlockRegex().Replace(reply, string.Empty).Replace("\r\n", "\n");
		var headings = IdeaHeadingRegex().Matches(text);
		if (headings.Count == 0)
		{
			return new LeadReply(text.Trim(), []);
		}

		var report = text[..headings[0].Index].Trim();
		var ideas = new List<LeadIdea>();
		for (var i = 0; i < headings.Count && ideas.Count < LeadReviewLimits.MaxIdeasPerReview; i++)
		{
			var heading = headings[i];
			var bodyStart = heading.Index + heading.Length;
			var bodyEnd = i + 1 < headings.Count ? headings[i + 1].Index : text.Length;
			var title = CleanTitle(heading.Groups["title"].Value);
			if (title.Length == 0)
			{
				continue;
			}

			var spec = text[bodyStart..bodyEnd].Trim();
			var size = SizeRegex().Match(spec) is { Success: true } match ? match.Groups["size"].Value.ToUpperInvariant() : null;
			ideas.Add(new LeadIdea(
				Truncate(title, ValidationLimits.IdeaDescriptionMaxLength),
				Truncate(spec.Length == 0 ? title : spec, ValidationLimits.IdeaExpandedDescriptionMaxLength),
				size));
		}

		return new LeadReply(report, ideas);
	}

	/// <summary>Text compared when deciding whether two ideas are the same.</summary>
	public static string NormalizeForComparison(string text)
		=> WhitespaceRegex().Replace(text.Trim().TrimEnd('.', '!').ToLowerInvariant(), " ");

	private static void AppendSection(StringBuilder builder, string heading, string? content)
	{
		if (string.IsNullOrWhiteSpace(content))
		{
			return;
		}

		builder.AppendLine();
		builder.AppendLine(heading);
		builder.AppendLine(Truncate(content.Trim(), MaxTextLength));
	}

	private static void AppendList(StringBuilder builder, string heading, IReadOnlyList<string> items)
	{
		if (items.Count == 0)
		{
			return;
		}

		builder.AppendLine();
		builder.AppendLine(heading);
		foreach (var item in items)
		{
			builder.Append("- ").AppendLine(item.Trim());
		}
	}

	private static string CleanTitle(string title)
		=> title.Trim().Trim('*', '_', '`', '"', ' ').Trim();

	private static string Truncate(string text, int maxLength)
		=> text.Length > maxLength ? text[..(maxLength - 1)].TrimEnd() + "…" : text;

	[GeneratedRegex(@"^[ \t]*#{2,4}[ \t]*\**IDEA\**[ \t]*:[ \t]*(?<title>.*)$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
	private static partial Regex IdeaHeadingRegex();

	[GeneratedRegex(@"^[ \t*_-]*Size[ \t*_]*:[ \t*_]*(?<size>[SML])\b", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
	private static partial Regex SizeRegex();

	[GeneratedRegex(@"<think>.*?(</think>|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
	private static partial Regex ThinkBlockRegex();

	[GeneratedRegex(@"\s+")]
	private static partial Regex WhitespaceRegex();
}
