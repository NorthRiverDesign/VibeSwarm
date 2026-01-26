using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

public interface ISettingsService
{
	/// <summary>
	/// Gets the application settings. Creates default settings if none exist.
	/// </summary>
	Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Updates the application settings.
	/// </summary>
	Task<AppSettings> UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets the default projects directory, if configured.
	/// </summary>
	Task<string?> GetDefaultProjectsDirectoryAsync(CancellationToken cancellationToken = default);
}
