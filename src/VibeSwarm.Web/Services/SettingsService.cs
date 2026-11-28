using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Utilities;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Shared.Services;

public class SettingsService : ISettingsService
{
	private readonly VibeSwarmDbContext _dbContext;
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

	// The defaults before the job system prompt rules took over the closing <commit-summary> block.
	private const string CommitSummaryDefaultIdeaImplementationPromptTemplate =
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
		A concise one-line description of what was implemented (aim for 72 chars; hard max 96 chars)
		</commit-summary>
		""";

	private const string CommitSummaryDefaultApprovedIdeaImplementationPromptTemplate =
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
		A concise one-line description of what was implemented (aim for 72 chars; hard max 96 chars)
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
				MaxConcurrentJobs = JobConcurrency.DefaultTotalJobs,
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
			var normalizedMaxConcurrentJobs = JobConcurrency.Normalize(settings.MaxConcurrentJobs);
			var normalizedIdeaImplementationPromptTemplate = NormalizePromptTemplate(
				settings.IdeaImplementationPromptTemplate,
				PromptBuilder.DefaultIdeaImplementationPromptTemplate,
				LegacyDefaultIdeaImplementationPromptTemplate,
				PreviousDefaultIdeaImplementationPromptTemplate,
				CommitSummaryDefaultIdeaImplementationPromptTemplate);
			var normalizedApprovedIdeaImplementationPromptTemplate = NormalizePromptTemplate(
				settings.ApprovedIdeaImplementationPromptTemplate,
				PromptBuilder.DefaultApprovedIdeaImplementationPromptTemplate,
				LegacyDefaultApprovedIdeaImplementationPromptTemplate,
				PreviousDefaultApprovedIdeaImplementationPromptTemplate,
				CommitSummaryDefaultApprovedIdeaImplementationPromptTemplate);

			if (!string.Equals(settings.TimeZoneId, normalizedTimeZoneId, StringComparison.Ordinal) ||
				settings.CriticalErrorLogRetentionDays != normalizedRetentionDays ||
				settings.CriticalErrorLogMaxEntries != normalizedMaxEntries ||
				settings.MaxConcurrentJobs != normalizedMaxConcurrentJobs ||
				!string.Equals(settings.IdeaImplementationPromptTemplate, normalizedIdeaImplementationPromptTemplate, StringComparison.Ordinal) ||
				!string.Equals(settings.ApprovedIdeaImplementationPromptTemplate, normalizedApprovedIdeaImplementationPromptTemplate, StringComparison.Ordinal))
			{
				settings.TimeZoneId = normalizedTimeZoneId;
				settings.CriticalErrorLogRetentionDays = normalizedRetentionDays;
				settings.CriticalErrorLogMaxEntries = normalizedMaxEntries;
				settings.MaxConcurrentJobs = normalizedMaxConcurrentJobs;
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
			// Only admins change it, through UpdateMaxConcurrentJobsAsync.
			settings.MaxConcurrentJobs = JobConcurrency.DefaultTotalJobs;
			settings.IdeaImplementationPromptTemplate = NormalizePromptTemplate(
				settings.IdeaImplementationPromptTemplate,
				PromptBuilder.DefaultIdeaImplementationPromptTemplate,
				LegacyDefaultIdeaImplementationPromptTemplate,
				PreviousDefaultIdeaImplementationPromptTemplate,
				CommitSummaryDefaultIdeaImplementationPromptTemplate);
			settings.ApprovedIdeaImplementationPromptTemplate = NormalizePromptTemplate(
				settings.ApprovedIdeaImplementationPromptTemplate,
				PromptBuilder.DefaultApprovedIdeaImplementationPromptTemplate,
				LegacyDefaultApprovedIdeaImplementationPromptTemplate,
				PreviousDefaultApprovedIdeaImplementationPromptTemplate,
				CommitSummaryDefaultApprovedIdeaImplementationPromptTemplate);
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
			existing.EnableHttps = settings.EnableHttps;
			existing.EnableBrowserTools = settings.EnableBrowserTools;
			existing.InferenceAnswersAgentQuestions = settings.InferenceAnswersAgentQuestions;
			existing.CriticalErrorLogRetentionDays = NormalizeCriticalErrorLogRetentionDays(settings.CriticalErrorLogRetentionDays);
			existing.CriticalErrorLogMaxEntries = NormalizeCriticalErrorLogMaxEntries(settings.CriticalErrorLogMaxEntries);
			existing.IdeaImplementationPromptTemplate = NormalizePromptTemplate(
				settings.IdeaImplementationPromptTemplate,
				PromptBuilder.DefaultIdeaImplementationPromptTemplate,
				LegacyDefaultIdeaImplementationPromptTemplate,
				PreviousDefaultIdeaImplementationPromptTemplate,
				CommitSummaryDefaultIdeaImplementationPromptTemplate);
			existing.ApprovedIdeaImplementationPromptTemplate = NormalizePromptTemplate(
				settings.ApprovedIdeaImplementationPromptTemplate,
				PromptBuilder.DefaultApprovedIdeaImplementationPromptTemplate,
				LegacyDefaultApprovedIdeaImplementationPromptTemplate,
				PreviousDefaultApprovedIdeaImplementationPromptTemplate,
				CommitSummaryDefaultApprovedIdeaImplementationPromptTemplate);
			existing.GitHubToken = NormalizeGitHubToken(settings.GitHubToken);
			existing.UpdatedAt = DateTime.UtcNow;
		}

		await _dbContext.SaveChangesAsync(cancellationToken);

		return existing ?? settings;
	}

	public async Task<AppSettings> UpdateMaxConcurrentJobsAsync(int maxConcurrentJobs, CancellationToken cancellationToken = default)
	{
		var settings = await GetSettingsAsync(cancellationToken);
		settings.MaxConcurrentJobs = JobConcurrency.Normalize(maxConcurrentJobs);
		settings.UpdatedAt = DateTime.UtcNow;
		await _dbContext.SaveChangesAsync(cancellationToken);

		return settings;
	}

	public async Task<AppSettings> UpdateTeamAsync(Guid? leadProfileId, Guid? reviewProfileId, CancellationToken cancellationToken = default)
	{
		var ids = new[] { leadProfileId, reviewProfileId }.OfType<Guid>().Distinct().ToList();
		var profiles = await _dbContext.Profiles
			.AsNoTracking()
			.Include(profile => profile.Provider)
			.Include(profile => profile.InferenceProvider)
			.Where(profile => ids.Contains(profile.Id))
			.ToDictionaryAsync(profile => profile.Id, cancellationToken);

		if (leadProfileId is Guid leadId)
		{
			if (!profiles.TryGetValue(leadId, out var lead))
			{
				throw new InvalidOperationException("The profile chosen to lead no longer exists.");
			}

			if (!ProfileRoles.CanRunReadOnly(lead))
			{
				throw new InvalidOperationException($"The Lead needs a Claude or Copilot profile, and '{lead.Name}' isn't one.");
			}
		}

		if (reviewProfileId is Guid reviewId)
		{
			if (!profiles.TryGetValue(reviewId, out var reviewer))
			{
				throw new InvalidOperationException("The profile chosen to review no longer exists.");
			}

			if (!ProfileRoles.CanHelpJobs(reviewer))
			{
				throw new InvalidOperationException($"Review needs a Claude, Copilot or inference profile, and '{reviewer.Name}' isn't one.");
			}
		}

		var settings = await GetSettingsAsync(cancellationToken);
		settings.TeamLeadProfileId = leadProfileId;
		settings.TeamReviewProfileId = reviewProfileId;
		settings.UpdatedAt = DateTime.UtcNow;

		// Projects hold a copy of the team's profiles, so each one working as a team is brought
		// up to date here; a role the project overrides keeps its own profile.
		var team = TeamLinks.From(settings);
		var projects = await _dbContext.Projects.Where(project => project.TeamEnabled).ToListAsync(cancellationToken);
		if (projects.Count > 0)
		{
			var linked = projects.SelectMany(project => new[] { project.LeadProfileId, project.ReviewProfileId }).OfType<Guid>()
				.Concat(ids).Distinct().ToList();
			var lookup = await _dbContext.Profiles
				.AsNoTracking()
				.Include(profile => profile.Provider)
				.Include(profile => profile.InferenceProvider)
				.Where(profile => linked.Contains(profile.Id))
				.ToDictionaryAsync(profile => profile.Id, cancellationToken);
			foreach (var project in projects)
			{
				TeamLinks.Apply(project, team);
				ProfileLinks.ApplyRoleLinks(project, lookup);
				project.UpdatedAt = DateTime.UtcNow;
			}
		}

		await _dbContext.SaveChangesAsync(cancellationToken);
		return settings;
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
