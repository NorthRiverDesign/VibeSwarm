using System.Text;
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
	private const int MaxCommitLogEntries = 10;
	private const int MaxCommitSubjectLength = 72;

	private static readonly string[] ActionKeywords =
	[
		"add", "added", "create", "created", "implement", "implemented",
		"fix", "fixed", "resolve", "resolved",
		"update", "updated", "modify", "modified", "change", "changed",
		"remove", "removed", "delete", "deleted",
		"refactor", "refactored", "restructure", "restructured",
		"improve", "improved", "enhance", "enhanced", "optimize", "optimized",
		"move", "moved", "rename", "renamed",
		"configure", "configured", "setup", "set up",
		"test", "tested",
		"review", "reviewed", "audit", "audited", "check", "checked",
		"secure", "secured", "harden", "hardened"
	];

	/// <summary>
	/// Generates a commit message summary from job data.
	/// </summary>
	/// <param name="job">The completed job</param>
	/// <returns>A concise summary suitable for a commit message, or null if insufficient data</returns>
	public static string? GenerateSummary(Job job)
	{
		if (job == null)
			return null;

		return GenerateSummary(
			gitDiff: job.GitDiff,
			goalPrompt: job.GoalPrompt,
			consoleOutput: job.ConsoleOutput,
			title: job.Title);
	}

	/// <summary>
	/// Generates a commit message summary from job data with an explicit commit log.
	/// </summary>
	/// <param name="job">The completed job</param>
	/// <param name="commitLog">List of commit messages made during job execution</param>
	/// <returns>A concise summary suitable for a commit message, or null if insufficient data</returns>
	public static string? GenerateSummary(Job job, IReadOnlyList<string>? commitLog)
	{
		if (job == null)
			return null;

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

		// Build the summary
		return BuildSummary(diffInfo, actionContext, goalPrompt, title, commitLog);
	}

	public static string BuildCommitSubject(Job job)
	{
		ArgumentNullException.ThrowIfNull(job);

		return BuildCommitSubject(job.SessionSummary, job.Title, job.GoalPrompt, job.ConsoleOutput);
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

		var match = CommitSummaryTagRegex().Match(consoleOutput);
		if (match.Success)
		{
			var raw = match.Groups[1].Value;
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

			return NormalizeCommitSubject(subject);
		}

		return null;
	}

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

		var lowerPrompt = goalPrompt.ToLowerInvariant();

		// Find the primary action keyword
		foreach (var keyword in ActionKeywords)
		{
			var index = lowerPrompt.IndexOf(keyword, StringComparison.Ordinal);
			if (index >= 0)
			{
				context.ActionVerb = NormalizeActionVerb(keyword);
				context.FoundAt = index;
				break;
			}
		}

		// If no action found, default based on common patterns
		if (string.IsNullOrEmpty(context.ActionVerb))
		{
			if (lowerPrompt.Contains("bug") || lowerPrompt.Contains("error") || lowerPrompt.Contains("issue"))
				context.ActionVerb = "Fix";
			else if (lowerPrompt.Contains("new") || lowerPrompt.Contains("feature"))
				context.ActionVerb = "Add";
			else
				context.ActionVerb = "Update";
		}

		// Extract a brief subject from the prompt (first meaningful clause)
		context.Subject = ExtractSubject(goalPrompt);

		return context;
	}

	/// <summary>
	/// Normalizes action verbs to their present tense, capitalized form.
	/// </summary>
	private static string NormalizeActionVerb(string verb)
	{
		return verb.ToLowerInvariant() switch
		{
			"add" or "added" or "create" or "created" => "Add",
			"implement" or "implemented" => "Implement",
			"fix" or "fixed" or "resolve" or "resolved" => "Fix",
			"update" or "updated" or "modify" or "modified" or "change" or "changed" => "Update",
			"remove" or "removed" or "delete" or "deleted" => "Remove",
			"refactor" or "refactored" or "restructure" or "restructured" => "Refactor",
			"improve" or "improved" or "enhance" or "enhanced" or "optimize" or "optimized" => "Improve",
			"move" or "moved" or "rename" or "renamed" => "Rename",
			"configure" or "configured" or "setup" or "set up" => "Configure",
			"test" or "tested" => "Add tests for",
			"review" or "reviewed" or "audit" or "audited" or "check" or "checked" => "Review",
			"secure" or "secured" or "harden" or "hardened" => "Secure",
			_ => char.ToUpper(verb[0]) + verb[1..].ToLower()
		};
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
			if (!string.IsNullOrWhiteSpace(subject))
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

		if (normalized.Length == 0 || IsCommitArtifactLine(normalized))
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
			var normalizedTitle = title
				.Replace("\\n", "\n")
				.Replace("\\r", "\r");
			var firstLine = normalizedTitle
				.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
				.Select(l => l.Trim())
				.FirstOrDefault(l => l.Length > 0)
				?? normalizedTitle.Trim();

			var cleanTitle = TruncateAtWordBoundary(firstLine, MaxCommitSubjectLength);
			if (cleanTitle.Length == 0)
			{
				cleanTitle = firstLine;
			}

			return char.ToUpper(cleanTitle[0]) + cleanTitle[1..];
		}

		if (!string.IsNullOrEmpty(actionContext.Subject))
		{
			var subjectLower = actionContext.Subject.ToLowerInvariant();
			var startsWithVerb = ActionKeywords.Any(k => subjectLower.StartsWith(k));

			if (startsWithVerb)
			{
				return char.ToUpper(actionContext.Subject[0]) + actionContext.Subject[1..];
			}

			return $"{actionContext.ActionVerb} {char.ToLower(actionContext.Subject[0])}{actionContext.Subject[1..]}";
		}

		return $"{actionContext.ActionVerb} code";
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
		var sb = new StringBuilder();

		// If we have no meaningful data, return null
		if (diffInfo.ChangedFiles.Count == 0 && string.IsNullOrWhiteSpace(goalPrompt) && string.IsNullOrWhiteSpace(title))
			return null;

		// Build the title line - prefer explicit title over parsing goalPrompt
		sb.Append(BuildHeadline(actionContext, title));

		// If we have commit log entries, add them as bullet points
		if (commitLog != null && commitLog.Count > 0)
		{
			sb.AppendLine();
			sb.AppendLine();
			sb.AppendLine("Changes:");

			var entries = commitLog.Take(MaxCommitLogEntries);
			foreach (var entry in entries)
			{
				sb.Append("• ");
				sb.AppendLine(entry);
			}

			if (commitLog.Count > MaxCommitLogEntries)
			{
				sb.AppendLine($"... and {commitLog.Count - MaxCommitLogEntries} more commit(s)");
			}
		}
		// If no commit log but we have file change info, add a brief summary
		else if (diffInfo.ChangedFiles.Count > 0)
		{
			sb.AppendLine();
			sb.AppendLine();

			// Group files by directory/pattern
			var filePatterns = GetFilePatterns(diffInfo.ChangedFiles);

			if (diffInfo.FilesChanged > 0 || diffInfo.Insertions > 0 || diffInfo.Deletions > 0)
			{
				sb.Append(diffInfo.FilesChanged > 0 ? diffInfo.FilesChanged : diffInfo.ChangedFiles.Count);
				sb.Append(" file(s) changed");

				if (diffInfo.Insertions > 0 || diffInfo.Deletions > 0)
				{
					sb.Append(" (");
					if (diffInfo.Insertions > 0)
					{
						sb.Append('+');
						sb.Append(diffInfo.Insertions);
					}
					if (diffInfo.Insertions > 0 && diffInfo.Deletions > 0)
					{
						sb.Append('/');
					}
					if (diffInfo.Deletions > 0)
					{
						sb.Append('-');
						sb.Append(diffInfo.Deletions);
					}
					sb.Append(')');
				}
			}

			// Add file patterns if useful
			if (filePatterns.Count > 0 && filePatterns.Count <= 5)
			{
				sb.AppendLine();
				sb.Append("Files: ");
				sb.Append(string.Join(", ", filePatterns));
			}
		}

		return sb.ToString().Trim();
	}

	/// <summary>
	/// Groups changed files into meaningful patterns for display.
	/// </summary>
	private static List<string> GetFilePatterns(List<string> changedFiles)
	{
		var patterns = new List<string>();

		if (changedFiles.Count == 0)
			return patterns;

		// If 3 or fewer files, just list them
		if (changedFiles.Count <= 3)
		{
			return changedFiles.Select(f => Path.GetFileName(f)).ToList();
		}

		// Group by directory
		var byDirectory = changedFiles
			.GroupBy(f => Path.GetDirectoryName(f) ?? "")
			.OrderByDescending(g => g.Count())
			.ToList();

		foreach (var group in byDirectory.Take(3))
		{
			var dir = group.Key;
			var count = group.Count();

			if (string.IsNullOrEmpty(dir))
			{
				if (count == 1)
					patterns.Add(Path.GetFileName(group.First()));
				else
					patterns.Add($"{count} root files");
			}
			else
			{
				// Simplify the directory path
				var simplifiedDir = dir.Replace('\\', '/');
				if (simplifiedDir.Length > 30)
				{
					var parts = simplifiedDir.Split('/');
					simplifiedDir = parts.Length > 2
						? $"{parts[0]}/.../{parts[^1]}"
						: simplifiedDir[..27] + "...";
				}

				if (count == 1)
					patterns.Add($"{simplifiedDir}/{Path.GetFileName(group.First())}");
				else
					patterns.Add($"{simplifiedDir}/* ({count})");
			}
		}

		// If there are more directories
		var remaining = changedFiles.Count - byDirectory.Take(3).Sum(g => g.Count());
		if (remaining > 0)
		{
			patterns.Add($"+{remaining} more");
		}

		return patterns;
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
		public string ActionVerb { get; set; } = "Update";
		public string Subject { get; set; } = "";
		public int FoundAt { get; set; } = -1;
	}
}
