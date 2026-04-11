using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Client.Components.Providers;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared;

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
				AuthenticationConnectionMode = ProviderConnectionMode.CLI,
				AuthenticationTypeLabel = "GitHub Token",
				IsAuthenticated = false,
				HasConfiguredProvider = false,
				InstallationStatus = $"{AppConstants.AppName} could not find 'copilot' on the host PATH. It also checked common user install locations.",
				AuthenticationStatus = "Sign in with 'copilot login' or save a GitHub token for this CLI connection.",
				HostTools =
				[
					new CommonProviderHostToolStatus
					{
						Name = "ripgrep",
						Command = "rg",
						Purpose = "Fast content search for autonomous CLI work.",
						IsInstalled = true,
						ResolvedExecutablePath = "/usr/bin/rg",
						Status = "Detected on host."
					},
					new CommonProviderHostToolStatus
					{
						Name = "fd",
						Command = "fd / fdfind",
						Purpose = "Fast file-name search. Debian and Ubuntu often install this command as fdfind.",
						IsInstalled = false,
						Status = "VibeSwarm could not find any of 'fd', 'fdfind' on the host PATH."
					}
				]
			}
		});

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var output = await renderer.RenderComponentAsync<CommonProviderSetupCard>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("GitHub Copilot", html);
		Assert.Contains("Not Installed", html);
		Assert.Contains("CLI GitHub Token Auth Needed", html);
		Assert.Contains("Provider Not Added", html);
		Assert.Contains("Host detection", html);
		Assert.Contains("Supporting host tools", html);
		Assert.Contains("ripgrep", html);
		Assert.Contains("fd / fdfind", html);
		Assert.Contains("No provider saved in VibeSwarm yet.", html);
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
				ResolvedExecutablePath = "/usr/local/bin/claude",
				InstallationStatus = "Detected on host.",
				AuthenticationConnectionMode = ProviderConnectionMode.SDK,
				AuthenticationTypeLabel = "API Key",
				IsAuthenticated = true,
				HasConfiguredProvider = true,
				ProviderName = "Claude Code CLI",
				AuthenticationStatus = "Saved in VibeSwarm for this SDK connection.",
				ConfiguredProviders =
				[
					new CommonProviderSetupConfiguredProvider
					{
						Id = Guid.NewGuid(),
						Name = "Claude Code CLI",
						ConnectionMode = ProviderConnectionMode.CLI,
						IsDefault = true
					}
				]
			}
		});

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var output = await renderer.RenderComponentAsync<CommonProviderSetupCard>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("Installed", html);
		Assert.Contains("SDK API Key Auth Ready", html);
		Assert.Contains("Provider Configured", html);
		Assert.Contains("Claude Code CLI", html);
		Assert.Contains("1.0.58", html);
		Assert.Contains("/usr/local/bin/claude", html);
		Assert.Contains("1 provider", html);
	}
}
