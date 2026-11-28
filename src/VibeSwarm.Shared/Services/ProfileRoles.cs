using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// Which roles a profile can fill. Pickers offer by these rules and the server checks by them, so
/// the two never disagree. Each rule reads the profile's loaded provider.
/// </summary>
public static class ProfileRoles
{
	/// <summary>Whether the profile can run a job: it is on a coding provider that is turned on.</summary>
	public static bool CanBuild(Profile profile)
		=> profile.ProviderId.HasValue && profile.Provider is { IsEnabled: true };

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
}
