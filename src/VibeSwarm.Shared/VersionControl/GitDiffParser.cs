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
				var fileName = parts.Length >= 4 ? parts[3].TrimStart('b', '/') : "unknown";

				currentFile = new DiffFile { FileName = fileName };
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
	/// Formats git diff output with HTML syntax highlighting for display
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

		foreach (var line in lines)
		{
			var escapedLine = WebUtility.HtmlEncode(line);

			if (line.StartsWith("@@"))
			{
				result.Append($"<div class=\"diff-hunk text-info bg-dark bg-opacity-50 px-2\">{escapedLine}</div>");
			}
			else if (line.StartsWith("+") && !line.StartsWith("+++"))
			{
				result.Append($"<div class=\"diff-add text-success bg-success bg-opacity-10 px-2\">{escapedLine}</div>");
			}
			else if (line.StartsWith("-") && !line.StartsWith("---"))
			{
				result.Append($"<div class=\"diff-del text-danger bg-danger bg-opacity-10 px-2\">{escapedLine}</div>");
			}
			else if (line.StartsWith("index ") || line.StartsWith("--- ") || line.StartsWith("+++ "))
			{
				result.Append($"<div class=\"diff-meta text-secondary px-2\">{escapedLine}</div>");
			}
			else if (line.StartsWith("diff "))
			{
				result.Append($"<div class=\"diff-header fw-bold px-2\">{escapedLine}</div>");
			}
			else if (line.StartsWith("new file") || line.StartsWith("deleted file"))
			{
				result.Append($"<div class=\"diff-meta text-secondary px-2\">{escapedLine}</div>");
			}
			else
			{
				result.Append($"<div class=\"diff-context px-2\">{escapedLine}</div>");
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

		var jobFileDict = jobDiff.ToDictionary(f => f.FileName, f => f);
		var workingFileDict = workingDiff.ToDictionary(f => f.FileName, f => f);

		// Find files in job diff but not in current working copy
		foreach (var jobFile in jobDiff)
		{
			if (!workingFileDict.ContainsKey(jobFile.FileName))
			{
				missingFiles.Add(jobFile.FileName);
			}
			else
			{
				// Check if the content differs
				var workingFile = workingFileDict[jobFile.FileName];
				if (jobFile.Additions != workingFile.Additions ||
					jobFile.Deletions != workingFile.Deletions ||
					jobFile.DiffContent.Trim() != workingFile.DiffContent.Trim())
				{
					modifiedFiles.Add(jobFile.FileName);
				}
			}
		}

		// Find files in current working copy but not in job diff
		foreach (var workingFile in workingDiff)
		{
			if (!jobFileDict.ContainsKey(workingFile.FileName))
			{
				extraFiles.Add(workingFile.FileName);
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
