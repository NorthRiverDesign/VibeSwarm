using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using VibeSwarm.Client.Pages;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

public sealed class ProvidersPageTests
{
	[Fact]
	public async Task RenderedProvidersPage_GroupsConfiguredProvidersWithinMatchingProviderSections()
	{
		var providers = new[]
		{
			new Provider
			{
				Id = Guid.NewGuid(),
				Name = "Copilot CLI Main",
				Type = ProviderType.Copilot,
				ConnectionMode = ProviderConnectionMode.CLI,
				IsEnabled = true
			},
			new Provider
			{
				Id = Guid.NewGuid(),
				Name = "OpenCode REST Main",
				Type = ProviderType.OpenCode,
				ConnectionMode = ProviderConnectionMode.REST,
				IsEnabled = true
			},
			new Provider
			{
				Id = Guid.NewGuid(),
				Name = "Claude SDK Primary",
				Type = ProviderType.Claude,
				ConnectionMode = ProviderConnectionMode.SDK,
				IsEnabled = true
			}
		};

		var statuses = new[]
		{
			CreateStatus(ProviderType.Claude, "Anthropic Claude", providers[2]),
			CreateStatus(ProviderType.Copilot, "GitHub Copilot", providers[0]),
			CreateStatus(ProviderType.OpenCode, "OpenCode Agent", providers[1])
		};

		var html = await RenderProvidersPageAsync(providers, statuses);

		Assert.Contains("aria-label=\"Anthropic Claude\"", html);
		Assert.Contains("aria-label=\"GitHub Copilot\"", html);
		Assert.Contains("aria-label=\"OpenCode Agent\"", html);
		Assert.True(html.IndexOf("Claude SDK Primary", StringComparison.Ordinal) < html.IndexOf("aria-label=\"GitHub Copilot\"", StringComparison.Ordinal));
		Assert.True(html.IndexOf("aria-label=\"GitHub Copilot\"", StringComparison.Ordinal) < html.IndexOf("Copilot CLI Main", StringComparison.Ordinal));
		Assert.True(html.IndexOf("Anthropic Claude", StringComparison.Ordinal) < html.IndexOf("Claude SDK Primary", StringComparison.Ordinal));
	}

	[Fact]
	public async Task RenderedProvidersPage_ShowsPerSectionEmptyStateForProviderTypesWithoutConnections()
	{
		var statuses = new[]
		{
			CreateStatus(ProviderType.Claude, "Anthropic Claude"),
			CreateStatus(ProviderType.Copilot, "GitHub Copilot"),
			CreateStatus(ProviderType.OpenCode, "OpenCode Agent")
		};

		var html = await RenderProvidersPageAsync([], statuses);

		Assert.Contains("Add a connection", html);
		Assert.Contains("use Anthropic Claude until it has one.", html);
		Assert.DoesNotContain("Add CLI", html);
		Assert.DoesNotContain("Add SDK", html);
	}

	[Fact]
	public async Task RenderedProvidersPage_ShowsPrimaryAddActionInHeader()
	{
		var html = await RenderProvidersPageAsync([], []);

		Assert.Contains("<h1 class=\"fs-2 fw-bold mb-0 me-auto\">Providers</h1>", html);
		Assert.Contains("btn btn-primary rounded-circle", html);
		Assert.Contains("aria-label=\"Add a connection\"", html);
		Assert.Contains("aria-label=\"Check this host again\"", html);
	}

	[Fact]
	public async Task RenderedProvidersPage_ShowsConnectionTypeInSummaryForCopilotByokConnection()
	{
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Copilot BYOK",
			Type = ProviderType.Copilot,
			ConnectionMode = ProviderConnectionMode.SDK,
			ApiEndpoint = "https://api.openai.com/v1",
			ApiKey = "sk-provider",
			IsEnabled = true
		};

		var statuses = new[]
		{
			CreateStatus(ProviderType.Copilot, "GitHub Copilot", provider)
		};

		var html = await RenderProvidersPageAsync([provider], statuses);

		Assert.Contains("Custom Provider", html);
	}

	[Fact]
	public async Task RenderedProvidersPage_ShowsEveryProviderOnOnePageWithoutTabs()
	{
		var installedProvider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Copilot CLI Main",
			Type = ProviderType.Copilot,
			ConnectionMode = ProviderConnectionMode.CLI,
			IsEnabled = true
		};

		var statuses = new[]
		{
			CreateStatus(ProviderType.Claude, "Anthropic Claude"),
			CreateStatus(ProviderType.Copilot, "GitHub Copilot", installedProvider)
		};

		var html = await RenderProvidersPageAsync([installedProvider], statuses);

		Assert.DoesNotContain("nav-tabs", html);
		Assert.Contains("aria-label=\"Anthropic Claude\"", html);
		Assert.Contains("aria-label=\"GitHub Copilot\"", html);
		Assert.Contains("Not installed", html);
		Assert.Contains("Ready", html);
	}

	[Fact]
	public async Task RenderedProvidersPage_StartsConnectionRowsCollapsed()
	{
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Claude CLI Main",
			Type = ProviderType.Claude,
			ConnectionMode = ProviderConnectionMode.CLI,
			IsEnabled = true
		};

		var statuses = new[]
		{
			CreateStatus(ProviderType.Claude, "Anthropic Claude", provider)
		};

		var html = await RenderProvidersPageAsync([provider], statuses);

		Assert.Contains("Claude CLI Main", html);
		Assert.Contains("aria-expanded=\"false\"", html);
		Assert.DoesNotContain("Test connection", html);
	}

	[Fact]
	public async Task RenderedProvidersPage_ListsConnectionsOfEveryModeInOneGroup()
	{
		var cliProvider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Claude CLI Main",
			Type = ProviderType.Claude,
			ConnectionMode = ProviderConnectionMode.CLI,
			IsEnabled = true
		};
		var sdkProvider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Claude SDK Main",
			Type = ProviderType.Claude,
			ConnectionMode = ProviderConnectionMode.SDK,
			IsEnabled = true
		};

		var statuses = new[]
		{
			CreateStatus(ProviderType.Claude, "Anthropic Claude", cliProvider)
		};

		var html = await RenderProvidersPageAsync([cliProvider, sdkProvider], statuses);

		Assert.DoesNotContain("nav-pills", html);
		Assert.Contains("Claude CLI Main", html);
		Assert.Contains("Claude SDK Main", html);
		Assert.Contains("bi bi-terminal", html);
		Assert.Contains("bi bi-box", html);
	}

	private static CommonProviderSetupStatus CreateStatus(ProviderType type, string displayName, Provider? provider = null)
	{
		return new CommonProviderSetupStatus
		{
			ProviderType = type,
			DisplayName = displayName,
			Description = $"{displayName} setup",
			DocumentationUrl = "https://example.com/docs",
			InstallMethodLabel = "Installer",
			InstallCommand = $"install-{type.ToString().ToLowerInvariant()}",
			ApiKeyLabel = "API Key",
			ApiKeyHelpText = "Save credentials",
			IsInstalled = provider != null,
			IsAuthenticated = provider != null,
			HasConfiguredProvider = provider != null,
			AuthenticationTypeLabel = provider == null ? null : ProviderCapabilities.GetConnectionTypeLabel(provider),
			ConfiguredProviders = provider == null
				? []
				:
				[
					new CommonProviderSetupConfiguredProvider
					{
						Id = provider.Id,
						Name = provider.Name,
						ConnectionMode = provider.ConnectionMode,
						IsDefault = provider.IsDefault
					}
				]
		};
	}

	private static async Task<string> RenderProvidersPageAsync(
		IReadOnlyList<Provider> providers,
		IReadOnlyList<CommonProviderSetupStatus> statuses)
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<IProviderService>(new FakeProviderService(providers));
		services.AddSingleton<ICommonProviderSetupService>(new FakeCommonProviderSetupService(statuses));
		services.AddSingleton(new HttpProviderService(new HttpClient(new FakeProviderHttpMessageHandler())
		{
			BaseAddress = new Uri("http://localhost")
		}));
		services.AddSingleton<NotificationService>();
		services.AddSingleton<IJSRuntime, NoOpJsRuntime>();

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		return await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var output = await renderer.RenderComponentAsync<Providers>();
			return output.ToHtmlString();
		});
	}

	private sealed class FakeProviderService(IReadOnlyList<Provider> providers) : FakeProviderServiceBase
	{
		private readonly IReadOnlyList<Provider> _providers = providers;
		public override Task<IEnumerable<Provider>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Provider>>(_providers);
		public override Task<Provider?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(_providers.FirstOrDefault(provider => provider.Id == id));
		public override Task<Provider?> GetDefaultAsync(CancellationToken cancellationToken = default) => Task.FromResult(_providers.FirstOrDefault(provider => provider.IsDefault));
	}

	private sealed class FakeCommonProviderSetupService(IReadOnlyList<CommonProviderSetupStatus> statuses) : ICommonProviderSetupService
	{
		private readonly IReadOnlyList<CommonProviderSetupStatus> _statuses = statuses;
		public Task<IReadOnlyList<CommonProviderSetupStatus>> GetStatusesAsync(CancellationToken cancellationToken = default) => Task.FromResult(_statuses);
		public Task<IReadOnlyList<CommonProviderSetupStatus>> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(_statuses);
		public Task<CommonProviderActionResult> InstallAsync(ProviderType providerType, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<CommonProviderActionResult> SaveAuthenticationAsync(CommonProviderSetupRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class FakeProviderHttpMessageHandler : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			if (request.RequestUri?.AbsolutePath == "/api/providers/usage-summaries")
			{
				return Task.FromResult(CreateJsonResponse(new Dictionary<Guid, ProviderUsageSummary>()));
			}

			throw new InvalidOperationException($"Unexpected HTTP request: {request.RequestUri}");
		}

		private static HttpResponseMessage CreateJsonResponse<T>(T payload)
		{
			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
			};
		}
	}

	private sealed class NoOpJsRuntime : IJSRuntime
	{
		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
			=> ValueTask.FromResult(default(TValue)!);

		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
			=> ValueTask.FromResult(default(TValue)!);
	}
}
