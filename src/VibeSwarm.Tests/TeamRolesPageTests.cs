using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using VibeSwarm.Client.Pages;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

public sealed class TeamRolesPageTests
{
	[Fact]
	public async Task RenderedTeamsPage_ShowsPrimaryAddActionInHeader()
	{
		var html = await RenderTeamsPageAsync([]);

		Assert.Contains("btn btn-primary", html);
		Assert.Contains(">Add Agent<", html);
		Assert.Contains(">Agents<", html);
		Assert.Contains("d-flex align-items-center justify-content-between gap-2 gap-sm-3 mb-3 mb-lg-4", html);
	}

	[Fact]
	public async Task RenderedTeamsPage_ShowsConfiguredRoleDetailsAndSkills()
	{
		var skill = new Skill
		{
			Id = Guid.NewGuid(),
			Name = "secure-review",
			Description = "Review auth and data exposure."
		};
		var teamRole = new TeamRole
		{
			Id = Guid.NewGuid(),
			Name = "Security Reviewer",
			Description = "Focuses on threats and auth flaws.",
			Responsibilities = "Review auth, secrets, and permission boundaries.",
			DefaultProvider = new Provider
			{
				Id = Guid.NewGuid(),
				Name = "Claude Code"
			},
			DefaultModelId = "claude-sonnet-4.6",
			DefaultCycleMode = CycleMode.Autonomous,
			DefaultCycleSessionMode = CycleSessionMode.ContinueSession,
			DefaultMaxCycles = 4,
			IsEnabled = true,
			SkillLinks =
			[
				new TeamRoleSkill
				{
					SkillId = skill.Id,
					Skill = skill
				}
			]
		};

		var html = await RenderTeamsPageAsync([teamRole], [skill]);

		Assert.Contains("Security Reviewer", html);
		Assert.Contains("Focuses on threats and auth flaws.", html);
		Assert.Contains("secure-review", html);
		Assert.Contains("linked skill", html);
		Assert.Contains("Default provider: Claude Code", html);
		Assert.Contains("Default model: claude-sonnet-4.6", html);
		Assert.Contains("Default run: autonomous up to 4 cycles, resume session", html);
	}

	private static async Task<string> RenderTeamsPageAsync(
		IReadOnlyList<TeamRole> teamRoles,
		IReadOnlyList<Skill>? skills = null)
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<ITeamRoleService>(new FakeTeamRoleService(teamRoles));
		services.AddSingleton<IProviderService>(new FakeProviderService([]));
		services.AddSingleton<ISkillService>(new FakeSkillService(skills ?? []));
		services.AddSingleton<NotificationService>();
		services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		return await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var output = await renderer.RenderComponentAsync<Teams>();
			return output.ToHtmlString();
		});
	}

	private sealed class FakeTeamRoleService(IReadOnlyList<TeamRole> teamRoles) : ITeamRoleService
	{
		private readonly IReadOnlyList<TeamRole> _teamRoles = teamRoles;

		public Task<IEnumerable<TeamRole>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<TeamRole>>(_teamRoles);
		public Task<IEnumerable<TeamRole>> GetEnabledAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<TeamRole>>(_teamRoles.Where(teamRole => teamRole.IsEnabled));
		public Task<TeamRole?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(_teamRoles.FirstOrDefault(teamRole => teamRole.Id == id));
		public Task<TeamRole> CreateAsync(TeamRole teamRole, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<TeamRole> UpdateAsync(TeamRole teamRole, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> NameExistsAsync(string name, Guid? excludeId = null, CancellationToken cancellationToken = default) => Task.FromResult(false);
	}

	private sealed class FakeSkillService(IReadOnlyList<Skill> skills) : ISkillService
	{
		private readonly IReadOnlyList<Skill> _skills = skills;

		public Task<IEnumerable<Skill>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Skill>>(_skills);
		public Task<IEnumerable<Skill>> GetEnabledAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Skill>>(_skills.Where(skill => skill.IsEnabled));
		public Task<Skill?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(_skills.FirstOrDefault(skill => skill.Id == id));
		public Task<Skill?> GetByNameAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(_skills.FirstOrDefault(skill => skill.Name == name));
		public Task<Skill> CreateAsync(Skill skill, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Skill> UpdateAsync(Skill skill, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> NameExistsAsync(string name, Guid? excludeId = null, CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<SkillImportPreview> PreviewImportAsync(SkillImportRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<SkillImportResult> ImportAsync(SkillImportRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<string?> ExpandSkillAsync(string description, Guid providerId, string? modelId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class FakeProviderService(IReadOnlyList<Provider> providers) : IProviderService
	{
		private readonly IReadOnlyList<Provider> _providers = providers;

		public Task<IEnumerable<Provider>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Provider>>(_providers);
		public Task<Provider?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(_providers.FirstOrDefault(provider => provider.Id == id));
		public Task<Provider?> GetDefaultAsync(CancellationToken cancellationToken = default) => Task.FromResult(_providers.FirstOrDefault(provider => provider.IsDefault));
		public IProvider? CreateInstance(Provider config) => null;
		public Task<Provider> CreateAsync(Provider provider, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Provider> UpdateAsync(Provider provider, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> TestConnectionAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<ConnectionTestResult> TestConnectionWithDetailsAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task SetEnabledAsync(Guid id, bool isEnabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task SetDefaultAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<SessionSummary> GetSessionSummaryAsync(Guid providerId, string? sessionId, string? workingDirectory = null, string? fallbackOutput = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<ProviderModel>> GetModelsAsync(Guid providerId, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<ProviderModel>>([]);
		public Task<IEnumerable<ProviderModel>> RefreshModelsAsync(Guid providerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task SetDefaultModelAsync(Guid providerId, Guid modelId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<CliUpdateResult> UpdateCliAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class NoOpJsRuntime : IJSRuntime
	{
		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
	}
}
