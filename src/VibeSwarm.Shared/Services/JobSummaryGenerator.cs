using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Utilities;
using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// Generates commit message summaries from Job data without requiring AI calls.
/// Parses GitDiff, GoalPrompt, and ConsoleOutput to produce concise commit messages.
/// </summary>
public static partial class JobSummaryGenerator
{
	private const int MaxCommitSubjectLength = 96;

	/// <summary>
	/// The placeholder the prompt templates put between the commit-summary tags. It reaches job
	/// output whenever an agent reads those templates, and is never a real subject.
	/// </summary>
	private const string CommitSummaryPlaceholder = "concise one-line description of what was implemented";
	private static readonly string[] NarrativePrefixes = ["i ", "we ", "i'm ", "we're ", "i’ve ", "we’ve ", "i'd ", "we'd "];
	private static readonly string[] DanglingEndingWords =
	[
		"a", "an", "and", "as", "at", "before", "but", "by", "for", "from",
		"in", "into", "of", "on", "or", "that", "the", "this", "to", "with"
	];

	/// <summary>
	/// Generates a commit message summary from job data.
	/// </summary>
	/// <returns>A concise summary suitable for a commit message, or null if insufficient data</returns>
	public static string? GenerateSummary(Job job)
	{
		if (job == null)
			return null;

		var planningSummary = ExtractCommitSummary(job.PlanningOutput);
		if (!string.IsNullOrWhiteSpace(planningSummary))
		{
			return planningSummary;
		}

		return GenerateSummary(
			gitDiff: job.GitDiff,
			goalPrompt: job.GoalPrompt,
			consoleOutput: job.ConsoleOutput,
			title: job.Title);
	}

	/// <summary>
	/// Generates a commit message summary from job data with an explicit commit log.
	/// </summary>
	/// <param name="commitLog">List of commit messages made during job execution</param>
	/// <returns>A concise summary suitable for a commit message, or null if insufficient data</returns>
	public static string? GenerateSummary(Job job, IReadOnlyList<string>? commitLog)
	{
		if (job == null)
			return null;

		var planningSummary = ExtractCommitSummary(job.PlanningOutput);
		if (!string.IsNullOrWhiteSpace(planningSummary))
		{
			return planningSummary;
		}

		return GenerateSummary(
			gitDiff: job.GitDiff,
			goalPrompt: job.GoalPrompt,
			consoleOutput: job.ConsoleOutput,
			title: job.Title,
			commitLog: commitLog);
	}

	/// <summary>
	/// Generates a commit message summary from individual components.
	/// </summary>
	/// <param name="gitDiff">The git diff output (from Job.GitDiff)</param>
	/// <param name="goalPrompt">The original goal/task prompt</param>
	/// <param name="consoleOutput">Optional console output to scan for context</param>
	/// <param name="title">Optional job title to use as the headline</param>
	/// <param name="commitLog">Optional list of commit messages made during job execution</param>
	/// <returns>A concise summary suitable for a commit message, or null if insufficient data</returns>
	public static string? GenerateSummary(
		string? gitDiff,
		string? goalPrompt,
		string? consoleOutput = null,
		string? title = null,
		IReadOnlyList<string>? commitLog = null)
	{
		// First, try to extract an AI-generated commit summary from console output
		var agentSummary = ExtractAgentCommitSummary(consoleOutput);
		if (!string.IsNullOrWhiteSpace(agentSummary))
		{
			return agentSummary;
		}

		// Parse the git diff for file information
		var diffInfo = ParseGitDiff(gitDiff);

		// Extract action context from goal prompt
		var actionContext = ExtractActionContext(goalPrompt);

		return BuildSummary(diffInfo, actionContext, goalPrompt, title, commitLog);
	}

	public static string BuildCommitSubject(Job job)
	{
		ArgumentNullException.ThrowIfNull(job);

		var planningSummary = ExtractCommitSummary(job.PlanningOutput);
		if (!string.IsNullOrWhiteSpace(planningSummary))
		{
			return BuildCommitSubject(planningSummary, job.Title, job.GoalPrompt);
		}

		return BuildCommitSubject(job.SessionSummary, job.Title, job.GoalPrompt, job.ConsoleOutput);
	}

	public static string? ExtractCommitSummary(string? content)
		=> ExtractAgentCommitSummary(content);

	public static string StripCommitSummary(string? content)
	{
		if (string.IsNullOrWhiteSpace(content))
		{
			return string.Empty;
		}

		var stripped = CommitSummaryTagRegex().Replace(content, string.Empty).Trim();
		if (stripped.Length == 0)
		{
			return string.Empty;
		}

		return ExcessBlankLinesRegex().Replace(stripped, Environment.NewLine + Environment.NewLine);
	}

	public static string BuildCommitSubject(
		string? sessionSummary,
		string? title,
		string? goalPrompt,
		string? consoleOutput = null)
	{
		var promptFallbackSubject = BuildPromptFallbackCommitSubject(goalPrompt);

		var agentSummary = ExtractAgentCommitSummary(consoleOutput);
		if (!string.IsNullOrWhiteSpace(agentSummary)
			&& !IsPromptDerivedCommitSubject(agentSummary, promptFallbackSubject))
		{
			return agentSummary;
		}

		var summarySubject = ExtractCommitSubjectCandidate(sessionSummary);
		if (!string.IsNullOrWhiteSpace(summarySubject)
			&& !IsPromptDerivedCommitSubject(summarySubject, promptFallbackSubject))
		{
			return summarySubject;
		}

		var shouldUseTitle = !JobTitleHelper.ShouldSyncTitleWithGoalPrompt(title, goalPrompt);
		if (shouldUseTitle)
		{
			var titleSubject = ExtractCommitSubjectCandidate(title);
			if (!string.IsNullOrWhiteSpace(titleSubject))
			{
				return titleSubject;
			}
		}

		if (!string.IsNullOrWhiteSpace(promptFallbackSubject))
		{
			return promptFallbackSubject;
		}

		return "Update code";
	}

	private static string? BuildPromptFallbackCommitSubject(string? goalPrompt)
	{
		if (string.IsNullOrWhiteSpace(goalPrompt))
		{
			return null;
		}

		var promptSubject = BuildHeadline(ExtractActionContext(goalPrompt), null);
		return NormalizeCommitSubject(promptSubject);
	}

	private static bool IsPromptDerivedCommitSubject(string? candidate, string? promptFallbackSubject)
	{
		return !string.IsNullOrWhiteSpace(candidate)
			&& !string.IsNullOrWhiteSpace(promptFallbackSubject)
			&& string.Equals(candidate, promptFallbackSubject, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Extracts the AI agent's commit summary from console output.
	/// Looks for content between &lt;commit-summary&gt; tags.
	/// </summary>
	private static string? ExtractAgentCommitSummary(string? consoleOutput)
	{
		if (string.IsNullOrWhiteSpace(consoleOutput))
			return null;

		// The agent is told to end with the tag, so the last one it wrote is its answer. Earlier
		// tags are usually not the agent's at all: a job that reads VibeSwarm's own prompt
		// templates gets the tag and its placeholder back in tool output.
		var agentTexts = GetAgentAuthoredText(consoleOutput);
		for (var textIndex = agentTexts.Count - 1; textIndex >= 0; textIndex--)
		{
			var matches = CommitSummaryTagRegex().Matches(agentTexts[textIndex]);
			for (var matchIndex = matches.Count - 1; matchIndex >= 0; matchIndex--)
			{
				var subject = ParseCommitSummaryTag(matches[matchIndex].Groups[1].Value);
				if (subject != null)
				{
					return subject;
				}
			}
		}

		return null;
	}

	private static string? ParseCommitSummaryTag(string raw)
	{
		// Normalize literal escape sequences that may appear when output is stored as escaped text
		var normalized = raw
			.Replace("\\n", "\n")
			.Replace("\\r", "\r")
			.Replace("\\t", " ");

		// Take only the first non-empty line as the commit subject
		var subject = normalized
			.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
			.Select(l => l.Trim())
			.FirstOrDefault(l => l.Length > 0);

		if (string.IsNullOrWhiteSpace(subject))
			return null;

		var normalizedSubject = NormalizeCommitSubject(subject);
		return IsLowQualitySummaryCandidate(normalizedSubject) ? null : normalizedSubject;
	}

	/// <summary>
	/// Returns the text the agent itself wrote, in order. CLI providers stream one JSON event per
	/// line, and only their message events are the agent's words: tool calls and tool results
	/// carry file contents and command output. Lines that are not JSON events are kept as text.
	/// </summary>
	private static List<string> GetAgentAuthoredText(string output)
	{
		var texts = new List<string>();
		var plainText = new StringBuilder();

		void FlushPlainText()
		{
			if (plainText.Length > 0)
			{
				texts.Add(plainText.ToString());
				plainText.Clear();
			}
		}

		foreach (var line in output.Split('\n'))
		{
			var trimmed = line.Trim();
			if (!trimmed.StartsWith('{'))
			{
				plainText.Append(line).Append('\n');
				continue;
			}

			// Skip parsing the many events that cannot hold a tag.
			if (!trimmed.Contains("commit-summary", StringComparison.OrdinalIgnoreCase))
			{
				FlushPlainText();
				continue;
			}

			JsonDocument document;
			try
			{
				document = JsonDocument.Parse(trimmed);
			}
			catch (JsonException)
			{
				plainText.Append(line).Append('\n');
				continue;
			}

			FlushPlainText();
			using (document)
			{
				var text = GetAgentMessageText(document.RootElement);
				if (!string.IsNullOrWhiteSpace(text))
				{
					texts.Add(text);
				}
			}
		}

		FlushPlainText();
		return texts;
	}

	private static string? GetAgentMessageText(JsonElement root)
	{
		if (root.ValueKind != JsonValueKind.Object
			|| !root.TryGetProperty("type", out var type)
			|| type.ValueKind != JsonValueKind.String)
		{
			return null;
		}

		switch (type.GetString())
		{
			// Claude Code's final answer
			case "result":
				return GetStringProperty(root, "result");

			// Claude Code turns carry content blocks; only text blocks are prose, tool_use input is not.
			// OpenCode message events carry the text directly.
			case "assistant":
			case "message":
				if (root.TryGetProperty("message", out var message)
					&& message.ValueKind == JsonValueKind.Object
					&& message.TryGetProperty("content", out var blocks)
					&& blocks.ValueKind == JsonValueKind.Array)
				{
					var text = new StringBuilder();
					foreach (var block in blocks.EnumerateArray())
					{
						if (block.ValueKind == JsonValueKind.Object
							&& GetStringProperty(block, "type") == "text"
							&& GetStringProperty(block, "text") is { } blockText)
						{
							text.Append(blockText).Append('\n');
						}
					}

					return text.ToString();
				}

				return GetStringProperty(root, "content");

			// Copilot session events
			case "assistant.message":
				return root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
					? GetStringProperty(data, "content")
					: null;

			default:
				return null;
		}
	}

	private static string? GetStringProperty(JsonElement element, string name)
		=> element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;

	/// <summary>
	/// Truncates a string at a word boundary up to the specified max length.
	/// </summary>
	private static string TruncateAtWordBoundary(string text, int maxLength)
	{
		if (text.Length <= maxLength)
			return text;

		var breakAt = text.LastIndexOf(' ', maxLength - 1);
		return breakAt > maxLength / 2 ? text[..breakAt] : text[..maxLength];
	}

	[GeneratedRegex(@"<commit-summary>\s*(.+?)\s*</commit-summary>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
	private static partial Regex CommitSummaryTagRegex();

	[GeneratedRegex(@"(\r?\n\s*){3,}")]
	private static partial Regex ExcessBlankLinesRegex();

	[GeneratedRegex(@"^\s*(changes?|files?|summary|details?)\s*:\s*$", RegexOptions.IgnoreCase)]
	private static partial Regex CommitArtifactHeadingRegex();

	[GeneratedRegex(@"^\s*(?:[-*•]|\d+[.)])\s+")]
	private static partial Regex ListPrefixRegex();

	[GeneratedRegex(@"^\s*\d+\s+file\(s\)\s+changed(?:\s*\([^)]+\))?\s*$", RegexOptions.IgnoreCase)]
	private static partial Regex FileChangeStatsRegex();

	[GeneratedRegex(@"\s*(?:[|]|[-–—:])\s*(?:files?|diff|changes?|details?)\s*:.*$", RegexOptions.IgnoreCase)]
	private static partial Regex InlineArtifactHeadingSuffixRegex();

	[GeneratedRegex(@"\s+(?:\d+\s+file(?:\(s\))?s?\s+changed(?:\s*\([^)]+\))?(?:,\s*\d+\s+insertions?\(\+\))?(?:,\s*\d+\s+deletions?\(-\))?|[+-]\d+(?:/[+-]\d+)+)\s*$", RegexOptions.IgnoreCase)]
	private static partial Regex InlineStatSuffixRegex();

	[GeneratedRegex(@"\s*(?:[|]|[-–—:])\s*(?:(?:[A-Za-z0-9._-]+[/\\])+[A-Za-z0-9._-]+(?:\s*,\s*(?:[A-Za-z0-9._-]+[/\\])+[A-Za-z0-9._-]+)*)\s*$", RegexOptions.IgnoreCase)]
	private static partial Regex InlinePathListSuffixRegex();

	[GeneratedRegex(@"[<>`*_#\[\]\{\}|~]+")]
	private static partial Regex CommitMarkupRegex();

	/// <summary>
	/// Parses a git diff string to extract file changes and statistics.
	/// </summary>
	public static DiffInfo ParseGitDiff(string? gitDiff)
	{
		var info = new DiffInfo();

		if (string.IsNullOrWhiteSpace(gitDiff))
			return info;

		var lines = gitDiff.Split('\n');

		foreach (var line in lines)
		{
			// Parse file headers: "diff --git a/path/file b/path/file"
			if (line.StartsWith("diff --git "))
			{
				var match = DiffHeaderRegex().Match(line);
				if (match.Success)
				{
					var filePath = match.Groups[1].Value;
					if (!info.ChangedFiles.Contains(filePath))
					{
						info.ChangedFiles.Add(filePath);
					}
				}
			}
			// Parse renamed files: "rename from X" / "rename to Y"
			else if (line.StartsWith("rename from ") || line.StartsWith("rename to "))
			{
				info.HasRenames = true;
			}
			// Parse new file mode
			else if (line.StartsWith("new file mode"))
			{
				info.NewFiles++;
			}
			// Parse deleted file mode
			else if (line.StartsWith("deleted file mode"))
			{
				info.DeletedFiles++;
			}
			// Parse shortstat line: "3 files changed, 42 insertions(+), 8 deletions(-)"
			else if (line.Contains("file") && line.Contains("changed") &&
					 (line.Contains("insertion") || line.Contains("deletion")))
			{
				ParseStatLine(line, info);
			}
		}

		// If we didn't get stats from shortstat, count from files
		if (info.FilesChanged == 0 && info.ChangedFiles.Count > 0)
		{
			info.FilesChanged = info.ChangedFiles.Count;
		}

		return info;
	}

	/// <summary>
	/// Extracts action context (verb and subject) from the goal prompt.
	/// </summary>
	private static ActionContext ExtractActionContext(string? goalPrompt)
	{
		var context = new ActionContext();

		if (string.IsNullOrWhiteSpace(goalPrompt))
			return context;

		// Extract a brief subject from the prompt (first meaningful clause)
		context.Subject = ExtractSubject(goalPrompt);

		return context;
	}

	/// <summary>
	/// Extracts a brief subject description from the goal prompt.
	/// </summary>
	private static string ExtractSubject(string goalPrompt)
	{
		if (string.IsNullOrWhiteSpace(goalPrompt))
			return "code changes";

		// Clean up the prompt, normalizing literal escape sequences first
		var subject = goalPrompt
			.Replace("\\n", "\n")
			.Replace("\\r", "\r")
			.Trim();

		// Remove common prefixes
		var prefixesToRemove = new[]
		{
			"please ", "can you ", "could you ", "i want to ", "i need to ",
			"help me ", "let's ", "we need to ", "i'd like to "
		};

		foreach (var prefix in prefixesToRemove)
		{
			if (subject.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			{
				subject = subject[prefix.Length..];
				break;
			}
		}

		// Take the first sentence or up to 80 characters
		var endPunctuation = subject.IndexOfAny(['.', '!', '?', '\n']);
		if (endPunctuation > 0 && endPunctuation < 80)
		{
			subject = subject[..endPunctuation];
		}
		else if (subject.Length > 80)
		{
			// Find a natural break point
			var breakAt = subject.LastIndexOf(' ', 77);
			if (breakAt > 40)
			{
				subject = subject[..breakAt];
			}
			else
			{
				subject = subject[..77];
			}
		}

		return subject.Trim();
	}

	private static string? ExtractCommitSubjectCandidate(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}

		var normalized = text
			.Replace("\\n", "\n")
			.Replace("\\r", "\r");

		foreach (var rawLine in normalized.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
		{
			var line = rawLine.Trim();
			if (line.Length == 0 || IsCommitArtifactLine(line))
			{
				continue;
			}

			var subject = NormalizeCommitSubject(line);
			if (!string.IsNullOrWhiteSpace(subject) && !IsLowQualitySummaryCandidate(subject))
			{
				return subject;
			}
		}

		return null;
	}

	private static bool IsCommitArtifactLine(string line)
	{
		return CommitArtifactHeadingRegex().IsMatch(line)
			|| ListPrefixRegex().IsMatch(line)
			|| FileChangeStatsRegex().IsMatch(line)
			|| line.StartsWith("Files:", StringComparison.OrdinalIgnoreCase)
			|| line.StartsWith("Diff:", StringComparison.OrdinalIgnoreCase);
	}

	private static string? NormalizeCommitSubject(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}

		var normalized = text.Trim();
		normalized = CommitSummaryTagRegex().Replace(normalized, "$1");
		normalized = ListPrefixRegex().Replace(normalized, string.Empty);
		normalized = CommitMarkupRegex().Replace(normalized, string.Empty);
		normalized = normalized.Replace('"', ' ').Replace('\t', ' ');
		normalized = string.Join(" ", normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
		normalized = StripInlineCommitArtifacts(normalized);
		normalized = normalized.Trim(' ', '.', ',', ';', ':', '-', '–', '—');

		if (normalized.Length == 0
			|| IsCommitArtifactLine(normalized)
			|| normalized.Contains(CommitSummaryPlaceholder, StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}

		if (normalized.Length == 1)
		{
			return normalized.ToUpperInvariant();
		}

		normalized = char.ToUpper(normalized[0]) + normalized[1..];
		return TruncateAtWordBoundary(normalized, MaxCommitSubjectLength);
	}

	private static string StripInlineCommitArtifacts(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return string.Empty;
		}

		var normalized = text;
		normalized = InlineArtifactHeadingSuffixRegex().Replace(normalized, string.Empty);
		normalized = InlineStatSuffixRegex().Replace(normalized, string.Empty);
		normalized = InlinePathListSuffixRegex().Replace(normalized, string.Empty);
		return normalized.Trim();
	}

	private static string BuildHeadline(ActionContext actionContext, string? title)
	{
		if (!string.IsNullOrWhiteSpace(title))
		{
			var normalizedTitle = NormalizeCommitSubject(title);
			if (!string.IsNullOrWhiteSpace(normalizedTitle))
			{
				return normalizedTitle;
			}
		}

		// Use the prompt's own words. A verb guessed from elsewhere in the prompt reads as nonsense
		// in front of a subject that already has one ("Update make sure ...").
		if (!string.IsNullOrEmpty(actionContext.Subject))
		{
			return NormalizeCommitSubject(actionContext.Subject) ?? "Update code";
		}

		return "Update code";
	}

	/// <summary>
	/// Builds the final summary from parsed information.
	/// </summary>
	private static string? BuildSummary(
		DiffInfo diffInfo,
		ActionContext actionContext,
		string? goalPrompt,
		string? title = null,
		IReadOnlyList<string>? commitLog = null)
	{
		// If we have no meaningful data, return null
		if (diffInfo.ChangedFiles.Count == 0 && string.IsNullOrWhiteSpace(goalPrompt) && string.IsNullOrWhiteSpace(title))
			return null;

		var preferredTitle = JobTitleHelper.ShouldSyncTitleWithGoalPrompt(title, goalPrompt)
			? null
			: title;

		return BuildHeadline(actionContext, preferredTitle);
	}

	private static bool IsLowQualitySummaryCandidate(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}

		var normalized = text.Trim();
		var lower = normalized.ToLowerInvariant();
		if (!NarrativePrefixes.Any(prefix => lower.StartsWith(prefix, StringComparison.Ordinal)))
		{
			return false;
		}

		var words = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (words.Length < 6)
		{
			return false;
		}

		var lastWord = words[^1].TrimEnd('.', ',', ';', ':', '!', '?');
		return DanglingEndingWords.Any(word => string.Equals(word, lastWord, StringComparison.Ordinal));
	}

	/// <summary>
	/// Parses the shortstat line for file/insertion/deletion counts.
	/// </summary>
	private static void ParseStatLine(string line, DiffInfo info)
	{
		// Pattern: "3 files changed, 42 insertions(+), 8 deletions(-)"
		var match = StatLineRegex().Match(line);
		if (match.Success)
		{
			if (int.TryParse(match.Groups[1].Value, out var files))
				info.FilesChanged = files;
			if (match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out var insertions))
				info.Insertions = insertions;
			if (match.Groups[3].Success && int.TryParse(match.Groups[3].Value, out var deletions))
				info.Deletions = deletions;
		}
	}

	/// <summary>
	/// Creates a GitDiffSummary from parsed diff info.
	/// </summary>
	public static GitDiffSummary? ToGitDiffSummary(DiffInfo info)
	{
		if (info.FilesChanged == 0 && info.ChangedFiles.Count == 0)
			return null;

		return new GitDiffSummary
		{
			FilesChanged = info.FilesChanged > 0 ? info.FilesChanged : info.ChangedFiles.Count,
			Insertions = info.Insertions,
			Deletions = info.Deletions
		};
	}

	[GeneratedRegex(@"diff --git a/(.+?) b/")]
	private static partial Regex DiffHeaderRegex();

	[GeneratedRegex(@"(\d+) file.*?changed(?:.*?(\d+) insertion)?(?:.*?(\d+) deletion)?")]
	private static partial Regex StatLineRegex();

	/// <summary>
	/// Information extracted from parsing a git diff.
	/// </summary>
	public class DiffInfo
	{
		public List<string> ChangedFiles { get; } = [];
		public int FilesChanged { get; set; }
		public int Insertions { get; set; }
		public int Deletions { get; set; }
		public int NewFiles { get; set; }
		public int DeletedFiles { get; set; }
		public bool HasRenames { get; set; }
	}

	/// <summary>
	/// Action context extracted from the goal prompt.
	/// </summary>
	private class ActionContext
	{
		public string Subject { get; set; } = "";
	}
}
