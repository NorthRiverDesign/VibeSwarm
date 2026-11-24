namespace VibeSwarm.Shared.Data;

/// <summary>
/// How many jobs run at once. A project always runs one job at a time (members of one team
/// swarm excepted, each in a worktree of its own), so two sessions never share a checkout. These
/// limits set how many projects run side by side: in total
/// (<see cref="AppSettings.MaxConcurrentJobs"/>) and on each provider
/// (<see cref="Providers.Provider.MaxConcurrentJobs"/>). Only admins change them.
/// </summary>
public static class JobConcurrency
{
	public const int MinJobs = 1;
	public const int MaxJobs = 20;

	/// <summary>Jobs running at once across all projects.</summary>
	public const int DefaultTotalJobs = 5;

	/// <summary>
	/// Jobs one provider runs at once, each in a different project. As many as the total, so by
	/// default jobs in separate projects start side by side on one provider; lower it for a plan
	/// that can't take several sessions at once.
	/// </summary>
	public const int DefaultJobsPerProvider = DefaultTotalJobs;

	public static int Normalize(int jobs) => Math.Clamp(jobs, MinJobs, MaxJobs);
}
