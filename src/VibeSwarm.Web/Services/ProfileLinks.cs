using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Services;

/// <summary>
/// Copies a linked profile's provider, model and reasoning into the settings that point at it:
/// a project's planning, review and Lead, its provider-priority entries and agent assignments,
/// and an agent's defaults. Every consumer keeps reading the copied values, so a profile only
/// ever changes what a setting holds, never how it is read. Only coding profiles fill these
/// slots, apart from review, which an inference provider can also do.
/// </summary>
public static class ProfileLinks
{
	/// <summary>
	/// Planning, review and the Lead run read-only, which only Claude and Copilot can do.
	/// </summary>
	public static bool CanRunReadOnly(Profile profile)
		=> profile.ProviderId.HasValue && profile.Provider != null && ProviderPlanningHelper.SupportsPlanningMode(profile.Provider.Type);

	/// <summary>
	/// Whether a running job can put questions to the profile: a Claude or Copilot profile reads
	/// the repository read-only, an inference profile sees only the question.
	/// </summary>
	public static bool CanHelpJobs(Profile profile)
		=> CanRunReadOnly(profile) || (profile.InferenceProviderId.HasValue && profile.InferenceProvider != null);

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
			else if (profiles.TryGetValue(reviewId, out var inference) && inference.InferenceProviderId is Guid inferenceProviderId)
			{
				// An inference reviewer reads the diff alone.
				project.ReviewProviderId = null;
				project.ReviewInferenceProviderId = inferenceProviderId;
				project.ReviewModelId = inference.ModelId;
				project.ReviewReasoningEffort = null;
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

	/// <summary>Drops a Decisions or Research link to a profile that is missing or can't help a job.</summary>
	public static void ApplyHelperLinks(Project project, IReadOnlyDictionary<Guid, Profile> profiles)
	{
		if (project.DecisionsProfileId is Guid decisionsId && !(profiles.TryGetValue(decisionsId, out var decisions) && CanHelpJobs(decisions)))
		{
			project.DecisionsProfileId = null;
		}

		if (project.ResearchProfileId is Guid researchId && !(profiles.TryGetValue(researchId, out var research) && CanHelpJobs(research)))
		{
			project.ResearchProfileId = null;
		}
	}

	/// <summary>Fills a provider-priority entry from its linked coding profile, or drops the link.</summary>
	public static void ApplyEntryLink(ProjectProvider entry, IReadOnlyDictionary<Guid, Profile> profiles)
	{
		if (entry.ProfileId is not Guid profileId)
		{
			return;
		}

		if (!profiles.TryGetValue(profileId, out var profile) || profile.ProviderId is not Guid providerId)
		{
			entry.ProfileId = null;
			return;
		}

		entry.ProviderId = providerId;
		entry.PreferredModelId = profile.ModelId;
		entry.PreferredReasoningEffort = profile.ReasoningEffort;
	}

	/// <summary>Fills an agent's default provider, model and reasoning from its linked coding profile, or drops the link.</summary>
	public static void ApplyAgentLink(Agent agent, IReadOnlyDictionary<Guid, Profile> profiles)
	{
		if (agent.ProfileId is not Guid profileId)
		{
			return;
		}

		if (!profiles.TryGetValue(profileId, out var profile) || profile.ProviderId is not Guid providerId)
		{
			agent.ProfileId = null;
			return;
		}

		agent.DefaultProviderId = providerId;
		agent.DefaultModelId = profile.ModelId;
		agent.DefaultReasoningEffort = profile.ReasoningEffort;
	}

	/// <summary>Fills a project's agent assignment from its linked coding profile, or drops the link.</summary>
	public static void ApplyAssignmentLink(ProjectAgent assignment, IReadOnlyDictionary<Guid, Profile> profiles)
	{
		if (assignment.ProfileId is not Guid profileId)
		{
			return;
		}

		if (!profiles.TryGetValue(profileId, out var profile) || profile.ProviderId is not Guid providerId)
		{
			assignment.ProfileId = null;
			return;
		}

		assignment.ProviderId = providerId;
		assignment.PreferredModelId = profile.ModelId;
		assignment.PreferredReasoningEffort = profile.ReasoningEffort;
	}

	/// <summary>Every profile id the project, its provider entries and its agent assignments link to.</summary>
	public static HashSet<Guid> GetLinkedProfileIds(Project project)
	{
		var ids = new HashSet<Guid>();
		foreach (var id in new[] { project.PlanningProfileId, project.ReviewProfileId, project.LeadProfileId, project.DecisionsProfileId, project.ResearchProfileId })
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

		foreach (var assignment in project.AgentAssignments ?? [])
		{
			if (assignment.ProfileId is Guid value)
			{
				ids.Add(value);
			}
		}

		return ids;
	}

	private static Profile? TryGetReadOnlyProfile(IReadOnlyDictionary<Guid, Profile> profiles, Guid id)
		=> profiles.TryGetValue(id, out var profile) && CanRunReadOnly(profile) ? profile : null;
}
