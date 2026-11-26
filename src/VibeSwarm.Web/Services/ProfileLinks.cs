using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Services;

/// <summary>
/// Copies a linked profile's provider, model and reasoning into the project settings that point
/// at it. Every consumer (planning, review, the Lead, the execution plan) keeps reading the copied
/// values, so a profile only ever changes what a setting holds, never how it is read.
/// </summary>
public static class ProfileLinks
{
	/// <summary>
	/// Planning, review and the Lead run read-only, which only Claude and Copilot can do.
	/// </summary>
	public static bool CanRunReadOnly(Profile profile)
		=> profile.Provider != null && ProviderPlanningHelper.SupportsPlanningMode(profile.Provider.Type);

	/// <summary>
	/// Fills the project's planning, review and Lead settings from their linked profiles. A link
	/// to a missing profile, or to one whose provider can't fill the role, is dropped and the
	/// values it last copied stay.
	/// </summary>
	public static void ApplyRoleLinks(Project project, IReadOnlyDictionary<Guid, Profile> profiles)
	{
		if (project.PlanningProfileId is Guid planningId)
		{
			if (TryGetReadOnlyProfile(profiles, planningId) is { } profile)
			{
				project.PlanningProviderId = profile.ProviderId;
				project.PlanningModelId = profile.ModelId;
				project.PlanningReasoningEffort = profile.ReasoningEffort;
			}
			else
			{
				project.PlanningProfileId = null;
			}
		}

		if (project.ReviewProfileId is Guid reviewId)
		{
			if (TryGetReadOnlyProfile(profiles, reviewId) is { } profile)
			{
				project.ReviewProviderId = profile.ProviderId;
				project.ReviewInferenceProviderId = null;
				project.ReviewModelId = profile.ModelId;
				project.ReviewReasoningEffort = profile.ReasoningEffort;
			}
			else
			{
				project.ReviewProfileId = null;
			}
		}

		if (project.LeadProfileId is Guid leadId)
		{
			if (TryGetReadOnlyProfile(profiles, leadId) is { } profile)
			{
				project.LeadProviderId = profile.ProviderId;
				project.LeadModelId = profile.ModelId;
				project.LeadReasoningEffort = profile.ReasoningEffort;
			}
			else
			{
				project.LeadProfileId = null;
			}
		}
	}

	/// <summary>Fills a provider-priority entry from its linked profile, or drops a link to a missing one.</summary>
	public static void ApplyEntryLink(ProjectProvider entry, IReadOnlyDictionary<Guid, Profile> profiles)
	{
		if (entry.ProfileId is not Guid profileId)
		{
			return;
		}

		if (!profiles.TryGetValue(profileId, out var profile))
		{
			entry.ProfileId = null;
			return;
		}

		entry.ProviderId = profile.ProviderId;
		entry.PreferredModelId = profile.ModelId;
		entry.PreferredReasoningEffort = profile.ReasoningEffort;
	}

	/// <summary>Every profile id the project and its provider entries link to.</summary>
	public static HashSet<Guid> GetLinkedProfileIds(Project project)
	{
		var ids = new HashSet<Guid>();
		foreach (var id in new[] { project.PlanningProfileId, project.ReviewProfileId, project.LeadProfileId })
		{
			if (id is Guid value)
			{
				ids.Add(value);
			}
		}

		foreach (var entry in project.ProviderSelections ?? [])
		{
			if (entry.ProfileId is Guid value)
			{
				ids.Add(value);
			}
		}

		return ids;
	}

	private static Profile? TryGetReadOnlyProfile(IReadOnlyDictionary<Guid, Profile> profiles, Guid id)
		=> profiles.TryGetValue(id, out var profile) && CanRunReadOnly(profile) ? profile : null;
}
