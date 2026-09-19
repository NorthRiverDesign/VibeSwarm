using System.Text.RegularExpressions;

namespace VibeSwarm.Shared.Services;

/// <summary>How a failed run should be treated by anything that might run it again.</summary>
public enum ProviderFailureKind
{
	/// <summary>The provider issued the error. Another attempt may well succeed.</summary>
	Retryable,

	/// <summary>The CLI or its host is misconfigured. Every attempt fails identically.</summary>
	Unrecoverable
}

/// <summary>
/// Tells an application failure apart from one the provider issued.
///
/// A provider that is rate limited, stalled or briefly unavailable is worth another go.
/// A CLI that cannot start — a missing OS dependency, an expired login, an argument the
/// installed version no longer accepts — fails the same way every time, so retrying it
/// only burns the usage budget and buries the job in a pile of identical failures.
///
/// Such a crash prints a stack trace from the CLI's own bundle, so the one line that
/// says what actually went wrong is pulled out for the job to display.
/// </summary>
public static class ProviderFailureClassifier
{
	private const int MaxSummaryLength = 600;

	/// <summary>
	/// High-signal markers of a host or configuration fault. Deliberately narrow: anything
	/// not listed here stays retryable, because wrongly halting automation is worse than
	/// one wasted retry.
	/// </summary>
	private static readonly string[] UnrecoverableMarkers =
	[
		// The CLI needs an OS package the host does not have (e.g. bubblewrap for
		// subprocess isolation).
		"bubblewrap is required",
		// The CLI binary itself is missing or not executable.
		"claude: command not found",
		"executable path is not configured",
		"failed to start claude cli process",
		"failed to start process:",
		// The installed CLI does not understand an argument, i.e. a version mismatch.
		"unknown option",
		"unknown argument",
		"unrecognized option",
		// The session has no usable credentials; no number of retries creates one.
		"invalid api key",
		"please run /login",
		"not authenticated",
		"authentication_error"
	];

	/// <summary>Lines from a bundled-JavaScript stack trace, which say nothing useful.</summary>
	private static readonly Regex NoiseLine = new(
		@"^\s*(\d+\s*\|| *at\s|\^|Bun v|node:internal)",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	public static ProviderFailureKind Classify(string? errorText)
	{
		if (string.IsNullOrWhiteSpace(errorText))
		{
			return ProviderFailureKind.Retryable;
		}

		return UnrecoverableMarkers.Any(marker =>
			errorText.Contains(marker, StringComparison.OrdinalIgnoreCase))
			? ProviderFailureKind.Unrecoverable
			: ProviderFailureKind.Retryable;
	}

	public static bool IsUnrecoverable(string? errorText) =>
		Classify(errorText) == ProviderFailureKind.Unrecoverable;

	/// <summary>
	/// Reduces raw CLI output to the line worth showing: the reported error if there is
	/// one, otherwise the last line that is not stack-trace noise.
	/// </summary>
	public static string? Summarize(string? errorText)
	{
		if (string.IsNullOrWhiteSpace(errorText))
		{
			return null;
		}

		var lines = errorText
			.Split('\n')
			.Select(line => line.TrimEnd('\r').Trim())
			.Where(line => line.Length > 0 && !NoiseLine.IsMatch(line))
			.ToList();

		if (lines.Count == 0)
		{
			return Truncate(errorText.Trim());
		}

		var reported = lines.LastOrDefault(line =>
			line.StartsWith("error:", StringComparison.OrdinalIgnoreCase) ||
			UnrecoverableMarkers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase)));

		return Truncate(reported ?? lines[^1]);
	}

	private static string Truncate(string value) =>
		value.Length <= MaxSummaryLength ? value : value[..MaxSummaryLength] + "…";
}
