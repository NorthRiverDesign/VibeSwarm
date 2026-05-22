using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Utilities;

namespace VibeSwarm.Shared.Services;

public class SettingsService : ISettingsService
{
	private readonly VibeSwarmDbContext _dbContext;
	private const string LegacyDefaultIdeaExpansionPromptTemplate =
		"""
		You are a staff-level software engineer turning a rough product idea into an implementation-ready specification.

		## Feature Idea
		{{idea}}

		## Instructions
		1. Explore the codebase, adjacent workflows, reusable components, and tests before deciding on the solution. Use subagents when they help you investigate in parallel.
		2. Make pragmatic assumptions from repository patterns and choose the option that best fits the current system.
		3. Return concise markdown with these sections: Overview, User Flows, Affected Areas, Implementation Plan, Edge Cases, Acceptance Criteria.
		4. Keep it concrete and brief. No code samples. Do not mention providers, models, or attribution.
		""";
	private const string LegacyDefaultIdeaImplementationPromptTemplate =
		"""
		You are a staff-level software engineer implementing a feature directly from a product idea.

		## Feature Idea
		{{idea}}

		## Instructions
		1. Explore the codebase, adjacent flows, tests, and reusable components before editing. Use subagents when they will speed up research or parallel analysis.
		2. Work in a tight inspect -> plan -> implement -> verify loop. Keep the plan lightweight and update it as you learn.
		3. Prefer the simplest solution that fully satisfies the idea. Reuse existing patterns, helpers, and components before introducing new ones.
		4. Make pragmatic assumptions from repository patterns and choose the option that best fits the current system.
		5. Deliver the feature end-to-end with the needed UX, validation, persistence, error handling, and tests. Fix the root cause, not just the first visible symptom.
		6. Operate like an autonomous CI coding job: complete the requested work, run the relevant verification, and leave the repository in a working state before finishing.
		7. Keep changes scoped to the request, handle edge cases, and preserve existing behavior unless the idea requires a change.
		8. Do not mention or attribute the work to any provider, model, or CLI tool.

		Implement this feature now without first writing a separate specification or stopping at a plan-only response.

		When you are finished, end your response with a short summary in this exact format:
		<commit-summary>
		A concise one-line description of what was implemented (max 72 chars)
		</commit-summary>
		""";
	private const string LegacyDefaultApprovedIdeaImplementationPromptTemplate =
		"""
		You are a staff-level software engineer implementing an approved specification.

		## Original Idea
		{{idea}}

		## Detailed Specification
		{{specification}}

		## Instructions
		1. Explore the codebase, adjacent flows, tests, and reusable components before editing. Use subagents when they will speed up research or parallel analysis.
		2. Use the approved specification as the source of truth, then fill in missing details from repository patterns.
		3. Work in a tight inspect -> plan -> implement -> verify loop. Keep the plan lightweight and update it as you learn.
		4. Prefer the simplest solution that fully satisfies the specification. Reuse existing patterns, helpers, and components before introducing new ones.
		5. Deliver the feature end-to-end with the needed UX, validation, persistence, error handling, and tests. Fix the root cause, not just the first visible symptom.
		6. Operate like an autonomous CI coding job: complete the requested work, run the relevant verification, and leave the repository in a working state before finishing.
		7. Keep changes scoped, handle edge cases, and preserve existing behavior unless the specification requires a change.
		8. Do not mention or attribute the work to any provider, model, or CLI tool.

		Implement this feature now.

		When you are finished, end your response with a short summary in this exact format:
		<commit-summary>
		A concise one-line description of what was implemented (max 72 chars)
		</commit-summary>
		""";
	private const string PreviousDefaultIdeaImplementationPromptTemplate =
		"""
		You are a staff-level software engineer implementing a feature directly from a product idea.

		## Feature Idea
		{{idea}}

		## Instructions
		1. Inspect the codebase, related flows, reusable components, and tests before editing. Use subagents when they help.
		2. Fill in missing details from repository patterns and prefer the simplest solution that fully satisfies the idea.
		3. Reuse existing patterns, helpers, and components before adding new ones.
		4. Implement the feature end-to-end with the needed UX, validation, persistence, error handling, and tests.
		5. Keep changes scoped, preserve existing behavior unless the idea requires a change, and leave the repository in a working state.
		6. Do not mention or attribute the work to any provider, model, or CLI tool.

		Implement this feature now without first writing a separate specification.

		When you are finished, end your response with a short summary in this exact format:
		<commit-summary>
		A concise one-line description of what was implemented (max 72 chars)
		</commit-summary>
		""";
	private const string PreviousDefaultApprovedIdeaImplementationPromptTemplate =
		"""
		You are a staff-level software engineer implementing an approved specification.

		## Original Idea
		{{idea}}

		## Detailed Specification
		{{specification}}

		## Instructions
		1. Use the approved specification as the source of truth, then fill in missing details from repository patterns.
		2. Inspect the codebase, related flows, reusable components, and tests before editing. Use subagents when they help.
		3. Reuse existing patterns, helpers, and components before adding new ones.
		4. Implement the feature end-to-end with the needed UX, validation, persistence, error handling, and tests.
		5. Keep changes scoped, preserve existing behavior unless the specification requires a change, and leave the repository in a working state.
		6. Do not mention or attribute the work to any provider, model, or CLI tool.

		Implement this feature now.

		When you are finished, end your response with a short summary in this exact format:
		<commit-summary>
		A concise one-line description of what was implemented (max 72 chars)
		</commit-summary>
		""";

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
			var normalizedIdeaExpansionPromptTemplate = NormalizePromptTemplate(
				settings.IdeaExpansionPromptTemplate,
				PromptBuilder.DefaultIdeaExpansionPromptTemplate,
				LegacyDefaultIdeaExpansionPromptTemplate);
			var normalizedIdeaImplementationPromptTemplate = NormalizePromptTemplate(
				settings.IdeaImplementationPromptTemplate,
				PromptBuilder.DefaultIdeaImplementationPromptTemplate,
				LegacyDefaultIdeaImplementationPromptTemplate,
				PreviousDefaultIdeaImplementationPromptTemplate);
			var normalizedApprovedIdeaImplementationPromptTemplate = NormalizePromptTemplate(
				settings.ApprovedIdeaImplementationPromptTemplate,
				PromptBuilder.DefaultApprovedIdeaImplementationPromptTemplate,
				LegacyDefaultApprovedIdeaImplementationPromptTemplate,
				PreviousDefaultApprovedIdeaImplementationPromptTemplate);

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
			settings.IdeaExpansionPromptTemplate = NormalizePromptTemplate(
				settings.IdeaExpansionPromptTemplate,
				PromptBuilder.DefaultIdeaExpansionPromptTemplate,
				LegacyDefaultIdeaExpansionPromptTemplate);
			settings.IdeaImplementationPromptTemplate = NormalizePromptTemplate(
				settings.IdeaImplementationPromptTemplate,
				PromptBuilder.DefaultIdeaImplementationPromptTemplate,
				LegacyDefaultIdeaImplementationPromptTemplate,
				PreviousDefaultIdeaImplementationPromptTemplate);
			settings.ApprovedIdeaImplementationPromptTemplate = NormalizePromptTemplate(
				settings.ApprovedIdeaImplementationPromptTemplate,
				PromptBuilder.DefaultApprovedIdeaImplementationPromptTemplate,
				LegacyDefaultApprovedIdeaImplementationPromptTemplate,
				PreviousDefaultApprovedIdeaImplementationPromptTemplate);
			settings.GitHubToken = NormalizeGitHubToken(settings.GitHubToken);
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
			existing.IdeaExpansionPromptTemplate = NormalizePromptTemplate(
				settings.IdeaExpansionPromptTemplate,
				PromptBuilder.DefaultIdeaExpansionPromptTemplate,
				LegacyDefaultIdeaExpansionPromptTemplate);
			existing.IdeaImplementationPromptTemplate = NormalizePromptTemplate(
				settings.IdeaImplementationPromptTemplate,
				PromptBuilder.DefaultIdeaImplementationPromptTemplate,
				LegacyDefaultIdeaImplementationPromptTemplate,
				PreviousDefaultIdeaImplementationPromptTemplate);
			existing.ApprovedIdeaImplementationPromptTemplate = NormalizePromptTemplate(
				settings.ApprovedIdeaImplementationPromptTemplate,
				PromptBuilder.DefaultApprovedIdeaImplementationPromptTemplate,
				LegacyDefaultApprovedIdeaImplementationPromptTemplate,
				PreviousDefaultApprovedIdeaImplementationPromptTemplate);
			existing.GitHubToken = NormalizeGitHubToken(settings.GitHubToken);
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

	private static string? NormalizeGitHubToken(string? token)
	{
		if (string.IsNullOrWhiteSpace(token))
		{
			return null;
		}

		var trimmed = token.Trim();
		return trimmed.Length == 0 ? null : trimmed;
	}

	private static string NormalizePromptTemplate(string? template, string defaultTemplate, params string[] legacyDefaultTemplates)
	{
		if (string.IsNullOrWhiteSpace(template))
		{
			return defaultTemplate;
		}

		var normalizedTemplate = template.Trim().ReplaceLineEndings("\n");
		return legacyDefaultTemplates.Any(legacyDefault =>
				string.Equals(normalizedTemplate, legacyDefault, StringComparison.Ordinal))
			? defaultTemplate
			: normalizedTemplate;
	}
}
