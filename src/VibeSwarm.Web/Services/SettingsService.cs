using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Utilities;

namespace VibeSwarm.Shared.Services;

public class SettingsService : ISettingsService
{
	private readonly VibeSwarmDbContext _dbContext;

	public SettingsService(VibeSwarmDbContext dbContext)
	{
		_dbContext = dbContext;
	}

	public async Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
	{
		var settings = await _dbContext.AppSettings.OrderBy(s => s.Id).FirstOrDefaultAsync(cancellationToken);

		if (settings == null)
		{
			// Create default settings if none exist
			settings = new AppSettings
			{
				Id = Guid.NewGuid(),
				DefaultProjectsDirectory = null,
				TimeZoneId = DateTimeHelper.UtcTimeZoneId,
				UpdatedAt = DateTime.UtcNow
			};

			_dbContext.AppSettings.Add(settings);
			await _dbContext.SaveChangesAsync(cancellationToken);
		}
		else if (string.IsNullOrWhiteSpace(settings.TimeZoneId))
		{
			settings.TimeZoneId = DateTimeHelper.UtcTimeZoneId;
			settings.UpdatedAt ??= DateTime.UtcNow;
			await _dbContext.SaveChangesAsync(cancellationToken);
		}

		return settings;
	}

	public async Task<AppSettings> UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
	{
		var existing = await _dbContext.AppSettings.OrderBy(s => s.Id).FirstOrDefaultAsync(cancellationToken);

		if (existing == null)
		{
			// Create new settings
			settings.Id = Guid.NewGuid();
			settings.TimeZoneId = NormalizeTimeZoneId(settings.TimeZoneId);
			settings.UpdatedAt = DateTime.UtcNow;
			_dbContext.AppSettings.Add(settings);
		}
		else
		{
			// Update existing settings
			existing.DefaultProjectsDirectory = settings.DefaultProjectsDirectory;
			existing.TimeZoneId = NormalizeTimeZoneId(settings.TimeZoneId);
			existing.EnablePromptStructuring = settings.EnablePromptStructuring;
			existing.InjectRepoMap = settings.InjectRepoMap;
			existing.InjectEfficiencyRules = settings.InjectEfficiencyRules;
			existing.UpdatedAt = DateTime.UtcNow;
		}

		await _dbContext.SaveChangesAsync(cancellationToken);

		return existing ?? settings;
	}

	public async Task<string?> GetDefaultProjectsDirectoryAsync(CancellationToken cancellationToken = default)
	{
		var settings = await _dbContext.AppSettings.OrderBy(s => s.Id).FirstOrDefaultAsync(cancellationToken);
		return settings?.DefaultProjectsDirectory;
	}

	private static string NormalizeTimeZoneId(string? timeZoneId)
		=> DateTimeHelper.ResolveTimeZone(timeZoneId).Id;
}
