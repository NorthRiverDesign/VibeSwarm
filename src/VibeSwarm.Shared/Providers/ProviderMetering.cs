namespace VibeSwarm.Shared.Providers;

/// <summary>
/// Decides whether a provider and model combination is metered by an upstream quota.
///
/// Claude Code and GitHub Copilot meter against a subscription, so their usage has to be
/// tracked and jobs paused when a window is exhausted. Self-hosted open-source models have
/// no upstream quota at all: tracking them produces empty meters, and — more importantly —
/// they must never be parked by exhaustion or cooldown logic, because nothing will ever
/// "reset".
/// </summary>
public static class ProviderMetering
{
	/// <summary>
	/// Model-provider segments that identify a locally or self-hosted runtime.
	/// OpenCode addresses models as "provider/model", so the segment before the first
	/// slash names the backend serving it.
	/// </summary>
	private static readonly HashSet<string> SelfHostedModelProviders = new(StringComparer.OrdinalIgnoreCase)
	{
		"ollama",
		"lmstudio",
		"lm-studio",
		"llamacpp",
		"llama.cpp",
		"vllm",
		"localai",
		"local",
		"jan",
		"koboldcpp",
		"text-generation-webui",
	};

	/// <summary>
	/// Returns the model-provider segment of an OpenCode-style "provider/model" identifier,
	/// or null when the identifier carries no provider segment.
	/// </summary>
	public static string? GetModelProviderSegment(string? modelId)
	{
		if (string.IsNullOrWhiteSpace(modelId))
		{
			return null;
		}

		var separatorIndex = modelId.IndexOf('/');
		return separatorIndex > 0
			? modelId[..separatorIndex].Trim()
			: null;
	}

	/// <summary>
	/// Whether a model is served by a self-hosted runtime and therefore incurs no
	/// upstream usage limits.
	/// </summary>
	public static bool IsSelfHostedModel(string? modelId)
	{
		var segment = GetModelProviderSegment(modelId);
		return segment != null && SelfHostedModelProviders.Contains(segment);
	}

	/// <summary>
	/// Whether usage for this provider and model should be metered against a quota.
	/// </summary>
	/// <remarks>
	/// Only returns false when the combination is known to be unmetered. An unrecognised
	/// model is treated as metered, so an unknown backend is never wrongly exempted from
	/// exhaustion handling.
	/// </remarks>
	public static bool IsMetered(ProviderType providerType, string? modelId)
	{
		if (IsSelfHostedModel(modelId))
		{
			return false;
		}

		// Copilot BYOK and OpenCode both proxy to whatever backend the user configured, so
		// the model identifier is the only reliable signal. Everything else is metered.
		return true;
	}

	/// <inheritdoc cref="IsMetered(ProviderType, string?)"/>
	public static bool IsMetered(Provider provider, string? modelId)
		=> IsMetered(provider.Type, modelId);

	/// <summary>
	/// Builds the limit state for a provider that has no upstream quota.
	/// </summary>
	public static UsageLimits CreateUnmeteredLimits(string? reason = null)
		=> new()
		{
			LimitType = UsageLimitType.Unmetered,
			IsLimitReached = false,
			Message = reason ?? "No usage limits. This model runs without an upstream quota.",
			Windows = []
		};

	/// <summary>
	/// Whether a limit type represents a provider that is known to have no quota,
	/// as opposed to one whose limits simply haven't been discovered yet.
	/// </summary>
	public static bool IsUnmetered(UsageLimitType limitType)
		=> limitType == UsageLimitType.Unmetered;
}
