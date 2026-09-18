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

public sealed class AgentsPageTests
{
	[Fact]
	public async Task RenderedAgentsPage_ShowsPrimaryAddActionInHeader()
	{
		var html = await RenderAgentsPageAsync([]);

		Assert.Contains("btn btn-primary", html);
		Assert.Contains(">Add Agent<", html);
		Assert.Contains(">Agents<", html);
		Assert.Contains("justify-content-between gap-2 gap-sm-3 mb-3 mb-lg-4", html);
	}

	[Fact]
	public async Task RenderedAgentsPage_ShowsConfiguredRoleDetailsAndSkills()
	{
		var skill = new Skill
		{
			Id = Guid.NewGuid(),
			Name = "secure-review",
			Description = "Review auth and data exposure."
		};
		var agent = new Agent
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
				new AgentSkill
				{
					SkillId = skill.Id,
					Skill = skill
				}
			]
		};

		var html = await RenderAgentsPageAsync([agent], [skill]);

		Assert.Contains("Security Reviewer", html);
		Assert.Contains("Focuses on threats and auth flaws.", html);
		Assert.Contains("secure-review", html);
		Assert.Contains("linked skill", html);
		Assert.Contains("Default provider: Claude Code", html);
		Assert.Contains("Default model: claude-sonnet-4.6", html);
		Assert.Contains("Default run: autonomous up to 4 cycles, resume session", html);
	}

	private static async Task<string> RenderAgentsPageAsync(
		IReadOnlyList<Agent> agents,
		IReadOnlyList<Skill>? skills = null)
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<IAgentService>(new FakeAgentService(agents));
		services.AddSingleton<IProviderService>(new FakeProviderService([]));
		services.AddSingleton<ISkillService>(new FakeSkillService(skills ?? []));
		services.AddSingleton<NotificationService>();
		services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		return await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var output = await renderer.RenderComponentAsync<Agents>();
			return output.ToHtmlString();
		});
	}

	private sealed class FakeAgentService(IReadOnlyList<Agent> agents) : FakeAgentServiceBase
	{
		private readonly IReadOnlyList<Agent> _agents = agents;
		public override Task<IEnumerable<Agent>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Agent>>(_agents);
		public override Task<IEnumerable<Agent>> GetEnabledAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Agent>>(_agents.Where(agent => agent.IsEnabled));
		public override Task<Agent?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(_agents.FirstOrDefault(agent => agent.Id == id));
		public override Task<bool> NameExistsAsync(string name, Guid? excludeId = null, CancellationToken cancellationToken = default) => Task.FromResult(false);
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

	private sealed class FakeProviderService(IReadOnlyList<Provider> providers) : FakeProviderServiceBase
	{
		private readonly IReadOnlyList<Provider> _providers = providers;
		public override Task<IEnumerable<Provider>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Provider>>(_providers);
		public override Task<Provider?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(_providers.FirstOrDefault(provider => provider.Id == id));
		public override Task<Provider?> GetDefaultAsync(CancellationToken cancellationToken = default) => Task.FromResult(_providers.FirstOrDefault(provider => provider.IsDefault));
	}

	private sealed class NoOpJsRuntime : IJSRuntime
	{
		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
	}
}
