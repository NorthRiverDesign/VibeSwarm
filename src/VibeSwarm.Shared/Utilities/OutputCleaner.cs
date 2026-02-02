using System.Text;
using System.Text.RegularExpressions;

namespace VibeSwarm.Shared.Utilities;

/// <summary>
/// Utilities for cleaning CLI output, removing tool usage information,
/// ANSI codes, and other formatting artifacts.
/// </summary>
public static partial class OutputCleaner
{
	/// <summary>
	/// Regex pattern for ANSI escape codes
	/// </summary>
	private static readonly Regex AnsiCodeRegex = AnsiCodePattern();

	/// <summary>
	/// Patterns that indicate tool usage lines from various CLI agents.
	/// These lines provide metadata about agent operations but are not part of the actual response.
	/// </summary>
	private static readonly string[] ToolUsagePatterns =
	[
		// Claude Code patterns
		@"^● .*$",                                    // Tool invocation line (● List directory, ● Read, etc.)
		@"^\s*└ \d+.*$",                              // Tool result line (└ 20 files found, └ 27 lines read)

		// GitHub Copilot patterns
		@"^> .*\.\.\.$",                              // Loading/processing indicator
		@"^Running tool:.*$",                         // Tool execution notification

		// OpenCode patterns
		@"^\[tool\].*$",                              // Tool invocation marker
		@"^→ .*$",                                    // Arrow-prefixed tool operations

		// Common patterns across agents
		@"^Searching.*\.\.\.$",                       // Search in progress
		@"^Reading.*\.\.\.$",                         // Reading in progress
		@"^Analyzing.*\.\.\.$",                       // Analysis in progress
		@"^\s*\(.*files?\s*(found|read|analyzed)\)$", // File operation results
	];

	/// <summary>
	/// Compiled regex patterns for tool usage detection
	/// </summary>
	private static readonly Regex[] ToolUsageRegexes = ToolUsagePatterns
		.Select(p => new Regex(p, RegexOptions.Compiled | RegexOptions.IgnoreCase))
		.ToArray();

	/// <summary>
	/// Strips ANSI escape codes from the input string.
	/// </summary>
	/// <param name="input">The input string containing potential ANSI codes</param>
	/// <returns>The input with ANSI codes removed</returns>
	public static string StripAnsiCodes(string input)
	{
		if (string.IsNullOrEmpty(input))
			return input;

		return AnsiCodeRegex.Replace(input, string.Empty);
	}

	/// <summary>
	/// Removes tool usage information from CLI agent output.
	/// This strips lines that indicate the agent is reading files, listing directories,
	/// or performing other tool operations that shouldn't be part of the final response.
	/// </summary>
	/// <param name="output">The raw CLI output</param>
	/// <returns>The output with tool usage lines removed</returns>
	public static string StripToolUsage(string output)
	{
		if (string.IsNullOrEmpty(output))
			return output;

		var lines = output.Split('\n');
		var cleanedLines = new List<string>();
		var skipNextLine = false;

		foreach (var line in lines)
		{
			// Skip continuation lines after a tool invocation
			if (skipNextLine && (line.TrimStart().StartsWith("└") || string.IsNullOrWhiteSpace(line)))
			{
				if (!line.TrimStart().StartsWith("└"))
					skipNextLine = false; // Reset if we hit a non-result line
				continue;
			}
			skipNextLine = false;

			// Check if this line matches any tool usage pattern
			var isToolUsageLine = false;
			foreach (var regex in ToolUsageRegexes)
			{
				if (regex.IsMatch(line.TrimEnd()))
				{
					isToolUsageLine = true;
					// If this is a tool invocation (●), expect result lines to follow
					if (line.TrimStart().StartsWith("●"))
						skipNextLine = true;
					break;
				}
			}

			if (!isToolUsageLine)
			{
				cleanedLines.Add(line);
			}
		}

		// Remove leading/trailing empty lines that may result from stripping
		var result = string.Join('\n', cleanedLines);
		return result.Trim();
	}

	/// <summary>
	/// Performs full cleaning of CLI output: strips ANSI codes and tool usage information.
	/// </summary>
	/// <param name="output">The raw CLI output</param>
	/// <returns>The cleaned output suitable for storing as a response</returns>
	public static string CleanCliOutput(string output)
	{
		if (string.IsNullOrEmpty(output))
			return output;

		// First strip ANSI codes, then tool usage
		var withoutAnsi = StripAnsiCodes(output);
		var withoutToolUsage = StripToolUsage(withoutAnsi);

		return withoutToolUsage;
	}

	[GeneratedRegex(@"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])", RegexOptions.Compiled)]
	private static partial Regex AnsiCodePattern();
}
