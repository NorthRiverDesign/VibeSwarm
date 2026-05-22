using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

public sealed class SettingsServiceTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;

	public SettingsServiceTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();

		_dbOptions = new DbContextOptionsBuilder<VibeSwarmDbContext>()
			.UseSqlite(_connection)
			.Options;

		using var dbContext = CreateDbContext();
		dbContext.Database.EnsureCreated();
	}

	[Fact]
	public async Task UpdateSettingsAsync_PersistsTimezoneAndAgentQualityFlags()
	{
		await using var dbContext = CreateDbContext();
		var service = new SettingsService(dbContext);

		var updated = await service.UpdateSettingsAsync(new AppSettings
		{
			DefaultProjectsDirectory = "/tmp/projects",
			TimeZoneId = "UTC",
			EnablePromptStructuring = false,
			InjectRepoMap = false,
			InjectEfficiencyRules = false,
			EnableCommitAttribution = false,
			CriticalErrorLogRetentionDays = 45,
			CriticalErrorLogMaxEntries = 350,
			IdeaExpansionPromptTemplate = "Expand {{idea}}",
			IdeaImplementationPromptTemplate = "Implement {{idea}}",
			ApprovedIdeaImplementationPromptTemplate = "Idea {{idea}}\nSpec {{specification}}"
		});

		var saved = await dbContext.AppSettings.SingleAsync();
		Assert.Equal(updated.Id, saved.Id);
		Assert.Equal("/tmp/projects", saved.DefaultProjectsDirectory);
		Assert.Equal("UTC", saved.TimeZoneId);
		Assert.False(saved.EnablePromptStructuring);
		Assert.False(saved.InjectRepoMap);
		Assert.False(saved.InjectEfficiencyRules);
		Assert.False(saved.EnableCommitAttribution);
		Assert.Equal(45, saved.CriticalErrorLogRetentionDays);
		Assert.Equal(350, saved.CriticalErrorLogMaxEntries);
		Assert.Equal("Expand {{idea}}", saved.IdeaExpansionPromptTemplate);
		Assert.Equal("Implement {{idea}}", saved.IdeaImplementationPromptTemplate);
		Assert.Equal("Idea {{idea}}\nSpec {{specification}}", saved.ApprovedIdeaImplementationPromptTemplate);
		Assert.NotNull(saved.UpdatedAt);
	}

	[Fact]
	public async Task GetSettingsAsync_CreatesDefaultSettingsWithCommitAttributionEnabled()
	{
		await using var dbContext = CreateDbContext();
		var service = new SettingsService(dbContext);

		var settings = await service.GetSettingsAsync();

		Assert.True(settings.EnableCommitAttribution);
	}

	[Fact]
	public async Task GetSettingsAsync_CreatesDefaultIdeaPromptTemplates()
	{
		await using var dbContext = CreateDbContext();
		var service = new SettingsService(dbContext);

		var settings = await service.GetSettingsAsync();

		Assert.Equal(PromptBuilder.DefaultIdeaExpansionPromptTemplate, settings.IdeaExpansionPromptTemplate);
		Assert.Equal(PromptBuilder.DefaultIdeaImplementationPromptTemplate, settings.IdeaImplementationPromptTemplate);
		Assert.Equal(PromptBuilder.DefaultApprovedIdeaImplementationPromptTemplate, settings.ApprovedIdeaImplementationPromptTemplate);
		Assert.Contains("aim for 72 chars; hard max 96 chars", settings.IdeaImplementationPromptTemplate);
		Assert.Contains("aim for 72 chars; hard max 96 chars", settings.ApprovedIdeaImplementationPromptTemplate);
	}

	[Fact]
	public async Task GetSettingsAsync_CreatesDefaultSettingsWithUtcTimezoneAndCriticalErrorDefaults()
	{
		await using var dbContext = CreateDbContext();
		var service = new SettingsService(dbContext);

		var settings = await service.GetSettingsAsync();

		Assert.Equal("UTC", settings.TimeZoneId);
		Assert.Equal(AppSettings.DefaultCriticalErrorLogRetentionDays, settings.CriticalErrorLogRetentionDays);
		Assert.Equal(AppSettings.DefaultCriticalErrorLogMaxEntries, settings.CriticalErrorLogMaxEntries);
	}

	[Fact]
	public async Task GetSettingsAsync_NormalizesCriticalErrorLogCapacity()
	{
		await using var dbContext = CreateDbContext();
		dbContext.AppSettings.Add(new AppSettings
		{
			Id = Guid.NewGuid(),
			TimeZoneId = "UTC",
			CriticalErrorLogRetentionDays = AppSettings.MaxCriticalErrorLogRetentionDays + 20,
			CriticalErrorLogMaxEntries = AppSettings.MaxCriticalErrorLogMaxEntries + 500
		});
		await dbContext.SaveChangesAsync();

		var service = new SettingsService(dbContext);
		var settings = await service.GetSettingsAsync();

		Assert.Equal(AppSettings.MaxCriticalErrorLogRetentionDays, settings.CriticalErrorLogRetentionDays);
		Assert.Equal(AppSettings.MaxCriticalErrorLogMaxEntries, settings.CriticalErrorLogMaxEntries);
	}

	[Fact]
	public async Task UpdateSettingsAsync_BlankPromptTemplates_ResetToDefaults()
	{
		await using var dbContext = CreateDbContext();
		var service = new SettingsService(dbContext);

		var updated = await service.UpdateSettingsAsync(new AppSettings
		{
			TimeZoneId = "UTC",
			IdeaExpansionPromptTemplate = "   ",
			IdeaImplementationPromptTemplate = "",
			ApprovedIdeaImplementationPromptTemplate = null
		});

		Assert.Equal(PromptBuilder.DefaultIdeaExpansionPromptTemplate, updated.IdeaExpansionPromptTemplate);
		Assert.Equal(PromptBuilder.DefaultIdeaImplementationPromptTemplate, updated.IdeaImplementationPromptTemplate);
		Assert.Equal(PromptBuilder.DefaultApprovedIdeaImplementationPromptTemplate, updated.ApprovedIdeaImplementationPromptTemplate);
	}

	[Fact]
	public async Task GetSettingsAsync_UpgradesLegacyDefaultIdeaPromptTemplates()
	{
		const string legacyIdeaExpansionPromptTemplate =
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
		const string legacyIdeaImplementationPromptTemplate =
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
		const string legacyApprovedIdeaImplementationPromptTemplate =
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

		await using var seedContext = CreateDbContext();
		seedContext.AppSettings.Add(new AppSettings
		{
			Id = Guid.NewGuid(),
			TimeZoneId = "UTC",
			IdeaExpansionPromptTemplate = legacyIdeaExpansionPromptTemplate,
			IdeaImplementationPromptTemplate = legacyIdeaImplementationPromptTemplate,
			ApprovedIdeaImplementationPromptTemplate = legacyApprovedIdeaImplementationPromptTemplate
		});
		await seedContext.SaveChangesAsync();

		await using var dbContext = CreateDbContext();
		var service = new SettingsService(dbContext);
		var settings = await service.GetSettingsAsync();

		Assert.Equal(PromptBuilder.DefaultIdeaExpansionPromptTemplate, settings.IdeaExpansionPromptTemplate);
		Assert.Equal(PromptBuilder.DefaultIdeaImplementationPromptTemplate, settings.IdeaImplementationPromptTemplate);
		Assert.Equal(PromptBuilder.DefaultApprovedIdeaImplementationPromptTemplate, settings.ApprovedIdeaImplementationPromptTemplate);

		await using var verificationContext = CreateDbContext();
		var saved = await verificationContext.AppSettings.SingleAsync();
		Assert.Equal(PromptBuilder.DefaultIdeaExpansionPromptTemplate, saved.IdeaExpansionPromptTemplate);
		Assert.Equal(PromptBuilder.DefaultIdeaImplementationPromptTemplate, saved.IdeaImplementationPromptTemplate);
		Assert.Equal(PromptBuilder.DefaultApprovedIdeaImplementationPromptTemplate, saved.ApprovedIdeaImplementationPromptTemplate);
	}

	[Fact]
	public async Task GetSettingsAsync_UpgradesPreviousDefaultCommitSummaryPromptLimits()
	{
		const string previousIdeaImplementationPromptTemplate =
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
		const string previousApprovedIdeaImplementationPromptTemplate =
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

		await using var seedContext = CreateDbContext();
		seedContext.AppSettings.Add(new AppSettings
		{
			Id = Guid.NewGuid(),
			TimeZoneId = "UTC",
			IdeaExpansionPromptTemplate = PromptBuilder.DefaultIdeaExpansionPromptTemplate,
			IdeaImplementationPromptTemplate = previousIdeaImplementationPromptTemplate,
			ApprovedIdeaImplementationPromptTemplate = previousApprovedIdeaImplementationPromptTemplate
		});
		await seedContext.SaveChangesAsync();

		await using var dbContext = CreateDbContext();
		var service = new SettingsService(dbContext);
		var settings = await service.GetSettingsAsync();

		Assert.Equal(PromptBuilder.DefaultIdeaImplementationPromptTemplate, settings.IdeaImplementationPromptTemplate);
		Assert.Equal(PromptBuilder.DefaultApprovedIdeaImplementationPromptTemplate, settings.ApprovedIdeaImplementationPromptTemplate);

		await using var verificationContext = CreateDbContext();
		var saved = await verificationContext.AppSettings.SingleAsync();
		Assert.Equal(PromptBuilder.DefaultIdeaImplementationPromptTemplate, saved.IdeaImplementationPromptTemplate);
		Assert.Equal(PromptBuilder.DefaultApprovedIdeaImplementationPromptTemplate, saved.ApprovedIdeaImplementationPromptTemplate);
	}

	private VibeSwarmDbContext CreateDbContext() => new(_dbOptions);

	public void Dispose()
	{
		_connection.Dispose();
	}
}
