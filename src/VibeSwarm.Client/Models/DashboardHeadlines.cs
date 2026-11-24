using VibeSwarm.Client.Components.Providers;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Client.Models;

/// <summary>The figures in the dashboard's number tiles and status line.</summary>
public static class DashboardHeadlines
{
	/// <summary>The fullest usage window across the providers shown, or null when none reports a limit.</summary>
	public sealed record UsageHeadline(string ProviderName, string WindowLabel, int PercentUsed, DateTime? ResetTime, bool IsLimitReached);

	/// <summary>
	/// Completed jobs as a share of the jobs that finished, or null when none did. It rounds down
	/// while any job failed, so a failure never shows as 100%.
	/// </summary>
	public static int? SuccessRatePercent(DashboardJobMetrics metrics)
	{
		var finished = metrics.TotalCompletedJobs + metrics.TotalFailedJobs;
		if (finished == 0)
		{
			return null;
		}

		return metrics.TotalFailedJobs == 0
			? 100
			: (int)Math.Floor(100.0 * metrics.TotalCompletedJobs / finished);
	}

	public static UsageHeadline? BusiestLimit(IEnumerable<(string Name, ProviderUsageSummary? Summary)> providers, DateTime utcNow)
	{
		UsageHeadline? busiest = null;

		foreach (var (name, summary) in providers)
		{
			foreach (var window in UsageMeter.GetDisplayWindows(summary, utcNow))
			{
				if (window.PercentUsed is not int percent)
				{
					continue;
				}

				var candidate = new UsageHeadline(
					name,
					window.Label ?? UsageMeter.GetScopeLabel(window.Scope),
					Math.Clamp(percent, 0, 100),
					window.ResetTime,
					window.IsLimitReached);

				if (busiest is null ||
					(candidate.IsLimitReached && !busiest.IsLimitReached) ||
					(candidate.IsLimitReached == busiest.IsLimitReached && candidate.PercentUsed > busiest.PercentUsed))
				{
					busiest = candidate;
				}
			}
		}

		return busiest;
	}

	/// <summary>"6m", "5h" or "3d": the time left, short enough for a tile's one line.</summary>
	public static string FormatShortSpan(TimeSpan span)
	{
		if (span.TotalHours < 1)
		{
			return $"{Math.Max(1, (int)Math.Ceiling(span.TotalMinutes))}m";
		}

		return span.TotalHours < 48
			? $"{(int)span.TotalHours}h"
			: $"{(int)span.TotalDays}d";
	}
}
