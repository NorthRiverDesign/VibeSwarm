using System.Net;
using System.Text;
using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Shared.VersionControl;

/// <summary>
/// Utility class for parsing and formatting git diff output
/// </summary>
public static class GitDiffParser
{
	/// <summary>
	/// Parses a git diff string into individual file diff structures
	/// </summary>
	/// <param name="diff">The raw git diff output</param>
	/// <returns>A list of parsed diff files</returns>
	public static List<DiffFile> ParseDiff(string diff)
	{
		var files = new List<DiffFile>();
		if (string.IsNullOrEmpty(diff))
			return files;

		var lines = diff.Split('\n');
		DiffFile? currentFile = null;
		var currentContent = new StringBuilder();

		foreach (var line in lines)
		{
			if (line.StartsWith("diff --git"))
			{
				// Save previous file
				if (currentFile != null)
				{
					currentFile.DiffContent = currentContent.ToString();
					files.Add(currentFile);
				}

				// Extract filename from "diff --git a/path/to/file b/path/to/file"
				var parts = line.Split(' ');
				var rawName = parts.Length >= 4 ? parts[3] : "unknown";
				// Remove the "b/" prefix (not TrimStart which would strip repeated b/b chars)
				var fileName = rawName.StartsWith("b/") ? rawName[2..] : rawName;
				currentContent.Clear();
				currentContent.AppendLine(line);
			}
			else if (currentFile != null)
			{
				currentContent.AppendLine(line);

				if (line.StartsWith("new file"))
				{
					currentFile.IsNew = true;
				}
				else if (line.StartsWith("deleted file"))
				{
					currentFile.IsDeleted = true;
				}
				else if (line.StartsWith("+") && !line.StartsWith("+++"))
				{
					currentFile.Additions++;
				}
				else if (line.StartsWith("-") && !line.StartsWith("---"))
				{
					currentFile.Deletions++;
				}
			}
		}

		// Save the last file
		if (currentFile != null)
		{
			currentFile.DiffContent = currentContent.ToString();
			files.Add(currentFile);
		}

		return files;
	}

	/// <summary>
	/// Formats git diff output with HTML syntax highlighting for display.
	/// Hides diff metadata lines (diff --git, index, ---, +++) and shows only relevant content with line numbers.
	/// </summary>
	/// <param name="diff">The raw git diff string</param>
	/// <returns>HTML formatted diff content</returns>
	public static string FormatDiffHtml(string diff)
	{
		if (string.IsNullOrEmpty(diff))
			return string.Empty;

		var lines = diff.Split('\n');
		var result = new StringBuilder();
		result.Append("<div class=\"diff-content font-monospace small\">");

		int oldLine = 0;
		int newLine = 0;

		foreach (var line in lines)
		{
			// Skip diff metadata lines - only show relevant content
			if (line.StartsWith("diff --git") ||
				line.StartsWith("index ") ||
				line.StartsWith("--- ") ||
				line.StartsWith("+++ ") ||
				line.StartsWith("new file") ||
				line.StartsWith("deleted file") ||
				line.StartsWith("old mode") ||
				line.StartsWith("new mode") ||
				line.StartsWith("similarity index") ||
				line.StartsWith("rename from") ||
				line.StartsWith("rename to") ||
				line.StartsWith("Binary files"))
			{
				continue;
			}

			var escapedLine = WebUtility.HtmlEncode(line);

			if (line.StartsWith("@@"))
			{
				// Parse hunk header: @@ -oldStart,oldCount +newStart,newCount @@
				var match = System.Text.RegularExpressions.Regex.Match(line, @"@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@");
				if (match.Success)
				{
					oldLine = int.Parse(match.Groups[1].Value);
					newLine = int.Parse(match.Groups[2].Value);
				}
				// Show hunk header spanning both line number columns
				result.Append($"<div class=\"diff-hunk d-flex text-info bg-dark bg-opacity-50\"><span class=\"diff-line-nums text-end pe-2 opacity-50 flex-shrink-0\">...</span><span class=\"px-2 flex-grow-1\">{escapedLine}</span></div>");
			}
			else if (line.StartsWith("+") && !line.StartsWith("+++"))
			{
				result.Append($"<div class=\"diff-add d-flex text-success bg-success bg-opacity-10\"><span class=\"diff-line-nums text-end pe-2 opacity-75 flex-shrink-0\">{newLine}</span><span class=\"px-2 flex-grow-1\">{escapedLine}</span></div>");
				newLine++;
			}
			else if (line.StartsWith("-") && !line.StartsWith("---"))
			{
				result.Append($"<div class=\"diff-del d-flex text-danger bg-danger bg-opacity-10\"><span class=\"diff-line-nums text-end pe-2 opacity-75 flex-shrink-0\">{oldLine}</span><span class=\"px-2 flex-grow-1\">{escapedLine}</span></div>");
				oldLine++;
			}
			else
			{
				// Context line - both line numbers advance
				var lineNum = oldLine > 0 ? oldLine.ToString() : "";
				result.Append($"<div class=\"diff-context d-flex\"><span class=\"diff-line-nums text-end pe-2 opacity-50 flex-shrink-0\">{lineNum}</span><span class=\"px-2 flex-grow-1\">{escapedLine}</span></div>");
				oldLine++;
				newLine++;
			}
		}

		result.Append("</div>");
		return result.ToString();
	}

	/// <summary>
	/// Compares two parsed diffs and returns the differences
	/// </summary>
	/// <param name="jobDiff">The original job diff files</param>
	/// <param name="workingDiff">The current working directory diff files</param>
	/// <returns>Tuple of (missing files, extra files, modified files)</returns>
	public static (List<string> Missing, List<string> Extra, List<string> Modified) CompareDiffs(
		List<DiffFile> jobDiff,
		List<DiffFile> workingDiff)
	{
		var missingFiles = new List<string>();
		var extraFiles = new List<string>();
		var modifiedFiles = new List<string>();

		// Use GroupBy instead of ToDictionary to handle duplicate file entries
		// (e.g. when a file appears in both committed and uncommitted sections).
		// For duplicates, merge additions/deletions across all entries.
		var jobFileDict = jobDiff
			.GroupBy(f => f.FileName)
			.ToDictionary(
				g => g.Key,
				g => new DiffFile
				{
					FileName = g.Key,
					Additions = g.Sum(f => f.Additions),
					Deletions = g.Sum(f => f.Deletions),
					IsNew = g.Any(f => f.IsNew),
					IsDeleted = g.Any(f => f.IsDeleted),
					DiffContent = string.Join("\n", g.Select(f => f.DiffContent))
				});

		var workingFileDict = workingDiff
			.GroupBy(f => f.FileName)
			.ToDictionary(
				g => g.Key,
				g => new DiffFile
				{
					FileName = g.Key,
					Additions = g.Sum(f => f.Additions),
					Deletions = g.Sum(f => f.Deletions),
					IsNew = g.Any(f => f.IsNew),
					IsDeleted = g.Any(f => f.IsDeleted),
					DiffContent = string.Join("\n", g.Select(f => f.DiffContent))
				});

		// Find files in job diff but not in current working copy
		foreach (var (fileName, jobFile) in jobFileDict)
		{
			if (!workingFileDict.TryGetValue(fileName, out var workingFile))
			{
				missingFiles.Add(fileName);
			}
			else
			{
				// Compare by additions/deletions counts only — raw diff content
				// varies between different git diff invocations for the same changes.
				if (jobFile.Additions != workingFile.Additions ||
					jobFile.Deletions != workingFile.Deletions)
				{
					modifiedFiles.Add(fileName);
				}
			}
		}

		// Find files in current working copy but not in job diff
		foreach (var fileName in workingFileDict.Keys)
		{
			if (!jobFileDict.ContainsKey(fileName))
			{
				extraFiles.Add(fileName);
			}
		}

		return (missingFiles, extraFiles, modifiedFiles);
	}

	/// <summary>
	/// Calculates the total additions across all diff files
	/// </summary>
	public static int TotalAdditions(List<DiffFile> files) => files.Sum(f => f.Additions);

	/// <summary>
	/// Calculates the total deletions across all diff files
	/// </summary>
	public static int TotalDeletions(List<DiffFile> files) => files.Sum(f => f.Deletions);
}
