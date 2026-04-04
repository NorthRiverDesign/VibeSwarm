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
				EnableCommitAttribution = true,
				CriticalErrorLogRetentionDays = AppSettings.DefaultCriticalErrorLogRetentionDays,
				CriticalErrorLogMaxEntries = AppSettings.DefaultCriticalErrorLogMaxEntries,
				IdeaExpansionPromptTemplate = PromptBuilder.DefaultIdeaExpansionPromptTemplate,
				IdeaImplementationPromptTemplate = PromptBuilder.DefaultIdeaImplementationPromptTemplate,
				ApprovedIdeaImplementationPromptTemplate = PromptBuilder.DefaultApprovedIdeaImplementationPromptTemplate,
				UpdatedAt = DateTime.UtcNow
			};

			_dbContext.AppSettings.Add(settings);
			await _dbContext.SaveChangesAsync(cancellationToken);
		}
		else
		{
			var normalizedTimeZoneId = NormalizeTimeZoneId(settings.TimeZoneId);
			var normalizedRetentionDays = NormalizeCriticalErrorLogRetentionDays(settings.CriticalErrorLogRetentionDays);
			var normalizedMaxEntries = NormalizeCriticalErrorLogMaxEntries(settings.CriticalErrorLogMaxEntries);
			var normalizedIdeaExpansionPromptTemplate = NormalizePromptTemplate(settings.IdeaExpansionPromptTemplate, PromptBuilder.DefaultIdeaExpansionPromptTemplate);
			var normalizedIdeaImplementationPromptTemplate = NormalizePromptTemplate(settings.IdeaImplementationPromptTemplate, PromptBuilder.DefaultIdeaImplementationPromptTemplate);
			var normalizedApprovedIdeaImplementationPromptTemplate = NormalizePromptTemplate(settings.ApprovedIdeaImplementationPromptTemplate, PromptBuilder.DefaultApprovedIdeaImplementationPromptTemplate);

			if (!string.Equals(settings.TimeZoneId, normalizedTimeZoneId, StringComparison.Ordinal) ||
				settings.CriticalErrorLogRetentionDays != normalizedRetentionDays ||
				settings.CriticalErrorLogMaxEntries != normalizedMaxEntries ||
				!string.Equals(settings.IdeaExpansionPromptTemplate, normalizedIdeaExpansionPromptTemplate, StringComparison.Ordinal) ||
				!string.Equals(settings.IdeaImplementationPromptTemplate, normalizedIdeaImplementationPromptTemplate, StringComparison.Ordinal) ||
				!string.Equals(settings.ApprovedIdeaImplementationPromptTemplate, normalizedApprovedIdeaImplementationPromptTemplate, StringComparison.Ordinal))
			{
				settings.TimeZoneId = normalizedTimeZoneId;
				settings.CriticalErrorLogRetentionDays = normalizedRetentionDays;
				settings.CriticalErrorLogMaxEntries = normalizedMaxEntries;
				settings.IdeaExpansionPromptTemplate = normalizedIdeaExpansionPromptTemplate;
				settings.IdeaImplementationPromptTemplate = normalizedIdeaImplementationPromptTemplate;
				settings.ApprovedIdeaImplementationPromptTemplate = normalizedApprovedIdeaImplementationPromptTemplate;
				settings.UpdatedAt ??= DateTime.UtcNow;
				await _dbContext.SaveChangesAsync(cancellationToken);
			}
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
			settings.CriticalErrorLogRetentionDays = NormalizeCriticalErrorLogRetentionDays(settings.CriticalErrorLogRetentionDays);
			settings.CriticalErrorLogMaxEntries = NormalizeCriticalErrorLogMaxEntries(settings.CriticalErrorLogMaxEntries);
			settings.IdeaExpansionPromptTemplate = NormalizePromptTemplate(settings.IdeaExpansionPromptTemplate, PromptBuilder.DefaultIdeaExpansionPromptTemplate);
			settings.IdeaImplementationPromptTemplate = NormalizePromptTemplate(settings.IdeaImplementationPromptTemplate, PromptBuilder.DefaultIdeaImplementationPromptTemplate);
			settings.ApprovedIdeaImplementationPromptTemplate = NormalizePromptTemplate(settings.ApprovedIdeaImplementationPromptTemplate, PromptBuilder.DefaultApprovedIdeaImplementationPromptTemplate);
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
			existing.EnableCommitAttribution = settings.EnableCommitAttribution;
			existing.CriticalErrorLogRetentionDays = NormalizeCriticalErrorLogRetentionDays(settings.CriticalErrorLogRetentionDays);
			existing.CriticalErrorLogMaxEntries = NormalizeCriticalErrorLogMaxEntries(settings.CriticalErrorLogMaxEntries);
			existing.IdeaExpansionPromptTemplate = NormalizePromptTemplate(settings.IdeaExpansionPromptTemplate, PromptBuilder.DefaultIdeaExpansionPromptTemplate);
			existing.IdeaImplementationPromptTemplate = NormalizePromptTemplate(settings.IdeaImplementationPromptTemplate, PromptBuilder.DefaultIdeaImplementationPromptTemplate);
			existing.ApprovedIdeaImplementationPromptTemplate = NormalizePromptTemplate(settings.ApprovedIdeaImplementationPromptTemplate, PromptBuilder.DefaultApprovedIdeaImplementationPromptTemplate);
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

	private static int NormalizeCriticalErrorLogRetentionDays(int retentionDays)
		=> Math.Clamp(
			retentionDays,
			AppSettings.MinCriticalErrorLogRetentionDays,
			AppSettings.MaxCriticalErrorLogRetentionDays);

	private static int NormalizeCriticalErrorLogMaxEntries(int maxEntries)
		=> Math.Clamp(
			maxEntries,
			AppSettings.MinCriticalErrorLogMaxEntries,
			AppSettings.MaxCriticalErrorLogMaxEntries);

	private static string NormalizePromptTemplate(string? template, string defaultTemplate)
		=> string.IsNullOrWhiteSpace(template)
			? defaultTemplate
			: template.Trim().ReplaceLineEndings("\n");
}
