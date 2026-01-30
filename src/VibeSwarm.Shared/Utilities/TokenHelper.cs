namespace VibeSwarm.Shared.Utilities;

/// <summary>
/// Helper class for estimating token counts in text strings.
/// Used by both the worker and web UI to calculate costs associated with jobs and prompts.
/// </summary>
/// <remarks>
/// Token counts are estimates based on common tokenization patterns.
/// For precise counts, use the actual tokenizer for your target model.
/// These estimates are generally accurate within 10-15% for English text.
/// </remarks>
public static class TokenHelper
{
	/// <summary>
	/// Average characters per token for typical English text.
	/// Most LLM tokenizers average around 4 characters per token.
	/// </summary>
	private const double CharsPerToken = 4.0;

	/// <summary>
	/// Average characters per token for code content.
	/// Code typically has shorter tokens due to symbols and keywords.
	/// </summary>
	private const double CharsPerTokenCode = 3.5;

	/// <summary>
	/// Estimates the number of tokens in a text string.
	/// </summary>
	/// <param name="text">The text to count tokens for.</param>
	/// <returns>Estimated token count, or 0 if text is null or empty.</returns>
	public static int EstimateTokenCount(string? text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return 0;
		}

		// Use character-based estimation
		// This is a reasonable approximation for most LLM tokenizers
		return (int)Math.Ceiling(text.Length / CharsPerToken);
	}

	/// <summary>
	/// Estimates the number of tokens in code content.
	/// Code typically tokenizes differently than natural language.
	/// </summary>
	/// <param name="code">The code text to count tokens for.</param>
	/// <returns>Estimated token count, or 0 if code is null or empty.</returns>
	public static int EstimateCodeTokenCount(string? code)
	{
		if (string.IsNullOrEmpty(code))
		{
			return 0;
		}

		// Code has more symbols and shorter identifiers on average
		return (int)Math.Ceiling(code.Length / CharsPerTokenCode);
	}

	/// <summary>
	/// Estimates the total token count for multiple text segments.
	/// </summary>
	/// <param name="texts">Collection of text strings to count.</param>
	/// <returns>Total estimated token count across all texts.</returns>
	public static int EstimateTokenCount(IEnumerable<string?> texts)
	{
		if (texts == null)
		{
			return 0;
		}

		return texts.Sum(EstimateTokenCount);
	}

	/// <summary>
	/// Estimates the cost in USD for a given token count at a specified rate.
	/// </summary>
	/// <param name="tokenCount">Number of tokens.</param>
	/// <param name="costPerMillionTokens">Cost per million tokens in USD.</param>
	/// <returns>Estimated cost in USD.</returns>
	public static decimal EstimateCost(int tokenCount, decimal costPerMillionTokens)
	{
		if (tokenCount <= 0 || costPerMillionTokens <= 0)
		{
			return 0m;
		}

		return (tokenCount / 1_000_000m) * costPerMillionTokens;
	}

	/// <summary>
	/// Estimates the total cost for input and output tokens with separate rates.
	/// </summary>
	/// <param name="inputTokens">Number of input tokens.</param>
	/// <param name="outputTokens">Number of output tokens.</param>
	/// <param name="inputCostPerMillion">Cost per million input tokens in USD.</param>
	/// <param name="outputCostPerMillion">Cost per million output tokens in USD.</param>
	/// <returns>Total estimated cost in USD.</returns>
	public static decimal EstimateTotalCost(
		int inputTokens,
		int outputTokens,
		decimal inputCostPerMillion,
		decimal outputCostPerMillion)
	{
		return EstimateCost(inputTokens, inputCostPerMillion) +
			   EstimateCost(outputTokens, outputCostPerMillion);
	}

	/// <summary>
	/// Checks if the text would exceed a token limit.
	/// </summary>
	/// <param name="text">The text to check.</param>
	/// <param name="maxTokens">Maximum allowed tokens.</param>
	/// <returns>True if estimated tokens exceed the limit.</returns>
	public static bool ExceedsTokenLimit(string? text, int maxTokens)
	{
		return EstimateTokenCount(text) > maxTokens;
	}

	/// <summary>
	/// Gets the remaining tokens available given current usage and a limit.
	/// </summary>
	/// <param name="currentTokens">Current token count used.</param>
	/// <param name="maxTokens">Maximum token limit.</param>
	/// <returns>Remaining tokens, or 0 if limit is exceeded.</returns>
	public static int GetRemainingTokens(int currentTokens, int maxTokens)
	{
		var remaining = maxTokens - currentTokens;
		return remaining > 0 ? remaining : 0;
	}

	/// <summary>
	/// Formats a token count for display (e.g., "1.5K", "2.3M").
	/// </summary>
	/// <param name="tokenCount">The token count to format.</param>
	/// <returns>Formatted string representation.</returns>
	public static string FormatTokenCount(int tokenCount)
	{
		return tokenCount switch
		{
			>= 1_000_000 => $"{tokenCount / 1_000_000.0:F1}M",
			>= 1_000 => $"{tokenCount / 1_000.0:F1}K",
			_ => tokenCount.ToString()
		};
	}

	/// <summary>
	/// Formats a cost value for display with appropriate precision.
	/// </summary>
	/// <param name="cost">The cost in USD.</param>
	/// <returns>Formatted cost string with dollar sign.</returns>
	public static string FormatCost(decimal cost)
	{
		return cost switch
		{
			>= 1.00m => $"${cost:F2}",
			>= 0.01m => $"${cost:F3}",
			> 0 => $"${cost:F4}",
			_ => "$0.00"
		};
	}

	/// <summary>
	/// Formats a duration for display in a human-readable format.
	/// </summary>
	/// <param name="duration">The duration to format.</param>
	/// <returns>Formatted duration string (e.g., "2m 30s", "1h 15m").</returns>
	public static string FormatDuration(TimeSpan? duration)
	{
		if (!duration.HasValue)
			return "-";

		var d = duration.Value;

		if (d.TotalDays >= 1)
			return $"{(int)d.TotalDays}d {d.Hours}h";
		if (d.TotalHours >= 1)
			return $"{(int)d.TotalHours}h {d.Minutes}m";
		if (d.TotalMinutes >= 1)
			return $"{(int)d.TotalMinutes}m {d.Seconds}s";
		if (d.TotalSeconds >= 1)
			return $"{d.TotalSeconds:F1}s";
		return $"{d.TotalMilliseconds:F0}ms";
	}

	/// <summary>
	/// Formats a duration in seconds for display.
	/// </summary>
	/// <param name="seconds">The duration in seconds.</param>
	/// <returns>Formatted duration string.</returns>
	public static string FormatDuration(double? seconds)
	{
		if (!seconds.HasValue)
			return "-";
		return FormatDuration(TimeSpan.FromSeconds(seconds.Value));
	}
}
