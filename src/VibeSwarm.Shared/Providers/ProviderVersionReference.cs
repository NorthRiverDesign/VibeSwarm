using System.Text.RegularExpressions;

namespace VibeSwarm.Shared.Providers;

/// <summary>
/// The CLI release each provider integration was built and verified against.
///
/// Provider CLIs move fast and their flags, output shapes, and limit reporting change
/// between releases. Every version gate in the provider implementations
/// (<c>ClaudeProvider</c>, <c>CopilotProvider</c>, <c>OpenCodeProvider</c>) is a minimum;
/// this type records the upper bound — the release the behaviour was actually observed on.
///
/// When re-verifying against a newer CLI, update <see cref="LastReviewed"/> and the
/// matching <see cref="ProviderVersionTarget.VerifiedAgainst"/> so the gap between
/// "what we tested" and "what is installed" stays visible.
/// </summary>
public static partial class ProviderVersionReference
{
	/// <summary>
	/// Date the provider integrations were last checked against live CLI releases.
	/// </summary>
	public static readonly DateOnly LastReviewed = new(2026, 9, 18);

	private static readonly Dictionary<ProviderType, ProviderVersionTarget> Targets = new()
	{
		[ProviderType.Claude] = new ProviderVersionTarget(
			ProviderType.Claude,
			Executable: "claude",
			VerifiedAgainst: new Version(2, 1, 276),
			MinimumSupported: new Version(2, 0, 0),
			DocumentationUrl: "https://code.claude.com/docs/en/cli-reference",
			Notes: "Usage limits arrive as 'rate_limit_event' stream-json messages carrying "
				+ "five_hour and seven_day windows. Effort levels accept low/medium/high/xhigh/max, "
				+ "with 'standard' still tolerated as an alias for 'medium'."),

		[ProviderType.Copilot] = new ProviderVersionTarget(
			ProviderType.Copilot,
			Executable: "copilot",
			VerifiedAgainst: new Version(1, 0, 86),
			MinimumSupported: new Version(1, 0, 0),
			DocumentationUrl: "https://docs.github.com/en/copilot/how-tos/copilot-cli",
			Notes: "Billing is moving from premium requests to GitHub AI Credits (--max-ai-credits). "
				+ "--usage-output-file writes end-of-session usage as JSON. Effort levels widened to "
				+ "none/minimal/low/medium/high/xhigh/max. --alt-screen was removed in 1.0.8."),

		[ProviderType.OpenCode] = new ProviderVersionTarget(
			ProviderType.OpenCode,
			Executable: "opencode",
			VerifiedAgainst: new Version(1, 18, 31),
			MinimumSupported: new Version(1, 0, 0),
			DocumentationUrl: "https://opencode.ai/docs/cli",
			Notes: "Bring-your-own-model harness with no first-party quota. Reasoning effort maps to "
				+ "--variant. Metering depends entirely on the configured model provider, so locally "
				+ "hosted open-source models are unmetered."),
	};

	/// <summary>
	/// The verified release target for a provider type.
	/// </summary>
	public static ProviderVersionTarget For(ProviderType providerType)
		=> Targets.TryGetValue(providerType, out var target)
			? target
			: throw new ArgumentOutOfRangeException(nameof(providerType), providerType, "No version reference recorded for this provider.");

	public static bool TryGet(ProviderType providerType, out ProviderVersionTarget target)
		=> Targets.TryGetValue(providerType, out target!);

	/// <summary>
	/// All recorded targets, ordered for stable display.
	/// </summary>
	public static IReadOnlyList<ProviderVersionTarget> All
		=> Targets.Values.OrderBy(target => target.Type.ToString(), StringComparer.Ordinal).ToList();

	/// <summary>
	/// Extracts a version from a CLI's --version output.
	/// </summary>
	/// <remarks>
	/// The three CLIs format this differently — "2.1.276 (Claude Code)",
	/// "GitHub Copilot CLI 1.0.86." and a bare "1.18.31" — so this takes the first
	/// dotted numeric run anywhere in the string rather than assuming a position.
	/// </remarks>
	public static bool TryParseVersion(string? versionOutput, out Version? version)
	{
		version = null;
		if (string.IsNullOrWhiteSpace(versionOutput))
		{
			return false;
		}

		var match = VersionPattern().Match(versionOutput);
		return match.Success && Version.TryParse(match.Value, out version);
	}

	[GeneratedRegex(@"\d+\.\d+(?:\.\d+){0,2}")]
	private static partial Regex VersionPattern();

	/// <summary>
	/// Compares an installed CLI version against the recorded target.
	/// </summary>
	public static VersionSupportState Evaluate(ProviderType providerType, Version? detectedVersion)
	{
		if (detectedVersion == null || !TryGet(providerType, out var target))
		{
			return VersionSupportState.Unknown;
		}

		if (detectedVersion < target.MinimumSupported)
		{
			return VersionSupportState.Unsupported;
		}

		if (detectedVersion < target.VerifiedAgainst)
		{
			return VersionSupportState.Older;
		}

		return detectedVersion > target.VerifiedAgainst
			? VersionSupportState.Newer
			: VersionSupportState.Verified;
	}

	/// <summary>
	/// A short, user-facing explanation of an <see cref="Evaluate"/> result.
	/// </summary>
	public static string Describe(ProviderType providerType, Version? detectedVersion)
	{
		var state = Evaluate(providerType, detectedVersion);
		if (state == VersionSupportState.Unknown || !TryGet(providerType, out var target))
		{
			return "CLI version could not be determined.";
		}

		return state switch
		{
			VersionSupportState.Unsupported =>
				$"CLI {detectedVersion} is older than the minimum supported {target.MinimumSupported}. Update the CLI.",
			VersionSupportState.Older =>
				$"CLI {detectedVersion} is older than the verified {target.VerifiedAgainst}; newer features stay disabled.",
			VersionSupportState.Newer =>
				$"CLI {detectedVersion} is newer than the verified {target.VerifiedAgainst}; behaviour is unverified.",
			_ => $"CLI {detectedVersion} matches the verified release.",
		};
	}
}

/// <summary>
/// A provider CLI release that the integration was built and verified against.
/// </summary>
/// <param name="Type">Provider this target describes.</param>
/// <param name="Executable">Default executable name on PATH.</param>
/// <param name="VerifiedAgainst">Release the integration was observed working on.</param>
/// <param name="MinimumSupported">Oldest release the version gates still cover.</param>
/// <param name="DocumentationUrl">Upstream CLI reference used during verification.</param>
/// <param name="Notes">What changed upstream that this integration depends on.</param>
public sealed record ProviderVersionTarget(
	ProviderType Type,
	string Executable,
	Version VerifiedAgainst,
	Version MinimumSupported,
	string DocumentationUrl,
	string Notes);

/// <summary>
/// How an installed CLI compares to the verified release target.
/// </summary>
public enum VersionSupportState
{
	/// <summary>Version could not be detected.</summary>
	Unknown,

	/// <summary>Older than the minimum the version gates cover.</summary>
	Unsupported,

	/// <summary>Supported, but older than the verified release.</summary>
	Older,

	/// <summary>Exactly the verified release.</summary>
	Verified,

	/// <summary>Newer than the verified release; unverified but allowed.</summary>
	Newer
}
