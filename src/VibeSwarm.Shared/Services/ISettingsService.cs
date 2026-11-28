using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

public interface ISettingsService
{
	/// <summary>
	/// Gets the application settings. Creates default settings if none exist.
	/// </summary>
	Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Updates the application settings, except <see cref="AppSettings.MaxConcurrentJobs"/>, which
	/// only admins change through <see cref="UpdateMaxConcurrentJobsAsync"/>.
	/// </summary>
	Task<AppSettings> UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default);

	/// <summary>
	/// Sets how many jobs run at once across all projects, clamped to
	/// <see cref="JobConcurrency.MinJobs"/>–<see cref="JobConcurrency.MaxJobs"/>. Admins only.
	/// </summary>
	Task<AppSettings> UpdateMaxConcurrentJobsAsync(int maxConcurrentJobs, CancellationToken cancellationToken = default);

	/// <summary>
	/// Sets the team: the profile that leads and the profile that reviews, or null for a role
	/// the team goes without. Every project working as a team takes the change at once, except
	/// where it picked its own profile for the role.
	/// </summary>
	Task<AppSettings> UpdateTeamAsync(Guid? leadProfileId, Guid? reviewProfileId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets the default projects directory, if configured.
	/// </summary>
	Task<string?> GetDefaultProjectsDirectoryAsync(CancellationToken cancellationToken = default);
}
