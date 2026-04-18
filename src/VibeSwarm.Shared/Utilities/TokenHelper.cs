namespace VibeSwarm.Shared.Utilities;

public static class TokenHelper
{
	private const double CharsPerToken = 4.0;

	public static int EstimateTokenCount(string? text)
		=> string.IsNullOrEmpty(text) ? 0 : (int)Math.Ceiling(text.Length / CharsPerToken);

	public static string FormatTokenCount(int tokenCount) => tokenCount switch
	{
		>= 1_000_000 => $"{tokenCount / 1_000_000.0:F1}M",
		>= 1_000 => $"{tokenCount / 1_000.0:F1}K",
		_ => tokenCount.ToString()
	};

	public static string FormatCost(decimal cost) => cost switch
	{
		>= 1.00m => $"${cost:F2}",
		>= 0.01m => $"${cost:F3}",
		> 0 => $"${cost:F4}",
		_ => "$0.00"
	};

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

	public static string FormatDuration(double? seconds)
		=> seconds.HasValue ? FormatDuration(TimeSpan.FromSeconds(seconds.Value)) : "-";
}
