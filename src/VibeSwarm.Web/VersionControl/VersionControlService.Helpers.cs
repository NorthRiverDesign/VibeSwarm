using System.Text.Json;
using System.Text.Json.Serialization;
using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Shared.VersionControl;

public sealed partial class VersionControlService
{
	private static string EscapeCommandArgument(string value)
		=> value.Replace("\\", "\\\\").Replace("\"", "\\\"");

	private static T? DeserializeCommandJson<T>(string output, char startToken, char endToken)
	{
		var json = ExtractJsonPayload(output, startToken, endToken);
		if (string.IsNullOrWhiteSpace(json))
		{
			return default;
		}

		return JsonSerializer.Deserialize<T>(json, GitHubJsonSerializerOptions);
	}

	private static string? ExtractJsonPayload(string? output, char startToken, char endToken)
	{
		if (string.IsNullOrWhiteSpace(output))
		{
			return null;
		}

		var trimmedOutput = output.Trim();
		if (trimmedOutput[0] == startToken && trimmedOutput[^1] == endToken)
		{
			return trimmedOutput;
		}

		var startIndex = trimmedOutput.IndexOf(startToken, StringComparison.Ordinal);
		var endIndex = trimmedOutput.LastIndexOf(endToken);
		if (startIndex < 0 || endIndex <= startIndex)
		{
			return null;
		}

		return trimmedOutput.Substring(startIndex, endIndex - startIndex + 1);
	}

	private static string BuildCommandError(GitCommandResult result, string fallbackMessage)
	{
		var parts = new[] { result.Error, result.Output }
			.Where(value => !string.IsNullOrWhiteSpace(value))
			.Select(value => value.Trim())
			.ToArray();
		return parts.Length > 0 ? string.Join(Environment.NewLine, parts) : fallbackMessage;
	}

	private static string BuildPreserveSummary(GitOperationResult result)
	{
		var savedReference = result.SavedReference ?? "stash@{0}";
		var changedFilesCount = result.ChangedFilesCount ?? 0;
		return $" Preserved {changedFilesCount} changed file(s) in {savedReference} before continuing.";
	}

	private static List<string> ParseWorkingTreeStatus(string output)
	{
		if (string.IsNullOrWhiteSpace(output))
		{
			return [];
		}

		var changedFiles = new List<string>();
		foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			var line = rawLine.TrimEnd('\r');
			if (line.Length < 4)
			{
				continue;
			}

			var path = line[3..].Trim();
			var renameSeparatorIndex = path.LastIndexOf(" -> ", StringComparison.Ordinal);
			if (renameSeparatorIndex >= 0)
			{
				path = path[(renameSeparatorIndex + 4)..].Trim();
			}

			if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
			{
				path = path[1..^1];
			}

			if (!string.IsNullOrWhiteSpace(path))
			{
				changedFiles.Add(path);
			}
		}

		return changedFiles
			.Distinct(StringComparer.Ordinal)
			.ToList();
	}

	private static string? ExtractPullRequestUrl(string output)
	{
		if (string.IsNullOrWhiteSpace(output))
		{
			return null;
		}

		foreach (var token in output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
		{
			if (Uri.TryCreate(token.Trim(), UriKind.Absolute, out var uri) &&
				uri.Host.Contains("github.com", StringComparison.OrdinalIgnoreCase) &&
				uri.AbsolutePath.Contains("/pull/", StringComparison.Ordinal))
			{
				return uri.ToString();
			}
		}

		return null;
	}

	private static int? ExtractPullRequestNumber(string? pullRequestUrl)
	{
		if (string.IsNullOrWhiteSpace(pullRequestUrl))
		{
			return null;
		}

		if (!Uri.TryCreate(pullRequestUrl, UriKind.Absolute, out var uri))
		{
			return null;
		}

		var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
		var pullSegmentIndex = Array.IndexOf(segments, "pull");
		if (pullSegmentIndex < 0 || pullSegmentIndex + 1 >= segments.Length)
		{
			return null;
		}

		return int.TryParse(segments[pullSegmentIndex + 1], out var number) ? number : null;
	}

	private static readonly JsonSerializerOptions GitHubJsonSerializerOptions = new()
	{
		PropertyNameCaseInsensitive = true
	};

	private sealed class GitHubViewerResponse
	{
		[JsonPropertyName("login")]
		public string? Login { get; set; }
	}

	private sealed record MergeTargetResolution(string Reference, string LocalBranchName, bool CreateOrResetLocalBranch);
}
