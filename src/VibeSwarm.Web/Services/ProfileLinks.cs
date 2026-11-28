using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Services;

/// <summary>
/// Copies a linked profile's provider, model and reasoning into the settings that point at it:
/// a project's planning, review and Lead, its provider-priority entries and a job template. Every consumer keeps reading the copied values, so a
/// profile only ever changes what a setting holds, never how it is read. Only coding profiles
/// fill these slots, apart from review, which an inference provider can also do. Which profile
/// may fill which role is decided by <see cref="ProfileRoles"/>.
/// </summary>
public static class ProfileLinks
{
	/// <summary>
	/// What a job run on the profile gets. A model or reasoning level the profile leaves open
	/// falls to its provider's default, so the answer never depends on who asks. The profile's
	/// provider must be loaded.
	/// </summary>
	public static (Guid ProviderId, string? ModelId, string? ReasoningEffort) ResolveForJob(Profile profile, string? providerDefaultModelId)
	{
		var provider = profile.Provider ?? throw new InvalidOperationException("The profile's provider is not loaded.");
		return (
			provider.Id,
			string.IsNullOrWhiteSpace(profile.ModelId) ? providerDefaultModelId : profile.ModelId,
			ProviderCapabilities.NormalizeReasoningEffort(profile.ReasoningEffort ?? provider.DefaultReasoningEffort));
	}

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
		if (project.DecisionsProfileId is Guid decisionsId && !(profiles.TryGetValue(decisionsId, out var decisions) && ProfileRoles.CanHelpJobs(decisions)))
		{
			project.DecisionsProfileId = null;
		}

		if (project.ResearchProfileId is Guid researchId && !(profiles.TryGetValue(researchId, out var research) && ProfileRoles.CanHelpJobs(research)))
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

	/// <summary>Fills a job template's provider, model and reasoning from its linked coding profile, or drops the link.</summary>
	public static void ApplyTemplateLink(JobTemplate template, IReadOnlyDictionary<Guid, Profile> profiles)
	{
		if (template.ProfileId is not Guid profileId)
		{
			return;
		}

		if (!profiles.TryGetValue(profileId, out var profile) || profile.ProviderId is not Guid providerId)
		{
			template.ProfileId = null;
			return;
		}

		template.ProviderId = providerId;
		template.ModelId = profile.ModelId;
		template.ReasoningEffort = profile.ReasoningEffort;
	}

	/// <summary>
	/// What a schedule, an auto-pilot loop or the ideas queue keeps beside its profile's id: the
	/// provider and model, which their own scheduling and usage-limit checks read. Null when the
	/// profile is missing or only answers questions, which drops the link.
	/// </summary>
	public static (Guid ProviderId, string? ModelId)? RunnerValues(Guid? profileId, IReadOnlyDictionary<Guid, Profile> profiles)
		=> profileId is Guid id && profiles.TryGetValue(id, out var profile) && profile.ProviderId is Guid providerId
			? (providerId, profile.ModelId)
			: null;

	/// <summary>
	/// The same for a setting that only asks questions: an inference profile's provider and model.
	/// </summary>
	public static (Guid InferenceProviderId, string? ModelId)? InferenceValues(Guid? profileId, IReadOnlyDictionary<Guid, Profile> profiles)
		=> profileId is Guid id && profiles.TryGetValue(id, out var profile) && profile.InferenceProviderId is Guid inferenceProviderId
			? (inferenceProviderId, profile.ModelId)
			: null;

	/// <summary>
	/// Fills a schedule from its linked profile, or drops the link: a job schedule takes a coding
	/// profile's provider and model, an idea schedule an inference profile's.
	/// </summary>
	public static void ApplyScheduleLink(JobSchedule schedule, IReadOnlyDictionary<Guid, Profile> profiles)
	{
		if (!schedule.ProfileId.HasValue)
		{
			return;
		}

		if (schedule.ScheduleType == JobScheduleType.GenerateIdeas)
		{
			if (InferenceValues(schedule.ProfileId, profiles) is not { } ideaValues)
			{
				schedule.ProfileId = null;
				return;
			}

			(schedule.InferenceProviderId, schedule.ModelId) = ideaValues;
			return;
		}

		if (RunnerValues(schedule.ProfileId, profiles) is not { } values)
		{
			schedule.ProfileId = null;
			return;
		}

		(schedule.ProviderId, schedule.ModelId) = values;
	}

	/// <summary>Fills the project's idea provider and commit message writer from their linked inference profiles, or drops the links.</summary>
	public static void ApplyInferenceRoleLinks(Project project, IReadOnlyDictionary<Guid, Profile> profiles)
	{
		if (project.IdeasProfileId.HasValue)
		{
			if (InferenceValues(project.IdeasProfileId, profiles) is { } ideas)
			{
				(project.IdeaInferenceProviderId, project.IdeaInferenceModelId) = ideas;
			}
			else
			{
				project.IdeasProfileId = null;
			}
		}

		if (project.CommitSummaryProfileId.HasValue)
		{
			if (InferenceValues(project.CommitSummaryProfileId, profiles) is { } summary)
			{
				(project.CommitSummaryInferenceProviderId, project.CommitSummaryInferenceModelId) = summary;
			}
			else
			{
				project.CommitSummaryProfileId = null;
			}
		}
	}

	/// <summary>Fills an auto-pilot loop's idea provider and model from its linked inference profile, or drops the link.</summary>
	public static void ApplyLoopIdeasLink(IterationLoop loop, IReadOnlyDictionary<Guid, Profile> profiles)
	{
		if (!loop.IdeasProfileId.HasValue)
		{
			return;
		}

		if (InferenceValues(loop.IdeasProfileId, profiles) is not { } values)
		{
			loop.IdeasProfileId = null;
			return;
		}

		(loop.InferenceProviderId, loop.InferenceModelId) = values;
	}

	/// <summary>Fills an auto-pilot loop's provider and model from its linked coding profile, or drops the link.</summary>
	public static void ApplyLoopLink(IterationLoop loop, IReadOnlyDictionary<Guid, Profile> profiles)
	{
		if (!loop.ProfileId.HasValue)
		{
			return;
		}

		if (RunnerValues(loop.ProfileId, profiles) is not { } values)
		{
			loop.ProfileId = null;
			return;
		}

		(loop.ProviderId, loop.ModelId) = values;
	}

	/// <summary>Fills the ideas queue's provider and model from its linked coding profile, or drops the link.</summary>
	public static void ApplyIdeasQueueLink(Project project, IReadOnlyDictionary<Guid, Profile> profiles)
	{
		if (!project.IdeasProcessingProfileId.HasValue)
		{
			return;
		}

		if (RunnerValues(project.IdeasProcessingProfileId, profiles) is not { } values)
		{
			project.IdeasProcessingProfileId = null;
			return;
		}

		(project.IdeasProcessingProviderId, project.IdeasProcessingModelId) = values;
	}

	/// <summary>Every profile id the project and its provider entries link to.</summary>
	public static HashSet<Guid> GetLinkedProfileIds(Project project)
	{
		var ids = new HashSet<Guid>();
		foreach (var id in new[] { project.PlanningProfileId, project.ReviewProfileId, project.LeadProfileId, project.DecisionsProfileId, project.ResearchProfileId, project.IdeasProfileId, project.CommitSummaryProfileId })
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
		=> profiles.TryGetValue(id, out var profile) && ProfileRoles.CanRunReadOnly(profile) ? profile : null;
}
