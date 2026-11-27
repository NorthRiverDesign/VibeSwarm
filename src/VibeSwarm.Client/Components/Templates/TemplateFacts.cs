using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Utilities;

namespace VibeSwarm.Client.Components.Templates;

/// <summary>Plain-language summaries of a template for list rows and pickers.</summary>
public static class TemplateFacts
{
	/// <summary>What the template sets instead of each project's own settings, or null when it sets nothing.</summary>
	public static string? Describe(JobTemplate template)
	{
		var parts = new List<string>();
		if (template.ProfileId.HasValue || template.ProviderId.HasValue)
		{
			// The template's profile goes first; the job still falls back to the project's others if it fails.
			parts.Add($"{template.ProfileName ?? template.Provider?.Name ?? "a fixed profile"} first");
		}
		if (template.CycleMode == CycleMode.FixedCount)
		{
			parts.Add($"{template.MaxCycles} passes");
		}
		else if (template.CycleMode == CycleMode.Autonomous)
		{
			parts.Add($"up to {template.MaxCycles} passes");
		}
		if (!string.IsNullOrWhiteSpace(template.Branch))
		{
			parts.Add($"branch {template.Branch}");
		}
		if (template.GitChangeDeliveryMode is { } mode)
		{
			parts.Add(mode == GitChangeDeliveryMode.PullRequest
				? string.IsNullOrWhiteSpace(template.TargetBranch) ? "pull request" : $"pull request into {template.TargetBranch}"
				: "commits to the branch");
		}

		return parts.Count == 0 ? null : "Uses " + string.Join(", ", parts);
	}

	/// <summary>The second line of a template's row: where it last ran, else its note.</summary>
	public static string RunSummary(JobTemplate template)
	{
		if (template.LastRunAt is { } lastRun && template.ProjectRunCount > 0)
		{
			var projects = template.ProjectRunCount == 1 ? "1 project" : $"{template.ProjectRunCount} projects";
			return $"Ran on {projects} · {lastRun.FormatRelativeToNow()}";
		}

		return string.IsNullOrWhiteSpace(template.Description) ? "Not run yet" : template.Description;
	}

	/// <summary>A job's status as the run history states it.</summary>
	public static string StatusLabel(JobStatus status) => status switch
	{
		JobStatus.Completed => "Done",
		JobStatus.Failed => "Failed",
		JobStatus.Cancelled => "Cancelled",
		JobStatus.New or JobStatus.Pending => "Queued",
		JobStatus.Paused => "Waiting for your reply",
		JobStatus.Stalled => "Stalled",
		JobStatus.Planning => "Planning",
		_ => "Running"
	};
}
