using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Client.Components.Providers;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Tests;

public sealed class CommonProviderSetupCardTests
{
	[Fact]
	public async Task CommonProviderSetupCard_ShowsInstallAndAuthStatuses()
	{
		var services = new ServiceCollection();
		services.AddLogging();

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
		{
			[nameof(CommonProviderSetupCard.Status)] = new CommonProviderSetupStatus
			{
				ProviderType = ProviderType.Copilot,
				DisplayName = "GitHub Copilot",
				Description = "Install and configure Copilot.",
				InstallMethodLabel = "Official installer",
				InstallCommand = "curl -fsSL https://gh.io/copilot-install | bash",
				ApiKeyLabel = "GitHub Token",
				ApiKeyHelpText = "Use a fine-grained PAT.",
				IsInstalled = false,
				IsAuthenticated = false,
				HasConfiguredProvider = false,
				AuthenticationStatus = "Save a GitHub token to let VibeSwarm authenticate the Copilot CLI."
			}
		});

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var output = await renderer.RenderComponentAsync<CommonProviderSetupCard>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("GitHub Copilot", html);
		Assert.Contains("Not Installed", html);
		Assert.Contains("Auth Needed", html);
		Assert.Contains("Provider Not Added", html);
		Assert.Contains("curl -fsSL https://gh.io/copilot-install | bash", html);
	}

	[Fact]
	public async Task CommonProviderSetupCard_ShowsConfiguredProviderDetails()
	{
		var services = new ServiceCollection();
		services.AddLogging();

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
		{
			[nameof(CommonProviderSetupCard.Status)] = new CommonProviderSetupStatus
			{
				ProviderType = ProviderType.Claude,
				DisplayName = "Claude Code",
				Description = "Install Claude Code.",
				InstallMethodLabel = "Official installer",
				InstallCommand = "curl -fsSL https://claude.ai/install.sh | bash",
				ApiKeyLabel = "Anthropic API Key",
				ApiKeyHelpText = "Save an Anthropic key.",
				IsInstalled = true,
				InstalledVersion = "1.0.58",
				IsAuthenticated = true,
				HasConfiguredProvider = true,
				ProviderName = "Claude Code CLI",
				AuthenticationStatus = "Saved in VibeSwarm."
			}
		});

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var output = await renderer.RenderComponentAsync<CommonProviderSetupCard>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("Installed", html);
		Assert.Contains("Auth Ready", html);
		Assert.Contains("Provider Configured", html);
		Assert.Contains("Claude Code CLI", html);
		Assert.Contains("1.0.58", html);
	}
}
