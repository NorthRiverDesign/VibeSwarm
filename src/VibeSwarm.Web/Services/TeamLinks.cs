using VibeSwarm.Shared.Data;

namespace VibeSwarm.Web.Services;

/// <summary>
/// Copies the team set once in Settings into a project's own Lead and Review settings. Every
/// consumer (the job runner, the reviewer, the Lead) keeps reading the project, so the team only
/// ever changes what a project holds, never how it is read; <see cref="ProfileLinks"/> then
/// fills the provider, model and reasoning from the profile ids left here.
/// </summary>
public static class TeamLinks
{
	/// <summary>The profiles the team's roles run on; null leaves the team without that role.</summary>
	public readonly record struct Team(Guid? LeadProfileId, Guid? ReviewProfileId);

	public static Team From(AppSettings? settings) => new(settings?.TeamLeadProfileId, settings?.TeamReviewProfileId);

	/// <summary>
	/// Says which profile leads and which reviews on the project. With the team switched off the
	/// builder works alone: no Lead, no review, and nothing left overridden. With it on, a role
	/// takes the team's profile unless the project picked its own.
	/// </summary>
	public static void Apply(Project project, Team team)
	{
		if (!project.TeamEnabled)
		{
			project.TeamOverrides = TeamRoles.None;
			ClearLead(project);
			project.ReviewEnabled = false;
			return;
		}

		// An override that names no profile is not an override.
		if (project.TeamOverrides.HasFlag(TeamRoles.Lead) && !project.LeadProfileId.HasValue)
		{
			project.TeamOverrides &= ~TeamRoles.Lead;
		}

		if (project.TeamOverrides.HasFlag(TeamRoles.Review) && !project.ReviewProfileId.HasValue)
		{
			project.TeamOverrides &= ~TeamRoles.Review;
		}

		if (!project.TeamOverrides.HasFlag(TeamRoles.Lead))
		{
			if (team.LeadProfileId is Guid lead)
			{
				project.LeadProfileId = lead;
			}
			else
			{
				ClearLead(project);
			}
		}

		if (!project.TeamOverrides.HasFlag(TeamRoles.Review))
		{
			project.ReviewProfileId = team.ReviewProfileId;
		}

		project.ReviewEnabled = project.ReviewProfileId.HasValue;
	}

	// The Lead has no switch of its own: it is on whenever it has a provider.
	private static void ClearLead(Project project)
	{
		project.LeadProfileId = null;
		project.LeadProviderId = null;
		project.LeadModelId = null;
		project.LeadReasoningEffort = null;
	}
}
